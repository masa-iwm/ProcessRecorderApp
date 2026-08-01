using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Console;
using Windows.Win32.System.Pipes;

namespace ProcessRecorderApp.Components;

/// <summary>
/// ネイティブDLL内・マネージドDLL内からの標準出力/標準エラー出力を
/// 行単位で <see cref="BlockingCollection{T}"/> にリダイレクトする。
///
/// 匿名パイプを1本作成し、以下の3層をパイプの書き込み側へ差し替えることで、
/// 出力経路によらず捕捉できるようにする：
///   1. Win32層  ： SetStdHandle（GetStdHandle + WriteFile 系の出力）
///   2. CRT層    ： freopen + _dup2（ucrtbase.dll の printf / fputs / write 系の出力）
///   3. マネージド層： Console.SetOut / SetError（Console.WriteLine 等の出力）
///
/// パイプは mintty（MSYS2/Cygwin 端末）の PTY パイプ命名規則
/// （\msys-&lt;16桁hex&gt;-pty0-to-master）に合わせた名前付きパイプとして作成する。
/// これにより GLib の win32_is_pipe_tty 等の判定が「カラー対応端末」となり、
/// 通常のコンソールと同様に ANSI エスケープ付きの色付き出力を捕捉できる。
///
/// 注意事項：
///   - プロセス全体の標準ハンドルを差し替えるため、同時に有効化できるのは1インスタンスのみ。
///   - CRT層の対象は ucrtbase.dll（MSVCビルドのネイティブDLL）。MinGW/msvcrt.dll 系は対象外。
///   - コンソールを持たないプロセス（CreateNoWindow 起動）でも動作する。
/// </summary>
[SupportedOSPlatform("windows5.1.2600")]
public sealed partial class StandardStreamRedirector : IDisposable
{
    /// <summary>捕捉した行（改行文字は含まない）。stdout / stderr の区別はしない</summary>
    public BlockingCollection<string> Lines { get; } = [];

    // パイプのハンドル（書き込み側を閉じると読み取りスレッドが終了する）
    private readonly SafeFileHandle _pipeWriteHandle;

    // 復元用に退避した元の標準ハンドル（CRT層の差し替え時に元ハンドル自体が閉じられるため、複製を保持する。
    // 元のハンドルが無効（コンソール無しプロセス等）の場合は 0）
    private readonly nint _originalStdOutputHandle;
    private readonly nint _originalStdErrorHandle;

    // デバッグ用ログファイル出力（SetLogFile で有効化）
    private readonly Lock _logFileLock = new();
    private StreamWriter? _logWriter;

    // 差し替える前の標準エラーへの複写（AppEnvironment.MirrorToOriginalStdErrVariable で有効化）。
    // 無効時・標準エラーが無いプロセスでは null のまま。
    private readonly StreamWriter? _originalStdErrWriter;

    private bool _disposed;

