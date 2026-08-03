using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// テスト用 settings.json の組み立て。
///
/// <para>
/// <b>エンコーダーと GOP 長は必ず固定する。</b> 事前バッファの検証は生成 MP4 の「尺」で
/// 判定するが、GOP 長が事前バッファ長より長いと録画開始点の I フレームが無く、
/// 尺が検証対象と無関係な理由で短くなる（GPU 実機で実証済み）。
/// ここでは <c>PreferredH264Encoder</c> でカタログ経由の候補を1つに固定する
/// ── 起動文字列と GOP 長を製品側の定義（<c>EncoderCatalog</c>）から取るため、
/// テストが独自のエンコーダー設定を持って製品と食い違うことがない。
/// </para>
/// </summary>
public sealed class SettingsFile
{
    /// <summary>
    /// CI ランナーには GPU が無い（WARP）。<c>Type=System</c> + <c>videotestsrc</c> +
    /// <c>x264enc</c> を明示するのは、GPU 不在時のソフトウェアエンコーダーへの
    /// フォールバックが効いていることの実証でもある。
    /// </summary>
    public const string DefaultEncoder = "x264enc";

    /// <summary>軽い既定ソース。GPU 無しの環境でも 15fps を落とさずに出せる。</summary>
    public const string SmallVideoTestSrc =
        "videotestsrc is-live=true do-timestamp=true ! videoconvert ! " +
        "video/x-raw,format=I420,width=320,height=240,framerate=15/1";

    /// <summary>
    /// <b>H.264 エンコーダーが直接は受け付けない画素形式</b>で終わるソース。
    /// <see cref="EncoderNegotiationTests"/> 専用。
    ///
    /// <para>
    /// <c>x264enc</c> の sink は <c>{Y444, Y42B, I420, YV12, NV12, GRAY8}</c> で
    /// <b><c>BGRA</c> を含まない</b>（<c>gst-inspect</c> の実測）。
    /// <c>openh264enc</c> は <b>I420 のみ</b>、<c>mfh264enc</c> は
    /// <c>{I420, YV12, NV12, YUY2}</c> ── <b>どれも BGRA を受けない</b>ので、
    /// エンコーダーが何であれ「変換が要る」状態を作れる。
    /// </para>
    /// <para>
    /// <b>末尾の capsfilter が要点。</b> 手前に <c>videoconvert</c> が在っても、
    /// ここで形式を固定した時点で下流はその形式のまま流れる
    /// ── 交渉の余地を消しているのは<b>このソース文字列自身</b>である。
    /// </para>
    /// <para>
    /// <c>dwriteclockoverlay</c> の sink caps は <c>video/x-raw(ANY)</c> なので
    /// BGRA はそのままエンコーダーの手前まで届く（実測）。
    /// </para>
    /// </summary>
    public const string UnconvertibleFormatVideoTestSrc =
        "videotestsrc is-live=true do-timestamp=true ! videoconvert ! " +
        "video/x-raw,format=BGRA,width=320,height=240,framerate=15/1";

    /// <summary>
    /// 大きいファイルを作るためのソース。<c>mp4mux faststart=true</c> は EOS 後に
    /// ファイル全体を書き直すので、排出コストはファイルサイズにほぼ比例する
    /// ── 「停止の同期性」は小さい録画では原理的に検出できない（実測済み）。
    /// </summary>
    public const string LargeVideoTestSrc =
        "videotestsrc is-live=true do-timestamp=true ! videoconvert ! " +
        "video/x-raw,format=I420,width=1280,height=720,framerate=30/1";

    /// <summary>大きいファイル用のエンコーダー指定（約 20Mbit）。</summary>
    public const string LargeEncodingProperties =
        "x264enc tune=zerolatency bitrate=20000 speed-preset=ultrafast key-int-max=30";

