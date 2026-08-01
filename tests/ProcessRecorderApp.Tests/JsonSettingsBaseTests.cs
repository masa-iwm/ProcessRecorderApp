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

    public static SampleSettings Load(string filePath)
        => LoadOrCreate(filePath, SampleSettingsJsonContext.Default.SampleSettings, () => new());
}

[JsonSerializable(typeof(SampleSettings))]
internal partial class SampleSettingsJsonContext : JsonSerializerContext
{
}
