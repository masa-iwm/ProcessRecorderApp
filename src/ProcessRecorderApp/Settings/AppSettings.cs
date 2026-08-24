using CommunityToolkit.Mvvm.ComponentModel;
using ProcessRecorderApp.Components;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace ProcessRecorderApp.Settings;

public partial class AppSettings : JsonSettingsBase<AppSettings>
{
    /// <summary>設定ファイルの名前（保存先ディレクトリは <c>AppEnvironment</c> が決める）。</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// 設定ファイルの隣に置く JSON Schema のファイル名。
    /// <see cref="SchemaReference"/> がこの名前を <c>$schema</c> として書き込むので、
    /// <b>相対参照のまま解決できるよう settings.json と同じディレクトリに置く</b>。
    /// </summary>
    public static readonly string SchemaFileName =
        Path.GetFileName(GetSchemaFilePath(SettingsFileName));

    // 保存先は AppEnvironment が解決する（テストは PROCESSRECORDERAPP_DATA_DIR で隔離する）
    private static readonly string FilePath = AppEnvironment.GetDataFilePath(SettingsFileName);

    /// <summary>
    /// 既定設定の「種」── <see cref="FilePath"/> が<b>存在しないとき</b>にだけ読む、
    /// 実行ファイルの隣の <c>settings.json</c>。配布物に初期設定を同梱して
    /// 「展開して起動すれば構成済み」にするためのもので、読むだけ（複写も退避もしない）。
    ///
    /// <para>
    /// 基準は <see cref="AppDirectories.BaseDirectory"/>（<c>Environment.ProcessPath</c> の
    /// ディレクトリ）で、<c>AppContext.BaseDirectory</c> ではない ── 単一ファイル発行では
    /// あちらは展開先の一時ディレクトリを指す。カレントディレクトリも使わない
    /// （常駐ワーカーは起動したシェルの CWD をプロセス寿命ぶん引きずる。
    /// <see cref="OutputDirectory"/> の解決規則と同じ理由）。
    /// </para>
    /// <para>
    /// <b>探すのは <see cref="AppEnvironment.DataDirectory"/> 側の不在であって
    /// <c>%LOCALAPPDATA%</c> の不在ではない。</b> こうしておくと E2E の
    /// <c>PROCESSRECORDERAPP_DATA_DIR</c> による隔離がそのまま効く。
    /// </para>
    /// </summary>
    private static readonly string SeedFilePath =
        Path.Combine(AppDirectories.BaseDirectory, SettingsFileName);

