using System;
using System.Collections.Generic;
using System.Linq;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// H.264 エンコーダー候補1件の定義。
/// </summary>
/// <param name="FactoryName">
/// GStreamer の要素ファクトリ名。存在確認（プローブ）はこの名前で行う。
/// </param>
/// <param name="LaunchString">
/// パイプラインへ埋め込む文字列（ファクトリ名＋プロパティ）。
/// プロパティの単位・名称はエンコーダーごとに異なるため、ファクトリ名とは別に持つ
/// （例: <c>x264enc</c> の <c>bitrate</c> は kbit/sec だが <c>openh264enc</c> は bit/sec）。
/// </param>
/// <param name="NeedsSystemMemory">
/// 入力にシステムメモリを要求するか。<c>Type=D3d12</c> の経路では <c>tee</c> から
/// <c>video/x-raw(memory:D3D12Memory)</c> がそのまま流れてくるが、
/// <c>parse_launch</c> は変換要素を自動挿入しないため、真のエンコーダーの手前には
/// <c>d3d12download ! videoconvert !</c> が必要になる。
/// <b>このフラグの意味は「システムメモリが要る」ではなく「ダウンロードを挿す」</b>であり、
/// 行き先のメモリは固定せず交渉に任せる。
/// これを入れないと「リンクできない」で <c>ParseLaunch</c> が失敗する
/// （＝まさに直したい AMD/NVIDIA 機で壊れる）。
/// </param>
/// <param name="BitrateUnitPerKbps">
/// <c>bitrate</c> プロパティ 1 単位が何 kbit/sec に当たるか。
/// <see langword="null"/> は「この定義は <c>bitrate</c> を持たない」を意味する。
/// <c>x264enc</c> / <c>mfh264enc</c> は kbit/sec なので 1、<c>openh264enc</c> は bit/sec なので 1000。
/// <b>単位を持つ定義は必ず <c>LaunchString</c> に <c>bitrate=</c> を含む</b>
/// （<see cref="WithBitrateKbps"/> の前提。L1 がカタログ全体で縛る）。
/// </param>
public sealed partial record H264EncoderDef(
    string FactoryName, string LaunchString, bool NeedsSystemMemory, int? BitrateUnitPerKbps = null)
{
    /// <summary>
    /// <c>bitrate=&lt;数値&gt;</c> のトークン（値だけを置き換える）。
    ///
    /// <para>
    /// <b>先頭は <c>\b</c> ではなく <c>(?&lt;![-\w])</c> で切る。</b> <c>-</c> は語の境界なので、
    /// <c>\bbitrate=</c> は <c>max-bitrate=</c> や <c>target-bitrate=</c> にも一致する
    /// ── 別のプロパティの値を書き換えてしまう。
    /// </para>
    /// <para>
    /// <b><see cref="System.Text.RegularExpressions.GeneratedRegexAttribute"/> で生成する。</b>
    /// Native AOT では <c>RegexOptions.Compiled</c> が黙って解釈実行へ落ちる。
    /// </para>
    /// <para>
    /// <b><c>internal</c> なのは L1 が引くため。</b> テスト側へパターンを写すと、
    /// 片方だけを緩めた日に「カタログの不変条件が別のプロパティで満たされている」定義を
    /// 見逃す ── 規則は 1 か所にしか置かない。
    /// </para>
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![-\w])bitrate=\d+\b")]
    internal static partial System.Text.RegularExpressions.Regex BitrateTokenRegex();

    /// <summary>
    /// プロパティを一切付けない、ファクトリ名だけの定義を返す。
    /// <b><see cref="BitrateUnitPerKbps"/> も null へ戻す</b> ── 落とした
    /// <c>LaunchString</c> にはもう <c>bitrate=</c> が無いので、残すと
    /// <see cref="WithBitrateKbps"/> が置換先を見つけられない。
    /// </summary>
    public H264EncoderDef WithoutProperties()
        => LaunchString == FactoryName
            ? this
            : this with { LaunchString = FactoryName, BitrateUnitPerKbps = null };

    /// <summary>
    /// <c>bitrate</c> を指定の kbit/sec へ書き換えた定義を返す。
    ///
    /// <para>
    /// <b><see cref="BitrateUnitPerKbps"/> が <see langword="null"/> の定義はそのまま返す</b>
    /// ── 実機で単位を確認できていない GPU 系エンコーダーに <c>bitrate</c> を書くと、
    /// プロパティ名も単位も当て推量になる（<see cref="EncoderCatalog.ExpandAttempts"/> の
    /// 再試行に頼るだけの形になり、候補が 1 つ無駄になる）。
    /// </para>
    /// <para>
    /// 単位を持つのに <c>bitrate=</c> が無い定義は<b>設計違反</b>なので投げる ──
    /// 黙って元の値のまま返すと、指定した帯域が効いていないことに気付けない。
    /// </para>
    /// </summary>
    public H264EncoderDef WithBitrateKbps(int kbps)
    {
        if (BitrateUnitPerKbps is not { } unit)
            return this;

        // 「見つからない」の判定は置換の前に行う ── 置換後の文字列が元と同じになるのは
        // 同じ値を指定した正常な場合（既定値の往復）でもありうる。
        if (!BitrateTokenRegex().IsMatch(LaunchString))
        {
            throw new InvalidOperationException(
                $"{FactoryName}: BitrateUnitPerKbps is set but the launch string has no 'bitrate=' token: {LaunchString}");
        }

        // **checked。** 単位が 1000 のエンコーダー（bit/sec で受ける定義）では
        // 大きな kbps が int を溢れ、投げずに負の bitrate 文字列を書いてしまう。
        string value = checked(kbps * unit).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return this with { LaunchString = BitrateTokenRegex().Replace(LaunchString, $"bitrate={value}", 1) };
    }
}

