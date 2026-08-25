using System;
using System.Globalization;

namespace ProcessRecorderApp.Components;

/// <summary>DASH の配信経路が受け付ける 3 種類。</summary>
public enum DashRouteKind
{
    /// <summary>MPD（<c>manifest.mpd</c>）。</summary>
    Manifest,

    /// <summary>Init セグメント（<see cref="DashManifest.InitializationTemplate"/>）。</summary>
    Init,

    /// <summary>メディアセグメント（<see cref="DashManifest.MediaTemplate"/> の <c>$Time$</c> 展開形）。</summary>
    Media,
}

/// <summary>
/// 配信経路の末尾 1 セグメント（<c>/api/recorders/{id}/dash/{file}</c> の <c>{file}</c>）を
/// 読む純関数。
///
/// <para>
/// <b>名前の正本は <see cref="DashManifest"/> 側にある。</b> MPD が書く
/// <c>initialization</c> / <c>media</c> のテンプレートをそのまま分解して照合するので、
/// テンプレートを変えればここも一緒に動く ── <c>"seg-"</c> と <c>".m4s"</c> を
/// 2 か所に持つと、片方だけ変えた日に MPD の指す URL が 404 になる。
/// </para>
/// <para>
/// <b>受け付けるのは 10 進数字だけ。</b> <c>ulong.TryParse</c> は既定で符号と空白を
/// 通すので、書式（<see cref="NumberStyles.None"/>）だけでなく文字も自分で見る
/// ── <c>seg-+1.m4s</c> と <c>seg-1.m4s</c> が同じセグメントを指すと、
/// 同じものに 2 つの URL がある状態になる。
/// </para>
/// </summary>
public static class DashRoutes
{
    /// <summary>MPD のファイル名。</summary>
    public const string ManifestFile = "manifest.mpd";

    /// <summary><c>$Time$</c> の識別子置換（DASH の仕様が決めている綴り）。</summary>
    private const string TimeIdentifier = "$Time$";

    private static readonly string MediaPrefix =
        DashManifest.MediaTemplate[..DashManifest.MediaTemplate.IndexOf(TimeIdentifier, StringComparison.Ordinal)];

    private static readonly string MediaSuffix =
        DashManifest.MediaTemplate[
            (DashManifest.MediaTemplate.IndexOf(TimeIdentifier, StringComparison.Ordinal) + TimeIdentifier.Length)..];

    /// <summary>
    /// <paramref name="file"/> を読む。<b>照合はすべて序数（大文字小文字を区別する）</b>。
    /// </summary>
    /// <param name="file">経路の末尾 1 セグメント。</param>
    /// <param name="kind">読めた種別。</param>
    /// <param name="time">
    /// <see cref="DashRouteKind.Media"/> のときの <c>tfdt</c> の刻み（他では 0）。
    /// </param>
    /// <returns>読めたら true。読めなければ false（呼び出し側は 404 にする）。</returns>
    public static bool TryParse(string file, out DashRouteKind kind, out ulong time)
    {
        kind = DashRouteKind.Manifest;
        time = 0;

        if (string.IsNullOrEmpty(file))
            return false;

        if (string.Equals(file, ManifestFile, StringComparison.Ordinal))
            return true;

        if (string.Equals(file, DashManifest.InitializationTemplate, StringComparison.Ordinal))
        {
            kind = DashRouteKind.Init;
            return true;
        }

        if (!file.StartsWith(MediaPrefix, StringComparison.Ordinal)
            || !file.EndsWith(MediaSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        int start = MediaPrefix.Length;
        int length = file.Length - MediaSuffix.Length - start;
        if (length <= 0)
            return false;

        ReadOnlySpan<char> digits = file.AsSpan(start, length);
        foreach (char character in digits)
        {
            if (character is < '0' or > '9')
                return false;
        }

        // 桁は全部数字だと分かっているので、ここで false になるのは ulong の溢れだけ。
        if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out time))
            return false;

        kind = DashRouteKind.Media;
        return true;
    }
}
