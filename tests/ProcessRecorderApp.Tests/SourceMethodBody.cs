using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>ソースをテキストとして突き合わせる</b>検査の共有部品。
///
/// <para>
/// 実行では守れない不変条件（プロセス起動のレースや、終了処理の途中にリダイレクトを
/// 差し込む順序）を固定するために使う。<c>WorkerAcceptingEventOrderTests</c> と
/// <c>ShutdownRedirectHandlingTests</c> が共有している。
/// </para>
/// </summary>
internal static class SourceMethodBody
{
    /// <summary>シグネチャ直後の <c>{</c> から対応する <c>}</c> までを取り出す。</summary>
    internal static string Extract(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0,
            $"ソースに '{signature}' が見つからない（改名された可能性がある）。"
            + "改名したなら、この検査も一緒に直すこと。");

        int open = source.IndexOf('{', start + signature.Length);
        Assert.True(open >= 0, $"'{signature}' の本体が見つからない。");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"'{signature}' の本体が閉じていない。");
    }

    /// <summary>
    /// <b>コメント行は数えない</b>最初の出現位置。
    ///
    /// <para>
    /// <b>これが無いと検査が黙って無効になる。</b> この種の検査が見るメソッドには
    /// 「なぜこの順序なのか」「なぜこの1行が<i>無い</i>のか」を説明するコメントが必ず付くので、
    /// <b>コメントが本文と同じ語を含む</b>。素の <c>IndexOf</c> はそちらを拾い、
    /// 実際に退行を入れても<b>緑のまま通ってしまう</b>
    /// （<c>WorkerAcceptingEventOrderTests</c> を書いた直後の退行注入で実際に踏んだ）。
    /// </para>
    /// </summary>
    internal static int IndexOfCode(string body, string needle)
    {
        for (int i = body.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = body.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            if (!SourceReferences.IsCommentLine(body, i))
                return i;
        }

        return -1;
    }

    /// <summary>コメント以外の場所に <paramref name="needle"/> が現れるか。</summary>
    internal static bool ContainsCode(string body, string needle) => IndexOfCode(body, needle) >= 0;
}
