using System.Text.Json;
using System.Text.Json.Serialization;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="JsonSettingsBase{TSelf}"/> の読み書き契約。
///
/// 実アプリの <c>AppSettings</c> は WinUI3 プロジェクト側にあり L1 からは参照できないため、
/// 同じ基底クラス上に最小の派生型（<see cref="SampleSettings"/>）を置いて検証する。
/// 検証する契約は AppSettings と同一:
///   - ラウンドトリップ（Save → LoadOrCreate で全プロパティが復元される）
///   - ファイルが無い／壊れている場合は既定値へフォールバックし、例外を出さない
///   - 未知のプロパティは <c>[JsonExtensionData]</c> に保持され、保存し直しても消えない
///     （設定ファイルのダウングレード耐性。DataVersion 運用の前提）
///   - <b>既定値へ倒れたことは必ず呼び出し側へ報告される</b>（黙って設定を捨てない）。
///     中身が壊れていたファイルは退避し、読めなかっただけのファイルは退避しない
///   - <b>保存は一時ファイル経由で原子的に差し替える</b> ── このアプリはデバウンス保存中に
///     強制終了されうるので、直接書くと切れた JSON が残って設定が丸ごと消える
/// </summary>
public class JsonSettingsBaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ProcessRecorderApp.Tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ---- ラウンドトリップ ----

    [Fact]
    public void Save_ThenLoad_RestoresEveryProperty()
    {
        var saved = SampleSettings.CreateDefault();
        saved.Text = "value with space";
        saved.Number = 42;
        saved.Flag = false;
        saved.Items.Add("a");
        saved.Items.Add("b");
        saved.Save(FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        var loaded = SampleSettings.Load(FilePath);

        Assert.Equal("value with space", loaded.Text);
        Assert.Equal(42, loaded.Number);
        Assert.False(loaded.Flag);
        Assert.Equal(new[] { "a", "b" }, loaded.Items);
    }

    [Fact]
    public void Save_CreatesTheDirectoryIfItDoesNotExist()
    {
        string nested = Path.Combine(_dir, "a", "b", "settings.json");

        SampleSettings.CreateDefault().Save(nested, SampleSettingsJsonContext.Default.SampleSettings);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Save_ClearsIsFirstRun()
    {
        var settings = SampleSettings.CreateDefault();
        Assert.True(settings.IsFirstRun);

        settings.Save(FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        Assert.False(settings.IsFirstRun);
        Assert.False(SampleSettings.Load(FilePath).IsFirstRun);
    }

    // ---- フォールバック ----

    [Fact]
    public void Load_MissingFile_ReturnsTheDefaultsAndReportsFirstRun()
    {
        var loaded = SampleSettings.Load(FilePath);

        Assert.True(loaded.IsFirstRun);
        Assert.Equal("default", loaded.Text);
        Assert.Equal(1, loaded.Number);
        Assert.True(loaded.OnLoadedWasCalled);
    }

    [Fact]
    public void Load_CorruptedJson_FallsBackToTheDefaultsWithoutThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not json");

        var loaded = SampleSettings.Load(FilePath);

        Assert.Equal("default", loaded.Text);
        Assert.True(loaded.OnLoadedWasCalled);
    }

    /// <summary>
    /// <b>読めなかったファイルは上書きされる前に退避される。</b>
    ///
    /// <para>
    /// 既定値へ倒れた直後は、次の <c>Save</c> がその場に新しい既定値を書く。
    /// 退避しないと、利用者が手で復旧する材料（元の JSON）も同時に消える
    /// ── 壊れ方が「レコーダー定義もトリガ定義も丸ごと消えた」なので、
    /// <b>捨ててよいファイルではない</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void Load_CorruptedJson_QuarantinesTheFileInsteadOfLeavingItToBeOverwritten()
    {
        Directory.CreateDirectory(_dir);
        const string original = """{ "Number": 7, "Text": "not closed""";
        File.WriteAllText(FilePath, original);

        var failures = new List<string>();
        var loaded = SampleSettings.Load(FilePath, failures.Add);

        Assert.Equal("default", loaded.Text);
        Assert.NotEmpty(failures);

        string quarantined = FilePath + JsonSettingsBase<SampleSettings>.UnreadableFileSuffix;
        Assert.True(File.Exists(quarantined), $"退避されていない: {quarantined}");
        Assert.Equal(original, File.ReadAllText(quarantined));
        Assert.False(File.Exists(FilePath), "壊れたファイルが元の場所に残っている");
    }

    /// <summary>
    /// <b>読み込み自体の失敗（ロック・権限）では退避しない。</b>
    /// 中身は正しいかもしれないので、退けると<b>正しいファイルを失わせる</b>。
    /// </summary>
    [Fact]
    public void Load_UnreadableFile_IsReportedButNotQuarantined()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "Number": 7 }""");

        var failures = new List<string>();
        SampleSettings loaded;
        // 排他で開いたまま読ませる（File.ReadAllText が IOException になる）。
        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            loaded = SampleSettings.Load(FilePath, failures.Add);
        }

        Assert.Equal(1, loaded.Number); // 既定値へ倒れている
        Assert.NotEmpty(failures);
        Assert.False(
            File.Exists(FilePath + JsonSettingsBase<SampleSettings>.UnreadableFileSuffix),
            "読めなかっただけのファイルを退避してはいけない");
        Assert.True(File.Exists(FilePath), "元のファイルが消えている");
    }

    [Fact]
    public void Load_JsonNullLiteral_IsReportedAndQuarantined()
    {
        // 中身が JSON リテラルの null は正当な JSON なので JsonException にならないが、
        // 「読める設定が入っていない」点では切り詰められた空ファイルと同じ。
        // この形だけ無記録で既定値へ倒れると、次の Save が証拠ごと上書きする。
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "null");

        var failures = new List<string>();
        var loaded = SampleSettings.Load(FilePath, failures.Add);

        Assert.Equal(1, loaded.Number);   // 既定値へ倒れている
        Assert.NotEmpty(failures);
        Assert.True(
            File.Exists(FilePath + JsonSettingsBase<SampleSettings>.UnreadableFileSuffix),
            "壊れたファイルが退避されていない");
    }

    [Fact]
    public void Save_ReportsAFailureInsteadOfThrowing()
    {
        // 一時ファイルの位置を同名のディレクトリで塞いで、書き込みを確実に失敗させる。
        // 投げてしまうと、無防備な呼び出し経路（終了時の AppWindow.Destroying・
        // デバウンス保存の DispatcherQueue コールバック）で後続の処理を道連れにする
        // ── 特に終了経路では録画エンジンの破棄（moov の確定）が飛ぶ。
        Directory.CreateDirectory(FilePath + JsonSettingsBase<SampleSettings>.TemporaryFileSuffix);

        string? reported = null;
        SampleSettings.CreateDefault().Save(
            FilePath, SampleSettingsJsonContext.Default.SampleSettings, detail => reported = detail);

        Assert.NotNull(reported);
        Assert.Contains(FilePath, reported);
        Assert.False(File.Exists(FilePath));
    }

    /// <summary>
    /// <b>保存は一時ファイル経由で、書き終えたら残さない。</b>
    /// 残っていると、次回の起動でどちらが正本か分からなくなる。
    /// </summary>
    [Fact]
    public void Save_GoesThroughATemporaryFileAndLeavesNoneBehind()
    {
        var settings = SampleSettings.CreateDefault();
        settings.Number = 3;
        settings.Save(FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        Assert.True(File.Exists(FilePath));
        Assert.False(
            File.Exists(FilePath + JsonSettingsBase<SampleSettings>.TemporaryFileSuffix),
            "一時ファイルが残っている");
        Assert.Equal(3, SampleSettings.Load(FilePath).Number);
    }

    /// <summary>
    /// <b>書きかけの一時ファイルが取り残されていても、本体は前回の内容のまま読める。</b>
    /// これが「原子的に差し替える」ことの外から見える効果そのもの
    /// ── 直接書いていた頃は、この状況で本体が切れた JSON になっていた。
    /// </summary>
    [Fact]
    public void Load_IgnoresALeftOverTemporaryFile()
    {
        var settings = SampleSettings.CreateDefault();
        settings.Number = 9;
        settings.Save(FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        // 保存の途中でプロセスが消えた状態を模す（一時ファイルだけが壊れて残る）。
        File.WriteAllText(FilePath + JsonSettingsBase<SampleSettings>.TemporaryFileSuffix, "{ broken");

        Assert.Equal(9, SampleSettings.Load(FilePath).Number);
    }

    [Fact]
    public void Load_EmptyFile_FallsBackToTheDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "");

        Assert.Equal("default", SampleSettings.Load(FilePath).Text);
    }

    [Fact]
    public void Load_JsonNullLiteral_FallsBackToTheDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "null");

        Assert.Equal("default", SampleSettings.Load(FilePath).Text);
    }

    [Fact]
    public void Load_PartialJson_KeepsTheDefaultsForTheMissingProperties()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "Number": 7 }""");

        var loaded = SampleSettings.Load(FilePath);

        Assert.Equal(7, loaded.Number);
        Assert.Equal("default", loaded.Text);
    }

    // ---- 未知プロパティの保持 ----

    [Fact]
    public void Load_UnknownProperty_IsCapturedAsExtensionData()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "Number": 7, "FutureProperty": "keep me" }""");

        var loaded = SampleSettings.Load(FilePath);

        Assert.Equal("keep me", loaded.ExtensionData["FutureProperty"].GetString());
    }

    [Fact]
    public void Save_PreservesUnknownPropertiesReadFromAnOlderOrNewerFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "Number": 7, "FutureProperty": "keep me" }""");

        var loaded = SampleSettings.Load(FilePath);
        loaded.Save(FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        // 保存し直しても、知らないプロパティが落ちていないこと
        using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal("keep me", document.RootElement.GetProperty("FutureProperty").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("Number").GetInt32());
    }

    // ---- JSON Schema の随伴 ----

    /// <summary>
    /// スキーマは<b>本体の拡張子を置き換えた名前で隣に置く</b>（<c>settings.json</c> なら
    /// <c>settings.schema.json</c>）。<c>settings.json.schema.json</c> のように積み増すと、
    /// 設定本体に書く <c>$schema</c> の相対参照と食い違う。
    /// </summary>
    [Fact]
    public void SchemaFilePath_ReplacesTheExtensionOfTheSettingsFile()
    {
        Assert.Equal(
            Path.Combine(_dir, "settings.schema.json"),
            JsonSettingsBase<SampleSettings>.GetSchemaFilePath(FilePath));
    }

    [Fact]
    public void SaveSchema_WritesTheSchemaNextToTheSettingsFile()
    {
        JsonSettingsBase<SampleSettings>.SaveSchema(
            FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        string schemaPath = JsonSettingsBase<SampleSettings>.GetSchemaFilePath(FilePath);
        Assert.True(File.Exists(schemaPath), $"スキーマが書かれていない: {schemaPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        Assert.Equal(
            JsonSettingsBase<SampleSettings>.SchemaDialect,
            document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal(nameof(SampleSettings), document.RootElement.GetProperty("title").GetString());
        Assert.Equal("object", document.RootElement.GetProperty("type").GetString());
    }

    /// <summary>
    /// <b>スキーマが、実際に書かれた設定ファイルと同じキーを持つこと。</b>
    ///
    /// <para>
    /// これが随伴スキーマの唯一の存在理由 ── ずれた瞬間に、エディタが
    /// <b>正しい設定ファイルへ赤線を引く</b>ようになる。名前付け（PascalCase か
    /// camelCase か）や解決器の取り違えは、どちらのファイルも単体では正しく見えるので、
    /// 突き合わせでしか捕まらない。
    /// </para>
    /// </summary>
    [Fact]
    public void SavedSchema_DescribesExactlyTheKeysThatSaveWrites()
    {
        var settings = SampleSettings.CreateDefault();
        settings.Items.Add("a");
        settings.Save(FilePath, SampleSettingsJsonContext.Default.SampleSettings);
        JsonSettingsBase<SampleSettings>.SaveSchema(
            FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        using var saved = JsonDocument.Parse(File.ReadAllText(FilePath));
        using var schema = JsonDocument.Parse(File.ReadAllText(
            JsonSettingsBase<SampleSettings>.GetSchemaFilePath(FilePath)));

        var written = saved.RootElement.EnumerateObject()
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        var described = schema.RootElement.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        // 走査が壊れて 0 件になると「差が無い」で緑になる
        Assert.NotEmpty(written);
        Assert.Equal(described, written);
    }

    /// <summary>
    /// <b>中身が同じなら書き直さない。</b> スキーマはビルドごとの定数なのに、保存は
    /// 約1秒のデバウンスで何度も走る ── 毎回書くと、利用者が開いている設定ディレクトリで
    /// ファイルの更新時刻だけが動き続ける。
    /// </summary>
    [Fact]
    public void SaveSchema_DoesNotRewriteAnIdenticalFile()
    {
        string schemaPath = JsonSettingsBase<SampleSettings>.GetSchemaFilePath(FilePath);
        JsonSettingsBase<SampleSettings>.SaveSchema(
            FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        var marker = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(schemaPath, marker);

        JsonSettingsBase<SampleSettings>.SaveSchema(
            FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        Assert.Equal(marker, File.GetLastWriteTimeUtc(schemaPath));
    }

    /// <summary>
    /// 中身が違えば書き直す（上の「書かない」が「一度書いたら二度と直さない」に
    /// 退行していないこと）。
    /// </summary>
    [Fact]
    public void SaveSchema_RewritesAStaleFile()
    {
        string schemaPath = JsonSettingsBase<SampleSettings>.GetSchemaFilePath(FilePath);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(schemaPath, "{ \"stale\": true }");

        JsonSettingsBase<SampleSettings>.SaveSchema(
            FilePath, SampleSettingsJsonContext.Default.SampleSettings);

        Assert.Contains("\"title\"", File.ReadAllText(schemaPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>スキーマが書けなくても投げない。</b> スキーマは設定本体の付随物であり、
    /// これで保存や終了処理を巻き添えにしてはならない（<c>Save</c> と同じ判断）。
    /// 書けなかったことは呼び出し側へ通知する。
    /// </summary>
    [Fact]
    public void SaveSchema_ReportsFailureInsteadOfThrowing()
    {
        string schemaPath = JsonSettingsBase<SampleSettings>.GetSchemaFilePath(FilePath);
        // 差し替え先をディレクトリにして File.Move を確実に失敗させる
        Directory.CreateDirectory(schemaPath);

        string? reported = null;
        JsonSettingsBase<SampleSettings>.SaveSchema(
            FilePath, SampleSettingsJsonContext.Default.SampleSettings, d => reported = d);

        Assert.NotNull(reported);
        Assert.Contains(schemaPath, reported);
    }

    // ---- 既定設定の「種」（実行ファイルの隣の settings.json） ----
    //
    // 種は「保存先に設定が無いときだけ読む初期値」であって、利用者の設定を置き換える手段ではない。
    // 4 つの分岐（本体あり／本体なし＋種あり／本体なし＋種が壊れている／どちらも無い）を
    // 全部縛る ── この機能は L2 では検証しない（発行物は全 E2E で共有するフィクスチャなので、
    // 実行ファイルの隣に置いたファイルを消し忘れると release.yml が作る zip にまで混入する）。

    private string SeedPath => Path.Combine(_dir, "seed", "settings.json");

    private void WriteSeed(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SeedPath)!);
        File.WriteAllText(SeedPath, json);
    }

    [Fact]
    public void Load_WithNoFileButASeed_ReadsTheSeedAndReportsIt()
    {
        WriteSeed("""{"Text":"from seed","Number":7}""");

        string? seedUsed = null;
        var loaded = SampleSettings.LoadWithSeed(FilePath, SeedPath, onSeedUsed: p => seedUsed = p);

        Assert.Equal("from seed", loaded.Text);
        Assert.Equal(7, loaded.Number);
        Assert.Equal(SeedPath, seedUsed);

        // 種は「初回の既定値」であって「初回そのもの」ではない ── これを true のまま
        // 返すと、初回だけ働く OnLoaded の処理が種の内容の上に重ねて走る。
        Assert.False(loaded.IsFirstRun);

        // 読むだけ。複写しない（本体は最初の保存で生まれる）。
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Load_WithAnExistingFile_IgnoresTheSeedEntirely()
    {
        var saved = SampleSettings.CreateDefault();
        saved.Text = "from the user";
        saved.Save(FilePath, SampleSettingsJsonContext.Default.SampleSettings);
        WriteSeed("""{"Text":"from seed"}""");

        string? seedUsed = null;
        var loaded = SampleSettings.LoadWithSeed(FilePath, SeedPath, onSeedUsed: p => seedUsed = p);

        // 利用者が育てた設定を同梱物で黙って置き換えないこと。
        Assert.Equal("from the user", loaded.Text);
        Assert.Null(seedUsed);
    }

    [Fact]
    public void Load_WithABrokenFileAndASeed_FallsBackToTheDefaults_NotToTheSeed()
    {
        // **本体が「壊れている」は「本体が無い」ではない。** ここで種へ倒すと、
        // 一時的に読めなかっただけの利用者の設定が同梱物で置き換わる。
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not json");
        WriteSeed("""{"Text":"from seed"}""");

        string? seedUsed = null;
        var loaded = SampleSettings.LoadWithSeed(FilePath, SeedPath, onSeedUsed: p => seedUsed = p);

        Assert.Equal("default", loaded.Text);
        Assert.Null(seedUsed);
        // 本体は壊れていたので退避される（従来どおり）。
        Assert.True(File.Exists(FilePath + JsonSettingsBase<SampleSettings>.UnreadableFileSuffix));
    }

    [Fact]
    public void Load_WithABrokenSeed_ReportsItAndNeverQuarantinesTheSeed()
    {
        WriteSeed("{ this is not json");

        var failures = new List<string>();
        string? seedUsed = null;
        var loaded = SampleSettings.LoadWithSeed(
            FilePath, SeedPath, onLoadFailure: failures.Add, onSeedUsed: p => seedUsed = p);

        Assert.Equal("default", loaded.Text);
        Assert.Null(seedUsed);
        Assert.Contains(failures, f => f.Contains(SeedPath, StringComparison.Ordinal));

        // **種は退避しない。** 退避は File.Move であり、実行ファイルの隣は読み取り専用で
        // ありうるし複数の利用者で共有されうる ── 本体（利用者ごとの書き込み先）とは性質が違う。
        Assert.False(File.Exists(SeedPath + JsonSettingsBase<SampleSettings>.UnreadableFileSuffix));
        Assert.True(File.Exists(SeedPath));
    }

    [Fact]
    public void Load_WithNeitherFileNorSeed_ReturnsTheDefaults()
    {
        var loaded = SampleSettings.LoadWithSeed(FilePath, SeedPath);

        Assert.True(loaded.IsFirstRun);
        Assert.Equal("default", loaded.Text);
        Assert.True(loaded.OnLoadedWasCalled);
    }

    [Fact]
    public void Load_WithNoSeedPath_BehavesLikeBefore()
    {
        // 省略可能引数なので、既存の呼び出し（種を渡さない）は挙動が変わらないこと。
        var loaded = SampleSettings.LoadWithSeed(FilePath, seedFilePath: null);

        Assert.True(loaded.IsFirstRun);
        Assert.Equal("default", loaded.Text);
    }

    // ---- 変更通知 ----

    [Fact]
    public void SettingAProperty_RaisesPropertyChanged()
    {
        var settings = SampleSettings.CreateDefault();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.Number = 5;

        Assert.Contains(nameof(SampleSettings.Number), raised);
    }
}

/// <summary>テスト専用の最小設定型。実アプリの AppSettings と同じ形（ObservableProperty + ExtensionData）。</summary>
public partial class SampleSettings : JsonSettingsBase<SampleSettings>
{
    [JsonIgnore]
    public bool OnLoadedWasCalled { get; private set; }

    public string Text { get; set; } = "default";

    private int _number = 1;
    public int Number
    {
        get => _number;
        set => SetProperty(ref _number, value);
    }

    public bool Flag { get; set; } = true;

    [JsonInclude]
    public List<string> Items { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];

    protected override void OnLoaded()
    {
        base.OnLoaded();
        OnLoadedWasCalled = true;
    }

    public static SampleSettings CreateDefault() => new();

    public static SampleSettings Load(string filePath, Action<string>? onLoadFailure = null)
        => LoadOrCreate(filePath, SampleSettingsJsonContext.Default.SampleSettings, () => new(), onLoadFailure);

    public static SampleSettings LoadWithSeed(
        string filePath,
        string? seedFilePath,
        Action<string>? onLoadFailure = null,
        Action<string>? onSeedUsed = null)
        => LoadOrCreate(filePath, SampleSettingsJsonContext.Default.SampleSettings, () => new(),
                        onLoadFailure, seedFilePath, onSeedUsed);
}

[JsonSerializable(typeof(SampleSettings))]
internal partial class SampleSettingsJsonContext : JsonSerializerContext
{
}