/// <summary>プローブ1件の結果（どのファクトリが実機に存在したか）。</summary>
public sealed record EncoderProbeEntry(string FactoryName, bool Available);

/// <summary>プローブ全体の結果と、その時点で選ばれた候補列。</summary>
public sealed record EncoderProbeReport(
    IReadOnlyList<EncoderProbeEntry> Entries,
    IReadOnlyList<string> D3d12Order,
    IReadOnlyList<string> SystemOrder)
{
    /// <summary>
    /// ログ・診断用の1行表現。イベント名（<c>gst.encoders</c>）は含まない
    /// ── activity.log 側が付けるため、ここで持つと二重になる。
    /// </summary>
    public string ToLogLine()
    {
        string available = string.Join(",", Entries.Where(e => e.Available).Select(e => e.FactoryName));
        string missing = string.Join(",", Entries.Where(e => !e.Available).Select(e => e.FactoryName));
        return $"available=[{available}] missing=[{missing}] "
             + $"d3d12-order=[{string.Join(",", D3d12Order)}] system-order=[{string.Join(",", SystemOrder)}]";
    }
}

/// <summary>
/// H.264 エンコーダーの候補カタログと解決ロジック。
///
/// エンコーダーをベンダー決め打ち（例: <c>D3d12</c>=<c>qsvh264enc</c>＝Intel 専用、
/// <c>System</c>=<c>x264enc</c>）にすると、他ベンダーの機械や GPU の無い環境では
/// <c>Initialize()</c> が失敗して録画そのものができない。本クラスは
/// 「実機に存在するもの」を優先順に並べ、<see cref="EventRecorder.Initialize"/> が
/// 上から順に試せるようにする。
///
/// **「存在する」≠「動く」** ── ファクトリが登録されていてもネゴシエーションが通るとは限らない。
/// 最終判定は実際に <c>ParseLaunch</c> と <c>SetState(Playing)</c> を行う
/// <see cref="EventRecorder.Initialize"/> 側が持ち、本クラスは候補の順序付けまでを担当する。
///
/// GStreamer の GPU 系プラグイン（qsv / d3d12 / nvcodec / amfcodec）は、プラグイン自体は
/// ロードされても**対応ハードウェアが無ければ要素ファクトリを登録しない**。
/// つまり <c>ElementFactory.Find</c> が null を返すことが、そのまま
/// 「この実機では使えない」という正しい判定になる。
/// </summary>
public static class EncoderCatalog
{
    // ---- 候補定義 ------------------------------------------------------------------
    //
    // LaunchString のプロパティは、この開発機（GPU 無し）で gst-inspect により
    // 実在と単位を確認できたものだけを付けている。確認できていない GPU 系エンコーダーは
    // ファクトリ名＋GOP 長のみとし、それ以外は既定値に任せる。
    // 存在しないプロパティを書くと ParseLaunch が失敗するが、ExpandAttempts が
    // プロパティ無しで再試行するためエンコーダー自体を取りこぼすことはない
    // （その場合レポートに failedAttempts=1 が出るので、見直しの合図になる）。
    // 実機での確認手順は docs/gpu-verification.md を参照。

