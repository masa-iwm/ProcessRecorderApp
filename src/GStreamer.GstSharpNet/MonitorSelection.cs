using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProcessRecorderApp.GStreamer;

/// <summary><see cref="MonitorSelection.Resolve"/> の結果。</summary>
public sealed class MonitorSelectionResult
{
    /// <summary>
    /// 解決後の <c>SrcPipeline</c> 文字列。<see cref="Failure"/> が付いているときは null。
    /// 何もすることが無かった場合は<b>入力そのもの</b>を返す。
    /// </summary>
    public string? Pipeline { get; init; }

    /// <summary>
    /// 指定を活かせず <c>monitor-index</c> へ縮退したときの理由（英語・非ローカライズ）。
    /// <b>握り潰してはいけない</b> ── 縮退したことは画面からは分からない。
    /// </summary>
    public string? Warning { get; init; }

    /// <summary>
    /// 解決できず初期化を失敗させるべきときの理由（英語・非ローカライズ）。指定されたパスを含む。
    /// </summary>
    public string? Failure { get; init; }

    /// <summary>解決できたか（縮退した場合も true。区別は <see cref="Warning"/> で付く）。</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// <b>モニターを位置ではなく安定した識別子で指定するための解決規則。</b>
///
/// <para>
/// 画面キャプチャ要素（<c>d3d12screencapturesrc</c> / <c>d3d11screencapturesrc</c>）が持つ
/// 選択プロパティは <c>monitor-index</c> と <c>monitor-handle</c> の 2 つだけで、
/// <b>パスを受け取るプロパティは無い</b>。番号は位置依存（前のモニターを抜くと詰まる）、
/// ハンドルは実行時の <c>HMONITOR</c>（保存できない）── どちらも設定に書ける識別子ではない。
/// 一方でデバイスプロバイダは各モニターに <c>device.path</c> を付けており、これは
/// 物理モニター＋端子ごとに安定している（上流自身が再 probe 後の同一性判定に使っている）。
/// </para>
/// <para>
/// したがって<b>「パスで保存し、パイプラインを組む時点でハンドルへ解決する」</b>のが唯一の道になる。
/// <c>monitor-device-path</c> は<b>要素のプロパティではなくアプリの擬似プロパティ</b>で、
/// ここで <c>monitor-handle</c> へ置き換えられて消える ── 残したまま
/// <c>gst_parse_launch</c> へ渡すと <c>no property "monitor-device-path"</c> で落ちる。
/// </para>
/// <para>
/// <b>純粋関数にしてある。</b> 列挙（<see cref="GstIntrospect.GetMonitors"/>）は
/// 呼び出し側が行い、ここは文字列とモニター一覧だけを見る ── そうしないと規則そのものを
/// L1 で縛れず、実機のあるなしで検査できる範囲が変わってしまう。
/// </para>
/// </summary>
public static class MonitorSelection
{
    /// <summary>設定に書ける擬似プロパティの名前（<b>実要素には存在しない</b>）。</summary>
    public const string PathProperty = "monitor-device-path";

    /// <summary>解決先の実プロパティ（<c>HMONITOR</c>、<c>guint64</c>）。</summary>
    public const string HandleProperty = "monitor-handle";

    /// <summary>パス指定が優先するときに取り除く実プロパティ。</summary>
    public const string IndexProperty = "monitor-index";

    /// <summary>
    /// その文字列の解決にモニターの列挙が要るか。
    ///
    /// <para>
    /// <b>列挙は安くない</b>（デバイスプロバイダの Start/Stop と <c>monitor.devices</c> の 1 行）のに、
    /// <c>InitializeCore</c> はレコーダーごと・復帰のたびに走る ── テストソースの
    /// レコーダーまで巻き込んで 60 秒ごとにプロバイダを起こさないよう、ここで先に落とす。
    /// </para>
    /// <para>
    /// <b>見落としは起きない</b> ── <see cref="SrcPipelineBuilder.Parse"/> が
    /// <see cref="PathProperty"/> を見つけられる文字列は、必ずこの綴りを部分文字列として含む。
    /// 逆（含むが解決対象ではない）は起こりうるが、そのときは <see cref="Resolve"/> が
    /// 何もせずに入力をそのまま返すだけである。
    /// </para>
    /// </summary>
    public static bool RequiresMonitors(string? srcPipeline)
        => srcPipeline is not null && srcPipeline.Contains(PathProperty, StringComparison.Ordinal);

