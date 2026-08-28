using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 録画 1 本のメタデータ。録画ファイルと同じフォルダーの
/// <c>&lt;録画ファイル名&gt;.json</c>（例 <c>20260828_101500_cam1.mp4.json</c>）に置く。
///
/// <para>
/// <b>best-effort で、無くても動く。</b> 書き込みは録画パイプラインの外側で、
/// 失敗してもログだけを残す。読む側は <see langword="null"/> を必ず扱えること
/// ── 以前に録った分・別の道具が置いた分には sidecar が無い。
/// </para>
/// <para>
/// <b>一度書いたら上書きしない。</b> 録画の完了時刻とその時点の caps を写したもので、
/// 後から書き直して意味が変わるものではない。
/// </para>
/// </summary>
/// <param name="Version">形式の版。現在は <see cref="RecordingSidecar.CurrentVersion"/> のみ。</param>
/// <param name="Recorder">録画したレコーダーの名前（ファイル名テンプレートの <c>{Name}</c> と同じ値）。</param>
/// <param name="StartTime">録画開始時刻。</param>
/// <param name="EndTime">排出が完了した時刻。</param>
/// <param name="DurationMs">
/// <paramref name="EndTime"/> − <paramref name="StartTime"/>。
/// メディアの尺そのものではなく、録画していた実時間である。
/// </param>
/// <param name="Trigger">開始理由。単発録画では <see langword="null"/>、常時録画では <c>continuous</c>。</param>
/// <param name="Width">映像の幅（caps 未確定なら <see langword="null"/>）。</param>
/// <param name="Height">映像の高さ（同上）。</param>
/// <param name="Fps">フレームレート（分数を割ったもの。<c>0/1</c> なら <see langword="null"/>）。</param>
public sealed record RecordingSidecar(
    int Version,
    string Recorder,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    long? DurationMs,
    string? Trigger,
    int? Width,
    int? Height,
    double? Fps)
{
    /// <summary>現在の形式の版。</summary>
    public const int CurrentVersion = 1;

    /// <summary>sidecar のファイル名に足す拡張子。</summary>
    public const string Extension = ".json";

    /// <summary>サムネイルのファイル名に足す拡張子。</summary>
    public const string ThumbnailExtension = ".png";

    /// <summary>読み込む上限。壊れた・別物の巨大なファイルを丸ごとメモリへ載せない。</summary>
    private const long MaxReadBytes = 64 * 1024;

    /// <summary><paramref name="recordingPath"/> に対応する sidecar のパス。</summary>
    public static string PathFor(string recordingPath) => recordingPath + Extension;

    /// <summary><paramref name="recordingPath"/> に対応するサムネイルのパス。</summary>
    public static string ThumbnailPathFor(string recordingPath) => recordingPath + ThumbnailExtension;

    /// <summary>
    /// sidecar を読む。無い・読めない・壊れている・版が違うはすべて
    /// <see langword="null"/> に畳む ── 呼び出し側はフォールバックを必ず持っている。
    /// </summary>
    public static RecordingSidecar? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || MaxReadBytes < info.Length)
                return null;

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var sidecar = JsonSerializer.Deserialize(stream, RecordingSidecarJsonContext.Default.RecordingSidecar);
            if (sidecar is null || sidecar.Version != CurrentVersion || sidecar.Recorder is null)
                return null;

            return sidecar;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// sidecar を書く。<b>一時ファイルへ書いてから置き換える</b>
    /// ── 読む側は録画中のフォルダーを走査しているので、途中まで書けた JSON を
    /// 見せないため。例外は呼び出し側で握り潰す（録画そのものを止めない）。
    /// </summary>
    public static void Write(string path, RecordingSidecar sidecar)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sidecar);

        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, sidecar, RecordingSidecarJsonContext.Default.RecordingSidecar);

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { /* 後始末の失敗で元の例外を隠さない */ }
            throw;
        }
    }

    /// <summary>
    /// 録画に付くサムネイル（<c>&lt;録画ファイル名&gt;.png</c>）を書く。
    ///
    /// <para>
    /// <b>既に在れば何もしない。</b> サムネイルは録画ファイル名が決まった直後の
    /// 1 枚を写したもので、後から撮り直して意味が変わるものではない
    /// （<see cref="Write"/> と同じ規律）。存在確認と <c>Move</c> の間の競合は許容する
    /// ── 撮る側は 1 本の録画につき 1 回しか要求しない。
    /// </para>
    /// <para>
    /// <b>一時ファイルへ書いてから置き換える。</b> 読む側（索引・配信）は録画中の
    /// フォルダーを走査しているので、途中まで書けた PNG を見せない。
    /// 例外は呼び出し側で握り潰す（録画そのものを止めない）。
    /// </para>
    /// </summary>
    public static void WriteThumbnail(string recordingPath, ThumbnailImage image)
    {
        ArgumentNullException.ThrowIfNull(recordingPath);
        ArgumentNullException.ThrowIfNull(image);

        string path = ThumbnailPathFor(recordingPath);
        if (File.Exists(path))
            return;

        // 一時ファイルは同じフォルダーに置く（Move を同一ボリューム内の置換にするため）。
        // 名前は毎回一意にする ── 索引が拾わない拡張子なので、残っても Cleanup の
        // 対象外のまま害が無い。
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                PngWriter.Write(stream, image.Width, image.Height, image.Rgb24);

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { /* 後始末の失敗で元の例外を隠さない */ }
            throw;
        }
    }
}

/// <summary>
/// sidecar の JSON（ソース生成）。Native AOT ではリフレクション経路が使えないので、
/// この文脈を通してのみ読み書きする。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(RecordingSidecar))]
public sealed partial class RecordingSidecarJsonContext : JsonSerializerContext;