    /// <summary>
    /// 全エンコーダー共通の GOP 長（フレーム数）。
    ///
    /// **これは画質設定ではなく、アプリの中核契約を成立させるための制約**。
    /// 録画開始時、<c>PushRecordBuffer</c> は最初の I フレームが見つかるまでバッファを
    /// 捨て続ける。リングバッファ（<c>BufferDuration</c>、既定 10 秒）の中に I フレームが
    /// 1枚も無いと、**事前バッファが丸ごと捨てられたうえ、次の I フレームが来るまでの
    /// ライブ映像まで失われる**。つまり「録画ボタンを押す前の映像が残る」という
    /// アプリの中核価値が静かに消える。
    ///
    /// 実測（15fps / <c>BufferDuration</c>=2000ms / 録画窓3秒）:
    /// <list type="bullet">
    ///   <item><c>gop-size=64</c>（＝4.27秒間隔）の <c>qsvh264enc</c> → 生成尺 1.067秒</item>
    ///   <item><c>key-int-max=30</c>（＝2.0秒間隔＝バッファ長と同値）の <c>x264enc</c>
    ///         → 3.2秒 と 5.067秒。GOP の位相次第で当たり外れが出る</item>
    /// </list>
    ///
    /// <b>固定のフレーム数ではなく、実際のフレームレートから「秒」で逆算する。</b>
    /// フレーム数を固定すると、低いフレームレートの経路で間隔が伸び切る ──
    /// <b>実測: 60 フレーム固定のまま 5fps の常時録画枝を走らせると 12 秒間隔になり、
    /// 5 秒のセグメントがキーフレーム待ちで 10 秒へ伸びた</b>
    /// （<c>continuous.overshoot</c>。分割はキーフレームでしか行えない）。
    /// 同じ理由で、イベント録画側も 15fps のソースでは 4 秒間隔になり、
    /// 事前バッファの短い構成では録画の立ち上がりがそのぶん遅れる。
    /// <b>逆算にした後は実機でも確認済み</b> ── 15fps のソースは `gop-size=30`、
    /// 5fps の常時枝は `gop-size=10` が選ばれ、<c>continuous.overshoot</c> は出なくなった。
    /// </summary>
    public const int TargetKeyframeIntervalSeconds = 2;

    /// <summary>
    /// caps に framerate が無い・読めないときに想定するフレームレート。
    /// <see cref="SrcPipelineBuilder"/> の caps の既定と揃えてある。
    /// </summary>
    public const int AssumedFps = 30;

    /// <summary>
    /// 既定のフレームレートでの GOP 長（＝60）。文書と
    /// <c>tools/Verify-GpuEncoders.ps1</c> の手動指定行がこの値を基準にしている。
    /// </summary>
    public const int GopSize = AssumedFps * TargetKeyframeIntervalSeconds;

    /// <summary>
    /// caps の framerate 文字列（<c>30/1</c> など）から、録画のキーフレーム間隔
    /// （<see cref="TargetKeyframeIntervalSeconds"/> 秒）ぶんの GOP 長を決める。
    /// 読めなければ <see cref="GopSize"/>。
    /// </summary>
    public static int GopForFramerate(string? framerate)
        => GopForFramerate(framerate, TargetKeyframeIntervalSeconds, GopSize);

    /// <summary>
    /// caps の framerate 文字列（<c>30/1</c> など）から
    /// <paramref name="keyframeIntervalSeconds"/> 秒ぶんの GOP 長（フレーム数）を決める。
    /// <b>GOP 長を秒から逆算する場所はここ 1 か所である</b>
    /// ── 録画は 2 秒、トランスコードは <c>fragment</c> 1 つぶん（1 秒）と、
    /// 欲しい間隔が経路ごとに違うので秒を引数で受ける。
    ///
    /// <para>
    /// 解析は <see cref="ContinuousFirstSampleBudget.TryParseFramerate"/> と同じ規則を使う
    /// （フレームレートの読み方を 2 か所に書かない）。<b>読めなければ
    /// <paramref name="fallback"/> をそのまま返す</b> ── 呼び出し側にとって
    /// 「測れなかった」と「測ったら同じだった」を区別する必要が無いので、
    /// 既定値は呼び出し側が持つ。
    /// </para>
    /// </summary>
    /// <param name="framerate">caps の <c>framerate</c>（<c>5/1</c> / <c>89/3</c> / <c>30</c>）。</param>
    /// <param name="keyframeIntervalSeconds">欲しいキーフレーム間隔(秒)。</param>
    /// <param name="fallback"><paramref name="framerate"/> が読めなかったときに返す GOP 長。</param>
    public static int GopForFramerate(string? framerate, double keyframeIntervalSeconds, int fallback)
    {
        if (!ContinuousFirstSampleBudget.TryParseFramerate(framerate, out int numerator, out int denominator))
            return fallback;

        double fps = (double)numerator / denominator;
        int gop = (int)Math.Round(fps * keyframeIntervalSeconds, MidpointRounding.AwayFromZero);
        return Math.Max(1, gop);
    }