    /// <summary>
    /// <b>ファイルは大きいまま、エンコードは軽い</b>ソース。GUI を操作しながら録画する
    /// <c>ShutdownTests.CtrlClose_WhileRecording_FinalizesEveryFile</c> 専用。
    ///
    /// <para>
    /// <b>この2つの要求は別々のつまみに乗っている。</b> 排出の検出力は<b>バイト数</b>で決まり
    /// （<c>mp4mux faststart=true</c> は EOS 後にファイル全体を書き直す）、
    /// UI スレッドの飢えは<b>毎秒の画素数</b>で決まる。<see cref="LargeVideoTestSrc"/> は
    /// 両方を上げてしまうので、GPU の無い 2 vCPU のランナーでは
    /// <b>UIA の要素が 0 件</b>になるところまで UI スレッドが応答しなくなった。
    /// </para>
    /// <para>
    /// <b><c>pattern=snow</c> が要点で、解像度を下げるだけでは成立しない。</b>
    /// 既定の SMPTE バーは圧縮が効きすぎるため、画素数を落とすと x264 が
    /// 指定ビットレートを埋められず<b>ファイルが縮んで検出力が消える</b>
    /// ── 実測（この開発機・10 秒録画）:
    /// </para>
    /// <list type="table">
    ///   <item><description>1280x720/30fps バー（<see cref="LargeVideoTestSrc"/>）… 31MB / CPU 1.6〜2.0 コア</description></item>
    ///   <item><description>640x480/15fps バー … <b>6.3MB</b>（4.6Mbit しか出ない＝検出力が消える）</description></item>
    ///   <item><description>640x360/15fps snow（これ）… <b>31MB</b> / CPU <b>1.1〜1.2 コア</b></description></item>
    /// </list>
    /// <para>
    /// アプリは小さいレコーダー1本でも 1.1 コア使う（＝これが下限）ので、
    /// この構成の負荷は<b>実質そこまで下がっている</b>。バイト数と排出時間は据え置きで、
    /// <c>stop-recording</c> の実測は 877〜1003ms（<see cref="LargeVideoTestSrc"/> では 718〜897ms）と<b>むしろ長い</b>。
    /// </para>
    /// <para>
    /// <b><see cref="LargeVideoTestSrc"/> を作り替えなかったのは意図的。</b> あちらは
    /// <c>StopSynchronicityTests</c> の 20MB 下限と CLI の <c>await</c> を外す注入に対して
    /// 較正済みで、**現に検出できることが実測されている**。共用のヘルパーを変えると
    /// その較正をやり直すまで両方が未検証になる。
    /// </para>
    /// </summary>
    public const string BulkyCheapVideoTestSrc =
        "videotestsrc is-live=true do-timestamp=true pattern=snow ! videoconvert ! " +
        "video/x-raw,format=I420,width=640,height=360,framerate=15/1";

    /// <summary>
    /// <see cref="BulkyCheapVideoTestSrc"/> 用のエンコーダー指定（約 20Mbit）。
    /// <b><c>key-int-max</c> はフレームレートと揃える</b> ── GOP 長が事前バッファ長
    /// （<see cref="RecorderSpec.BufferDuration"/>・既定 3000ms）より長いと、
    /// 録画開始点の I フレームが無くなって尺が別の理由で短くなる。
    /// </summary>
    public const string BulkyCheapEncodingProperties =
        "x264enc tune=zerolatency bitrate=20000 speed-preset=ultrafast key-int-max=15";

    /// <summary>
    /// <b>1フレームが <c>queue</c> の既定 <c>max-size-bytes</c> の半分を超える</b>ソース
    /// （＝プレビュー枝の queue に2フレーム目が入らない大きさ）。
    ///
    /// <para>
    /// <b>この「半分」が閾値であることが要点。</b> <c>queue</c> の既定は
    /// <c>max-size-bytes=10485760</c>（10MB）で、queue は上限を超えていても
    /// 1件目は必ず受け取る。したがって <c>1フレーム &gt; 5,242,880 バイト</c> になると
    /// プレビュー枝の queue は<b>常に1フレームしか持てない</b>。
    /// I420 は <c>幅×高×1.5</c> バイトなので、境界は <c>3,495,254 画素</c>
    /// ── 1920x1080(2.07Mpx) は下・2560x1440(3.69Mpx) は上。
    /// </para>
    /// <para>
    /// <b>2560x1440 は「再現する最小の解像度」として選んである。</b>
    /// エンコード費用は画素数に比例するので、これ以上大きくしても
    /// 検出できるものは変わらず費用だけが増える（実機の報告は 3840x2160）。
    /// <c>pattern=black</c> なのは、ここで見たいのが<b>フレームが流れるかどうか</b>だけで
    /// バイト数ではないため ── <see cref="BulkyCheapVideoTestSrc"/> とは目的が違う。
    /// </para>
    /// </summary>
    public const string OversizedFrameVideoTestSrc =
        "videotestsrc is-live=true do-timestamp=true pattern=black ! videoconvert ! " +
        "video/x-raw,format=I420,width=2560,height=1440,framerate=15/1";

