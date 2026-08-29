using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.Threading;

using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 補助エンコーダー枠の計数器。
///
/// <para>
/// <b>ここが唯一の上限である。</b> ライブ DASH と録画トランスコードは同じ計数器を取り合うので、
/// 数え間違い（二重解放・解放漏れ・上限の丸め忘れ）はそのまま
/// 「空いているのに繋がらない」「上限を超えてエンコーダーが走る」になる。
/// </para>
/// <para>
/// <b><see cref="AuxiliaryEncoderSlots.Shared"/> は使わない。</b> プロセス全体で 1 つの実体を
/// テストから触ると、他のテストや設定の初期化と干渉する。
/// </para>
/// </summary>
public sealed class AuxiliaryEncoderSlotsTests
{
    [Fact]
    public void AcquiringAndReleasingMovesInUseAndFree()
    {
        var slots = new AuxiliaryEncoderSlots { Limit = 2 };

        Assert.Equal(0, slots.InUse);
        Assert.Equal(2, slots.Free);

        Assert.True(slots.TryAcquire("dash:A", out var first));
        Assert.Equal("dash:A", first.Owner);
        Assert.Equal(1, slots.InUse);
        Assert.Equal(1, slots.Free);

        Assert.True(slots.TryAcquire("transcode:v1", out var second));
        Assert.Equal(2, slots.InUse);
        Assert.Equal(0, slots.Free);

        // 満席なら待たずに false。
        Assert.False(slots.TryAcquire("dash:B", out var refused));
        Assert.Null(refused);

        first.Dispose();
        Assert.Equal(1, slots.InUse);
        Assert.Equal(1, slots.Free);

        second.Dispose();
        Assert.Equal(0, slots.InUse);
        Assert.Equal(2, slots.Free);
    }

    [Theory]
    [InlineData(0, AuxiliaryEncoderLimits.MinLimit)]
    [InlineData(-5, AuxiliaryEncoderLimits.MinLimit)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(9, AuxiliaryEncoderLimits.MaxLimit)]
    [InlineData(int.MaxValue, AuxiliaryEncoderLimits.MaxLimit)]
    public void TheLimitIsClampedToTheAllowedRange(int requested, int expected)
    {
        var slots = new AuxiliaryEncoderSlots { Limit = requested };
        Assert.Equal(expected, slots.Limit);
    }

    /// <summary>
    /// <b>上限を下げても、既に出ている貸出は続く。</b> 走っているエンコーダーを止める道が
    /// 無いので、空きが 0 に張り付いて新規が取れなくなるだけである
    /// （<see cref="AuxiliaryEncoderSlots.InUse"/> が上限を超えた状態が正しく現れること）。
    /// </summary>
    [Fact]
    public void LoweringTheLimitKeepsTheLeasesThatAreAlreadyOut()
    {
        var slots = new AuxiliaryEncoderSlots { Limit = 4 };

        Assert.True(slots.TryAcquire("a", out var a));
        Assert.True(slots.TryAcquire("b", out var b));
        Assert.True(slots.TryAcquire("c", out var c));

        slots.Limit = 1;

        Assert.Equal(3, slots.InUse);
        Assert.Equal(0, slots.Free);
        Assert.False(slots.TryAcquire("d", out _));

        a.Dispose();
        b.Dispose();
        Assert.Equal(1, slots.InUse);
        Assert.Equal(0, slots.Free);
        Assert.False(slots.TryAcquire("d", out _));

        c.Dispose();
        Assert.Equal(0, slots.InUse);
        Assert.Equal(1, slots.Free);
        Assert.True(slots.TryAcquire("d", out _));
    }

