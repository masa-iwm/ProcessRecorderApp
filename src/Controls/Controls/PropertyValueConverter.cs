using System;
using System.Globalization;

namespace ProcessRecorderApp.Controls;

/// <summary>
/// テキストボックスの入力文字列とプロパティの実際の型との相互変換を行う。
///
/// System.ComponentModel.TypeConverter (TypeDescriptor.GetConverter 等) は
/// 内部で広範なリフレクションと動的コード生成を必要とし、Native AOT / トリミングと
/// 相性が悪いため、ここではよく使われる型に対する変換を静的なコードで直接実装している。
/// 独自の値型を編集可能にしたい場合は、このクラスに分岐を追加するか、
/// IPropertyAccess の実装側で別の仕組みを用意すること。
/// </summary>
internal static class PropertyValueConverter
{
    public static bool TryConvert(Type targetType, string text, out object? value)
    {
        Type? underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying is not null)
        {
            if (string.IsNullOrEmpty(text))
            {
                value = null;
                return true;
            }
            targetType = underlying;
        }

        if (targetType == typeof(string))
        {
            value = text;
            return true;
        }

        if (targetType.IsEnum)
        {
            // Enum.TryParse(Type, ...) は enum の静的メタデータのみを利用するため
            // 通常の Native AOT 構成でも動的コード生成なしで動作する。
            if (Enum.TryParse(targetType, text, ignoreCase: true, out object? parsed))
            {
                value = parsed;
                return true;
            }
            value = null;
            return false;
        }

        var ic = CultureInfo.InvariantCulture;

        if (targetType == typeof(bool) && bool.TryParse(text, out var b)) { value = b; return true; }
        if (targetType == typeof(byte) && byte.TryParse(text, NumberStyles.Integer, ic, out var by)) { value = by; return true; }
        if (targetType == typeof(sbyte) && sbyte.TryParse(text, NumberStyles.Integer, ic, out var sb)) { value = sb; return true; }
        if (targetType == typeof(short) && short.TryParse(text, NumberStyles.Integer, ic, out var s16)) { value = s16; return true; }
        if (targetType == typeof(ushort) && ushort.TryParse(text, NumberStyles.Integer, ic, out var u16)) { value = u16; return true; }
        if (targetType == typeof(int) && int.TryParse(text, NumberStyles.Integer, ic, out var i32)) { value = i32; return true; }
        if (targetType == typeof(uint) && uint.TryParse(text, NumberStyles.Integer, ic, out var u32)) { value = u32; return true; }
        if (targetType == typeof(long) && long.TryParse(text, NumberStyles.Integer, ic, out var i64)) { value = i64; return true; }
        if (targetType == typeof(ulong) && ulong.TryParse(text, NumberStyles.Integer, ic, out var u64)) { value = u64; return true; }
        if (targetType == typeof(float) && float.TryParse(text, NumberStyles.Float, ic, out var f32)) { value = f32; return true; }
        if (targetType == typeof(double) && double.TryParse(text, NumberStyles.Float, ic, out var f64)) { value = f64; return true; }
        if (targetType == typeof(decimal) && decimal.TryParse(text, NumberStyles.Number, ic, out var dec)) { value = dec; return true; }
        if (targetType == typeof(DateTime) && DateTime.TryParse(text, ic, DateTimeStyles.None, out var dt)) { value = dt; return true; }
        if (targetType == typeof(DateTimeOffset) && DateTimeOffset.TryParse(text, ic, DateTimeStyles.None, out var dto)) { value = dto; return true; }
        if (targetType == typeof(TimeSpan) && TimeSpan.TryParse(text, ic, out var ts)) { value = ts; return true; }
        if (targetType == typeof(Guid) && Guid.TryParse(text, out var g)) { value = g; return true; }
        if (targetType == typeof(char) && text.Length == 1) { value = text[0]; return true; }

        value = null;
        return false;
    }
}
