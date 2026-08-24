using System.Security.Cryptography;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// リモート利用者のパスワードと名前の規則（<see cref="RemoteUserRules"/>）。
///
/// <para>
/// 利用側（編集ダイアログと ASP.NET Core の認証）はどちらも L1 から参照できないので、
/// <c>RemoteApiRules</c> と同じく規則そのものをここで固定する。
/// <b>間違えても静かに壊れる</b> ── 形式の取り違えは「誰も認証を通れない」か
/// 「壊れた値で例外が飛ぶ」のどちらかになり、どちらもビルドは通る。
/// </para>
/// </summary>
public class RemoteUserRulesTests
{
    [Fact]
    public void Verify_AcceptsTheSamePassword_AndRejectsAnother()
    {
        string stored = RemoteUserRules.HashPassword("correct horse");

        Assert.True(RemoteUserRules.Verify("correct horse", stored));
        Assert.False(RemoteUserRules.Verify("correct hors", stored));
        Assert.False(RemoteUserRules.Verify("", stored));
    }

    /// <summary>
    /// 形式は <c>pbkdf2-sha256$&lt;iter&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>。
    /// <b>保存された文字列そのものが契約</b>である（設定ファイルに残り、次の版が読む）。
    /// </summary>
    [Fact]
    public void HashPassword_WritesThePbkdf2Format()
    {
        string stored = RemoteUserRules.HashPassword("pw");
        string[] parts = stored.Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal(RemoteUserRules.HashPrefix, parts[0]);
        Assert.Equal(RemoteUserRules.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture), parts[1]);
        Assert.Equal(RemoteUserRules.SaltBytes, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(RemoteUserRules.HashBytes, Convert.FromBase64String(parts[3]).Length);
        Assert.True(RemoteUserRules.IsWellFormedHash(stored));
    }

    /// <summary>
    /// salt は毎回引き直す。同じパスワードで同じ文字列が出ると、
    /// <b>設定ファイルを見ただけで「この2人は同じパスワード」が分かる</b>。
    /// </summary>
    [Fact]
    public void HashPassword_UsesAFreshSaltEveryTime()
    {
        string first = RemoteUserRules.HashPassword("same");
        string second = RemoteUserRules.HashPassword("same");

        Assert.NotEqual(first, second);
        Assert.True(RemoteUserRules.Verify("same", first));
        Assert.True(RemoteUserRules.Verify("same", second));
    }

    /// <summary>
    /// 反復回数は<b>保存側の値</b>を使う ── <see cref="RemoteUserRules.Iterations"/> を
    /// 将来引き上げても、古い設定ファイルのハッシュが照合できなくなってはいけない。
    /// </summary>
    [Fact]
    public void Verify_UsesTheIterationCountThatWasStored()
    {
        const int OldIterations = 1000;
        byte[] salt = RandomNumberGenerator.GetBytes(RemoteUserRules.SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            "legacy", salt, OldIterations, HashAlgorithmName.SHA256, RemoteUserRules.HashBytes);
        string stored = $"{RemoteUserRules.HashPrefix}${OldIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

        Assert.NotEqual(OldIterations, RemoteUserRules.Iterations);
        Assert.True(RemoteUserRules.IsWellFormedHash(stored));
        Assert.True(RemoteUserRules.Verify("legacy", stored));
        Assert.False(RemoteUserRules.Verify("other", stored));
    }

    /// <summary>
    /// settings.json は手で編集できる。<b>壊れた値で例外を投げない</b>
    /// ── 投げると、1行の書き損じが起動や要求の処理ごと落とす。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("pbkdf2-sha256")]
    [InlineData("pbkdf2-sha256$600000$c2FsdA==")]                 // 区切りが足りない
    [InlineData("pbkdf2-sha256$600000$c2FsdA==$aGFzaA==$extra")]  // 区切りが多い
    [InlineData("scrypt$600000$c2FsdA==$aGFzaA==")]               // 別の方式
    [InlineData("pbkdf2-sha256$abc$c2FsdA==$aGFzaA==")]           // 反復回数が数値でない
    [InlineData("pbkdf2-sha256$0$c2FsdA==$aGFzaA==")]             // 反復回数が 0
    [InlineData("pbkdf2-sha256$-1$c2FsdA==$aGFzaA==")]            // 反復回数が負
    [InlineData("pbkdf2-sha256$600000$not base64!$aGFzaA==")]     // salt が Base64 でない
    [InlineData("pbkdf2-sha256$600000$c2FsdA==$not base64!")]     // ハッシュが Base64 でない
    [InlineData("pbkdf2-sha256$600000$$aGFzaA==")]                // salt が空
    [InlineData("pbkdf2-sha256$600000$c2FsdA==$")]                // ハッシュが空
    public void MalformedHashes_AreRejectedWithoutThrowing(string stored)
    {
        Assert.False(RemoteUserRules.IsWellFormedHash(stored));
        Assert.False(RemoteUserRules.Verify("pw", stored));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("a")]
    [InlineData("山田 太郎")]      // 途中の空白は使える
    [InlineData("a.b-c_d@example")]
    public void IsValidName_AcceptsOrdinaryNames(string name)
        => Assert.True(RemoteUserRules.IsValidName(name));

    [Theory]
    [InlineData("")]
    [InlineData(" alice")]         // 前の空白
    [InlineData("alice ")]         // 後ろの空白
    [InlineData("ali\tce")]        // 制御文字
    [InlineData("ali\nce")]
    [InlineData("host:8752")]      // ':' は activity.log の key=value と衝突する
    public void IsValidName_RejectsTheseNames(string name)
        => Assert.False(RemoteUserRules.IsValidName(name));

    /// <summary>長さの境界（<see cref="RemoteUserRules.MaxNameLength"/> ちょうどは通る）。</summary>
    [Fact]
    public void IsValidName_AllowsExactlyTheMaximumLength()
    {
        Assert.True(RemoteUserRules.IsValidName(new string('a', RemoteUserRules.MaxNameLength)));
        Assert.False(RemoteUserRules.IsValidName(new string('a', RemoteUserRules.MaxNameLength + 1)));
    }

    /// <summary>
    /// 反復回数の上限（<see cref="RemoteUserRules.MaxIterations"/>）。
    ///
    /// <para>
    /// <b>settings.json は手で編集できる。</b> 上限が無いと、大きな数を 1 つ書くだけで
    /// ログイン要求 1 本が何分も CPU を回すことになる ── 壊れた値と同じく
    /// 「不正な形式」として断る。<b>ちょうど上限は通す</b>（境界を両側から縛る）。
    /// </para>
    /// </summary>
    [Fact]
    public void IsWellFormedHash_RejectsAnIterationCountAboveTheCap()
    {
        string salt = Convert.ToBase64String(new byte[RemoteUserRules.SaltBytes]);
        string hash = Convert.ToBase64String(new byte[RemoteUserRules.HashBytes]);

        string atCap = $"{RemoteUserRules.HashPrefix}${RemoteUserRules.MaxIterations}${salt}${hash}";
        string overCap = $"{RemoteUserRules.HashPrefix}${RemoteUserRules.MaxIterations + 1}${salt}${hash}";

        Assert.True(RemoteUserRules.IsWellFormedHash(atCap));
        Assert.False(RemoteUserRules.IsWellFormedHash(overCap));
        // Verify も同じ判定を通る（上限超えは照合そのものを行わない）。
        Assert.False(RemoteUserRules.Verify("pw", overCap));
    }
}
