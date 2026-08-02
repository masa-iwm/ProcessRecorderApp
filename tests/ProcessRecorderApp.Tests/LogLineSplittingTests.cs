using System.Text;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="IncrementalLineSplitter"/> と <see cref="LogText"/> の契約。
///
/// 分割器の出力はデバッグログファイルと元標準エラーへの複写に使う。どちらも既存の契約
/// （「突然死の直前の1行」「E2E ハーネスの観測面」）を持つので、
/// <b>行の内容は <see cref="StreamReader.ReadLine"/> と一致していなければならない</b>。
/// そこで期待値はハードコードせず、<b>同じ入力を実際に <see cref="StreamReader"/> へ通した結果</b>と
/// 突き合わせる ── 規則を2か所に書かないため。
/// </summary>
public class LogLineSplittingTests
{
    private static List<string> Split(params string[] chunks)
    {
        var lines = new List<string>();
        var splitter = new IncrementalLineSplitter();
        foreach (var chunk in chunks)
        {
            splitter.Push(chunk, span => lines.Add(span.ToString()));
        }
        splitter.Eof(span => lines.Add(span.ToString()));
        return lines;
    }

    private static List<string> ReadLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return lines;
    }

    [Theory]
    [InlineData("a\nb\n")]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\rb\r")]
    [InlineData("\r\r\n")]
    [InlineData("\n\n\n")]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a\r\nb")]
    [InlineData("no terminator at all")]
    [InlineData("\u001b[31mcolored\u001b[0m\nplain\n")]
    public void OneChunk_MatchesStreamReader(string text)
    {
        Assert.Equal(ReadLines(text), Split(text));
    }

    [Theory]
    // \r と \n がチャンク境界で分かれる ── 空行を増やしてはいけない
    [InlineData("a\r", "\nb\n")]
    [InlineData("a\r", "b\n")]
    [InlineData("a", "b\n")]
    [InlineData("a\n", "b")]
    [InlineData("line\r\n", "next\r\n")]
    [InlineData("\u001b[3", "1mcolored\u001b[0m\n")]
    public void ChunkBoundaries_MatchStreamReader(string first, string second)
    {
        Assert.Equal(ReadLines(first + second), Split(first, second));
    }

    [Fact]
    public void NoInputAtAll_YieldsNoLines()
    {
        // 「空行が1本」ではない。ReadLine は最初の呼び出しで null を返す
        Assert.Empty(Split());
    }

    [Fact]
    public void ATrailingCarriageReturn_DoesNotWaitForTheNextChunk()
    {
        // ここだけ StreamReader と挙動が違う（意図的）。
        // ReadLine は次の1バイトが来るまで返さないが、この分割器はその場で確定する。
        // 行の内容は同じで、出るタイミングだけが早い
        var lines = new List<string>();
        var splitter = new IncrementalLineSplitter();

        splitter.Push("progress\r", span => lines.Add(span.ToString()));

        Assert.Equal(["progress"], lines);
    }

    // ---- LogText ----

    [Theory]
    [InlineData("abcdef\rXY", "XYcdef")]
    [InlineData("plain", "plain")]
    [InlineData("10%\r20%\r100%", "100%")]
    [InlineData("first\rsecond\nthird\rx", "second\nxhird")]
    public void FlattenCarriageReturns_BehavesLikeAColumnCursor(string input, string expected)
    {
        Assert.Equal(expected, LogText.FlattenCarriageReturns(input));
    }

    [Theory]
    [InlineData("abcdef\rXY", "XY")]
    [InlineData("plain", "plain")]
    [InlineData("a\rb\rc", "c")]
    public void TakeAfterLastCr_KeepsOnlyTheLastOverwrite(string input, string expected)
    {
        Assert.Equal(expected, LogText.TakeAfterLastCr(input));
    }

    [Fact]
    public void FlattenCarriageReturns_IsMeantToRunAfterStrippingAnsi()
    {
        // エスケープは画面上 0 桁なのに文字列としては桁を進めるので、
        // 先に落としておかないと上書き位置がずれる
        const string raw = "\u001b[31mabcdef\u001b[0m\rXY";

        var flattened = LogText.FlattenCarriageReturns(AnsiEscape.Strip(raw));

        Assert.Equal("XYcdef", flattened);
    }

    [Fact]
    public void FlattenCarriageReturns_LeavesTextWithoutCarriageReturnsUntouched()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            text.Append($"line{i}\n");
        }
        var original = text.ToString();

        Assert.Same(original, LogText.FlattenCarriageReturns(original));
    }
}