    /// <summary>
    /// <c>monitor-device-path</c> を実際に要素へ渡せる形へ解決する。規則は 5 つ:
    ///
    /// <list type="number">
    /// <item>指定が無い → 文字列は変えない（警告なし）。</item>
    /// <item>一致するモニターが在り、ハンドルも読めた → <c>monitor-device-path=…</c> を
    /// <c>monitor-handle=…</c> へ置き換え、<c>monitor-index</c> を取り除く
    /// （<b>パス指定は番号より優先する</b> ── 両方残すと要素側の優先順に委ねることになる）。</item>
    /// <item>モニターは列挙できたのに一致しない → <b>失敗</b>。そのモニターは今つながっていない
    /// ので、番号へ縮退すると<b>黙って別の画面を録り始める</b> ── 直そうとしている取り違えを
    /// 自分で作ることになる。失敗させれば復帰の連鎖が拾い、モニターが戻れば復帰する。</item>
    /// <item>列挙が空（利用者の GStreamer に d3d12 が無い等） → <b><c>monitor-index</c> が
    /// 書かれていれば</b>パスだけ取り除いて番号を残し、<b>警告</b>を返す。ここで失敗させると
    /// 「番号なら録れる機械で 1 フレームも録れない」になる。<b>書かれていなければ失敗</b>
    /// ── 戻せる先が無いのにパスを消すと、選択プロパティが 1 つも無い文字列になり
    /// <b>既定のモニター（index 0）を黙って撮る</b>。それは <c>monitor-device-path</c> が
    /// 防ごうとしている事故そのものである。</item>
    /// <item>一致したがハンドルが 0（パスは読めたがハンドルが読めないビルド） → 4 と同じ扱い
    /// （<c>monitor-index</c> の有無で縮退／失敗が決まる）。</item>
    /// </list>
    ///
    /// <para>
    /// <b>失敗の理由は 3 つとも別の文言にしてある。</b> 原因が違う（3 は繋がっていない、
    /// 4 は列挙できない、5 はハンドルが読めない）ので、同じ文言だと利用者が違う対処へ
    /// 誘導される。4/5 の失敗には<b>「<c>monitor-index</c> を書けば縮退できる」</b>ことも書く
    /// ── 番号で構わない利用者にとっては、それが唯一の前進である。
    /// </para>
    ///
    /// <para>
    /// <b>それ以外は 1 文字も変えない。</b> <see cref="SrcPipelineBuilder.Parse"/> →
    /// <see cref="SrcPipelineBuilder.Assemble"/> の往復では実現できない ── あちらは
    /// カタログに載っている caps とプロパティしか書き戻さず、ソースより後ろの中間要素
    /// （<c>! identity ! videoconvert</c> 等）を丸ごと落とす。そのため<b>対象のトークンだけを
    /// 書き換える</b>形にしてある。
    /// </para>
    /// </summary>
    /// <param name="srcPipeline">レコーダーの <c>SrcPipeline</c>。</param>
    /// <param name="monitors">いま見えているモニター（<see cref="GstIntrospect.GetMonitors"/>）。</param>
    public static MonitorSelectionResult Resolve(string? srcPipeline, IReadOnlyList<MonitorInfo> monitors)
    {
        // 規則 1。**分割も解析もせずに入力の参照をそのまま返す**ので、
        // 圧倒的多数の経路が「1 文字も変えない」ことは自明になる。
        if (!RequiresMonitors(srcPipeline))
            return new MonitorSelectionResult { Pipeline = srcPipeline };

        var parsed = SrcPipelineBuilder.Parse(srcPipeline);
        if (!parsed.Properties.TryGetValue(PathProperty, out string? path))
        {
            // 綴りは在るが key=value としては読めない（値が無い・引用された別の値の中身など）。
            // 触らない ── 中途半端に消すより、gst_parse_launch に大きな声で落ちてもらう方がよい。
            return new MonitorSelectionResult { Pipeline = srcPipeline };
        }

        // **縮退できるのは戻せる先が在るときだけ。** `monitor-index` が書かれていなければ、
        // パスを取り除いた文字列には選択プロパティが 1 つも残らず、要素は既定のモニター
        // （index 0）を黙って撮る ── パス指定が防ごうとしている事故そのものである。
        // 判定は `Parse` の結果で行う（＝パスを読んだのと同じ規則・同じソース要素セグメント）。
        bool hasIndex = parsed.Properties.ContainsKey(IndexProperty);

        // 規則 4
        if (monitors is null || monitors.Count == 0)
        {
            if (!hasIndex)
            {
                return new MonitorSelectionResult
                {
                    Failure = $"no screen-capture monitor could be enumerated, so {PathProperty}='{path}' "
                        + $"could not be resolved, and no {IndexProperty} is written to fall back on "
                        + $"(add {IndexProperty} if selecting the monitor by position is acceptable)",
                };
            }

            return new MonitorSelectionResult
            {
                Pipeline = Rewrite(srcPipeline!, handle: null, removeIndex: false),
                Warning = $"no screen-capture monitor could be enumerated, so {PathProperty}='{path}' "
                    + $"was ignored and {IndexProperty} is used instead",
            };
        }

        MonitorInfo? match = null;
        int withPath = 0;
        foreach (MonitorInfo monitor in monitors)
        {
            if (!string.IsNullOrEmpty(monitor.Path))
                withPath++;
            if (match is null && string.Equals(monitor.Path, path, StringComparison.Ordinal))
                match = monitor;
        }

        // 規則 3。**理由にパスを含める** ── 「どのモニターを探して見つからなかったか」が
        // 分からないと、利用者は設定を直しようがない。
        // **パスを読めた台数も出す。** 1 台も読めない構成（プロバイダーが device.path を
        // 付けないビルド）でもここへ落ちるが、そのときの真因は「繋がっていない」ではなく
        // 「照合する材料が無い」であり、件数だけでは繋ぎ直しという無効な対処へ誘導してしまう。
        if (match is null)
        {
            return new MonitorSelectionResult
            {
                Failure = $"the monitor with {PathProperty}='{path}' is not connected "
                    + $"({monitors.Count} monitor(s) enumerated, {withPath} with a readable device path)",
            };
        }

        // 規則 5
        if (match.Handle == 0)
        {
            if (!hasIndex)
            {
                return new MonitorSelectionResult
                {
                    Failure = $"the monitor with {PathProperty}='{path}' was found but its {HandleProperty} "
                        + $"could not be read, and no {IndexProperty} is written to fall back on "
                        + $"(add {IndexProperty} if selecting the monitor by position is acceptable)",
                };
            }

            return new MonitorSelectionResult
            {
                Pipeline = Rewrite(srcPipeline!, handle: null, removeIndex: false),
                Warning = $"the monitor with {PathProperty}='{path}' was found but its {HandleProperty} "
                    + $"could not be read, so {IndexProperty} is used instead",
            };
        }

        // 規則 2
        return new MonitorSelectionResult
        {
            Pipeline = Rewrite(srcPipeline!, match.Handle.ToString(CultureInfo.InvariantCulture), removeIndex: true),
        };
    }

