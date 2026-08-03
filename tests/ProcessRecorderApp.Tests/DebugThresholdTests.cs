using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b><c>GstDebug</c> の即時反映が、GStreamer の初期化前に発火しても安全であること。</b>
///
/// <para>
/// <c>AppSettings</c> は <c>Gst.Functions.Init</c> より<b>前</b>に読み込まれる
/// （<c>Program.Main</c> の初期化コールバックが <c>AppSettings.Default</c> を触った後に
/// <c>Controller.StaticInitialize()</c> を呼ぶ）。逆シリアル化は
/// <c>[ObservableProperty]</c> の setter を通るので、<c>OnGstDebugChanged</c> は
/// <b>PATH の組み立てと <c>DllImportResolver</c> の登録より前に</b>呼ばれる。
/// ここでネイティブへ触ると <c>libgstreamer-1.0-0.dll</c> の解決に失敗し、
/// <b>settings.json に <c>GstDebug</c> を書いた利用者だけがアプリを起動できなくなる</b>。
/// </para>
/// <para>
/// このテストが見られるのは<b>ガードの向きだけ</b>である（L1 では GStreamer を
/// 初期化できないので「初期化後は本当に適用される」側は踏めない）。
/// そちらは L2 E2E（<c>GstDebugLiveTests</c>）が見る。
/// </para>
/// </summary>
public class DebugThresholdTests
{
    /// <summary>
    /// 初期化前は false を返し、<b>例外も投げない</b>こと。
    /// 例外を投げる実装にすると、逆シリアル化の途中で起動が死ぬ。
    /// </summary>
    [Fact]
    public void TrySetThreshold_BeforeGstInit_DoesNothingAndDoesNotThrow()
    {
        Assert.False(DebugLogEx.TrySetThreshold("4"));
        Assert.False(DebugLogEx.TrySetThreshold(""));
        Assert.False(DebugLogEx.TrySetThreshold(null));
    }

    /// <summary>
    /// <c>.dot</c> のファイル名は、時刻が先頭で並び順が経過順になること。
    /// </summary>
    [Fact]
    public void BuildDotFileName_PutsTheTimestampFirst()
    {
        var at = new System.DateTime(2026, 8, 3, 14, 5, 6, 789);

        Assert.Equal("20260803-140506.789.R1.sink.dot", DebugLogEx.BuildDotFileName(at, "R1.sink"));
    }

    /// <summary>
    /// レコーダー名はそのままファイル名になるので、パスに使えない文字は均すこと。
    /// <b>名前は利用者が自由に付けられる</b>（PropertyGrid の <c>Name</c> 行）ので、
    /// ここを素通しすると保存が例外で終わる。
    /// </summary>
    [Theory]
    [InlineData("a/b", "a_b")]
    [InlineData("a\\b", "a_b")]
    [InlineData("a:b", "a_b")]
    [InlineData("a*b?", "a_b_")]
    public void BuildDotFileName_ReplacesCharactersThatCannotBeInAFileName(string name, string expected)
    {
        var at = new System.DateTime(2026, 8, 3, 0, 0, 0, 0);

        Assert.Equal($"20260803-000000.000.{expected}.dot", DebugLogEx.BuildDotFileName(at, name));
    }

    /// <summary>空名でも「名前の無いファイル」を作らないこと。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildDotFileName_FallsBackWhenTheNameIsBlank(string name)
    {
        var at = new System.DateTime(2026, 8, 3, 0, 0, 0, 0);

        Assert.Equal("20260803-000000.000.unnamed.dot", DebugLogEx.BuildDotFileName(at, name));
    }
}
