using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <c>activity.log</c> の読み取り（1イベント＝1行・書式はインバリアント固定）。
///
/// <para>
/// <b>イベント名の照合を単純な前方一致で書いてはいけない。</b> 製品は成功と失敗を
/// イベント名で分けており（<c>recording.stop</c> と <c>recording.stop timeout</c>、
/// <c>recorder.init ok</c> と <c>recorder.init fail</c>）、前方一致だと
/// 「成功したか」を見るつもりの照合が失敗行にも一致してしまう。
/// そのため<b>既知のイベント名を表として持ち、最長一致で切り出す</b>。
/// </para>
/// </summary>
public static class ActivityLogFile
{
    /// <summary>
    /// 製品が記録するイベント名（src/README.md の <c>activity.log</c> のイベント表に
    /// 契約として明記されているもの）。
    /// 空白を含む名前があるため、1行の「イベント名」はこの表との最長一致で決める。
    /// </summary>
    public static readonly IReadOnlyList<string> KnownEvents =
    [
        "app.start",
        "app.exit",
        "app.error",
        "cli",
        "ping",
        "gst.runtime",
        "gst.encoders",
        "gst.encoder selected",
        "gst.encoder fallback-from",
        "gst.encoder candidate-failed",
        // バインディングの診断（Controller.StaticInitialize が購読する）。
        // 基底型のラッパーで包んだ／ネイティブのコールバック境界で例外を捕捉した。
        // 製品側のコールバックは自前で例外を握るので、通常は 1 件も出ない。
        "gst.typefallback",
        "gst.callback",
        "recorder.init ok",
        "recorder.init fail",
        "recorder.error",
        "recorder.warning",
        "recorder.eos",
        "recorder.restart",
        "recorder.leak",
        "recording.start",
        "recording.start fail",
        "recording.stop",
        "recording.stop slow",
        "recording.stop timeout",
        "recording.stop error",
        // 1フレームも書けずに終わった停止。**この表に足すのを忘れないこと** ──
        // 忘れると EventNameOf が最長一致で "recording.stop" に丸め、
        // 「recording.stop が2件」を数えている既存の表明が黙って3件になる。
        "recording.stop empty",
        "recording.aborted",
        // 常時録画。**分割は継続する方が損失が小さい**ので、1 本の失敗ではエンジンを止めない。
        // "continuous.finalize backlog" は "continuous.finalize" より長いので、
        // 最長一致の表に両方載せておかないと backlog が finalize として数えられる。
        "recorder.continuous-init ok",
        "recorder.continuous-init fail",
        "continuous.start",
        "continuous.finalize",
        "continuous.finalize backlog",
        "continuous.overshoot",
        "continuous.error",
        "continuous.stop",
        "continuous.leak",
        // Log 画面のターミナル。**どちらのレンダラーで描いているかは画面から見分けが付かない**ので、
        // ここが「WebView2 が実際に起きた」ことを確かめられる唯一の自動的な観測点になる。
        "log.terminal",
        // DebugLogFile を開けなかった（保存は諦めて捕捉は継続する）。
        "log.file error",
        // settings.json を読めず既定値へ倒れた／書けなかった。
        "settings.load",
        "settings.save",
        // 保存先に settings.json が無く、実行ファイルの隣の「種」を既定設定として読んだ。
        "settings.seed",
        // カメラ設定（CameraControls）を当てた／当てられなかった。録画は止めない。
        "camera.control",
        // カメラのデバイス列挙の結果（件数と device-path が読めた数）。
        "camera.devices",
        // モニターの列挙の結果（件数と device.path が読めた数）。0 台でも 1 行出る。
        "monitor.devices",
        // カメラ設定を開いたときの解決結果（開くたびに 1 行）。
        "camera.open",
        // プレビューパイプラインの実行時障害（録画は止めない。1パイプラインにつき1行）。
        // **これは画面の D3D プレビューのもの**で、下のライブ配信の 6 種とは別物。
        // 名前を寄せないこと ── 最長一致の表で衝突すると、どちらの観測点も読めなくなる。
        "preview.error",
        // リモート操作のライブプレビュー（fMP4 の配信）。購読の増減と、
        // それに応じた mux の起き/落ち。**表に無くても EventNameOf は最初のトークンへ
        // 倒すので名前自体は取れる**（空白を含まないため）が、載せないと
        // 「未知のイベント」として扱われ、名前の取り違えが起きても誰も気付けない。
        "preview.subscribe",
        "preview.unsubscribe",
        "preview.stream-start",
        "preview.stream-stop",
        "preview.stream-error",
        "preview.leak",
        // リモート操作の DASH プレビュー（再エンコードする第 2 パイプライン）。
        // **`preview.*` とは別物**で、こちらは購読の増減ではなく貸出（lease）で起き落ちする。
        "dash.stream-start",
        "dash.stream-stop",
        "dash.stream-error",
        "dash.leak",
        // 録画トランスコード。**停止理由がそのままイベント名の後半になる**
        // （`transcode.<理由>`・7 個。正本は `TranscodeSession.StopReasons`）。
        // 既知イベントとして表に載せ、失敗の診断で名前引きできるようにする。
        "transcode.start",
        "transcode.error",
        "transcode.leak",
        "transcode.eos",
        "transcode.client-closed",
        "transcode.replaced",
        "transcode.lease-expired",
        "transcode.start-failed",
        "transcode.shutdown",
        // デバイス到着の監視（復帰待ちのあいだだけプロバイダを started に保つ）と、
        // 観測した到着。到着で復帰の待ちを打ち切ったことは recorder.restart の
        // wake=device-arrival に出る。
        "device.watch",
        "device.arrive",
        // 古い mp4 の自動削除（何もしなかった周回は出さない）。
        "cleanup.run",
        "cleanup.error",
        // Variables 画面で既存のキーと重複する入力を差し戻した。
        "variables.duplicate-key",
        // GST_DEBUG のしきい値を実行中に適用した。適用は GStreamer の内部状態を変えるだけで
        // 画面にもファイルにも痕跡が残らないので、ここが唯一の観測点になる。
        "gst.debug",
        // リモート操作（LAN の別 PC のブラウザから）。サーバーの起動・停止・失敗と、
        // 書き込み要求の認証失敗。**"remote.auth fail" は空白を含む**ので、
        // 表に載せないと EventNameOf が最初のトークン "remote.auth" までしか切り出せない。
        "remote.start",
        "remote.stop",
        "remote.error",
        "remote.auth fail",
        // 成功も記録する。**"remote.auth fail" より長い名前を先に置く必要はない**
        // （切り出しは最長一致でこの表を並べ替えるため）が、載せ忘れると
        // ログインの行が丸ごと未知イベントとして落ちる。
        "remote.auth login",
        "remote.auth logout",
        // パイプラインのグラフ(.dot)を保存した。
        "gst.dot",
    ];

