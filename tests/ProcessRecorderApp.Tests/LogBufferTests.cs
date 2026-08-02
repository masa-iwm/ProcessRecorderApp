using System.Text;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="LogBuffer"/> の契約。
///
/// Log 画面の表示は WebView2 の中（既定）でも ListView（フォールバック）でも
/// <b>E2E から中身を読めない</b>（<c>docs/coverage-gaps.md</c>）。
/// したがって「何を保持し、何を捨て、捨てたことをどう伝えるか」を機械的に守れるのは
/// この層だけであり、ここが唯一の継ぎ目になる。
///
/// 検証する契約:
///   - 上限を超えたら古い側から捨てる（行数・総文字数・1セグメント長の3つで有界）
///   - <b>捨てるのは改行の境界まで</b>（エスケープシーケンスの途中で切らない）
///   - <c>DroppedLines</c> は「一度も読み出されないまま消えた行」だけを数える
///   - カーソル（絶対位置）で読み進めれば取りこぼしも重複も無い
///   - <see cref="LogBuffer.Clear"/> は世代番号で伝わり、位置は逆行しない
/// </summary>
public class LogBufferTests
{
    private const int Budget = 1 << 20;

    /// <summary>カーソルを最後まで進めながら全文を読み出す（読み手の定常動作）</summary>
    private static (string Text, LogRead Last) ReadAll(LogBuffer buffer, long pos = 0, long line = 0, int epoch = 0)
    {
        var builder = new StringBuilder();
        LogRead read;
        do
        {
            read = buffer.Read(pos, line, epoch, Budget);
            builder.Append(read.Text);
            (pos, line, epoch) = (read.NextPos, read.NextLine, read.Epoch);
        }
        while (read.Text.Length > 0);
        return (builder.ToString(), read);
    }

    // ---- 追記と読み出し ----

    [Fact]
    public void Read_ReturnsEverythingAppended_Verbatim()
    {
        var buffer = new LogBuffer();
        buffer.Append("\u001b[31mred\u001b[0m\r\n");
        buffer.Append("progress 10%\rprogress 20%\r\n");

        var (text, _) = ReadAll(buffer);

        // ANSI も CR も落とさない ── 解釈するのは端末側の仕事
        Assert.Equal("\u001b[31mred\u001b[0m\r\nprogress 10%\rprogress 20%\r\n", text);
    }

    [Fact]
    public void Read_FromTheCursor_NeitherSkipsNorRepeats()
    {
        var buffer = new LogBuffer();
        buffer.Append("one\n");
        var first = buffer.Read(0, 0, 0, Budget);

        buffer.Append("two\nthree\n");
        var second = buffer.Read(first.NextPos, first.NextLine, first.Epoch, Budget);

        Assert.Equal("one\n", first.Text);
        Assert.Equal("two\nthree\n", second.Text);
        Assert.Equal(0, second.DroppedLines);
    }

    [Fact]
    public void Read_HonoursTheCharacterBudget_AndResumesExactlyWhereItStopped()
    {
        var buffer = new LogBuffer();
        buffer.Append("abcde\nfghij\n");

        var first = buffer.Read(0, 0, 0, 4);
        var second = buffer.Read(first.NextPos, first.NextLine, first.Epoch, Budget);

        Assert.Equal("abcd", first.Text);
        Assert.Equal("e\nfghij\n", second.Text);
    }

    [Fact]
    public void Read_ReturnsNothing_WhenTheCursorIsAlreadyAtTheEnd()
    {
        var buffer = new LogBuffer();
        buffer.Append("only\n");
        var first = buffer.Read(0, 0, 0, Budget);

        var again = buffer.Read(first.NextPos, first.NextLine, first.Epoch, Budget);

        Assert.Equal(string.Empty, again.Text);
        Assert.False(again.Cleared);
        Assert.Equal(0, again.DroppedLines);
    }

    // ---- 上限と破棄 ----