    /// <summary>
    /// <b>最初の1フレームを出すまでに数フレームぶん入力を溜める</b>エンコーダー指定。
    ///
    /// <para>
    /// <b>ここで <c>tune=zerolatency</c> を使ってはいけない。</b> zerolatency の x264 は
    /// 1フレーム目で出力するため、録画側 <c>appsink</c> が即座にプリロールし、
    /// プレビュー枝の queue が詰まるより先にパイプラインが <c>PLAYING</c> に達してしまう
    /// ── <b>再現しない構成になり、「仮説が外れた」と読める緑になる。</b>
    /// </para>
    /// <para>
    /// <c>rc-lookahead=4</c> は実機で停止した <c>qsvh264enc</c> の
    /// <c>async-depth</c> 既定（4）に合わせてある。<c>threads=1</c> は
    /// フレーム並列によるさらなる遅延を排して、遅延の理由を lookahead ひとつに固定するため。
    /// </para>
    /// </summary>
    public const string SmallLookaheadEncodingProperties =
        "x264enc speed-preset=ultrafast rc-lookahead=4 bframes=0 threads=1 key-int-max=15";

    /// <summary>
    /// <b>1フレームも下流へ届かないソース。</b> 障害は設定だけで起こす方針
    /// （<c>identity error-after=N</c> と同じ流儀）に沿って <c>identity drop-probability=1.0</c> を使う。
    ///
    /// <para>
    /// <b>caps は通り、バッファだけが消える</b>ので、<c>ParseLaunch</c> もリンクも
    /// <c>SetState(Playing)</c> も成功する ── <b>「初期化は成功したのに何も流れない」を
    /// 決定的に作り出せる唯一の形。</b> 実機で報告された 4K 停止が外から見えていた姿と同じで、
    /// あちらとの違いは止まる理由だけ。
    /// </para>
    /// <para>
    /// 解像度は小さいままでよい（詰まりではなく<b>到達しないこと</b>を見るため）。
    /// </para>
    /// </summary>
    public const string SilentVideoTestSrc =
        "videotestsrc is-live=true do-timestamp=true ! identity drop-probability=1.0 ! videoconvert ! " +
        "video/x-raw,format=I420,width=320,height=240,framerate=15/1";

    /// <summary>
    /// <b>初期化は成功するが、しばらくしてソースが終わる</b>構成
    /// （<c>num-buffers</c> ぶん出したら EOS）。
    ///
    /// <para>
    /// <see cref="SilentVideoTestSrc"/> との違いが要点。あちらは<b>1フレームも出ない</b>ので
    /// <c>PLAYING</c> 到達待ちに掛かって<b>初期化が失敗する</b>。こちらは
    /// 最初のフレームが出るので<b>初期化は成功し、その後で供給が止まる</b> ──
    /// つまり<b>「健全なレコーダーが、録画しても1フレームも書けない」</b>状態を
    /// 設定だけで作れる（注入ではない）。実際に観測された 587 バイトの空 MP4 の<b>症状</b>と同じ形。
    /// </para>
    /// <para>
    /// <b>ここで効いているのは製品側の構造</b> ── 事前バッファの排出は
    /// 「<c>TryPullSample</c> が実を返したとき」にしか走らないので、
    /// ソースが終わっているとリングバッファに溜まっていた分すら押し込まれない。
    /// </para>
    /// <para>
    /// <b><c>num-buffers</c> は「初期化が終わるより十分あと」に EOS が来る値にすること。</b>
    /// 15fps × 75 ＝ <b>5 秒</b>。初期化の実測は 0.39〜0.67 秒（この開発機）だが、
    /// GPU の無い 2 vCPU のランナーはその何倍もかかりうる ── ここを詰めると
    /// <b>初期化失敗（終了コード 14）に化けて、まったく別のテストになる。</b>
    /// </para>
    /// </summary>
    public const string EndingVideoTestSrc =
        "videotestsrc is-live=true do-timestamp=true num-buffers=75 ! videoconvert ! " +
        "video/x-raw,format=I420,width=320,height=240,framerate=15/1";

