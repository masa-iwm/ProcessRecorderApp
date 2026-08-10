using System;
using System.Globalization;
using System.Text;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// <b>常時録画の枝（<c>tee</c> の 3 本目）を組み立てる純粋関数。</b>
///
/// <para>
/// イベント録画の枝と <b>同じ <c>tee</c> を共有する</b> ── キャプチャは 1 回で済み、
/// 2 本目のデバイスを開けないソース（カメラ等）でも常時録画ができる。
/// 枝の終端は <c>appsink</c> で、そこから先（セグメント単位のファイル書き出し）は
/// C# 側（<c>ContinuousRecorder</c>）が持つ。<c>splitmuxsink</c> は使わない
/// ── 同梱ランタイムに <c>libgstmultifile.dll</c> が無く、
/// 分割そのものは既存の <c>appsrc ! mp4mux ! filesink</c> の作り直しで足りるため。
/// </para>
/// </summary>
public static class ContinuousBranch
{
    /// <summary>常時録画の枝の終端 <c>appsink</c> の名前。</summary>
    public const string AppSinkName = "cont";

    /// <summary>フレームレートを変えるために必要な要素（<b>同梱ランタイムには入っていない</b>）。</summary>
    public const string VideorateFactory = "videorate";

    /// <summary>
    /// <b>常時録画の枝の <c>queue</c>。<c>leaky=downstream</c> は意図的。</b>
    ///
    /// <para>
    /// エンコーダー枝（イベント録画）が素の <c>queue</c> なのは、詰まったときに
    /// <c>tee</c> を止めて<b>録画を優先する</b>のが正しい背圧だから。
    /// 常時録画の枝は逆で、<b>常時録画がイベント録画を道連れにしてはならない</b>
    /// ── 詰まったら常時側がフレームを捨てる。
    /// </para>
    /// <para>
    /// <c>max-size-bytes=0 / max-size-time=0</c> はプレビュー枝と同じ理由で外す。
    /// <c>queue</c> の既定 <c>max-size-bytes=10485760</c> は解像度が上がると
    /// 実質「フレーム数の上限」に化けるので、解像度に依存させない。
    /// </para>
    /// </summary>
    public const string Queue =
        "queue leaky=downstream max-size-buffers=8 max-size-bytes=0 max-size-time=0";

    /// <summary>
    /// セグメント 1 本ぶんの書き出しパイプライン。
    ///
    /// <para>
    /// <b><c>faststart=true</c> は付けない。</b> faststart は EOS のあとにファイル全体を
    /// 書き直して <c>moov</c> を先頭へ移すもので、数分ごとの切り替えでそれをやると
    /// 分割のたびに I/O が跳ねる。常時録画のセグメントは書庫であって、
    /// 先頭からのシークの即応性は要らない。
    /// </para>
    /// </summary>
    public const string SegmentWriterPipeline =
        "appsrc format=time name=src ! h264parse ! mp4mux name=mux ! filesink name=file";

    /// <summary>
    /// フレームレートの上書きが指定されているか（＝<c>videorate</c> が要るか）。
    /// 空なら枝に <c>videorate</c> を入れない ── <c>videorate</c> は同梱ランタイムに入れてあるが、
    /// 利用者が別途入れた GStreamer には無いことがある。無条件に書くと、フレームレートを
    /// 変えていない構成まで巻き添えで初期化に失敗する。
    /// </summary>
    public static bool RequiresVideorate(string? framerate)
        => !string.IsNullOrWhiteSpace(framerate);

    /// <summary><c>1280x720</c> 形式の解像度を読む。読めなければ false（＝上書きしない）。</summary>
    public static bool TryParseResolution(string? resolution, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrWhiteSpace(resolution))
            return false;

