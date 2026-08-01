using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace ProcessRecorderApp.SingleInstance;

public sealed partial class SingleInstanceManager
{
    /// <summary>常駐ワーカーから受け取った結果通知1件。</summary>
    internal readonly record struct CommandResult(int ExitCode, string ConsoleOutput, string ConsoleError);

    /// <summary>
    /// ランチャーと常駐ワーカーの間で「1回のコマンドの結果」を受け渡す名前付きチャネル。
    /// 名前付き <see cref="MemoryMappedFile"/>（結果の中身）と名前付き
    /// <see cref="EventWaitHandle"/>（書けた合図）の2つで構成される。
    ///
    /// <para>
    /// <b>結果には必ず相関 ID（<c>requestId</c>）を刻む。</b> 名前付きオブジェクトは
    /// <c>keyPrefix</c> ごとに1組しか無いので、刻まないと<b>遅れて届いた結果が
    /// 次のコマンドの答えとして読まれる</b> ── 結果待ちを打ち切った
    /// （<see cref="ExitCode_WorkerResultTimeout"/>）ランチャーの後ろで、別のランチャーが
    /// 同じ名前のチャネルを張り直すためである。前のコマンドの終了コードと出力が
    /// そのまま次のコマンドの結果になる。
    /// <b>これは「遅い」ではなく「間違った答えを返す」壊れ方</b>なので、
    /// 打ち切り時間をどう調整しても直らない。
    /// </para>
    /// <para>
    /// <b>「<c>LauncherMutex</c> が直列化しているから相関 ID は不要」は成り立たない。</b>
    /// <c>LauncherMutex</c> が直列化するのは<b>ランチャー側だけ</b>である。
    /// ワーカーのコマンド処理は UI スレッドの <c>DispatcherQueue</c> 上で走り、
    /// ランチャーが諦めて Mutex を放した後も続く ── 通知は Mutex の外側から来る。
    /// </para>
    /// <para>
    /// <b>ワーカーは ID を「届いた瞬間」に読み、読んだら消す</b>
    /// （<see cref="ClaimRequestId"/>）。消さないと、<c>Activated</c> が想定外に余分に
    /// 発火したときに<b>まだ生きているランチャーの ID を拾って偽の結果を刻める</b>。
    /// 1つの ID につき発火は1回なので、消しても正常系は何も失わない。
    /// </para>
    /// <para>
    /// <b>残る窓（既知・許容）:</b> ID が届くのはリダイレクトの<b>直前</b>に書かれた
    /// 共有スロット経由で、<c>AppActivationArguments</c> そのものには載らない
    /// （<c>AppInstance.GetCurrent().GetActivatedEventArgs()</c> で得た他人の器に
    /// 値を足す手段が WinAppSDK に無い）。したがって<b>リダイレクトの配送そのものが
    /// ランチャー1世代ぶん遅れた</b>場合、古い活性化が新しい ID を claim しうる。
    /// この窓の実際の幅は未計測（<c>RedirectActivationToAsync</c> の完了が
    /// <c>Activated</c> の発火と同期かどうかを確かめていない）。<b>設計の正しさは
    /// この同期性に依存しない</b> ── 依存するのは窓の幅だけである。
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b><c>partial</c> は必須</b>（<c>CsWinRT1028</c>）。<see cref="IDisposable"/> は
    /// WinRT の <c>IClosable</c> に射影されるので、<c>partial</c> でないと
    /// トリミング／AOT 用のマーシャリングコードが生成されない。
    /// </remarks>
    internal sealed partial class CommandResultChannel : IDisposable
    {
        // レイアウト（この型が唯一の定義元。両側が同じ定数を使うこと）:
        //   [requestId:Guid][resultId:Guid][exitCode:int][stdout長:int][stderr長:int][stdout UTF-8][stderr UTF-8]
        //
        // requestId はランチャーが書き、ワーカーが読んで消す（claim）。
        // resultId はワーカーが**結果の中身を書き終えた後に**書く（＝「読んでよい」の印）。
        private const int GuidSize = 16;
        private const int RequestIdOffset = 0;
        private const int ResultIdOffset = RequestIdOffset + GuidSize;
        private const int ExitCodeOffset = ResultIdOffset + GuidSize;
        private const int OutputLengthOffset = ExitCodeOffset + sizeof(int);
        private const int ErrorLengthOffset = OutputLengthOffset + sizeof(int);
        private const int PayloadOffset = ErrorLengthOffset + sizeof(int);

        /// <summary>コンソール出力（stdout + stderr）に使える合計バイト数。</summary>
        internal const int PayloadCapacity = 64 * 1024;

        /// <summary>結果通知用 MMF の容量。</summary>
        internal const int Capacity = PayloadOffset + PayloadCapacity;

        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly EventWaitHandle _resultReady;
        private readonly Guid _requestId;

