using ProcessRecorderApp.Components;
using System;
using System.IO;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 開いた時点の長さで本文を切るラッパ（<see cref="LengthCappedStream"/>）。
///
/// <para>
/// <b>ここが守るのは「同じ応答の中で長さが 1 つであること」。</b> 録画中のファイルは
/// 要求の処理中も伸びるので、上限を固定しないと ETag・<c>Content-Length</c>・
/// <c>Content-Range</c> の total がそれぞれ別の瞬間の長さになる。
/// <b>末尾は上限であって元のファイルの末尾ではない</b> ── <c>Range</c> の解釈が
/// この長さと一致していなければ、宣言と本文が食い違う。
/// </para>
/// </summary>
public sealed class LengthCappedStreamTests
{
    private const int Cap = 40;

    /// <summary>0,1,2,… で埋めた 100 バイト（読めた位置が値で分かる）。</summary>
    private static MemoryStream Source()
    {
        byte[] data = new byte[100];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)i;
        return new MemoryStream(data, writable: false);
    }

    [Fact]
    public void TheLengthIsTheCapNotTheFile()
    {
        using var inner = Source();
        using var capped = new LengthCappedStream(inner, Cap);

        Assert.Equal(Cap, capped.Length);
        Assert.True(capped.CanRead);
        Assert.True(capped.CanSeek);
        Assert.False(capped.CanWrite);
    }

    [Fact]
    public void ReadingStopsAtTheCap()
    {
        using var inner = Source();
        using var capped = new LengthCappedStream(inner, Cap);

        byte[] buffer = new byte[100];
        int read = 0;
        for (int n; 0 < (n = capped.Read(buffer, read, buffer.Length - read));)
            read += n;

        Assert.Equal(Cap, read);
        Assert.Equal(Cap - 1, buffer[Cap - 1]);
        Assert.Equal(0, buffer[Cap]);
        Assert.Equal(0, capped.Read(buffer, 0, buffer.Length));
    }

    /// <summary>上限をまたぐ 1 回の読み取りは、上限までで切られる。</summary>
    [Fact]
    public void AReadThatCrossesTheCapIsCut()
    {
        using var inner = Source();
        using var capped = new LengthCappedStream(inner, Cap) { Position = Cap - 5 };

        Assert.Equal(5, capped.Read(new byte[20], 0, 20));
        Assert.Equal(Cap, capped.Position);
    }

    [Fact]
    public async Task ReadAsyncIsCappedToo()
    {
        using var inner = Source();
        using var capped = new LengthCappedStream(inner, Cap) { Position = Cap - 3 };

        Assert.Equal(3, await capped.ReadAsync(new byte[20], TestContext.Current.CancellationToken));
        Assert.Equal(0, await capped.ReadAsync(new byte[20], TestContext.Current.CancellationToken));
    }

    /// <summary><c>SeekOrigin.End</c> の末尾は<b>上限</b>である。</summary>
    [Fact]
    public void SeekingFromTheEndUsesTheCap()
    {
        using var inner = Source();
        using var capped = new LengthCappedStream(inner, Cap);

        Assert.Equal(Cap, capped.Seek(0, SeekOrigin.End));
        Assert.Equal(0, capped.Read(new byte[10], 0, 10));

        Assert.Equal(Cap - 10, capped.Seek(-10, SeekOrigin.End));
        Assert.Equal(10, capped.Read(new byte[10], 0, 10));
    }

    [Fact]
    public void SeekingFromTheStartAndTheCurrentPositionWorks()
    {
        using var inner = Source();
        using var capped = new LengthCappedStream(inner, Cap);

        Assert.Equal(10, capped.Seek(10, SeekOrigin.Begin));
        Assert.Equal(15, capped.Seek(5, SeekOrigin.Current));

        byte[] one = new byte[1];
        Assert.Equal(1, capped.Read(one, 0, 1));
        Assert.Equal(15, one[0]);
    }

    /// <summary>上限より後ろへ移せはするが、そこから読めるものは無い。</summary>
    [Fact]
    public void BeyondTheCapNothingIsReadable()
    {
        using var inner = Source();
        using var capped = new LengthCappedStream(inner, Cap) { Position = Cap + 10 };

        Assert.Equal(0, capped.Read(new byte[10], 0, 10));
    }

    [Fact]
    public void ItIsReadOnly()
    {
        using var inner = Source();
        using var capped = new LengthCappedStream(inner, Cap);

        Assert.Throws<NotSupportedException>(() => capped.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => capped.SetLength(1));
    }

    /// <summary><b>元のストリームは閉じない</b>（寿命は開いた側が持つ）。</summary>
    [Fact]
    public void DisposingTheWrapperLeavesTheInnerStreamOpen()
    {
        using var inner = Source();
        new LengthCappedStream(inner, Cap).Dispose();

        Assert.Equal(0, inner.Position);
        Assert.Equal(100, inner.Length);
    }
}
