using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProcessRecorderApp.SingleInstance;

/// <summary>
/// 常駐ワーカーが受け取った起動引数（コマンドライン文字列）を、
/// アプリ側のコマンドパーサーに渡せる引数配列（string[]）へ変換する。
/// ここではあくまで文字列の分解のみを行い、コマンド名・オプションの意味づけは行わない。
/// </summary>
internal static class ActivationTokenizer
{
    /// <summary>
    /// AppActivationArguments から、起動時のコマンドライン文字列を取り出す。
    /// アンパッケージWin32アプリの Launch アクティブ化の場合、この文字列は
    /// <c>Environment.CommandLine</c> 相当（argv[0]にあたる自身のexeパスを含む）になる。
    /// 先頭のexeパスは <see cref="StripExecutablePath"/> で除去する。
    /// </summary>
    public static string ExtractCommandLine(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Launch &&
            args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
        {
            return launchArgs.Arguments ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// コマンドライン文字列を <c>CommandLineToArgvW</c> と同じ規則でトークン化する。
    /// ランチャー側の検証（<c>TryHandleInLauncher</c>）は <c>Main(string[] args)</c>
    /// （＝ OS の分解結果）に対して行われるため、ここが OS と違う規則だと
    /// **検証を通った引数がワーカーでは別の引数列として実行される**形の乖離になる。
    ///
    /// <para>
    /// 規則: 区切りは空白とタブのみ（全角スペース等では割らない）／二重引用符は区間のトグル／
    /// 引用区間内の <c>""</c> はリテラルの <c>"</c>／連続する n 個の <c>\</c> の直後に
    /// <c>"</c> が来る場合は n/2 個の <c>\</c>（n が奇数なら続けてリテラルの <c>"</c>）、
    /// それ以外の <c>\</c> はそのまま。
    /// </para>
    /// <para>
    /// <b>唯一の意図的な差:</b> 空のトークン（<c>""</c>）は捨てる ── コマンドパーサーへ渡す
    /// 引数に空文字列の意味は無く、従来からの挙動として L1
    /// （<c>Tokenize_EmptyQuotedSpan_ProducesNoToken</c>）が固定している。
    /// </para>
    /// </summary>
    public static string[] Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        // IsNullOrWhiteSpace で早期 return しない ── OS が区切りにしない空白
        // （全角スペース等）だけの入力を「空」と誤答してしまう。
        if (string.IsNullOrEmpty(commandLine))
        {
            return [.. tokens];
        }

        var current = new StringBuilder();
        bool inQuotes = false;
        int i = 0;

        while (i < commandLine.Length)
        {
            char c = commandLine[i];

            if (c == '\\')
            {
                int start = i;
                while (i < commandLine.Length && commandLine[i] == '\\')
                    i++;
                int backslashes = i - start;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if ((backslashes & 1) != 0)
                    {
                        current.Append('"');
                        i++;   // エスケープされた '"' を消費（引用のトグルにはしない）
                    }
                    // 偶数個なら '"' は次の周回で引用のトグルとして処理される
                }
                else
                {
                    current.Append('\\', backslashes);
                }
                continue;
            }

            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');   // 引用区間内の "" はリテラルの '"'
                    i += 2;
                    continue;
                }
                inQuotes = !inQuotes;
                i++;
                continue;
            }

            if (!inQuotes && c is ' ' or '\t')
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return [.. tokens];
    }

    /// <summary>
    /// トークン配列の先頭が自プロセスの実行ファイル（argv[0]相当）を指している場合、
    /// それを取り除いた配列を返す。アンパッケージWin32アプリの Launch アクティブ化では
    /// 起動引数の文字列がexeパスを含んだ完全なコマンドラインになるため、コマンドパーサーへ
    /// 渡す前に除去しておく必要がある。
    /// exeを直接実行した場合はフルパス（例: <c>G:\...\ProcessRecorderApp.exe</c>）、
    /// Visual Studioのデバッグ実行の場合はファイル名のみ（例: <c>ProcessRecorderApp.exe</c>）が
    /// 設定されるなど、環境によって形式が異なるため、ファイル名部分のみを比較する
    /// （一致しない場合はそのまま返す）。
    /// </summary>
    public static string[] StripExecutablePath(string[] tokens)
    {
        if (tokens.Length == 0)
        {
            return tokens;
        }

        string? exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            return tokens;
        }

        string firstTokenFileName = Path.GetFileName(tokens[0]);
        string exeFileName = Path.GetFileName(exePath);
        if (string.Equals(firstTokenFileName, exeFileName, StringComparison.OrdinalIgnoreCase))
        {
            return tokens[1..];
        }

        return tokens;
    }
}