        string[] parts = resolution.Trim().Split(['x', 'X'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && 0 < width && 0 < height;
    }

    /// <summary>
    /// 枝の文字列を組み立てる。<paramref name="encoder"/> が空なら空文字を返す（＝枝を足さない）。
    /// </summary>
    /// <param name="type">録画種別（メモリ機能の書き方が変わる）。</param>
    /// <param name="encoder">常時録画のエンコーダー起動文字列。</param>
    /// <param name="needsSystemMemory">エンコーダーがシステムメモリ入力を要求するか。</param>
    /// <param name="framerate">上書きするフレームレート（<c>5/1</c>）。空なら上書きしない。</param>
    /// <param name="resolution">上書きする解像度（<c>1280x720</c>）。空なら上書きしない。</param>
    /// <remarks>
    /// <para>
    /// <b>解像度はソースの caps ではなく変換段で効かせる。</b>
    /// <c>d3d12screencapturesrc</c> の src caps はモニター解像度に固定されているので、
    /// ソース側 capsfilter で幅・高さを指定すると交渉に失敗する。
    /// <c>d3d12convert</c> / <c>videoscale</c> はどちらも同梱ランタイムに入っている。
    /// </para>
    /// <para>
    /// <b><c>videorate</c> の直後の capsfilter でメモリ機能を書き落とさないこと。</b>
    /// <c>D3d12</c> 経路で <c>video/x-raw, framerate=X</c> と書くとシステムメモリを
    /// 要求してしまい、上流に <c>d3d12download</c> が無いので
    /// <c>could not link</c> で初期化ごと失敗する。<c>videorate</c> の pad テンプレートは
    /// <c>video/x-raw(ANY)</c> なので、D3D12 メモリのまま通せる（実測）。
    /// </para>
    /// <para>
    /// <b><c>appsink</c> に <c>async=false</c> を付けるのは必須。</b> sink が preroll を待つと、
    /// 低いフレームレートのときこの枝がパイプライン全体の <c>PLAYING</c> 到達を握る
    /// ── <c>EventRecorder.PlayingStateTimeoutMs</c> が名指ししている唯一の誤検出形
    /// （低 fps × 出力の遅いエンコーダー）が、常時録画そのものである。
    /// </para>
    /// </remarks>
    public static string Build(
        EventRecordingType type,
        string encoder,
        bool needsSystemMemory,
        string? framerate,
        string? resolution)
    {
        if (string.IsNullOrWhiteSpace(encoder))
            return "";

        bool d3d12 = type == EventRecordingType.D3d12;
        string memory = d3d12 ? "video/x-raw(memory:D3D12Memory)" : "video/x-raw";

        var sb = new StringBuilder();
        sb.Append("t. ! ").Append(Queue).Append(" ! ");

        if (RequiresVideorate(framerate))
            sb.Append("videorate ! ").Append(memory)
              .Append(", framerate=").Append(framerate!.Trim()).Append(" ! ");

        bool hasSize = TryParseResolution(resolution, out int width, out int height);
        if (d3d12)
        {
            if (hasSize)
                sb.Append("d3d12convert ! ").Append(memory).Append(", format=NV12")
                  .Append(", width=").Append(width.ToString(CultureInfo.InvariantCulture))
                  .Append(", height=").Append(height.ToString(CultureInfo.InvariantCulture)).Append(" ! ");
            if (needsSystemMemory)
                sb.Append("d3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert ! ");
        }
        else
        {
            if (hasSize)
                sb.Append("videoscale ! video/x-raw")
                  .Append(", width=").Append(width.ToString(CultureInfo.InvariantCulture))
                  .Append(", height=").Append(height.ToString(CultureInfo.InvariantCulture)).Append(" ! ");
            sb.Append("videoconvert ! ");
        }

        // h264parse config-interval=-1 と alignment=au はイベント枝と同じ理由で必須。
        // 前者は全 IDR の直前にパラメータセットを再挿入する（セグメントは任意の IDR から
        // 始まるので、これが無いと 2 本目以降のセグメントが再生できない）。
        // 後者は「1 バッファ＝1 フレーム」を保つため（nal 揃えだと PTS の扱いが崩れる）。
        sb.Append(encoder)
          .Append(" ! h264parse config-interval=-1 ! ")
          .Append("video/x-h264, stream-format=byte-stream, alignment=au, profile=main ! ")
          .Append("appsink name=").Append(AppSinkName).Append(" async=false");

        return sb.ToString();
    }
}