    /// <summary>Intel QSV。</summary>
    private static H264EncoderDef Qsv(int gop) =>
        new("qsvh264enc", $"qsvh264enc rate-control=icq icq-quality=30 gop-size={gop}", NeedsSystemMemory: false);

    /// <summary>x264（bitrate は kbit/sec。GOP は key-int-max）。</summary>
    private static H264EncoderDef X264(int gop) =>
        new("x264enc", $"x264enc tune=zerolatency bitrate=2000 speed-preset=ultrafast key-int-max={gop}", NeedsSystemMemory: true,
            BitrateUnitPerKbps: 1);

    /// <summary>OpenH264。**bitrate は bit/sec** なので x264 の数値をそのまま使ってはいけない。</summary>
    private static H264EncoderDef OpenH264(int gop) =>
        new("openh264enc", $"openh264enc bitrate=2000000 gop-size={gop}", NeedsSystemMemory: true,
            BitrateUnitPerKbps: 1000);

    /// <summary>Media Foundation（bitrate は kbit/sec）。</summary>
    private static H264EncoderDef MediaFoundation(int gop) =>
        new("mfh264enc", $"mfh264enc bitrate=2000 gop-size={gop} low-latency=true", NeedsSystemMemory: true,
            BitrateUnitPerKbps: 1);

    // 以下はプロパティ未確認のため GOP 長のみ指定する（既定の GOP は長すぎることが多く、
    // 上記のとおり事前バッファを壊すため、ここだけは既定値に任せられない）。
    private static H264EncoderDef D3d12(int gop) => new("d3d12h264enc", $"d3d12h264enc gop-size={gop}", NeedsSystemMemory: false);
    /// <summary>
    /// NVENC の Direct3D11 モード。
    ///
    /// <para>
    /// <b><c>nvd3d12h264enc</c> という要素は存在しない</b>
    /// （実機の <c>gst-inspect</c> と <c>libgstnvcodec.dll</c> の
    /// 文字列の両方で確認。<c>nvcodec</c> が提供する NVENC は
    /// <c>nvh264enc</c>（CUDA モード）/ <c>nvd3d11h264enc</c>（D3D11 モード）/
    /// <c>nvautogpuh264enc</c>（自動選択）で、<b>D3D12 モードは無い</b>）。
    /// 存在しない名前を書いても <see cref="Resolve"/> が黙って飛ばして録画は他候補で
    /// 成立するため、<b>NVIDIA 機で意図した候補が一度も選ばれない</b>ことに気付けない。
    /// </para>
    /// <para>
    /// <b><c>NeedsSystemMemory</c> は <c>true</c>。</b> この要素は D3D11 メモリを受けるので、
    /// <c>D3d12</c> 経路が作る <b>D3D12 メモリはそのままでは渡せず</b>
    /// <c>d3d12download</c> を挟む必要がある。
    /// <b>このフラグの実際の意味は「システムメモリが要る」ではなく
    /// 「<c>d3d12download</c> を挿す」</b>である。
    /// </para>
    /// <para>
    /// <b>メモリ交渉は NVIDIA 実機で決着済み</b>（<c>tools/Verify-NvD3d11Memory.ps1</c>）。
    /// この要素の sink caps は <c>video/x-raw(memory:D3D11Memory)</c> と素の <c>video/x-raw</c> だけで、
    /// <b>D3D12Memory は受けない</b> ── だから <c>d3d12download</c> は必須で、
    /// <c>NeedsSystemMemory: true</c>（＝ダウンロードを挿す）は正しい。
    /// <b>ただし行き先を <c>video/x-raw(memory:SystemMemory)</c> で固定してはいけない</b> ──
    /// 固定すると明示のフィーチャ同士の一致にならず、毎フレーム GPU→CPU の往復が入る。
    /// 固定を外すと <c>d3d12download</c> の src もこの要素の sink も
    /// <c>video/x-raw(memory:D3D11Memory)</c> で折り合う（実測）ので、現行の形は
    /// <c>d3d12download ! videoconvert !</c> である。
    /// <b>この形で NVIDIA/Intel 実機の全ケースが OK</b>（<c>tools/Verify-GpuEncoders.ps1</c>。
    /// NVENC の 3 経路とも <c>retries</c> 0 で有効な MP4）。
    /// </para>
    /// </summary>
    private static H264EncoderDef NvD3d11(int gop) => new("nvd3d11h264enc", $"nvd3d11h264enc gop-size={gop}", NeedsSystemMemory: true);
    private static H264EncoderDef Nvidia(int gop) => new("nvh264enc", $"nvh264enc gop-size={gop}", NeedsSystemMemory: true);