    /// <summary>
    /// <b>解放は冪等。</b> 枠を返す経路は複数ある（正常終了・切断・例外・猶予失効・停止）ので、
    /// 二重解放で席が増えてはいけない。
    /// </summary>
    [Fact]
    public void DisposingALeaseTwiceReleasesOnlyOneSlot()
    {
        var slots = new AuxiliaryEncoderSlots { Limit = 2 };

        Assert.True(slots.TryAcquire("a", out var lease));
        Assert.True(slots.TryAcquire("b", out _));
        Assert.Equal(2, slots.InUse);

        lease.Dispose();
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, slots.InUse);
        Assert.Equal(1, slots.Free);
    }

    /// <summary>
    /// 取得・解放・上限の変更のそれぞれ<b>後</b>に 1 回ずつ発火すること。
    /// <b>値が変わらない <c>Limit</c> の代入では発火しない</b>
    /// ── SSE のデバウンスへ毎回流すと、同じ値の PATCH で通知が出続ける。
    /// </summary>
    [Fact]
    public void ChangedFiresOnceForEachAcquireReleaseAndLimitChange()
    {
        var slots = new AuxiliaryEncoderSlots { Limit = 1 };
        int fired = 0;
        slots.Changed += () => fired++;

        Assert.True(slots.TryAcquire("a", out var lease));
        Assert.Equal(1, fired);

        Assert.False(slots.TryAcquire("x", out _));
        Assert.False(slots.TryAcquire("y", out _));
        Assert.Equal(1, fired); // 取れなかったときは発火しない（InUse は動いていない）

        lease.Dispose();
        Assert.Equal(2, fired);

        lease.Dispose();
        Assert.Equal(2, fired); // 2 度目の Dispose は何もしない

        slots.Limit = 3;
        Assert.Equal(3, fired);

        slots.Limit = 3;
        Assert.Equal(3, fired); // 同じ値

        slots.Limit = 99; // クランプ後は 8 なので変化する
        Assert.Equal(4, fired);
    }

    /// <summary>
    /// <b>並行して取りに来ても上限を超えない。</b> 取得と解放は 1 つのロックの下で
    /// 数えるので、成功した数はちょうど <c>Limit</c> になる。
    /// </summary>
    [Fact]
    public void ConcurrentAcquiresNeverExceedTheLimit()
    {
        const int Limit = 3;
        const int Racers = 8;

        var slots = new AuxiliaryEncoderSlots { Limit = Limit };
        var leases = new List<AuxiliaryEncoderLease>();
        var gate = new object();

        // **専用スレッドで回す。** 待ち合わせで塞ぐので、スレッドプールを使うと
        // 同時に走っている他のテストの仕事が動けなくなる。
        using var start = new Barrier(Racers);
        var threads = new Thread[Racers];
        for (int i = 0; i < Racers; i++)
        {
            int index = i;
            threads[i] = new Thread(() =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(30));
                if (!slots.TryAcquire("racer" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                      out var lease))
                {
                    return;
                }

                lock (gate)
                    leases.Add(lease);
            })
            { IsBackground = true };
            threads[i].Start();
        }

        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "並行取得のスレッドが終わらない");

        Assert.Equal(Limit, leases.Count);
        Assert.Equal(Limit, slots.InUse);
        Assert.Equal(0, slots.Free);

        foreach (var lease in leases)
            lease.Dispose();

        Assert.Equal(0, slots.InUse);
    }

    /// <summary>既定値と範囲の関係（既定は必ず範囲の中）。</summary>
    [Fact]
    public void TheDefaultLimitIsInsideTheAllowedRange()
    {
        Assert.True(AuxiliaryEncoderLimits.MinLimit <= AuxiliaryEncoderLimits.DefaultLimit);
        Assert.True(AuxiliaryEncoderLimits.DefaultLimit <= AuxiliaryEncoderLimits.MaxLimit);
        Assert.Equal(AuxiliaryEncoderLimits.DefaultLimit, new AuxiliaryEncoderSlots().Limit);
    }

    [Fact]
    public void AcquireRejectsANullOwner()
        => Assert.Throws<ArgumentNullException>(() => new AuxiliaryEncoderSlots().TryAcquire(null!, out _));
}
