using System;
using System.IO;

namespace ProcessRecorderApp.Components;

/// <summary>
/// プロセス全体で共有するパス・識別子の解決。
///
/// いずれも環境変数で上書きできる。通常起動時は環境変数が無いので挙動は既定のままであり、
/// 上書きは自動テストのためだけに存在する:
///
/// <list type="bullet">
///   <item><see cref="DataDirectoryVariable"/> ──
///     設定ファイル・ログの保存先。E2E テストが実ユーザーの
///     <c>%LOCALAPPDATA%\ProcessRecorderApp</c> を壊さないために必要。</item>
///   <item><see cref="KeyPrefixVariable"/> ──
///     単一インスタンス制御に使う名前付きオブジェクトの接頭辞。テスト実行中に開発者の
///     常駐インスタンスが居ると、テストのコマンドがそちらへ転送されてしまう
///     （逆にテストの常駐ワーカーが開発者の操作を奪う）ため、テストごとに隔離する。</item>
///   <item><see cref="LanguageVariable"/> ──
///     表示言語の強制。OS の表示言語を切り替えずに en-US / ja-JP / フォールバックを検証する。</item>
///   <item><see cref="TestDeviceArrivalVariable"/> ──
///     デバイス到着シグナルの外部注入。カメラの抜き差しもモニタの抜き差しも
///     自動テストからは起こせないので、これが無いと「到着で復帰が早まる」経路を
///     一度も実行できない。</item>
///   <item><see cref="H264DecoderVariable"/> ──
///     録画トランスコードの H.264 デコーダーの名指し。候補表はハードウェアだけなので、
///     GPU の無い機械ではこれが無いと変換の経路を一度も実行できない。</item>
/// </list>
/// </summary>
public static class AppEnvironment
{
    /// <summary>設定・ログの保存先ディレクトリを上書きする環境変数名。</summary>
    public const string DataDirectoryVariable = "PROCESSRECORDERAPP_DATA_DIR";

    /// <summary>単一インスタンス制御キーの接頭辞を上書きする環境変数名。</summary>
    public const string KeyPrefixVariable = "PROCESSRECORDERAPP_KEY_PREFIX";

    /// <summary>表示言語（BCP-47 タグ）を強制する環境変数名。</summary>
    public const string LanguageVariable = "PROCESSRECORDERAPP_LANG";

    /// <summary>
    /// 捕捉した標準出力/標準エラーを、<b>差し替える前の標準エラー</b>へも複写させる環境変数名
    /// （<c>1</c> / <c>true</c> で有効）。
    ///
    /// <para>
    /// <b>これが無いと、外からプロセスを起動した側は出力を1行も受け取れない。</b>
    /// <see cref="StandardStreamRedirector"/> は Win32・CRT・マネージドの3層すべてを
    /// プロセス内パイプへ差し替えるので、差し替え後の出力は<b>原理的に</b>
    /// 親プロセスのリダイレクト先へ届かない ── <c>ActivityLog.Initialize</c> の
    /// <c>mirrorToConsole: true</c> もこの差し替えより後ろにあるため、
    /// 「コンソールへ複写している」という意図が親からは観測できない状態になっていた。
    /// </para>
    /// <para>
    /// <b>既定で有効にはしない。</b> 有効にすると常駐ワーカーを端末から直接起動した利用者に
    /// GStreamer の出力がそのまま流れ、挙動が変わる。加えて<b>吸い出す相手が居ない状態で
    /// 有効にしてはいけない</b> ── 親がパイプを読まないと、埋まった時点で書き込みが
    /// ブロックし、読み取りスレッドごと止まる。E2E ハーネスは常時読み続けるので安全。
    /// </para>
    /// </summary>
    public const string MirrorToOriginalStdErrVariable = "PROCESSRECORDERAPP_MIRROR_STDERR";

    /// <summary>
    /// デバイス到着の外部注入を有効にする環境変数名（<c>1</c> / <c>true</c> で有効）。
    ///
    /// <para>
    /// <b>自動テストのためだけに存在する。</b> 有効なとき、録画エンジンは
    /// <see cref="TestDeviceArrivalEventName"/> の名前付きイベントを待ち、
    /// シグナルを「デバイスが戻ってきた」として扱う。
    /// <b>本物のデバイスプロバイダには影響しない</b> ── 注入は購読とは別の経路で、
    /// プロバイダを開始も停止もしない。
    /// </para>
    /// <para>
    /// これが無いと、早期復帰の経路は<b>どのテスト層でも 1 行も実行されない</b>
    /// ── 開発機にも CI にもカメラが無く、モニタの抜き差しもできないため
    /// （docs/coverage-gaps.md「デバイス到着の監視」）。
    /// </para>
    /// </summary>
    public const string TestDeviceArrivalVariable = "PROCESSRECORDERAPP_TEST_DEVICE_ARRIVAL";

