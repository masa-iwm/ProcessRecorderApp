using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <c>status</c> サブコマンド。レコーダーごとの健全性を外から観測できること。
///
/// <para>
/// <b>判定に使うのは ASCII の列だけ</b> ── 名前・<c>True</c>/<c>False</c>・パス・終了コード。
/// 標準エラーのメッセージはロケール依存で、バイト列はコンソールのコードページになるため
/// 文字列比較の土台にできない（実際に一度誤読しかけた）。
/// そこは「レコーダー名が含まれること」だけを見る。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class StatusCommandTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>製品側 <c>ActivationCommands.ExitCode_RecorderUnhealthy</c> と一致していること。</summary>
    private const int ExitRecorderUnhealthy = 15;

    /// <summary>1行の列数（名前 / 初期化済み / 録画中 / 直近のファイル / 直近の障害）。</summary>
    private const int ColumnCount = 5;

    private sealed record StatusRow(string Name, bool Initialized, bool Recording, string LastFilename, string LastError);

    /// <summary>
    /// <c>status</c> の標準出力を解析する。<b>列数そのものを表明する</b> ──
    /// 列が増減したら「たまたま欲しい位置に値があった」ではなく、ここで落ちてほしい。
    /// </summary>
    private static IReadOnlyList<StatusRow> Parse(CliResult result)
    {
        var rows = new List<StatusRow>();
        foreach (string raw in result.StdOut.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            string[] cells = line.Split('\t');
            Assert.True(cells.Length == ColumnCount,
                $"status の行は {ColumnCount} 列でなければなりません: `{line}`（{cells.Length} 列）");

            rows.Add(new StatusRow(
                cells[0],
                ParseBoolean(cells[1], line),
                ParseBoolean(cells[2], line),
                cells[3],
                cells[4]));
        }
        return rows;
    }

    /// <summary>
    /// 真偽値の列は <c>bool.ToString()</c>（<c>True</c>/<c>False</c>）でカルチャに依存しない。
    /// ここで厳密に見ておかないと、書式が変わったときに「解析できないので false」と
    /// 読み替えられて静かに緑になる。
    /// </summary>
    private static bool ParseBoolean(string cell, string line)
    {
        if (cell == bool.TrueString)
            return true;
        if (cell == bool.FalseString)
            return false;
        Assert.Fail($"真偽値の列が `{bool.TrueString}`/`{bool.FalseString}` ではありません: `{cell}`（行: `{line}`）");
        return false;
    }

    /// <summary>
    /// 健全なときは終了コード 0 で、<b>録画の開始と終了が列に実際に映る</b>こと。
    /// 「録画中」の列がモデルに繋がっていなければ、開始しても <c>False</c> のままになる。
    /// </summary>
    [Fact]
    public void Status_TracksEveryRecorderThroughARecordingCycle()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");
        settings.AddRecorder("R2");

        using var instance = AppInstance.Create(app, settings);

        // --- 録画前 ---
        var idle = instance.Run("status");
        output.WriteLine(idle.ToString());
        Assert.Equal(0, idle.ExitCode);
        Assert.Equal("", idle.StdErr.Trim());

        var before = Parse(idle);
        Assert.Equal(["R1", "R2"], before.Select(r => r.Name));
        Assert.All(before, r => Assert.True(r.Initialized, $"'{r.Name}' が初期化されていません: {r.LastError}"));
        Assert.All(before, r => Assert.False(r.Recording));
        Assert.All(before, r => Assert.Equal("", r.LastError));
        // まだ一度も録画していないので、直近のファイルは無い。
        Assert.All(before, r => Assert.Equal("", r.LastFilename));

        // --- 録画中 ---
        var start = instance.Run("start-recording-all");
        Assert.Equal(0, start.ExitCode);
        // start-recording-all の出力は「名前 TAB 解決済みファイル名」。
        var startedFiles = start.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\t'))
            .Where(c => c.Length == 2)
            .ToDictionary(c => c[0], c => c[1], StringComparer.Ordinal);

        var recording = instance.Run("status");
        output.WriteLine(recording.ToString());
        Assert.Equal(0, recording.ExitCode);

        var during = Parse(recording);
        Assert.All(during, r => Assert.True(r.Recording, $"'{r.Name}' が録画中として報告されていません"));
        // 同じファイルを指していること ── 「録画中」だけなら定数 True でも通ってしまう。
        foreach (var row in during)
            Assert.Equal(startedFiles[row.Name], row.LastFilename);

        Thread.Sleep(TimeSpan.FromSeconds(2));

        // --- 停止後 ---
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        var stopped = instance.Run("status");
        output.WriteLine(stopped.ToString());
        Assert.Equal(0, stopped.ExitCode);

        var after = Parse(stopped);
        Assert.All(after, r => Assert.False(r.Recording, $"'{r.Name}' が停止後も録画中として報告されています"));
        Assert.All(after, r => Assert.True(r.Initialized));
        // 停止しても「直近のファイル」は残る（次の録画開始まで消えない）。
        foreach (var row in after)
            Assert.Equal(startedFiles[row.Name], row.LastFilename);

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// 初期化に失敗したレコーダーがあると終了コード 15 を返し、
    /// <b>どれが不健全なのかが標準エラーから分かる</b>こと。
    ///
    /// <para>
    /// 健全な方が同じ実行で <c>True</c> のまま報告されることも併せて見る ──
    /// 「1つでも壊れていたら全部 False」でも終了コードだけの表明は通ってしまう。
    /// </para>
    /// </summary>
    [Fact]
    public void Status_ReportsWhichRecorderIsUnhealthy_AndReturns15()
    {
        // レコーダー名は**健全性のメッセージに絶対に現れない語**にする。
        // `Healthy` / `Broken` のような語だと、en-US の `Cli_RecorderError`
        // （"Recorder '{0}' is not healthy: {1}"）と1文字違いで衝突し、
        // 訳文を書き換えただけで「名前が含まれない」の表明が偽の赤になる。
        var settings = new SettingsFile();
        settings.AddRecorder("Alpha");
        var broken = settings.AddRecorder("Beta");

        // 存在しないファイルを filesrc で開く。src パッドの caps が ANY なので
        // gst_parse_launch は通り、**状態遷移で**失敗する ── つまり要素が生成された後で
        // 初期化が失敗する（満たせない caps や存在しない要素名だとパース時点で落ちてしまい、
        // 「初期化が途中で失敗した」状態に届かない）。
        // gst_parse_launch ではバックスラッシュがエスケープ文字なのでパスはスラッシュで書く。
        broken.SrcPipeline = "filesrc location=C:/no-such-directory-for-e2e/x.raw ! videoconvert";

        using var instance = AppInstance.Create(app, settings);

        var result = instance.Run("status");
        output.WriteLine(result.ToString());

        Assert.Equal(ExitRecorderUnhealthy, result.ExitCode);

        var rows = Parse(result);
        Assert.Equal(["Alpha", "Beta"], rows.Select(r => r.Name));

        var healthy = rows.Single(r => r.Name == "Alpha");
        Assert.True(healthy.Initialized, $"健全なはずのレコーダーが不健全と報告されました: {healthy.LastError}");
        Assert.Equal("", healthy.LastError);

        var failed = rows.Single(r => r.Name == "Beta");
        Assert.False(failed.Initialized, "初期化に失敗したはずのレコーダーが初期化済みと報告されました");
        // 障害の理由文は1行に潰されている（1レコーダー＝1行を崩さない）。
        Assert.DoesNotContain('\n', failed.LastError);

        // どれが不健全なのかが標準エラーに出ること。判定は ASCII の名前だけ
        // ── メッセージ本体はロケール依存。
        Assert.Contains("Beta", result.StdErr, StringComparison.Ordinal);
        // 健全な方は出ない（「不健全なものがある」だけを出す実装だと、
        // どれを直せばよいのか分からないままになる）。
        Assert.DoesNotContain("Alpha", result.StdErr, StringComparison.Ordinal);

        // 初期化の失敗は activity.log にも残っている。
        var log = instance.ReadActivityLog();
        Assert.NotEmpty(ActivityLogFile.Events(log, "recorder.init fail"));

        // status は観測のためのコマンドで、既存のスクリプトの挙動は変えない
        // ── 健全な方は普通に録画を開始できる。
        Assert.Equal(0, instance.Run("start-recording", "Alpha").ExitCode);
    }
}
