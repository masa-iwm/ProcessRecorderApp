using System.Collections.Generic;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// ライブ画質の姿（<c>GET /api/recorders/{id}/preview/qualities</c> と
/// <c>POST /api/recorders/{id}/preview/quality</c> の応答）。
/// </summary>
/// <param name="Current">
/// 選ばれている id。override が無ければ <c>custom</c>（＝レコーダー設定の 4 値をそのまま配信）。
/// </param>
/// <param name="Source">
/// 最後に読めたソースの形。<b>まだ 1 枚も届いていなければ null</b>
/// （レコーダーが動いていないときも null）。
/// </param>
/// <param name="Effective">
/// <b>いま実際に配信している</b> 4 値。指示を変えても mux を組み直すまでは古いままで、
/// 配信していなければ null。
/// </param>
/// <param name="Qualities">選べる項目（<b>末尾は必ず <c>custom</c></b>）。</param>
public sealed record PreviewQualityStateDto(
    string Current,
    PreviewSourceDto? Source,
    PreviewEffectiveQualityDto? Effective,
    IReadOnlyList<PreviewQualityOptionDto> Qualities);

/// <summary>プレビュー枝に届いているソースの形。</summary>
/// <param name="Width">幅(px)。</param>
/// <param name="Height">高さ(px)。</param>
/// <param name="Fps">フレームレート(fps)。読めていなければ 0。</param>
public sealed record PreviewSourceDto(int Width, int Height, int Fps);

/// <summary>
/// 選べる画質 1 件。<b>幅・高さは解決済み</b>（プリセットはソースの縦横比と高さで縮む）。
/// </summary>
/// <param name="Id"><c>1080p</c> / <c>720p</c> / <c>480p</c> / <c>360p</c> / <c>custom</c>。</param>
/// <param name="Label">表示名。</param>
/// <param name="Width">幅(px)。</param>
/// <param name="Height">高さ(px)。</param>
/// <param name="Fps">フレームレート(fps)。</param>
/// <param name="BitrateKbps">ビットレート(kbit/sec)。</param>
public sealed record PreviewQualityOptionDto(
    string Id, string Label, int Width, int Height, int Fps, int BitrateKbps);

/// <summary>
/// いま配信している 4 値と、その id。
///
/// <para>
/// <b><see cref="PreviewQualityOptionDto"/> を使い回していない。</b> 封筒の
/// <c>DefaultIgnoreCondition</c> は <c>Never</c> なので、使い回すと選択肢でしか意味の無い
/// <c>label</c> が実効値にも必ず出る。
/// </para>
/// </summary>
/// <param name="Id">この連続体を組んだときの id。</param>
/// <param name="Width">幅(px)。</param>
/// <param name="Height">高さ(px)。</param>
/// <param name="Fps">フレームレート(fps)。</param>
/// <param name="BitrateKbps">ビットレート(kbit/sec)。</param>
public sealed record PreviewEffectiveQualityDto(
    string Id, int Width, int Height, int Fps, int BitrateKbps);

/// <summary>
/// <c>POST /api/recorders/{id}/preview/quality</c> の本文。
/// </summary>
/// <param name="Id">
/// 切り替える先の id。<b>経路が <c>PreviewQualityPresets.IsValidId</c> で検査する</b>ので、
/// 欠けていても知らない値でも 400 になる。
/// </param>
public sealed record PreviewQualityRequestDto(string? Id);
