using ProcessRecorderApp.Components;
using System.IO;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 設定に書かれたディレクトリ指定の解決規則（<see cref="AppDirectories"/>）。
///
/// <para>
/// <b>基準は実行ファイルのあるディレクトリで、カレントディレクトリではない。</b>
/// 常駐ワーカーは最初に起動したシェルのカレントディレクトリをプロセス寿命ぶん
/// 引きずるので、カレント基準だと「誰がどこから起動したか」で出力先が変わる。
/// </para>
/// <para>
/// 基準ディレクトリは引数で受け取る形にしてある ── そうしないと
/// 「テストランナーの exe の場所」に依存して、検証が実質的に何も言わなくなる。
/// </para>
/// </summary>
public class AppDirectoriesTests
{
    private const string Base = @"C:\app";

    [Fact]
    public void AnEmptySettingMeansTheBaseDirectoryItself()
    {
        Assert.Equal(Base, AppDirectories.ResolveOrBase("", Base));
        Assert.Equal(Base, AppDirectories.ResolveOrBase(null, Base));
        Assert.Equal(Base, AppDirectories.ResolveOrBase("   ", Base));
    }

    [Fact]
    public void ARelativePathIsResolvedAgainstTheBaseDirectory()
    {
        Assert.Equal(@"C:\app\rec", AppDirectories.ResolveOrBase("rec", Base));
        Assert.Equal(@"C:\app\rec\out", AppDirectories.ResolveOrBase(@"rec\out", Base));
        // 相対の遡りも通す（利用者が意図して書いた場合に黙って別の場所へ出さない）
        Assert.Equal(@"C:\rec", AppDirectories.ResolveOrBase(@"..\rec", Base));
    }

    [Fact]
    public void AnAbsolutePathIsUsedAsIs()
    {
        Assert.Equal(@"D:\videos", AppDirectories.ResolveOrBase(@"D:\videos", Base));
        Assert.Equal(@"D:\videos", AppDirectories.ResolveOrBase(@"D:\videos\", Base));
    }

    [Fact]
    public void SurroundingWhitespaceIsIgnored()
    {
        Assert.Equal(@"C:\app\rec", AppDirectories.ResolveOrBase("  rec  ", Base));
    }

    [Fact]
    public void ResolveOptional_KeepsAnEmptySettingEmpty()
    {
        // 「指定が無ければ何もしない」設定（GST_DEBUG_DUMP_DOT_DIR）は、
        // 空欄を基準ディレクトリへ化けさせてはいけない
        // ── 化けると、指定していないのに毎回 .dot が吐かれることになる。
        Assert.Equal("", AppDirectories.ResolveOptional("", Base));
        Assert.Null(AppDirectories.ResolveOptional(null, Base));
        Assert.Equal(@"C:\app\dots", AppDirectories.ResolveOptional("dots", Base));
    }

    [Fact]
    public void TheDefaultBaseDirectoryIsWhereTheExecutableIs()
    {
        // 値そのものはホスト依存なので、規則だけを見る。
        Assert.Equal(
            Path.GetDirectoryName(System.Environment.ProcessPath),
            AppDirectories.BaseDirectory);
    }
}
