using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// 要求の認証（誰であるか）と、ブラウザ用のセッション発行。認可の規則そのものは
/// <see cref="RemoteAuthRules"/>（純粋関数、L1 が固定）にあり、ここは
/// <b>HTTP から資格を取り出してそちらへ渡すだけ</b>である。
///
/// <para>
/// <b>読み取りにも名乗りが要る。</b> 例外は <c>RemoteControlAllowGuestRead</c> が
/// true のときの読み取りだけで、その場合は誰でも <see cref="RemoteRole.Viewer"/> 相当に見える。
/// 静的資産（<c>/</c>・<c>/{name}</c>）は無認証のまま ── ログインの画面そのものが
/// そこから配られる。
/// </para>
/// <para>
/// <b>判定順は ① 名乗り ② クライアントヘッダー ③ 役割。</b> 名乗れていなければ 401、
/// 名乗れているのに <c>X-PRApp-Client: 1</c> が無ければ 403、役割が足りなければ 403 と、
/// <b>状態が「誰であるか」「どこから来たか」「何をしてよいか」で分かれる</b>。
/// </para>
/// <para>
/// <b>ヘッダーの検査は CSRF 対策である。</b> 「他所のページが仕込んだフォーム送信」は
/// Cookie を運べてもカスタムヘッダーを付けられない
/// （CORS ミドルウェアを入れないので、付けようとすればプリフライトで止まる）。
/// そのため<b>正しい Cookie でもヘッダーが無ければ通さない</b>。
/// </para>
/// </summary>
internal sealed class RemoteAuth(
    string accessToken,
    IReadOnlyList<RemoteUserDefinition> users,
    bool allowGuestRead,
    SessionStore sessions)
{
    /// <summary>ブラウザのセッション Cookie 名。</summary>
    public const string CookieName = "prapp_session";

    /// <summary>書き込み要求に必須のヘッダー（CSRF 対策）。</summary>
    public const string ClientHeaderName = "X-PRApp-Client";

    /// <summary><see cref="ClientHeaderName"/> に要求する値。</summary>
    public const string ClientHeaderValue = "1";

    /// <summary>
    /// <c>remote.auth fail</c> を書く間隔の下限。
    /// <b>失敗のたびに書くと、総当たりが activity.log を数分で使い切る</b>
    /// （1MB で退避、世代は 1 つだけ）── 攻撃者に他の記録を消させないための間引き。
    /// </summary>
    public static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// セッション Cookie の属性。<b>発行するのは 2 箇所（<c>GET /?token=</c> と
    /// <c>POST /api/login</c>）あるので、値ではなくこの関数を共有する</b>
    /// ── 写して置くと、片方だけ直された日に属性が食い違う。
    ///
    /// <para>
    /// HTTP のみ（HTTPS は v1 の対象外）なので <c>Secure</c> は付けない。
    /// <c>HttpOnly</c> はスクリプトから読ませないため、<c>SameSite=Strict</c> は
    /// 他所のページからの遷移で Cookie を送らせないため。
    /// <b>有効期限を付けない</b>のでブラウザを閉じると消える（サーバー側の絶対期限は
    /// <see cref="SessionStore.SessionLifetime"/> が別に持つ）。
    /// </para>
    /// </summary>
    public static CookieOptions SessionCookieOptions() => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
    };

    private readonly SessionStore _sessions = sessions;
    private readonly string _accessToken = accessToken;
    private readonly IReadOnlyList<RemoteUserDefinition> _users = users;
    private readonly bool _allowGuestRead = allowGuestRead;
    private long _lastFailureLogMs = long.MinValue;

    /// <summary>
    /// この要求が誰であるか。名乗れていなければ <see langword="null"/>。
    ///
    /// <para>
    /// <b>誤った Bearer でも短絡せず Cookie を見る</b> ── ブラウザは Cookie を自動で
    /// 送るので、手で付けた古い <c>Authorization</c> が同居しうる。
    /// </para>
    /// </summary>
    public RemotePrincipal? Resolve(HttpContext ctx)
    {
        string authorization = ctx.Request.Headers.Authorization.ToString();
        if (0 < authorization.Length)
        {
            const string Prefix = "Bearer ";
            if (authorization.StartsWith(Prefix, StringComparison.Ordinal)
                && RemoteApiRules.TokenEquals(_accessToken, authorization[Prefix.Length..]))
            {
                return new RemotePrincipal(RemoteAuthRules.TokenPrincipalName, RemoteRole.Admin);
            }
        }

        return _sessions.TryGet(ctx.Request.Cookies[CookieName], out SessionEntry entry)
            ? new RemotePrincipal(entry.Name, entry.Role)
            : null;
    }

    /// <summary>この要求を通してよいか（判定は <see cref="RemoteAuthRules.Decide"/>）。</summary>
    public RemoteAuthDecision Authorize(HttpContext ctx, RemoteRole required, bool write)
        => RemoteAuthRules.Decide(Resolve(ctx), required, write, HasClientHeader(ctx), _allowGuestRead);

    /// <summary><c>X-PRApp-Client: 1</c> が付いているか（序数一致）。</summary>
    public static bool HasClientHeader(HttpContext ctx)
        => string.Equals(ctx.Request.Headers[ClientHeaderName].ToString(), ClientHeaderValue, StringComparison.Ordinal);

    /// <summary>
    /// クエリの <c>token</c> が正しければ Admin のセッションを発行する。誤りなら null。
    /// </summary>
    public string? TryIssueSession(string? presentedToken)
        => RemoteApiRules.TokenEquals(_accessToken, presentedToken)
            ? _sessions.Issue(RemoteAuthRules.TokenPrincipalName, RemoteRole.Admin)
            : null;

    /// <summary>
    /// パスワードの照合を<b>プロセス全体で同時 1 本</b>に絞る門。
    ///
    /// <para>
    /// PBKDF2 の 60 万回は 1 回で数十 ms の CPU を使う。<b>未認証で叩ける経路</b>である以上、
    /// 並列に投げられると録画と同じ CPU をいくらでも奪える ── ここで直列にすれば、
    /// どれだけ同時に来ても消費は 1 コア分で頭打ちになる。
    /// </para>
    /// <para>
    /// <b><see langword="static"/> であることが仕様。</b> <see cref="RemoteAuth"/> は
    /// 設定を変えるたびに作り直されるので、実体に持たせると作り直しのたびに上限が増える。
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim PasswordGate = new(1, 1);

    /// <summary>
    /// 利用者名とパスワードで名乗る。合えばセッションを発行して ID と本人を返す。
    /// 合わなければ <see langword="null"/>（照合に掛かる時間は名前の存否で変わらない）。
    ///
    /// <para>
    /// <b>照合は <see cref="PasswordGate"/> の中だけで行う。</b> 待っているあいだに
    /// 呼び出し側が切れば <paramref name="ct"/> で打ち切る ── 断った要求のために
    /// 順番待ちの列を伸ばし続けない。
    /// </para>
    /// </summary>
    public async Task<(string SessionId, RemotePrincipal Principal)?> TryLoginAsync(
        string name, string password, CancellationToken ct)
    {
        await PasswordGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (RemoteAuthRules.Authenticate(_users, name, password) is not { } principal)
                return null;

            return (_sessions.Issue(principal.Name, principal.Role), principal);
        }
        finally
        {
            PasswordGate.Release();
        }
    }

    /// <summary>セッションを失効させる（ログアウト）。</summary>
    public void RemoveSession(string? sessionId) => _sessions.Remove(sessionId);

    /// <summary>
    /// 認証の失敗を記録する（<see cref="FailureLogInterval"/> に 1 行へ間引く）。
    /// <b>トークンも利用者名も書かない</b> ── activity.log は利用者が貼り付けて共有する種類のファイル。
    /// </summary>
    public void ReportFailure(HttpContext ctx)
    {
        // **単調時計を使う**（<see cref="Environment.TickCount64"/>）── 壁時計だと
        // 時刻合わせが後ろへ跳んだ瞬間に間引きが効かなくなる。
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastFailureLogMs);
        if (last != long.MinValue && now - last < (long)FailureLogInterval.TotalMilliseconds)
            return;
        if (Interlocked.CompareExchange(ref _lastFailureLogMs, now, last) != last)
            return;

        ActivityLog.Warn("remote.auth fail", $"remote={ctx.Connection.RemoteIpAddress}");
    }
}