        private CommandResultChannel(MemoryMappedFile map, EventWaitHandle resultReady, Guid requestId)
        {
            _map = map;
            _accessor = map.CreateViewAccessor(0, Capacity);
            _resultReady = resultReady;
            _requestId = requestId;
        }

        /// <summary>
        /// ランチャー側でチャネルを張り、<paramref name="requestId"/> のコマンドの結果を
        /// 待てる状態にする。<b>リダイレクトより前に呼ぶこと</b>
        /// ── ワーカーは既存のオブジェクトを開くだけでよい、という前提を守るため。
        /// </summary>
        internal static CommandResultChannel CreateForRequest(string keyPrefix, Guid requestId)
        {
            var resultReady = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, Names.CommandResultReadyEvent(keyPrefix));
            MemoryMappedFile? map = null;
            try
            {
                map = MemoryMappedFile.CreateOrOpen(Names.CommandResultMap(keyPrefix), Capacity);
                var channel = new CommandResultChannel(map, resultReady, requestId);
                channel.Arm();
                return channel;
            }
            catch (Exception)
            {
                map?.Dispose();
                resultReady.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 結果待ちを始める前の初期化。
        ///
        /// <para>
        /// <b>リクエスト ID は最後に書く</b> ── 掃除を全部済ませてから ID を公開する。
        /// これで「ID が公開されている＝結果スロットは空だと確定している」が成り立ち、
        /// <b>公開後にこのスロットへ書くのはワーカーだけ</b>になる。
        /// 逆順にすると、ID が生きている状態で掃除が走る区間ができる
        /// ── そこへ書かれた結果は消える。
        /// </para>
        /// <para>
        /// <b>取り残された活性化が居ても壊れない。</b> リセットと掃除の間に古い結果が
        /// 書かれた場合、合図はシグナル状態のまま <c>resultId</c> だけが消える
        /// ── 待ち手は一度空振りして、そのまま待ち直す（相関 ID が一致しないため）。
        /// <b>自分の ID を claim されることはこの時点ではあり得ない</b>（まだ公開していない）。
        /// </para>
        /// </summary>
        private void Arm()
        {
            _resultReady.Reset();
            WriteGuid(_accessor, ResultIdOffset, Guid.Empty);
            Thread.MemoryBarrier();
            WriteGuid(_accessor, RequestIdOffset, _requestId);
        }

        /// <summary>
        /// 自分の <c>requestId</c> が刻まれた結果が届くまで待つ。
        ///
        /// <para>
        /// <b>他人の結果を掴んだら捨てて待ち直すが、予算は延長しない。</b>
        /// <paramref name="timeoutMs"/> は<b>締め切り</b>であって1回の待ちの長さではない
        /// ── 捨てるたびに測り直すと、<c>LauncherMutex</c> の占有が「取り残された
        /// コマンドの本数 × 60 秒」まで伸び、順番待ちの CLI が全部その分待たされる。
        /// </para>
        /// </summary>
        /// <returns>自分の結果を受け取れたか。<c>false</c> は締め切り切れ（＝成否不明）。</returns>
        internal bool TryWaitForResult(int timeoutMs, out CommandResult result)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                int remaining = timeoutMs - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    result = default;
                    return false;
                }

                if (!_resultReady.WaitOne(remaining))
                {
                    result = default;
                    return false;
                }

                if (TryReadResult(out result))
                    return true;

