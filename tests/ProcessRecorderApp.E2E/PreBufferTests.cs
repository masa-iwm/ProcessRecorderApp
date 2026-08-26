using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// アプリの中核契約 ──<b>録画ボタンを押す前の映像が残ること</b>── の検証。
///
/// <para>
/// 最優先で落とせなければならないミューテーションは
/// 「<c>PushRecordBuffer</c> の立ち上がりエッジでリングバッファ全体ではなく最新バッファだけを
/// push する」── これを落とせなければ、他のテストは全部無意味になる。
/// そのため判定は<b>差分</b>で行う ── 事前バッファ有りと無しの2件を同じ条件で録り、
/// 「有り」の尺が「無し」より明確に長いことを見る。尺の絶対値だけを見ると、
/// エンコーダーの立ち上がりや GOP の位相で下限を緩めざるを得ず、
/// ミューテーション（＝事前バッファが丸ごと消える）の結果と重なってしまう。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PreBufferTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>
    /// 録画ボタンを押してから停止するまでの時間。
    ///
    /// <para>
    /// <b>GOP 長に合わせて決めてある。</b> 録画は最初の I フレームから始まるので、
    /// 対照側（事前バッファ 0ms）は<b>必ず GOP 1 つぶんを失う</b> ── カタログの GOP は
    /// **フレームレートから 2 秒で逆算**されるので、このハーネスの既定ソース（15fps）では
    /// 30 フレーム＝2 秒。窓を GOP の 3 倍以上に取ることで、対照にも意味のある尺を残す。
    /// </para>
    /// </summary>
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 事前バッファ長。<b>GOP（このハーネスの 15fps で 2 秒）より
    /// 十分長いこと</b> ── バッファの中に I フレームが1枚も無いと遡れる映像が存在せず、
    /// 「事前バッファ有り」の側が製品と無関係な理由で対照と同じ尺になる。
    /// </summary>
    private const int PreBufferMs = 6000;

    /// <summary>リングバッファが満たされるまで待つ時間（事前バッファ長＋余裕）。</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(9);

    [Fact]
    public void WithPreBuffer_TheRecordingReachesBackBeforeTheStartCommand()
    {
        double withPreBuffer = RecordAndMeasure(PreBufferMs);
        double withoutPreBuffer = RecordAndMeasure(0);
        output.WriteLine($"尺: 事前バッファ {PreBufferMs}ms → {withPreBuffer:F3}s / 0ms → {withoutPreBuffer:F3}s " +
                         $"（録画窓 {RecordingWindow.TotalSeconds}s）");

        // 対照側が本当に録画窓ぶん録れていること。ここが空に近いと（例: 生成が丸ごと失敗した）、
        // 下の差分が事前バッファ以外の理由で開いてしまい、判定が意味を失う。
        //
        // 下限が録画窓（3秒）よりかなり低いのには理由が2つある:
        //   - 録画は最初の I フレームから始まるので、GOP 1つぶん（2 秒）まで削れる
        //   - 実効の録画窓は CLI の往復ぶんだけ伸び縮みする。AOT 発行物はランチャーの起動が速く、
        //     同じ設定でも selfcontained より短くなる（実測: 3.267s → 2.267s）
        Assert.True(withoutPreBuffer >= 3.5,
            $"事前バッファ無しの尺が短すぎます: {withoutPreBuffer:F3}s（録画窓は {RecordingWindow.TotalSeconds}s）");

        // これが本体の判定。事前バッファ 6000ms・GOP 2 秒なので、遡れるのは
        // リングバッファ内の最も古い I フレームまで＝4.0〜6.0 秒ぶん。
        // 立ち上がりエッジでリングバッファ全体を push しない退行では、この差が 0 以下になる
        // （録画開始後の最初の I フレームから始まるので、むしろ対照より短くなる）。
        Assert.True(withPreBuffer - withoutPreBuffer >= 1.5,
            $"事前バッファぶんの差が足りません: 有り {withPreBuffer:F3}s / 無し {withoutPreBuffer:F3}s");

        // 絶対値の下限（差分だけだと対照が壊れて短くなった場合に緩むため、補助として置く）。
        Assert.True(withPreBuffer >= 9.0,
            $"事前バッファ有りの尺が短すぎます: {withPreBuffer:F3}s");
    }

    /// <summary>
    /// 指定した事前バッファ長で1回録画し、生成された MP4 の尺（秒）を返す。
    /// </summary>
    private double RecordAndMeasure(int bufferDurationMs)
    {
        var settings = new SettingsFile();
        settings.FragmentedOutput = false;
        settings.AddRecorder("R1").BufferDuration = bufferDurationMs;

        using var instance = AppInstance.Create(app, settings);

        // エンコーダーが期待どおり固定されていることを先に表明する。
        // GOP 長は候補ごとに製品側（EncoderCatalog）が固定しており、
        // 別のエンコーダーが選ばれると尺が検証対象と無関係な理由で揺れる。
        var log = instance.ReadActivityLog();
        string selected = Assert.Single(ActivityLogFile.Events(log, "gst.encoder selected"));
        Assert.Contains(SettingsFile.DefaultEncoder, selected);

        // リングバッファが満たされるのを待つ（ここを待たないと「事前バッファ有り」でも
        // 遡れる映像が存在せず、テストが製品と無関係な理由で落ちる）。
        Thread.Sleep(SettleTime);

        var start = instance.Run("start-recording-all");
        Assert.Equal(0, start.ExitCode);

        Thread.Sleep(RecordingWindow);

        var stop = instance.Run("stop-recording-all");
        Assert.Equal(0, stop.ExitCode);

        string file = Assert.Single(instance.ListRecordings());
        var probe = RecordedMp4.AssertUsable(file, instance, output);

        return probe.DurationSeconds!.Value;
    }
}
