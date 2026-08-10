using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// settings.json に随伴する JSON Schema（<c>settings.schema.json</c>）。
///
/// <para>
/// <b>L1 では検証できない。</b> スキーマを決めているのは <c>AppSettings</c> と
/// <c>AppSettingsJsonContext</c> で、どちらも WinUI アプリプロジェクトにあり
/// L1 のテストプロジェクトは参照していない（参照させると WinUI がテストホストに載る）。
/// 実際に書かれたファイルを見られるのはこの層だけ ── 基底クラスの機構そのものは
/// L1 の <c>JsonSettingsBaseTests</c> が押さえている。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class SettingsSchemaTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>デバウンス保存（約1秒）が書き切るまでの待ち。</summary>
    private static readonly TimeSpan DebounceMargin = TimeSpan.FromSeconds(3);

    /// <summary>リポジトリに登録してあるスキーマ（配布物・エディタ・レビューが読む正本）。</summary>
    private static string CommittedSchemaPath
        => Path.Combine(RepositoryLayout.Root, "docs", "settings.schema.json");

    /// <summary>
    /// 保存を実際に起こし、書かれた settings.json と隣のスキーマを返す。
    ///
    /// <para>
    /// <b>「保存が走ったこと」を確かめてから中身を見る。</b> そうしないと、保存が
    /// 一度も走らなくても（＝ハーネスが置いた初期ファイルを見ているだけでも）緑になる。
    /// </para>
    /// </summary>
    private (string Settings, string Schema) SaveAndRead()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        Assert.Equal(0, instance.Run("--set", "Kept=on-disk").ExitCode);
        Assert.Equal(0, instance.Run("--persist", "Kept").ExitCode);

        Thread.Sleep(DebounceMargin);
        instance.KillWorkers();

        string saved = File.ReadAllText(instance.SettingsPath);
        Assert.Contains("on-disk", saved, StringComparison.Ordinal);   // 保存が走った証拠

        string schemaPath = Path.Combine(
            Path.GetDirectoryName(instance.SettingsPath)!, "settings.schema.json");
        Assert.True(File.Exists(schemaPath),
            $"settings.json の隣にスキーマが書かれていません: {schemaPath}");

        return (saved, File.ReadAllText(schemaPath));
    }

    /// <summary>
    /// 保存されたスキーマが、<b>同じ保存で書かれた settings.json と同じキー</b>を
    /// 過不足なく持つこと。
    ///
    /// <para>
    /// これが随伴スキーマの唯一の存在理由 ── ずれた瞬間に、エディタが
    /// <b>正しい設定ファイルへ赤線を引く</b>ようになる。名前付け（PascalCase か
    /// camelCase か）や解決器の取り違えは、どちらのファイルも単体では正しく見えるので
    /// 突き合わせでしか捕まらない。
    /// </para>
    /// <para>
    /// <b>AOT 発行物に対して流したときにこそ効く</b>（<c>PersistenceTests</c> の書式検査と
    /// 同じ理由）── スキーマは <c>Save</c> と同じソース生成の型情報から作っているので、
    /// 「AOT でソース生成の解決器から外れる」種類の壊れ方をここで捕まえられる。
    /// </para>
    /// </summary>
    [Fact]
    public void Schema_DescribesExactlyTheKeysThatWereSaved()
    {
        var (saved, schema) = SaveAndRead();
        output.WriteLine(schema);

        using var savedDocument = JsonDocument.Parse(saved);
        using var schemaDocument = JsonDocument.Parse(schema);

        string[] written = [.. savedDocument.RootElement.EnumerateObject()
            .Select(p => p.Name).Order(StringComparer.Ordinal)];
        string[] described = [.. schemaDocument.RootElement.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).Order(StringComparer.Ordinal)];

        // 走査が壊れて 0 件になると「差が無い」で緑になる
        Assert.NotEmpty(written);
        Assert.Equal(described, written);
    }

    /// <summary>
    /// settings.json の <c>$schema</c> が、隣に書かれたスキーマを<b>相対参照で</b>指すこと。
    /// 絶対パスや別名になると、設定ディレクトリごと移した先でエディタが解決できなくなる。
    /// </summary>
    [Fact]
    public void Settings_PointAtTheSchemaFileNextToThem()
    {
        var (saved, _) = SaveAndRead();
        output.WriteLine(saved);

        using var document = JsonDocument.Parse(saved);
        Assert.Equal("settings.schema.json", document.RootElement.GetProperty("$schema").GetString());
    }

    /// <summary>
    /// 列挙は<b>名前で</b>書かれること（<c>PaneDisplayMode</c> と レコーダーの <c>Type</c>）。
    /// 数値だと手で開いたときに意味が読めず、並びを変えた瞬間に既存ファイルの意味が黙って変わる。
    /// </summary>
    [Fact]
    public void Enums_AreWrittenByName()
    {
        var (saved, schema) = SaveAndRead();
        output.WriteLine(saved);

        using var document = JsonDocument.Parse(saved);
        Assert.Equal("Top", document.RootElement.GetProperty("PaneDisplayMode").GetString());
        Assert.Equal("System",
            document.RootElement.GetProperty("Recorders")[0].GetProperty("Type").GetString());

        // スキーマ側も名前の集合として書かれていること（数値のままなら "type": "integer" になる）
        using var schemaDocument = JsonDocument.Parse(schema);
        var paneMode = schemaDocument.RootElement.GetProperty("properties").GetProperty("PaneDisplayMode");
        Assert.True(paneMode.TryGetProperty("enum", out var values),
            $"PaneDisplayMode が名前の集合になっていません: {paneMode}");
        Assert.Contains("Top", values.EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>
    /// <b>数値で書かれた列挙も読めること。</b> 名前で書くようにする前の settings.json や、
    /// 手で数値を入れたファイルが<b>丸ごと既定値へ倒れない</b>ことを担保する
    /// （倒れると記録は残るが、利用者から見れば設定が消えたのと同じ）。
    /// 読めたことは「名前へ書き直されて保存される」ことで確かめる。
    /// </summary>
    [Fact]
    public void Enums_WrittenAsNumbers_AreStillRead()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, startWorker: false);

        // ハーネスが書いたファイルの列挙だけを数値へ落とす（古い形のファイルを模す）。
        var root = JsonNode.Parse(File.ReadAllText(instance.SettingsPath))!.AsObject();
        root["PaneDisplayMode"] = 0;                    // NavigationViewPaneDisplayMode.Auto
        root["Recorders"]![0]!["Type"] = 0;             // EventRecordingType.System
        File.WriteAllText(instance.SettingsPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        instance.StartWorkerAndWaitUntilReady(AppInstance.DefaultReadyBudget);

        Assert.Equal(0, instance.Run("--set", "Kept=on-disk").ExitCode);
        Assert.Equal(0, instance.Run("--persist", "Kept").ExitCode);
        Thread.Sleep(DebounceMargin);
        instance.KillWorkers();

        string saved = File.ReadAllText(instance.SettingsPath);
        output.WriteLine(saved);
        Assert.Contains("on-disk", saved, StringComparison.Ordinal);   // 保存が走った証拠

        using var document = JsonDocument.Parse(saved);
        // 既定値（Top）へ倒れていない＝数値がそのまま解釈された
        Assert.Equal("Auto", document.RootElement.GetProperty("PaneDisplayMode").GetString());
        Assert.Equal("System",
            document.RootElement.GetProperty("Recorders")[0].GetProperty("Type").GetString());
    }

    /// <summary>
    /// リポジトリに登録してあるスキーマが、アプリが実際に書くものと一致すること。
    ///
    /// <para>
    /// <b>登録しておく理由</b>: 設定の形の変更が<b>差分としてレビューに乗る</b>ようになる
    /// （プロパティの増減・列挙の値・型の変化はここに出る）。
    /// アプリを一度も起動していない環境からでもスキーマを参照できる、という副次的な利点もある。
    /// </para>
    /// <para>
    /// <b>改行は正規化して比べる。</b> 生成側の改行はプラットフォーム既定になりうるうえ、
    /// リポジトリ側は <c>core.autocrlf</c> の設定次第でチェックアウト時に書き換わる
    /// ── そのまま比べると<b>手元だけ緑・CI だけ赤</b>になる。
    /// </para>
    /// </summary>
    [Fact]
    public void CommittedSchema_MatchesWhatTheApplicationWrites()
    {
        var (_, schema) = SaveAndRead();

        Assert.True(File.Exists(CommittedSchemaPath),
            $"リポジトリにスキーマがありません: {CommittedSchemaPath}");

        string expected = Normalize(File.ReadAllText(CommittedSchemaPath));
        string actual = Normalize(schema);

        Assert.True(expected == actual,
            $"docs/settings.schema.json が古くなっています。{Environment.NewLine}"
            + $"アプリを一度起動して設定を保存し、書かれた settings.schema.json を"
            + $" docs/settings.schema.json へ上書きしてください。{Environment.NewLine}"
            + $"--- アプリが書いたもの ---{Environment.NewLine}{schema}");

        static string Normalize(string text) => text.Replace("\r\n", "\n");
    }
}