    /// <summary>
    /// NVENC の自動 GPU 選択モード。
    ///
    /// <para>
    /// <b>優先順位は <see cref="Nvidia"/> の後ろに置く。</b> 明示的な2つもこの自動選択も、
    /// NVIDIA 実機で選ばれて有効な MP4 を出すことを確認済み
    /// （<c>tools/Verify-GpuEncoders.ps1</c> の専用ケース。docs/gpu-verification.md）。
    /// <b>それでも後ろのままにしてある</b> ── 自動選択を前に出す根拠が無いのに
    /// <b>NVIDIA 機で選ばれるエンコーダーだけが変わる</b>からで、
    /// ここに置けば「明示の2つが両方だめだったときの最後の NVIDIA 経路」として増えるだけで済む。
    /// </para>
    /// <para>
    /// <b><c>NeedsSystemMemory</c> は <c>true</c>。</b> <c>nvcodec</c> の要素は
    /// CUDA / GL / D3D11 / システムメモリを受けるが <b>D3D12 メモリは受けない</b>ので、
    /// <c>D3d12</c> 経路では <c>d3d12download</c> が要る（他の2つと同じ理由）。
    /// <b>実機の SINK caps では未確認</b>なので安全側に倒してある
    /// ── 偽にして間違っていた場合はリンク失敗で録画できなくなるが、
    /// 真にして間違っていても余分な変換が入るだけで録画は成立する。
    /// </para>
    /// </summary>
    private static H264EncoderDef NvAutoGpu(int gop) =>
        new("nvautogpuh264enc", $"nvautogpuh264enc gop-size={gop}", NeedsSystemMemory: true);

    private static H264EncoderDef Amd(int gop) => new("amfh264enc", $"amfh264enc gop-size={gop}", NeedsSystemMemory: true);

    /// <summary>
    /// <c>Type=D3d12</c> の優先順位。GPU ネイティブ（ダウンロード不要）を上位に置き、
    /// 最後にソフトウェアエンコーダーへ落ちる。
    /// </summary>
    public static readonly IReadOnlyList<H264EncoderDef> D3d12Candidates = D3d12CandidatesFor(GopSize);

    /// <summary>指定の GOP 長で <c>Type=D3d12</c> の候補列を作る。</summary>
    public static IReadOnlyList<H264EncoderDef> D3d12CandidatesFor(int gop) =>
        (H264EncoderDef[])[D3d12(gop), Qsv(gop), NvD3d11(gop), Nvidia(gop), NvAutoGpu(gop), Amd(gop),
                           MediaFoundation(gop), OpenH264(gop), X264(gop)];

    /// <summary><c>Type=System</c> の優先順位（入力は元からシステムメモリ）。</summary>
    public static readonly IReadOnlyList<H264EncoderDef> SystemCandidates = SystemCandidatesFor(GopSize);

    /// <summary>指定の GOP 長で <c>Type=System</c> の候補列を作る。</summary>
    public static IReadOnlyList<H264EncoderDef> SystemCandidatesFor(int gop) =>
        (H264EncoderDef[])[X264(gop), OpenH264(gop), MediaFoundation(gop)];

    /// <summary>録画種別に対応する候補列を返す。</summary>
    public static IReadOnlyList<H264EncoderDef> CandidatesFor(EventRecordingType type)
        => CandidatesFor(type, GopSize);

    /// <summary>録画種別と GOP 長に対応する候補列を返す。</summary>
    public static IReadOnlyList<H264EncoderDef> CandidatesFor(EventRecordingType type, int gop)
        => type == EventRecordingType.D3d12 ? D3d12CandidatesFor(gop) : SystemCandidatesFor(gop);

    /// <summary>カタログに定義済みのファクトリ名かどうかを調べる。</summary>
    public static bool TryGetKnown(string factoryName, out H264EncoderDef definition)
    {
        definition = D3d12Candidates.Concat(SystemCandidates)
            .FirstOrDefault(c => string.Equals(c.FactoryName, factoryName, StringComparison.Ordinal))!;
        return definition is not null;
    }

