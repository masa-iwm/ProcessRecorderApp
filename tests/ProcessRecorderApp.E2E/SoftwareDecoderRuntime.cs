namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b>ソフトウェアの H.264 デコーダーが在るランタイムで常駐ワーカーを走らせる</b>ための設定。
///
/// <para>
/// 録画トランスコードの候補表はハードウェアのデコーダーだけなので、GPU の無い開発機と CI では
/// 変換の経路が 1 行も実行されない。ここでやることは 2 つだけである:
/// <list type="number">
///   <item>ワーカーの <c>PATH</c> の先頭へ、ソフトウェアのデコーダーを持つ GStreamer の
///     <c>bin</c> を置く。<b>ローダーは PATH の走査を最優先で解決し、勝った <c>bin</c> の
///     ランタイムを丸ごと選ぶ</b>（<c>bin</c> の隣の <c>lib\gstreamer-1.0</c> をそこから辿る）
///     ので、プラグインの混在は起きない（src/README.md「GStreamer の解決経路」）。</item>
///   <item><c>PROCESSRECORDERAPP_H264_DECODER</c> でその要素名を名指す
///     （既定は <c>openh264dec</c>）。</item>
/// </list>
/// </para>
/// <para>
/// <b>能力は各テストが断定する。</b> ここは env を置くだけで、変換が成立したかは見ない
/// ── 「無ければ skip」にすると、能力検出が壊れたときに黙って緑になる。
/// </para>
/// <para>
/// <b>この 2 つを置かないインスタンスの挙動は変わらない。</b> 既存の
/// 「この機械では変換できない」を断定するケース（<c>RemoteControlTests</c> の 2 件と
/// <c>WebUiBrowserTests</c> の 1 件）はそのまま false のまま走る。
/// </para>
/// </summary>
public static class SoftwareDecoderRuntime
{
    /// <summary>
    /// 製品側の名指しの環境変数名（<c>Components.AppEnvironment.H264DecoderVariable</c>）。
    /// E2E は製品のアセンブリを参照しないので、綴りはここに写してある
    /// （<c>AppInstance.DataDirVariable</c> と同じ流儀。綴りの一致は L1 が固定する）。
    /// </summary>
    public const string ProductDecoderVariable = "PROCESSRECORDERAPP_H264_DECODER";

    /// <summary>
    /// ワーカーの <c>PATH</c> の先頭へ置く <c>bin</c> を差し替える環境変数名。
    /// <b>空文字を明示すると「そのまま」</b>（＝実行環境の解決に任せる。CI の MSYS2 がこれ）。
    /// </summary>
    public const string GstBinVariable = "PROCESSRECORDERAPP_E2E_GST_BIN";

    /// <summary>名指しするデコーダーの要素名を差し替える環境変数名。</summary>
    public const string DecoderVariable = "PROCESSRECORDERAPP_E2E_H264_DECODER";

    /// <summary>
    /// 名指しの既定。<c>libgstopenh264.dll</c>（BSD）は公式のフルインストールにも
    /// MSYS2 の <c>gst-plugins-bad</c> にも入っている ── <c>avdec_h264</c>（LGPL の
    /// <c>libgstlibav.dll</c>）と違い、追加のパッケージが要らない。
    /// </summary>
    public const string DefaultDecoder = "openh264dec";

    /// <summary>
    /// 開発機の公式フルインストール（<see cref="GstBinVariable"/> が無いときに在れば使う）。
    /// <b>在らなければ何も足さない</b> ── CI（MSYS2）はランタイムの解決を実行環境に任せる。
    /// </summary>
    private static string DefaultGstBin => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "gstreamer", "1.0", "mingw_x86_64", "bin");

    /// <summary>名指しするデコーダーの要素名（<see cref="DecoderVariable"/>／既定）。</summary>
    public static string Decoder
    {
        get
        {
            string? name = Environment.GetEnvironmentVariable(DecoderVariable);
            return string.IsNullOrWhiteSpace(name) ? DefaultDecoder : name.Trim();
        }
    }

    /// <summary>
    /// ワーカーの <c>PATH</c> の先頭へ置く <c>bin</c>（置かないなら <see langword="null"/>）。
    /// <see cref="GstBinVariable"/> が空文字なら明示的に「置かない」。
    /// </summary>
    public static string? GstBin
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(GstBinVariable);
            if (configured is not null)
                return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();

            string fallback = DefaultGstBin;
            return Directory.Exists(fallback) ? fallback : null;
        }
    }

    /// <summary>
    /// <paramref name="instance"/> の常駐ワーカーへ、上の 2 つを渡す。
    /// <b><c>AppInstance.Create</c> の <c>configure</c> から呼ぶ</b>
    /// ── ワーカーの起動より後では効かない（デコーダーの確認は静的初期化で 1 回だけ）。
    /// </summary>
    public static void Apply(AppInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.ExtraEnvironment[ProductDecoderVariable] = Decoder;

        if (GstBin is { } bin)
        {
            instance.ExtraEnvironment["PATH"] =
                bin + ";" + Environment.GetEnvironmentVariable("PATH");
        }
    }

    /// <summary>失敗のメッセージへ入れる 1 行（どこの何を使ったか）。</summary>
    public static string Describe()
        => $"gstBin={GstBin ?? "(そのまま)"} decoder={Decoder}";
}
