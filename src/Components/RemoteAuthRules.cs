using System;
using System.Collections.Generic;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 1 つの要求を通すかどうかの判定結果。<b>断り方が 3 通りある</b>のは、
/// 呼び出し側が「何を直せばよいか」を応答だけで判別できるようにするため。
/// </summary>
public enum RemoteAuthDecision
{
    /// <summary>通す。</summary>
    Allow,

    /// <summary>名乗れていない（401）。</summary>
    Unauthorized,

    /// <summary>名乗れてはいるが <c>X-PRApp-Client</c> が無い（403）。</summary>
    ClientHeaderRequired,

    /// <summary>名乗れてはいるが役割が足りない（403）。</summary>
    InsufficientRole,
}

/// <summary>
/// 認証を通った相手。<b>Bearer トークンは <c>Name="token"</c> の
/// <see cref="RemoteRole.Admin"/></b> ── トークンは元から「すべてできる秘密」であり、
/// 役割を足したことでその意味を弱めない。
/// </summary>
public readonly record struct RemotePrincipal(string Name, RemoteRole Role);

/// <summary>
/// リモート操作の<b>認可の純粋な規則</b>。
///
/// <para>
/// 使うのは ASP.NET Core 側（<c>RemoteAuth</c> / <c>AuthGate</c>）だけだが、
/// あちらは L1 テストプロジェクトから参照できない（共有フレームワークを
/// テストホストへ降ろさないため）。間違えると<b>LAN から誰でも操作できる</b>
/// 種類の欠陥になるので、<see cref="RemoteApiRules"/> や <see cref="RemoteUserRules"/> と
/// 同じ形でここへ置いて L1 で固定する。
/// </para>
/// </summary>
public static class RemoteAuthRules
{
    /// <summary>Bearer トークンで名乗った相手に与える名前。</summary>
    public const string TokenPrincipalName = "token";

    /// <summary>
    /// 利用者が見つからなかったときに 1 回だけ照合するダミーのハッシュ。
    ///
    /// <para>
    /// <b>形式・反復回数・salt と鍵の長さは本物と同じ</b>にしてある ── 見つからない場合だけ
    /// 即座に返すと、応答時間の差が「その名前は存在する」を答えてしまう
    /// （利用者名の総当たりが可能になる）。値そのものに意味は無い。
    /// </para>
    /// </summary>
    public static readonly string DummyPasswordHash = string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{RemoteUserRules.HashPrefix}${RemoteUserRules.Iterations}$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

    /// <summary>
    /// 要求を通してよいか。<b>この順序そのものが仕様である</b>:
    ///
    /// <list type="number">
    /// <item>未認証 ── ゲスト読み取りが許可されていて、要る役割が
    /// <see cref="RemoteRole.Viewer"/> で、書き込みでないなら通す。それ以外は
    /// <see cref="RemoteAuthDecision.Unauthorized"/>。</item>
    /// <item>書き込みなのに <c>X-PRApp-Client</c> が無い →
    /// <see cref="RemoteAuthDecision.ClientHeaderRequired"/>（CSRF 対策）。</item>
    /// <item>役割が足りない → <see cref="RemoteAuthDecision.InsufficientRole"/>。</item>
    /// <item>通す。</item>
    /// </list>
    ///
    /// <para>
    /// <b>資格を役割より先に見る。</b> ヘッダーを付け忘れただけの呼び出し元には
    /// 「名乗りは通っている」と分かる 403 を返す ── 401 に混ぜると、
    /// 資格を疑って探り直すことになる。
    /// </para>
    /// </summary>
    public static RemoteAuthDecision Decide(
        RemotePrincipal? principal, RemoteRole required, bool write, bool clientHeaderOk, bool allowGuestRead)
    {
        if (principal is not { } identified)
        {
            return allowGuestRead && required == RemoteRole.Viewer && !write
                ? RemoteAuthDecision.Allow
                : RemoteAuthDecision.Unauthorized;
        }

        if (write && !clientHeaderOk)
            return RemoteAuthDecision.ClientHeaderRequired;

        // 役割の強さは列挙の数値の順（Viewer < Operator < Admin）。
        if (identified.Role < required)
            return RemoteAuthDecision.InsufficientRole;

        return RemoteAuthDecision.Allow;
    }

    /// <summary>
    /// 利用者名（序数一致）とパスワードで照合する。合わなければ <see langword="null"/>。
    ///
    /// <para>
    /// <b>見つからなかった場合も <see cref="DummyPasswordHash"/> で 1 回照合する</b>
    /// ── PBKDF2 の 60 万回は応答時間に出るので、名前の存否で早さが変わると
    /// 利用者名を当てられる。
    /// </para>
    /// </summary>
    public static RemotePrincipal? Authenticate(
        IReadOnlyList<RemoteUserDefinition> users, string name, string password)
    {
        ArgumentNullException.ThrowIfNull(users);

        RemoteUserDefinition? found = null;
        if (!string.IsNullOrEmpty(name))
        {
            foreach (RemoteUserDefinition user in users)
            {
                if (user is not null && string.Equals(user.Name, name, StringComparison.Ordinal))
                {
                    found = user;
                    break;
                }
            }
        }

        if (found is null)
        {
            // 結果は捨てる。掛けたいのは時間だけである。
            _ = RemoteUserRules.Verify(password ?? "", DummyPasswordHash);
            return null;
        }

        return RemoteUserRules.Verify(password ?? "", found.PasswordHash)
            ? new RemotePrincipal(found.Name, found.Role)
            : null;
    }

    /// <summary>
    /// セッションの絶対期限が切れているか。<b>境界（同時刻）は切れている扱い</b>
    /// ── 期限を「その時刻まで有効」と読むと、時計の分解能ぶんだけ生き延びる窓が残る。
    /// </summary>
    public static bool IsExpired(DateTimeOffset entryExpires, DateTimeOffset now) => entryExpires <= now;
}
