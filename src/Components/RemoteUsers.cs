using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace ProcessRecorderApp.Components;

/// <summary>
/// リモート利用者の役割。<b>数値の順序がそのまま権限の強さ</b>
/// （<c>Viewer &lt; Operator &lt; Admin</c>）。
/// </summary>
public enum RemoteRole
{
    /// <summary>読み取りだけ。</summary>
    Viewer = 0,

    /// <summary>録画の開始・停止と変数の書き換え。</summary>
    Operator = 1,

    /// <summary>設定の変更を含むすべて。</summary>
    Admin = 2,
}

/// <summary>
/// settings.json に永続化するリモート利用者 1 人分。
/// <see cref="PasswordHash"/> は <see cref="RemoteUserRules"/> の形式のみを受け付ける
/// （平文は保持しない）。
/// </summary>
public sealed class RemoteUserDefinition
{
    /// <summary>利用者名。規約は <see cref="RemoteUserRules.IsValidName"/>。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// パスワードのハッシュ（<c>pbkdf2-sha256$&lt;iter&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>）。
    /// </summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// 役割。<b>settings.json には名前で書く</b> ── 数値だと手で開いても意味が読めず、
    /// 宣言の並びを変えた瞬間に既存ファイルの意味が黙って変わる。
    /// 総称版の変換器を使うのは、非総称版が実行時リフレクションを要求して
    /// Native AOT で使えないため。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RemoteRole>))]
    public RemoteRole Role { get; set; } = RemoteRole.Viewer;
}

/// <summary>
/// リモート利用者の<b>純粋な規則</b>（パスワードのハッシュ化・照合・名前の妥当性）。
///
/// <para>
/// 利用側は WinUI アプリ（編集ダイアログ）と ASP.NET Core（認証）の両方にあり、
/// どちらも L1 テストプロジェクトから参照できない。間違えると
/// <b>LAN から誰でも操作できる</b>種類の欠陥になるので、<c>RemoteApiRules</c> と
/// 同じ形でここへ置いて L1 で固定する。
/// </para>
/// </summary>
public static class RemoteUserRules
{
    /// <summary>ハッシュ文字列の先頭に置く方式名。</summary>
    public const string HashPrefix = "pbkdf2-sha256";

    /// <summary>新しく作るハッシュの反復回数。</summary>
    public const int Iterations = 600_000;

    /// <summary>
    /// 保存済みのハッシュで受け付ける反復回数の上限（<see cref="Iterations"/> の 4 倍）。
    ///
    /// <para>
    /// <b>上限が無いと、settings.json に大きな数を書くだけでログイン 1 回が数分の
    /// CPU になる</b> ── 手で編集できるファイルの値をそのまま PBKDF2 へ渡すのだから、
    /// 壊れた値と同じく「不正な形式」として扱う。4 倍あるのは、
    /// <see cref="Iterations"/> を将来引き上げても古いハッシュが読めるようにするための余地。
    /// </para>
    /// </summary>
    public const int MaxIterations = Iterations * 4;

    /// <summary>salt の長さ（バイト）。</summary>
    public const int SaltBytes = 16;

    /// <summary>導出鍵の長さ（バイト）。</summary>
    public const int HashBytes = 32;

    /// <summary>登録できる利用者の上限。</summary>
    public const int MaxUsers = 64;

    /// <summary>利用者名の長さの上限（文字）。</summary>
    public const int MaxNameLength = 64;

    /// <summary>ハッシュ文字列の区切り。</summary>
    private const char Separator = '$';

    /// <summary>区切りで分けたときの要素数（方式名・反復回数・salt・ハッシュ）。</summary>
    private const int PartCount = 4;

    /// <summary>
    /// パスワードをハッシュ化する。形式は
    /// <c>pbkdf2-sha256$&lt;iter&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>
    /// （Base64 は標準・パディング有り）。salt は
    /// <see cref="RandomNumberGenerator"/> から取るので、同じパスワードでも毎回別の文字列になる。
    /// </summary>
    public static string HashPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return string.Create(CultureInfo.InvariantCulture,
            $"{HashPrefix}{Separator}{Iterations}{Separator}{Convert.ToBase64String(salt)}{Separator}{Convert.ToBase64String(hash)}");
    }

    /// <summary>
    /// パスワードを保存済みのハッシュと照合する。
    ///
    /// <para>
    /// <b>反復回数は保存側の値を使う</b> ── <see cref="Iterations"/> を将来引き上げても、
    /// 古い設定ファイルのハッシュが照合できなくなってはいけない。ただし
    /// <see cref="MaxIterations"/> を超える値は不正な形式として false にする。
    /// </para>
    /// <para>
    /// <b>不正な形式では例外を投げず false を返す</b>。settings.json は手で編集できるので、
    /// 壊れた値は「照合に失敗した」として扱う（起動や要求の処理を落とさない）。
    /// 比較は <see cref="CryptographicOperations.FixedTimeEquals"/>。
    /// </para>
    /// </summary>
    public static bool Verify(string password, string storedHash)
    {
        if (password is null || !TryParse(storedHash, out int iterations, out byte[] salt, out byte[] expected))
            return false;

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// 利用者名として使えるか。空でない・<see cref="MaxNameLength"/> 以内・
    /// 前後に空白が無い・制御文字を含まない・<c>:</c> を含まない
    /// （<c>activity.log</c> の <c>key=value</c> と衝突させないため）。
    /// </summary>
    public static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            return false;

        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
            return false;

        foreach (char c in name)
        {
            if (char.IsControl(c) || c == ':')
                return false;
        }

        return true;
    }

    /// <summary>保存済みのハッシュが <see cref="HashPassword"/> の形式になっているか。</summary>
    public static bool IsWellFormedHash(string storedHash)
        => TryParse(storedHash, out _, out _, out _);

    /// <summary>ハッシュ文字列を分解する。形式に合わなければ false（例外は投げない）。</summary>
    private static bool TryParse(string storedHash, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];

        if (string.IsNullOrEmpty(storedHash))
            return false;

        string[] parts = storedHash.Split(Separator);
        if (parts.Length != PartCount)
            return false;

        if (!string.Equals(parts[0], HashPrefix, StringComparison.Ordinal))
            return false;

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations <= 0 || MaxIterations < iterations)
        {
            return false;
        }

        if (!TryFromBase64(parts[2], out salt) || salt.Length == 0)
            return false;

        if (!TryFromBase64(parts[3], out hash) || hash.Length == 0)
            return false;

        return true;
    }

    /// <summary>Base64 を復号する。復号できなければ false（例外は投げない）。</summary>
    private static bool TryFromBase64(string text, out byte[] bytes)
    {
        byte[] buffer = new byte[((text.Length + 3) / 4) * 3];
        if (Convert.TryFromBase64String(text, buffer, out int written))
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }
}