    /// <summary>リダイレクトを開始する</summary>
    public StandardStreamRedirector()
    {
        // 1. パイプを作成（stdout / stderr 共用）。
        //    色付き出力を得るため mintty 命名の名前付きパイプを優先し、失敗時は匿名パイプへフォールバック
        //    （フォールバック時は捕捉は継続するが、GLib 等は非TTY判定となり色は付かない）
        if (!TryCreateMinttyStylePipe(out var pipeReadHandle, out var pipeWriteHandle) &&
            !PInvoke.CreatePipe(out pipeReadHandle, out pipeWriteHandle, null, 0))
        {
            throw new InvalidOperationException("Failed to create a pipe."); // パイプの作成に失敗
        }
        _pipeWriteHandle = pipeWriteHandle;
        var writeHandle = new HANDLE(pipeWriteHandle.DangerousGetHandle());

        // 2. Win32層：元の標準ハンドルを複製して退避（後続の CRT層差し替えで元ハンドルが閉じられるため）
        //    してからパイプの書き込み側へ差し替え
        _originalStdOutputHandle = DuplicateOrZero(PInvoke.GetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE));
        _originalStdErrorHandle = DuplicateOrZero(PInvoke.GetStdHandle(STD_HANDLE.STD_ERROR_HANDLE));
        PInvoke.SetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE, writeHandle);
        PInvoke.SetStdHandle(STD_HANDLE.STD_ERROR_HANDLE, writeHandle);

        // 3. CRT層：ucrtbase の stdout / stderr をパイプへ向ける
        RedirectCrtToPipe(writeHandle);

        // 4. マネージド層：SetStdHandle 済みのため OpenStandardOutput/Error はパイプに向く
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });

        // 4'. 差し替える前の標準エラーへの複写（既定では無効）。
        //     ここより後ろの出力は3層とも差し替え済みなので、親プロセスが
        //     RedirectStandardError で待ち構えていても**原理的に1行も届かない**。
        //     外から観測する必要がある場合（E2E ハーネス）だけ、退避しておいた
        //     元のハンドルへ書き戻す。
        _originalStdErrWriter = TryCreateOriginalStdErrWriter(_originalStdErrorHandle);

        // 5. 読み取りスレッド：パイプから行単位で読み取り Lines へ追加する。
        //    書き込み側ハンドルがすべて閉じられると ReadLine が null を返して終了する
        var readerThread = new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(
                    new FileStream(pipeReadHandle, FileAccess.Read), Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    Lines.Add(line);
                    WriteToLogFile(line);
                    MirrorToOriginalStdErr(line);
                }
            }
            catch (Exception)
            {
                // パイプ破断等の例外は無視（プロセス終了時など）
            }
            finally
            {
                Lines.CompleteAdding();
            }
        })
        {
            IsBackground = true,
            Name = "StandardStreamRedirector.Reader",
        };
        readerThread.Start();
    }

    /// <summary>
    /// デバッグ用に、捕捉した行を ANSI エスケープ除去のうえログファイルへ追記する。
    /// <b>1行ごとにファイルへ Flush する</b>（<see cref="WriteToLogFile"/> の注記を参照）。
    /// <paramref name="logFilePath"/> に null または空文字を渡すと出力を停止する
    /// </summary>
    public void SetLogFile(string? logFilePath)
    {
        lock (_logFileLock)
        {
            _logWriter?.Dispose(); // Dispose 内で Flush される
            _logWriter = null;

            if (string.IsNullOrEmpty(logFilePath))
            {
                return;
            }

            _logWriter = new StreamWriter(logFilePath, append: true);
        }
    }

    /// <summary>捕捉した行をログファイルへ書き込む（読み取りスレッドから呼ばれる）</summary>
    private void WriteToLogFile(string line)
    {
        if (_logWriter is null)
        {
            return; // 未設定時はロックを取らず早期リターン
        }
        lock (_logFileLock)
        {
            try
            {
                _logWriter?.WriteLine(AnsiEscape.Strip(line));

                // **1行ごとに Flush する。** このログの主用途は「プロセスが突然死んだときに
                // 何が起きていたか」を後から読むことで、そのとき欲しいのは常に**最後の1行**
                // ── 死因はバッファに載ったまま失われる側にある。
                // バッファリング＋周期タイマーの Flush にすると、クラッシュや
                // 強制終了の直前の出力（＝死因そのもの）が丸ごと消える。
                // 周期タイマーも置かない ── 常に Flush 済みなので、
                // タイマーが起きても書き出すものは何も無い。
                _logWriter?.Flush();
            }
            catch (Exception)
            {
                // 書き込み失敗は無視して捕捉自体は継続する
            }
        }
    }

    /// <summary>
    /// 差し替える前の標準エラーへ書き戻す Writer を作る（無効・不可なら null）。
    ///
    /// <para>
    /// <b>コンソールを持たないプロセスでは必ず null になる。</b> ランチャーが
    /// <c>UseShellExecute=true</c> で暗黙起動した常駐ワーカーには標準エラーが無く、
    /// <see cref="DuplicateOrZero"/> は 0 を返す ── ここで例外を投げると、
    /// 通常の起動経路（＝利用者の経路）を丸ごと壊すことになる。
    /// </para>
    /// </summary>
    private static StreamWriter? TryCreateOriginalStdErrWriter(nint originalStdErrorHandle)
    {
        if (originalStdErrorHandle == 0)
        {
            return null;
        }

        var setting = Environment.GetEnvironmentVariable(AppEnvironment.MirrorToOriginalStdErrVariable);
        if (setting is null ||
            !(setting.Equals("1", StringComparison.Ordinal) ||
              setting.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            // ownsHandle: false ── このハンドルは Dispose の SetStdHandle による
            // 復元でも使うので、こちらで閉じてはいけない。
            var handle = new SafeFileHandle(originalStdErrorHandle, ownsHandle: false);
            var stream = new FileStream(handle, FileAccess.Write);
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return new StreamWriter(stream, encoding) { AutoFlush = true };
        }
        catch (Exception)
        {
            // 複写はあくまで診断用の付加機能。作れなくても捕捉自体は続ける
            return null;
        }
    }

    /// <summary>捕捉した行を、差し替える前の標準エラーへ複写する（読み取りスレッドから呼ばれる）</summary>
    private void MirrorToOriginalStdErr(string line)
    {
        if (_originalStdErrWriter is null)
        {
            return;
        }
        try
        {
            _originalStdErrWriter.WriteLine(line);
        }
        catch (Exception)
        {
            // 親がパイプを閉じた等。捕捉自体は継続する
        }
    }

    /// <summary>
    /// mintty（MSYS2/Cygwin 端末）の PTY パイプ命名規則に合わせた名前付きパイプを作成する。
    /// GLib の win32_is_pipe_tty はパイプ名が
    /// 「\(msys|cygwin)-&lt;16桁hex&gt;-pty&lt;1桁&gt;-(to|from)-master」に一致すると
    /// カラー対応端末と判定するため、この名前にすることで ANSI 色付き出力を引き出せる
    /// </summary>
    private static bool TryCreateMinttyStylePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle)
    {
        // 16桁 hex は GUID から生成し、他プロセスとの名前衝突を避ける
        var hex = Guid.NewGuid().ToString("N")[..16];
        var pipeName = $@"\\.\pipe\msys-{hex}-pty0-to-master";

        readHandle = PInvoke.CreateNamedPipe(
            pipeName,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAGS_AND_ATTRIBUTES.PIPE_ACCESS_INBOUND,
            NAMED_PIPE_MODE.PIPE_TYPE_BYTE, // PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT
            1, 65536, 65536, 0, null);
        if (readHandle.IsInvalid)
        {
            writeHandle = readHandle;
            return false;
        }

        // 書き込み側（クライアント）を同名で開く。複製されたハンドルにも同じパイプ名が引き継がれる
        writeHandle = PInvoke.CreateFile(
            pipeName, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_WRITE, 0, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING, 0, null);
        if (writeHandle.IsInvalid)
        {
            readHandle.Dispose();
            return false;
        }
        return true;
    }

    /// <summary>ハンドルを複製して返す（無効なハンドルや複製失敗時は 0）</summary>
    private static unsafe nint DuplicateOrZero(HANDLE handle)
    {
        var value = (nint)handle.Value;
        if (value is 0 or -1) // NULL / INVALID_HANDLE_VALUE
        {
            return 0;
        }
        var currentProcess = PInvoke.GetCurrentProcess();
        HANDLE duplicated;
        // 複製したハンドルは SetStdHandle での保持や CRT への移譲に使うため、
        // SafeHandle に包まず raw ハンドルのまま扱う（二重クローズ防止）
        return PInvoke.DuplicateHandle(
                   currentProcess, handle, currentProcess, &duplicated,
                   0, false, DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS)
            ? (nint)duplicated.Value : 0;
    }

    /// <summary>
    /// CRT（ucrtbase.dll）の stdout / stderr をパイプへ差し替える。
    /// コンソールを持たないプロセスでは FILE が無効（fd = -2）のため、
    /// いったん freopen("NUL") で有効な fd を割り当ててから _dup2 で差し替える定石を用いる。
    /// 失敗しても Win32層・マネージド層の捕捉は機能するため、例外は投げない
    /// </summary>
    private static void RedirectCrtToPipe(HANDLE pipeWriteHandle)
    {
        try
        {
            var stdoutFile = NativeMethods.__acrt_iob_func(1);
            var stderrFile = NativeMethods.__acrt_iob_func(2);
            NativeMethods.freopen("NUL", "w", stdoutFile);
            NativeMethods.freopen("NUL", "w", stderrFile);

            // CRT にハンドルの所有権が移るため、複製したハンドルを渡す
            var crtHandle = DuplicateOrZero(pipeWriteHandle);
            if (crtHandle == 0)
            {
                return;
            }

            var pipeFd = NativeMethods._open_osfhandle(crtHandle, 0);
            if (pipeFd == -1)
            {
                return;
            }

            // FILE ストリームの実際の fd と、生の fd 1 / 2 の両方をパイプへ向ける
            _ = NativeMethods._dup2(pipeFd, NativeMethods._fileno(stdoutFile));
            _ = NativeMethods._dup2(pipeFd, NativeMethods._fileno(stderrFile));
            _ = NativeMethods._dup2(pipeFd, 1);
            _ = NativeMethods._dup2(pipeFd, 2);

            // パイプ出力は既定でフルバッファとなり出力が届かないため、バッファリングを無効化する
            _ = NativeMethods.setvbuf(stdoutFile, 0, NativeMethods._IONBF, 0);
            _ = NativeMethods.setvbuf(stderrFile, 0, NativeMethods._IONBF, 0);
        }
        catch (Exception)
        {
            // CRT層の差し替え失敗は致命的でないため無視
        }
    }

    /// <summary>
    /// リダイレクトを解除して元の状態を復元する。
    /// 書き込み側ハンドルが閉じられることで読み取りスレッドが終了し、
    /// <see cref="Lines"/> は CompleteAdding される
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // CRT層をパイプから切り離す（NUL へ向け直す。元のコンソールへの復元は行わない）。
        // UCRT の freopen は fd 0-2 に対して内部で SetStdHandle も行うため、
        // Win32層の復元より必ず「先に」実行すること（後だと復元したハンドルが NUL で上書きされる）
        try
        {
            NativeMethods.freopen("NUL", "w", NativeMethods.__acrt_iob_func(1));
            NativeMethods.freopen("NUL", "w", NativeMethods.__acrt_iob_func(2));
        }
        catch (Exception)
        {
            // 切り離し失敗は無視
        }

        // Win32層：退避しておいた複製ハンドルへ戻す
        PInvoke.SetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE, new HANDLE(_originalStdOutputHandle));
        PInvoke.SetStdHandle(STD_HANDLE.STD_ERROR_HANDLE, new HANDLE(_originalStdErrorHandle));

        // マネージド層：復元後の標準ハンドルから Writer を作り直す
        // （リダイレクト前の Console.Out は CRT層の差し替えで閉じられたハンドルを掴んでいるため再利用できない。
        //   標準ハンドルが無い場合、OpenStandardOutput は Stream.Null を返すので安全）
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });

        // 書き込み側を閉じる（CRT が保持する複製 fd も上の freopen で解放済みのためパイプが破断する）
        _pipeWriteHandle.Dispose();

        // Lines への追加終了を通知
        Lines.CompleteAdding();

        // ログファイル出力を停止（最終 Flush してクローズ）
        SetLogFile(null);
    }

    /// <summary>
    /// ucrtbase CRT の P/Invoke 定義（AOT対応のため LibraryImport を使用）。
    /// Win32 API は CsWin32（NativeMethods.txt）で生成した <see cref="PInvoke"/> を使用し、
    /// CRT 関数は Win32 メタデータに存在せず CsWin32 で生成できないため、ここのみ手書きとする
    /// </summary>
    private static partial class NativeMethods
    {
        public const int _IONBF = 4;

        // ucrtbase.dll：C ランタイムの標準ストリーム操作
        [LibraryImport("ucrtbase.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        public static partial nint __acrt_iob_func(uint index);

        [LibraryImport("ucrtbase.dll", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        public static partial nint freopen(string fileName, string mode, nint stream);

        [LibraryImport("ucrtbase.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        public static partial int _open_osfhandle(nint osfhandle, int flags);

        [LibraryImport("ucrtbase.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        public static partial int _dup2(int fd1, int fd2);

        [LibraryImport("ucrtbase.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        public static partial int _fileno(nint stream);

        [LibraryImport("ucrtbase.dll")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        public static partial int setvbuf(nint stream, nint buffer, int mode, nuint size);
    }
}
