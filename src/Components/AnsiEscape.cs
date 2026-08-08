using System.Text;

namespace ProcessRecorderApp.Components;

/// <summary>ANSI SGR で指定される文字装飾のフラグ</summary>
[Flags]
public enum AnsiStyle
{
    None = 0,
    /// <summary>太字（SGR 1）</summary>
    Bold = 1 << 0,
    /// <summary>淡色（SGR 2）</summary>
    Dim = 1 << 1,
    /// <summary>斜体（SGR 3）</summary>
    Italic = 1 << 2,
    /// <summary>下線（SGR 4）</summary>
    Underline = 1 << 3,
    /// <summary>前景/背景の反転（SGR 7）</summary>
    Reverse = 1 << 4,
    /// <summary>取り消し線（SGR 9）</summary>
    Strikethrough = 1 << 5,
}

/// <summary>
/// ANSI 解析結果の1セグメント（同一装飾が続くテキスト範囲）。
/// 色は 16 色パレットのインデックス（0-7: 標準色、8-15: 明色）、-1 は既定色
/// </summary>
public readonly record struct AnsiSegment(string Text, int ForegroundIndex, int BackgroundIndex, AnsiStyle Style);

/// <summary>
/// ANSI エスケープシーケンス（SGR）の解析・除去を行う純粋関数ユーティリティ。
/// UI に依存しないため、ログファイル出力・クリップボードコピー・画面描画から共用できる。
///
/// 対応 SGR: 0(リセット) / 1,2,22(太字・淡色) / 3,23(斜体) / 4,24(下線) /
/// 7,27(反転) / 9,29(取り消し線) / 30-37,90-97,39(前景色) / 40-47,100-107,49(背景色) /
/// 38;5;n・48;5;n は n が 0-15 の場合のみ色として解釈。
/// 上記以外の SGR コードと SGR 以外の CSI シーケンスは無視して除去する。
/// OSC（ESC ]）と DCS（ESC P）はペイロードごと終端（BEL / ST）まで除去する
/// ── ウィンドウタイトル設定等の中身を平文としてログへ漏らさないため
/// </summary>
public static class AnsiEscape
{
    private const char Escape = '\x1b';

    /// <summary>文字列に ANSI エスケープが含まれるか（高速パス判定用）</summary>
    public static bool Contains(string text) => text.Contains(Escape);