    public int DataVersion { get; set; } = 1;
    public string PreferredH264Encoder { get; set; } = DefaultEncoder;
    public string GstDebug { get; set; } = "";

    /// <summary>グラフ(.dot)の保存先。null なら書かない（＝製品の既定＝空欄＝データディレクトリ）。</summary>
    public string? GstDebugDumpDotDir { get; set; }

    public int? StopFinalizeTimeoutMs { get; set; }

    /// <summary>録画の保存先。null なら書かない（＝製品の既定＝実行ファイルのあるディレクトリ）。</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>古い mp4 の自動削除（日数）。null なら書かない（＝製品の既定＝0＝削除しない）。</summary>
    public int? RecordingRetentionDays { get; set; }

    /// <summary>自動削除の間隔（時間）。null なら書かない。</summary>
    public int? RecordingCleanupIntervalHours { get; set; }

    public Dictionary<string, string> TemplateVariables { get; } = [];
    public List<RecorderSpec> Recorders { get; } = [];

    /// <summary>レコーダーを1件追加して返す（設定を続けて書き換えられるように参照を返す）。</summary>
    public RecorderSpec AddRecorder(string name)
    {
        var recorder = new RecorderSpec(name);
        Recorders.Add(recorder);
        return recorder;
    }

    /// <summary>settings.json を書き出す。既存ファイルは上書きする。</summary>
    public void WriteTo(string path, string recordingsDir)
    {
        string dataDir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dataDir);

        var root = new JsonObject
        {
            ["DataVersion"] = DataVersion,
            ["DebugLogFile"] = Path.Combine(dataDir, "debug.log"),
            ["GstDebug"] = GstDebug,
            ["PreferredH264Encoder"] = PreferredH264Encoder,
        };

        if (GstDebugDumpDotDir is { } dotDir)
            root["GstDebugDumpDotDir"] = dotDir;

        if (StopFinalizeTimeoutMs is { } timeout)
            root["StopFinalizeTimeoutMs"] = timeout;

        if (OutputDirectory is { } outputDirectory)
            root["OutputDirectory"] = outputDirectory;
        if (RecordingRetentionDays is { } retentionDays)
            root["RecordingRetentionDays"] = retentionDays;
        if (RecordingCleanupIntervalHours is { } intervalHours)
            root["RecordingCleanupIntervalHours"] = intervalHours;

        if (TemplateVariables.Count > 0)
        {
            var variables = new JsonObject();
            foreach (var (key, value) in TemplateVariables)
                variables[key] = value;
            root["TemplateVariables"] = variables;
        }

        var recorders = new JsonArray();
        foreach (var recorder in Recorders)
            recorders.Add(recorder.ToJson(recordingsDir));
        root["Recorders"] = recorders;

        // 製品側の設定は UTF-8（BOM 無し）・PascalCase・インデント付きで読み書きされる
        // （AppSettings.SettingsTypeInfo。非 ASCII は \uXXXX へ逃がさない）。
        // ここは読ませる側なので書式は問われない ── 揃えてあるのは差分を読みやすくするため。
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
}

/// <summary>settings.json 内の1レコーダー。</summary>
public sealed class RecorderSpec(string name)
{
    public string Name { get; set; } = name;

    /// <summary>事前バッファ長(ms)。</summary>
    public int BufferDuration { get; set; } = 3000;

    /// <summary>
    /// null なら「録画先ディレクトリ配下の <c>{Name}_{Now:HHmmssfff}.mp4</c>」。
    /// <b>相対パスを使ってよいのは <see cref="SettingsFile.OutputDirectory"/> を
    /// 指定した場合だけ</b> ── 指定しないと実行ファイル（＝発行ディレクトリ）の
    /// あるところに書かれる。
    /// </summary>
    public string? FilenameTemplate { get; set; }

