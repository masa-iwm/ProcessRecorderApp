using System.Diagnostics;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <c>stop-recording</c> が復帰した時点で MP4 が確定していること。
/// 想定用途は「<c>stop-recording X</c> の直後に <c>copy</c> するバッチ」で、
/// コマンドが返った瞬間にファイルが使える必要がある。
///
/// <para>
/// <b>このテストは十分に大きいファイルで書かなければ意味がない。</b>
/// 効いているのは <c>mp4mux faststart=true</c> で、EOS 後にファイル全体を書き直すため
/// 排出コストはファイルサイズにほぼ比例する。320x240/3秒（約 0.9MB）では
/// 排出（約 50ms）がランチャーの IPC 往復（約 500ms）に隠れてしまい、
/// <b>CLI の await を外す退行を注入しても検出できなかった</b>（実測済み）。
/// 1280x720/30fps/20Mbit・20秒（生成サイズは下限 20MB を大きく超える）にすると検出できる。
/// </para>
///
/// <para>
/// 判定は<b>共有違反</b>（書き込み側がまだファイルを開いているか）で行う。
/// <c>moov</c> の有無より早く現れるので最も鋭い。
/// </para>
/// <para>
/// <b><c>FragmentedOutput = false</c> を明示する。</b> 排出コストの実体は
/// <c>faststart=true</c> の「EOS 後にファイル全体を書き直す」処理で、既定の fMP4 では
/// それが起きない ── 既定構成で走らせると排出が IPC 往復に隠れ、
/// <c>await</c> を外す退行を<b>1 件も検出しないテスト</b>になる。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class StopSynchronicityTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>録画時間。排出コストを IPC 往復より確実に大きくするために長く取る。</summary>
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// この大きさに届かないと、排出が IPC 往復に隠れて<b>退行を検出できないテスト</b>になる。
    /// 届かない場合は「緑だが何も守っていない」状態なので、通さずに落とす。
    /// </summary>
    private const long MinimumFileSizeBytes = 20L * 1024 * 1024;

    [Fact]
    public void StopRecording_ReturnsOnlyAfterTheFileIsFinalized()
    {
        var settings = new SettingsFile();
        settings.FragmentedOutput = false;
        var recorder = settings.AddRecorder("R1").AsLarge();
        // 事前バッファはこのテストの対象ではないので短くする（20Mbit を数秒ぶん抱えないため）。
        recorder.BufferDuration = 1000;

        using var instance = AppInstance.Create(app, settings);

        var start = instance.Run("start-recording", "R1");
        Assert.Equal(0, start.ExitCode);
        string file = start.FirstOutputLine;
        Assert.True(Path.IsPathRooted(file), start.ToString());

        Thread.Sleep(RecordingWindow);

        var stopwatch = Stopwatch.StartNew();
        var stop = instance.Run("stop-recording", "R1");
        stopwatch.Stop();
        Assert.Equal(0, stop.ExitCode);

        // 復帰**直後**に見る。ここに待ちを入れると、このテストは何も検出しなくなる。
        bool closed = Mp4File.IsClosedByWriter(file);
        var info = new FileInfo(file);
        output.WriteLine($"stop-recording: {stopwatch.ElapsedMilliseconds}ms / {info.Length:N0} bytes / closed={closed}");

        // 本題の判定を先に置く。サイズの下限を先に見ると、退行を注入したときに
        // 「録画が小さすぎる」という誤った診断が出る（排出の途中なのでまだ小さい）。
        Assert.True(closed,
            "stop-recording から復帰した時点で書き込み側がまだファイルを開いています" +
            Environment.NewLine + instance.DiagnosticDump());

        Assert.True(info.Length >= MinimumFileSizeBytes,
            $"録画が小さすぎて排出コストが IPC に隠れます: {info.Length:N0} bytes " +
            $"（{MinimumFileSizeBytes:N0} 以上必要）。この状態では退行注入を検出できません。" +
            Environment.NewLine + instance.DiagnosticDump());

        RecordedMp4.AssertUsable(file, instance, output);

        var log = instance.ReadActivityLog();
        var stops = ActivityLogFile.Events(log, "recording.stop");
        // Assert.All は空集合で緑になる ── 停止の記録が別イベント名（recording.stop slow /
        // recording.stop empty）へ移る退行では stops が 0 件になり、ok の表明ごと素通りする。
        // 「停止の記録が 1 件はあること」を先に固定する。
        Assert.NotEmpty(stops);
        Assert.All(stops, l => Assert.Contains("result=ok", l));
        Assert.Empty(ActivityLogFile.Events(log, "recording.stop timeout"));
        Assert.Empty(ActivityLogFile.Events(log, "recording.stop error"));

        // 停止が受け付けられた時点で IsRecording は同期的に倒れるので、二重停止は弾かれる。
        Assert.NotEqual(0, instance.Run("stop-recording", "R1").ExitCode);
    }
}
