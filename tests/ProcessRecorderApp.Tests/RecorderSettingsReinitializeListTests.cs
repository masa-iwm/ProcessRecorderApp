using System;
using System.Linq;
using System.Reflection;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="EventRecorderSettings.PropertiesRequiringReinitialize"/> が、実在する
/// 書き込み可能なプロパティだけを名指していること。
///
/// <para>
/// <b>この一覧は文字列であり、コンパイラは中身を見ない。</b> プロパティを改名すると
/// <c>nameof</c> は追随するが、<b>削除</b>や<b>読み取り専用化</b>は追随しない
/// ── リモート操作の PATCH はこの一覧との交わりを
/// 「再初期化が要る」として応答に載せるので、腐ると<b>嘘の助言</b>になる。
/// </para>
/// <para>
/// <b>「どれが載るべきか」はここでは決めない。</b> 反映の時期は実装（パイプラインの
/// 組み立て方）が決めることで、テキストの検査では判定できない。
/// 固定するのは<b>形</b>（実在・書き込み可能・重複なし）と、
/// <b>パイプライン文字列そのものである 2 つ</b>（<c>Type</c> / <c>SrcPipeline</c>）が
/// 必ず載っていることだけである。
/// </para>
/// </summary>
public sealed class RecorderSettingsReinitializeListTests
{
    private static readonly PropertyInfo[] Properties =
        typeof(EventRecorderSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public);

    [Fact]
    public void EveryNamedPropertyExistsAndIsWritable()
    {
        foreach (string name in EventRecorderSettings.PropertiesRequiringReinitialize)
        {
            var property = Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

            Assert.True(property is not null,
                $"EventRecorderSettings に '{name}' という public インスタンスプロパティがありません。");

            // 書けないプロパティを載せても PATCH からは届かない（＝助言が出る経路が無い）。
            Assert.True(property!.CanWrite,
                $"EventRecorderSettings.{name} は書き込みできないので、この一覧に載せる意味がありません。");
        }
    }

    [Fact]
    public void TheListHasNoDuplicates()
    {
        var names = EventRecorderSettings.PropertiesRequiringReinitialize;
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ThePipelineDefiningPropertiesAreListed()
    {
        // この 2 つは sink / src のパイプライン文字列そのもので、
        // 組み立て直さずに効かせる道が存在しない。
        Assert.Contains(nameof(EventRecorderSettings.Type),
            EventRecorderSettings.PropertiesRequiringReinitialize, StringComparer.Ordinal);
        Assert.Contains(nameof(EventRecorderSettings.SrcPipeline),
            EventRecorderSettings.PropertiesRequiringReinitialize, StringComparer.Ordinal);
    }
}
