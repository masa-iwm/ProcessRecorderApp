using System;
using System.Threading;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// 書き込み要求の認証と、ブラウザ用のセッション発行。
///
/// <para>
/// <b>読み取り（GET）は認証しない。</b> 画面の内容が LAN に見えることは利用者の決定であり、
/// <c>&lt;video&gt;</c> や <c>EventSource</c> がヘッダーを付けられない以上、
/// 読み取りに Bearer を要求すると成立しない。守りは「機能オフ既定」「トークン」
/// 「Firewall」の 3 つが担う。
/// </para>
/// <para>
/// <b>書き込みの判定順は ① 資格（Bearer か Cookie）② クライアントヘッダー。</b>
/// 資格が無ければ 401、資格はあるが <c>X-PRApp-Client: 1</c> が無ければ 403 と、
/// <b>状態が「誰であるか」と「どこから来たか」で分かれる</b> ── 資格の有無を
/// 先に答えるので、ヘッダーを忘れた呼び出し側は 403 を見て
/// 「トークンは合っている」と分かる。
/// </para>
/// <para>
/// <b>ヘッダーの検査は CSRF 対策である。</b> 「他所のページが仕込んだフォーム送信」は
/// Cookie を運べてもカスタムヘッダーを付けられない
/// （CORS ミドルウェアを入れないので、付けようとすればプリフライトで止まる）。
/// そのため<b>正しい Cookie でもヘッダーが無ければ通さない</b>。
/// </para>
/// </summary>
internal sealed class RemoteAuth(string accessToken, SessionStore sessions)
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

    private readonly SessionStore _sessions = sessions;
    private readonly string _accessToken = accessToken;
    private long _lastFailureLogMs = long.MinValue;

    /// <summary>認証の判定結果。</summary>
    public enum Decision
    {
        /// <summary>通す。</summary>
        Allow,

        /// <summary>クライアントヘッダーが無い（403）。</summary>
        MissingClientHeader,

        /// <summary>資格が無いか間違っている（401）。</summary>
        Unauthorized,
    }

    /// <summary>書き込み要求を通してよいか。</summary>
    public Decision AuthorizeWrite(HttpContext ctx)
    {
        if (!HasCredentials(ctx))
            return Decision.Unauthorized;

        if (!string.Equals(ctx.Request.Headers[ClientHeaderName].ToString(), ClientHeaderValue, StringComparison.Ordinal))
            return Decision.MissingClientHeader;

        return Decision.Allow;
    }

    /// <summary>Bearer か セッション Cookie の<b>どちらか</b>が正しいか。</summary>
    private bool HasCredentials(HttpContext ctx)
    {
        string authorization = ctx.Request.Headers.Authorization.ToString();
        if (0 < authorization.Length)
        {
            const string Prefix = "Bearer ";
            if (authorization.StartsWith(Prefix, StringComparison.Ordinal)
                && RemoteApiRules.TokenEquals(_accessToken, authorization[Prefix.Length..]))
            {
                return true;
            }
        }

        // 誤った Bearer でも Cookie は見る ── ブラウザは Cookie を自動で送るので、
        // 手で付けた古い Authorization が同居しうる。
        return _sessions.Contains(ctx.Request.Cookies[CookieName]);
    }

    /// <summary>クエリの <c>token</c> が正しければセッションを発行する。誤りなら null。</summary>
    public string? TryIssueSession(string? presentedToken)
        => RemoteApiRules.TokenEquals(_accessToken, presentedToken) ? _sessions.Issue() : null;

    /// <summary>
    /// 認証の失敗を記録する（<see cref="FailureLogInterval"/> に 1 行へ間引く）。
    /// <b>トークンは絶対に書かない</b> ── activity.log は利用者が貼り付けて共有する種類のファイル。
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
