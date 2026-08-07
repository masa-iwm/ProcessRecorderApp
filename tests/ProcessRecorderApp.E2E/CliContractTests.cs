using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// CLI の契約（終了コードと変数の読み書き）。
/// これらは常駐ワーカーを1つ立てれば全部確認できるので、1件のテストにまとめてある
/// ── ケースごとにワーカーを起こすと、確認していることの割に実行時間が桁で増える。
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class CliContractTests(PublishedApp app, ITestOutputHelper output)
{
    // 終了コードの値は製品側（ActivationCommands / SingleInstanceManager）と一致していなければ
    // ならない。製品側の定数は L1 と src/README.md の終了コード表が固定しているので、
    // ここは「外から見て実際にその値が返る」ことの確認になる。
    private const int ExitInvalidArguments = 4;
    private const int ExitVariableNotDefined = 11;
    private const int ExitRecorderNotFound = 13;
    private const int ExitRecordingNotExecutable = 14;

    [Fact]
    public void ExitCodes_MatchTheDocumentedContract()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        Assert.Equal(0, instance.Run("--help").ExitCode);
        Assert.Equal(0, instance.Run("ping").ExitCode);

        // 未知のコマンドはランチャー側で弾く（常駐ワーカーへは転送されない）。
        Assert.Equal(ExitInvalidArguments, instance.Run("no-such-command").ExitCode);

        // 未定義の変数。
        Assert.Equal(ExitVariableNotDefined, instance.Run("--get", "NoSuchKey").ExitCode);

        // 存在しないレコーダー名。
        Assert.Equal(ExitRecorderNotFound, instance.Run("start-recording", "NoSuchRecorder").ExitCode);

        // 録画していないレコーダーの停止は「現在の状態で実行できない」。
        Assert.Equal(ExitRecordingNotExecutable, instance.Run("stop-recording", "R1").ExitCode);

        // 一連の失敗が activity.log にも終了コードつきで残ること。
        var cli = ActivityLogFile.Events(instance.ReadActivityLog(), "cli");
        Assert.Contains(cli, l => l.Contains($"exitCode={ExitRecorderNotFound}"));
        Assert.Contains(cli, l => l.Contains($"exitCode={ExitRecordingNotExecutable}"));

        // ランチャー側で弾いたコマンド（--help / 未知のコマンド）は常駐ワーカーへ届かないので
        // cli 行にも出ない。これは仕様であって取りこぼしではない。
        Assert.DoesNotContain(cli, l => l.Contains("args=[--help]"));

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// <c>activate</c> はウィンドウを出す唯一のコマンドで、GUI 側の初期化
    /// （MainPage・プレビュー面）を実際に通す。GPU の無い環境ではプレビューの初期化に
    /// 失敗しうるが、その場合もログして続行する設計なので、
    /// <b>ウィンドウが開くこと自体は成功しなければならない</b>。
    /// 画面の中身の検証は L3（UIA）の担当。
    /// </summary>
    [Fact]
    public void Activate_ShowsTheWindowWithoutFaulting()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        Assert.Equal(0, instance.Run("activate").ExitCode);

        // 表示後もコマンドが通り続けること（エンジンがページに巻き込まれて死んでいない）。
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.Equal(0, instance.Run("ping").ExitCode);

        var log = instance.ReadActivityLog();
        output.WriteLine(string.Join(Environment.NewLine, log));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));
    }

    [Fact]
    public void Variables_RoundTripThroughTheCli()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        // 変数が0件のときの --get は「失敗ではない」（終了コード 0・stdout は空）。
        var empty = instance.Run("--get");
        Assert.Equal(0, empty.ExitCode);
        Assert.Equal("", empty.StdOut.Trim());

        Assert.Equal(0, instance.Run("--set", "Site=tokyo").ExitCode);
        Assert.Equal(0, instance.Run("--set", "Stage=alpha").ExitCode);

        var one = instance.Run("--get", "Site");
        Assert.Equal(0, one.ExitCode);
        Assert.Equal("tokyo", one.StdOut.Trim());

        // キー省略で全件。
        var all = instance.Run("--get");
        Assert.Equal(0, all.ExitCode);
        var lines = all.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        Assert.Contains("Site=tokyo", lines);
        Assert.Contains("Stage=alpha", lines);

        // 複数キーは --get を繰り返す（--get A B は終了コード 4。README もそう書いてある）。
        var several = instance.Run("--get", "Site", "--get", "Stage");
        Assert.Equal(0, several.ExitCode);
        Assert.Equal(2, several.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(ExitInvalidArguments, instance.Run("--get", "Site", "Stage").ExitCode);

        // 未定義キーは終了コード 11 で、標準エラーに**キー名を含む**
        // （どのキーが未定義かをバッチ側が特定できるように）。
        // 判定に使うのは ASCII のキー名だけにする ── メッセージ本体はロケール依存で、
        // 生バイトはコンソールのコードページになるため文字列比較の土台にできない。
        var missing = instance.Run("--get", "Site", "--get", "NoSuchKey", "--get", "Stage");
        Assert.Equal(ExitVariableNotDefined, missing.ExitCode);
        Assert.Contains("NoSuchKey", missing.StdErr);
        // 定義済みのぶんは通常どおり出力される（1件の失敗で全体を捨てない）。
        Assert.Equal(2, missing.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        output.WriteLine(missing.ToString());
    }

    /// <summary>
    /// <b><c>--get</c> は他の出力を消さない。</b>
    ///
    /// <para>
    /// <c>--set</c> / <c>--get</c> / <c>--persist</c> は <c>Recursive</c> なので
    /// サブコマンドと併用できる。<c>--get</c> の後処理が <c>ConsoleOutput</c> /
    /// <c>ConsoleError</c> を<b>差し替えて</b>いると、
    /// <c>status --get</c> では健全性テーブルが丸ごと消え、
    /// <c>--persist &lt;未定義&gt; --get &lt;定義済み&gt;</c> では
    /// <b>終了コード 11 だけが返って理由がどこにも出ない</b>という壊れ方をする。
    /// どちらも「黙って情報が減る」形なので、終了コードだけを見るテストでは拾えない。
    /// </para>
    /// <para>
    /// <b>終了コードは表明しない。</b> <c>status</c> はレコーダーの健全性で
    /// 0 と 15 のどちらにもなり、それはこの機械に <c>x264enc</c> が在るかどうかで変わる
    /// ── ここで見たいのは「出力が残っているか」だけである。
    /// </para>
    /// </summary>
    [Fact]
    public void Get_AppendsToTheCommandOutput_InsteadOfReplacingIt()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        Assert.Equal(0, instance.Run("--set", "Site=tokyo").ExitCode);

        // ① サブコマンドの標準出力が残ったうえで、変数の値が足されること。
        var status = instance.Run("status", "--get", "Site");
        output.WriteLine(status.ToString());
        Assert.Contains("R1", status.StdOut);        // status の行（1レコーダー＝1行）
        Assert.Contains("tokyo", status.StdOut);     // --get の出力

        // ② --persist の未定義キーの指摘が、後続の --get に消されないこと。
        //    判定に使うのは ASCII のキー名だけにする（上の missing と同じ理由）。
        var persist = instance.Run("--persist", "NoSuchVariable", "--get", "Site");
        output.WriteLine(persist.ToString());
        Assert.Equal(ExitVariableNotDefined, persist.ExitCode);
        Assert.Contains("NoSuchVariable", persist.StdErr);
        Assert.Contains("tokyo", persist.StdOut);
    }
}