    [Fact]
    public void OldestLinesAreDiscarded_WhenTheLineLimitIsExceeded()
    {
        var buffer = new LogBuffer { MaxLines = LogBuffer.MinMaxLines };
        for (var i = 0; i < LogBuffer.MinMaxLines + 5; i++)
        {
            buffer.Append($"line{i}\n");
        }

        var snapshot = buffer.Snapshot();

        Assert.DoesNotContain("line0\n", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("line4\n", snapshot, StringComparison.Ordinal);
        Assert.StartsWith("line5\n", snapshot, StringComparison.Ordinal);
        Assert.EndsWith($"line{LogBuffer.MinMaxLines + 4}\n", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void DroppedLines_CountsOnlyWhatWasNeverRead()
    {
        var buffer = new LogBuffer { MaxLines = LogBuffer.MinMaxLines };
        for (var i = 0; i < LogBuffer.MinMaxLines + 3; i++)
        {
            buffer.Append($"line{i}\n");
        }

        var read = buffer.Read(0, 0, 0, Budget);

        Assert.Equal(3, read.DroppedLines);
    }

    [Fact]
    public void DroppedLines_StaysZero_WhenTheReaderHadAlreadyConsumedTheEvictedLines()
    {
        const int limit = LogBuffer.MinMaxLines;
        var buffer = new LogBuffer { MaxLines = limit };
        for (var i = 0; i < limit; i++)
        {
            buffer.Append($"line{i}\n");
        }
        // 読み手が追いついている状態を作る
        var (_, caughtUp) = ReadAll(buffer);

        // ここから押し出しが起きるが、押し出されるのは読み終えた行だけ。
        // これは欠落ではないので数えてはいけない
        for (var i = limit; i < limit + (limit / 2); i++)
        {
            buffer.Append($"line{i}\n");
        }
        var next = buffer.Read(caughtUp.NextPos, caughtUp.NextLine, caughtUp.Epoch, Budget);

        Assert.Equal(0, next.DroppedLines);
        Assert.StartsWith($"line{limit}\n", next.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EvictionStopsAtALineBoundary_SoAnEscapeSequenceIsNeverCutInHalf()
    {
        var buffer = new LogBuffer { MaxLines = LogBuffer.MinMaxLines };
        for (var i = 0; i < LogBuffer.MinMaxLines * 2; i++)
        {
            // 1行がまるごと「色を付けて→戻す」で構成されている
            buffer.Append($"\u001b[3{i % 8}mline{i}\u001b[0m\n");
        }

        var snapshot = buffer.Snapshot();

        // 保持窓の先頭は必ずエスケープの開始（ESC）で、途中（'[' や数字）ではない
        Assert.StartsWith("\u001b[3", snapshot, StringComparison.Ordinal);
        Assert.Equal(LogBuffer.MinMaxLines, snapshot.Count(c => c == '\n'));
    }

    [Fact]
    public void ATailWithoutAnyNewline_IsStillBounded()
    {
        var buffer = new LogBuffer { MaxLines = LogBuffer.MinMaxLines };
        // 改行を1つも吐かない生産者。行数だけでは有界にならない
        for (var i = 0; i < 40; i++)
        {
            buffer.Append(new string('x', LogBuffer.MaxSegmentChars));
        }

        var snapshot = buffer.Snapshot();

        Assert.True(
            snapshot.Length <= (LogBuffer.MinMaxLines + 1) * LogBuffer.MaxSegmentChars,
            $"改行の無い出力が有界になっていない: {snapshot.Length} 文字");
    }

    [Fact]
    public void ShrinkingTheLimit_TrimsImmediately()
    {
        var buffer = new LogBuffer();
        for (var i = 0; i < 500; i++)
        {
            buffer.Append($"line{i}\n");
        }

        buffer.MaxLines = LogBuffer.MinMaxLines;

        Assert.Equal(LogBuffer.MinMaxLines, buffer.Snapshot().Count(c => c == '\n'));
    }

    [Theory]
    [InlineData(1, LogBuffer.MinMaxLines)]
    [InlineData(int.MaxValue, LogBuffer.MaxMaxLines)]
    public void TheLineLimitIsClamped(int requested, int expected)
    {
        var buffer = new LogBuffer { MaxLines = requested };

        Assert.Equal(expected, buffer.MaxLines);
    }

    // ---- Clear ----

    [Fact]
    public void Clear_IsReportedThroughTheEpoch_AndIsNotCountedAsALoss()
    {
        var buffer = new LogBuffer();
        buffer.Append("before\n");
        var before = buffer.Read(0, 0, 0, Budget);

        buffer.Clear();
        buffer.Append("after\n");
        var after = buffer.Read(before.NextPos, before.NextLine, before.Epoch, Budget);

        Assert.True(after.Cleared);
        // 利用者が明示的に消したものは「取りこぼし」ではない
        Assert.Equal(0, after.DroppedLines);
        Assert.Equal("after\n", after.Text);
    }

    [Fact]
    public void Clear_NeverMovesThePositionBackwards()
    {
        var buffer = new LogBuffer();
        buffer.Append("0123456789\n");
        var before = buffer.Read(0, 0, 0, Budget);

        buffer.Clear();
        var after = buffer.Read(0, 0, 0, Budget);

        Assert.True(after.NextPos >= before.NextPos);
        Assert.True(after.NextLine >= before.NextLine);
        Assert.Equal(string.Empty, after.Text);
    }

    [Fact]
    public void Clear_EmptiesTheSnapshot()
    {
        var buffer = new LogBuffer();
        buffer.Append("gone\n");

        buffer.Clear();

        Assert.Equal(string.Empty, buffer.Snapshot());
    }

    // ---- 並行性 ----

    [Fact]
    public void ConcurrentAppendAndClear_DoNotCorruptTheBuffer()
    {
        // 実際の使われ方（読み取りスレッドが Append、UI スレッドが Read / Clear）を再現する。
        // Task ではなく Thread を直に使うのは、スレッドプールの都合で
        // 3 者が本当に同時に走らない事態を避けるため
        var buffer = new LogBuffer { MaxLines = LogBuffer.MinMaxLines };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        var failures = new List<Exception>();

        void Run(Action body)
        {
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    body();
                }
            }
            catch (Exception ex)
            {
                lock (failures) { failures.Add(ex); }
            }
        }

        var counter = 0;
        long pos = 0, line = 0;
        var epoch = 0;

        var threads = new[]
        {
            new Thread(() => Run(() => buffer.Append($"line{counter++}\n"))),
            new Thread(() => Run(() => { buffer.Clear(); Thread.Sleep(1); })),
            new Thread(() => Run(() =>
            {
                var read = buffer.Read(pos, line, epoch, 4096);
                (pos, line, epoch) = (read.NextPos, read.NextLine, read.Epoch);
            })),
        };

        foreach (var thread in threads) { thread.Start(); }
        foreach (var thread in threads) { thread.Join(); }

        // 不変条件（保持窓の先頭・セグメントの絶対位置）が壊れると添字例外になる
        Assert.Empty(failures);
    }
}
