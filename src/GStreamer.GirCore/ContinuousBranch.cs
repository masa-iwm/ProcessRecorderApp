using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 常時録画の枝の組み立て結果。
///
/// <para>
/// <b>捨てた上書きを黙って捨てない。</b> 設定が効いていないことは画面からは分からないので、
/// 理由を <see cref="DroppedOverride"/> として返し、呼び出し側が
/// <c>ContinuousLastError</c> と <c>activity.log</c> へ出す。
/// </para>
/// </summary>
/// <param name="Branch">tee に足す枝の文字列。空なら枝を足さない。</param>
/// <param name="DroppedOverride">効かせられなかった上書きとその理由（無ければ null）。</param>
/// <param name="AppliesResolution">
/// 枝の中で拡縮するか。<b>真のときは <c>tee</c> の手前も固定しなければならない</b>
/// ── D3d12 経路の <c>d3d12upload ! d3d12convert</c> は拡縮できるので、
/// 固定しないと枝の要求を吸収して<b>プレビューとイベント録画まで縮める</b>（実測）。
/// </param>
public sealed record ContinuousBranchPlan(string Branch, string? DroppedOverride, bool AppliesResolution);

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
    /// <summary>
    /// フレームレート変換の<b>要素名</b>。<c>ElementFactory.Find</c> と
    /// 同梱台帳の検査に使うので、<b>プロパティを付けてはいけない</b>
    /// ── 付けると「この GStreamer には videorate drop-only=true が無い」と
    /// 誤って報告し、常時録画が丸ごと無効になる。パイプラインへ出す文字列は
    /// <see cref="VideorateElement"/> の方。
    /// </summary>
    public const string VideorateFactory = "videorate";

    /// <summary>
    /// 常時枝へ出すフレームレート変換。<c>drop-only=true</c> は<b>複製をさせない</b>ため
    /// （常時録画は下げる方向にしか使わず、ソースが止まったとき複製フレームを書庫へ
    /// 入れる方が困る）。<b>本線のフレームレート低下は これでは直らない</b>（実測）。
    /// </summary>
    public const string VideorateElement = VideorateFactory + " drop-only=true";

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

    /// <summary>
    /// <c>SrcPipeline</c> の caps が幅・高さを固定しているか。
    ///
    /// <para>
    /// これが false のとき解像度の上書きは効かせられない ── 枝の capsfilter の要求が
    /// <c>tee</c> を越えてソースまで伝播し、<b>プレビューとイベント録画まで一緒に縮む</b>。
    /// 解析は <see cref="SrcPipelineBuilder.Parse"/> に任せる（引用符の中の <c>!</c> の扱い等、
    /// 規則を 2 か所に書かないため）。
    /// </para>
    /// </summary>
    public static bool SourceSizeIsPinned(string? srcPipeline)
        => SourceSize(srcPipeline).Length > 0;

    /// <summary>
    /// <c>SrcPipeline</c> の caps が <c>framerate</c> を固定しているか。
    /// 固定していないときフレームレートの上書きは効かせられない
    /// ── 要求が <c>tee</c> を越えて伝播し、<b>本線まで同じレートに落ちる</b>。
    /// </summary>
    public static bool SourceFramerateIsPinned(string? srcPipeline)
        => SrcPipelineBuilder.Parse(srcPipeline).CapsFields.TryGetValue("framerate", out string? rate)
            && ContinuousFirstSampleBudget.TryParseFramerate(rate, out _, out _);

    /// <summary>
    /// <c>SrcPipeline</c> の caps が固定している大きさ（<c>1920x1080</c>）。固定していなければ空。
    /// <c>tee</c> の手前を固定するのに使う。
    /// </summary>
    public static string SourceSize(string? srcPipeline)
    {
        var caps = SrcPipelineBuilder.Parse(srcPipeline).CapsFields;
        return caps.TryGetValue("width", out string? w) && caps.TryGetValue("height", out string? h)
            && int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            && int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            && 0 < width && 0 < height
            ? $"{width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}"
            : "";
    }

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
    public static ContinuousBranchPlan Plan(
        EventRecordingType type,
        string encoder,
        bool needsSystemMemory,
        string? framerate,
        string? resolution,
        bool sourceSizeIsPinned,
        bool sourceFramerateIsPinned)
    {
        if (string.IsNullOrWhiteSpace(encoder))
            return new ContinuousBranchPlan("", null, false);

        var dropped = new List<string>();

        // フレームレートは分数へ正規化する。caps の framerate は GstFraction なので、
        // "5" と書くと gst_caps_from_string が (int)5 として読み、
        // **どの要素も扱えない caps になってリンクに失敗する**（実測）。
        //
        // 解像度と同じく、**上流が固定されていないときは上書きを捨てる**。videorate の
        // 要求も tee を越えて伝播するので、ソースが任意のレートを出せると
        // **プレビューとイベント録画まで同じレートに落ちる**（実測: ソースの caps に
        // framerate を書かずに枝を 5/1 にすると、プレビュー枝も 5/1 になった）。
        // ただし固定に必要なのはソース側の caps だけである ── 解像度と違い、
        // tee の手前の d3d12convert はレートを変えられないので吸収しない（実測）。
        string? rate = null;
        if (RequiresVideorate(framerate))
        {
            if (!TryNormalizeFramerate(framerate, out string normalized))
                dropped.Add($"ContinuousFramerate='{framerate}' is not a frame rate (write it as 5/1 or 5); it was ignored");
            else if (!sourceFramerateIsPinned)
                dropped.Add(
                    $"ContinuousFramerate='{framerate}' was ignored because the source pipeline does not pin "
                    + "framerate. Changing only this branch would drag the whole capture (preview and event "
                    + "recording included) down to that rate. Add framerate=... to the caps of SrcPipeline, "
                    + "or clear ContinuousFramerate");
            else
                rate = normalized;
        }

        // 解像度の上書きは tee の入力が固定されているときだけ許す。
        //
        // **これは安全側へ倒すための制限ではなく、正しさの要件である。** 枝の capsfilter が
        // 要求する幅・高さは、拡縮できる要素（d3d12convert / videoscale）を素通りして
        // tee を越え、**ソースまで伝播する** ── ソースが任意の大きさを出せる場合
        // （d3d12screencapturesrc は width:[1,MAX] を名乗る）、ソースはその小さい方を選び、
        // **プレビューもイベント録画も一緒に縮む**。実測では 3840x2160 のキャプチャが
        // 常時枝の 960x540 に引きずられ、プレビュー枝も 960x540 になった。
        //
        // 上流を固定するのは利用者にしかできない（アプリはソースの素の大きさを知らない
        // ── モニターの物理サイズを Win32 から取ると DPI 仮想化で別の値になる。実測: 2194x1234 と
        // 3840x2160）。したがって、固定されていないときは**上書きを捨てて理由を返す**。
        // 常時録画が等倍で回るだけで済み、イベント録画は無傷 ── 隔離契約と同じ形である。
        bool hasSize = false;
        int width = 0, height = 0;
        if (!string.IsNullOrWhiteSpace(resolution))
        {
            if (!TryParseResolution(resolution, out width, out height))
                dropped.Add($"ContinuousResolution='{resolution}' is not a frame size (write it as 1280x720); it was ignored");
            else if (!sourceSizeIsPinned)
                dropped.Add(
                    $"ContinuousResolution='{resolution}' was ignored because the source pipeline does not pin "
                    + "width/height. Scaling only this branch would drag the whole capture (preview and event "
                    + "recording included) down to that size. Add width=... height=... to the caps of SrcPipeline, "
                    + "or clear ContinuousResolution");
            else
                hasSize = true;
        }

        bool d3d12 = type == EventRecordingType.D3d12;
        string memory = d3d12 ? "video/x-raw(memory:D3D12Memory)" : "video/x-raw";

        var sb = new StringBuilder();
        sb.Append("t. ! ").Append(Queue).Append(" ! ");

        // videorate はフレームレートだけを変える（caps は video/x-raw(ANY) なので
        // D3D12 メモリのまま通る。実測で確認済み）。**メモリ機能を書き落とさないこと** ──
        // D3d12 経路で video/x-raw と書くとシステムメモリを要求してしまい、
        // 上流に d3d12download が無いのでリンクに失敗する。
        //
        // **drop-only=true は本線のフレームレート低下を直さない**（実測。下記）。
        // それでも付けているのは、この用途では素の videorate の挙動が要らないからである
        // ── 常時録画は必ず「下げる」方向にしか使わないので複製は不要で、ソースが止まった
        // ときに複製フレームを書庫へ入れる方が困る。既定の videorate は次のフレームが
        // 来るまで前のバッファを持ち続けるが、drop-only ではすぐ手放す
        // （上流の `gst_buffer_replace (&videorate->prevbuf, NULL)`）。
        //
        // **未解決**: 常時枝に videorate が入ると本線の実 fps が落ちる。
        // 実測（実機・カメラ 1920x1080@30、20 秒窓の実効 fps）:
        //
        //   常時録画なし                          29.9fps
        //   常時録画あり・フレームレート上書き無し   29.9fps（縮小はする＝下流の仕事は多い）
        //   常時録画あり・5fps（drop-only 無し）    11.9fps
        //   常時録画あり・5fps（drop-only 付き）    12.4fps  ← 変わらない
        //
        // **合成ソース（d3d12testsrc / videotestsrc）では再現しない** ── 実カメラと
        // 画面キャプチャでだけ出る。切り分けの続きは docs/gpu-verification.md。
        if (rate is not null)
            sb.Append(VideorateElement).Append(" ! ").Append(memory)
              .Append(", framerate=").Append(rate).Append(" ! ");

        if (d3d12)
        {
            // 拡縮は D3D12 のまま GPU 側で行う（d3d12convert は変換と拡縮を兼ねる）。
            // ダウンロードはエンコーダーがシステムメモリを要求するときだけ、しかも
            // 縮めた後に行う ── 順序を逆にすると、素の解像度のフレームを CPU へ運ぶことになる。
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

        return new ContinuousBranchPlan(
            sb.ToString(), dropped.Count == 0 ? null : string.Join(" / ", dropped), hasSize);
    }

    /// <summary>
    /// フレームレートを caps に書ける分数へ正規化する（<c>5</c> → <c>5/1</c>）。
    /// 解析は <see cref="ContinuousFirstSampleBudget.TryParseFramerate"/> と同じ規則を使う
    /// ── 予算の計算と枝の組み立てで受け付ける書き方が食い違うと、
    /// 「予算は計算できたのにパイプラインが組めない」形で壊れる。
    /// </summary>
    public static bool TryNormalizeFramerate(string? framerate, out string normalized)
    {
        normalized = "";
        if (!ContinuousFirstSampleBudget.TryParseFramerate(framerate, out int numerator, out int denominator))
            return false;

        normalized = numerator.ToString(CultureInfo.InvariantCulture)
            + "/" + denominator.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
