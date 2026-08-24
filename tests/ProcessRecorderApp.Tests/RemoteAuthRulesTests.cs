using System;
using System.Collections.Generic;
using System.Diagnostics;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// リモート操作の認可（<see cref="RemoteAuthRules"/>）。
///
/// <para>
/// <b>判定順そのものが仕様である。</b> 利用側（<c>RemoteAuth</c> / <c>AuthGate</c>）は
/// ASP.NET Core 側にあって L1 から参照できないので、規則をここで固定する。
/// 取り違えると <b>LAN の誰でも録画を操作できる</b>か、
/// <b>正しい利用者が誰も通れない</b>かのどちらかになり、どちらもビルドは通る。
/// </para>
/// </summary>
public class RemoteAuthRulesTests
{
    /// <summary>
    /// 判定の全分岐。列は
    /// 「名乗れているか」「その役割」「要る役割」「書き込みか」「クライアントヘッダー」
    /// 「ゲスト読み取り許可」で、最後が期待する判定。
    /// </summary>
    public static TheoryData<bool, RemoteRole, RemoteRole, bool, bool, bool, RemoteAuthDecision> Cases() => new()
    {
        // ---- ① 未認証 ----
        // ゲスト不許可なら読み取りでも 401。
        { false, RemoteRole.Viewer, RemoteRole.Viewer, false, false, false, RemoteAuthDecision.Unauthorized },
        // ゲスト許可 ＋ Viewer ＋ 読み取り → 通す（これが「ゲスト」の全部である）。
        { false, RemoteRole.Viewer, RemoteRole.Viewer, false, false, true, RemoteAuthDecision.Allow },
        // ゲスト許可でも書き込みは 401（ヘッダーの有無に関わらず、資格が先）。
        { false, RemoteRole.Viewer, RemoteRole.Viewer, true, true, true, RemoteAuthDecision.Unauthorized },
        // ゲスト許可でも Viewer を超える要求は 401（403 ではない ── 誰であるかが先）。
        { false, RemoteRole.Viewer, RemoteRole.Operator, false, false, true, RemoteAuthDecision.Unauthorized },

        // ---- ② クライアントヘッダー（CSRF 対策） ----
        // Bearer 正・ヘッダー無し・write → 403 client header required。
        // **役割は足りている**（Admin）のに 403 になるのが要点で、
        // 「トークンは合っている」と呼び出し側に伝わる。
        { true, RemoteRole.Admin, RemoteRole.Operator, true, false, false, RemoteAuthDecision.ClientHeaderRequired },
        // 役割が足りない ＋ ヘッダーも無い → ヘッダーが先に出る（判定順の証拠）。
        { true, RemoteRole.Viewer, RemoteRole.Admin, true, false, false, RemoteAuthDecision.ClientHeaderRequired },
        // 読み取りにはヘッダーは要らない（<video> も EventSource も付けられない）。
        { true, RemoteRole.Viewer, RemoteRole.Viewer, false, false, false, RemoteAuthDecision.Allow },

        // ---- ③ 役割 ----
        // Viewer が start（Operator）を押した → 403 insufficient role。
        { true, RemoteRole.Viewer, RemoteRole.Operator, true, true, false, RemoteAuthDecision.InsufficientRole },
        // Operator が設定の PATCH（Admin）を押した → 403。
        { true, RemoteRole.Operator, RemoteRole.Admin, true, true, false, RemoteAuthDecision.InsufficientRole },
        // 読み取りでも役割は見る（将来 Viewer より上を要る GET を足したときのため）。
        { true, RemoteRole.Viewer, RemoteRole.Operator, false, false, false, RemoteAuthDecision.InsufficientRole },

        // ---- ④ 通す ----
        { true, RemoteRole.Operator, RemoteRole.Operator, true, true, false, RemoteAuthDecision.Allow },
        { true, RemoteRole.Admin, RemoteRole.Admin, true, true, false, RemoteAuthDecision.Allow },
        // 上位の役割は下位の要求を満たす。
        { true, RemoteRole.Admin, RemoteRole.Viewer, true, true, false, RemoteAuthDecision.Allow },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decide_FollowsTheFixedOrder(
        bool authenticated,
        RemoteRole role,
        RemoteRole required,
        bool write,
        bool clientHeaderOk,
        bool allowGuestRead,
        RemoteAuthDecision expected)
    {
        RemotePrincipal? principal = authenticated ? new RemotePrincipal("someone", role) : null;

        Assert.Equal(expected, RemoteAuthRules.Decide(principal, required, write, clientHeaderOk, allowGuestRead));
    }

    /// <summary>
    /// 名乗れていない相手にゲスト読み取りを許しても、<b>書き込みは 401 のまま</b>。
    /// ここが崩れると「読ませるだけ」のつもりの設定で録画を止められる。
    /// </summary>
    [Fact]
    public void Decide_NeverLetsAGuestWrite()
    {
        foreach (RemoteRole required in new[] { RemoteRole.Viewer, RemoteRole.Operator, RemoteRole.Admin })
        {
            foreach (bool header in new[] { false, true })
            {
                Assert.Equal(
                    RemoteAuthDecision.Unauthorized,
                    RemoteAuthRules.Decide(null, required, write: true, clientHeaderOk: header, allowGuestRead: true));
            }
        }
    }

    // ---- Authenticate ----

    private static RemoteUserDefinition User(string name, string password, RemoteRole role) => new()
    {
        Name = name,
        PasswordHash = RemoteUserRules.HashPassword(password),
        Role = role,
    };

    [Fact]
    public void Authenticate_ReturnsThePrincipalForAMatchingPassword()
    {
        // 具体型（配列）を経由する ── コレクション式を IReadOnlyList<T> へ直接
        // 向けると実体の型が決まらず CsWinRT1032 になる。
        RemoteUserDefinition[] users =
        [
            User("viewer", "pw-viewer", RemoteRole.Viewer),
            User("admin", "pw-admin", RemoteRole.Admin),
        ];

        RemotePrincipal? admin = RemoteAuthRules.Authenticate(users, "admin", "pw-admin");

        Assert.NotNull(admin);
        Assert.Equal("admin", admin.Value.Name);
        Assert.Equal(RemoteRole.Admin, admin.Value.Role);
    }

    [Fact]
    public void Authenticate_RejectsAWrongPassword_AndAnUnknownUser()
    {
        RemoteUserDefinition[] users = [User("admin", "pw-admin", RemoteRole.Admin)];

        Assert.Null(RemoteAuthRules.Authenticate(users, "admin", "pw-admi"));
        Assert.Null(RemoteAuthRules.Authenticate(users, "admin", ""));
        Assert.Null(RemoteAuthRules.Authenticate(users, "nobody", "pw-admin"));
        Assert.Null(RemoteAuthRules.Authenticate(users, "", "pw-admin"));
        // 名前は序数一致（大文字小文字は別人）。
        Assert.Null(RemoteAuthRules.Authenticate(users, "Admin", "pw-admin"));
        Assert.Null(RemoteAuthRules.Authenticate(System.Array.Empty<RemoteUserDefinition>(), "admin", "pw-admin"));
    }

    /// <summary>
    /// 壊れたハッシュ（手で編集された settings.json）でも例外を投げず、通さない。
    /// </summary>
    [Fact]
    public void Authenticate_RejectsAMalformedStoredHash()
    {
        RemoteUserDefinition[] users =
        [
            new RemoteUserDefinition { Name = "broken", PasswordHash = "not-a-hash", Role = RemoteRole.Admin },
        ];

        Assert.Null(RemoteAuthRules.Authenticate(users, "broken", "anything"));
    }

    /// <summary>
    /// <b>知らない名前でも同じだけ時間が掛かる。</b> 早く断ると、応答時間が
    /// 「その利用者は居る」を答えてしまう（利用者名の総当たりが成立する）。
    ///
    /// <para>
    /// 判定は<b>桁</b>で行う ── 実測値を焼き込むと機種で落ちる。PBKDF2 の 60 万回は
    /// この開発機で数百 ms あり、「ダミーを回していない」実装は 1ms 未満で返るので、
    /// 「見つかった場合の 1/4 以上」なら実装の違いは十分に検出できる。
    /// </para>
    /// </summary>
    [Fact]
    public void Authenticate_TakesTheSameWorkForAnUnknownUser()
    {
        RemoteUserDefinition[] users = [User("admin", "pw-admin", RemoteRole.Admin)];

        // 1 回目は JIT と初回のコード生成を含むので、計測の前に温める。
        _ = RemoteAuthRules.Authenticate(users, "admin", "wrong");
        _ = RemoteAuthRules.Authenticate(users, "nobody", "wrong");

        var known = Stopwatch.StartNew();
        _ = RemoteAuthRules.Authenticate(users, "admin", "wrong");
        known.Stop();

        var unknown = Stopwatch.StartNew();
        _ = RemoteAuthRules.Authenticate(users, "nobody", "wrong");
        unknown.Stop();

        Assert.True(known.Elapsed.TotalMilliseconds / 4 <= unknown.Elapsed.TotalMilliseconds,
            $"未知の利用者が {unknown.Elapsed.TotalMilliseconds:F1}ms で返っています"
            + $"（既知の名前は {known.Elapsed.TotalMilliseconds:F1}ms）。"
            + "ダミーのハッシュで 1 回照合していないと、応答時間で利用者名を当てられます。");
    }

    /// <summary>ダミーのハッシュ自身が形式として正しいこと（でなければ即座に false が返る）。</summary>
    [Fact]
    public void TheDummyHash_IsWellFormed()
        => Assert.True(RemoteUserRules.IsWellFormedHash(RemoteAuthRules.DummyPasswordHash));

    // ---- IsExpired ----

    /// <summary>
    /// 期限の境界。<b>ちょうど同時刻は切れている扱い</b>
    /// （<c>SessionStore</c> はここを呼ぶだけで、自分では時刻を比べない）。
    /// </summary>
    [Fact]
    public void IsExpired_TreatsTheBoundaryAsExpired()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        Assert.False(RemoteAuthRules.IsExpired(now + TimeSpan.FromTicks(1), now));
        Assert.True(RemoteAuthRules.IsExpired(now, now));
        Assert.True(RemoteAuthRules.IsExpired(now - TimeSpan.FromTicks(1), now));
    }
}