    /// <summary>
    /// 録画トランスコードに使う H.264 デコーダーの要素名を名指しする環境変数名。
    ///
    /// <para>
    /// <b>検証のためだけに存在する。</b> 候補表（<c>EncoderCatalog.H264DecoderCandidates</c>）は
    /// ハードウェアのデコーダーだけなので、GPU の無い機械では変換の経路が
    /// <b>どのテスト層でも 1 行も実行されない</b>。ここで
    /// <c>openh264dec</c> / <c>avdec_h264</c> を名指すと、<b>利用者側の GStreamer に
    /// そのデコーダーが在るときだけ</b>変換が成立する（同梱ランタイムには無い）。
    /// </para>
    /// <para>
    /// <b>設定項目にはしない。</b> 名指したものが実機に無ければ候補表へ落ちるだけで、
    /// 「無い」以上の壊れ方はしない。
    /// </para>
    /// </summary>
    public const string H264DecoderVariable = "PROCESSRECORDERAPP_H264_DECODER";

    /// <summary>上書きが無い場合の単一インスタンスキー接頭辞。</summary>
    public const string DefaultKeyPrefix = nameof(ProcessRecorderApp);

    private static readonly Lazy<string> _dataDirectory = new(() => ResolveDataDirectory(
        Environment.GetEnvironmentVariable(DataDirectoryVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    private static readonly Lazy<string> _keyPrefix = new(() => ResolveKeyPrefix(
        Environment.GetEnvironmentVariable(KeyPrefixVariable)));

    /// <summary>
    /// 設定ファイル（<c>settings.json</c>）・アクティビティログの保存先ディレクトリ。
    /// 既定は <c>%LOCALAPPDATA%\ProcessRecorderApp</c>。
    /// プロセス起動後に環境変数を変えても反映されない（1度だけ解決する）。
    /// </summary>
    public static string DataDirectory => _dataDirectory.Value;

    /// <summary>
    /// 名前付き Mutex / EventWaitHandle / MemoryMappedFile / AppInstance キーの接頭辞。
    /// 既定は <see cref="DefaultKeyPrefix"/>。
    /// </summary>
    public static string KeyPrefix => _keyPrefix.Value;

    /// <summary>
    /// <see cref="DataDirectory"/> の解決規則（テストのために純粋関数として分離）。
    /// 上書き値が空白のみの場合は指定なしとみなす。相対パスは現在のディレクトリ基準で絶対化する。
    /// </summary>
    public static string ResolveDataDirectory(string? overrideValue, string localAppData)
        => string.IsNullOrWhiteSpace(overrideValue)
            ? Path.Combine(localAppData, DefaultKeyPrefix)
            : Path.GetFullPath(overrideValue.Trim());

    /// <summary><see cref="KeyPrefix"/> の解決規則（テストのために純粋関数として分離）。</summary>
    public static string ResolveKeyPrefix(string? overrideValue)
        => string.IsNullOrWhiteSpace(overrideValue) ? DefaultKeyPrefix : overrideValue.Trim();

    /// <summary>
    /// <see cref="DataDirectory"/> 配下のファイルパスを組み立てる。ディレクトリは作成しない。
    /// </summary>
    public static string GetDataFilePath(string fileName) => Path.Combine(DataDirectory, fileName);

    private static readonly Lazy<bool> _testDeviceArrival = new(() => ResolveTestDeviceArrivalEnabled(
        Environment.GetEnvironmentVariable(TestDeviceArrivalVariable)));

    /// <summary>
    /// デバイス到着の外部注入が有効か（<see cref="TestDeviceArrivalVariable"/>）。
    /// プロセス起動後に環境変数を変えても反映されない（1度だけ解決する）。
    /// </summary>
    public static bool TestDeviceArrivalEnabled => _testDeviceArrival.Value;

    /// <summary>
    /// デバイス到着の注入に使う名前付きイベントの名前。
    /// <b><see cref="KeyPrefix"/> を含める</b> ── 含めないと、並行して走る E2E の
    /// インスタンス同士が互いの復帰を起こし合う。
    /// </summary>
    public static string TestDeviceArrivalEventName => ResolveTestDeviceArrivalEventName(KeyPrefix);

    /// <summary>
    /// <see cref="TestDeviceArrivalEnabled"/> の解決規則（テストのために純粋関数として分離）。
    /// 受け付けるのは <c>1</c> と <c>true</c>（大文字小文字を問わない）だけ。
    /// </summary>
    public static bool ResolveTestDeviceArrivalEnabled(string? overrideValue)
        => overrideValue is not null
           && (overrideValue.Equals("1", StringComparison.Ordinal)
               || overrideValue.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <see cref="TestDeviceArrivalEventName"/> の組み立て規則（テストのために純粋関数として分離）。
    /// </summary>
    public static string ResolveTestDeviceArrivalEventName(string keyPrefix)
        => keyPrefix + "-DeviceArrival";
}