    /// <summary>ANSI エスケープをすべて除去したプレーン文字列を返す</summary>
    public static string Strip(string text)
    {
        if (!Contains(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == Escape)
            {
                index = ConsumeEscape(text, index, codes: null);
            }
            else
            {
                builder.Append(text[index]);
                index++;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// ANSI SGR を解析し、同一装飾ごとのセグメント列に分解する。
    /// エスケープを含まない場合は既定装飾の単一セグメントを返す
    /// </summary>
    public static List<AnsiSegment> Parse(string text)
    {
        var segments = new List<AnsiSegment>();
        if (!Contains(text))
        {
            segments.Add(new AnsiSegment(text, -1, -1, AnsiStyle.None));
            return segments;
        }

        var builder = new StringBuilder(text.Length);
        var foreground = -1;
        var background = -1;
        var style = AnsiStyle.None;
        var codes = new List<int>();

        void FlushSegment()
        {
            if (builder.Length > 0)
            {
                segments.Add(new AnsiSegment(builder.ToString(), foreground, background, style));
                builder.Clear();
            }
        }

        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == Escape)
            {
                codes.Clear();
                index = ConsumeEscape(text, index, codes);
                if (codes.Count > 0)
                {
                    // 装飾が変わる位置でセグメントを確定してから状態を更新する
                    FlushSegment();
                    ApplyCodes(codes, ref foreground, ref background, ref style);
                }
            }
            else
            {
                builder.Append(text[index]);
                index++;
            }
        }
        FlushSegment();
        return segments;
    }

    /// <summary>
    /// ESC から始まるシーケンスを読み飛ばし、次の読み取り位置を返す。
    /// SGR（CSI ... 'm'）の場合はパラメータを <paramref name="codes"/> へ格納する（null なら格納しない）
    /// </summary>
    private static int ConsumeEscape(string text, int index, List<int>? codes)
    {
        // index は ESC を指している。
        char kind = index + 1 < text.Length ? text[index + 1] : '\0';

        // OSC（ESC ] ... BEL/ST）と DCS（ESC P ... ST）はペイロードごと読み飛ばす。
        // ESC+1 文字だけ消すと "0;ウィンドウタイトル" 等のペイロードが平文として
        // デバッグログ・コピー・リスト表示へ漏れる。終端（BEL または ST=ESC '\'）が
        // 見つからない場合は行の残りを捨てる（この API は行単位で使われる）。
        if (kind is ']' or 'P')
        {
            for (var j = index + 2; j < text.Length; j++)
            {
                if (text[j] == '\a')
                    return j + 1;
                if (text[j] == Escape && j + 1 < text.Length && text[j + 1] == '\\')
                    return j + 2;
                // **改行を跨いで捨てない。** この API はログ全体（複数行の1文字列）にも
                // 掛かる（「すべてコピー」は LogBuffer.Snapshot() を通す）。終端の無い
                // シーケンスが1つあるだけで以降の全行が黙って消えるのは、
                // 漏れを塞ぐために作った副作用としては重すぎる。
                if (text[j] == '\n')
                    return j;
            }
            return text.Length;
        }

        // CSI（ESC '['）以外は ESC ごと1文字読み飛ばす
        if (kind != '[')
        {
            return index + 2 <= text.Length ? index + 2 : text.Length;
        }

        var current = 0;      // 解析中の数値
        var hasDigit = false; // 現在のパラメータに数字があったか
        var parsed = codes is null ? null : new List<int>();

        var i = index + 2;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= '0' and <= '9')
            {
                current = current * 10 + (c - '0');
                hasDigit = true;
            }
            else if (c == ';')
            {
                parsed?.Add(hasDigit ? current : 0); // 空パラメータは 0 扱い
                current = 0;
                hasDigit = false;
            }
            else if (c is ':' or '<' or '=' or '>' or '?')
            {
                // その他のパラメータバイトは無視して読み進める
            }
            else
            {
                // 終端バイト。'm'（SGR）の場合のみコードを確定する
                if (c == 'm' && parsed is not null && codes is not null)
                {
                    parsed.Add(hasDigit ? current : 0);
                    codes.AddRange(parsed);
                }
                return i + 1;
            }
        }
        return text.Length; // 終端バイトが無いまま文字列が終わった（不完全なシーケンス）
    }

    /// <summary>SGR コード列を装飾状態へ適用する</summary>
    private static void ApplyCodes(List<int> codes, ref int foreground, ref int background, ref AnsiStyle style)
    {
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            switch (code)
            {
                case 0:
                    foreground = -1;
                    background = -1;
                    style = AnsiStyle.None;
                    break;
                case 1: style |= AnsiStyle.Bold; break;
                case 2: style |= AnsiStyle.Dim; break;
                case 3: style |= AnsiStyle.Italic; break;
                case 4: style |= AnsiStyle.Underline; break;
                case 7: style |= AnsiStyle.Reverse; break;
                case 9: style |= AnsiStyle.Strikethrough; break;
                case 22: style &= ~(AnsiStyle.Bold | AnsiStyle.Dim); break;
                case 23: style &= ~AnsiStyle.Italic; break;
                case 24: style &= ~AnsiStyle.Underline; break;
                case 27: style &= ~AnsiStyle.Reverse; break;
                case 29: style &= ~AnsiStyle.Strikethrough; break;
                case >= 30 and <= 37: foreground = code - 30; break;
                case 39: foreground = -1; break;
                case >= 40 and <= 47: background = code - 40; break;
                case 49: background = -1; break;
                case >= 90 and <= 97: foreground = code - 90 + 8; break;
                case >= 100 and <= 107: background = code - 100 + 8; break;
                case 38:
                case 48:
                    // 拡張色指定。38;5;n（256色）は後続1個、38;2;r;g;b（RGB）は後続3個を消費する。
                    // 16色パレット範囲（n が 0-15）のみ色として解釈し、それ以外は無視
                    if (i + 1 < codes.Count && codes[i + 1] == 5)
                    {
                        if (i + 2 < codes.Count && codes[i + 2] is >= 0 and <= 15)
                        {
                            if (code == 38) { foreground = codes[i + 2]; } else { background = codes[i + 2]; }
                        }
                        i += 2;
                    }
                    else if (i + 1 < codes.Count && codes[i + 1] == 2)
                    {
                        i += 4;
                    }
                    break;
                default:
                    break; // 未対応コードは無視
            }
        }
    }
}