    /// <summary>
    /// settings.json の読み書きに使う型情報。<b>人が開いて読み・手で直すファイル</b>なので、
    /// 既定（1行・非 ASCII は <c>\uXXXX</c>）ではなくインデント付き・生の UTF-8 で書く。
    ///
    /// <para>
    /// <b><c>[JsonSourceGenerationOptions]</c> では書けない。</b> 属性の引数はコンパイル時
    /// 定数に限られ、<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> はインスタンス
    /// だからである。ソース生成器が出す <c>AppSettingsJsonContext(JsonSerializerOptions)</c> で
    /// 組み立てるのが唯一の道で、<c>WriteIndented</c> だけ属性側へ分けたりはしない
    /// （設定の定義元を2つにしない）。<b>Native AOT でも解決器はソース生成のままなので、
    /// リフレクションのフォールバックには落ちない。</b>
    /// </para>
    /// <para>
    /// <b><see cref="JsonSerializerDefaults.Web"/> を使ってはいけない。</b> キーが camelCase に
    /// なり、既存の settings.json（PascalCase）が丸ごと読めなくなる。
    /// </para>
    /// </summary>
    private static readonly AppSettingsJsonContext SettingsContext =
        new(new JsonSerializerOptions
        {
            WriteIndented = true,
            // 書き出し先は HTML ではなく設定ファイルなので、< > & ' を \uXXXX へ
            // 逃がす必要が無い（「Unsafe」はその HTML 文脈での意味）。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <inheritdoc cref="SettingsContext"/>
    ///
    /// <para>
    /// <b>internal なのは、部分更新（リモート操作の PATCH）が保存と同じ型情報を通すため。</b>
    /// あちらは「現在値を JSON にする → 要求のキーだけ差し替える → 型へ戻す」で値を検証するので、
    /// 別の <c>JsonSerializerOptions</c> を使うと settings.json に書ける値と
    /// PATCH で通る値が食い違う。
    /// </para>
    internal static readonly JsonTypeInfo<AppSettings> SettingsTypeInfo = SettingsContext.AppSettings;

    /// <summary>
    /// レコーダー設定 1 件ぶんの型情報。用途と理由は <see cref="SettingsTypeInfo"/> と同じ。
    /// </summary>
    internal static readonly JsonTypeInfo<GStreamer.EventRecorderSettings> RecorderTypeInfo =
        SettingsContext.EventRecorderSettings;

    private static readonly Lazy<AppSettings> _default = new(
        () => LoadOrCreate(FilePath, SettingsTypeInfo, () => new(), ReportLoadFailure,
                           SeedFilePath, ReportSeedUsed));

    /// <summary>
    /// settings.json を読めなかったことを <c>activity.log</c> に残す。
    ///
    /// <para>
    /// <b>これが無いと、利用者の設定（レコーダー定義・トリガ定義・永続化した変数・保存先）が
    /// 丸ごと既定値へ戻ったことがどこにも残らない。</b>
    /// 記録を <c>JsonSettingsBase</c> 側に置かないのは、あちらが <c>Components</c> にあり
    /// L1 から直接呼ばれるため ── そこで書くと、テストを回しただけで実ユーザーの
    /// activity.log が汚れる（<c>RecordingCleanup</c> と同じ判断）。
    /// </para>
    /// </summary>
    private static void ReportLoadFailure(string detail)
        => Components.ActivityLog.Error("settings.load", detail + "; falling back to the defaults");

    /// <summary>
    /// 実行ファイルの隣の <c>settings.json</c> を既定設定として読んだことを
    /// <c>activity.log</c> に残す（<see cref="SeedFilePath"/>）。
    ///
    /// <para>
    /// <b>記録が無いと「設定した覚えのない初期値で始まった」ことを誰も追えない。</b>
    /// 種を読むのは保存先に settings.json が無いときだけなので、この行が出るのは
    /// 実質「初回起動」か「利用者が設定ファイルを消したあと」に限られる。
    /// </para>
    /// </summary>
    private static void ReportSeedUsed(string path)
        => Components.ActivityLog.Info("settings.seed", $"file='{path}'");

    /// <summary>
    /// settings.json を書けなかったことを <c>activity.log</c> に残す
    /// （<see cref="ReportLoadFailure"/> と対称。記録を <c>JsonSettingsBase</c> 側に
    /// 置かない理由もあちらと同じ）。書けなかった変更は次の保存契機
    /// （デバウンス・トレイ格納・終了）で改めて書かれる。
    /// </summary>
    private static void ReportSaveFailure(string detail)
        => Components.ActivityLog.Error("settings.save", detail);

    public static AppSettings Default => _default.Value;

    /// <summary>
    /// 現在の settings.json のスキーマ版。
    ///
    /// <para>
    /// <b>本アプリは未リリースで利用者がいないため、この一連の改修も含めて版は 1 のまま</b>
    /// とする（開発中の増減をいちいち版に反映しても、移行すべき既存ファイルが
    /// 世の中に存在しない以上、意味が無いため）。
    /// 一度リリースした後に**非**加算的な変更を入れるときに初めて 2 へ上げ、
    /// <see cref="Migrate"/> に変換を書く。
    /// </para>
    /// </summary>
    public const int CurrentDataVersion = 1;

    /// <summary>将来的に設定ファイル構造の変更に備えるためのDataVersion</summary>
    [System.ComponentModel.Browsable(false)]
    public int DataVersion { get; internal set; } = CurrentDataVersion;

    [System.ComponentModel.Browsable(false)]
    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; internal set; } = [];

    /// <summary>
    /// 隣に置いた JSON Schema への参照（<c>settings.json</c> の <c>$schema</c> キー）。
    ///
    /// <para>
    /// <b>実体のあるプロパティにしてある。</b> 宣言しないと、手で書かれた <c>$schema</c> が
    /// <see cref="ExtensionData"/> に落ち、こちらが書き出す <c>$schema</c> と合わせて
    /// <b>同じキーが2回出る壊れた JSON</b> になる。
    /// </para>
    /// <para>
    /// <b>読み込んだ値は上書きしない。</b> 利用者が別の場所（社内で配布したスキーマ等）を
    /// 指していることがありうるので、既定値は「キーが無かったとき」にだけ効く。
    /// </para>
    /// <para>
    /// <see cref="JsonPropertyOrderAttribute"/> で先頭へ出すのは、人がファイルを開いたときに
    /// 「これは何のファイルで、どこに定義があるか」が最初に見えるようにするため。
    /// </para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-1)]
    // setter が internal なので [JsonInclude] が要る。無いとソース生成の逆シリアライズが
    // 代入をせず、手で書かれた値が次の保存で黙って既定値へ戻る（上の約束が破れる）。
    // 同じファイルの UiaTriggers / TemplateVariables / Recorders / UiaTriggerAssignments と同じ形。
    [JsonInclude]
    public string SchemaReference { get; internal set; } = SchemaFileName;

    // ウィンドウの位置/サイズ等、アプリが自動保存する内部状態のため PropertyGrid には表示しない。
    [System.ComponentModel.Browsable(false)]
    [ObservableProperty]
    public partial int WindowWidth { get; set; } = 1280;
    [System.ComponentModel.Browsable(false)]
    [ObservableProperty]
    public partial int WindowHeight { get; set; } = 720;
    [System.ComponentModel.Browsable(false)]
    [ObservableProperty]
    public partial double SettingsWidth { get; set; } = 300;

    /// <summary>Recorder 削除時に確認ダイアログを表示するかどうか。</summary>
    [System.ComponentModel.Description("PropDesc_ConfirmRecorderRemoval")]
    [ObservableProperty]
    public partial bool ConfirmRecorderRemoval { get; set; } = true;

    /// <summary>Preview 画面のプロパティペインの折りたたみ状態。</summary>
    [System.ComponentModel.Browsable(false)]
    [ObservableProperty]
    public partial bool IsPropertyPaneCollapsed { get; set; } = false;

    /// <summary>
    /// プレビューへ重ねる構図補助線の種類（既定は出さない）。
    ///
    /// <para>
    /// <b>アプリ全体の設定であってレコーダーごとではない</b> ── プレビュー面は
    /// プロセス内に 1 面しか無く（<c>d3d12swapchainsink</c> のスワップチェーンを共有）、
    /// 見えているのは常に選択中の 1 台だからである。
    /// </para>
    /// <para>
    /// 線は<b>パネル全体ではなく映像の矩形</b>に引く（<c>FramingGridGeometry</c>）──
    /// シンクが <c>force-aspect-ratio=true</c> でレターボックスを作るため。
    /// </para>
    /// <para>
    /// <b>settings.json には名前で書く</b>（<c>PaneDisplayMode</c> と同じ理由）。
    /// 画面に出す文言だけを訳すために <c>ChoiceList</c> を付けてある
    /// ── <b>保存値は列挙型の名前のまま</b>なので、設定ファイルの形も
    /// <c>docs/settings.schema.json</c> も変わらない。
    /// </para>
    /// </summary>
    [System.ComponentModel.Description("PropDesc_FramingGrid")]
    [Components.ChoiceList(FramingGridChoiceListKey)]
    [JsonConverter(typeof(JsonStringEnumConverter<FramingGridKind>))]
    [ObservableProperty]
    public partial FramingGridKind FramingGrid { get; set; } = FramingGridKind.None;

    /// <summary>
    /// <see cref="FramingGrid"/> の選択肢一覧の識別キー。
    /// <b><c>PropCat_</c> / <c>PropDesc_</c> で始めてはいけない</b>（<see cref="EncoderChoiceListKey"/> と同じ理由）。
    /// 一覧の正本は <c>Components.FramingGridChoices</c>。
    /// </summary>
    public const string FramingGridChoiceListKey = "FramingGrid";

    /// <summary>
    /// NavigationView のペイン表示モード。
    ///
    /// <para>
    /// <b>settings.json には名前で書く</b>（<c>EventRecordingType</c> と同じ理由）。
    /// 型が WinUI 側にあり属性を付けられないので、変換器はプロパティで指定する。
    /// </para>
    /// </summary>
    [System.ComponentModel.Description("PropDesc_PaneDisplayMode")]
    [JsonConverter(typeof(JsonStringEnumConverter<Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode>))]
    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode PaneDisplayMode { get; set; }
        = Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.Top;

    /// <summary>
    /// GStreamer のデバッグ出力レベル（環境変数 <c>GST_DEBUG</c> と同じ書式）。<b>変更時に即時反映</b>。
    ///
    /// <para>
    /// <b>起動時と変更時で経路が違う。</b> 起動時は
    /// <see cref="ApplyStartupEnvironmentVariables"/> が環境変数として渡す
    /// ── 外部で <c>GST_DEBUG</c> が設定済みならそちらを尊重して上書きしない。
    /// 変更時は <c>gst_debug_set_threshold_from_string</c> を直接呼ぶので、
    /// 外部指定があっても利用者の操作が勝つ。この非対称は意図である。
    /// </para>
    /// </summary>
    [System.ComponentModel.Category("PropCat_Debug")]
    [System.ComponentModel.Description("PropDesc_GstDebug")]
    [ObservableProperty]
    public partial string GstDebug { get; set; } = "";
    // 読み込み（逆シリアル化）でもここへ来るが、そのときはまだ Gst.Functions.Init 前なので
    // TrySetThreshold が false を返して何もしない（起動時の反映は環境変数側の担当）。
    // UI からの編集と Reload() はウィンドウが立った後なので、必ず Init 後に来る。
    // **OnLoaded() では再代入しない** ── そこで押し込むと外部の GST_DEBUG を黙って
    // 上書きすることになり、しかも Init 前なので落ちる。
    partial void OnGstDebugChanged(string value)
        => GStreamer.DebugLogEx.TrySetThreshold(value);

    /// <summary>
    /// パイプライングラフ(.dot)の出力先ディレクトリ。相対パスは Exe のあるディレクトリ基準。
    ///
    /// <para>
    /// Log 画面の「グラフを保存」はこの値を<b>押すたびに読む</b>ので即時反映される。
    /// 一方 <b>GStreamer 内部のダンプに効かせるには起動時の指定が要る</b>
    /// ── 環境変数 <c>GST_DEBUG_DUMP_DOT_DIR</c> は <c>gst_init</c> の時点でしか
    /// 読まれず、後から書き換えても反映する手段が GStreamer 側に無い。
    /// </para>
    /// </summary>
    [System.ComponentModel.Category("PropCat_Debug")]
    [System.ComponentModel.Description("PropDesc_GstDebugDumpDotDir")]
    // 「…」でフォルダー選択ダイアログを開く。**読み取り専用にはしない**
    // （空欄＝データディレクトリ、と相対パスはダイアログでは表せない。OutputDirectory と同じ判断）。
    [ValueBuilder(GstDebugDumpDotDirBuilderKey)]
    [ObservableProperty]
    public partial string GstDebugDumpDotDir { get; set; } = "";

    /// <summary>
    /// <see cref="GstDebugDumpDotDir"/> のビルダー識別キー。
    /// <b><c>PropCat_</c> / <c>PropDesc_</c> で始めてはいけない</b>（<see cref="EncoderChoiceListKey"/> と同じ理由）。
    /// </summary>
    public const string GstDebugDumpDotDirBuilderKey = "GstDebugDumpDotDir";

    /// <summary>デバッグログの保存先ファイルパス。設定変更時に即時反映。空欄なら保存しない。</summary>
    [System.ComponentModel.Category("PropCat_Debug")]
    [System.ComponentModel.Description("PropDesc_DebugLogFile")]
    // 「…」でファイル保存ダイアログを開く。**読み取り専用にはしない**（上と同じ理由。
    // 空欄＝保存しない、は直接入力でしか表せない）。
    [ValueBuilder(DebugLogFileBuilderKey)]
    [ObservableProperty]
    public partial string DebugLogFile { get; set; } = "";

    /// <summary>
    /// <see cref="DebugLogFile"/> のビルダー識別キー。
    /// <b><c>PropCat_</c> / <c>PropDesc_</c> で始めてはいけない</b>（<see cref="EncoderChoiceListKey"/> と同じ理由）。
    /// </summary>
    public const string DebugLogFileBuilderKey = "DebugLogFile";

    /// <summary>
    /// Log 画面が保持する行数の上限。超えた分は古い側から破棄し、
    /// <b>破棄した行数は画面へ明示する</b>（無言で消さない）。
    /// 値の丸めは <see cref="Components.LogBuffer.MaxLines"/> 側で行う
    /// ── setter で丸めると <c>[ObservableProperty]</c> の再入になる。
    /// </summary>
    [System.ComponentModel.Category("PropCat_Debug")]
    [System.ComponentModel.Description("PropDesc_LogScrollbackLines")]
    [ObservableProperty]
    public partial int LogScrollbackLines { get; set; } = Components.LogBuffer.DefaultMaxLines;
    partial void OnLogScrollbackLinesChanged(int value)
        => Components.LogBuffer.Shared.MaxLines = value;

    /// <summary>
    /// 優先する H.264 エンコーダーの要素ファクトリ名（空欄なら実機に存在するものから自動選択）。
    /// 指定した要素が実機に存在しない場合は、黙って自動選択へフォールバックする
    /// （設定ミスで録画が一切できなくなる方が有害なため）。
    /// 実際に採用されたエンコーダーは各レコーダーの <c>ActualEncodingProperties</c> に出る。
    /// </summary>
    [System.ComponentModel.Category("PropCat_Encoding")]
    [System.ComponentModel.Description("PropDesc_PreferredEncoder")]
    // 選択肢は実行時の状況（その PC に何が在るか）で決まるので、型からは決められない。
    // MainPage が PropertyGridView.ChoiceProvider にこのキーで応答する。
    // **型は string のまま**にしてある ── 列挙型にすると settings.json の形が変わり、
    // 未リリースのあいだ DataVersion を 1 に固定する方針と衝突する。
    [Components.ChoiceList(EncoderChoiceListKey)]
    [ObservableProperty]
    public partial string PreferredH264Encoder { get; set; } = "";

    /// <summary>
    /// <see cref="PreferredH264Encoder"/> の選択肢一覧の識別キー。
    /// <b><c>PropCat_</c> / <c>PropDesc_</c> で始めてはいけない</b>
    /// ── L4 がその形のリテラルをリソースキー参照として拾うため。
    /// </summary>
    public const string EncoderChoiceListKey = "H264Encoder";
    partial void OnPreferredH264EncoderChanged(string value)
        => GStreamer.EventRecorder.PreferredH264Encoder = value;

    /// <summary>
    /// 録画停止時に、src パイプラインの排出（EOS）を待つ上限(ms)。
    ///
    /// 無限待ちにしてはいけない ── mux が詰まると停止を要求したスレッドごと永久にハングする。
    /// <b>ランチャーの結果待ち（60 秒）より十分小さくすること</b> ── 超えると
    /// <c>stop-recording</c> が終了コード 2（結果待ちタイムアウト）を返し始める。
    /// 目安は 50000 以下。
    /// </summary>
    [System.ComponentModel.Category("PropCat_Recorders")]
    [System.ComponentModel.Description("PropDesc_StopFinalizeTimeout")]
    [ObservableProperty]
    public partial int StopFinalizeTimeoutMs { get; set; } = GStreamer.EventRecorder.DefaultStopFinalizeTimeoutMs;
    partial void OnStopFinalizeTimeoutMsChanged(int value)
        => GStreamer.EventRecorder.StopFinalizeTimeoutMs = value;

    /// <summary>
    /// 録画ファイルの保存先。
    ///
    /// <para>
    /// <b>空欄のときは実行ファイルのあるディレクトリ。</b> 相対パスを書いた場合も
    /// そこからの相対になる（<c>GstDebugDumpDotDir</c> と同じ規約。<c>AppDirectories</c>）。
    /// <see cref="GStreamer.EventRecorderSettings.FilenameTemplate"/> が相対パスのとき、
    /// このディレクトリからの相対として解決される。テンプレートが絶対パスなら無視される。
    /// </para>
    /// <para>
    /// <b>プロセスのカレントディレクトリ基準にしてはいけない</b> ── 常駐ワーカーは
    /// 最初に起動したシェルの作業ディレクトリを引きずるため、保存先が起動のしかたで変わる。
    /// </para>
    /// </summary>
    [System.ComponentModel.Category("PropCat_Output")]
    [System.ComponentModel.Description("PropDesc_OutputDirectory")]
    // 「…」でフォルダー選択ダイアログを開く。**読み取り専用にはしない** ── 空欄（＝実行ファイルの
    // あるディレクトリ）と相対パスはダイアログでは表せないので、直接入力の道を残す。
    [ValueBuilder(OutputDirectoryBuilderKey)]
    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = "";
    partial void OnOutputDirectoryChanged(string value)
        => GStreamer.EventRecorder.OutputDirectory = Components.AppDirectories.ResolveOrBase(value);

    /// <summary>
    /// 初期化に失敗したパイプラインのグラフ（<c>.dot</c>）の保存先も、Log 画面の
    /// 「グラフを保存」と同じ設定を使う。空欄なら書かない（頼まれていない場所へ撒かない）。
    /// </summary>
    partial void OnGstDebugDumpDotDirChanged(string value)
        => GStreamer.EventRecorder.DebugDumpDotDirectory = Components.AppDirectories.ResolveOptional(value) ?? "";

    /// <summary>
    /// <see cref="OutputDirectory"/> のビルダー識別キー。
    /// <b><c>PropCat_</c> / <c>PropDesc_</c> で始めてはいけない</b>（<see cref="EncoderChoiceListKey"/> と同じ理由）。
    /// </summary>
    public const string OutputDirectoryBuilderKey = "OutputDirectory";

    /// <summary>
    /// 保存先の中の mp4 を、この日数を過ぎたら自動削除する。<b>0 なら削除しない</b>（既定）。
    /// </summary>
    [System.ComponentModel.Category("PropCat_Output")]
    [System.ComponentModel.Description("PropDesc_RecordingRetentionDays")]
    [ObservableProperty]
    public partial int RecordingRetentionDays { get; set; }

    /// <summary>
    /// 自動削除を実行する間隔（時間）。
    /// <b>起動直後にも1回実行する</b>ので、間隔を長くしても「起動のたびに掃除される」。
    /// </summary>
    [System.ComponentModel.Category("PropCat_Output")]
    [System.ComponentModel.Description("PropDesc_RecordingCleanupIntervalHours")]
    [ObservableProperty]
    public partial int RecordingCleanupIntervalHours { get; set; } = 6;

    /// <summary>
    /// LAN の別 PC のブラウザから操作するための HTTP サーバーを動かすか。
    ///
    /// <para>
    /// <b>既定は無効。</b> 有効にすると <see cref="RemoteControlBindAddress"/> の
    /// アドレスで待ち受け、<b>読み取り（一覧・状態・設定の閲覧）は認証なしで応答する</b>
    /// ── 守っているのは「オフ既定」「アクセストークン」「Windows Firewall」の 3 つだけである。
    /// </para>
    /// </summary>
    [System.ComponentModel.Category("PropCat_RemoteControl")]
    [System.ComponentModel.Description("PropDesc_RemoteControlEnabled")]
    [ObservableProperty]
    public partial bool RemoteControlEnabled { get; set; }

    /// <summary>
    /// 有効化したときにトークンが空なら、その場で生成する。
    /// <b>空のまま起動させない</b> ── トークン無しで待ち受けている状態を作らないため。
    /// </summary>
    partial void OnRemoteControlEnabledChanged(bool value)
    {
        if (value && string.IsNullOrEmpty(RemoteControlAccessToken))
            RemoteControlAccessToken = RemoteApiRules.GenerateAccessToken();
    }

    /// <summary>
    /// 待ち受ける IP アドレス。既定の <c>0.0.0.0</c> は全インターフェイス
    /// （「別 PC から操作する」が目的そのものなので、<c>127.0.0.1</c> では成立しない）。
    /// </summary>
    [System.ComponentModel.Category("PropCat_RemoteControl")]
    [System.ComponentModel.Description("PropDesc_RemoteControlBindAddress")]
    [ObservableProperty]
    public partial string RemoteControlBindAddress { get; set; } = "0.0.0.0";

    /// <summary>待ち受けるポート。<b>0 は「空いているものを OS に選ばせる」</b>。</summary>
    [System.ComponentModel.Category("PropCat_RemoteControl")]
    [System.ComponentModel.Description("PropDesc_RemoteControlPort")]
    [ObservableProperty]
    public partial int RemoteControlPort { get; set; } = 8752;

    /// <summary>
    /// 書き込み操作に要るアクセストークン。<b>読み取り専用</b>（選択・コピーはできる）で、
    /// 値を変える手段は<b>「…」ボタンでの再生成だけ</b>
    /// （<c>[ReadOnly(true)]</c> ＋ <c>[ValueBuilder]</c> ＝「ビルダーでのみ変更できる」）。
    /// <see cref="RemoteControlEnabled"/> を有効にした時点で空なら自動生成される。
    /// </summary>
    [System.ComponentModel.Category("PropCat_RemoteControl")]
    [System.ComponentModel.Description("PropDesc_RemoteControlAccessToken")]
    [System.ComponentModel.ReadOnly(true)]
    [ValueBuilder(RemoteControlAccessTokenBuilderKey)]
    [ObservableProperty]
    public partial string RemoteControlAccessToken { get; set; } = "";

    /// <summary>
    /// <see cref="RemoteControlAccessToken"/> のビルダー識別キー。
    /// <b><c>PropCat_</c> / <c>PropDesc_</c> で始めてはいけない</b>（<see cref="EncoderChoiceListKey"/> と同じ理由）。
    /// </summary>
    public const string RemoteControlAccessTokenBuilderKey = "RemoteControlAccessToken";

    /// <summary>
    /// UIA トリガ監視全体の有効/無効。無効にすると監視スレッドごと止める
    /// （<c>UiaTriggerService</c> がこのプロパティの変更を購読している）。
    /// </summary>
    [System.ComponentModel.Category("PropCat_Triggers")]
    [System.ComponentModel.Description("PropDesc_UiaTriggersEnabled")]
    [ObservableProperty]
    public partial bool UiaTriggersEnabled { get; set; } = true;

    /// <summary>
    /// トリガ一覧の編集の起動口。<b>現在のトリガ件数を表示するだけ</b>で、直接は編集できない
    /// （<c>[ReadOnly(true)]</c> ＋ <c>[ValueBuilder]</c> ＝「ビルダーでのみ変更できる」）。
    /// 「…」ボタンでトリガ一覧エディタが開く（MainPage が
    /// <c>PropertyGridView.ValueBuilder</c> にこのキーで応答する）。
    /// トリガの正本は <see cref="UiaTriggers"/> なので、この値は永続化しない。
    /// </summary>
    [System.ComponentModel.Category("PropCat_Triggers")]
    [System.ComponentModel.Description("PropDesc_UiaTriggerList")]
    [System.ComponentModel.ReadOnly(true)]
    [ValueBuilder(UiaTriggerBuilderKey)]
    [JsonIgnore]
    [ObservableProperty]
    public partial string UiaTriggerList { get; set; } = "";

    /// <summary>
    /// <see cref="UiaTriggerList"/> に出す件数の表示文字列。
    /// キーは文字列リテラルで渡す ── 補間で組むと L4 が参照を拾えず、未参照キーとして報告される。
    /// </summary>
    private static string FormatTriggerCount(int count)
        => Components.Localization.GetString("Resources/TriggerList_Count", count);

    partial void OnUiaTriggersChanged(List<UiaTrigger.Models.TriggerDefinition> value)
        // null は手で編集された settings.json（"UiaTriggers": null）で起こりうる
        => UiaTriggerList = FormatTriggerCount(value is null ? 0 : value.Count);

    /// <summary>
    /// <see cref="UiaTriggerList"/> のビルダー識別キー。
    /// <b><c>PropCat_</c> / <c>PropDesc_</c> で始めてはいけない</b>（<see cref="EncoderChoiceListKey"/> と同じ理由）。
    /// </summary>
    public const string UiaTriggerBuilderKey = "UiaTriggerList";

    /// <summary>
    /// UIA トリガ定義の一覧（正本）。編集はトリガ一覧エディタ経由のみで、PropertyGrid には出さない。
    ///
    /// <para>
    /// エディタは常に新しい写しを返すので、<b>変更はリストごと差し替える</b>（要素の in-place
    /// 変更をしない）。PropertyChanged が飛んでデバウンス保存と監視の再起動が効くのは
    /// その形だけである。差し替え運用なのでバックグラウンド（監視サービス）からの
    /// 参照読みも安全になる。
    /// </para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [JsonInclude]
    [ObservableProperty]
    public partial List<UiaTrigger.Models.TriggerDefinition> UiaTriggers { get; internal set; } = [];

    /// <summary>
    /// テンプレート変数（<c>{キー名}</c>）のうち、<b>永続化するよう明示的に指定されたもの</b>。
    ///
    /// <para>
    /// <b>実行時の真実はここではなく <see cref="GStreamer.EventRecorder.TemplateVariables"/></b>
    /// （CLI の <c>--set</c> と Variables 画面が既に共有している static ストア）。
    /// このプロパティは保存／復元のための器で、<see cref="Save"/> の瞬間に
    /// static ストアから値を取り直す ── 変更イベントを取りこぼしても
    /// 永続化される値が正しくなるようにするため。
    /// </para>
    /// <para>
    /// <b>「ここに載っているキー」＝「永続化する」と定義する</b>（意図的な仕様）。
    /// static ストア全体を無条件にスナップショットしてはいけない ── 意図に反する。
    /// <c>--set</c> はセッション限りが既定で、永続化は <c>--persist</c> か
    /// Variables 画面の「保存」列で明示したものだけが対象になる。
    /// 別のフラグを増やさずキーの有無で表すことで、<b>settings.json の形は変わらない</b>
    /// ── 既存のファイルは全キーが載っているので「永続化指定済み」として素直に読め、
    /// 移行も <see cref="DataVersion"/> の更新も要らない。
    /// </para>
    /// <para>
    /// 値の型が <see cref="string"/> なのは Native AOT の都合
    /// （<c>object</c> 値は System.Text.Json のソース生成で扱えない）。
    /// </para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [JsonInclude]
    public Dictionary<string, string> TemplateVariables { get; internal set; } = [];

    /// <summary>そのテンプレート変数が永続化対象として指定されているか。</summary>
    public bool IsTemplateVariablePersistent(string key) => TemplateVariables.ContainsKey(key);

    /// <summary>
    /// テンプレート変数の永続化指定を切り替える。指定した瞬間の値も入れる
    /// ── <see cref="Save"/> は<b>既にあるキーを更新するだけ</b>なので、
    /// ここで足さないと <c>--persist</c> が黙って何もしないことになる。
    /// </summary>
    /// <returns>指定が実際に変わったら true。</returns>
    public bool SetTemplateVariablePersistent(string key, bool persistent)
    {
        if (persistent)
        {
            if (TemplateVariables.ContainsKey(key))
                return false;
            TemplateVariables[key] = GStreamer.EventRecorder.TemplateVariables.TryGetValue(key, out var value)
                ? value
                : string.Empty;
        }
        else if (!TemplateVariables.Remove(key))
        {
            return false;
        }

        // Dictionary を差し替えていないので PropertyChanged は飛ばない。明示的に保存を予約する。
        RequestSave();
        return true;
    }

    /// <summary>
    /// テンプレート変数の改名に永続化指定を追随させる。
    /// 追随させないと、名前を変えた瞬間に「保存する」指定だけが古い名前に残る。
    /// </summary>
    public void RenameTemplateVariablePersistence(string oldKey, string newKey)
    {
        if (!TemplateVariables.Remove(oldKey))
            return;
        TemplateVariables[newKey] = GStreamer.EventRecorder.TemplateVariables.TryGetValue(newKey, out var value)
            ? value
            : string.Empty;
        RequestSave();
    }

    [System.ComponentModel.Category("PropCat_Recorders")]
    [System.ComponentModel.Description("PropDesc_Recorders")]
    [CollectionItemType(typeof(GStreamer.EventRecorderSettings))]
    [JsonInclude]
    [ObservableProperty]
    public partial ObservableCollection<GStreamer.EventRecorderSettings> Recorders { get; internal set; } = [];

    /// <summary>
    /// レコーダー設定を複製する（「追加時に既存の設定をコピーする」ための唯一の口）。
    ///
    /// <para>
    /// <b>プロパティを1つずつ書き写す <c>Clone()</c> を書いてはいけない。</b>
    /// レコーダー設定は既に手書きのミラーを4箇所持っており（<c>EventRecorder</c> の
    /// <c>Settings_PropertyChanged</c> と ctor、<c>GstEventRecorderViewModel</c> の
    /// <c>ApplyModelChange</c> と ctor。<c>RecorderSettingsMirrorTests</c> が縛る）、
    /// 手書きの複製は<b>検出器の無い5つ目のミラー</b>になって静かに腐る
    /// ── 増やしたプロパティがコピーされないという形で、例外もテストの赤も出さずに。
    /// ソース生成 JSON の往復なら、プロパティが増えても自動で追随する。
    /// </para>
    /// <para>
    /// 名前は写したままにする ── 一意化は追加の流れ（<c>GstControllerViewModel.AddRecorder</c>）が
    /// <c>RecorderNaming.MakeUnique</c> で行う。ここで変えると、その規則が2箇所に分かれる。
    /// </para>
    /// </summary>
    public static GStreamer.EventRecorderSettings CloneRecorder(GStreamer.EventRecorderSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string json = JsonSerializer.Serialize(source, SettingsContext.EventRecorderSettings);
        return JsonSerializer.Deserialize(json, SettingsContext.EventRecorderSettings)
               ?? new GStreamer.EventRecorderSettings();
    }

    partial void OnRecordersChanging(ObservableCollection<GStreamer.EventRecorderSettings> oldValue, ObservableCollection<GStreamer.EventRecorderSettings> newValue)
    {
        if (oldValue == newValue)
            return;
        oldValue?.CollectionChanged -= Recorders_CollectionChanged;
        newValue?.CollectionChanged += Recorders_CollectionChanged;

        // **要素の購読もコレクションと一緒に張り替える。** 逆シリアライズは「要素を
        // 詰め終えたコレクションを setter へ代入する」ため、既に入っている要素の
        // Add イベントは来ない。ここで張り替えないと、settings.json から読み込んだ
        // レコーダー（＝実運用の通常ケース）の子プロパティ編集が
        // RecordersChild_PropertyChanged に届かず、デバウンス自動保存が保存契機に
        // ならない（保存されるのは終了時とトレイ格納時だけに退化する）。
        foreach (var s in _subscribedRecorders)
            s.PropertyChanged -= RecordersChild_PropertyChanged;
        _subscribedRecorders.Clear();
        if (newValue is not null)
            foreach (var s in newValue)
                SubscribeRecorder(s);
    }
    [JsonConstructor]
    internal AppSettings()
    {
        Recorders.CollectionChanged += Recorders_CollectionChanged;
        UiaTriggerAssignments.CollectionChanged += UiaTriggerAssignments_CollectionChanged;
    }

    /// <summary>
    /// PropertyChanged を購読済みのレコーダー設定。
    /// Reset の通知はコレクションが既に空になった後に来るため、コレクション自体を
    /// 走査しても購読解除できない（＝購読が漏れる）。購読対象を別途保持して解除する。
    /// </summary>
    private readonly List<GStreamer.EventRecorderSettings> _subscribedRecorders = [];

    private void SubscribeRecorder(GStreamer.EventRecorderSettings s)
    {
        if (_subscribedRecorders.Contains(s))
            return;
        s.PropertyChanged += RecordersChild_PropertyChanged;
        _subscribedRecorders.Add(s);
    }

    private void UnsubscribeRecorder(GStreamer.EventRecorderSettings s)
    {
        if (!_subscribedRecorders.Remove(s))
            return;
        s.PropertyChanged -= RecordersChild_PropertyChanged;
    }

    private void Recorders_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                    foreach (GStreamer.EventRecorderSettings s in e.NewItems)
                        SubscribeRecorder(s);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                    foreach (GStreamer.EventRecorderSettings s in e.OldItems)
                        UnsubscribeRecorder(s);
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is not null && e.NewItems is not null
                    && !e.OldItems.OfType<GStreamer.EventRecorderSettings>().SequenceEqual(
                        e.NewItems.OfType<GStreamer.EventRecorderSettings>()))
                {
                    foreach (GStreamer.EventRecorderSettings s in e.OldItems)
                        UnsubscribeRecorder(s);
                    foreach (GStreamer.EventRecorderSettings s in e.NewItems)
                        SubscribeRecorder(s);
                }
                break;
            case NotifyCollectionChangedAction.Reset:
                foreach (var s in _subscribedRecorders)
                    s.PropertyChanged -= RecordersChild_PropertyChanged;
                _subscribedRecorders.Clear();
                break;
        }

        // デバウンス保存の契機。**AttachAutoSave から直接 Recorders.CollectionChanged を
        // 購読してはいけない** ── Recorders はプロパティごと差し替わりうる
        // （`[JsonInclude]` + internal setter で逆シリアライズ時に代入される）。
        // その場合、別に張ったラムダは古いインスタンスに残って無音で効かなくなる。
        // このハンドラは OnRecordersChanging が張り替えるので、差し替えに追随する。
        RequestSave();
    }

    private void RecordersChild_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        int idx = Recorders.IndexOf((GStreamer.EventRecorderSettings)sender!);
        if (0 <= idx)
            Recorders[idx] = Recorders[idx];
    }

    /// <summary>
    /// トリガ ID ごとの録画アクション割り当て。行の増減はトリガ編集
    /// （<see cref="TriggerAssignmentReconciler"/>）が管理し、PropertyGrid では各行の
    /// 値（アクション・対象）だけを編集する ── だから <c>[CollectionItemType]</c> は
    /// 付けない（Add ボタンを出さない。Remove で行を消しても次の編集で復活し、実害はない）。
    /// 変数の反映は割り当てに関係なく常に行われる。
    /// </summary>
    [System.ComponentModel.Category("PropCat_Triggers")]
    [System.ComponentModel.Description("PropDesc_UiaTriggerAssignments")]
    [JsonInclude]
    [ObservableProperty]
    public partial ObservableCollection<UiaTriggerAssignment> UiaTriggerAssignments { get; internal set; } = [];

    partial void OnUiaTriggerAssignmentsChanging(ObservableCollection<UiaTriggerAssignment> oldValue, ObservableCollection<UiaTriggerAssignment> newValue)
    {
        if (oldValue == newValue)
            return;
        oldValue?.CollectionChanged -= UiaTriggerAssignments_CollectionChanged;
        newValue?.CollectionChanged += UiaTriggerAssignments_CollectionChanged;

        // 要素の購読もコレクションと一緒に張り替える（理由は OnRecordersChanging と同じ ──
        // 逆シリアライズで代入されるコレクションの既存要素は Add イベントを発火しない）。
        foreach (var a in _subscribedAssignments)
            a.PropertyChanged -= AssignmentsChild_PropertyChanged;
        _subscribedAssignments.Clear();
        if (newValue is not null)
            foreach (var a in newValue)
                SubscribeAssignment(a);
    }

    /// <summary>購読済みの割り当て行（保持する理由は <see cref="_subscribedRecorders"/> と同じ）。</summary>
    private readonly List<UiaTriggerAssignment> _subscribedAssignments = [];

    private void SubscribeAssignment(UiaTriggerAssignment a)
    {
        if (_subscribedAssignments.Contains(a))
            return;
        a.PropertyChanged += AssignmentsChild_PropertyChanged;
        _subscribedAssignments.Add(a);
    }

    private void UnsubscribeAssignment(UiaTriggerAssignment a)
    {
        if (!_subscribedAssignments.Remove(a))
            return;
        a.PropertyChanged -= AssignmentsChild_PropertyChanged;
    }

    private void UiaTriggerAssignments_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                    foreach (UiaTriggerAssignment a in e.NewItems)
                        SubscribeAssignment(a);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                    foreach (UiaTriggerAssignment a in e.OldItems)
                        UnsubscribeAssignment(a);
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is not null && e.NewItems is not null
                    && !e.OldItems.OfType<UiaTriggerAssignment>().SequenceEqual(
                        e.NewItems.OfType<UiaTriggerAssignment>()))
                {
                    foreach (UiaTriggerAssignment a in e.OldItems)
                        UnsubscribeAssignment(a);
                    foreach (UiaTriggerAssignment a in e.NewItems)
                        SubscribeAssignment(a);
                }
                break;
            case NotifyCollectionChangedAction.Reset:
                foreach (var a in _subscribedAssignments)
                    a.PropertyChanged -= AssignmentsChild_PropertyChanged;
                _subscribedAssignments.Clear();
                break;
        }

        // デバウンス保存の契機。AttachAutoSave から直接購読しない理由は
        // Recorders_CollectionChanged のコメント参照（プロパティごと差し替わりうる）。
        RequestSave();
    }

    private void AssignmentsChild_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        int idx = UiaTriggerAssignments.IndexOf((UiaTriggerAssignment)sender!);
        if (0 <= idx)
            UiaTriggerAssignments[idx] = UiaTriggerAssignments[idx];
    }
    /// <summary>
    /// settings.json を読み直して、このインスタンスの各プロパティへ反映する。
    ///
    /// <para>
    /// 呼び出し元は Settings 画面の「再読み込み」ボタン
    /// （<c>MainPageViewModel.ReloadSettingsCommand</c>）。
    /// <b>録画中・排出中は押せないようにしてある</b> ── 下の <see cref="Recorders"/> の
    /// <c>Clear()</c> が動いているエンジンをその場で破棄し、UI スレッドが排出の完了まで
    /// 固まるため（<c>GstControllerViewModel.IsIdleAll</c>）。
    /// </para>
    /// <para>
    /// <b>全プロパティを手書きでコピーしている</b>ので、プロパティを増やしたときに
    /// 追記を忘れるとその値だけ黙って既定値へ戻る。L3 からは実行できるが、
    /// <b>どのプロパティが写ったかまでは外から見えない</b>ため、
    /// <c>tests/ProcessRecorderApp.Tests/AppSettingsReloadTests.cs</c> が
    /// <b>ソースをテキストとして</b>突き合わせて追記漏れを検出する
    /// （実際に <see cref="ExtensionData"/> のコピー漏れが1件見つかっている）。
    /// </para>
    /// </summary>
    public void Reload()
    {
        // 種（SeedFilePath）は起動時と同じ規則で渡す。**本体が在れば効かない**ので、
        // ここが種を読むのは「手で settings.json を消してから再読み込みした」場合だけ
        // ── 起動と再読み込みで挙動が食い違わないようにするためであって、
        // 通常の再読み込みで同梱物へ倒れるという意味ではない。
        // AppSettingsReloadTests はソース照合なので、この意図はここに書くしかない。
        AppSettings loaded = LoadOrCreate(FilePath, SettingsTypeInfo, () => new(), ReportLoadFailure,
                                          SeedFilePath, ReportSeedUsed);
        IsFirstRun = loaded.IsFirstRun;

        SchemaReference = loaded.SchemaReference;

        WindowWidth = loaded.WindowWidth;
        WindowHeight = loaded.WindowHeight;
        SettingsWidth = loaded.SettingsWidth;
        ConfirmRecorderRemoval = loaded.ConfirmRecorderRemoval;
        IsPropertyPaneCollapsed = loaded.IsPropertyPaneCollapsed;
        FramingGrid = loaded.FramingGrid;
        PaneDisplayMode = loaded.PaneDisplayMode;
        GstDebug = loaded.GstDebug;
        GstDebugDumpDotDir = loaded.GstDebugDumpDotDir;
        DebugLogFile = loaded.DebugLogFile;
        LogScrollbackLines = loaded.LogScrollbackLines;
        PreferredH264Encoder = loaded.PreferredH264Encoder;
        StopFinalizeTimeoutMs = loaded.StopFinalizeTimeoutMs;
        OutputDirectory = loaded.OutputDirectory;
        RecordingRetentionDays = loaded.RecordingRetentionDays;
        RecordingCleanupIntervalHours = loaded.RecordingCleanupIntervalHours;
        // **順序が効く。** RemoteControlEnabled を先に写すと OnRemoteControlEnabledChanged が
        // トークンを生成し、その直後にファイル側の空文字が上書きして消す。
        RemoteControlAccessToken = loaded.RemoteControlAccessToken;
        RemoteControlBindAddress = loaded.RemoteControlBindAddress;
        RemoteControlPort = loaded.RemoteControlPort;
        RemoteControlEnabled = loaded.RemoteControlEnabled;
        UiaTriggersEnabled = loaded.UiaTriggersEnabled;
        // トリガ定義は差し替え運用（要素を in-place 変更しない）なので参照コピーでよい
        UiaTriggers = loaded.UiaTriggers;
        // UiaTriggerList は表示専用の件数。永続値が無いので loaded からは写さず、ここで組み立てる
        UiaTriggerList = FormatTriggerCount(UiaTriggers.Count);
        DataVersion = loaded.DataVersion;
        // loaded 側の OnLoaded() が static ストアへの復元まで済ませている。
        // ここは永続化用の器を揃えるだけ。
        TemplateVariables = new(loaded.TemplateVariables);
        // 現在のスキーマに無い JSON キーの退避場所。ここでコピーしないと、
        // Reload() の後の Save() で「知らないキー」を消してしまう
        // （JsonSettingsBase が保持している意味が失われる）。
        ExtensionData = new(loaded.ExtensionData);

        Recorders.Clear();
        foreach (var r in loaded.Recorders)
            Recorders.Add(r);

        UiaTriggerAssignments.Clear();
        foreach (var a in loaded.UiaTriggerAssignments)
            UiaTriggerAssignments.Add(a);

        // 一時インスタンス（loaded）側の要素購読を外す ── 逆シリアライズ時に
        // OnXChanging が要素へ張った PropertyChanged が残ると、移し替えた要素が
        // 用済みの loaded を生かし続け、要素の変更のたびに loaded 側のハンドラも走る。
        // 空コレクションの代入で OnXChanging が旧要素を全解除する。
        loaded.Recorders = [];
        loaded.UiaTriggerAssignments = [];
    }

    protected override void OnLoaded()
    {
        base.OnLoaded();

        // **ミラーリングより先に行う。** 現時点の v1→v2 は加算的で no-op なので順序は
        // 結果に影響しないが、将来 TemplateVariables 等を変換する移行を書いたとき、
        // 後ろに置いてあると「移行前のデータで static ストアを seed してしまう」。
        // 通るテストからは見えない種類の不具合なので、先に置いておく。
        Migrate();

        // 件数の表示を読み込み結果に揃える。**フックだけに任せてはいけない**
        // ── settings.json に UiaTriggers キーが無いと setter が呼ばれず
        // OnUiaTriggersChanged も走らないので、空欄のまま残る。
        UiaTriggerList = FormatTriggerCount(UiaTriggers.Count);

        // 読み込み値を GStreamer 層の static ミラーへ反映する。
        // （[ObservableProperty] の OnChanged は「変化した場合」しか走らないため、
        //   既定値と同じ値が読み込まれた場合に取りこぼさないよう明示的に代入する）
        GStreamer.EventRecorder.PreferredH264Encoder = PreferredH264Encoder;
        GStreamer.EventRecorder.StopFinalizeTimeoutMs = StopFinalizeTimeoutMs;
        GStreamer.EventRecorder.OutputDirectory = Components.AppDirectories.ResolveOrBase(OutputDirectory);
        GStreamer.EventRecorder.DebugDumpDotDirectory =
            Components.AppDirectories.ResolveOptional(GstDebugDumpDotDir) ?? "";
        Components.LogBuffer.Shared.MaxLines = LogScrollbackLines;

        // テンプレート変数を実行時ストアへ復元する。
        // **SetTemplateVariable は使わない** ── TemplateVariablesChanged が飛んで
        // デバウンス保存が走り、「起動しただけで書き戻す」動きになるため。
        GStreamer.EventRecorder.TemplateVariables.Clear();
        foreach (var pair in TemplateVariables)
            GStreamer.EventRecorder.TemplateVariables[pair.Key] = pair.Value;
#if DEBUG
        if (IsFirstRun)
        {
            Recorders.Add(new()
            {
                Name = "Recorder #1",
                Type = GStreamer.EventRecordingType.D3d12,
                SrcPipeline = "d3d12testsrc is-live=true ! video/x-raw(memory:D3D12Memory), width=1280, height=720, framerate=30/1",
            });
            Recorders.Add(new()
            {
                Name = "Recorder #2",
                Type = GStreamer.EventRecordingType.D3d12,
                SrcPipeline = "d3d12testsrc is-live=true ! video/x-raw(memory:D3D12Memory), width=640, height=480, framerate=30/1",
            });
        }
#endif
    }

    /// <summary>
    /// 読み込んだ設定を現在のスキーマ版（<see cref="CurrentDataVersion"/>）へ揃える。
    ///
    /// <para>
    /// <b>現時点では版を書き戻すだけの no-op。</b> 未リリースで利用者がいないため、
    /// 開発中に増えたプロパティ（<c>PreferredH264Encoder</c> / <c>StopFinalizeTimeoutMs</c> /
    /// <c>TemplateVariables</c>）はすべて版 1 の一部として扱う。いずれも加算的なので、
    /// それらが無い古い開発中のファイルも既定値でそのまま読める。
    /// </para>
    /// <para>
    /// フックを用意しておくのは、リリース後に**非**加算的な変更を入れるときに
    /// 「どこに書くか」で迷わないようにするため。そのときは
    /// <see cref="CurrentDataVersion"/> を 2 にし、ここに <c>1 → 2</c> の変換を書く。
    /// </para>
    /// <para>
    /// 大小比較ではなく不一致で揃えているのは、開発中に版を上げ下げしても
    /// ファイルに古い値が残り続けないようにするため。
    /// </para>
    /// </summary>
    private void Migrate()
    {
        if (DataVersion == CurrentDataVersion)
            return;

        DataVersion = CurrentDataVersion;
    }

    /// <summary>
    /// 設定を保存する。
    ///
    /// <para>
    /// <b>タイマーは畳めるが、既に <c>DispatcherQueue</c> へ積まれた保存は取り消せない。</b>
    /// 「タイマー発火 → TryEnqueue → 終了操作 → <c>Destroying</c> の Save() →
    /// <c>engine.Dispose()</c> → 積まれていた Save() が走る」という順序が起こりうる。
    /// これは無害 ── 参照するのは <c>EventRecorder.TemplateVariables</c>（static で生存）と
    /// <see cref="Recorders"/>（ただの設定オブジェクトで破棄対象ではない）だけなので、
    /// 同じ内容をもう一度書くだけで終わる。
    /// （そもそも終了後はディスパッチャが回らず実行されないことが多い。）
    /// </para>
    /// </summary>
    public void Save()
    {
        // 保留中のデバウンス保存を畳む（この直後に書くので二重に書かない）
        _saveDebounce?.Change(Timeout.Infinite, Timeout.Infinite);

        // 実行時の真実は EventRecorder.TemplateVariables（CLI と Variables 画面が
        // 共有する static ストア）。**保存の瞬間にそこから取り直す** ことで、
        // 変更イベントを取りこぼしていても永続化される値が正しくなる。
        //
        // **取り直すのは既に載っているキーだけ。** 「載っている＝永続化するよう
        // 明示された」であり、静的ストア全体を写すと `--set` しただけの
        // セッション限りの変数まで書き出してしまう（意図に反する）。
        // 逆に「指定が無ければ空を書く」もしてはいけない ── 既に永続化済みの値を消す。
        //
        // **Dictionary を作り直さずその場で書き換える。** [ObservableProperty] ではない
        // ただのプロパティだが、差し替えると Reload() 側の参照前提が崩れるうえ、
        // 将来 [ObservableProperty] にしたときに Save() の中から PropertyChanged が飛んで
        // 冒頭で畳んだデバウンスを張り直すことになる。
        foreach (string key in TemplateVariables.Keys.ToArray())
        {
            if (GStreamer.EventRecorder.TemplateVariables.TryGetValue(key, out var value))
                TemplateVariables[key] = value;
            else
                TemplateVariables.Remove(key);   // 変数そのものが消された
        }

        Save(FilePath, SettingsTypeInfo, ReportSaveFailure);

        // 設定本体を書いた直後にスキーマを揃える（内容が同じなら書き込みは起きない）。
        // **本体より先には書かない** ── 先に書くと、本体の保存に失敗したときに
        // 「新しい形のスキーマ」と「古い内容の本体」が並ぶ。
        SaveSchema(FilePath, SettingsTypeInfo, ReportSaveFailure);
    }

    /// <summary>
    /// 現在の設定を JSON のノードにする（settings.json と同じ表現＝PascalCase）。
    ///
    /// <para>
    /// 保存と同じソース生成の型情報を通すので、リモートが読む形と保存される形が食い違わない。
    /// </para>
    /// </summary>
    internal static System.Text.Json.Nodes.JsonNode? ToJsonNode()
        => JsonSerializer.SerializeToNode(Default, SettingsTypeInfo);

    private System.Threading.Timer? _saveDebounce;
    private Microsoft.UI.Dispatching.DispatcherQueue? _saveDispatcherQueue;

    /// <summary>デバウンス保存の待ち時間(ms)。</summary>
    public const int SaveDebounceMs = 1000;

    /// <summary>
    /// 設定変更を約1秒デバウンスで自動保存するようにする。<b>常駐ワーカーで一度だけ</b>呼ぶ。
    ///
    /// <para>
    /// これが無いと保存契機は <c>AppWindow.Destroying</c> と <c>WindowHiddenToTray</c> だけで、
    /// <b>トレイ常駐中に <c>--set</c> して強制終了すると変更が失われる</b>
    /// （レコーダーの編集も同様）。
    /// </para>
    /// <para>
    /// 実際の <see cref="Save"/> は <paramref name="dispatcherQueue"/> 経由で UI スレッドへ
    /// 戻して行う ── <see cref="Recorders"/> は UI スレッドが変更する
    /// <see cref="ObservableCollection{T}"/> なので、プールスレッドから直列化すると
    /// 変更と衝突しうる。既存の保存契機もすべて UI スレッドであり、そこに揃える。
    /// </para>
    /// </summary>
    public void AttachAutoSave(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        if (_saveDebounce is not null)
            return; // 冪等

        _saveDispatcherQueue = dispatcherQueue;
        _saveDebounce = new System.Threading.Timer(
            _ => _saveDispatcherQueue?.TryEnqueue(Save), null, Timeout.Infinite, Timeout.Infinite);

        PropertyChanged += (_, __) => RequestSave();
        GStreamer.EventRecorder.TemplateVariablesChanged += (_, __) => RequestSave();
        // Recorders.CollectionChanged はここでは購読しない。
        // 既存の Recorders_CollectionChanged（OnRecordersChanging が張り替える）から
        // RequestSave() を呼ぶ ── 理由はそちらのコメント参照。
    }

    /// <summary>保存を予約する（既に予約済みなら先送りする＝デバウンス）。</summary>
    private void RequestSave() => _saveDebounce?.Change(SaveDebounceMs, Timeout.Infinite);

    /// <summary>デバッグ用環境変数を起動時に反映する（既に設定済みの場合は上書きしない）。</summary>
    public void ApplyStartupEnvironmentVariables()
    {
        ApplyIfUnset("GST_DEBUG", GstDebug);
        // GST_DEBUG_DUMP_DOT_DIR は相対パスなら Exe のあるディレクトリ基準の絶対パスに解決する
        // （OutputDirectory と同じ規約。実装は Components.AppDirectories に集約してある）
        ApplyIfUnset("GST_DEBUG_DUMP_DOT_DIR", Components.AppDirectories.ResolveOptional(GstDebugDumpDotDir));

        static void ApplyIfUnset(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value) &&
                Environment.GetEnvironmentVariable(name) is null)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

[JsonSerializable(typeof(AppSettings))]
// レコーダー設定を単体で直列化できるようにする（<see cref="AppSettings.CloneRecorder"/>）。
// **スキーマには影響しない** ── BuildSchema / Save / LoadOrCreate はいずれも
// `.AppSettings` の JsonTypeInfo だけを使う。AppSettings 経由で到達可能な型なので
// 生成物も実質増えないが、生成器の実装詳細に依存しないよう入口を明示的に開けておく。
[JsonSerializable(typeof(GStreamer.EventRecorderSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}
