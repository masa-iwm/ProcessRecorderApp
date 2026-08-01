using System;
using System.IO;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 設定に書かれたディレクトリ指定を絶対パスへ解決する。
///
/// <para>
/// <b>基準は実行ファイルのあるディレクトリ</b>で、プロセスのカレントディレクトリではない。
/// 常駐ワーカーは<b>最初に起動したシェルのカレントディレクトリをプロセス寿命ぶん引きずる</b>
/// （<c>SingleInstanceManager.Launcher</c> は <c>WorkingDirectory</c> を指定していない）ため、
/// カレントディレクトリを基準にすると<b>誰がどこから起動したかで出力先が変わる</b>。
/// 2回目以降の CLI 実行は既存のワーカーへ転送されるだけなので、
/// 「さっきと同じ場所で叩いたのに前と違うところに出る」という形で表面化する。
/// </para>
/// <para>
/// <c>AppContext.BaseDirectory</c> ではなく <see cref="Environment.ProcessPath"/> を使うのは
/// 単一ファイル発行のため（そちらは展開先の一時ディレクトリを指す）。
/// </para>
/// </summary>
public static class AppDirectories
{
    /// <summary>相対パスの基準（実行ファイルのあるディレクトリ）。</summary>
    public static string BaseDirectory
        => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// 空欄なら <paramref name="baseDirectory"/> そのもの、相対パスならそこからの相対、
    /// 絶対パスならそのまま。<b>純粋関数</b>（実在確認はしない）。
    /// </summary>
    public static string ResolveOrBase(string? value, string baseDirectory)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        string full = trimmed.Length == 0
            ? Path.GetFullPath(baseDirectory)
            : Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(baseDirectory, trimmed));

        // 末尾の区切りを落とす。`D:\videos` と `D:\videos\` は同じ指定であるべきで、
        // ここで揃えておかないと、この値を組み立てに使う側（活動ログの表示や
        // 自動削除の「root は消さない」判定）でパス比較が食い違う。
        // ルート（`D:\`）は TrimEndingDirectorySeparator が落とさない。
        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <inheritdoc cref="ResolveOrBase(string?, string)"/>
    public static string ResolveOrBase(string? value) => ResolveOrBase(value, BaseDirectory);

    /// <summary>
    /// <see cref="ResolveOrBase(string?, string)"/> と同じだが、<b>空欄は空欄のまま</b>返す。
    /// 「指定が無ければ何もしない」設定（<c>GST_DEBUG_DUMP_DOT_DIR</c> など）向け。
    /// </summary>
    public static string? ResolveOptional(string? value, string baseDirectory)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? value : ResolveOrBase(trimmed, baseDirectory);
    }

    /// <inheritdoc cref="ResolveOptional(string?, string)"/>
    public static string? ResolveOptional(string? value) => ResolveOptional(value, BaseDirectory);
}
