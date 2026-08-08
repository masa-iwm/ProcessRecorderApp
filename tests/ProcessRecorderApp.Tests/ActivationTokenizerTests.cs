using ProcessRecorderApp.SingleInstance;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="ActivationTokenizer"/> のトークン化と argv[0] 除去。
///
/// 常駐ワーカーが受け取るのは「1本のコマンドライン文字列」であり、
/// ここでの分解を誤ると CLI のすべてのコマンドが別物として解釈される。
/// 特にクォート処理が落ちると、空白を含む値（レコーダー名 <c>Recorder #1</c> や
/// <c>C:\Program Files\...</c> 形式のパス）が複数トークンに割れる。
/// </summary>
public class ActivationTokenizerTests
{
    [Fact]
    public void Tokenize_Empty_ReturnsEmptyArray()
        => Assert.Empty(ActivationTokenizer.Tokenize(""));

    [Fact]
    public void Tokenize_Whitespace_ReturnsEmptyArray()
        => Assert.Empty(ActivationTokenizer.Tokenize("   \t  "));

    [Fact]
    public void Tokenize_SimpleArguments_SplitOnWhitespace()
        => Assert.Equal(
            new[] { "start-recording", "Recorder" },
            ActivationTokenizer.Tokenize("start-recording Recorder"));

    [Fact]
    public void Tokenize_CollapsesRunsOfWhitespace()
        => Assert.Equal(
            new[] { "ping", "activate" },
            ActivationTokenizer.Tokenize("  ping \t\t activate  "));

    [Fact]
    public void Tokenize_QuotedSpan_IsKeptAsASingleToken()
        => Assert.Equal(
            new[] { "start-recording", "Recorder #1" },
            ActivationTokenizer.Tokenize("start-recording \"Recorder #1\""));

    [Fact]
    public void Tokenize_QuotedPathWithSpaces_IsKeptAsASingleToken()
        => Assert.Equal(
            new[] { "--set", @"Dir=C:\Program Files\out" },
            ActivationTokenizer.Tokenize("--set \"Dir=C:\\Program Files\\out\""));

    [Fact]
    public void Tokenize_QuotesInsideAToken_AreRemovedWithoutSplitting()
        => Assert.Equal(
            new[] { @"Dir=C:\Program Files\out" },
            ActivationTokenizer.Tokenize("Dir=\"C:\\Program Files\\out\""));

    [Fact]
    public void Tokenize_MultipleQuotedTokens_AreSeparated()
        => Assert.Equal(
            new[] { "a b", "c d" },
            ActivationTokenizer.Tokenize("\"a b\" \"c d\""));

    [Fact]
    public void Tokenize_UnterminatedQuote_KeepsTheRestAsOneToken()
        => Assert.Equal(
            new[] { "--set", "K=a b c" },
            ActivationTokenizer.Tokenize("--set \"K=a b c"));

    [Fact]
    public void Tokenize_EmptyQuotedSpan_ProducesNoToken()
        => Assert.Equal(
            new[] { "ping" },
            ActivationTokenizer.Tokenize("ping \"\""));

    // ---- CommandLineToArgvW との規則整合 ----
    // ランチャー側の検証は Main(string[] args)（＝ OS の分解結果）に対して行われるため、
    // ここが OS と違う規則だと「検証を通った引数がワーカーでは別物として実行される」。

    [Fact]
    public void Tokenize_EscapedQuoteInsideQuotes_IsALiteralQuote()
        => Assert.Equal(
            new[] { "--set", "Msg=say \"hi\"" },
            ActivationTokenizer.Tokenize("--set \"Msg=say \\\"hi\\\"\""));

    [Fact]
    public void Tokenize_DoubledQuoteInsideQuotes_IsALiteralQuote()
        => Assert.Equal(
            new[] { "a\"b" },
            ActivationTokenizer.Tokenize("\"a\"\"b\""));

    [Fact]
    public void Tokenize_FullWidthSpace_DoesNotSplit()
    {
        // OS の区切りは空白とタブだけ。全角スペース（U+3000）で割ると、ランチャーの
        // 検証を通った日本語の値がワーカーでは余剰トークンになる。
        Assert.Equal(
            new[] { "--set", "Name=会議\u3000メモ" },
            ActivationTokenizer.Tokenize("--set Name=会議\u3000メモ"));
    }

    [Fact]
    public void Tokenize_BackslashesNotBeforeAQuote_AreLiteral()
        => Assert.Equal(
            new[] { @"C:\a\\b" },
            ActivationTokenizer.Tokenize(@"C:\a\\b"));

    [Fact]
    public void Tokenize_EvenBackslashesBeforeAQuote_AreHalvedAndTheQuoteToggles()
        => Assert.Equal(
            new[] { "--set", @"Dir=C:\out" + "\\", "next" },
            ActivationTokenizer.Tokenize(@"--set ""Dir=C:\out\\"" next"));

    // ---- argv[0] の除去 ----

    [Fact]
    public void StripExecutablePath_Empty_ReturnsEmpty()
        => Assert.Empty(ActivationTokenizer.StripExecutablePath([]));

    [Fact]
    public void StripExecutablePath_FullPathOfTheCurrentProcess_IsRemoved()
        => Assert.Equal(
            new[] { "ping" },
            ActivationTokenizer.StripExecutablePath([Environment.ProcessPath!, "ping"]));

    [Fact]
    public void StripExecutablePath_FileNameOnly_IsRemoved()
    {
        // Visual Studio のデバッグ実行では argv[0] がファイル名のみになる
        string exeFileName = Path.GetFileName(Environment.ProcessPath!);

        Assert.Equal(new[] { "activate" }, ActivationTokenizer.StripExecutablePath([exeFileName, "activate"]));
    }

    [Fact]
    public void StripExecutablePath_IsCaseInsensitive()
    {
        string exeFileName = Path.GetFileName(Environment.ProcessPath!).ToUpperInvariant();

        Assert.Equal(new[] { "activate" }, ActivationTokenizer.StripExecutablePath([exeFileName, "activate"]));
    }

    [Fact]
    public void StripExecutablePath_UnrelatedFirstToken_IsKept()
        => Assert.Equal(
            new[] { "ping", "activate" },
            ActivationTokenizer.StripExecutablePath(["ping", "activate"]));

    [Fact]
    public void StripExecutablePath_OnlyTheExecutable_LeavesNoArguments()
        => Assert.Empty(ActivationTokenizer.StripExecutablePath([Environment.ProcessPath!]));
}
