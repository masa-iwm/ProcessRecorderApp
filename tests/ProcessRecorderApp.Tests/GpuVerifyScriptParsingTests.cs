using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b><c>tools/Verify-GpuEncoders.ps1</c> が <c>gst.runtime</c> の行から読む
/// <c>dir=</c> の正規表現を、<b>空白を含むパス</b>で固定する。</b>
///
/// <para>
/// <b>実機で偽の警告を出した欠陥の再発防止。</b> <c>dir=(\S+)</c> のような空白止まりの式は、
/// 既定のインストール先 <c>C:\Program Files\gstreamer\1.0\mingw_x86_64\bin</c> を
/// <c>C:\Program</c> まで読んで打ち切る。値はスクリプトが調べた場所と当然一致しないので、
/// <b>「MISMATCH: この報告は別のランタイムについてのものだ」と宣言する</b>
/// ── いちばん普通のインストールで、いちばん強い警告が出る。
/// </para>
/// <para>
/// <b>偽の赤は「製品に欠陥が1件ある」という誤った記録を残すので害が大きい。</b>
/// この repo は同じ性質の事故を既に踏んでいる（<c>x264enc</c> 決め打ちで
/// 同梱ランタイムに偽の <c>FAILED</c> を出した件）。
/// </para>
/// <para>
/// PowerShell と .NET は同じ正規表現エンジンなので、<b>スクリプトから式そのものを取り出して
/// ここで動かせる</b> ── 「同じ規則を2か所に書く」形にならない。
/// </para>
/// </summary>
public class GpuVerifyScriptParsingTests
{
    private static readonly string ScriptPath =
        RepositoryFiles.At("tools", "Verify-GpuEncoders.ps1");

    /// <summary>
    /// 実機で採取した本物の1行（`C:\src\_scratch\n3\gst with space` に置いた
    /// ランタイムを読ませたときのもの。段の名前はバインディングの
    /// <c>GstInstallOrigin</c> のもの）。<b>空白を含む値の後ろに別のフィールドが続く</b>
    /// という、この欠陥がまさに壊れる形になっている。行末の <c>source=</c> は
    /// <b>空白を含む自由文</b>なので、そこまで飲み込まないことも同時に固定する。
    /// </summary>
    private const string RealLineWithSpaces =
        @"2026-07-31 22:48:57.422 INFO gst.runtime selected=PathDirectory flavor=MinGW "
        + @"dir=C:\src\_scratch\n3\gst with space\bin "
        + @"core=C:\src\_scratch\n3\gst with space\bin\libgstreamer-1.0-0.dll "
        + @"glib=C:\src\_scratch\n3\gst with space\bin\libglib-2.0-0.dll "
        + @"mixed=False source=a directory of the PATH environment variable";

    /// <summary><c>$script:GstRuntimeFieldPattern = '...'</c> を取り出す。</summary>
    private static Regex PatternFromScript()
    {
        string text = File.ReadAllText(ScriptPath);
        Match m = Regex.Match(text, @"\$script:GstRuntimeFieldPattern\s*=\s*'([^']+)'");

        Assert.True(m.Success,
            $"{RepositoryFiles.Relative(ScriptPath)} に $script:GstRuntimeFieldPattern が見つからない。"
            + Environment.NewLine
            + "書き方を変えたなら、この検査も一緒に直すこと"
            + Environment.NewLine
            + "── 見つからないまま緑にすると、検査そのものが消える。");

        return new Regex(m.Groups[1].Value);
    }

    [Fact]
    public void TheDirFieldIsReadWholeEvenWhenThePathHasSpaces()
    {
        Match m = PatternFromScript().Match(RealLineWithSpaces);

        Assert.True(m.Success, "本物の gst.runtime 行から dir= を読めない。");
        Assert.Equal(@"C:\src\_scratch\n3\gst with space\bin", m.Groups[1].Value.Trim());
    }

    /// <summary>
    /// <b>既定のインストール先そのもの。</b> 実機で警告を出したのはこの形。
    /// </summary>
    [Fact]
    public void TheDefaultInstallLocationIsReadWhole()
    {
        string line = @"2026-07-31 10:00:00.000 INFO gst.runtime selected=DefaultInstallDirectory flavor=MinGW "
            + @"dir=C:\Program Files\gstreamer\1.0\mingw_x86_64\bin "
            + @"core=C:\Program Files\gstreamer\1.0\mingw_x86_64\bin\libgstreamer-1.0-0.dll "
            + @"mixed=False source=the default install directory of the official installer";

        Match m = PatternFromScript().Match(line);

        Assert.True(m.Success, "既定のインストール先の行から dir= を読めない。");
        Assert.Equal(@"C:\Program Files\gstreamer\1.0\mingw_x86_64\bin", m.Groups[1].Value.Trim());
        // 次のフィールドまで飲み込んでいないこと（逆向きの壊れ方）。
        Assert.DoesNotContain("core=", m.Groups[1].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// 空白の無いパスも当然読めること
    /// ── 直したつもりで普通の場合を落とす、が起きやすい。
    /// </summary>
    [Fact]
    public void APathWithoutSpacesStillReadsTheSameWay()
    {
        string line = @"2026-07-31 10:00:00.000 INFO gst.runtime selected=BundledRuntime flavor=Msvc "
            + @"dir=D:\app\runtimes\win-x64\bin core=D:\app\runtimes\win-x64\bin\gstreamer-1.0-0.dll "
            + @"mixed=False source=the runtime tree shipped next to the application";

        Match m = PatternFromScript().Match(line);

        Assert.True(m.Success, "空白の無いパスの行から dir= を読めない。");
        Assert.Equal(@"D:\app\runtimes\win-x64\bin", m.Groups[1].Value.Trim());
    }

    /// <summary>
    /// <b>ディレクトリを固定しない段（ベアネーム）の <c>dir=(search-path)</c> も丸ごと読めること。</b>
    /// スクリプト側はこの値を「読めなかった」と取り違えてはいけない
    /// ── 取り違えると、正しく読めた行に対して「行を読み違えた」という警告を出す
    /// （このファイルが防いでいる偽の警告と同じ性質の事故）。
    /// </summary>
    [Fact]
    public void TheBareNameStageIsReadAsAWholeToken()
    {
        string line = @"2026-07-31 10:00:00.000 INFO gst.runtime selected=ProcessSearchPath flavor=(none) "
            + @"dir=(search-path) core=C:\Windows\System32\gstreamer-1.0-0.dll glib=(not loaded) "
            + @"mixed=unknown source=a plain file name handed to the operating system loader";

        Match m = PatternFromScript().Match(line);

        Assert.True(m.Success, "dir=(search-path) の行から dir= を読めない。");
        Assert.Equal("(search-path)", m.Groups[1].Value.Trim());
    }
}
