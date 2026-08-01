using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 録画ファイル名テンプレートの展開処理。
///
/// GStreamer に依存しない純粋な文字列処理として <see cref="EventRecorder"/> から分離してあり、
/// 単体テストから直接検証できる（<see cref="EventRecorder.FormatFilename"/> は本クラスへの薄い委譲）。
///
/// 対応するプレースホルダ:
/// <list type="bullet">
///   <item><c>{Now}</c> / <c>{Now:書式}</c> — 録画開始時刻（<see cref="IFormattable"/> の書式指定子が使える）</item>
///   <item><c>{Name}</c> — レコーダー名</item>
///   <item><c>{ENV.変数名}</c> — 環境変数</item>
///   <item><c>{任意のキー}</c> — ユーザー定義変数（未登録のキーは展開せず原文のまま残す）</item>
///   <item><c>{{</c> / <c>}}</c> — 波括弧のエスケープ（string.Format 互換）</item>
/// </list>
/// </summary>
public static partial class FilenameTemplate
{
    /// <summary>
    /// <c>{{</c> / <c>}}</c> のエスケープと、<c>{キー}</c> / <c>{キー:書式}</c> のプレースホルダ。
    ///
    /// エスケープを「先頭の選択肢」として同じ正規表現に含めることで、置換を1パスで完結させる
    /// （展開後に <c>Replace("{{", "{")</c> を後掛けすると、展開結果に含まれる波括弧まで
    /// 巻き込んでしまうため）。
    ///
    /// キーの文字クラスが <c>[\w.]</c> なのは <c>{ENV.USERNAME}</c> のようにドットを含むキーを
    /// 受けるため（<c>\w</c> はドットを含まない）。
    /// </summary>
    [GeneratedRegex(@"\{\{|\}\}|\{([\w.]+)(?::([^{}]*))?\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>環境変数を参照するキーの接頭辞。</summary>
    private const string EnvPrefix = "ENV.";

    // ファイル名に使えない文字の全角対応表
    private static readonly Dictionary<char, char> _fullWidthCharMap = new()
    {
        ['\\'] = '＼',
        ['/'] = '／',
        [':'] = '：',
        ['*'] = '＊',
        ['?'] = '？',
        ['"'] = '＂',
        ['<'] = '＜',
        ['>'] = '＞',
        ['|'] = '｜',
    };

    /// <summary>
    /// ファイル名に使えない文字を全角文字へ変換し、対応がない文字は <c>_</c> に置換する。
    /// テンプレート本文ではなく「展開されたを値」にのみ適用する
    /// （テンプレート中の <c>\</c> はサブディレクトリ指定として意味を持つため）。
    /// </summary>
    public static string SanitizeValue(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (Array.IndexOf(invalidChars, c) < 0)
                sb.Append(c);
            else if (_fullWidthCharMap.TryGetValue(c, out var fullWidth))
                sb.Append(fullWidth);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// テンプレートを展開する。
    /// </summary>
    /// <param name="template">展開対象のテンプレート文字列。</param>
    /// <param name="recorderName"><c>{Name}</c> に対応するレコーダー名。</param>
    /// <param name="now"><c>{Now}</c> に対応する時刻。複数箇所で同一時刻になるよう呼び出し側で1回だけ取得して渡す。</param>
    /// <param name="variables">ユーザー定義変数。</param>
    /// <param name="environmentVariable"><c>{ENV.x}</c> の解決関数（テストから差し替えられるよう引数化している）。</param>
    public static string Format(
        string template,
        string recorderName,
        DateTime now,
        IReadOnlyDictionary<string, string> variables,
        Func<string, string?> environmentVariable)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(environmentVariable);

        return PlaceholderRegex().Replace(template, m =>
        {
            // エスケープ（キーのキャプチャが無い＝"{{" または "}}"）
            if (!m.Groups[1].Success)
                return m.Value == "{{" ? "{" : "}";

            var key = m.Groups[1].Value;
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;

            object? value;
            switch (key)
            {
                case "Now":
                    value = now;
                    break;
                case "Name":
                    value = recorderName;
                    break;
                default:
                    if (key.StartsWith(EnvPrefix, StringComparison.Ordinal))
                    {
                        value = environmentVariable(key[EnvPrefix.Length..]);
                    }
                    else if (variables.TryGetValue(key, out var variableValue))
                    {
                        value = variableValue;
                    }
                    else
                    {
                        return m.Value; // 未登録キーはそのまま残す
                    }
                    break;
            }

            var text = format is not null && value is IFormattable formattable
                ? formattable.ToString(format, CultureInfo.InvariantCulture)
                : value?.ToString() ?? string.Empty;
            return SanitizeValue(text);
        });
    }
}
