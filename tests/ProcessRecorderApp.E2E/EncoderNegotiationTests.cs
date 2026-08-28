using System.Diagnostics;
using System.Drawing;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b>ソースの画素形式がエンコーダーの受け付ける形式と違っても録画できること。</b>
///
/// <para>
/// <b>これは実機で起きた障害の回帰テストである。</b> GPU 実機で
/// 同梱版を検証したところ <c>Type=System</c> の自動選択が
/// <c>recorder.init fail</c> で落ちた ──
/// <c>could not link queue1 to mfh264enc0</c>。
/// ハードウェアの MediaFoundation MFT は I420 を受けず、ソースは
/// <c>format=I420</c> で固定されていた。<c>parse_launch</c> は変換要素を
/// 自動挿入しないので、<b>形式が合わなければ初期化そのものが失敗する。</b>
/// </para>
/// <para>
/// <b>長らく出なかった理由がこのテストの存在理由でもある。</b>
/// L1・L2・L3 も <c>tools/Verify-GpuEncoders.ps1</c> も、ソースを
/// <b>すべて <c>format=I420</c> に固定</b>しており、これまで試した
/// エンコーダーは<b>全部 I420 を受けた</b> ── <b>検証側が偶然そろっていたので、
/// 交渉が要る経路が一度も踏まれていなかった。</b>
/// </para>
/// <para>
/// <b>ここは「そう書いてある」の検査ではない。</b> 実際に <c>BGRA</c> を流し、
/// 製品が変換を挟んで録画まで到達することを見る。
/// <see cref="EventRecorder.BuildSinkPipeline"/> の静的な検査は L1 側
/// （<c>BuildSinkPipelineTests.System_AlwaysConvertsBeforeTheEncoder</c>）にある。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class EncoderNegotiationTests(PublishedApp app, ITestOutputHelper output)
{
    [Fact]
    public void ASourceFormatTheEncoderCannotTake_IsConvertedInsteadOfFailingToLink()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1").SrcPipeline = SettingsFile.UnconvertibleFormatVideoTestSrc;

        using var instance = AppInstance.Create(app, settings);

        var log = instance.ReadActivityLog();

        // 初期化が通ること。変換が無いと候補は全部 link に失敗し、
        // 「recorder.init fail ... All N H.264 encoder candidate(s) failed」になる。
        Assert.NotEmpty(ActivityLogFile.Events(log, "recorder.init ok"));
        Assert.Empty(ActivityLogFile.Events(log, "recorder.init fail"));

        // **フォールバックで拾われたのでは意味が無い。**
        // 変換が入っていれば既定のエンコーダーが一発で通る（failedAttempts=0）。
        // ここを見ないと、たまたま BGRA を受ける候補が居る機械で緑になってしまう。
        string selected = Assert.Single(ActivityLogFile.Events(log, "gst.encoder selected"));
        output.WriteLine(selected);
        Assert.Contains(SettingsFile.DefaultEncoder, selected);
        Assert.Contains("failedAttempts=0", selected);
        Assert.Empty(ActivityLogFile.Events(log, "gst.encoder candidate-failed"));

        // 形式が通ることと、フレームが実際に流れることは別。録って確かめる。
        var start = instance.Run("start-recording-all");
        Assert.Equal(0, start.ExitCode);
        Thread.Sleep(TimeSpan.FromSeconds(3));
        var stop = instance.Run("stop-recording-all");
        Assert.Equal(0, stop.ExitCode);

        string file = Assert.Single(instance.ListRecordings());
        RecordedMp4.AssertUsable(file, instance, output);

        // **BGRA からもサムネイルが撮れること。** 撮るのはプレビュー枝で、そこには
        // エンコーダーの手前の変換を通らない BGRA がそのまま届く
        // ── 自動で通る 4 バイト系の経路はここだけである（他の E2E は I420）。
        string thumbnail = file + ".png";
        var waiting = Stopwatch.StartNew();
        while (waiting.Elapsed < ThumbnailBudget && !File.Exists(thumbnail))
            Thread.Sleep(250);

        Assert.True(File.Exists(thumbnail), "BGRA のフレームからサムネイルが書かれていない: " + thumbnail);
        output.WriteLine($"{thumbnail}: {new FileInfo(thumbnail).Length} bytes");

        // **ファイルが在ることは「撮れた」ことではない。** 4 バイト系の並びやオフセットを
        // 取り違えても PNG は書けてしまうので、独立した復号器（System.Drawing）で読み返し、
        // 大きさと「1 色ではないこと」まで見る（PNG は File.Move で現れるので途中の姿は無い）。
        using (var stream = new MemoryStream(File.ReadAllBytes(thumbnail)))
        using (var bitmap = new Bitmap(stream))
        {
            output.WriteLine($"{bitmap.Width}x{bitmap.Height} {bitmap.PixelFormat}");
            Assert.InRange(bitmap.Width, 1, 320);
            Assert.True(0 < bitmap.Height);

            var first = bitmap.GetPixel(0, 0);
            bool uniform = true;
            for (int y = 0; y < bitmap.Height && uniform; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y) != first)
                    {
                        uniform = false;
                        break;
                    }
                }
            }

            Assert.False(uniform, $"BGRA のサムネイルが一様な 1 色（{first}）になっている。");
        }
    }

    /// <summary>サムネイルが書かれるまでの上限（撮るのも書くのも録画の外側）。</summary>
    private static readonly TimeSpan ThumbnailBudget = TimeSpan.FromSeconds(20);
}