    /// <summary>
    /// 任意のファクトリ名について、そのエンコーダーがシステムメモリ入力を要求するかを決める。
    ///
    /// カタログに定義があればその値を使い、未知の要素のみ**安全側**に倒す
    /// （<c>D3d12</c> 経路では真＝ダウンロードを挟む）。
    /// 一律で真にしてはいけない ── <c>qsvh264enc</c> のような GPU ネイティブな要素を
    /// ユーザーが手動指定した場合に、不要な <c>d3d12download</c> が入って
    /// せっかくの GPU 経路が毎フレーム CPU へコピーされることになる。
    /// 一律で偽にしてもいけない ── <c>x264enc</c> を手動指定した場合に必要な
    /// <c>d3d12download</c> が入らずリンクできず、
    /// 「AMD / NVIDIA 機で録画できない」失敗モードがそのまま再現する。
    /// </summary>
    public static bool NeedsSystemMemoryFor(string factoryName, EventRecordingType type)
        => TryGetKnown(factoryName, out var known)
            ? known.NeedsSystemMemory
            : type == EventRecordingType.D3d12;

    // ---- プローブ ------------------------------------------------------------------

    /// <summary>
    /// 実際に GStreamer へ問い合わせるプローブ。
    /// 初期化前（<c>Controller.StaticInitialize</c> 完了前）は GStreamer に触らず false を返す
    /// ── GstSharp.Net では初期化前のバインディング呼び出しは例外で済まず、
    /// **ネイティブライブラリのロードだけが先に走り、<c>gst_init</c> 前の GStreamer を
    /// 呼ぶことになる**（要素ファクトリの登録が済んでいないので、答えも信用できない）。
    /// 例外の握りつぶしは想定外の失敗への保険として残す。
    /// </summary>
    public static bool ProbeWithGStreamer(string factoryName)
    {
        if (!DebugLogEx.IsGstInitialized)
            return false;
        try
        {
            // ファクトリは interned な GObject ラッパーなので破棄しない。
            var factory = Gst.ElementFactory.Find(factoryName);
            return factory is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>直近の <see cref="Probe"/> の結果（診断用。未実行なら <see langword="null"/>）。</summary>
    public static EncoderProbeReport? LastProbe { get; private set; }

    /// <summary>
    /// 全候補の存在を1回だけ確認し、結果を返す（<see cref="LastProbe"/> にも保持する）。
    /// <c>Controller.StaticInitialize()</c> の末尾（GStreamer 初期化の後）から呼ぶ。
    /// ログ出力は行わない ── 出力先の決定は呼び出し側の責務とする。
    /// </summary>
    public static EncoderProbeReport Probe(Func<string, bool>? probe = null)
    {
        probe ??= ProbeWithGStreamer;

        var names = D3d12Candidates.Concat(SystemCandidates)
            .Select(c => c.FactoryName)
            .Distinct()
            .ToArray();

        var entries = names.Select(n => new EncoderProbeEntry(n, probe(n))).ToArray();
        var available = entries.Where(e => e.Available).Select(e => e.FactoryName).ToHashSet(StringComparer.Ordinal);

        var report = new EncoderProbeReport(
            entries,
            Resolve(EventRecordingType.D3d12, preferred: null, probe: available.Contains).Select(c => c.FactoryName).ToArray(),
            Resolve(EventRecordingType.System, preferred: null, probe: available.Contains).Select(c => c.FactoryName).ToArray());

        LastProbe = report;
        return report;
    }

    // ---- H.264 デコーダー（録画トランスコード） --------------------------------------

    /// <summary>
    /// 録画トランスコードに使う H.264 デコーダーの候補（この順）。
    ///
    /// <para>
    /// <b>ハードウェアだけ</b>である。<c>d3d11h264dec</c> は DXVA 経由でベンダに依存しないので
    /// 先頭に置く。<b>ソフトウェアのデコーダーは同梱ランタイムに 1 つも無い</b>
    /// （<c>avdec_h264</c> / <c>openh264dec</c> はフルインストールの GStreamer にしか無い）ので、
    /// ここに書いても実機で 1 つも見つからない環境がそのまま残る ── その場合は
    /// 録画トランスコードを提供しない（<c>TranscodeCapability.Transcode</c> が false）。
    /// <b>この表の外の要素は <see cref="ProbeH264Decoder"/> の <c>preferred</c>
    /// （検証用の環境変数）でしか選ばれない。</b>
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> H264DecoderCandidates =
        (string[])["d3d11h264dec", "d3d12h264dec", "nvh264dec", "qsvh264dec"];

    /// <summary>
    /// 直近の <see cref="ProbeH264Decoder"/> の結果（未実行・1 つも無いなら <see langword="null"/>）。
    /// <c>Controller.StaticInitialize()</c> が <see cref="Probe"/> の直後に 1 回だけ設定する。
    /// </summary>
    public static string? LastH264Decoder { get; private set; }

    /// <summary>
    /// <see cref="H264DecoderCandidates"/> のうち<b>実機に在る最初の 1 つ</b>を返す
    /// （1 つも無ければ <see langword="null"/>）。結果は <see cref="LastH264Decoder"/> にも残す。
    ///
    /// <para>
    /// <paramref name="preferred"/> が空でなく<b>実機に在れば、候補表より先にそれを返す</b>。
    /// 名指しできるのは検証のためで（<c>PROCESSRECORDERAPP_H264_DECODER</c>）、
    /// 利用者側の GStreamer に <c>openh264dec</c> / <c>avdec_h264</c> のような
    /// ソフトウェアのデコーダーが在るときだけ効く ── 在らなければ候補表へ落ちるので、
    /// 綴りを間違えても「デコーダーが無い」以上の壊れ方はしない。
    /// </para>
    /// </summary>
    /// <param name="probe">存在確認。テストから差し替えられるよう引数で受ける。</param>
    /// <param name="preferred">先に試す要素名（未指定・空白のみなら候補表だけを見る）。</param>
    public static string? ProbeH264Decoder(Func<string, bool> probe, string? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(probe);

        string? found = null;

        if (!string.IsNullOrWhiteSpace(preferred) && probe(preferred))
            found = preferred;

        if (found is null)
        {
            foreach (string name in H264DecoderCandidates)
            {
                if (probe(name))
                {
                    found = name;
                    break;
                }
            }
        }

        LastH264Decoder = found;
        return found;
    }

    // ---- 選択肢一覧（設定画面用） ----------------------------------------------------

    /// <summary>
    /// <see cref="ChoicesFor"/> が返す選択肢1件の種別。
    /// <b>表示の文言はここでは決めない</b> ── ローカライズはアプリ層の責務で、
    /// この層に持ち込むと L1 から検証できなくなる。
    /// </summary>
    public enum EncoderChoiceKind
    {
        /// <summary>空文字＝自動選択。</summary>
        Automatic,

        /// <summary>カタログに定義がある要素。</summary>
        Catalog,

        /// <summary>
        /// カタログに無い現在値（古い名前・打ち間違い・手で編集された設定ファイル）。
        /// <b>存在するかどうかは分からない</b> ── プローブはカタログの名前しか調べていない。
        /// </summary>
        Unknown
    }

    /// <param name="Value">設定へ保存される値。</param>
    /// <param name="Kind">種別。</param>
    /// <param name="Available">
    /// その PC に実在するか。<see cref="EncoderChoiceKind.Catalog"/> 以外では意味を持たない
    /// （判断できないので <see langword="true"/> にしてある ── 分からないことを
    /// 「見つかりません」と表示してはいけない）。
    /// </param>
    public readonly record struct EncoderChoice(string Value, EncoderChoiceKind Kind, bool Available);

    /// <summary>
    /// <c>AppSettings.PreferredH264Encoder</c> の選択肢を組み立てる（設定画面用の純関数）。
    ///
    /// <para>
    /// <b>意図的に、その PC に実在するものだけに絞らず、カタログの全候補を出し、
    /// 実在しないものへ印を付ける。</b> 絞ると選択肢が機械ごとに変わり、
    /// 設定ファイルを別の機械へ持っていったときに<b>載っていない値</b>が入ることになる。
    /// </para>
    /// <para>
    /// <b>現在値がカタログに無い場合は、その値自体を末尾に足す。</b>
    /// 足さないとコンボボックスは<b>空を表示し、利用者が他の行に触れた瞬間にその値を失う</b>
    /// ── 編集不可なので打ち直すこともできない。このプロパティの doc コメントが
    /// 「設定ミスで録画が一切できなくなる方が有害」としているのと同じ判断。
    /// </para>
    /// <para>
    /// <b><paramref name="probe"/> が <see langword="null"/> のときは何にも印を付けない。</b>
    /// <see cref="Probe"/> は <c>Controller.StaticInitialize()</c> の末尾で走るが、
    /// <see cref="ProbeWithGStreamer"/> は例外を握り潰すので、走っていない・空で返る場合がある。
    /// そこで全件に「見つかりません」と付けると<b>画面が嘘をつく</b>
    /// ── 分からないときは黙るのが正しい。
    /// </para>
    /// </summary>
    /// <param name="currentValue">現在の設定値（空文字＝自動選択）。</param>
    /// <param name="probe">プローブ結果。<see langword="null"/> なら実在の印を付けない。</param>
    public static IReadOnlyList<EncoderChoice> ChoicesFor(string? currentValue, EncoderProbeReport? probe)
    {
        var available = probe?.Entries
            .Where(e => e.Available)
            .Select(e => e.FactoryName)
            .ToHashSet(StringComparer.Ordinal);

        List<EncoderChoice> choices = [new("", EncoderChoiceKind.Automatic, Available: true)];

        // 並びは Probe と同じ「D3d12 の優先順 → System だけにあるもの」。
        // GPU ネイティブが上に来るので、利用者から見て意味のある順序になっている。
        foreach (string name in D3d12Candidates.Concat(SystemCandidates).Select(c => c.FactoryName).Distinct())
        {
            // probe が無いときは印を付けない（= 実在扱い）
            bool exists = available is null || available.Contains(name);
            choices.Add(new(name, EncoderChoiceKind.Catalog, exists));
        }

        string current = currentValue?.Trim() ?? "";
        if (current.Length > 0 && !choices.Any(c => string.Equals(c.Value, current, StringComparison.Ordinal)))
            choices.Add(new(current, EncoderChoiceKind.Unknown, Available: true));

        return choices;
    }

    // ---- 解決 ----------------------------------------------------------------------

    /// <summary>
    /// 実機に存在する候補だけを優先順に並べて返す。
    ///
    /// <paramref name="preferred"/>（<c>AppSettings.PreferredH264Encoder</c>）が指定され、
    /// かつ実機に存在する場合は先頭へ移動する。カタログに無いファクトリ名でも、
    /// 実機に存在すればプロパティ無しの候補として先頭に加える（ユーザーの明示指定を優先する）。
    /// 指定された要素が存在しない場合は**黙って通常の優先順位へフォールスルーする**
    /// ── 設定ミスで録画が一切できなくなる方が有害なため。
    /// </summary>
    /// <param name="type">録画種別。</param>
    /// <param name="preferred">優先するファクトリ名。<see langword="null"/> または空なら自動選択。</param>
    /// <param name="probe">存在確認。テストから差し替えられるよう引数で受ける。</param>
    /// <param name="gop">
    /// 候補に付ける GOP 長（フレーム数）。省略すると既定フレームレートの値。
    /// <b>実際に流れるフレームレートから <see cref="GopForFramerate"/> で決めること</b> ──
    /// 常時録画の枝は本線より低いレートで走るのが普通で、フレーム数を共有すると
    /// そちらだけ GOP が伸びてセグメント分割が遅れる。
    /// </param>
    public static IReadOnlyList<H264EncoderDef> Resolve(
        EventRecordingType type, string? preferred, Func<string, bool> probe, int gop = GopSize)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var ordered = CandidatesFor(type, gop).Where(c => probe(c.FactoryName)).ToList();

        if (string.IsNullOrWhiteSpace(preferred))
            return ordered;

        string name = preferred.Trim();

        int index = ordered.FindIndex(c => string.Equals(c.FactoryName, name, StringComparison.Ordinal));
        if (0 <= index)
        {
            var pick = ordered[index];
            ordered.RemoveAt(index);
            ordered.Insert(0, pick);
            return ordered;
        }

        // カタログ外のファクトリ名でも、実機に存在するなら尊重する。
        // メモリ要件は不明なので、D3d12 経路では安全側（システムメモリ要求）に倒す
        // ── 余分な d3d12download が入っても動作するが、必要な download が無いと必ず失敗するため。
        if (probe(name))
            ordered.Insert(0, new H264EncoderDef(name, name, NeedsSystemMemoryFor(name, type)));

        return ordered;
    }

    /// <summary>
    /// 候補列を「実際に試す順番」へ展開する。
    /// プロパティ付きの定義は、まずその形で試し、失敗したら**プロパティ無し**でもう一度試す。
    /// 実機未確認の GPU エンコーダーでプロパティ名・単位が違っていても、
    /// そのエンコーダー自体を取りこぼさないようにするため。
    /// </summary>
    public static IEnumerable<H264EncoderDef> ExpandAttempts(IEnumerable<H264EncoderDef> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var c in candidates)
        {
            yield return c;
            var bare = c.WithoutProperties();
            if (!ReferenceEquals(bare, c))
                yield return bare;
        }
    }
}