    /// <summary><c>System</c>=0 / <c>D3d12</c>=1。既定は GPU を要求しない System。</summary>
    public EventRecordingType Type { get; set; } = EventRecordingType.System;

    public string SrcPipeline { get; set; } = SettingsFile.SmallVideoTestSrc;

    /// <summary>null なら自動選択（<c>PreferredH264Encoder</c> が効く）。</summary>
    public string? EncodingProperties { get; set; }

    /// <summary>1280x720/30fps・約20Mbit（排出コストを可視化できる大きさ）にする。</summary>
    public RecorderSpec AsLarge()
    {
        SrcPipeline = SettingsFile.LargeVideoTestSrc;
        EncodingProperties = SettingsFile.LargeEncodingProperties;
        return this;
    }

    /// <summary>
    /// <see cref="AsLarge"/> と<b>同じだけのバイト数</b>を、<b>はるかに軽い CPU 負荷</b>で作る
    /// （640x360/15fps の <c>snow</c>・約20Mbit）。
    /// GUI を操作しながら録画するケース専用 ── 理由は
    /// <see cref="SettingsFile.BulkyCheapVideoTestSrc"/> に書いてある。
    /// </summary>
    public RecorderSpec AsBulkyButCheapToEncode()
    {
        SrcPipeline = SettingsFile.BulkyCheapVideoTestSrc;
        EncodingProperties = SettingsFile.BulkyCheapEncodingProperties;
        return this;
    }

    /// <summary>
    /// <b>1フレームがプレビュー枝の queue に2つ入らない大きさ</b>にし、
    /// <b>最初の1フレームを出すまでに入力を溜める</b>エンコーダーを指定する
    /// （2560x1440/15fps・<c>rc-lookahead=4</c>）。理由は
    /// <see cref="SettingsFile.OversizedFrameVideoTestSrc"/> と
    /// <see cref="SettingsFile.SmallLookaheadEncodingProperties"/> に書いてある。
    /// </summary>
    public RecorderSpec AsOversizedFrames()
    {
        SrcPipeline = SettingsFile.OversizedFrameVideoTestSrc;
        EncodingProperties = SettingsFile.SmallLookaheadEncodingProperties;
        return this;
    }

    /// <summary>
    /// <b>リンクも状態遷移も成功するのに、1フレームも下流へ届かない</b>構成にする。
    /// 理由は <see cref="SettingsFile.SilentVideoTestSrc"/> に書いてある。
    ///
    /// <para>
    /// <b>エンコーダーを明示するのは待ち時間のため。</b> 自動選択にすると
    /// 候補の数だけ <c>EventRecorder.PlayingStateTimeoutMs</c> を消費する。
    /// </para>
    /// </summary>
    public RecorderSpec AsSilentSource()
    {
        SrcPipeline = SettingsFile.SilentVideoTestSrc;
        EncodingProperties = SettingsFile.DefaultEncoder;
        return this;
    }

    /// <summary>
    /// <b>初期化は成功し、その後でソースが終わる</b>構成にする。
    /// 理由と <c>num-buffers</c> の決め方は <see cref="SettingsFile.EndingVideoTestSrc"/> に書いてある。
    /// </summary>
    public RecorderSpec AsSourceThatEnds()
    {
        SrcPipeline = SettingsFile.EndingVideoTestSrc;
        EncodingProperties = SettingsFile.DefaultEncoder;
        return this;
    }

    internal JsonObject ToJson(string recordingsDir) => new()
    {
        ["Name"] = Name,
        ["BufferDuration"] = BufferDuration,
        ["FilenameTemplate"] = FilenameTemplate ?? Path.Combine(recordingsDir, "{Name}_{Now:HHmmssfff}.mp4"),
        ["Type"] = (int)Type,
        ["SrcPipeline"] = SrcPipeline,
        ["EncodingProperties"] = EncodingProperties,
    };
}

/// <summary>製品側の <c>EventRecordingType</c> と同じ並び（JSON には数値で入る）。</summary>
public enum EventRecordingType
{
    System = 0,
    D3d12 = 1,
}