                // 相関 ID が違う＝取り残された古いコマンドの結果。
                // 合図を消費したのは正しい（この合図はその古い結果のものだった）。
            }
        }

        private bool TryReadResult(out CommandResult result)
        {
            result = default;

            if (ReadGuid(_accessor, ResultIdOffset) != _requestId)
                return false;

            Thread.MemoryBarrier();
            int exitCode = _accessor.ReadInt32(ExitCodeOffset);
            string output = ReadText(OutputLengthOffset, PayloadOffset, out int outputLength);
            string error = ReadText(ErrorLengthOffset, PayloadOffset + outputLength, out _);

            // **払い出す直前にもう一度 ID を見る。** 読んでいる間に別の（取り残された）
            // コマンドの結果が上書きしていた場合、ここまでの中身は2つの混成になっている。
            //
            // **残る窓（既知・許容）**: ここで捨てると、自分の結果を二度と拾えず
            // 締め切り切れで ExitCode_WorkerResultTimeout（2）になることがある。
            // だが 2 は従来からある「成否不明」であって、**間違った答えではない**
            // ── 混ぜて返すより厳密に良い。踏むには「取り残されたコマンドが2本以上、
            // かつ読み取りの最中に完了する」必要がある。
            Thread.MemoryBarrier();
            if (ReadGuid(_accessor, ResultIdOffset) != _requestId)
                return false;

            result = new CommandResult(exitCode, output, error);
            return true;
        }

        /// <summary>
        /// 常駐ワーカー側で「今どのコマンドの結果を待たれているか」を取り出す。
        /// <b>読んだ ID はその場で消す</b>（同じ ID を2回配らない）。
        /// </summary>
        /// <returns>
        /// 相関 ID。<see cref="Guid.Empty"/> は<b>待っている相手が居ない</b>
        /// （チャネルが無い／既に claim 済み）ことを表す。
        /// </returns>
        internal static Guid ClaimRequestId(string keyPrefix)
        {
            try
            {
                using var map = MemoryMappedFile.OpenExisting(Names.CommandResultMap(keyPrefix));
                using var accessor = map.CreateViewAccessor(0, Capacity);

                var claimed = ReadGuid(accessor, RequestIdOffset);
                WriteGuid(accessor, RequestIdOffset, Guid.Empty);
                return claimed;
            }
            catch (Exception)
            {
                // 引数無しの起動（結果を待つランチャーが居ない）ではチャネルが存在しない。
                // ここで投げると常駐ワーカーが落ちるので、握り潰して「相手が居ない」を返す。
                return Guid.Empty;
            }
        }

        /// <summary>
        /// コマンド処理の結果を、<paramref name="requestId"/> を刻んでランチャーへ通知する。
        /// 通知先の名前付きオブジェクトはランチャーが先に作成済みのはずだが、万一開けなくても
        /// 常駐ワーカー自体を落とさないよう例外は握り潰す（その場合ランチャー側は
        /// 結果待ちがタイムアウトし、専用の終了コードを返す）。
        /// </summary>
        internal static void Publish(
            string keyPrefix, Guid requestId, int exitCode, string? consoleOutput, string? consoleError)
        {
            if (requestId == Guid.Empty)
            {
                // 受け取れる相手が居ない（ランチャーが既に諦めた／そもそも待っていない）。
                // 無印の結果を書き込むと、**次のコマンドがそれを読む余地を作るだけ**なので書かない。
                //
                // **この行を外しても L1 は赤くならない**（注入で確認済み）──
                // ランチャー側の相関 ID 照合が同じことを保証しているため。
                // ＝ここは二重の防護であって、単独の不変条件ではない。
                return;
            }

            try
            {
                // 容量超過分は切り詰める（stdout を優先して格納する）。
                byte[] outputBytes = consoleOutput is null ? [] : Encoding.UTF8.GetBytes(consoleOutput);
                byte[] errorBytes = consoleError is null ? [] : Encoding.UTF8.GetBytes(consoleError);
                int outputLength = Math.Min(outputBytes.Length, PayloadCapacity);
                int errorLength = Math.Min(errorBytes.Length, PayloadCapacity - outputLength);

                using var map = MemoryMappedFile.OpenExisting(Names.CommandResultMap(keyPrefix));
                using (var accessor = map.CreateViewAccessor(0, Capacity))
                {
                    accessor.Write(ExitCodeOffset, exitCode);
                    accessor.Write(OutputLengthOffset, outputLength);
                    accessor.Write(ErrorLengthOffset, errorLength);
                    if (outputLength > 0)
                        accessor.WriteArray(PayloadOffset, outputBytes, 0, outputLength);
                    if (errorLength > 0)
                        accessor.WriteArray(PayloadOffset + outputLength, errorBytes, 0, errorLength);

                    // **相関 ID は最後に書く。** ランチャーは ID の一致を「読んでよい」の
                    // 印にしているので、先に書くと**中身が揃う前に読まれうる**。
                    Thread.MemoryBarrier();
                    WriteGuid(accessor, ResultIdOffset, requestId);
                }

                using var resultReadyEvent = new EventWaitHandle(
                    initialState: false, EventResetMode.AutoReset, Names.CommandResultReadyEvent(keyPrefix));
                resultReadyEvent.Set();
            }
            catch (Exception)
            {
            }
        }

        private string ReadText(int lengthOffset, int payloadOffset, out int length)
        {
            // 長さは共有メモリから読む値なので、必ず範囲内に丸めてから使う
            // （壊れた値をそのまま new byte[] に渡すと、結果通知が例外で消える）。
            length = Math.Clamp(_accessor.ReadInt32(lengthOffset), 0, Capacity - payloadOffset);
            if (length == 0)
                return string.Empty;

            var bytes = new byte[length];
            _accessor.ReadArray(payloadOffset, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteGuid(MemoryMappedViewAccessor accessor, int offset, Guid value)
        {
            byte[] bytes = value.ToByteArray();
            accessor.WriteArray(offset, bytes, 0, GuidSize);
        }

        private static Guid ReadGuid(MemoryMappedViewAccessor accessor, int offset)
        {
            var bytes = new byte[GuidSize];
            accessor.ReadArray(offset, bytes, 0, GuidSize);
            return new Guid(bytes);
        }

        public void Dispose()
        {
            _accessor.Dispose();
            _map.Dispose();
            _resultReady.Dispose();
        }
    }
}
