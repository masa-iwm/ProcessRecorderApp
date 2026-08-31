using System.Diagnostics;
using System.Drawing;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b>パディングの入った buffer から、歪んでいないサムネイルが撮れること（D3d12 経路）。</b>
///
/// <para>
/// <b>これは「meta の枝が一度も走らない」という検査側の穴を塞ぐテストである。</b>
/// サムネイルの平面レイアウトは <c>GstVideoMeta</c> →  caps（<c>GstVideoInfo</c>）→ 既定
/// の順に落ちる（<c>EventRecorder.ReadFrameLayout</c>）が、<c>videotestsrc</c> の buffer は
/// meta を持たないので、他の E2E が通るのは<b>caps の枝だけ</b>である
/// ── <c>GstVideoInfo</c> は<b>既定レイアウトしか表せない</b>ため、
/// caps の枝が緑でも「実際の stride で読む」ことは一度も確かめられていない。
/// </para>
/// <para>
/// <b>この寸法では「撮らない」側へは倒れない ── だからログだけでは足りない。</b>
/// 製品の <c>D3d12</c> 経路のプレビュー枝は
/// <c>d3d12convert ! … ! tee ! queue ! d3d12download</c> で、ここへ届く NV12 の buffer は
/// <b>幅 640 に対し stride 1024</b>・<b>最終行は stride まで埋めない</b>
/// （実測: 640x480 の 1 フレームは <b>736,896 バイト</b>）。既定レイアウト（stride ＝ 幅）の
/// 必要長は <b>460,800 バイト</b>で<b>収まってしまう</b>ので、
/// <c>thumbnail.unsupported reason=buffer-too-short</c> にはならず、
/// <b>1 行あたり 384 バイトずつずれた絵が、異常の見えないまま撮れる</b>
/// （同じ buffer を <c>rawvideoparse</c> で既定レイアウトとして読ませて確認済み ──
/// 縞は完全に崩れ、白も青も残らない）。したがって<b>画素まで見ないと退行を検出できない</b>。
/// </para>
/// <para>
/// <b><c>thumbnail.*</c> は <c>activity.log</c> には出ない。</b> <c>DebugLogEx</c> は
/// GStreamer の <c>myapp</c> カテゴリへ書くので、<c>GstDebug</c> を <c>myapp:5</c>
/// （5 ＝ DEBUG）にして <c>DebugLogFile</c> に落ちたものを読む。<b>カテゴリを広げないこと</b>
/// ── 洪水になって読み取りが重くなるだけで、見たい行は増えない。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class ThumbnailLayoutTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>
    /// <b>縦縞だけの帯を持つソース</b>（SMPTE 100% カラーバー・640x480）。
    ///
    /// <para>
    /// <b>幅は 640 でなければならない。</b> stride が幅と一致する幅（256 の倍数）を選ぶと、
    /// meta を読まなくても同じ絵になり、<b>何も検出しないテストになる</b>。
    /// </para>
    /// <para>
    /// 縦縞なのは<b>斜行を机上で判定できる形にする</b>ためで、正しく読めた帯の中では
    /// どの行も同じ画素の並びになる（＝列ごとに一定）。単色のソースでは、
    /// 行がずれても絵が変わらないので検出できない。
    /// </para>
    /// </summary>
    private const string D3d12SmpteSrc =
        "d3d12testsrc is-live=true do-timestamp=true pattern=smpte ! " +
        "video/x-raw(memory:D3D12Memory), format=NV12, width=640, height=480, framerate=15/1";

    /// <summary>サムネイルが書かれ、ログの行が debug.log まで届くまでの上限。</summary>
    private static readonly TimeSpan ThumbnailBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public void OnTheD3d12Path_TheThumbnailIsReadWithTheStrideFromTheVideoMeta()
    {
        var settings = new SettingsFile { GstDebug = "myapp:5" };
        var recorder = settings.AddRecorder("R1");
        recorder.Type = EventRecordingType.D3d12;
        recorder.SrcPipeline = D3d12SmpteSrc;

        using var instance = AppInstance.Create(app, settings);

        Assert.NotEmpty(ActivityLogFile.Events(instance.ReadActivityLog(), "recorder.init ok"));
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "recorder.init fail"));

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        string file = Assert.Single(instance.ListRecordings());
        string thumbnail = file + ".png";
        string debugLog = Path.Combine(instance.DataDir, "debug.log");

        // **PNG の実在とログの到着は別々に待つ。** 行は書き込みの<b>後</b>に出て、
        // しかも gst → 標準エラー → DebugLogFile と経由するので、
        // File.Exists の直後に 1 回読むだけでは取りこぼす。
        // 行が持つのは<b>録画ファイル（.mp4）のパス</b>で、`.png` は書き手が足す
        // （`path=` は要求として積まれたパスそのもの）。
        string? written = null;
        var waiting = Stopwatch.StartNew();
        while (waiting.Elapsed < ThumbnailBudget)
        {
            written ??= ActivityLogFile.ReadLines(debugLog).FirstOrDefault(
                l => l.Contains("thumbnail.written", StringComparison.Ordinal)
                     && l.Contains(Path.GetFileName(file), StringComparison.Ordinal));
            if (written is not null && File.Exists(thumbnail))
                break;
            Thread.Sleep(250);
        }

        var debug = ActivityLogFile.ReadLines(debugLog);

        // **実在の断定を先に置く。** 「無いこと」の表明を先にすると、debug.log が
        // 1 行も無い（＝しきい値も捕捉も効いていない）場合に真空で通ってしまい、
        // 本当の失敗が 1 手先に押しやられる。
        Assert.True(File.Exists(thumbnail), $"D3d12 経路のサムネイルが書かれていない: {thumbnail}");
        Assert.True(written is not null, $"debug.log に thumbnail.written の行が無い（{debug.Count} 行）。");
        output.WriteLine(written);

        // 撮れなかった側（レイアウトを拒否した／例外）は、無いことを別に表明する
        // ── 斜行ではなくこちらへ倒れた退行を、行そのもので言い当てられるようにする。
        Assert.DoesNotContain(debug, l => l.Contains("thumbnail.unsupported", StringComparison.Ordinal));
        Assert.DoesNotContain(debug, l => l.Contains("thumbnail.capture failed", StringComparison.Ordinal));

        // **どの枝でレイアウトを読んだかを断定する。** stride 1024 の buffer を
        // caps / 既定の枝で読めば絵は歪むが、ここで先に落とせば理由が 1 行で分かる。
        Assert.Contains("source=meta", written, StringComparison.Ordinal);

        using var stream = new MemoryStream(File.ReadAllBytes(thumbnail));
        using var bitmap = new Bitmap(stream);
        output.WriteLine($"{bitmap.Width}x{bitmap.Height} {bitmap.PixelFormat}");

        // 幅は上限（320）まで、高さは縦横比を保つ ── 640x480 なら 320x240 の 1 通りしかない。
        Assert.Equal(320, bitmap.Width);
        Assert.Equal(240, bitmap.Height);

        // 検査する帯は<b>上から 1/4 〜 1/2</b>。上端には時計（dwriteclockoverlay は既定で
        // 左上へ描く）が、下 1/3 には SMPTE の別の帯（雑音を含む）が在るので、
        // 縦縞だけが在る範囲に限る。
        int top = bitmap.Height / 4;
        int bottom = bitmap.Height / 2;

        var reference = new Color[bitmap.Width];
        for (int x = 0; x < bitmap.Width; x++)
            reference[x] = bitmap.GetPixel(x, top);

        for (int y = top + 1; y < bottom; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                Assert.True(
                    Math.Abs(pixel.R - reference[x].R) <= RowTolerance
                    && Math.Abs(pixel.G - reference[x].G) <= RowTolerance
                    && Math.Abs(pixel.B - reference[x].B) <= RowTolerance,
                    $"縦縞の帯で行がずれている（斜行）: ({x},{y})={pixel} だが ({x},{top})={reference[x]}");
            }
        }

        // **行が揃っているだけでは足りない。** 灰色一色でも上の検査は通るので、
        // 縞が縞として撮れていること（＝横方向に構造が在ること）を色で断定する。
        // 色は BT.709 limited 固定で復号されるので、BT.601 のソースでは彩度の高い
        // 色がわずかにずれる ── 帯の中央を見て、緩い範囲で判定する。
        int middle = (top + bottom) / 2;
        var bars = new Color[SmpteBars];
        for (int i = 0; i < SmpteBars; i++)
            bars[i] = bitmap.GetPixel((2 * i + 1) * bitmap.Width / (2 * SmpteBars), middle);
        output.WriteLine("bars: " + string.Join(" ", bars.Select(c => $"({c.R},{c.G},{c.B})")));

        // 左端は白、右端は青（SMPTE 100% カラーバーの並び）。
        Assert.True(
            200 <= bars[0].R && 200 <= bars[0].G && 200 <= bars[0].B,
            $"左端の縞が白くない: {bars[0]}");
        Assert.True(
            200 <= bars[SmpteBars - 1].B && bars[SmpteBars - 1].R <= 60 && bars[SmpteBars - 1].G <= 60,
            $"右端の縞が青くない: {bars[SmpteBars - 1]}");
    }

    /// <summary>
    /// 同じ列の画素値に許す差。<b>緩めても検出力は落ちない</b> ── stride 1024 を 640 で
    /// 読んだときのずれは 1 行あたり 384 バイト（＝縞 4 本ぶん以上）で、
    /// 許容差では吸収できない大きさである。
    /// </summary>
    private const int RowTolerance = 2;

    /// <summary>SMPTE 100% カラーバーの縦縞の本数（白・黄・シアン・緑・マゼンタ・赤・青）。</summary>
    private const int SmpteBars = 7;
}
