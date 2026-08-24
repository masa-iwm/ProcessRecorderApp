using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 元のストリームを<b>開いた時点の長さ</b>で切って見せる読み取り専用のラッパ。
///
/// <para>
/// <b>録画中のファイルは要求の処理中も伸び続ける。</b> 上限を固定しないと、
/// ETag に載せた長さ・<c>Content-Length</c>・<c>Content-Range</c> の total が
/// それぞれ別の瞬間の <c>Stream.Length</c> になり、同じ応答の中で食い違う
/// ── クライアントは「宣言より長い/短い本文」を受け取ることになる。
/// 長さを 1 回だけ読み、その値をこのラッパへ渡すことで、3 つとも同じ値になる。
/// </para>
/// <para>
/// <b>元のストリームは閉じない。</b> 寿命は開いた側（<c>using</c>）が持つ。
/// </para>
/// </summary>
/// <param name="inner">元のストリーム（シーク可能であること）。</param>
/// <param name="length">見せる長さ。元の長さより大きい値は意味を持たない。</param>
public sealed partial class LengthCappedStream(Stream inner, long length) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly long _length = 0 <= length ? length : throw new ArgumentOutOfRangeException(nameof(length));

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    /// <summary>書けない（配信専用のラッパ）。</summary>
    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <summary>上限までの残り（越えた位置なら 0）。</summary>
    private int Remaining(int count)
    {
        long left = _length - _inner.Position;
        return left <= 0 ? 0 : (int)Math.Min(left, count);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int capped = Remaining(count);
        return capped == 0 ? 0 : _inner.Read(buffer, offset, capped);
    }

    public override int Read(Span<byte> buffer)
    {
        int capped = Remaining(buffer.Length);
        return capped == 0 ? 0 : _inner.Read(buffer[..capped]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int capped = Remaining(count);
        return capped == 0
            ? Task.FromResult(0)
            : _inner.ReadAsync(buffer, offset, capped, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int capped = Remaining(buffer.Length);
        return capped == 0
            ? ValueTask.FromResult(0)
            : _inner.ReadAsync(buffer[..capped], cancellationToken);
    }

    /// <summary>
    /// 位置を移す。<b>末尾は上限のこと</b>（元のファイルの末尾ではない）
    /// ── <c>Range</c> の解釈がこの長さと一致していなければならない。
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _inner.Position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
            throw new IOException("シーク先が先頭より前です。");

        return _inner.Seek(target, SeekOrigin.Begin);
    }

    public override void Flush() => _inner.Flush();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