    /// <summary>
    /// ソース要素セグメントだけを書き換え、残りは原文のまま返す。
    ///
    /// <para>
    /// <see cref="SrcPipelineBuilder.SplitOutsideQuotes"/> は区切りの <c>'!'</c> 以外を
    /// 1 文字も落とさないので、<c>string.Join("!", …)</c> で触らなかった部分は原文に戻る。
    /// どのセグメントがソースかの判定は <see cref="SrcPipelineBuilder.Parse"/> と同じ
    /// （空白だけの区画を飛ばし、メディアタイプで始まらない最初の区画）。
    /// </para>
    /// </summary>
    private static string Rewrite(string pipeline, string? handle, bool removeIndex)
    {
        List<string> segments = SrcPipelineBuilder.SplitOutsideQuotes(pipeline);
        for (int i = 0; i < segments.Count; i++)
        {
            string trimmed = segments[i].Trim();
            if (trimmed.Length == 0 || SrcPipelineBuilder.MediaTypeRegex().IsMatch(trimmed))
                continue;

            segments[i] = RewriteSegment(segments[i], handle, removeIndex);
            break;
        }
        return string.Join("!", segments);
    }

    /// <summary>
    /// 1 つのセグメントの中で、対象のトークンだけを置き換え／取り除く。
    ///
    /// <para>
    /// 走査は <see cref="SrcPipelineBuilder.KeyValueRegex"/>（＝解析と同じ規則）で行う。
    /// 左から順の非重複マッチなので、<b>引用された値の中身は独立したトークンにならない</b>
    /// ── <c>device-name="monitor-device-path=x"</c> は 1 つのトークンとして通過する。
    /// </para>
    /// <para>
    /// 取り除くときは<b>直前の空白もまとめて落とす</b>。残すと二重の空白が生まれ、
    /// 「対象以外は 1 文字も変えない」という検査にも掛からない見えない差になる。
    /// </para>
    /// </summary>
    private static string RewriteSegment(string segment, string? handle, bool removeIndex)
    {
        var sb = new StringBuilder(segment.Length);
        int copied = 0;

        foreach (Match match in SrcPipelineBuilder.KeyValueRegex().Matches(segment))
        {
            string key = match.Groups["key"].Value;
            bool isPath = string.Equals(key, PathProperty, StringComparison.Ordinal);
            bool isIndex = removeIndex && string.Equals(key, IndexProperty, StringComparison.Ordinal);
            if (!isPath && !isIndex)
                continue;

            sb.Append(segment, copied, match.Index - copied);
            copied = match.Index + match.Length;

            if (isPath && handle is not null)
            {
                sb.Append(HandleProperty).Append('=').Append(handle);
                continue;
            }

            int keep = sb.Length;
            while (0 < keep && char.IsWhiteSpace(sb[keep - 1]))
                keep--;
            sb.Length = keep;
        }

        sb.Append(segment, copied, segment.Length - copied);
        return sb.ToString();
    }
}