    private static readonly string[] LongestFirst = [.. KnownEvents.OrderByDescending(e => e.Length)];

    private static readonly Regex PidPattern = new(@"\bpid=(\d+)", RegexOptions.Compiled);

    private static readonly Regex ExitCodePattern = new(@"\bexitCode=(-?\d+)", RegexOptions.Compiled);

    /// <summary>行の先頭 23 文字はタイムスタンプ（<c>yyyy-MM-dd HH:mm:ss.fff</c>）。</summary>
    private const int TimestampLength = 23;

    public static IReadOnlyList<string> ReadLines(string path)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            // 常駐ワーカーが書いている最中でも読めるように共有を許す。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            return lines;
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>1行のイベント名（既知の表との最長一致）。書式に合わない行は null。</summary>
    public static string? EventNameOf(string line)
    {
        string? rest = AfterLevel(line);
        if (rest is null)
            return null;

        foreach (string candidate in LongestFirst)
        {
            if (rest.Equals(candidate, StringComparison.Ordinal) ||
                rest.StartsWith(candidate + " ", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        // 表に無いイベント名（製品側に追加されたばかり）は最初のトークンとして返す。
        // 黙って null にすると「イベントが1件も無い」と読めてしまう。
        int space = rest.IndexOf(' ');
        return space < 0 ? rest : rest[..space];
    }

    /// <summary>1行のレベル（<c>INFO</c> / <c>WARN</c> / <c>ERROR</c>）。</summary>
    public static string? LevelOf(string line)
    {
        if (line.Length <= TimestampLength + 1)
            return null;
        string tail = line[(TimestampLength + 1)..];
        int space = tail.IndexOf(' ');
        return space < 0 ? tail : tail[..space];
    }

    /// <summary>イベント名の後ろに続く詳細（無ければ空文字）。</summary>
    public static string DetailOf(string line)
    {
        string? rest = AfterLevel(line);
        if (rest is null || EventNameOf(line) is not { } name)
            return "";
        return rest.Length > name.Length ? rest[(name.Length + 1)..] : "";
    }

    /// <summary>行のタイムスタンプ。</summary>
    public static DateTime? TimestampOf(string line)
    {
        if (line.Length < TimestampLength)
            return null;
        return DateTime.TryParseExact(line[..TimestampLength], "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    }

    /// <summary>指定したイベント名に<b>完全一致</b>する行だけを返す。</summary>
    public static IReadOnlyList<string> Events(IEnumerable<string> lines, string eventName) =>
        [.. lines.Where(l => EventNameOf(l) == eventName)];

    /// <summary>
    /// <c>app.start</c> 行に記録された常駐ワーカーの pid。
    ///
    /// <para>
    /// <b>ここへ <c>app.exit</c> の pid を足してはいけない。</b> この列は
    /// <c>KillWorkers</c>（殺す相手）と <c>ListWorkerWindows</c>（ウィンドウの持ち主）が
    /// 使っており、<b>終了済みの pid は OS に再利用される</b>。
    /// 殺す側は <c>ProcessName</c> で守られているが、ウィンドウ側は守られておらず、
    /// 再利用された pid の別プロセスのウィンドウを<b>アプリのものとして誤って帰属</b>する。
    /// 終了の会計は <see cref="WorkerExits"/> で別に行うこと。
    /// </para>
    /// </summary>
    public static IEnumerable<int> WorkerPids(IEnumerable<string> lines) =>
        Events(lines, "app.start")
            .Select(l => PidPattern.Match(l))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct();

    /// <summary>
    /// <c>app.exit</c> 行に記録された終了（pid と終了コード）。
    ///
    /// <para>
    /// <b>これは <see cref="WorkerPids"/> の部分集合ではない。</b> 単一インスタンスの
    /// キー登録に負けたワーカーは <c>app.start</c> を書かずに終了する
    /// （<c>app.start</c> の記録はキーを取れた側の初期化コールバックの中にあるため）が、
    /// <c>app.exit ... exitCode=3</c> は書く。したがって<b>親を持たない <c>app.exit</c></b> が
    /// 実在し、それが「起動しようとして弾かれたワーカーが何回居たか」の唯一の痕跡になる。
    /// </para>
    /// </summary>
    public static IEnumerable<(int Pid, int ExitCode)> WorkerExits(IEnumerable<string> lines) =>
        Events(lines, "app.exit")
            .Select(l => (Pid: PidPattern.Match(l), Exit: ExitCodePattern.Match(l)))
            .Where(m => m.Pid.Success && m.Exit.Success)
            .Select(m => (
                int.Parse(m.Pid.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Exit.Groups[1].Value, CultureInfo.InvariantCulture)))
            .Distinct();

    private static string? AfterLevel(string line)
    {
        if (line.Length <= TimestampLength + 1)
            return null;
        string tail = line[(TimestampLength + 1)..];
        int space = tail.IndexOf(' ');
        return space < 0 ? null : tail[(space + 1)..];
    }
}
