using CommunityToolkit.Mvvm.ComponentModel;
using Gst;
using GstApp = Gst.App;
using GstBase = Gst.Base;
using GObject = Gst.GObject;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 録画の方式。
///
/// <para>
/// <b>settings.json には名前で書く</b>（<c>"System"</c> / <c>"D3d12"</c>）── 手で開いて直す
/// ファイルなので、数値だと意味が読めないうえ、並びを変えた瞬間に既存ファイルの意味が
/// 黙って変わる。<c>JsonStringEnumConverter&lt;T&gt;</c> の総称版を使うのは Native AOT のため
/// （非総称版は実行時リフレクションを要求する）。読み取りは数値も受けるので、
/// 数値で書かれた古いファイルもそのまま読める。
/// </para>
/// </summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<EventRecordingType>))]
public enum EventRecordingType
{
    System,
    D3d12,
}

public partial class EventRecorderSettings : ObservableObject, Components.IPropertyAccess
{
    /// <summary>事前バッファ長(ms)の下限。</summary>
    public const int MinBufferDuration = 0;

    /// <summary>
    /// 事前バッファ長(ms)の上限（10分）。
    /// リングバッファはメモリ上に保持されるため、上限がないと設定値ひとつでメモリを枯渇させられる。
    /// </summary>
    public const int MaxBufferDuration = 600_000;

    /// <summary>事前バッファ長(ms)を有効範囲へ丸める。</summary>
    public static int ClampBufferDuration(int value) => Math.Clamp(value, MinBufferDuration, MaxBufferDuration);

    /// <summary>PropertyGridView 向けのプロパティ列挙(自型の public インスタンスプロパティを返す)。</summary>
    public IEnumerable<PropertyInfo> GetProperties()
        => typeof(EventRecorderSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public);

    /// <summary>
    /// 変更しても<b><see cref="EventRecorder.Initialize"/> をやり直すまで効かない</b>プロパティ。
    ///
    /// <para>
    /// <b>これらはパイプラインの組み立ての入力である。</b> sink / src のパイプラインは
    /// <c>parse_launch</c> へ渡す<b>文字列</b>として一度きり組み立てられ、
    /// 常時録画の枝（<see cref="ContinuousRecording"/> 一式）も同じ文字列の一部なので、
    /// 動いているパイプラインへ後から効かせる道が無い。
    /// <see cref="ContinuousFilenameTemplate"/> と <see cref="ContinuousSegmentSeconds"/> は
    /// 文字列ではないが、常時録画エンジンの構築時に<b>値として写し取られる</b>
    /// （<c>ContinuousRecorder</c> の ctor）ので同じ扱いになる。
    /// <see cref="CameraControls"/> は初期化の成功後に 1 回だけ適用される。
    /// </para>
    /// <para>
    /// <b>ここに<u>無い</u>のは <see cref="Name"/> / <see cref="BufferDuration"/> /
    /// <see cref="FilenameTemplate"/> と、プレビュー配信の 4 つ
    /// （<see cref="PreviewWidth"/> / <see cref="PreviewHeight"/> / <see cref="PreviewFps"/> /
    /// <see cref="PreviewBitrateKbps"/>）である。</b> 前の 3 つは動作中に読み直され
    /// （バッファ長はサンプルごと、ファイル名は録画開始ごと）、
    /// 後の 4 つは録画パイプラインの組み立てに一切関わらない
    /// （配信側が畳んで組み直す）。
    /// </para>
    /// <para>
    /// 用途は<b>助言</b>である ── 変更そのものは常に受け付ける。
    /// リモート操作の PATCH がこの一覧との交わりを応答へ載せ、
    /// 「今の録画には効かない」ことを呼び出し側へ伝える。
    /// </para>
    /// </summary>
    public static readonly string[] PropertiesRequiringReinitialize =
    [
        nameof(Type),
        nameof(SrcPipeline),
        nameof(EncodingProperties),
        nameof(CameraControls),
        nameof(ContinuousRecording),
        nameof(ContinuousFramerate),
        nameof(ContinuousResolution),
        nameof(ContinuousEncodingProperties),
        nameof(ContinuousFilenameTemplate),
        nameof(ContinuousSegmentSeconds),
    ];

    [Description("PropDesc_Rec_Name")]
    [ObservableProperty]
    public partial string Name { get; set; } = "Recorder";

    /// <summary>
    /// 事前バッファ長(ms)。設定値は常に <see cref="ClampBufferDuration"/> で丸める。
    /// （[ObservableProperty] の OnXChanged 内で再入補正すると、丸め後の値がモデル側の
    ///   現在値と一致した場合に PropertyChanged が飛ばず、UI が範囲外の値を表示したまま残る）
    /// </summary>
    [Description("PropDesc_Rec_BufferDuration")]
    public int BufferDuration
    {
        get => _bufferDuration;
        set => SetProperty(ref _bufferDuration, ClampBufferDuration(value));
    }
    private int _bufferDuration = 10_000;

    /// <summary>
    /// 録画ファイル名のテンプレート。「録画ファイルをどう書くか」なので
    /// <c>PropCat_Output</c> の組に置く。
    /// </summary>
    [Category("PropCat_Output")]
    [Description("PropDesc_Rec_FilenameTemplate")]
    [ObservableProperty]
    public partial string FilenameTemplate { get; set; } = "{Now:yyyyMMdd_HHmmss}_{Name}.mp4";

    [Description("PropDesc_Rec_Type")]
    [ObservableProperty]
    public partial EventRecordingType Type { get; set; }
#if true
        = EventRecordingType.D3d12;
#else
        = EventRecordingType.System;
#endif
    [Description("PropDesc_Rec_SrcPipeline")]
    [ObservableProperty]
    public partial string? SrcPipeline { get; set; } =
#if true
        "d3d12testsrc is-live=true do-timestamp=true ! " +
        "video/x-raw(memory:D3D12Memory), format=NV12, width=1280, height=720, framerate=30/1";
#else
        //"videotestsrc is-live=true do-timestamp=true ! videoconvert ! " +
        //"video/x-raw,format=I420,width=1280, height=720,framerate=15/1";
#endif
    [Description("PropDesc_Rec_EncodingProperties")]
    [ObservableProperty]
    public partial string? EncodingProperties { get; set; }

    /// <summary>
    /// カメラ（<c>mfvideosrc</c>）のフォーカス・明るさ等。
    /// 形は <c>brightness=128;focus=30;focus-auto=false</c>（<see cref="CameraControlSettings"/>）。
    ///
    /// <para>
    /// <b>17 項目を個別のプロパティにしない。</b> レコーダー設定を 1 つ増やすたびに
    /// 手書きのミラーが 4 箇所（<c>RecorderSettingsMirrorTests</c> が縛る）＋ E2E の
    /// <c>RecorderSpec</c> に要るので、17 個なら 85 箇所になる。
    /// <see cref="SrcPipeline"/> と同じ「そのまま持つ文字列」にして、編集はダイアログに任せる。
    /// </para>
    /// <para>
    /// <b>反映は <c>Initialize</c> の成功後に 1 回。</b> ソースが <c>mfvideosrc</c> でなければ
    /// 何もしない。適用に失敗しても初期化は落とさない。
    /// </para>
    /// </summary>
    [Description("PropDesc_Rec_CameraControls")]
    [ObservableProperty]
    public partial string? CameraControls { get; set; }

    /// <summary>プレビュー配信の幅(px)の下限。</summary>
    public const int MinPreviewWidth = 160;

    /// <summary>プレビュー配信の幅(px)の上限。</summary>
    public const int MaxPreviewWidth = 3840;

    /// <summary>プレビュー配信の幅(px)を有効範囲へ丸める。</summary>
    public static int ClampPreviewWidth(int value) => Math.Clamp(value, MinPreviewWidth, MaxPreviewWidth);

    /// <summary>プレビュー配信の高さ(px)の下限。</summary>
    public const int MinPreviewHeight = 120;

    /// <summary>プレビュー配信の高さ(px)の上限。</summary>
    public const int MaxPreviewHeight = 2160;

    /// <summary>プレビュー配信の高さ(px)を有効範囲へ丸める。</summary>
    public static int ClampPreviewHeight(int value) => Math.Clamp(value, MinPreviewHeight, MaxPreviewHeight);

    /// <summary>プレビュー配信のフレームレート(fps)の下限。</summary>
    public const int MinPreviewFps = 1;

    /// <summary>プレビュー配信のフレームレート(fps)の上限。</summary>
    public const int MaxPreviewFps = 60;

    /// <summary>プレビュー配信のフレームレート(fps)を有効範囲へ丸める。</summary>
    public static int ClampPreviewFps(int value) => Math.Clamp(value, MinPreviewFps, MaxPreviewFps);

    /// <summary>プレビュー配信のビットレート(kbit/s)の下限。</summary>
    public const int MinPreviewBitrateKbps = 100;

    /// <summary>プレビュー配信のビットレート(kbit/s)の上限。</summary>
    public const int MaxPreviewBitrateKbps = 20_000;

    /// <summary>プレビュー配信のビットレート(kbit/s)を有効範囲へ丸める。</summary>
    public static int ClampPreviewBitrateKbps(int value)
        => Math.Clamp(value, MinPreviewBitrateKbps, MaxPreviewBitrateKbps);

    /// <summary>
    /// プレビュー配信の幅(px)。設定値は常に <see cref="ClampPreviewWidth"/> で丸める
    /// （<see cref="BufferDuration"/> と同じ理由で <c>[ObservableProperty]</c> にしない
    ///   ── 丸め後の値が現在値と一致すると PropertyChanged が飛ばず、
    ///   UI が範囲外の値を表示したまま残る）。
    /// </summary>
    [Category("PropCat_Preview")]
    [Description("PropDesc_Rec_PreviewWidth")]
    public int PreviewWidth
    {
        get => _previewWidth;
        set => SetProperty(ref _previewWidth, ClampPreviewWidth(value));
    }
    private int _previewWidth = 1280;

    /// <summary>
    /// プレビュー配信の高さ(px)。設定値は常に <see cref="ClampPreviewHeight"/> で丸める
    /// （<see cref="PreviewWidth"/> と同じ理由で <c>[ObservableProperty]</c> にしない）。
    /// </summary>
    [Category("PropCat_Preview")]
    [Description("PropDesc_Rec_PreviewHeight")]
    public int PreviewHeight
    {
        get => _previewHeight;
        set => SetProperty(ref _previewHeight, ClampPreviewHeight(value));
    }
    private int _previewHeight = 720;

    /// <summary>
    /// プレビュー配信のフレームレート(fps)。設定値は常に <see cref="ClampPreviewFps"/> で丸める
    /// （<see cref="PreviewWidth"/> と同じ理由で <c>[ObservableProperty]</c> にしない）。
    /// </summary>
    [Category("PropCat_Preview")]
    [Description("PropDesc_Rec_PreviewFps")]
    public int PreviewFps
    {
        get => _previewFps;
        set => SetProperty(ref _previewFps, ClampPreviewFps(value));
    }
    private int _previewFps = 15;

    /// <summary>
    /// プレビュー配信のビットレート(kbit/s)。設定値は常に
    /// <see cref="ClampPreviewBitrateKbps"/> で丸める
    /// （<see cref="PreviewWidth"/> と同じ理由で <c>[ObservableProperty]</c> にしない）。
    /// </summary>
    [Category("PropCat_Preview")]
    [Description("PropDesc_Rec_PreviewBitrateKbps")]
    public int PreviewBitrateKbps
    {
        get => _previewBitrateKbps;
        set => SetProperty(ref _previewBitrateKbps, ClampPreviewBitrateKbps(value));
    }
    private int _previewBitrateKbps = 2000;

    /// <summary>常時録画のセグメント長(秒)の下限。</summary>
    public const int MinContinuousSegmentSeconds = 5;

    /// <summary>
    /// 常時録画のセグメント長(秒)の上限（24 時間 ＝ 86400 秒）。
    /// セグメントが長いほど、異常終了で <c>moov</c> ごと失う範囲が広がる
    /// （書き込み中のファイルは常に未確定で、確定するのは切り替えか終了のときだけ）。
    /// </summary>
    public const int MaxContinuousSegmentSeconds = 86_400;

    /// <summary>常時録画のセグメント長(秒)を有効範囲へ丸める。</summary>
    public static int ClampContinuousSegmentSeconds(int value)
        => Math.Clamp(value, MinContinuousSegmentSeconds, MaxContinuousSegmentSeconds);

    /// <summary>
    /// 常時録画（イベント録画とは別の枝を常に回し、一定時間ごとにファイルを切り替える）を行うか。
    /// <b>反映は <c>Initialize</c> で効く</b> ── 枝は sink パイプラインの文字列そのものなので、
    /// 組み立て直さないと増減できない（<see cref="SrcPipeline"/> / <see cref="Type"/> と同じ）。
    /// </summary>
    [Category("PropCat_Continuous")]
    [Description("PropDesc_Rec_ContinuousRecording")]
    [ObservableProperty]
    public partial bool ContinuousRecording { get; set; }

    /// <summary>
    /// 常時録画のフレームレート（<c>5/1</c> のような分数）。空ならイベント録画と同じ。
    /// <b>空でないときだけ <c>videorate</c> を挿入する</b> ── <c>videorate</c> は同梱ランタイムに
    /// 入れてあるが、利用者が別途入れた GStreamer には無いことがある。無条件に書くと、
    /// フレームレートを変えていない構成まで巻き添えで初期化に失敗する。
    /// </summary>
    [Category("PropCat_Continuous")]
    [Description("PropDesc_Rec_ContinuousFramerate")]
    [ObservableProperty]
    public partial string ContinuousFramerate { get; set; } = "";

    /// <summary>
    /// 常時録画の解像度（<c>1280x720</c>）。空ならイベント録画と同じ。
    /// 効かせるのはソースの caps ではなく変換段（<c>d3d12convert</c> / <c>videoscale</c>）
    /// ── 画面キャプチャの src caps はモニター解像度に固定されており、
    /// ソース側 capsfilter では交渉に失敗する。
    /// </summary>
    [Category("PropCat_Continuous")]
    [Description("PropDesc_Rec_ContinuousResolution")]
    [ObservableProperty]
    public partial string ContinuousResolution { get; set; } = "";

    /// <summary>
    /// 常時録画のエンコーダー起動文字列。空なら自動選択（イベント側と同じ規則）。
    /// <b>手書きするなら GOP を必ず固定すること</b> ── セグメントの切り替えはキーフレームでしか
    /// 行わないので、GOP が長いとその分だけ分割が遅れる。
    /// </summary>
    [Category("PropCat_Continuous")]
    [Description("PropDesc_Rec_ContinuousEncodingProperties")]
    [ObservableProperty]
    public partial string? ContinuousEncodingProperties { get; set; }

    /// <summary>
    /// 常時録画のファイル名テンプレート。<b>セグメントごとに展開し直す</b>ので
    /// <c>{Now}</c> は毎回変わる。<c>{Segment}</c> は 5 桁 0 詰めの連番。
    /// </summary>
    [Category("PropCat_Continuous")]
    [Description("PropDesc_Rec_ContinuousFilenameTemplate")]
    [ObservableProperty]
    public partial string ContinuousFilenameTemplate { get; set; } = "{Now:yyyyMMdd_HHmmss}_{Name}_c{Segment}.mp4";

    /// <summary>
    /// 常時録画のセグメント長(秒)。設定値は常に <see cref="ClampContinuousSegmentSeconds"/> で丸める
    /// （<see cref="BufferDuration"/> と同じ理由で <c>[ObservableProperty]</c> にしない）。
    /// </summary>
    [Category("PropCat_Continuous")]
    [Description("PropDesc_Rec_ContinuousSegmentSeconds")]
    public int ContinuousSegmentSeconds
    {
        get => _continuousSegmentSeconds;
        set => SetProperty(ref _continuousSegmentSeconds, ClampContinuousSegmentSeconds(value));
    }
    private int _continuousSegmentSeconds = 600;
}

public partial class EventRecorder : ObservableObject, IDisposable
{
    private Pipeline? _sinkPipeline;
    private Bus? _sinkBus;
    private GstApp.AppSink? _previewSink;
    private GstApp.AppSink? _appSink;
    private Pipeline? _srcPipeline;
    private GstBase.BaseSrc? _errorSinkSrc;

    /// <summary>
    /// sink パイプラインが EOS を受けたか（<c>Initialize</c> のたびに畳む）。
    ///
    /// <para>
    /// <b>EOS のあとは要素単位の復帰では戻らない。</b> EOS は下流へ流れて残るので、
    /// ソース要素を <c>Ready</c>→<c>Playing</c> しても <c>tee</c> から先
    /// （プレビュー枝・エンコーダー枝）は元に戻らない。
    /// 実機ではカメラの抜き差しで<b>「復帰は result=ok なのにプレビューがカタつき、
    /// Initialize を押すと直る」</b>という形で現れた。
    /// </para>
    /// </summary>
    private volatile bool _sinkSawEos;

    /// <summary>
    /// sink の <c>appsink</c> から取り出せたサンプルの通算数（<b>録画中かどうかに依らない</b>）。
    ///
    /// <para>
    /// 用途は「要素単位の再開のあと実が流れているか」の 1 点だけで、値そのものに
    /// 意味は無い（<see cref="WaitForSinkSamplesAsync"/> が差分だけを見る）。
    /// 巻き戻さないので、<c>Initialize</c> を跨いでも単調に増える。
    /// </para>
    /// </summary>
    private long _sinkSamplesSeen;

    /// <summary>
    /// 最後に sink の <c>appsink</c> からサンプルを取り出した時刻
    /// （<see cref="Environment.TickCount64"/>）。
    ///
    /// <para>
    /// <b>「いま実が流れているか」を待たずに判断するための値。</b>
    /// <see cref="StartCore"/> は要素単位の再開を掛けてよいかをここで決める ──
    /// <see cref="RestartSinkSrc"/> は <c>Ready</c> を経由する破壊的な操作なので、
    /// 自力で戻ったソースに掛けるとデバイスを閉じて開き直すことになる。
    /// </para>
    /// </summary>
    private long _lastSinkSampleAt;
    private Bus? _srcBus;
    private GstApp.AppSrc? _appSrc;
    private Element? _mux;
    private Element? _file;

    /// <summary>常時録画の枝の終端 appsink（sink パイプラインの一部）。</summary>
    private GstApp.AppSink? _continuousSink;

    /// <summary>常時録画エンジン。常時録画が無効か、枝を組めなかった場合は null。</summary>
    private ContinuousRecorder? _continuous;

    /// <summary>
    /// ライブプレビューの配信エンジン。初期化に成功している間だけ非 null で、
    /// <b>購読者が 0 人のうちは何も作らない</b>（mux は最初の購読で起きる）。
    /// </summary>
    private LivePreviewStream? _live;

    /// <summary>
    /// ライブプレビューの購読口。<b>読むのは配信側だけ</b>で、初期化前・破棄後は null。
    /// </summary>
    internal LivePreviewStream? LivePreview => Volatile.Read(ref _live);

    /// <summary>
    /// DASH プレビューの配信エンジン。初期化に成功している間だけ非 null で、
    /// <b>読み手が 0 人のあいだは何も作らない</b>（第 2 パイプラインは最初の
    /// <c>TryGetSnapshot</c> で起きる）。
    /// </summary>
    private DashPreviewStream? _dash;

    /// <summary>
    /// DASH プレビューの読み口。<b>読むのは配信側だけ</b>で、初期化前・破棄後は null。
    /// </summary>
    internal DashPreviewStream? DashPreview => Volatile.Read(ref _dash);

    static int _instanceCount = 0;
    [ObservableProperty]
    public partial string Name { get; set; } = $"Recorder #{Interlocked.Increment(ref _instanceCount)}";
    partial void OnNameChanged(string value)
    {
        if (_currentSettings?.Name != value)
            _currentSettings?.Name = value;
    }

    /// <summary>事前バッファ長(ms)。<see cref="EventRecorderSettings.ClampBufferDuration"/> で丸める。</summary>
    public int BufferDuration
    {
        get => _bufferDuration;
        set
        {
            if (SetProperty(ref _bufferDuration, EventRecorderSettings.ClampBufferDuration(value))
                && _currentSettings is { } settings
                && settings.BufferDuration != _bufferDuration)
            {
                settings.BufferDuration = _bufferDuration;
            }
        }
    }
    private int _bufferDuration;

    [ObservableProperty]
    public partial string FilenameTemplate { get; set; }
    partial void OnFilenameTemplateChanged(string value)
    {
        if (_currentSettings?.FilenameTemplate != value)
            _currentSettings?.FilenameTemplate = value;
    }
    [ObservableProperty]
    public partial string? LastFilename { get; private set; }

    /// <summary>
    /// 録画中フラグ（sink の <c>appsink</c> コールバックがサンプルごとに読む）。
    /// <see cref="IsRecording"/> は UI 通知用のミラーで、こちらが実体。
    /// <b>volatile は必須</b> ── 書くのは UI スレッド（<see cref="Start"/> /
    /// <see cref="Stop"/>）、読むのはストリーミングスレッドで、ロックを介さない。
    /// </summary>
    volatile bool _IsRecording;
    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    /// <summary>
    /// 停止を受け付けてから排出（EOS → バス待ち → <c>SetState(Null)</c>）が
    /// 終わるまで true。<see cref="IsRecording"/> は停止を受け付けた時点で即座に
    /// false になるため、この2つは同時に false の期間（＝排出中）を持つ。
    ///
    /// <para>
    /// この間に <see cref="Start"/> を受けると排出中の <c>_srcPipeline</c> と競合するので、
    /// UI/CLI の「開始できるか」はこのフラグも見る必要がある
    /// （<c>GstEventRecorderViewModel.CanStartRecording</c>）。
    /// </para>
    /// <para>
    /// <b>プールスレッドから false に戻される</b>ため、VM 側は
    /// <c>PropertyChanged</c> を UI スレッドへマーシャリングすること。
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial bool IsStopping { get; private set; }

    [ObservableProperty]
    public partial bool IsInitialized { get; private set; }

    /// <summary>
    /// 自動復帰の作り直しで畳んだ録画を、復帰したら録り直す予定があるか（実体）。
    /// <see cref="IsAwaitingRecoveryResume"/> は UI 通知用のミラー。
    ///
    /// <para>
    /// <b>volatile は必須</b> ── 立てるのは復帰の連鎖（プールスレッド）、
    /// 降ろすのは <see cref="Stop"/>（UI/CLI スレッド）で、ロックを介さない。
    /// </para>
    /// <para>
    /// <b>作り直しを跨いで生き残る。</b> デバイスが 2 分抜けていれば
    /// <c>rebuildOnly</c> の連鎖が何周も回るので、周回ごとに立て直すのではなく
    /// レコーダーが持つ。消えるのは「止められたとき」「録り直したとき」
    /// 「破棄されたとき」の 3 つだけである。
    /// </para>
    /// </summary>
    volatile bool _resumeAfterRecovery;

    /// <summary>
    /// 録り直す本の開始理由（sidecar の <c>trigger</c>）。畳む録画の <see cref="_startTrigger"/> を
    /// <c>_resumeAfterRecovery</c> を立てるのと同じ <c>_stateLock</c> の下で控える。
    ///
    /// <para>
    /// <b>復帰は利用者の操作ではない。</b> 引き継がずに <c>manual</c> で置くと、
    /// <c>uia:&lt;id&gt;</c> 起点の録画（停止まで <c>UiaTriggerService</c> が所有し続ける）が
    /// 録り直した本の sidecar でだけ理由を偽る。
    /// </para>
    /// <para>
    /// 読み書きは <c>_resumeAfterRecovery</c> と同じ 3 か所で降ろす
    /// （止められたとき・録り直したとき・破棄されたとき）。
    /// </para>
    /// </summary>
    private string? _resumeTrigger;

    [ObservableProperty]
    public partial bool IsAwaitingRecoveryResume { get; private set; }

    /// <summary>
    /// 直近に検出した障害（バスの Error / Warning、停止のタイムアウト等）。正常時は <see langword="null"/>。
    ///
    /// <para>
    /// バスの Error を**パースもログもせず捨てる**と、障害はどこにも現れない。
    /// ユーザーから見える唯一の症状が「録画ファイルが壊れている」になり、
    /// 原因の切り分けができなくなる。
    /// </para>
    /// <para>
    /// <b>このプロパティは GStreamer のストリーミングスレッド</b>（バスの同期ハンドラ・
    /// <c>appsink</c> のコールバック）<b>やプールスレッド</b>（停止の排出）
    /// <b>から変更される。</b> 購読側（VM）は
    /// UI スレッドへマーシャリングすること ── <c>GstEventRecorderViewModel.Model_PropertyChanged</c>
    /// がそれを行っている。
    /// </para>
    /// <para>
    /// 書き込みは<b>参照の代入だけ</b>で、前の値を壊さない ── 複数のスレッドが同時に書いた
    /// ときは「最後に書いた者が勝つ」。UI が出すのは最新の 1 件でよいので、これで足りる。
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string? LastError { get; private set; }

    /// <summary>
    /// 障害を検出したときに発火する（UI スレッドとは限らないスレッドから）。
    /// </summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// <see cref="Initialize"/> / <see cref="Start"/> / <see cref="Stop"/> / <see cref="Close"/> の
    /// 相互排他。これらは UI スレッド・プールスレッド・CLI 経路から呼ばれるのに、
    /// これまで同期が一切無かった。
    ///
    /// <para>
    /// <b>sink・preview・常時録画の <c>appsink</c> コールバックはこのロックを取らない。</b>
    /// <see cref="Close"/> はロックを保持したまま sink パイプラインを <c>Null</c> へ落として
    /// <b>実行中のコールバックの復帰を待ち</b>（<see cref="CloseCore"/> の quiesce）、
    /// そのあと常時録画の停止を待つ
    /// ── どちらも同じロックを待つ側に回った瞬間にデッドロックする。
    /// コールバックは volatile フィールド（<see cref="_isAlive"/> /
    /// <see cref="_IsRecording"/>）の読みだけで回すこと。
    /// </para>
    /// <para>
    /// <b>ロック順序は <c>_stateLock</c> → <see cref="_busLock"/> → <c>_restartLock</c> の
    /// 一方向のみ</b>（逆向きの辺は無い ── <see cref="_busLock"/> の doc を参照）。
    /// <c>DeviceArrivalWatcher</c> のロックは<b>葉</b>で、この 3 本のどれを保持したままでも
    /// 取らない（取得は復帰連鎖の先頭＝プールスレッドに限る）。
    /// </para>
    /// <para>
    /// <b>バスの同期ハンドラもこのロックを取らない。</b>
    /// ハンドラはメッセージを post した要素自身のストリーミングスレッドで走るので、
    /// ここで <c>_stateLock</c> を待つと、そのロックを保持している <see cref="Close"/> の
    /// <c>SetState(Null)</c>（当の要素の停止を待つ）と組んでデッドロックする。
    /// ハンドラが取ってよいのは <see cref="_busLock"/> だけ。
    /// </para>
    /// <para>
    /// <b><see cref="StopAsync"/> がプールへ投げる排出タスクもこのロックを取らない。</b>
    /// <see cref="Close"/> はロックを保持したままその完了を待つ（<see cref="WaitForPendingStop"/>）
    /// ため、排出側がロックを取るとデッドロックする。排出が触るのはネイティブの
    /// パイプライン・バスと <c>ActivityLog</c> だけに留めること。
    /// </para>
    /// </summary>
    private readonly object _stateLock = new();

    /// <summary>
    /// バスの同期ハンドラの直列化と、購読の取り付けゲートを兼ねるロック。
    ///
    /// <para>
    /// <b>ロック順序は <c>_stateLock</c> → <see cref="_busLock"/> → <c>_restartLock</c> の
    /// 一方向のみ。</b> 逆向きの辺は存在しない ── <see cref="CancelPendingRestart"/> は
    /// <c>_stateLock</c> → <c>_restartLock</c> で <see cref="_busLock"/> を挟まず、
    /// <see cref="RestartLoopAsync"/> は <c>_restartLock</c> を離してから
    /// <see cref="Initialize"/>（<c>_stateLock</c> → <see cref="_busLock"/>）へ進む。
    /// </para>
    /// <para>
    /// <b>このロックを保持したまま <c>SetState</c> を呼んではいけない。</b>
    /// ハンドラは post 元＝当の要素のストリーミングスレッドで走るため、
    /// そこから状態遷移を掛けると自スレッドの復帰を待って固まる。
    /// 状態遷移が要るときはプールスレッドへ逃がすこと。
    /// </para>
    /// <para>
    /// <b>取り付けのゲートでもある。</b> 購読より後に post されたメッセージはキューに入らない
    /// （配送も破棄もバインディングが行う）ので、このロックの下で「購読 → キューの汲み切り」を
    /// 行えば、汲み切る対象は購読前のバックログだけになる。
    /// </para>
    /// </summary>
    private readonly object _busLock = new();

    /// <summary>
    /// sink バスの購読（<c>Bus.SubscribeSyncDrop</c> の戻り値。<c>Dispose</c> がバスの
    /// 同期ハンドラを外す）。未購読なら <see langword="null"/>。
    /// </summary>
    private IDisposable? _sinkBusSubscription;

    /// <summary>src バスの購読（<see cref="_sinkBusSubscription"/> と同じ）。</summary>
    private IDisposable? _srcBusSubscription;

    /// <summary>
    /// 停止の排出待ちが受け取る結果。<b>ラッパーではなく写した値を運ぶ</b> ──
    /// <c>Message</c> のラッパーはハンドラの実行中しか有効でないので、
    /// <c>ParseError</c> のマネージド値（Dispose 不要）だけを取り出して渡す。
    /// </summary>
    private readonly record struct StopDrainResult(StopDrainSignal Signal, string? ErrorMessage, string? Debug);

    /// <summary>
    /// 停止の排出待ち（<see cref="StopDrainAndFinalize"/> が武装し、src バスの
    /// 同期ハンドラが完了させる）。武装していなければ <see langword="null"/>。
    ///
    /// <para>
    /// <b>読み書きは必ず <see cref="_busLock"/> の下で行う。</b> 武装は
    /// <c>EndOfStream()</c> より前に済ませること ── 後にすると、EOS より先に届く
    /// Error を取りこぼす。
    /// </para>
    /// <para>
    /// <b>生成は必ず <c>RunContinuationsAsynchronously</c> つきで、待ちは同期
    /// <c>Wait(timeout)</c>。</b> どちらかを外すと継続が post 元のストリーミングスレッドで
    /// インライン実行され、<c>finally</c> の <c>SetState(Null)</c> が自スレッドで走って固まる。
    /// </para>
    /// </summary>
    private System.Threading.Tasks.TaskCompletionSource<StopDrainResult>? _stopDrain;

    [ObservableProperty]
    public partial EventRecordingType Type { get; set; }
    partial void OnTypeChanged(EventRecordingType value)
    {
        if (_currentSettings?.Type != value)
            _currentSettings?.Type = value;
    }
    [ObservableProperty]
    public partial EventRecordingType ActualType { get; private set; }

    [ObservableProperty]
    public partial string? SrcPipeline { get; set; }
    partial void OnSrcPipelineChanged(string? value)
    {
        if (_currentSettings?.SrcPipeline != value)
            _currentSettings?.SrcPipeline = value;
    }
    [ObservableProperty]
    public partial string? ActualSrcPipeline { get; private set; }

    [ObservableProperty]
    public partial string? EncodingProperties { get; set; }
    partial void OnEncodingPropertiesChanged(string? value)
    {
        if (_currentSettings?.EncodingProperties != value)
            _currentSettings?.EncodingProperties = value;
    }


    /// <summary>
    /// カメラ制御の設定（設定側 <c>EventRecorderSettings.CameraControls</c> のミラー）。
    /// 反映は <c>Initialize</c> の成功後に 1 回。
    /// </summary>
    [Description("PropDesc_Rec_CameraControls")]
    [ObservableProperty]
    public partial string? CameraControls { get; set; }
    partial void OnCameraControlsChanged(string? value)
    {
        if (_currentSettings?.CameraControls != value)
            _currentSettings?.CameraControls = value;
    }

    /// <summary>カメラ制御の適用に失敗した理由（成功なら空）。読み取り専用。</summary>
    [ReadOnly(true)]
    [Description("PropDesc_Rec_CameraControlsLastError")]
    [ObservableProperty]
    public partial string? CameraControlsLastError { get; private set; }

    [ObservableProperty]
    public partial string? ActualEncodingProperties { get; private set; }

    [ObservableProperty]
    public partial bool ContinuousRecording { get; set; }
    partial void OnContinuousRecordingChanged(bool value)
    {
        if (_currentSettings?.ContinuousRecording != value)
            _currentSettings?.ContinuousRecording = value;
    }

    [ObservableProperty]
    public partial string ContinuousFramerate { get; set; } = "";
    partial void OnContinuousFramerateChanged(string value)
    {
        if (_currentSettings?.ContinuousFramerate != value)
            _currentSettings?.ContinuousFramerate = value;
    }

    [ObservableProperty]
    public partial string ContinuousResolution { get; set; } = "";
    partial void OnContinuousResolutionChanged(string value)
    {
        if (_currentSettings?.ContinuousResolution != value)
            _currentSettings?.ContinuousResolution = value;
    }

    [ObservableProperty]
    public partial string? ContinuousEncodingProperties { get; set; }
    partial void OnContinuousEncodingPropertiesChanged(string? value)
    {
        if (_currentSettings?.ContinuousEncodingProperties != value)
            _currentSettings?.ContinuousEncodingProperties = value;
    }

    [ObservableProperty]
    public partial string ContinuousFilenameTemplate { get; set; } = "";
    partial void OnContinuousFilenameTemplateChanged(string value)
    {
        if (_currentSettings?.ContinuousFilenameTemplate != value)
            _currentSettings?.ContinuousFilenameTemplate = value;
    }

    /// <summary>プレビュー配信の幅(px)。設定・VM と同じく 3 箇所すべてで丸める。</summary>
    public int PreviewWidth
    {
        get => _previewWidth;
        set
        {
            if (SetProperty(ref _previewWidth, EventRecorderSettings.ClampPreviewWidth(value))
                && _currentSettings is { } settings
                && settings.PreviewWidth != _previewWidth)
            {
                settings.PreviewWidth = _previewWidth;
            }
        }
    }
    private int _previewWidth;

    /// <summary>プレビュー配信の高さ(px)。設定・VM と同じく 3 箇所すべてで丸める。</summary>
    public int PreviewHeight
    {
        get => _previewHeight;
        set
        {
            if (SetProperty(ref _previewHeight, EventRecorderSettings.ClampPreviewHeight(value))
                && _currentSettings is { } settings
                && settings.PreviewHeight != _previewHeight)
            {
                settings.PreviewHeight = _previewHeight;
            }
        }
    }
    private int _previewHeight;

    /// <summary>プレビュー配信のフレームレート(fps)。設定・VM と同じく 3 箇所すべてで丸める。</summary>
    public int PreviewFps
    {
        get => _previewFps;
        set
        {
            if (SetProperty(ref _previewFps, EventRecorderSettings.ClampPreviewFps(value))
                && _currentSettings is { } settings
                && settings.PreviewFps != _previewFps)
            {
                settings.PreviewFps = _previewFps;
            }
        }
    }
    private int _previewFps;

    /// <summary>プレビュー配信のビットレート(kbit/s)。設定・VM と同じく 3 箇所すべてで丸める。</summary>
    public int PreviewBitrateKbps
    {
        get => _previewBitrateKbps;
        set
        {
            if (SetProperty(ref _previewBitrateKbps, EventRecorderSettings.ClampPreviewBitrateKbps(value))
                && _currentSettings is { } settings
                && settings.PreviewBitrateKbps != _previewBitrateKbps)
            {
                settings.PreviewBitrateKbps = _previewBitrateKbps;
            }
        }
    }
    private int _previewBitrateKbps;

    /// <summary>常時録画のセグメント長(秒)。設定・VM と同じく 3 箇所すべてで丸める。</summary>
    public int ContinuousSegmentSeconds
    {
        get => _continuousSegmentSeconds;
        set
        {
            if (SetProperty(ref _continuousSegmentSeconds,
                    EventRecorderSettings.ClampContinuousSegmentSeconds(value))
                && _currentSettings is { } settings
                && settings.ContinuousSegmentSeconds != _continuousSegmentSeconds)
            {
                settings.ContinuousSegmentSeconds = _continuousSegmentSeconds;
            }
        }
    }
    private int _continuousSegmentSeconds;

    /// <summary>常時録画の枝が実際に動いているか（初期化に成功し、セグメントを書いている）。</summary>
    [ObservableProperty]
    public partial bool IsContinuousRecording { get; private set; }

    /// <summary>常時録画が現在書いているセグメントのパス。</summary>
    [ObservableProperty]
    public partial string? ContinuousLastFilename { get; private set; }

    /// <summary>
    /// 常時録画側だけで起きた障害（正常時は <see langword="null"/>）。
    /// <b><see cref="LastError"/> とは別にする</b> ── 常時録画の設定ミスが
    /// イベント録画の状態表示を汚さないようにするため（隔離契約）。
    /// </summary>
    [ObservableProperty]
    public partial string? ContinuousLastError { get; private set; }

    /// <summary>常時録画で実際に動いたエンコーダーの起動文字列。</summary>
    [ObservableProperty]
    public partial string? ActualContinuousEncodingProperties { get; private set; }

    /// <summary>初期化してから書き出したセグメントの本数。</summary>
    [ObservableProperty]
    public partial int ContinuousSegmentCount { get; private set; }


    private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var settings = (EventRecorderSettings?)sender;
        if (settings is null)
            return;
        switch (e.PropertyName)
        {
            case nameof(Name): if (Name != settings.Name) Name = settings.Name; break;
            case nameof(BufferDuration): if (BufferDuration != settings.BufferDuration) BufferDuration = settings.BufferDuration; break;
            case nameof(FilenameTemplate): if (FilenameTemplate != settings.FilenameTemplate) FilenameTemplate = settings.FilenameTemplate; break;
            case nameof(Type): if (Type != settings.Type) Type = settings.Type; break;
            case nameof(SrcPipeline): if (SrcPipeline != settings.SrcPipeline) SrcPipeline = settings.SrcPipeline; break;
            case nameof(EncodingProperties): if (EncodingProperties != settings.EncodingProperties) EncodingProperties = settings.EncodingProperties; break;
            case nameof(CameraControls): if (CameraControls != settings.CameraControls) CameraControls = settings.CameraControls; break;
            case nameof(PreviewWidth): if (PreviewWidth != settings.PreviewWidth) PreviewWidth = settings.PreviewWidth; break;
            case nameof(PreviewHeight): if (PreviewHeight != settings.PreviewHeight) PreviewHeight = settings.PreviewHeight; break;
            case nameof(PreviewFps): if (PreviewFps != settings.PreviewFps) PreviewFps = settings.PreviewFps; break;
            case nameof(PreviewBitrateKbps): if (PreviewBitrateKbps != settings.PreviewBitrateKbps) PreviewBitrateKbps = settings.PreviewBitrateKbps; break;
            case nameof(ContinuousRecording): if (ContinuousRecording != settings.ContinuousRecording) ContinuousRecording = settings.ContinuousRecording; break;
            case nameof(ContinuousFramerate): if (ContinuousFramerate != settings.ContinuousFramerate) ContinuousFramerate = settings.ContinuousFramerate; break;
            case nameof(ContinuousResolution): if (ContinuousResolution != settings.ContinuousResolution) ContinuousResolution = settings.ContinuousResolution; break;
            case nameof(ContinuousEncodingProperties): if (ContinuousEncodingProperties != settings.ContinuousEncodingProperties) ContinuousEncodingProperties = settings.ContinuousEncodingProperties; break;
            case nameof(ContinuousFilenameTemplate): if (ContinuousFilenameTemplate != settings.ContinuousFilenameTemplate) ContinuousFilenameTemplate = settings.ContinuousFilenameTemplate; break;
            case nameof(ContinuousSegmentSeconds): if (ContinuousSegmentSeconds != settings.ContinuousSegmentSeconds) ContinuousSegmentSeconds = settings.ContinuousSegmentSeconds; break;
        }
    }


    private readonly EventRecorderSettings? _currentSettings;
    public EventRecorder(EventRecorderSettings settings)
    {
        _currentSettings = settings;
        _currentSettings.PropertyChanged += Settings_PropertyChanged;

        if (settings.Name == "Recorder")
            settings.Name = this.Name;
        else
            this.Name = settings.Name;
        this.BufferDuration = settings.BufferDuration;
        this.FilenameTemplate = settings.FilenameTemplate;

        this.Type = settings.Type;
        this.SrcPipeline = settings.SrcPipeline;
        this.EncodingProperties = settings.EncodingProperties ?? string.Empty;
        this.CameraControls = settings.CameraControls ?? string.Empty;

        this.PreviewWidth = settings.PreviewWidth;
        this.PreviewHeight = settings.PreviewHeight;
        this.PreviewFps = settings.PreviewFps;
        this.PreviewBitrateKbps = settings.PreviewBitrateKbps;

        this.ContinuousRecording = settings.ContinuousRecording;
        this.ContinuousFramerate = settings.ContinuousFramerate;
        this.ContinuousResolution = settings.ContinuousResolution;
        this.ContinuousEncodingProperties = settings.ContinuousEncodingProperties ?? string.Empty;
        this.ContinuousFilenameTemplate = settings.ContinuousFilenameTemplate;
        this.ContinuousSegmentSeconds = settings.ContinuousSegmentSeconds;
    }

    /// <summary>
    /// アプリ層から設定される、優先する H.264 エンコーダーのファクトリ名
    /// （<c>AppSettings.PreferredH264Encoder</c>。空なら自動選択）。
    ///
    /// <c>GStreamer.GstSharpNet</c> は <c>AppSettings</c> を知らない設計なので、
    /// <c>EventRecorder.TemplateVariables</c> と同じく static のミラーとして受け取る。
    /// </summary>
    public static string? PreferredH264Encoder { get; set; }

    /// <summary>
    /// アプリ層から設定される、録画ファイルを fragmented MP4 で書くか
    /// （<c>AppSettings.FragmentedOutput</c>）。
    ///
    /// <para>
    /// イベント録画は <see cref="BuildSrcPipeline"/> の入力なので<b>次の
    /// <c>Initialize</c> から</b>、常時録画は
    /// <c>ContinuousBranch.BuildSegmentWriterPipeline</c> の入力なので
    /// <b>次のセグメントから</b>効く。
    /// </para>
    /// <para>
    /// <c>GStreamer.GstSharpNet</c> は <c>AppSettings</c> を知らない設計なので、
    /// <see cref="PreferredH264Encoder"/> と同じく static のミラーとして受け取る。
    /// </para>
    /// <para>
    /// <b>初期値は <c>true</c></b> ── <c>AppSettings.FragmentedOutput</c> の既定と揃える
    /// （何も読み込まれていない状態でもアプリの既定と同じ形で書く）。
    /// </para>
    /// </summary>
    public static bool FragmentedOutput { get; set; } = true;

    /// <summary>
    /// fragmented 出力の fragment の長さ(ms)。<b>1 つの <c>moof</c>＋<c>mdat</c> が覆う時間</b>で、
    /// 追いかけ再生の粒度（＝新しい映像が読めるようになるまでの間隔）そのものになる。
    /// <b><c>LivePreviewStream.FragmentDurationMs</c> とは別の定数</b> ── あちらは
    /// ライブ配信の遅延を決める値で、こちらはファイルの構造を決める値である。
    /// </summary>
    public const int FragmentDurationMs = 1000;

    /// <summary>
    /// src 側（録画中だけ動く）パイプライン文字列を組み立てる。純粋関数なので
    /// 単体テストから直接検証できる。
    ///
    /// <para>
    /// <b>false のときの文字列は動かさない</b> ── 既定の録画物のバイト列がこれで決まっている。
    /// <c>faststart=true</c> は EOS のあとにファイル全体を書き直して <c>moov</c> を
    /// 先頭へ移すので、書き込み中は <c>filesink</c> の出力先が 0 バイトのままになる。
    /// </para>
    /// <para>
    /// <b>true では <c>faststart</c> を付けない</b>（付けると fragment を出す意味が無くなる）。
    /// <c>fragment-mode=dash-or-mss</c> は fragment ごとに <c>moof</c>＋<c>mdat</c> を
    /// 書き出させる指定で、EOS では末尾に <c>mfra</c> を足すだけ ── <c>moov</c> は
    /// 書き直されないので、録画中でも強制終了後でもファイルが読める。
    /// その代わり <c>mvhd</c> の尺は 0 のままで、他のプレイヤーでは録画中のファイルを
    /// シークできない。
    /// </para>
    /// <para>
    /// <b>true では <c>filesink</c> の <c>buffer-mode=unbuffered</c> が要る。</b>
    /// 既定の <c>filesink</c> は受け取ったバッファを自分の中に溜め、
    /// <c>buffer-size</c>（既定 65536）に届いてから 1 度に書くので、mux が 1 秒ごとに
    /// fragment を出しても<b>他のプロセスから見えるファイル長は 64 KiB 溜まるまで伸びない</b>
    /// ── 低ビットレートでは数秒に 1 度しか伸びず、ブラウザの追いかけ再生がデータ切れで
    /// カタつく。強制終了では溜まっていたぶん（最大 64 KiB）が失われる。
    /// </para>
    /// </summary>
    /// <param name="fragmented">fragmented MP4 で書くか（<c>FragmentedOutput</c>）。</param>
    public static string BuildSrcPipeline(bool fragmented)
        => fragmented
            ? "appsrc format=time name=src ! h264parse ! mp4mux fragment-duration="
              + FragmentDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
              + " fragment-mode=dash-or-mss name=mux ! filesink name=file buffer-mode=unbuffered"
            : "appsrc format=time name=src ! h264parse ! mp4mux faststart=true name=mux ! filesink name=file";

    /// <summary>
    /// sink 側パイプライン文字列を組み立てる。純粋関数なので単体テストから直接検証できる。
    /// </summary>
    /// <param name="type">録画種別。</param>
    /// <param name="srcPipeline">ソース側パイプライン文字列。</param>
    /// <param name="encoder">エンコーダーの起動文字列（ファクトリ名＋プロパティ）。</param>
    /// <param name="needsSystemMemory">
    /// エンコーダーがシステムメモリ入力を要求するか。<c>D3d12</c> 経路で真の場合、
    /// エンコーダーの手前へ <c>d3d12download ! videoconvert !</c> を挿入する
    /// （<c>parse_launch</c> は変換要素を自動挿入しないため、これが無いとリンクに失敗する）。
    /// <b><c>video/x-raw(memory:SystemMemory)</c> を書いてはいけない</b> ── 明示のフィーチャなので
    /// <c>memory:D3D11Memory</c> と一致せず、<b>毎フレームの GPU→CPU 往復を強制する</b>。外すと
    /// <c>d3d12download</c> は下流に合わせて折り合う（NVIDIA 実機の実測: <c>nvd3d11h264enc</c> 相手だと
    /// 両パッドとも <c>video/x-raw(memory:D3D11Memory)</c> で決まり、CPU へは降りない）。
    /// システムメモリしか受けないエンコーダー（<c>mfh264enc</c> 等）なら、同じ形のまま
    /// 交渉がシステムメモリへ落ちる。<c>videoconvert</c> は caps が <c>video/x-raw(ANY)</c> なので
    /// この交渉を妨げない（必要なときだけ形式を合わせる）。
    /// <c>System</c> 経路では入力が元からシステムメモリなので無視される。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b><c>h264parse config-interval=-1</c> は必須</b>。本アプリの中核契約は
    /// 「録画は任意の瞬間にストリームの途中から開始できる」ことであり、その再開点には
    /// SPS/PPS（パラメータセット）が無ければならない。リングバッファには数秒分しか残らないので、
    /// ストリーム先頭で1回だけ送られたパラメータセットは録画開始時には既に捨てられている。
    /// <c>config-interval=-1</c> は**全ての IDR の直前にパラメータセットを再挿入する**。
    /// </para>
    /// <para>
    /// これを入れないと、パラメータセットを繰り返さないエンコーダーでは
    /// src パイプラインの <c>h264parse</c> が全スライスのヘッダを解釈できず、
    /// <c>broken/invalid nal ... will be dropped</c> として**全 NAL を捨てる**。
    /// エラーにはならないため、中身の無い MP4 が黙って残る（NVIDIA 機の <c>nvh264enc</c> で観測。
    /// 診断ログには Type 5/1 のみが現れ、Type 7(SPS)/8(PPS) が1つも無かった）。
    /// 「エンコーダーがヘッダを繰り返してくれる」という暗黙の前提に依存していたのが誤り。
    /// </para>
    /// <para>
    /// <c>alignment=au</c> も明示する。<c>PushRecordBuffer</c> とリングバッファの PTS 退避は
    /// 「1バッファ＝1フレーム」を前提としており、<c>nal</c> アラインメントに解決されると
    /// バッファが NAL 単位になって前提が崩れるため。
    /// </para>
    /// <para>
    /// 副次的な利点として、<c>h264parse</c> が挟まることで <c>avc</c> しか出せないエンコーダーでも
    /// 下流の <c>byte-stream</c> 要求を満たせるようになり、候補の互換性が広がる。
    /// </para>
    /// </remarks>
    /// <summary>
    /// <b>プレビュー枝の <c>queue</c>。既定のままにしてはいけない。</b>
    ///
    /// <para>
    /// <c>queue</c> の既定は <c>max-size-bytes=10485760</c>（10MB）で、
    /// <b>この上限は解像度が上がると「フレーム数の上限」に化ける</b> ──
    /// queue は上限を超えていても1件目は必ず受け取るので、
    /// <c>1フレーム &gt; 5MB</c>（I420 で約 3.5Mpx ＝ 2560x1440 以上）になると
    /// <b>常に1フレームしか持てない</b>。
    /// </para>
    /// <para>
    /// <b>そして詰まったプレビュー枝は録画枝を道連れにする。</b>
    /// プレビューの <c>appsink</c> は <c>PAUSED</c> の間プリロールで止まっているので
    /// queue は排出されず、満杯の queue が <c>tee</c> を止め、エンコーダーにフレームが
    /// 届かなくなる。エンコーダーは最初の1フレームを出すまでに数フレーム溜めるので
    /// 出力が出ず、録画側 <c>appsink name=sink</c> がプリロールできず、
    /// パイプラインは <c>PLAYING</c> に到達せず、プレビューの <c>appsink</c> は
    /// 止まったまま ── <b>循環待ちで、自然には解けない。</b>
    /// 症状は「<c>IsInitialized=on</c> / <c>LastError=null</c> なのに
    /// 録画もプレビューも1フレームも進まない」（実機 4K で報告された症状そのもの）。
    /// </para>
    /// <para>
    /// <b>実測（解像度だけを変え、他は同一）:</b>
    /// 320x240 / 1280x720 / 1920x1080 は 0.39〜0.49 秒で <c>PLAYING</c>、
    /// <b>2560x1440 と 3840x2160 は 15 秒経っても到達しない。</b>
    /// この <c>leaky</c> を付けると 4K でも 0.49 秒で到達する。
    /// </para>
    /// <para>
    /// <b>プレビューは背圧を掛ける側であってはならない</b>というのが本来の設計で、
    /// その意図は <c>appsink</c> 側に <c>max-buffers=1 drop=true</c> として既に書いてある。
    /// <c>appsink</c> が <c>PAUSED</c> で止まる窓ではそれが効かないので、
    /// 手前の <c>queue</c> にも同じ意図を書く ── <c>leaky=downstream</c>（古い方を捨てる）で
    /// 最新フレームだけを通し、バイト数と時間の上限は外して<b>解像度に依存させない</b>。
    /// </para>
    /// <para>
    /// <b>エンコーダー枝の <c>queue</c> は既定のままにしてある。</b> あちらはエンコーダーが
    /// 実際に排出するので詰まらず（実機の <c>.dot</c> でも空だった）、
    /// 詰まった場合に <c>tee</c> を止めるのは<b>録画を優先する正しい背圧</b>である。
    /// 解像度が上がるとそこも実質1フレームぶんの余裕しか無くなるが、
    /// それは遅延であってデッドロックではない。
    /// </para>
    /// </summary>
    private const string PreviewQueue =
        "queue leaky=downstream max-size-buffers=1 max-size-bytes=0 max-size-time=0";

    /// <param name="continuousBranch">
    /// 常時録画の枝（<see cref="ContinuousBranch.Build"/> の出力）。空なら枝を足さない。
    /// <b>既定値を空にしてあるのは、枝の有無で既存の呼び出しと出力を変えないため</b>
    /// ── 常時録画を切っている構成のパイプライン文字列は、この機能を入れる前と 1 文字も変わらない。
    /// </param>
    public static string BuildSinkPipeline(
        EventRecordingType type, string? srcPipeline, string encoder, bool needsSystemMemory,
        string continuousBranch = "", string pinnedResolution = "")
    {
        // D3d12 経路でのみ、システムメモリを要求するエンコーダーの手前にダウンロードを挟む
        string download = type == EventRecordingType.D3d12 && needsSystemMemory
            ? "d3d12download ! videoconvert ! "
            : "";

        // **RGB を YUV へ変換する構成でだけ colorimetry を固定する。**
        //
        // 画面キャプチャは BGRA を `colorimetry=sRGB` で出す（実測: d3d12screencapturesrc の
        // src caps は `format=BGRA, colorimetry=sRGB`）。固定しないと出力の colorimetry を
        // d3d12convert が自分で決め、**transfer は sRGB のまま**（mp4 の colr と SPS VUI では
        // transfer=13）、**行列は機械依存**になる ── 実測: WARP では 1920x1080 でも
        // `2:4:7:1`（BT.601）、GPU 実機の録画は BT.709。画素そのものはスタジオレンジで
        // 正しい（白 Y=235）が、映像では使われない transfer=13 が付いたファイルは
        // 再生側が 16-235 の展開をせず、**白が灰色**・黒が浮いた低コントラストになる。
        // bt709（`2:3:5:1`）を固定しても**画素値は 1 バイトも変わらない**
        // （実測: `2:3:7:1` と同一 ── transfer だけの違いではガンマ変換は入らない）。
        //
        // **入力が既に YUV のときは固定してはいけない。** d3d12convert は YUV→YUV では
        // 行列を変換せず**タグだけ書き換える**（実測: BT.601 の画素値のまま bt709 と名乗る）
        // ── カメラ（`mfvideosrc` の NV12 / YUY2）で逆向きの嘘になる。
        // 同じ場面で `videoconvert` は実際に変換する（実測）ので、System 経路は事情が違う。
        //
        // **SD は BT.601 にする。** 行列を大きさで決めるのは映像の慣例で、GStreamer 自身の
        // 既定もそこで分かれる（実測: videotestsrc の NV12 は 576 本まで BT.601、
        // 577 本から BT.709 の画素値を出す）── タグを読まずに大きさで決める再生系が
        // 居る以上、慣例から外れた組み合わせを名乗らない方が安全である。
        // 別名（`bt709` / `bt601`）で書くのも同じ理由で、**素性の知れた組み合わせ**
        // （DXGI の色空間にも ISO の値にもそのまま対応がある）から外れないようにしている。
        string colorimetry = SourceOutputsRgb(srcPipeline)
            ? ConvertsToStandardDefinition(srcPipeline, pinnedResolution)
                ? ", colorimetry=bt601"
                : ", colorimetry=bt709"
            : "";

        // System 経路には変換段の出力 caps が無いので、`videoconvert` の後ろへ capsfilter を置く。
        // **形式は書かない**（下の `EventRecordingType.System` のコメント）。
        string systemColorimetry = colorimetry.Length == 0 ? "" : $"video/x-raw{colorimetry} ! ";

        // **tee の手前の幅・高さの固定。** 常時録画の枝で拡縮するときだけ効かせる。
        // これが無いと、枝の capsfilter が要求する小さい大きさを手前の d3d12convert が
        // 吸収して**プレビューとイベント録画まで縮む** ── ソースの caps を固定していても
        // 起こる（変換が tee の手前に居るため）。実測: ソース 1920x1080 固定・枝 960x540 で
        // プレビューが 960x540 になり、この固定を足すと 1920x1080 に戻った。
        string pinned = ContinuousBranch.TryParseResolution(pinnedResolution, out int pinnedWidth, out int pinnedHeight)
            ? $", width={pinnedWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + $", height={pinnedHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : "";

        return $"""
                {srcPipeline} !
                {(type switch
                {
                    // **`clockoverlay`（pango）を使ってはいけない。**
                    // `clockoverlay` は `libgstpango` の要素で、描画は
                    // pangocairo → cairo → （cairo が DirectWrite バックエンドで
                    // ビルドされている場合）Direct2D と流れる。
                    // cairo はこの経路で **`D2D1_FACTORY_TYPE_SINGLE_THREADED` の
                    // ファクトリをプロセス共通のグローバルに1つだけ**作る
                    // （`libcairo-2.dll` の逆アセンブルで確認）。
                    // 単一スレッド用のファクトリは**呼び出し側が直列化する責任**を負うが、
                    // cairo はそれをしないので、
                    // **複数スレッドから同時に叩かれると D2D の内部状態を壊す** ──
                    // レコーダーごとにストリーミングスレッドが1本あるので、
                    // **2本以上のレコーダーで録画するだけで再現する。**
                    // 症状はワーカーが `app.exit` も残さず消えることで、
                    // 実測した終了コードは `0xC0000005`（アクセス違反）、
                    // `0xC0000374`（グリフキャッシュの二重解放）、
                    // `0xC0000409`（CFG の間接呼び出しチェック失敗）。
                    // 詳細は docs/environment-facts.md「clockoverlay と Direct2D」。
                    //
                    // `dwriteclockoverlay` は cairo を経由せず DirectWrite を直接使う。
                    // `D3d12` 経路が元から使っているものと同じ要素・同じプロパティで、
                    // sink caps は `video/x-raw(ANY)` なのでシステムメモリでも通る（実測）。
                    // エンコーダーの直前の `videoconvert` は必須。**外すと実機でだけ壊れる。**
                    //
                    // `parse_launch` は変換要素を自動挿入しないので、ソースの画素形式が
                    // エンコーダーの sink caps に無いと **`could not link queue1 to <enc>0`**
                    // で初期化そのものが失敗する。`videoconvert` を置くと、
                    // **下流が受け付ける形式へ交渉して合わせてくれる。**
                    //
                    // **`srcPipeline` の中に `videoconvert` が在っても代わりにならない。**
                    // 典型的なソースは `videotestsrc ! videoconvert ! video/x-raw,format=I420,...`
                    // のように **capsfilter で形式を固定して終わる**ので、そこから下流は
                    // 固定された形式のまま流れる ── **必要なのは tee/queue より後、
                    // エンコーダーの直前**である。「重複しているから」と消さないこと。
                    //
                    // **形式を書いた capsfilter を後ろに付けてもいけない。** 形式を決めずに
                    // 交渉させることが、まさにこの不具合を直している点である。
                    //
                    // **colorimetry だけを書く capsfilter は別**（`{systemColorimetry}`）。
                    // 形式を空けたままなので交渉を妨げず、`videoconvert` は要求された行列へ
                    // **実際に変換する**（実測。`d3d12convert` の YUV→YUV と違う）。
                    // 素の `video/x-raw`（＝システムメモリ）で書いてよいのは、System の候補が
                    // `x264enc` / `openh264enc` / `mfh264enc` の 3 つで、いずれも
                    // **システムメモリの YUV しか受けない**ため ── `videoconvert` の出力は
                    // どのみちシステムメモリで、往復は増えない。手動指定で GPU メモリを受ける
                    // エンコーダーを選んだ場合は CPU 経路に倒れるが、**色は正しいまま**である。
                    //
                    // 実際に踏んだ失敗（GPU 実機・同梱構成）:
                    // `Type=System` の自動選択が `mfh264enc` を選べず `recorder.init fail`。
                    // ハードウェアの MediaFoundation MFT は I420 を受けず、
                    // ソースは `format=I420` 固定だった。**同じ機械で `Type=D3d12` の
                    // `mfh264enc` 手動指定は通っている** ── あちらには
                    // `d3d12download ! ... ! videoconvert !` が在るからで、差はそこだけ。
                    // GPU 無しの開発機でも `openh264enc`（I420 のみ）＋ NV12 ソースで
                    // 同じ形の失敗を再現できる。
                    //
                    // **テストや検証スクリプトが `format=I420` を固定していると、
                    // 試す全エンコーダーが I420 を受けるため、この失敗は再現しない。**
                    EventRecordingType.System => $""""
                                dwriteclockoverlay time-format="%Y-%m-%d %H:%M:%S" auto-resize=false font-family=Arial font-size=36 !
                                tee name=t ! {PreviewQueue} ! appsink max-buffers=1 drop=true sync=false name=preview t. ! queue !
                                videoconvert ! {systemColorimetry}{encoder}
                                """",
                    EventRecordingType.D3d12 => $""""
                                d3d12upload ! d3d12convert ! video/x-raw(memory:D3D12Memory), format=NV12{colorimetry}{pinned} !
                                dwriteclockoverlay time-format="%Y-%m-%d %H:%M:%S" auto-resize=false font-family=Arial font-size=36 !
                                tee name=t ! {PreviewQueue} ! d3d12download ! video/x-raw(memory:SystemMemory) ! appsink max-buffers=1 drop=true sync=false name=preview t. ! queue !
                                {download}{encoder}
                                """",
                    _ => "",
                })} !
                h264parse config-interval=-1 !
                video/x-h264, stream-format=byte-stream, alignment=au, profile=main !
                appsink name=sink sync=false
                """
            + (continuousBranch.Length == 0 ? "" : "\n" + continuousBranch);
    }

    /// <summary>RGB の画素形式（<see cref="SourceOutputsRgb"/> の判定表）。</summary>
    private static readonly HashSet<string> RgbSourceFormats = new(StringComparer.Ordinal)
    {
        "RGBA", "BGRA", "RGBx", "BGRx", "xRGB", "xBGR", "ARGB", "ABGR", "RGB", "BGR",
    };

    /// <summary>
    /// ソースが RGB の画素を出すか ── <see cref="BuildSinkPipeline"/> の変換段が
    /// RGB→YUV を行う構成かどうかの判定。
    ///
    /// <para>
    /// caps に画素形式が書かれていればそれで決め、無いときは<b>要素名で決める</b>
    /// ── 画面キャプチャの caps は解像度と framerate しか持たない
    /// （<see cref="SrcPipelineBuilder"/> のカタログ）が、要素は必ず BGRA を出す。
    /// </para>
    /// <para>
    /// <b>判らないものを RGB 扱いしない。</b> 誤って YUV へ colorimetry を付けると
    /// d3d12convert は画素を変換せずタグだけ書き換えるので、いままでどおり
    /// d3d12convert に決めさせる方が安全である。
    /// </para>
    /// </summary>
    private static bool SourceOutputsRgb(string? srcPipeline)
    {
        var parsed = SrcPipelineBuilder.Parse(srcPipeline);
        return parsed.CapsFields.TryGetValue("format", out string? format)
            ? RgbSourceFormats.Contains(format)
            : parsed.SourceElement is "d3d12screencapturesrc" or "d3d11screencapturesrc";
    }

    /// <summary>
    /// SD と HD の境目（この本数までが SD）。
    ///
    /// <para>
    /// GStreamer 自身の既定と同じ値 ── 実測: <c>videotestsrc</c> の NV12 は
    /// 576 本まで BT.601、577 本から BT.709 の画素値を出す。
    /// </para>
    /// </summary>
    private const int StandardDefinitionMaxHeight = 576;

    /// <summary>
    /// 変換段の出力が SD か ── <b>大きさが判らないときは false</b>（HD 扱い）。
    ///
    /// <para>
    /// 画面キャプチャの caps は解像度を持たない構成が既定で、そのときの実体は
    /// モニターの実寸である ── 576 本以下の画面は無いので HD 扱いでよい。
    /// </para>
    /// </summary>
    private static bool ConvertsToStandardDefinition(string? srcPipeline, string pinnedResolution)
    {
        // tee の手前を固定しているなら、それが変換段の出力の大きさそのもの。
        if (ContinuousBranch.TryParseResolution(pinnedResolution, out _, out int pinnedHeight))
            return pinnedHeight <= StandardDefinitionMaxHeight;

        return SrcPipelineBuilder.Parse(srcPipeline).CapsFields.TryGetValue("height", out string? height)
            && int.TryParse(height, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0
            && parsed <= StandardDefinitionMaxHeight;
    }

    /// <summary>
    /// 試行するエンコーダー候補を決める。
    ///
    /// <see cref="EncodingProperties"/> がユーザーによって明示指定されている場合は、
    /// **その1件のみ**を候補とする（手動指定を常に優先し、勝手に別のエンコーダーへ
    /// フォールバックして黙って違う設定で録画することはしない）。
    /// </summary>
    private IReadOnlyList<H264EncoderDef> BuildEncoderCandidates()
    {
        if (!string.IsNullOrEmpty(EncodingProperties))
        {
            // 明示指定の文字列はファクトリ名＋プロパティの自由形式。先頭トークンを
            // ファクトリ名とみなし、メモリ要件はカタログと同じ規則で決める。
            //
            // ここを一律 false にすると、AMD / NVIDIA 機のユーザーが最も自然な回避策として
            // EncodingProperties に "x264enc ..." を指定したときに d3d12download が入らず、
            // D3D12 メモリを直結できずリンクに失敗する（しかも手動指定は
            // フォールバックしないので即 IsInitialized=false になる）。
            string factory = EncodingProperties.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
                ? first
                : EncodingProperties;
            return (H264EncoderDef[])[
                new H264EncoderDef(factory, EncodingProperties, EncoderCatalog.NeedsSystemMemoryFor(factory, Type))];
        }

        // GOP 長は**ソースの framerate から**決める。フレーム数を固定すると
        // 低いレートのソースでキーフレーム間隔が伸び、事前バッファの短い構成で
        // 録画の立ち上がりがそのぶん遅れる（EncoderCatalog.TargetKeyframeIntervalSeconds）。
        var resolved = EncoderCatalog.Resolve(
            Type, PreferredH264Encoder, EncoderCatalog.ProbeWithGStreamer,
            EncoderCatalog.GopForFramerate(SourceFramerate()));
        return EncoderCatalog.ExpandAttempts(resolved).ToArray();
    }

    /// <summary>
    /// カメラ（<c>mfvideosrc</c>）の設定を当てる。<c>InitializeCore</c> の末尾から呼ばれる。
    ///
    /// <para>
    /// <b>録画を巻き添えにしない。</b> 例外は投げず、駄目だった理由を
    /// <see cref="CameraControlsLastError"/> と <c>camera.control</c> に残すだけにする
    /// （常時録画の隔離契約と同じ判断）── カメラ設定の不備で「録画できない」に
    /// してはならない。
    /// </para>
    /// <para>
    /// <b>指定が無いときとソースがカメラでないときは、デバイスを開きもせずに戻る。</b>
    /// ここは <c>_stateLock</c> を握ったまま走るので、無条件にデバイスを開くと
    /// 画面キャプチャのレコーダーまで初期化が遅くなる。
    /// </para>
    /// </summary>
    private void ApplyCameraControls()
    {
        CameraControlsLastError = null;

        if (string.IsNullOrWhiteSpace(CameraControls))
            return;

        // 解決規則は CameraControl.ResolveDevicePath に 1 本化してある
        // （編集ダイアログと同じ入力・同じ判定を使う）。
        switch (CameraControl.ResolveDevicePath(CameraSourcePipeline, out string? devicePath))
        {
            case CameraDeviceResolution.NotACamera:
                CameraControlsLastError =
                    $"the source is not {CameraControl.SourceElement}; camera controls do not apply";
                return;

            case CameraDeviceResolution.NoDevicePath:
                CameraControlsLastError =
                    "the pipeline does not say which camera to use"
                    + " (set device-path, device-name or device-index on mfvideosrc)";
                Components.ActivityLog.Warn("camera.control", $"recorder='{Name}' {CameraControlsLastError}");
                return;

            case CameraDeviceResolution.DeviceNotFound:
                CameraControlsLastError =
                    "the camera named by the pipeline was not found on this PC (unplugged or renamed)";
                Components.ActivityLog.Warn("camera.control", $"recorder='{Name}' {CameraControlsLastError}");
                return;
        }

        try
        {
            string? failure = CameraControl.Apply(devicePath, CameraControls);
            if (failure is null)
            {
                Components.ActivityLog.Info("camera.control", $"recorder='{Name}' applied='{CameraControls}'");
                return;
            }
            CameraControlsLastError = failure;
            Components.ActivityLog.Warn("camera.control", $"recorder='{Name}' {failure}");
        }
        catch (Exception ex)
        {
            // ここまで来る想定は無い（CameraControl 側で握っている）が、
            // 万一漏れても録画を止めないための最後の砦。
            CameraControlsLastError = $"{ex.GetType().Name}: {ex.Message}";
            Components.ActivityLog.Error("camera.control", $"recorder='{Name}' {CameraControlsLastError}");
        }
    }

    /// <summary>
    /// カメラ設定の解決に使うパイプライン文字列。
    /// <b>実際に動いている構成（<see cref="ActualSrcPipeline"/>）を優先する</b> ──
    /// 編集しただけでまだ初期化していない値で開くと、<b>いま映っているカメラとは
    /// 別のデバイス</b>を触ることになる。編集ダイアログも同じものを使う。
    /// </summary>
    public string? CameraSourcePipeline => ActualSrcPipeline ?? SrcPipeline;

    /// <summary>
    /// パイプラインを構築して常時バッファリングを開始する。
    /// 状態遷移（<see cref="Initialize"/>/<see cref="Start"/>/<see cref="Stop"/>/<see cref="Close"/>）は
    /// <see cref="_stateLock"/> で直列化する。ロックは再入可能（同一スレッドの
    /// <c>Initialize → Close</c> は同じロックを取り直す）。
    /// </summary>
    public void Initialize()
    {
        try
        {
            lock (_stateLock)
                InitializeCore();
        }
        catch
        {
            // **失敗しても復帰の芽を残す。** ここを素通りさせると
            // 「パイプラインもバスも無い＝二度とエラーが飛ばない」状態になり、
            // デバイスを挿し直しても永久に何も起きない。到達経路は 3 つあり、
            // どれも同じ症状になる ── 起動時にカメラが無い（AddRecorderFor）、
            // 復帰のエスカレーションがデバイス不在のまま作り直そうとした、
            // パイプライン編集ダイアログでの再初期化。
            TryScheduleDeviceRebuild();
            throw;
        }
    }

    /// <summary>
    /// 初期化に失敗したとき、<b>デバイスの到着で作り直す</b>連鎖を張る。
    ///
    /// <para>
    /// <b>種別の付く映像源に限る。</b> パイプライン文字列の打ち間違い（テストソースや
    /// 未知の要素）を 60 秒ごとに永久に再試行しても得るものが無い ──
    /// 戻ってくるものが無いからである。
    /// </para>
    /// <para>
    /// <b><c>_stateLock</c> を保持しないところから呼ぶこと。</b> <see cref="Initialize"/> の
    /// <c>catch</c> は <c>lock</c> ブロックの外なので、この条件を満たしている。
    /// </para>
    /// </summary>
    private void TryScheduleDeviceRebuild()
    {
        if (_disposedValue)
            return;

        string? element = SrcPipelineBuilder.Parse(ActualSrcPipeline ?? SrcPipeline).SourceElement;
        if (DeviceKindRules.ForElement(element) == DeviceKind.None)
            return;

        ScheduleRestart(element ?? "?", rebuildOnly: true);
    }

    private void InitializeCore()
    {
        // ロック契約（このファイルの doc に分散している）を Debug ビルドで機械検査する。
        System.Diagnostics.Debug.Assert(Monitor.IsEntered(_stateLock), "InitializeCore must run under _stateLock");

        // Dispose 済みなら作り直さない。復帰エスカレーションの Initialize() は
        // IsCancellationRequested を確認してから _stateLock を待つため、確認と取得の間に
        // Close/Dispose が完了していると、ここで破棄済みのレコーダーが蘇生してしまう
        // （誰からも管理されないパイプラインがエンコーダーごと残る）。
        // キャンセル確認だけでは塞げないので、ロック取得後のここで検査する。
        ObjectDisposedException.ThrowIf(_disposedValue, this);

        Close();

        // **モニターのパス指定はここで 1 回だけ解く。**
        // `monitor-device-path` は要素のプロパティではなくアプリの擬似プロパティで、
        // 実行時のハンドル（`monitor-handle`）へ置き換えて消す必要がある
        // ── 残したまま渡すと `no property` でパイプラインが組めない。
        // 候補ループの中（InitializeWith）で解くと、同じ失敗が候補の数だけ繰り返される。
        //
        // **列挙は指定が在るときだけ走らせる。** InitializeCore はレコーダーごと・
        // 復帰のたびに走るので、無条件に列挙するとテストソースのレコーダーまで
        // デバイスプロバイダを 60 秒ごとに起こし続けることになる。
        // **1 回だけ読む。** SrcPipeline は UI から書き換わりうるので、門と解決で
        // 別の値を見ると「列挙しなかったのに解決だけ走る」形になりうる。
        string? configuredSrcPipeline = SrcPipeline;
        IReadOnlyList<MonitorInfo> monitors = MonitorSelection.RequiresMonitors(configuredSrcPipeline)
            ? GstIntrospect.GetMonitors()
            : Array.Empty<MonitorInfo>();
        MonitorSelectionResult monitorSelection = MonitorSelection.Resolve(configuredSrcPipeline, monitors);
        if (!monitorSelection.Succeeded)
        {
            Close();
            // **番号へ縮退させない。** 指定されたモニターが今つながっていないのだから、
            // 番号で代用すると黙って別の画面を録り始める ── 直そうとしている取り違えを
            // 自分で作ることになる。失敗させれば復帰の連鎖（TryScheduleDeviceRebuild は
            // 要素名で種別を引く）が拾い、モニターが戻れば作り直される。
            string unresolved = $"the monitor could not be resolved: {monitorSelection.Failure}";
            // 初期化の失敗も LastError に残す（候補が 0 件のときと同じ理由 ── 呼び出し側は
            // activity.log へ書くだけなので、ここで入れないと画面にも status にも出ない）。
            LastError = unresolved;
            throw new InvalidOperationException(unresolved);
        }
        _resolvedSrcPipeline = monitorSelection.Pipeline;

        // 効かせられなかった指定は黙って捨てない（常時録画の上書きと同じ流儀）。
        // 縮退したことは画面からは分からないので、記録と状態の両方へ出す。
        string? monitorWarning = monitorSelection.Warning;
        if (monitorWarning is not null)
            Components.ActivityLog.Warn("recorder.warning", $"recorder='{Name}' {monitorWarning}");

        var candidates = BuildEncoderCandidates();
        if (candidates.Count == 0)
        {
            Close();
            string noEncoder = $"No usable H.264 encoder was found for Type={Type}. "
                + $"Probed: {EncoderCatalog.LastProbe?.ToLogLine() ?? "(not probed)"}";
            // **初期化の失敗も LastError に残す。** 呼び出し側（AddRecorderFor）は
            // activity.log へ書くだけなので、ここで入れないと「録画できない」ことが
            // 画面（PropertyGrid の LastError 行）にも status にも出ない。
            LastError = noEncoder;
            throw new InvalidOperationException(noEncoder);
        }

        List<string> failures = [];
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            string? continuousFailure = null;
            try
            {
                // **常時録画の隔離契約（2 段初期化）。**
                // 常時枝は同じ ParseLaunch に同居するので、常時録画側の設定ミス
                // （壊れたエンコーダー文字列・無い要素・通らない解像度）が
                // そのままイベント録画を殺してしまう ── このアプリでは最悪の退行。
                // 枝つきで組めなかったら、**同じ候補で枝なしをもう一度試す**。
                // それも駄目なら、初めて「その候補の失敗」として次の候補へ送る。
                if (ContinuousRecording)
                {
                    try
                    {
                        InitializeWith(candidate, withContinuous: true);
                    }
                    catch (Exception continuousEx)
                    {
                        Close();
                        continuousFailure = continuousEx.Message;
                        InitializeWith(candidate, withContinuous: false);
                    }
                }
                else
                    InitializeWith(candidate, withContinuous: false);

                // 初期化できた＝「今の状態」は正常。前回の障害表示を消す
                // （StartCore が録画開始時に消すのと同じ規約。消さないと、パイプラインを
                //  直して復帰させても status が終了コード 15 を返し続ける）。
                LastError = null;

                // ただしモニター指定の縮退は「今の状態」そのものなので残す
                // （バスの Warning と同じ "[warning] " 接頭辞の規約）。
                if (monitorWarning is not null)
                    LastError = $"[warning] {monitorWarning}";

                if (continuousFailure is not null)
                {
                    string isolated = "the continuous-recording branch could not be built, so it is off; "
                        + $"the event recording is unaffected: {continuousFailure}";
                    ContinuousLastError = isolated;
                    Components.ActivityLog.Warn("recorder.continuous-init fail",
                        $"recorder='{Name}' {isolated}");
                }

                // 採用結果は activity.log へ出す。DebugLogEx.Log（gst_debug_log 経由）は
                // GST_DEBUG が未設定だと何も出力しないため、既定の起動では「どのエンコーダーが
                // 実際に動いたか」が誰にも見えなくなる ── GPU 実機での確認がこの1行に依存する。
                Components.ActivityLog.Info("gst.encoder selected",
                    $"recorder='{Name}' type={Type} encoder='{candidate.LaunchString}' "
                    + $"needsSystemMemory={candidate.NeedsSystemMemory} failedAttempts={failures.Count}");
                if (0 < failures.Count)
                    Components.ActivityLog.Warn("gst.encoder fallback-from", string.Join(" | ", failures));
                return;
            }
            catch (Exception ex)
            {
                // 「存在する」≠「動く」。ParseLaunch のリンク失敗（メモリフィーチャ不一致・
                // 未知のプロパティ）も SetState(Playing) の失敗も、ここで次の候補へ送る。
                Close();
                failures.Add($"{candidate.LaunchString}: {ex.Message}");
                Components.ActivityLog.Warn("gst.encoder candidate-failed",
                    $"recorder='{Name}' encoder='{candidate.LaunchString}': {ex.Message}"
                    + $" pipeline='{_lastAttemptedSinkPipeline}'");

                if (i == candidates.Count - 1)
                {
                    string allFailed = $"All {candidates.Count} H.264 encoder candidate(s) failed for Type={Type}: "
                        + string.Join(" | ", failures);
                    // 全候補が落ちた＝このレコーダーでは録画できない。呼び出し側は
                    // activity.log へ書くだけなので、画面と status へ届けるには
                    // ここで LastError に入れる必要がある。
                    LastError = allFailed;
                    throw new InvalidOperationException(allFailed, ex);
                }
            }
        }
    }

    /// <summary>
    /// <b>sink パイプラインが実際に <c>PLAYING</c> へ到達するのを待つ上限(ms)。</b>
    ///
    /// <para>
    /// <b>これは独立に決めてよい値ではない。</b> 起動時のレコーダー初期化は UI スレッドで走り、
    /// <c>GstControllerViewModel.IsReady</c> が立つ前に完了する必要がある
    /// ── その間に届いた CLI コマンドは <c>RecorderControlService.ReadyWaitTimeout</c> しか待たない。
    /// 関係は <c>PlayingStateBudgetTests</c>（L1）が機械的に守っている。
    /// </para>
    /// <para>
    /// <b>値の根拠は実測。</b> 健全なパイプラインが <c>PLAYING</c> に達するまでは
    /// 320x240 / 1280x720 / 1920x1080 / 2560x1440 / 3840x2160 のいずれでも
    /// <b>0.39〜0.67 秒</b>だった（詳細は docs/environment-facts.md）。
    /// 5 秒はその 7 倍以上ある。
    /// </para>
    /// <para>
    /// <b>誤検出しうる唯一の形は「極端に低いフレームレート × 出力の遅いエンコーダー」。</b>
    /// エンコーダーは最初の1フレームを出すまでに数フレームぶんの入力を溜めるので、
    /// たとえば 1fps で 4 フレーム溜めるエンコーダーは正当に 4 秒かかる。
    /// カタログの既定はいずれも低遅延（<c>tune=zerolatency</c> / 小さい <c>gop-size</c>）なので
    /// 該当するのは利用者が <c>EncodingProperties</c> を手書きした場合に限られ、
    /// そのときは候補フォールバックが働き、失敗すれば <c>recorder.init fail</c> が残る
    /// ── <b>黙って何も録れないより、大きな声で失敗する方を選ぶ。</b>
    /// </para>
    /// </summary>
    public const int PlayingStateTimeoutMs = 5000;

    /// <summary>指定したエンコーダー候補ひとつでパイプラインを構築して再生を開始する。</summary>
    private void InitializeWith(H264EncoderDef encoder, bool withContinuous = false)
    {
        try
        {
            // **ここから先は解決済みの文字列しか見ない。** 生の SrcPipeline には
            // 擬似プロパティ monitor-device-path が残っているので、渡すと組めない。
            string? srcPipeline = _resolvedSrcPipeline;

            H264EncoderDef? continuousEncoder = withContinuous ? ResolveContinuousEncoder() : null;
            string continuousBranch = "";
            string? droppedOverride = null;
            string pinnedResolution = "";
            if (continuousEncoder is not null)
            {
                var plan = ContinuousBranch.Plan(
                    Type, continuousEncoder.LaunchString, continuousEncoder.NeedsSystemMemory,
                    ContinuousFramerate, ContinuousResolution,
                    ContinuousBranch.SourceSizeIsPinned(srcPipeline),
                    ContinuousBranch.SourceFramerateIsPinned(srcPipeline));
                continuousBranch = plan.Branch;
                droppedOverride = plan.DroppedOverride;
                // 枝の中で拡縮するなら、tee の手前も同じ大きさで固定しないと
                // 手前の変換が枝の要求を吸収して本線まで縮む。
                if (plan.AppliesResolution)
                    pinnedResolution = ContinuousBranch.SourceSize(srcPipeline);
            }

            string SinkPipelineStr =
                BuildSinkPipeline(Type, srcPipeline, encoder.LaunchString, encoder.NeedsSystemMemory,
                    continuousBranch, pinnedResolution);

            // 失敗したときに「何を組もうとしたのか」を残す。リンク失敗のメッセージは
            // 要素名しか言わないので、これが無いと caps の書き方の誤りを机上で追えない。
            _lastAttemptedSinkPipeline = SinkPipelineStr;
            string SrcPipelineStr = BuildSrcPipeline(EventRecorder.FragmentedOutput);

            // 新しいパイプラインなので EOS の記憶も畳む（次の障害は要素単位から試せる）。
            // **組み立てより前に畳む。** バスの購読（と溜まっていた分の汲み切り）より後ろに
            // 置くと、新しいパイプラインが即座に出した EOS の印をここで消してしまい、
            // その EOS が予約した連鎖は要素単位の再開から始まる ── 戻っていないのに
            // result=ok と報告される形になる。古い購読は InitializeCore の Close() で
            // 外れているので、ここで畳んでも前のパイプラインの EOS を拾うことはない。
            _sinkSawEos = false;

            // ParseLaunch は失敗を Gst.GLib.GException で投げる。候補ごとの失敗は
            // InitializeCore の catch が次の候補へ送るので、ここでは握らない。
            _sinkPipeline = (Pipeline)Global.ParseLaunch(SinkPipelineStr);
            _sinkPipeline.SetName("event-recorder-sink-pipeline");
            _sinkBus = _sinkPipeline.GetBus();
            _appSink = (GstApp.AppSink)_sinkPipeline.GetByName("sink")!;
            _previewSink = (GstApp.AppSink)_sinkPipeline.GetByName("preview")!;
            _srcPipeline = (Pipeline)Global.ParseLaunch(SrcPipelineStr);
            _srcPipeline.SetName("event-recorder-src-pipeline");
            _srcBus = _srcPipeline.GetBus();
            _appSrc = (GstApp.AppSrc)_srcPipeline.GetByName("src")!;
            _mux = _srcPipeline.GetByName("mux")!;
            _file = _srcPipeline.GetByName("file")!;

            if (_sinkPipeline.SetState(State.Playing) == StateChangeReturn.Failure)
                throw new InvalidOperationException("ERROR: pipeline doesn't want to play.");

            WaitUntilPlaying(_sinkPipeline);

            // **バスの購読はここで 1 回だけ。** src 側は録画中しか動かないが、
            // GstPipeline は READY→NULL でバスを flushing 化する（auto-flush-bus 既定 true）ので、
            // 停止中の src バスには post が届かない ── 録画のたびに掛け直す必要は無い。
            // **PLAYING 到達より後に置く。** 候補フォールバックで落ちた候補のメッセージが
            // recorder.error として残らない現行の挙動をここで保つ。
            if (_sinkBus is { } sinkBus)
                _sinkBusSubscription = SubscribeBus(sinkBus, "sink", _sinkThrottles);
            if (_srcBus is { } srcBus)
                _srcBusSubscription = SubscribeBus(srcBus, "src", _srcThrottles);

            // appsrc のキャップスはここでは設定しない。_appSink.GetCaps() は appsink に
            // 設定された（テンプレート由来の、しばしば ANY な）キャップスであって、
            // 実際にネゴシエートされた結果ではないため。
            // ProcessRecordSample が最初のサンプルの sample.GetCaps() から設定する。

            _isAlive = true;

            // **サンプルを跨ぐ状態はセッションごとに new してクロージャへ渡す。**
            // フィールドに置くと、再初期化のたびに前セッションの caps 設定済み・
            // I フレーム検出・開始 PTS が持ち越され、作り直したパイプラインの最初の録画が
            // 古い基準で押し込まれる（RecordSampleState の doc を参照）。
            var recordState = new RecordSampleState(_recordingSession);
            _appSink?.SetSimpleCallbacks(onNewSample: sink =>
            {
                // **例外を漏らさない。** トランポリンは抜けた例外を FlowReturn.Error へ
                // 変換するので、漏らすとパイプラインがエラー停止する ── サンプル 1 枚の
                // 失敗で録画そのものを落とさない（握ってログし、次のサンプルへ進む）。
                try
                {
                    // teardown 中は押し込まない。CloseCore は _isAlive を倒してから
                    // quiesce（sink パイプラインの Null 化）に入る。
                    if (!_isAlive)
                        return FlowReturn.Ok;

                    // **1 プルでは足りない。** appsink は 1 render につき 1 回しか呼ばないので、
                    // 取り付け前（PLAYING 到達〜ここ）に溜まった分は初回に吸い切らないと、
                    // そのぶんの遅延が定常的に残り続ける。実測（videotestsrc 320x240/15fps）:
                    // 初回コールバックが吸うのは、プロセス最初のパイプラインで 4〜6 枚
                    // （プラグイン読み込みで窓が延びる）、以降のパイプラインでは 2 枚。
                    while (sink.TryPullSample(ClockTime.Zero) is { } sample)
                    {
                        using (sample)
                            ProcessRecordSample(sample, recordState);
                    }
                }
                catch (Exception ex)
                {
                    Log(DebugLevel.Error, $"GstEventRecorder sink callback failed!\n{ex}");
                }

                // **空プルでも Ok を返す。** Eos を返すと枝が止まる
                // （1 コールバック 1 プルの例をドレイン形へ写してはいけない）。
                return FlowReturn.Ok;
            });

            // プレビュー枝も同じ形で受ける。**サンプルはハンドラを抜けた時点で破棄される**
            // ので、購読側（Preview イベント）は同期で消費し切ること。
            _previewSink?.SetSimpleCallbacks(onNewSample: sink =>
            {
                // 例外を漏らさない（sink 側と同じ理由 ── トランポリンが FlowReturn.Error へ
                // 変換して枝が止まる）。
                try
                {
                    if (!_isAlive)
                        return FlowReturn.Ok;

                    // 1 render につき 1 回しか呼ばれないので、取り付け前に溜まった分も
                    // ここで吸い切る（sink 側と同じドレイン形）。
                    while (sink.TryPullSample(ClockTime.Zero) is { } sample)
                    {
                        using (sample)
                        {
                            // **DASH の配信エンジンも同じサンプルを同期で消費する。**
                            // 読み手が 0 人なら volatile の 1 回読みで抜けるので、
                            // 配信していないレコーダーの費用は変わらない。
                            _dash?.OnRawSample(sample);
                            OnPreview(sample);

                            // **サムネイルは要求が積まれている 1 枚だけ。** 要求が無い
                            // 間の費用はキューの空判定 1 回で、録画には触らない。
                            if (!_thumbnailRequests.IsEmpty)
                                CaptureThumbnail(sample);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(DebugLevel.Error, $"GstEventRecorder preview callback failed!\n{ex}");
                }

                // 空プルでも Ok（Eos を返すと枝が止まる）。
                return FlowReturn.Ok;
            });

            ActualType = Type;
            // **解決後の文字列を出す。** ここは「実際に使った値」を意味する読み取り専用の窓で、
            // カメラ設定の解決（CameraSourcePipeline）もこれを優先して読む。
            ActualSrcPipeline = srcPipeline;
            // 「実際に動いたエンコーダー」を読み取り専用プロパティへ出す。
            // 自動選択・フォールバックが起きた場合、UI 改修なしでその結果が見える。
            ActualEncodingProperties = encoder.LaunchString;

            // ライブプレビューの器を用意する。**mux は作らない** ── 購読が 1 人も
            // 来なければ何も起きず、録画経路の費用も変わらない。ここに置くのは
            // 常時録画と同じ理由で、sink パイプラインが PLAYING に達してからでないと
            // 押し込む相手（枝2 の appsink）が動いていないためである。
            Volatile.Write(ref _live, new LivePreviewStream(new LivePreviewHost(this)));

            // DASH の配信エンジンも同じ段で用意する。**第 2 パイプラインは作らない**
            // ── 誰も引かなければ何も起きず、録画経路の費用も変わらない。
            Volatile.Write(ref _dash, new DashPreviewStream(new DashPreviewHost(this)));

            // 常時録画は sink パイプラインが PLAYING になってから起こす。
            // ここで投げると呼び出し側（InitializeCore）が枝なしで組み直す。
            if (continuousEncoder is not null)
            {
                StartContinuous(continuousEncoder);

                // 効かせられなかった上書きは黙って捨てない。設定が無視されたことは
                // 画面からは分からないので、状態と activity.log の両方へ出す。
                if (droppedOverride is not null)
                {
                    ContinuousLastError = droppedOverride;
                    Components.ActivityLog.Warn("recorder.continuous-init fail",
                        $"recorder='{Name}' {droppedOverride}");
                }
            }

            IsInitialized = true;

            // カメラ制御は PLAYING 到達後に 1 回だけ当てる。**ここに置くのは、UI からの
            // Initialize() だけでなく自動復帰のエスカレーション（RestartLoopAsync →
            // Initialize()）でも設定が戻るようにするため** ── 呼び出し側それぞれに
            // 書くと必ずどれかが漏れる。失敗しても IsInitialized は下げない。
            ApplyCameraControls();
        }
        catch
        {
            // **破棄する前にグラフを残す。** Close() がパイプラインを捨てるので、
            // ここを過ぎると「どこまで組めていたか」を見る手段が無くなる。
            WriteFailureGraph();
            Close();
            throw;
        }
    }

    /// <summary>
    /// 初期化に失敗したときのパイプライングラフ（<c>.dot</c>）を書き出す。
    ///
    /// <para>
    /// <b>書けるのは「パイプラインが出来たあとの失敗」だけ。</b> <c>ParseLaunch</c> の
    /// リンク失敗ではパイプラインそのものが存在しないので <c>.dot</c> は原理的に作れない
    /// ── その場合の手がかりは <c>gst.encoder candidate-failed</c> に添える
    /// パイプライン文字列の方である（要素名しか言わないリンクエラーを、書いた caps と
    /// 突き合わせられる）。
    /// </para>
    /// <para>
    /// 保存先が設定されていなければ<b>何も書かずに返る</b>（<c>DebugLogEx</c> と同じ規約）。
    /// 頼まれてもいないのに実行ファイルの隣へファイルを撒かないため。
    /// </para>
    /// </summary>
    private void WriteFailureGraph()
    {
        string directory = DebugDumpDotDirectory;
        if (string.IsNullOrEmpty(directory) || _sinkPipeline is not { } pipeline)
            return;

        try
        {
            string written = DebugLogEx.WriteDotFile(
                pipeline, directory, $"{Name}.init-failed", System.DateTime.Now);
            Components.ActivityLog.Info("gst.dot", $"recorder='{Name}' file='{written}'");
        }
        catch (Exception ex)
        {
            // 診断のための処理で初期化の失敗を上書きしない（元の例外を投げ直す側が本体）。
            Components.ActivityLog.Error("gst.dot", $"recorder='{Name}' {ex.Message}");
        }
    }

    /// <summary>
    /// 直近に組もうとした sink パイプライン文字列（失敗の診断用）。
    /// <c>ParseLaunch</c> のリンクエラーは要素名しか言わないので、これが無いと
    /// caps の書き方の誤り（<c>framerate=5</c> と <c>5/1</c> の違い等）を追えない。
    /// </summary>
    private string? _lastAttemptedSinkPipeline;

    /// <summary>
    /// <see cref="SrcPipeline"/> から擬似プロパティ <c>monitor-device-path</c> を解いた文字列
    /// （<see cref="MonitorSelection"/>）。<b>実際に <c>gst_parse_launch</c> へ渡すのはこちら</b>。
    ///
    /// <para>
    /// 解決は <see cref="InitializeCore"/> の<b>エンコーダー候補ループより前で 1 回だけ</b>行い、
    /// <see cref="InitializeWith"/> は常にこの値を見る ── 候補ごとに解き直すと、
    /// 同じ失敗が候補の数だけ繰り返され、モニターの列挙も候補の数だけ走る。
    /// </para>
    /// </summary>
    private string? _resolvedSrcPipeline;

    /// <summary>
    /// <c>.dot</c> の保存先（<c>AppSettings.GstDebugDumpDotDir</c> の static ミラー）。
    /// 空なら書かない。<c>GStreamer.GstSharpNet</c> は <c>AppSettings</c> を知らない設計なので、
    /// <see cref="OutputDirectory"/> と同じく static のミラーとして受け取る。
    /// </summary>
    public static string DebugDumpDotDirectory { get; set; } = "";

    /// <summary>
    /// 常時録画のエンコーダーを決める。
    ///
    /// <para>
    /// <b>独自の候補チェーンは持たない。</b> 明示指定があればそれ 1 件、無ければ
    /// カタログの解決結果の<b>先頭 1 件だけ</b>を使う。入れ子のフォールバックを作ると、
    /// 候補数 × 2 段の <c>ParseLaunch</c> が起動時間に効くうえ、
    /// どの組み合わせで動いたのかが <see cref="ActualContinuousEncodingProperties"/> から読めなくなる
    /// ── 常時枝が組めないときは、黙って別のエンコーダーへ滑るより
    /// <b>枝を落として理由を残す</b>方（2 段初期化）を選ぶ。
    /// </para>
    /// </summary>
    /// <summary>ソースの caps が名乗る framerate（無ければ空）。</summary>
    private string SourceFramerate()
        => SrcPipelineBuilder.Parse(SrcPipeline).CapsFields.TryGetValue("framerate", out var rate) ? rate : "";

    /// <summary>
    /// 常時録画の枝を実際に流れるフレームレート。上書きが効く場合はそれ、
    /// 効かなければソース側の framerate。
    /// </summary>
    private string ContinuousEffectiveFramerate()
        => ContinuousBranch.RequiresVideorate(ContinuousFramerate) ? ContinuousFramerate : SourceFramerate();

    private H264EncoderDef ResolveContinuousEncoder()
    {
        // フレームレートを変えるには videorate が要る。同梱ランタイムには入れてあるが、
        // 利用者が別途入れた GStreamer には無いことがある。ParseLaunch の「no element」より先に、
        // 何が足りないのかを名指しで失敗させる。
        if (ContinuousBranch.RequiresVideorate(ContinuousFramerate)
            && !EncoderCatalog.ProbeWithGStreamer(ContinuousBranch.VideorateFactory))
        {
            throw new InvalidOperationException(
                $"the '{ContinuousBranch.VideorateFactory}' element is not available in this GStreamer runtime, "
                + $"so the continuous recording cannot run at ContinuousFramerate={ContinuousFramerate}. "
                + "Clear ContinuousFramerate to run it at the same rate as the event recording.");
        }

        if (!string.IsNullOrEmpty(ContinuousEncodingProperties))
        {
            string factory = ContinuousEncodingProperties.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
                ? first
                : ContinuousEncodingProperties;
            return new H264EncoderDef(
                factory, ContinuousEncodingProperties, EncoderCatalog.NeedsSystemMemoryFor(factory, Type));
        }

        // **枝のレートで GOP を決める。** 本線と同じフレーム数を使うと、
        // 5fps の枝ではキーフレームが 12 秒間隔になり、セグメントの分割は
        // キーフレームでしか行えないので 5 秒の設定が 10 秒へ伸びる（実測。
        // continuous.overshoot がこれを報じる）。
        var resolved = EncoderCatalog.Resolve(
            Type, PreferredH264Encoder, EncoderCatalog.ProbeWithGStreamer,
            EncoderCatalog.GopForFramerate(ContinuousEffectiveFramerate()));
        foreach (var attempt in EncoderCatalog.ExpandAttempts(resolved))
            return attempt;

        throw new InvalidOperationException(
            $"no usable H.264 encoder was found for the continuous recording (Type={Type}).");
    }

    /// <summary>常時枝の <c>appsink</c> を掴んでエンジンを起こす。</summary>
    private void StartContinuous(H264EncoderDef encoder)
    {
        _continuousSink = _sinkPipeline?.GetByName(ContinuousBranch.AppSinkName) as GstApp.AppSink
            ?? throw new InvalidOperationException(
                $"the continuous branch did not expose an appsink named '{ContinuousBranch.AppSinkName}'.");

        // 最初のサンプルを待つ予算は「実際に流れるフレームレート」から逆算する。
        // 上書きが無ければソース側 caps の framerate を使う（解析は SrcPipelineBuilder。
        // 規則を 2 か所に書かないため）。
        string framerate = ContinuousEffectiveFramerate();

        _continuous = new ContinuousRecorder(
            new ContinuousHost(this),
            _continuousSink,
            ContinuousFilenameTemplate,
            ContinuousSegmentSeconds,
            ContinuousFirstSampleBudget.For(framerate));
        _continuous.Start();

        ActualContinuousEncodingProperties = encoder.LaunchString;
        Components.ActivityLog.Info("recorder.continuous-init ok",
            $"recorder='{Name}' encoder='{encoder.LaunchString}' "
            + $"framerate='{ContinuousFramerate}' resolution='{ContinuousResolution}' "
            + $"segmentSeconds={ContinuousSegmentSeconds}");
    }

    /// <summary>
    /// 常時録画エンジンから <see cref="EventRecorder"/> の観測値へ書き戻す口。
    /// 入れ子クラスなので外側の private セッターへ触れる。
    /// </summary>
    private sealed class ContinuousHost(EventRecorder owner) : IContinuousRecorderHost
    {
        public string Name => owner.Name;

        public string ResolveSegmentPath(string template, int segmentIndex)
            => owner.ResolveContinuousPath(template, segmentIndex);

        public void OnContinuousStatus(bool running, string? currentFile, int segmentCount)
        {
            // 開始時刻だけ覚えておく。**sidecar はここでは書かない**
            // ── 前のセグメントの確定（moov の書き込み）は非同期で、この通知の時点では
            // まだ終わっていない。書けたかどうかは OnContinuousSegmentFinalized が報せる。
            if (currentFile is not null
                && !string.Equals(owner.ContinuousLastFilename, currentFile, StringComparison.Ordinal))
            {
                owner._continuousSegmentStarts[currentFile] = DateTimeOffset.UtcNow;

                // **新しいセグメントごとに 1 回だけ**サムネイルを要求する
                // （比較しているのは上書き前の旧値なので、同じ名前で 2 回積まない）。
                if (running)
                    owner._thumbnailRequests.Enqueue(currentFile);
            }

            // 停止の通知は最後のセグメントの確定を待ってから来る（ContinuousRecorder.Close）。
            // 残っているのは確定を諦めたセグメントなので、ここで捨てる。
            if (!running)
                owner._continuousSegmentStarts.Clear();

            owner.IsContinuousRecording = running;
            owner.ContinuousLastFilename = currentFile;
            owner.ContinuousSegmentCount = segmentCount;
        }

        public void OnContinuousSegmentFinalized(string path, bool ok)
        {
            // 開始時刻は「そのファイルを初めて見た時刻」。取り出しと同時に消す。
            // **覚えが無ければ sidecar は書かない** ── 今の時刻で埋めると尺 0 の
            // sidecar になり、ファイル名から開始時刻を採る索引のフォールバックより悪い。
            if (!owner._continuousSegmentStarts.TryRemove(path, out DateTimeOffset started))
                return;

            // **確定できた本だけに sidecar を置く。** sidecar が在ることは索引にとって
            // 「排出が終わっている＝開いて確かめなくてよい」の印なので、moov を書けなかった
            // ファイルに置くと録画中のものと区別が付かなくなる。
            if (ok)
                owner.WriteSidecarInBackground(path, started, DateTimeOffset.UtcNow, ContinuousTrigger);
        }

        public void OnContinuousError(string message) => owner.ContinuousLastError = message;
    }

    /// <summary>
    /// ライブプレビューの配信エンジンから見た宿主。<b>名前しか渡さない</b> ──
    /// 配信は観測値を一切書き換えない（録画の状態表示に影響しない）。
    /// </summary>
    private sealed class LivePreviewHost(EventRecorder owner) : ILivePreviewHost
    {
        public string Name => owner.Name;
    }

    /// <summary>
    /// DASH プレビューの配信エンジンから見た宿主。<b>名前と 4 つの設定しか渡さない</b> ──
    /// 配信は観測値を一切書き換えない（録画の状態表示に影響しない）。
    /// </summary>
    private sealed class DashPreviewHost(EventRecorder owner) : IDashPreviewHost
    {
        public string Name => owner.Name;

        public int PreviewWidth => owner.PreviewWidth;

        public int PreviewHeight => owner.PreviewHeight;

        public int PreviewFps => owner.PreviewFps;

        public int PreviewBitrateKbps => owner.PreviewBitrateKbps;
    }

    /// <summary>
    /// 常時録画のセグメント 1 本ぶんのパスを決める。<c>{Segment}</c> は 5 桁 0 詰めの連番として
    /// テンプレート変数に重ねる（<c>FilenameTemplate.Format</c> の書式指定は
    /// <see cref="IFormattable"/> にしか効かないため、桁揃えは呼び出し側で済ませる）。
    /// </summary>
    private string ResolveContinuousPath(string template, int segmentIndex)
    {
        var variables = new Dictionary<string, string>(TemplateVariables)
        {
            ["Segment"] = segmentIndex.ToString("00000", System.Globalization.CultureInfo.InvariantCulture),
        };

        string filename = GStreamer.FilenameTemplate.Format(
            template, Name, System.DateTime.Now, variables, Environment.GetEnvironmentVariable);

        return Path.IsPathRooted(filename) ? filename : Path.Combine(OutputDirectory, filename);
    }

    /// <summary>
    /// <b><c>SetState(Playing)</c> が返っただけでは「動いている」ことにならない。</b>
    /// パイプラインが実際に <c>PLAYING</c> へ到達するまで
    /// <see cref="PlayingStateTimeoutMs"/> を上限に待ち、到達しなければ候補の失敗として投げる。
    ///
    /// <para>
    /// <c>SetState</c> は非同期の状態遷移では <c>ASYNC</c> を返すが、
    /// それを <c>!= Failure</c> ＝成功として扱ってはいけない。
    /// このパイプラインは録画側 <c>appsink name=sink</c> がプリロールするまで
    /// <c>PLAYING</c> に到達しない ── <b>つまり「エンコーダーが最初の1フレームを出したか」を
    /// そのまま state で観測できる</b>。到達しないまま <c>IsInitialized=true</c> にすると、
    /// <b>録画もプレビューも一切動かないのに何のエラーも出ない</b>状態を利用者が
    /// 黙って踏み続ける（実機 4K で報告された停止がまさにこれで、
    /// 実機の <c>.dot</c> のパイプラインは <c>[=] -&gt; [&gt;]</c>＝
    /// <c>PAUSED</c> のまま <c>PLAYING</c> 待ちだった）。
    /// </para>
    /// <para>
    /// <b><c>NoPreroll</c> も成功として扱う。</b> ライブソースのパイプラインは
    /// プリロールを必要としないためこちらを返しうる。失敗と見なすのは
    /// <c>Async</c>（＝上限まで待っても遷移が終わらなかった）と <c>Failure</c> だけ。
    /// </para>
    /// </summary>
    private static void WaitUntilPlaying(Pipeline pipeline)
    {
        var ret = pipeline.GetState(
            out var state, out var pending, ClockTime.FromMilliseconds(PlayingStateTimeoutMs));

        if (ret is StateChangeReturn.Success or StateChangeReturn.NoPreroll)
            return;

        throw new InvalidOperationException(
            $"ERROR: the pipeline never reached PLAYING within {PlayingStateTimeoutMs}ms "
            + $"(get_state={ret}, state={state}, pending={pending}). "
            + "Linking and the state change succeeded, but no encoded frame ever came out "
            + "of the encoder, so nothing would have been recorded or previewed.");
    }

    /// <summary>
    /// 保持しているパイプライン・スレッド・バッファをすべて解放する。
    ///
    /// 破棄したフィールドは必ず null 化して**冪等**にしてある。
    /// <see cref="Initialize"/> は先頭で Close() を呼び、その後で各フィールドを再代入するため、
    /// 初期化が途中で失敗すると catch 内の Close() が「破棄済みのまま残ったフィールド」を
    /// 再度触ることになる（＝パイプライン編集ダイアログに不正な文字列を入れると到達する）。
    /// null 化していないと、そこでネイティブオブジェクトの二重解放になる。
    /// </summary>
    public void Close()
    {
        lock (_stateLock)
        {
            // 排出中のパイプラインを Dispose するとネイティブの二重解放になるため、
            // 進行中の停止を待ってから破棄へ進む。待ち切れなかった場合は
            // 破棄せず参照だけ落とす（WaitForPendingStop の注記を参照）。
            bool drained = WaitForPendingStop();
            CloseCore(abandonedStop: !drained);
        }
    }

    /// <summary>グラフ書き出しのために <c>_stateLock</c> を待つ上限(ms)。</summary>
    private const int DebugGraphLockTimeoutMs = 500;

    /// <summary>
    /// sink / src 両パイプラインのグラフを <c>.dot</c> として書き出し、書いた絶対パスを返す。
    ///
    /// <para>
    /// <b><c>_stateLock</c> の下で読む</b> ── 破棄と競合するとネイティブの二重解放になる。
    /// ただし<b>待ち続けない</b>。排出中の <see cref="Close"/> はロックを
    /// <see cref="StopFinalizeTimeoutMs"/> まで保持しうるので、UI スレッドから呼ぶと固まる。
    /// 取れなければ例外にして呼び出し側に報告させる ── <b>無言で件数を減らさない</b>。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> WriteDebugGraphs(string directory, System.DateTime timestamp)
    {
        if (!Monitor.TryEnter(_stateLock, DebugGraphLockTimeoutMs))
            throw new TimeoutException($"'{Name}' is busy (stopping); its graphs were not written.");

        try
        {
            List<string> written = [];
            if (_sinkPipeline is { } sink)
                written.Add(DebugLogEx.WriteDotFile(sink, directory, $"{Name}.sink", timestamp));
            if (_srcPipeline is { } src)
                written.Add(DebugLogEx.WriteDotFile(src, directory, $"{Name}.src", timestamp));
            return written;
        }
        finally
        {
            Monitor.Exit(_stateLock);
        }
    }

    /// <param name="abandonedStop">
    /// 進行中の排出を待ち切れなかった場合 true。src 側のオブジェクトは
    /// まだ排出タスクが使っている可能性があるので <b>Dispose せず参照だけ落とす</b>。
    /// </param>
    private void CloseCore(bool abandonedStop = false)
    {
        System.Diagnostics.Debug.Assert(Monitor.IsEntered(_stateLock), "CloseCore must run under _stateLock");

        // Stop() 失敗時などの再入を防ぐ（Close→Stop→(例外)→Close のような経路）
        if (_closing)
            return;
        _closing = true;
        try
        {
            IsInitialized = false;
            ActualType = EventRecordingType.System;
            ActualSrcPipeline = null;
            ActualEncodingProperties = null;
            ActualContinuousEncodingProperties = null;
            ContinuousLastError = null;

            // **押し込みの継続条件を先に倒す。** サンプルのコールバック（sink / preview）の
            // 早期 return はこれ 1 本で、quiesce より前に倒すことで
            // 「これから来るサンプルは触らない」を先に立てる
            // （常時録画のコールバックは自前の `_isAlive` を Close が倒す）。
            _isAlive = false;

            // **quiesce: sink パイプラインを Null へ落として、コールバックを退去させる。**
            // Null への遷移は appsink をフラッシュしてストリーミングスレッドを止める
            // ── GStreamer は実行中の onNewSample が返るまで待つので、これが返れば
            // 「もう誰もサンプルを押し込んでいない・リングを触っていない」が保証される。
            // **常時録画のコールバックの退去を保証するのもこれ 1 本**（3 本とも
            // この sink パイプラインの appsink に付いている）── ContinuousRecorder.Close
            // が最後のセグメントを安全に確定できるのは、これが成功した場合である
            // （失敗した場合の備えは ContinuousRecorder.Close の doc）。
            //
            // **SetState(Null) は必ず呼ぶ。** 呼ばなければキャプチャとエンコードが
            // 誰にも管理されないまま走り続ける。有界にするのは**待ちだけ**で、
            // 呼び出しそのものはプールスレッドでそのまま続行させる。
            // **ラムダにはフィールドではなくローカルを捕まえさせる** ── 待ちを諦めた側は
            // このあと _sinkPipeline を null 化するので、フィールドを読ませると
            // 「タスクがまだ走り出していなければ SetState が実行されない」になる。
            System.Diagnostics.Debug.Assert(!Monitor.IsEntered(_busLock), "SetState must not run under _busLock");
            var quiescing = _sinkPipeline;
            bool quiesced = quiescing is null
                || System.Threading.Tasks.Task.Run(() => quiescing.SetState(State.Null))
                    .Wait(SinkQuiesceTimeoutMs);

            // 撮れずに残った要求は捨てる。取り出す相手（プレビュー枝）はいま退去したので、
            // ここから先に来るフレームは無い。
            _thumbnailRequests.Clear();

            // **ライブプレビューは quiesce の直後・同期で畳む。** 触っていたのは
            // 枝2 のコールバック（＝いま退去させた相手）なので、ここへ来た時点で
            // 誰も mux を触っていない。畳むのに待つのは自前の mux パイプラインの
            // ストリーミングスレッドだけで、sink 側の解放判断には効かない。
            var live = _live;
            Volatile.Write(ref _live, null);
            live?.Close();

            // **DASH の配信エンジンも同じ段で畳む。** 触っていたのは枝A のコールバック
            // （＝いま退去させた相手）なので、ここへ来た時点で誰も第 2 パイプラインを
            // 触っていない。順序を live より前にしても後にしても安全だが、
            // 「枝を静止させてから配信を閉じる」という 1 つの規律で読めるように並べてある。
            var dash = _dash;
            Volatile.Write(ref _dash, null);
            dash?.Close();

            // **常時録画の確定はイベント側の排出と並行に走らせる。**
            // 直列にすると停止の予算（MaxAdvisedStopFinalizeTimeoutMs + StopFinalizeSlackMs
            //  < ランチャーの結果待ち）が崩れ、stop-recording がタイムアウトを返し始める。
            // **例外を持ち帰らせない。** ここで投げると Wait が AggregateException を出し、
            // 常時録画の後始末の失敗がレコーダーの破棄そのものの失敗に化ける（隔離契約が崩れる）。
            var continuous = _continuous;
            _continuous = null;
            System.Threading.Tasks.Task? continuousClose = continuous is null
                ? null
                : System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        continuous.Close();
                    }
                    catch (Exception ex)
                    {
                        Components.ActivityLog.Error("continuous.error",
                            $"recorder='{Name}' the continuous recording did not shut down cleanly: {ex.Message}");
                    }
                });

            if (_IsRecording)
            {
                // ここは同期のまま。終了経路（Ctrl+閉じる）はこの直後に
                // パイプラインを Dispose するので、プールへ逃がすと排出中の
                // オブジェクトを壊す。「録画中に Ctrl+閉じる → recording.stop result=ok・
                // 有効な MP4」という手動検証が見ているのはこの経路。
                long elapsedMs = _recordingStartedAt == 0 ? 0 : Environment.TickCount64 - _recordingStartedAt;
                IsRecording = _IsRecording = false;
                StopDrainAndFinalize(elapsedMs);
            }

            // 保留中の自動復帰を先に止める。放置すると最大60秒後に、破棄済みの
            // パイプラインへ SetState したり Initialize() を呼んだりする。
            CancelPendingRestart();

            // 常時録画の後始末（最後のセグメントの確定と、排出中のセグメントの回収）を待つ。
            // **これは sink 側を Dispose してよいかの判断には効かない** ── あちらが触るのは
            // 自前の書き出しパイプラインだけで、cont appsink を読んでいるコールバックは
            // 上の quiesce が退去させている。予算を超えたら、まだ排出中のセグメントを
            // 置いたまま先へ進む（その報告は recorder.leak と continuous.finalize backlog）。
            bool continuousStopped = continuousClose is null
                || continuousClose.Wait(StopFinalizeTimeoutMs + StopFinalizeSlackMs);

            // 抑制されたまま残っている sink 側の件数を取りこぼさない
            // （src 側は StopDrainAndFinalize の finally が畳む）。
            FlushThrottles(_sinkThrottles, "sink");

            // **バスの購読はここで外す。** sink はもう Null なので、遷移中に出た Error は
            // 既に購読中のハンドラが受けており、ScheduleRestart が積まれていることがある
            // ── だから直後にもう一度 CancelPendingRestart を掛ける。
            // **_busLock を保持せずに外すこと** ── 解除そのものはバスのロックの下で行われるので
            // 実行中のハンドラと競合しないが、解除が返った時点で走り出していたハンドラは
            // まだ走っている（そのハンドラは _busLock を取る）。
            _srcBusSubscription?.Dispose();
            _srcBusSubscription = null;
            _sinkBusSubscription?.Dispose();
            _sinkBusSubscription = null;

            // 購読を外すまでの間にハンドラが新しい復帰を積んでいることがある
            // ── 上の CancelPendingRestart はそれを知らない。ここでもう一度畳む。
            // 畳み残すと、直後の再初期化（Initialize は必ず Close を先に呼ぶ）で
            // _isAlive が立ち直った後に、旧セッション由来の復帰が走り出す。
            CancelPendingRestart();

            // リングバッファは**必ず空にする**（このインスタンスは次のセッションへ引き継がれる）。
            // 残すと前セッションの映像が次の事前バッファの先頭に混ざり、保持バイト数も
            // 持ち越されてサイズ基準の退避予算が狂う。
            // 解放するかどうかだけが quiesce しだい ── リングを触るのはサンプルの
            // コールバックだけなので、退去を確認できていなければ参照を落とすに留める
            // （パイプラインと同じ「リークするが落ちはしない」規律）。
            foreach (var b in _ringBuffer.DrainAll())
            {
                if (quiesced)
                    b.Dispose();
            }

            // **諦めた理由は 1 行にまとめて 1 回だけ書く。** 触っている者ごとに
            // 壊してはいけない対象が違う（下の 2 つの分岐）ので、判定は別々に行うが、
            // 記録が複数行に割れると「1 回の Close で何を諦めたか」が読めなくなる。
            string? leaked =
                abandonedStop ? "the src pipeline was still draining; leaked instead of disposed"
                : !quiesced ? "the sink pipeline did not reach NULL in time; leaked the pipelines instead of disposing them"
                : !continuousStopped ? "the continuous recording did not finish closing in time; left its segment writer(s) draining"
                : null;
            if (leaked is not null)
                Components.ActivityLog.Warn("recorder.leak", $"recorder='{Name}' {leaked}");

            // src パイプライン側
            //
            // コールバックは _appSrc へ押し込むので、quiesce できていなければ
            // src 側もまだ触られている可能性がある（preview / 常時録画は src を触らない）。
            if (abandonedStop || !quiesced)
            {
                // 排出タスク（abandonedStop）またはサンプルのコールバックがまだ
                // _srcPipeline / _appSrc / _srcBus を触っている可能性がある。
                // ここでパイプラインを Dispose すると使用中のネイティブオブジェクトを壊す
                // （＝クラッシュ）。Dispose せず参照だけ落とす ── リークするが落ちはしない
                // （排出の abandonedStop と同じ規律を quiesce の失敗にも適用する）。
                // 要素・バスはインターンされた GObject ラッパーで、どの経路でも Dispose しない。
                _file = null;
                _mux = null;
                _appSrc = null;
                _srcBus = null;
                _srcPipeline = null;
                _errorSinkSrc = null;
            }
            else
            {
                System.Diagnostics.Debug.Assert(!Monitor.IsEntered(_busLock), "SetState must not run under _busLock");
                _srcPipeline?.SetState(State.Null);
                // 要素・バスはインターンされた GObject ラッパーなので Dispose しない
                // （プロセス共通の参照を持ち、解放はランタイム側の責務）。参照だけ落とす。
                // Dispose するのは、このコードが作って「もう使わない」と決めた
                // パイプラインだけ（Null へ落とした後の公認の破棄）。
                _file = null;
                _mux = null;
                _appSrc = null;
                _srcBus = null;
                _srcPipeline?.Dispose();
                _srcPipeline = null;
                _errorSinkSrc = null;
            }

            // sink パイプライン側（SetState(Null) は手順の先頭の quiesce で済んでいる）
            //
            // **判断は quiesce だけで決まる。** sink パイプラインの appsink 3 本
            // （録画・プレビュー・常時録画）に触れるのはコールバックだけで、
            // その退去を保証するのは quiesce である。常時録画の Close がまだ走っていても、
            // あちらが触るのは自前の書き出しパイプラインなので、ここの判断には関わらない。
            if (!quiesced)
            {
                // 実行中のコールバックが sink 側ネイティブをまだ触っている可能性がある。
                // パイプラインを Dispose せず参照だけ落とす
                // （recorder.leak は上で記録済み）。**コールバックも外さない** ──
                // 解除は実行中のコールバックと競合しうる（バスの購読と同じ規律）。
                _previewSink = null;
                _appSink = null;
                _continuousSink = null;
                _sinkBus = null;
                _sinkPipeline = null;
            }
            else
            {
                // **コールバックは明示的に外す。** GCHandle を解放するのは
                // 「sink がコールバックの組を手放したとき」だけで、ClearSimpleCallbacks が
                // その決定的な解放点である（放置するとクロージャが捕まえた
                // RecordSampleState ごと、パイプラインの寿命を超えて残る）。
                // 外したあとの sink はシグナル経路へ戻る。
                _appSink?.ClearSimpleCallbacks();
                _previewSink?.ClearSimpleCallbacks();
                _continuousSink?.ClearSimpleCallbacks();

                // 要素（appsink 群）とバスはインターンされた GObject ラッパーなので
                // Dispose しない。Dispose するのはこのコードが作ったパイプラインだけ。
                _previewSink = null;
                _appSink = null;
                _continuousSink = null;
                _sinkBus = null;
                _sinkPipeline?.Dispose();
                _sinkPipeline = null;
            }

            // GObject ラッパーのファイナライザが積んだ解放要求を消化する。
            // このアプリは GMainLoop を回さないので、誰かが呼ばなければ溜まったままになる。
            // 定常状態ではラッパーを取得するたび（サンプルのプル）に排水されるので、
            // 明示的に呼ぶ必要があるのはここ ── 最後の Close のあとは
            // ラッパーを取得する者がいなくなる。
            global::GstSharp.DrainPendingReleases();
        }
        finally
        {
            _closing = false;
        }
    }

    /// <summary>Close() の再入防止フラグ。</summary>
    private bool _closing;

    /// <summary>
    /// <see cref="CloseCore"/> の quiesce（sink パイプラインの <c>Null</c> 化）を待つ上限(ms)。
    ///
    /// <para>
    /// <b>上限が掛かるのは待ちだけで、<c>SetState(Null)</c> の呼び出しそのものは
    /// 諦めない。</b> 呼ばなければキャプチャとエンコードが誰にも管理されないまま走り続ける
    /// ので、待ち切れなかった場合はプールスレッドに続行させ、こちらは
    /// <c>recorder.leak</c> を残して「Dispose もコールバックの解除もしない」へ倒す。
    /// </para>
    /// <para>
    /// 値は常時録画側の退去待ち（<c>ContinuousRecorder.CallbackExitTimeoutMs</c>）と
    /// 同じ 5 秒。健全なパイプラインの
    /// <c>Null</c> 化はミリ秒で終わり、掛かるのは要素が固まっている場合だけである。
    /// </para>
    /// </summary>
    private const int SinkQuiesceTimeoutMs = 5000;

    /// <summary>
    /// sink と preview の <c>appsink</c> コールバックの継続条件。
    /// <b>volatile は必須</b> ── <see cref="CloseCore"/> はこれを false にしてから
    /// quiesce へ進む。可視性が保証されないと、コールバックが teardown 中も
    /// 押し込みを続け、quiesce の待ちがそのぶん伸びる。
    /// </summary>
    private volatile bool _isAlive = true;

    // 退避の不変条件（時間・サイズの2本立て、直近の1件は必ず残す）は
    // RecordingRingBuffer にあり、L1（RecordingRingBufferTests）が守っている。
    private readonly RecordingRingBuffer<Gst.Buffer> _ringBuffer = new();

    /// <summary>
    /// リングバッファのサイズ上限（512MiB）。時間基準(<see cref="BufferDuration"/>)の退避だけに頼らないための二次防御。
    ///
    /// 退避条件の符号なし減算は、ソースの再起動などで PTS が巻き戻るとアンダーフローして
    /// 意図しない結果になりうる。またビットレートが極端に高い場合は、規定時間内であっても
    /// メモリを圧迫する。時間・サイズの両方で上限を掛ける（<see cref="RecordingRingBuffer{T}.Evict"/>）。
    /// </summary>
    private const ulong MaxRingBufferBytes = 512UL * 1024 * 1024;


    /// <summary>
    /// sink の <c>appsink</c> コールバックがサンプルを跨いで持つ状態。
    ///
    /// <para>
    /// <b>フィールドにせず <see cref="InitializeWith"/> ごとに <c>new</c> して
    /// クロージャへ捕まえさせる。</b> <see cref="EventRecorder"/> のインスタンスは
    /// 再初期化を跨いで生き続けるので、フィールドに置くと前セッションの
    /// 「caps 設定済み」「I フレーム検出済み」「開始 PTS」がそのまま持ち越され、
    /// 作り直したパイプラインの最初の録画が古い基準で押し込まれる
    /// ── 持ち越さないことを規律ではなく構造で保証する。
    /// </para>
    /// <para>
    /// <b>同期は要らない。</b> 触るのはこの <c>appsink</c> のストリーミングスレッド 1 本だけで
    /// （上流は <c>h264parse</c> 1 本）、そのスレッドが退去したことは
    /// <see cref="CloseCore"/> の quiesce が保証する。
    /// </para>
    /// </summary>
    /// <param name="session">
    /// 生成時点の <see cref="_recordingSession"/>。<b>0 で始めてはいけない</b> ──
    /// 再初期化したときに「新しい録画が始まった」と誤検出し、録画していないのに
    /// リングバッファ全体を押し込む。
    /// </param>
    private sealed class RecordSampleState(int session)
    {
        /// <summary>
        /// src パイプライン（<c>appsrc ! h264parse ! mp4mux ! filesink</c>）の appsrc に
        /// キャップスを設定したか。未設定だと h264parse は H.264 エレメンタリストリームの
        /// 框組み（stream-format / alignment）を typefind で推測するしかない。
        /// 推測が外れると全 NAL が "broken/invalid nal ... will be dropped" として捨てられ、
        /// <b>エラーにはならないまま</b>中身の無い MP4 が出来上がる（実機の nvh264enc で観測）。
        /// sink 側で実際にネゴシエートされたキャップスを渡して推測をやめさせる。
        /// </summary>
        public bool AppSrcCapsSet { get; set; }

        /// <summary>いま書いている録画ファイルの起点 PTS（最初の I フレームで決まる）。</summary>
        public ulong StartPts { get; set; }

        /// <summary>この録画で最初の I フレームを見つけたか。</summary>
        public bool IsIframeFound { get; set; }

        /// <summary>最後に見た録画セッションの世代（<see cref="_recordingSession"/>）。</summary>
        public int LastSeenSession { get; set; } = session;
    }

    /// <summary>
    /// sink の <c>appsink</c> から取り出したサンプル 1 枚を処理する
    /// （リングへ複製を積み、録画中なら appsrc へ押し込む）。
    ///
    /// <para>
    /// <b>ストリーミングスレッドで走る。</b> ここから <c>_stateLock</c> を取っては
    /// ならず、<c>SetState</c> も <c>Join</c> / <c>Wait</c> も呼んではならない ──
    /// <see cref="CloseCore"/> は <c>_stateLock</c> を保持したまま sink パイプラインを
    /// <c>Null</c> へ落とし、<b>実行中のこのメソッドが返るのを待つ</b>（quiesce）ので、
    /// ここで待つ側に回ると即デッドロックする。
    /// </para>
    /// <para>
    /// 触ってよいのは volatile フィールド（<see cref="_isAlive"/> /
    /// <see cref="_IsRecording"/> / <see cref="_recordingSession"/>）と、
    /// ネイティブのパイプライン・リングバッファ・<c>ActivityLog</c> だけ。
    /// </para>
    /// </summary>
    private void ProcessRecordSample(Sample sample, RecordSampleState state)
    {
        // **sink 側が生きている証拠はここでだけ取れる。** 復帰の連鎖が
        // 「要素は Playing に戻ったが実は来ていない」を見分けるのに読む
        // （WaitForSinkSamplesAsync）。録画中に限らず数えること ── 常時稼働の
        // sink パイプラインの生死を見るための数であって、録画の計測ではない。
        System.Threading.Interlocked.Increment(ref _sinkSamplesSeen);
        System.Threading.Volatile.Write(ref _lastSinkSampleAt, Environment.TickCount64);

        // 最初のサンプルで appsrc のキャップスを確定させる。
        // ここで設定するのは「バッファが1つも流れる前」に src パイプラインを
        // 構成しておくため（録画開始時はリングバッファの古いバッファから押し込むが、
        // H.264 エレメンタリストリームで解像度も不変なのでキャップスは同一）。
        // 途中で SetCaps すると下流の再ネゴシエーションが起きるので一度だけ。
        if (!state.AppSrcCapsSet)
        {
            // sample.GetCaps() のラッパーは同じネイティブ caps への参照を自前で
            // 1本持つ。Dispose が返すのはその1本だけで、sample 側の caps は残る。
            using var negotiated = sample.GetCaps();
            if (negotiated is not null)
            {
                // SetCaps は caps を複製する。渡したラッパーの所有はこちらのまま。
                _appSrc?.SetCaps(negotiated);
                state.AppSrcCapsSet = true;
                // GetStructure(0) は所有するコピー（Boxed）を返すので解放する。
                // ログには従来どおり構造体名だけを出す
                // （実際のキャップス全体は GST_DEBUG のネゴシエーションログに出る）。
                using var structure = negotiated.GetStructure(0);

                // sidecar に載せるぶんを控える。ここが caps の確定する唯一の場所で、
                // 以後は再ネゴシエーションしない（解像度もフレームレートも変わらない）。
                if (structure.GetInt("width", out int capsWidth)
                    && structure.GetInt("height", out int capsHeight))
                {
                    _capsWidth = capsWidth;
                    _capsHeight = capsHeight;
                }
                if (structure.GetFraction("framerate", out int capsNumerator, out int capsDenominator))
                {
                    _capsFramerateNumerator = capsNumerator;
                    _capsFramerateDenominator = capsDenominator;
                }

                Log(DebugLevel.Info,
                    $"appsrc caps set from the negotiated sink caps ({structure.GetName()})");
            }
        }

        using var buffer = sample.GetBuffer();
        if (buffer is null)
            return;

        // リングのキーは ulong(ns) のまま（押し込み時に ClockTime へ組み直す）。
        var pts = buffer.Pts.IsNone
            ? (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000)
            : buffer.Pts.Nanoseconds;

        // gst_buffer_copy 相当（GST_BUFFER_COPY_ALL・全域）。null は複製の失敗。
        var copy = buffer.CopyRegion(BufferCopy.All, 0, nuint.MaxValue);
        if (copy is null)
        {
            Log(DebugLevel.Error, "gst_buffer_copy_region failed; dropping this sample");
            return;
        }

        // 録画中もリングへ溜め続ける（所有権はリングへ移り、解放は退避か
        // CloseCore の DrainAll が行う）── 停止→即開始の次セッションが
        // 「直前の録画中」も含めた事前バッファ窓をそのまま再押し込みできる。
        // PushRecordBuffer はリングの項目を消費せず複製を送るので、
        // リングに残したままで安全。
        _ringBuffer.Enqueue(pts, copy, copy.GetSize());

        // 退避条件（時間・サイズの2本立て）と「直近の1件は必ず残す」ガードは
        // RecordingRingBuffer.Evict にあり、L1 が守っている。
        // 退避したバッファの解放（ネイティブ）はここで行う。
        ulong bufferDurationNs = (ulong)Math.Max(0, BufferDuration) * 1_000_000UL;
        foreach (var evicted in _ringBuffer.Evict(pts, bufferDurationNs, MaxRingBufferBytes))
            evicted.Dispose();

        // ライブプレビューへ回す。**リングの整理より後**に置くのは、mux の起動時に
        // 流し込むリングの中身を「いま押し込む窓」と一致させるため。
        // 購読者が 0 人ならこの呼び出しは volatile の 1 回読みで返る。
        _live?.OnEncodedSample(sample, buffer, pts, _ringBuffer);

        if (_IsRecording)
        {
            // **「サンプルが見えた」はここで数える。** この行に到達している＝
            // appsink が実を返している＝エンコーダーが動いている。
            // 逆に、ソースが EOS で終わっていると**コールバックそのものが呼ばれない**ので、
            // **この下の事前バッファの排出も一度も走らない**
            // ── 押し込みが 0 のまま録画が「成功」する経路（「587 バイトの空 MP4」の症状）。
            System.Threading.Interlocked.Increment(ref _samplesSeenWhileRecording);

            int session = _recordingSession;
            if (session != state.LastSeenSession)
            {
                // 新しい録画セッションの最初のサンプル。前セッションの I フレーム検出と
                // StartPts を持ち越さず、リングバッファ全体（今 Enqueue した copy を
                // 含む）を押し込む。フラグの false→true 遷移ではなく世代で検出する
                // 理由は _recordingSession の doc を参照。
                state.LastSeenSession = session;
                state.IsIframeFound = false;
                foreach (var (bufferPts, b) in _ringBuffer)
                    PushRecordBuffer(bufferPts, b, state);
            }
            else
                PushRecordBuffer(pts, copy, state);
        }
        else
            state.IsIframeFound = false;
    }

    /// <summary>
    /// バッファ 1 本を src パイプラインの <c>appsrc</c> へ押し込む。
    ///
    /// <para>
    /// <b>所有権: <paramref name="buffer"/> は消費しない（リング所有のまま借りる）。</b>
    /// 押し込むのはここで作る複製 ── 旧実装の <c>MakeWritable</c> も、リングが参照を持つ
    /// 共有バッファでは必ず複製を作ってから押し込んでいた（コストは同一）。
    /// リングの項目を消費しないことで、停止→即開始の次セッションが同じ事前バッファ窓を
    /// そのまま再押し込みできる。
    /// </para>
    /// </summary>
    private void PushRecordBuffer(ulong bufferPts, Gst.Buffer buffer, RecordSampleState state)
    {
        if (!state.IsIframeFound)
        {
            if (buffer.HasFlags(BufferFlags.DeltaUnit))
                return;
            else
            {
                state.StartPts = bufferPts;
                state.IsIframeFound = true;
            }
        }
        // PTS の巻き戻り（ソースの再起動）。符号なし減算のまま押し込むと約 2^64 ns 級の
        // PTS が mux へ渡り、当該録画のタイムスタンプが壊れる ── 退避側
        // （RecordingRingBuffer）と同じ「巻き戻りは起こる」前提を押し込み側にも適用し、
        // 次の I フレームから始点を取り直す。
        if (bufferPts < state.StartPts)
        {
            // 黙って捨てない（押し込みの拒否と同じ扱い）。同一内容が続くので畳む。
            var (emitRewind, rewindRepeated) = _srcThrottles.Warning.Observe("pts-rewind");
            if (emitRewind)
            {
                string repeated = 0 < rewindRepeated ? $" repeated={rewindRepeated}" : "";
                Components.ActivityLog.Warn("recorder.warning",
                    $"recorder='{Name}' bus=src element='(pts)' the source timestamp went backwards; "
                    + $"waiting for the next I-frame{repeated}");
            }
            state.IsIframeFound = false;
            return;
        }
        // gst_buffer_copy 相当（メタデータ複製・メモリは参照共有）。複製は参照1本の
        // 単独所有なので、MakeWritable なしで PTS/DTS を直接書ける。
        // PushBuffer はネイティブの所有権ごと引き取りラッパーも自ら Dispose する ──
        // using は押し込めなかった経路（appsrc 不在）の後始末で、PushBuffer 後の
        // 二重 Dispose は冪等なので無害。
        using var buf = buffer.CopyRegion(BufferCopy.All, 0, nuint.MaxValue);
        if (buf is null)
        {
            Log(DebugLevel.Warning, "gst_buffer_copy_region failed; dropping one record buffer");
            return;
        }
        buf.SetPts(ClockTime.FromNanoseconds(bufferPts - state.StartPts));
        buf.SetDts(ClockTime.None);
        // **数えるのは appsrc が受理した押し込みだけ。** PushBuffer は EOS 後は Eos、
        // 未始動なら Flushing を返してバッファを受け取らない。拒否も数えると
        // 「pushed が 0 でないのに MP4 は空」が成立し、停止時の空検出
        // （pushed==0 → result=empty → 終了コード 16）を素通りする。
        var flow = _appSrc?.PushBuffer(buf);
        if (flow == FlowReturn.Ok)
            System.Threading.Interlocked.Increment(ref _samplesPushed);
        else
            Log(DebugLevel.Warning, $"appsrc rejected a buffer: {flow?.ToString() ?? "(no appsrc)"}");
    }

    /// <summary>連続して抑制されていた件数を最後に1行だけ吐き出す。</summary>
    private void FlushThrottles(BusThrottles throttles, string busName)
    {
        int errors = throttles.Error.Flush();
        if (0 < errors)
            Components.ActivityLog.Error("recorder.error",
                $"recorder='{Name}' bus={busName} repeated={errors} (suppressed, final)");

        int warnings = throttles.Warning.Flush();
        if (0 < warnings)
            Components.ActivityLog.Warn("recorder.warning",
                $"recorder='{Name}' bus={busName} repeated={warnings} (suppressed, final)");
    }

    // Error と Warning で別インスタンスにする。1つを共有すると、Warning の連続の直後に
    // 出た Error が「Warning の抑制件数」を repeated=N として引き継いでしまい、
    // 診断のために作った表示が診断を誤らせる。
    private readonly BusThrottles _sinkThrottles = new();
    private readonly BusThrottles _srcThrottles = new();

    /// <summary>1つのバスに対する、種別ごとの抑制状態。</summary>
    private sealed class BusThrottles
    {
        public BusMessageThrottle Error { get; } = new();
        public BusMessageThrottle Warning { get; } = new();
    }

    /// <summary>
    /// 1 本のバスを <c>Bus.SubscribeSyncDrop</c> で購読し、取り付け直前までに
    /// 溜まっていた分を汲み切る。返り値は解除に使う購読（<c>Dispose</c> で外す）。
    ///
    /// <para>
    /// <b>このアプリでは <c>Bus.Message</c> / <c>AddWatch</c> は使えない</b>
    /// ── どちらも GMainLoop から配送されるが、GMainLoop を回していないので発火しない。
    /// メインループ無しで push 型に受けられるのは、バスの同期ハンドラだけである。
    /// </para>
    /// <para>
    /// <b>購読より後に post されたメッセージはキューに入らない。</b> 配送してから捨てる
    /// までをバインディングが行う（<c>Drop</c> の所有権の始末込み）ので、キューは伸びない。
    /// 汲み切りが要るのは購読前のバックログだけで、それを <see cref="_busLock"/> の下で
    /// 購読と続けて行う。
    /// </para>
    /// <para>
    /// <b>ハンドラの中で捕まえる。</b> 例外を漏らしてもメッセージは <c>Drop</c> され
    /// （所有権の後始末込み）キューは伸びないが、漏らした 1 件は分類も記録も
    /// <see cref="_stopDrain"/> の完了もされずに終わる ── 1 件の失敗で以後の
    /// メッセージまで落とさないために、ここで握ってログし次の 1 件へ進む。
    /// </para>
    /// </summary>
    private IDisposable SubscribeBus(Bus bus, string busName, BusThrottles throttles)
    {
        lock (_busLock)
        {
            IDisposable subscription = bus.SubscribeSyncDrop((_, message) =>
            {
                try
                {
                    // メッセージのラッパーはハンドラの実行中だけ有効（バインディングが破棄する）。
                    // Dispose してはいけない。
                    lock (_busLock)
                        HandleBusMessage(message, busName, throttles);
                }
                catch (Exception ex)
                {
                    Log(DebugLevel.Error, $"GstEventRecorder bus handler failed! bus={busName}\n{ex}");
                }
            });

            try
            {
                // Pop の戻りは所有権つきなので必ず解放する。
                while (bus.Pop() is { } backlog)
                {
                    using (backlog)
                        HandleBusMessage(backlog, busName, throttles);
                }
            }
            catch (Exception ex)
            {
                // バックログの処理の失敗で初期化そのものを落とさない
                // （購読は成立しているので、以後のメッセージは受けられる）。
                Log(DebugLevel.Error, $"GstEventRecorder bus backlog failed! bus={busName}\n{ex}");
            }

            return subscription;
        }
    }

    /// <summary>
    /// バスメッセージ1件を分類して記録し、必要なら復帰・停止へつなぐ。
    /// <b>呼び出しは必ず <see cref="_busLock"/> の下で行う</b>（<see cref="_stopDrain"/> を読むため）。
    ///
    /// <para>
    /// <b>ここから <c>SetState</c> を呼んではいけない。</b> post 元＝当の要素の
    /// ストリーミングスレッドで走るので、状態遷移はプールスレッドへ逃がすこと。
    /// </para>
    /// </summary>
    private void HandleBusMessage(Message msg, string busName, BusThrottles throttles)
    {
        // メッセージの発信元。インターンされた GObject ラッパーで、ラッパー自身が
        // 参照を持つのでメッセージの解放後も有効。Dispose してはいけない。
        // フィールドに保持してメッセージを跨いで使ってもよい（_errorSinkSrc がそれ）。
        var srcObject = msg.Src;
        string elementName = srcObject?.Name ?? "?";

        switch (msg.Type)
        {
            case MessageType.Error:
                {
                    // ParseError はネイティブ側の後始末込みで (GException, debug) を返す。
                    // GException はただの例外オブジェクトなので解放は不要。
                    var (error, debug) = msg.ParseError();
                    string message = error.Message;

                    // **停止の排出待ちが武装中の src Error は、ここでは報告しない。**
                    // 報告するのは停止側（recording.stop error）1 箇所だけ ──
                    // 両方から出すと同じ障害が recorder.error / recording.aborted と
                    // recording.stop error の二重になる。
                    if (busName == "src" && _stopDrain is { } draining)
                    {
                        draining.TrySetResult(new(StopDrainSignal.Error, message, debug));
                        break;
                    }

                    string detail = $"recorder='{Name}' bus={busName} element='{elementName}' {message} debug={debug}";

                    // Error も洪水になる ── 「Error は1件ごとに意味があるので抑制しない」は
                    // 成り立たない。実測では、ソース復帰後に x264enc が毎フレーム
                    // "Encode x264 frame failed" を出し、60秒で 41 行に達した。
                    // LastError は毎回更新し（UI は最新の状態を出すべき）、
                    // ログ行だけを畳む。
                    var (emitError, errorRepeated) = throttles.Error.Observe($"{elementName} {message}");
                    LastError = detail;
                    if (emitError)
                    {
                        string prefix = 0 < errorRepeated ? $"repeated={errorRepeated} " : "";
                        Components.ActivityLog.Error("recorder.error", prefix + detail);
                    }
                    try { ErrorOccurred?.Invoke(this, detail); }
                    catch (Exception ex) { Log(DebugLevel.Error, $"ErrorOccurred handler threw\n{ex}"); }

                    if (busName == "src")
                    {
                        // 録画中の src 側エラー（ディスク満杯・書込権限なし）。
                        // **録画を止める** ── 壊れたファイルを書き続けて「録れているつもり」に
                        // させない。
                        Components.ActivityLog.Error("recording.aborted",
                            $"recorder='{Name}' file='{LastFilename}' stopping because the source pipeline reported an error");
                        RequestAbortRecording();
                    }
                    else
                    {
                        // sink 側（常時稼働）は復帰を試みる。
                        //
                        // 障害要素がソース（BaseSrc）なら、その要素だけ Ready→Playing で
                        // 戻せることが多い（デバイスの一時的な消失）。
                        // **ソース以外でも必ず予約する** ── BaseSrc のときだけ予約すると、
                        // エンコーダーが壊れた場合に何も起きず毎フレームのエラーが出続ける
                        // （実測: ソース復帰後に x264enc が 60 秒で 41 件）。
                        // 要素単位で戻せない障害は、エスカレーションでパイプラインごと作り直す。
                        if (srcObject is GstBase.BaseSrc erroredSource)
                        {
                            // **控えるだけで、ここでは状態を触らない。** 戻すのは復帰試行
                            // （<see cref="RestartSinkSrc"/>）の側で、あちらはプールスレッド。
                            //
                            // basesrc は flow error のあと、この post と**同じ**
                            // ストリーミングスレッドから EOS を押す（Error の post →
                            // EOS の push の順）。ここで Ready へ落とすと pad が
                            // deactivate＝flushing になり、後から来る **EOS が捨てられて
                            // sink バスに届かない** ── <see cref="_sinkSawEos"/> が立たない
                            // まま要素単位の再開が result=ok を返し、作り直しへ進まなくなる。
                            // 別スレッドへ逃がしても同じで、遷移が EOS の push より先に
                            // 走るかどうかが機械の速さ次第になる（2 vCPU の CI で決定的に先行した）。
                            _errorSinkSrc = erroredSource;
                        }
                        ScheduleRestart(elementName);
                    }
                    break;
                }

            case MessageType.Warning:
                {
                    var (error, debug) = msg.ParseWarning();
                    string message = error.Message;

                    // Warning は洪水になる。同一内容の連続は畳む（BusMessageThrottle 参照）。
                    var (emit, repeatedBefore) = throttles.Warning.Observe($"{elementName} {message}");
                    if (!emit)
                        break;

                    string repeated = 0 < repeatedBefore ? $" repeated={repeatedBefore}" : "";
                    Components.ActivityLog.Warn("recorder.warning",
                        $"recorder='{Name}' bus={busName} element='{elementName}' {message}{repeated} debug={debug}");

                    // Warning では録画を止めない。**止められない**というのが実データの結論で、
                    // nvh264enc の全 NAL 破棄は Warning だけで出続けながら「正常な録画」と
                    // 区別できるのは結果の MP4 を見た後だけだった。ここでの責務は
                    // 「後から原因を辿れるようにする」ことに限る。
                    // ただし LastError には残す（UI で健全性が見えるようにするため）。
                    LastError = $"[warning] {elementName}: {message}";
                    break;
                }

            case MessageType.Eos:
                // src 側の EOS を待つのは停止の排出だけ。武装していなければ黙って捨てる
                // （recorder.eos は sink バス専用の印であって、停止のたびに出るものではない）。
                if (busName == "src")
                {
                    _stopDrain?.TrySetResult(new(StopDrainSignal.Eos, null, null));
                    break;
                }

                // sink 側の EOS は「このパイプラインはもう戻せない」という印
                // （<see cref="_sinkSawEos"/>）。次の復帰は要素単位を飛ばして作り直す。
                // **印を立てるのが先** ── 下で張る連鎖が mustRebuild の判断に読む。
                _sinkSawEos = true;
                Components.ActivityLog.Info("recorder.eos", $"recorder='{Name}' bus={busName} element='{elementName}'");

                // **EOS だけで終わるソースがある。** 画面キャプチャの WGC 経路
                // （capture-api=wgc）はディスプレイを切断しても Error を出さず、
                // sink バスへ EOS を流して黙る ── 印を立てるだけで誰も読まないまま終わるので、
                // ここで予約しなければ復帰の仕組みが丸ごと効かない（デバイス到着の監視も張られない）。
                //
                // **種別で絞る。** カメラ・画面キャプチャの EOS は切断以外にありえないので
                // 作り直すのが正しいが、有限のテストパターン（videotestsrc num-buffers=N）や
                // ファイルの EOS は**正常終了**であり、作り直すと同じ有限ストリームを
                // 無限に回し続けることになる。E2E の StopOutcomeTests は num-buffers で
                // 意図的に EOS を起こしているので、絞らないと 5 秒後の作り直しでソースが蘇り、
                // 「供給が止まっている」というあのテストの前提そのものが壊れる。
                if (DeviceKindRules.Classify(ActualSrcPipeline ?? SrcPipeline) != DeviceKind.None)
                    ScheduleRestart(elementName);
                break;
        }
    }

    /// <summary>障害を記録し、<see cref="LastError"/> と <see cref="ErrorOccurred"/> に反映する。</summary>
    private void ReportError(string detail)
    {
        Components.ActivityLog.Error("recorder.error", detail);
        LastError = detail;
        try { ErrorOccurred?.Invoke(this, detail); }
        catch (Exception ex) { Log(DebugLevel.Error, $"ErrorOccurred handler threw\n{ex}"); }
    }

    /// <summary>
    /// 録画中に src 側の障害を検出したときの停止要求。
    /// <b>バスのハンドラから <see cref="Stop"/> を直接呼んではいけない。</b>
    /// プールスレッドへ逃がし、そちらで通常の停止経路を通す。
    ///
    /// <para>
    /// 理由は2つあり、排出がプールで走る現在の形でも<b>この Task.Run は外せない</b>：
    /// </para>
    /// <list type="number">
    /// <item><see cref="StopAsync"/> は <c>_stateLock</c> を取る。ハンドラは
    /// <see cref="_busLock"/> を保持しているので、そこから <c>_stateLock</c> を取ると
    /// ロック順序（<c>_stateLock</c> → <see cref="_busLock"/>）が逆向きになる。</item>
    /// <item>排出は同じ <c>_srcBus</c> の EOS を待つ。ハンドラは post 元＝当のパイプラインの
    /// ストリーミングスレッドで走るので、そこで待つと EOS を運ぶスレッドを自分で止めることになる。</item>
    /// </list>
    /// </summary>
    private void RequestAbortRecording()
    {
        if (!_IsRecording)
            return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { Stop(); }
            catch (Exception ex) { Log(DebugLevel.Error, $"abort-stop failed\n{ex}"); }
        });
    }

    /// <summary>保留中の復帰タスクのキャンセル用。<see cref="Close"/> で確実に止めるために保持する。</summary>
    private CancellationTokenSource? _restartCts;

    /// <summary>保留中の復帰タスク（<see cref="Close"/> で有界待ちする）。</summary>
    private System.Threading.Tasks.Task? _restartTask;

    /// <summary>連続失敗回数。成功したら 0 に戻す。</summary>
    private int _restartAttempt;

    /// <summary>今の予約が保留中に、追加で来て積まれなかったエラーの件数。</summary>
    private int _restartRefusals;

    /// <summary>
    /// パイプラインを組めないまま作り直しを繰り返す連鎖（<c>rebuildOnly</c>）のログを畳む。
    ///
    /// <para>
    /// <b>この連鎖は成功するまで終わらない。</b> デバイスが二度と戻らない構成
    /// （挿さないカメラ・打ち間違えたパイプライン）では 60 秒ごとに失敗し続けるので、
    /// 素で書くと 1 日あたり数千行になり、<b>1MB のローテーションで
    /// 本当に見たい履歴が押し出される</b>。畳んでも件数は <c>repeated=N</c> に残る。
    /// </para>
    /// </summary>
    private readonly BusMessageThrottle _rebuildThrottle = new();

    /// <summary>
    /// 同じ理由で作り直しに失敗し続ける行を畳む。
    ///
    /// <para>
    /// <b><see cref="_rebuildThrottle"/> と別インスタンスにする。</b> あちらのキーは
    /// 要素名だけなので、共有すると<b>失敗の理由が別のものへ変わっても沈黙したまま</b>になる
    /// ── 「デバイスが無い」から「エンコーダーが無い」へ変わったことこそ読みたい情報である。
    /// </para>
    /// </summary>
    private readonly BusMessageThrottle _rebuildFailThrottle = new();

    /// <summary>
    /// 作り直しだけを試す連鎖（<c>rebuildOnly</c>）が何周目かを跨いで数える。
    ///
    /// <para>
    /// <b>1 周ごとに新しい連鎖になるので、ループ内の <c>attempt</c> では数えられない</b>
    /// ── 失敗した <see cref="Initialize"/> が次の連鎖を張る作りなので、
    /// <c>attempt</c> は常に 1 のままになり、ログを読む側が
    /// 「1 回しか試していない」と読み違える。作り直しが成功したら 0 へ戻す。
    /// </para>
    /// </summary>
    private int _rebuildRounds;

    /// <summary>
    /// 直近のデバイス到着より後に、作り直しへ失敗した回数。<c>-1</c> は「到着がまだ無い」。
    ///
    /// <para>
    /// <b><see cref="_rebuildRounds"/> と混ぜない。</b> あちらは「何周目か」を読ませるための
    /// 単調増加のカウンタで、こちらは<b>間隔を決める</b>ためのものである。到着のたびに 0 へ戻る
    /// ── 世界が変わったのだから、様子を見る理由が消えて短い梯子で追いかけ直す
    /// （<see cref="RestartPolicy.RebuildDelayMs"/>）。
    /// </para>
    /// <para>
    /// <b>連鎖ではなくレコーダーが持つ。</b> 作り直しの連鎖は 1 周ごとに終わって次の連鎖へ
    /// 渡されるので（<see cref="_rebuildRounds"/> と同じ理由）、ループのローカルでは数えられない。
    /// </para>
    /// <para>
    /// 素の <c>int</c> でよい ── 書くのは復帰の連鎖だけで、次の連鎖は <c>Task.Run</c> で
    /// 起こされるため、書き込みは新しい連鎖の読み出しに対して happens-before になる。
    /// </para>
    /// </summary>
    private int _rebuildFailuresSinceArrival = -1;

    /// <summary>
    /// 作り直し待ちの案内（<c>scheduled in ...</c>）を、何周に 1 回だけ出すか。
    ///
    /// <para>
    /// <b>この行だけは畳めない</b> ── 待ちに入るたびに出るので、
    /// デバイスが二度と戻らない構成では 1 分に 1 行、1 日で 1,400 行を超える。
    /// <c>activity.log</c> は 1MB で世代 1 つのローテーションなので、
    /// <b>これだけで数日ぶんの履歴が押し出される</b>。1 周目は必ず出し、
    /// 以後は約 1 時間に 1 行だけ「まだ待っている」を残す。
    /// </para>
    /// <para>
    /// <b>間隔が前の周回から変わった回は、この間引きから外す。</b> 到着で仕切り直した
    /// 短い梯子は<b>間引くと丸ごと見えなくなる</b> ── 作り直しの成否の行は畳まれる
    /// （<see cref="_rebuildThrottle"/>）ので、この案内が唯一の証拠である。
    /// <b>「頭打ちでない回は必ず出す」では上限が無い</b> ── 到着のたびに梯子は
    /// 1 段目へ戻るので、到着を繰り返すデバイスでは 5 秒ごとに 1 行という、
    /// この定数が防いでいるはずの洪水がそのまま戻ってくる。
    /// 変化した回だけなら、梯子 1 本につき数行で収まる。
    /// </para>
    /// </summary>
    private const int RebuildWaitAnnounceEveryRounds = 60;

    /// <summary>
    /// 直近に案内した作り直し待ちの間隔(ms)。<c>0</c> は「まだ案内していない」。
    ///
    /// <para>
    /// <b>連鎖ではなくレコーダーが持つ</b>（<see cref="_rebuildRounds"/> と同じ理由）。
    /// 前の周回と同じ間隔なら書くことが無いので、その回は
    /// <see cref="RebuildWaitAnnounceEveryRounds"/> の間引きに戻す。
    /// </para>
    /// </summary>
    private int _lastAnnouncedRebuildDelayMs;

    /// <summary>
    /// ソース障害からの自動復帰を予約する。
    ///
    /// <para>
    /// <b>保留中の復帰があれば積まずに return する。</b> 毎エラーごとに無条件に積むと、
    /// モニタを抜いたときの数十件の連続エラーに対して数十本の復帰試行が並走してしまう
    /// （<see cref="RestartPolicy"/> の注記を参照）。
    /// </para>
    /// </summary>
    private void ScheduleRestart(string elementName, bool rebuildOnly = false)
    {
        lock (_restartLock)
        {
            // **キャンセル済みの予約は空き枠として扱う。** CancelPendingRestart は
            // Cancel してから 2 秒だけ待つので、古い連鎖が Initialize() の中で
            // _stateLock を待って詰まっていると、キャンセル済みの _restartCts が
            // 残ったまま戻る ── そこで「already scheduled」と断ると、
            // **連鎖が 1 本も無い状態**（＝直したはずの詰みそのもの）になる。
            // 上書きは安全である ── 古い連鎖の finally は ReferenceEquals でしか消さない。
            if (_restartCts is { IsCancellationRequested: false })
            {
                // 既に予約済み。ここで積まないことが、復帰試行の並走を防ぐ要点そのもの。
                // **記録は予約1回につき1行だけ。** 壊れた要素は毎フレームのように
                // エラーを出すので（実測: 60秒で 41 件）、拒否を毎回書くと
                // それ自体が洪水になり、畳んだ意味が無くなる。件数は
                // 実行時に suppressedErrors= として報告する。
                if (_restartRefusals++ == 0)
                {
                    Components.ActivityLog.Info("recorder.restart",
                        $"recorder='{Name}' element='{elementName}' already scheduled (attempt {_restartAttempt + 1}); not stacking another");
                }
                return;
            }

            _restartRefusals = 0;
            var cts = new CancellationTokenSource();
            _restartCts = cts;

            // **1本のタスクが復帰の連鎖を最後まで所有する。**
            // 「1回試して、失敗したら ScheduleRestart を呼び直す」という再帰では動かない
            // ── 呼び直しの時点で _restartCts はまだ自分自身なので
            // 「already scheduled」で拒否され、連鎖が止まる。その形で次の試行が走るのは
            // **次のエラーが飛んで来たときだけ**なので、間隔は 5s/10s/30s の仕様どおりに
            // ならず、「試行回数は無制限」が成立するのもエラーを出し続けるソースだけになる
            // ── 「モニタを抜く＝エラーを数件出して以後は沈黙」というソースでは
            // 1回目の失敗で永久に止まる。ループ1本なら間隔も「諦めない」も仕様どおりになる。
            _restartTask = System.Threading.Tasks.Task.Run(() => RestartLoopAsync(elementName, cts, rebuildOnly));
        }
    }

    /// <summary>
    /// 復帰の連鎖。成功するか、エスカレーション（パイプライン再生成）に至るまで、
    /// <see cref="RestartPolicy"/> の間隔で試行し続ける。
    /// 新しいエラーの到着に依存しない ── 一度だけエラーを出して沈黙するソース
    /// （ケーブルを抜いたモニタ）でも最後まで進む。
    ///
    /// <para>
    /// <b>間隔は上限であって、待ち切る義務ではない。</b> 監視できる映像源
    /// （カメラ・画面キャプチャ）なら、デバイスの到着を観測した時点で待ちを打ち切る
    /// （<c>DeviceArrivalWatcher</c>）。<b>試行回数の数え方は変えない</b> ──
    /// 早く起きた回も通常の 1 回として数える。
    /// </para>
    /// <para>
    /// <paramref name="rebuildOnly"/> は<b>パイプラインがそもそも組めていない</b>連鎖。
    /// 要素単位の再開は対象が無いので飛ばし、<see cref="Initialize"/> だけを
    /// <see cref="RestartPolicy.RebuildDelayMs"/> の間隔（＋到着で早期）で試す
    /// ── 到着があるまでは 60 秒、到着の後は短い梯子をやり直す。
    /// この連鎖が無いと、初期化に失敗した時点でエラーの出所ごと消えて
    /// <b>デバイスを挿し直しても永久に復帰しない</b>。
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task RestartLoopAsync(
        string elementName, CancellationTokenSource cts, bool rebuildOnly)
    {
        // **監視の取得はここ（プールスレッド）で行う。** ScheduleRestart は _busLock を
        // 保持したストリーミングスレッドから呼ばれるので、そこでネイティブの
        // DeviceProvider.Start を呼んではいけない。解放は using が全出口で行う。
        DeviceKind kind = DeviceKindRules.Classify(ActualSrcPipeline ?? SrcPipeline);
        using IDisposable watch = DeviceArrivalWatcher.Instance.Acquire(kind);
        bool watched = DeviceArrivalWatcher.IsWatched(kind);
        // watch= はデバイスの種別が付くときだけ出す。テスト注入だけが有効な
        // DeviceKind.None の連鎖で "watch=none" と書くと、ログを読む側に
        // 「監視しているのに none」という読み方をさせてしまう。
        string watchTag = kind != DeviceKind.None ? $" watch={DeviceKindRules.LogName(kind)}" : "";

        // 作り直しだけの連鎖は 1 周で終わって次の連鎖へ渡すので、周回は連鎖を跨いで数える。
        int round = rebuildOnly ? Interlocked.Increment(ref _rebuildRounds) : 0;

        try
        {
            int attempt = 0;
            int earlyWakes = 0;

            // 直近の result=no-samples を出した回の基準値（-1 は「まだ無い」）。
            // 次の試行は、待っているあいだにこの値から進んでいれば
            // 「自力で戻った」と見て要素に触らない。
            long noSampleBaseline = -1;
            long seenArrival = DeviceArrivalWatcher.Instance.CurrentGeneration(kind);
            while (!cts.IsCancellationRequested)
            {
                attempt++;
                // **作り直しの間隔は到着で仕切り直す。** 頭打ちのままだと、到着で起きた回が
                // 失敗した時点で次の機会が丸 60 秒先になる ── RDP のセッション復帰のように
                // 到着の直後はまだ撮れない場合に、復帰がまるまる 1 分遅れる。
                int delayMs = rebuildOnly
                    ? RestartPolicy.RebuildDelayMs(_rebuildFailuresSinceArrival)
                    : RestartPolicy.DelayForAttempt(attempt);
                if (!rebuildOnly || round == 1 || round % RebuildWaitAnnounceEveryRounds == 0
                    || delayMs != _lastAnnouncedRebuildDelayMs)
                {
                    if (rebuildOnly)
                        _lastAnnouncedRebuildDelayMs = delayMs;
                    string counter = rebuildOnly ? $"round={round}" : $"attempt={attempt}";
                    Components.ActivityLog.Info("recorder.restart",
                        $"recorder='{Name}' element='{elementName}' {counter} scheduled in {delayMs}ms{watchTag}");
                }

                // **到着で打ち切れる回数には上限がある。** 上限を外すと、モニターの
                // 再構成のような連打だけでエスカレーションの予算（3 回）が数秒で尽き、
                // まだ落ち着いていない機械へパイプライン全再生成を掛けることになる。
                bool mayWakeEarly = watched && RestartPolicy.MayWakeEarly(earlyWakes);
                bool early = await WaitForRetrySlotAsync(kind, mayWakeEarly, seenArrival, delayMs, cts.Token);

                // **到着で起きたら、作り直しの間隔を最初からやり直す。** バックオフは
                // 「居ないデバイスを叩き続けない」ためのものなので、到着した時点でその
                // 理由は消えている。試行の**前**に戻すこと ── この回が失敗したときに
                // 次の周回が梯子の 1 段目（5s）から始まるようにするため。
                //
                // **rebuildOnly の連鎖に限らない。** エスカレーションの作り直しも
                // 到着で早められるので、そこで空振りしたときこそ次を早く来させたい
                // ── RDP のセッション復帰は「仮想ディスプレイが先に現れ、実モニタは
                // 後から戻る」二段構えで、一段目の到着で起きた作り直しは必ず失敗する。
                // 絞ると、そこから次の機会まで丸 1 分空く。
                if (early)
                    _rebuildFailuresSinceArrival = 0;

                if (early && !RestartPolicy.MayWakeEarly(++earlyWakes))
                {
                    // 1 連鎖につき 1 行だけ。以後はバックオフを待ち切る。
                    Components.ActivityLog.Info("recorder.restart",
                        $"recorder='{Name}' element='{elementName}' early wakes exhausted ({earlyWakes}); waiting out the backoff");
                }
                // **キーは reason= と分ける。** エスカレーションの行は既に
                // reason=eos を持っており、同じキーを重ねると
                // "reason=eos reason=device-arrival" という読めない行になる
                // ── 「なぜ作り直すか」と「なぜ今起きたか」は別の情報である。
                string wake = early ? " wake=device-arrival" : "";

                // Close() 中なら、破棄済みのパイプラインに触る前にここで降りる。
                // _isAlive では代用できない ── 通常の Initialize() 待ちでも false になる。
                if (cts.IsCancellationRequested || _disposedValue)
                    return;

                // **rebuildOnly では _isAlive を見ない。** あちらはパイプラインが無い状態が
                // 定常で _isAlive は false のままであり、触る先も Initialize() だけである。
                if (!rebuildOnly && !_isAlive)
                    return;

                // **次の待ちの基準をここで取り直す。** これより後に来た到着は
                // 「この試行より後の到着」なので、次の待ちを即座に起こしてよい
                // ── 取り直さないと、復帰を試している最中に挿し直された場合を取りこぼす。
                seenArrival = DeviceArrivalWatcher.Instance.CurrentGeneration(kind);

                int refused = _restartRefusals;
                string suppressed = 0 < refused ? $" suppressedErrors={refused}" : "";
                _restartRefusals = 0;

                // **EOS を受けていたら要素単位では戻らない。** 試行を重ねても同じなので、
                // 最初からパイプラインごと作り直す ── ここを飛ばすと、復帰は result=ok と
                // 報告されるのに映像だけが崩れたままになる（実機のカメラ抜き差しで観測）。
                bool mustRebuild = rebuildOnly || _sinkSawEos;

                // 失敗の理由。遷移そのものが拒まれたら failed、遷移は通ったのに
                // 実が来なければ no-samples（下の保険）。
                string failure = "failed";

                // 要素単位の再開が成功したときの後始末（3 か所から使う）。
                void ReportElementRecovered()
                {
                    _restartAttempt = 0;
                    // 戻せたのだから、到着に追いつくための梯子はもう要らない
                    // （作り直しが成功したときと同じ）。戻さないと、この回の早期起床で
                    // 置いた 0 が残り続け、ずっと後の・到着と無関係な作り直しの連鎖が
                    // 60 秒ではなく短い梯子で回る。
                    _rebuildFailuresSinceArrival = -1;
                    Components.ActivityLog.Info("recorder.restart",
                        $"recorder='{Name}' element='{elementName}' attempt={attempt} result=ok{wake}{suppressed}");
                }

                if (!mustRebuild)
                {
                    // **基準は再開の前に取る。** 後で取ると、再開と同時に来た 1 枚を
                    // 数え損ねて「戻ったのに no-samples」になる。
                    long baseline = Interlocked.Read(ref _sinkSamplesSeen);

                    // **待っているあいだに自力で戻っていたら触らない。** 前の回の
                    // no-samples から次の試行までは 10〜30 秒あり、その間にソースが
                    // 自力で流し始めることがある ── そこへ Ready→Playing を掛けるのは
                    // 動いているデバイスを閉じて開き直す行為で、コマ落ちを作るだけである。
                    if (0 <= noSampleBaseline && noSampleBaseline != baseline)
                    {
                        _errorSinkSrc = null;
                        ReportElementRecovered();
                        return;
                    }

                    if (RestartSinkSrc(out var restarted))
                    {
                        // **遷移が通っただけでは戻ったと言えない。** EOS が bus に届いて
                        // いれば mustRebuild でここへは来ないが、届かなかった回は
                        // 「Playing だが実は来ない」要素を result=ok と報告してしまい、
                        // 連鎖がそこで終わって誰も作り直さなくなる。
                        if (await WaitForSinkSamplesAsync(baseline, cts.Token).ConfigureAwait(false))
                        {
                            ReportElementRecovered();
                            return;
                        }

                        // 待ちを打ち切ったのが停止（Close）なら、失敗として数えずに降りる。
                        if (cts.IsCancellationRequested || _disposedValue)
                            return;

                        failure = $"no-samples waited={RestartPolicy.SinkSampleGraceMs}ms";
                        noSampleBaseline = baseline;

                        // **控えを戻す。** RestartSinkSrc は遷移が通った時点で控えを落とす
                        // ので、そのままだと次の試行は「対象なし」の false になり、
                        // もう一度 Ready→Playing を試す機会が消える
                        // （エスカレーションの数え方も理由の行も変わってしまう）。
                        // 戻すのは**実際に再開を試みた要素**（out で受けたもの）── ここで
                        // _errorSinkSrc を読み直すと、その間に別の要素が控え直されていた
                        // 場合に古い方で上書きしてしまう。
                        lock (_sinkSrcRestartLock)
                            _errorSinkSrc ??= restarted;
                    }
                }

                if (!mustRebuild)
                {
                    _restartAttempt = attempt;
                    Components.ActivityLog.Warn("recorder.restart",
                        $"recorder='{Name}' element='{elementName}' attempt={attempt} result={failure}{wake}{suppressed}");

                    if (!RestartPolicy.ShouldEscalate(attempt))
                        continue;   // まだ諦めない。次の間隔で再試行する
                }

                // 要素単位では戻せない状態（デバイスが別のキャップスで戻った・
                // エンコーダーが壊れた）とみなし、パイプラインごと作り直す。
                if (cts.IsCancellationRequested)
                    return;

                // 作り直しだけを繰り返す連鎖は終わりが無いので畳む。
                // エスカレーションは1つの連鎖につき1回きりなので畳まない。
                (bool emitRebuild, int rebuildRepeated) = rebuildOnly
                    ? _rebuildThrottle.Observe($"rebuild {elementName}")
                    : (true, 0);
                string rebuildRepeatedTag = 0 < rebuildRepeated ? $" repeated={rebuildRepeated}" : "";

                if (emitRebuild)
                {
                    Components.ActivityLog.Warn("recorder.restart",
                        rebuildOnly
                            ? $"recorder='{Name}' retrying the pipeline rebuild round={round}{wake}{rebuildRepeatedTag}"
                            : mustRebuild
                                ? $"recorder='{Name}' escalating to a full pipeline rebuild reason=eos{wake}"
                                : $"recorder='{Name}' escalating to a full pipeline rebuild after {attempt} failed attempts{wake}");
                }
                _restartAttempt = 0;

                // **作り直しの前に「録り直す」意図を控える。** この後の Initialize() は
                // 先頭の Close() で進行中の録画を確定させる（ファイルは壊れない）ので、
                // ここで控えておかないと**復帰しても録画だけが戻らない**
                // ── 常時録画は InitializeWith の末尾で作り直されるのに、
                // イベント録画だけ再開しない、という非対称がこれで消える。
                // **_stateLock の下で見る** ── ちょうど同時に止められた場合に、
                // 降ろされた直後の意図を立て直してしまわないため。
                lock (_stateLock)
                {
                    if (_IsRecording && !_resumeAfterRecovery)
                    {
                        _resumeAfterRecovery = true;
                        // 畳む録画の理由をここで控える（_IsRecording が真＝_startTrigger は非 null）。
                        // 何周回っても控え直さないのは、上の !_resumeAfterRecovery が
                        // 最初の 1 回だけ通すため ── 録り直しは畳んだ本の続きである。
                        _resumeTrigger = _startTrigger;
                        IsAwaitingRecoveryResume = true;
                        Components.ActivityLog.Info("recorder.restart",
                            $"recorder='{Name}' the recording will be resumed once the pipeline is rebuilt");
                    }
                }

                // 再生成へ進む前に、連鎖の所有権を自分で畳む。Initialize() は Close() 経由で
                // CancelPendingRestart を呼び、その時点の _restartTask は「実行中の自分自身」
                // ── 畳まずに進むと pending?.Wait(2000) が自タスクの完了を待つ形になり、
                // 原理的に完了しないまま毎回 2 秒、_stateLock を握ったまま止まる。
                // （畳んだ後に来たエラーは新しい連鎖を積めるが、それは Initialize() 内の
                //   Close が改めてキャンセルするので二重には走らない。）
                lock (_restartLock)
                {
                    if (ReferenceEquals(_restartCts, cts))
                        _restartCts = null;
                    _restartTask = null;
                }

                // **失敗の計上は Initialize() より前に行う。** 失敗した Initialize() は
                // 自分の中で次の連鎖を張る（TryScheduleDeviceRebuild）ので、例外がここまで
                // 返ってきてから数えたのでは**次の連鎖が古い値を読みうる**
                // ── 到着で 0 に戻した直後なら、その連鎖はまた 60 秒待つことになる。
                // 成功した場合は下で -1 へ戻すので、先に数えても見える値は変わらない。
                if (0 <= _rebuildFailuresSinceArrival)
                    _rebuildFailuresSinceArrival++;

                try
                {
                    Initialize();
                    // **成功は必ず出す。** 畳んだ抑制もここで解いて、次の障害を1行目から見せる。
                    // **畳んだ件数は捨てない** ── 捨てると「静かだった」と
                    // 「何百回も失敗を畳んでいた」が同じ 1 行に見える。
                    _rebuildRounds = 0;
                    // 作り直せたのだから、到着に追いつくための梯子はもう要らない。
                    _rebuildFailuresSinceArrival = -1;
                    int flushed = _rebuildThrottle.Flush() + _rebuildFailThrottle.Flush();
                    string flushedTag = 0 < flushed ? $" repeated={flushed}" : "";
                    Components.ActivityLog.Info("recorder.restart",
                        $"recorder='{Name}' rebuild result=ok{wake}{flushedTag}");
                    ResumeRecordingIfPending();
                }
                catch (Exception ex)
                {
                    // **ここで諦めない。** Initialize() は失敗時に自分で次の連鎖
                    // （rebuildOnly）を張るので、デバイスが戻ってくればそちらが作り直す。
                    // 所有権は既に畳んであるため、その予約は拒否されない。
                    //
                    // **失敗の行は理由そのもので畳む。** 上の rebuildOnly の畳みは
                    // 要素名だけをキーにしているので、それに従わせると
                    // 「原因が別のものへ変わった」が沈黙のまま流れてしまう。
                    (bool emitFailure, int failureRepeated) = rebuildOnly
                        ? _rebuildFailThrottle.Observe($"{elementName} {ex.Message}")
                        : (true, 0);
                    if (emitFailure)
                    {
                        string failureRepeatedTag = 0 < failureRepeated ? $" repeated={failureRepeated}" : "";
                        Components.ActivityLog.Error("recorder.restart",
                            $"recorder='{Name}' rebuild result=failed {ex.Message}{failureRepeatedTag}");
                    }
                }
                return;
            }
        }
        catch (OperationCanceledException) { /* Close() による中止。正常系 */ }
        catch (Exception ex) { Log(DebugLevel.Error, $"restart loop failed\n{ex}"); }
        finally
        {
            lock (_restartLock)
            {
                if (ReferenceEquals(_restartCts, cts))
                    _restartCts = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// 作り直しで畳んだ録画を録り直す。予定が無ければ何もしない。
    ///
    /// <para>
    /// <b>意図は先に降ろしてから開始する。</b> 開始が失敗したときに立てたままにすると、
    /// 次の作り直しのたびに永久に録り直しを試みることになる。
    /// </para>
    /// <para>
    /// <c>recording.start</c> は <see cref="StartCore"/> が出すので、ここでは
    /// 「なぜ始まったか」を 1 行だけ残す ── その 2 行が並ぶことが、利用者が始めた録画と
    /// 復帰で戻った録画を後から見分ける唯一の手がかりになる。
    /// </para>
    /// <para>
    /// <b>意図の検査から <see cref="Start"/> までを <c>_stateLock</c> の下で行う。</b>
    /// 分けると「作り直しのあいだの停止」が落ちる ── 停止側は
    /// <see cref="Initialize"/> が握っているロックの解放待ちで数秒止まっているので、
    /// ロック外で意図を読んだこちらが先に進み、後から入った <see cref="StopAsync"/> は
    /// <see cref="CancelRecoveryResume"/> が空振りしたうえ <c>_IsRecording</c> も false で
    /// <b>1 行も実行せずに返る</b>。その直後にこちらが開始するので、
    /// <b>利用者が止めた録画が戻ってくる</b>。
    /// </para>
    /// </summary>
    private void ResumeRecordingIfPending()
    {
        lock (_stateLock)
        {
            if (!_resumeAfterRecovery)
                return;

            _resumeAfterRecovery = false;
            IsAwaitingRecoveryResume = false;

            Components.ActivityLog.Info("recorder.restart",
                $"recorder='{Name}' resuming the recording that the rebuild finalized");
            try
            {
                // **理由は畳んだ本から引き継ぐ。** 復帰は利用者の操作ではないので、
                // "manual" で置くと uia:<id> 起点の録画が sidecar でだけ理由を偽る
                // （その録画は停止まで UiaTriggerService が所有し続ける）。
                // 控えが無いのは _startTrigger を持たない経路だけなので "manual" へ落とす。
                Start(_resumeTrigger ?? "manual");
            }
            catch (Exception ex)
            {
                // 失敗の記録と LastError は StartCore が済ませている。連鎖は殺さない。
                Log(DebugLevel.Error, $"resuming the recording failed\n{ex}");
            }
        }
    }

    /// <summary>
    /// 復帰後に録り直す予定を取り消す。<b>録画中でなくても効くこと</b>が要点で、
    /// 作り直しのあいだ（<c>_IsRecording</c> も <c>IsInitialized</c> も false）に
    /// 届いた停止を、ここで受け止める。
    /// </summary>
    private void CancelRecoveryResume(string reason)
    {
        if (!_resumeAfterRecovery)
            return;

        _resumeAfterRecovery = false;
        _resumeTrigger = null;
        IsAwaitingRecoveryResume = false;
        Components.ActivityLog.Info("recorder.restart",
            $"recorder='{Name}' not resuming the recording after the rebuild ({reason})");
    }

    /// <summary>
    /// 次の試行までの待ち。タイマーと<b>デバイスの到着</b>の早い方で返る。
    /// 到着で返ったときだけ true。
    ///
    /// <para>
    /// 到着で返った場合は <see cref="RestartPolicy.SettleAfterArrivalMs"/> だけ置いてから返す
    /// ── <b>列挙に出た＝開けるとは限らない</b>ため。置く時間は元の待ちを超えない。
    /// </para>
    /// <para>
    /// キャンセルを運ぶのはタイマー側だけである。到着側の札はキャンセルされないので、
    /// <c>WhenAny</c> がタイマーを返したときに<b>それを await して</b>
    /// <see cref="OperationCanceledException"/> を表に出す。
    /// </para>
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> WaitForRetrySlotAsync(
        DeviceKind kind, bool watched, long seenArrival, int delayMs, CancellationToken token)
    {
        var timer = System.Threading.Tasks.Task.Delay(delayMs, token);
        if (!watched)
        {
            await timer;
            return false;
        }

        System.Threading.Tasks.Task arrival =
            DeviceArrivalWatcher.Instance.WaitForArrivalAsync(kind, seenArrival);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Tasks.Task finished =
            await System.Threading.Tasks.Task.WhenAny(arrival, timer);

        if (!ReferenceEquals(finished, arrival))
        {
            await timer;
            return false;
        }

        int settleMs = RestartPolicy.SettleAfterArrivalMs(
            delayMs, (int)Math.Min(elapsed.ElapsedMilliseconds, int.MaxValue));
        if (0 < settleMs)
            await System.Threading.Tasks.Task.Delay(settleMs, token);

        return true;
    }

    private readonly object _restartLock = new();

    /// <summary>
    /// 保留中の自動復帰をキャンセルして有界待ちする。
    ///
    /// <para>
    /// <b><c>_stateLock</c> を保持したまま呼ばれる</b>（<see cref="CloseCore"/> から）。
    /// 復帰タスクは <see cref="RunScheduledRestart"/> の中で <see cref="Initialize"/> を
    /// 呼びうるので、そこで <c>_stateLock</c> を取る ── つまりキャンセルせずに待つと
    /// デッドロックする。<c>Cancel()</c> を先に呼んでから待つこと、および
    /// 待ちを有界にすることの両方が必要。
    /// </para>
    /// </summary>
    private void CancelPendingRestart()
    {
        System.Diagnostics.Debug.Assert(Monitor.IsEntered(_stateLock), "CancelPendingRestart must run under _stateLock");

        System.Threading.Tasks.Task? pending;
        lock (_restartLock)
        {
            _restartCts?.Cancel();
            pending = _restartTask;
            _restartTask = null;
        }

        // Delay 待ちなら即座にキャンセルされる。エスカレーションの Initialize() が
        // 走っている最中なら、それは Close() を呼んだスレッドが持つ _stateLock を
        // 待つので、ここで待ち切ることはできない（2秒で諦める）。
        // 諦めた場合の「破棄済みレコーダーの作り直し」は IsCancellationRequested では
        // 防げない（確認とロック取得の間に窓がある）── InitializeCore 先頭の
        // Dispose 済み検査が防ぐ。
        try { pending?.Wait(2000); }
        catch (AggregateException) { /* キャンセル例外。正常系 */ }
    }

    /// <summary>
    /// 障害を起こしたソース要素を <c>Ready</c> を経由して <c>Playing</c> に戻す。
    /// 遷移が拒否されなければ true。
    ///
    /// <para>
    /// <b>false は「要素単位では戻せない」を意味する</b> ── 対象が無い場合
    /// （ソース以外の要素が壊れた場合）も false を返し、呼び出し側の
    /// エスカレーション（パイプライン再生成）へ進ませる。
    /// </para>
    /// <para>
    /// <b>Ready を必ず経由する。</b> flow error のあとの basesrc はストリーミングタスクが
    /// pause しているだけで、状態は <c>PLAYING</c> のままである ── <c>Playing</c> を
    /// 指示しても遷移が無く、<c>Success</c> を返して<b>何も再開しない</b>。
    /// <c>Ready</c> まで落として pad を作り直して初めてタスクが起き直る。
    /// <c>Async</c> は成功として扱う（非同期に遷移する要素をここで待たない）。
    /// </para>
    /// <para>
    /// <b>呼んでよいのは復帰の連鎖（プールスレッド）と <see cref="StartCore"/> だけ。</b>
    /// バスのハンドラは壊れた要素自身のストリーミングスレッドなので、そこから呼ぶと
    /// 自スレッドの復帰を待って固まり、さらに basesrc がこの後に押す EOS が
    /// flushing で消える。
    /// </para>
    /// <para>
    /// <b>true は「遷移が通った」までしか意味しない。</b> 実が流れ出したかは
    /// 呼び出し側が <see cref="_sinkSamplesSeen"/> で確かめる
    /// （<see cref="RestartPolicy.SinkSampleGraceMs"/>）。
    /// </para>
    /// <para>
    /// <b>同時に 2 本走らせない</b>（<see cref="_sinkSrcRestartLock"/>）── 復帰の連鎖と
    /// <see cref="StartCore"/> は別のスレッドで、同じ要素へ <c>Ready</c> と
    /// <c>Playing</c> を同時に出しうる。<b>専用のロックであって
    /// <c>_restartLock</c> ではない</b> ── あちらは <c>ScheduleRestart</c> が
    /// ストリーミングスレッドから取るので、その下で <c>SetState(Ready)</c>
    /// （＝当のストリーミングスレッドの停止待ち）を行うと相互待ちになる。
    /// </para>
    /// </summary>
    /// <param name="restarted">
    /// 実際に再開を試みた要素（対象が無ければ <c>null</c>）。呼び出し側が
    /// 失敗時に控えを戻すために使う ── ロックの外で <c>_errorSinkSrc</c> を読み直すと、
    /// その間に別の要素が控え直されていた場合に古い方を復元してしまう。
    /// </param>
    private bool RestartSinkSrc(out GstBase.BaseSrc? restarted)
    {
        lock (_sinkSrcRestartLock)
        {
            var target = _errorSinkSrc;
            restarted = target;
            if (target is null)
                return false;

            var ready = target.SetState(State.Ready);
            if (ready == StateChangeReturn.Failure)
                return false;

            // **Async のまま Playing を出さない。** READY へ着く前に PLAYING を
            // 指示すると、pad を作り直している最中の要素に対する指示になり、
            // 遷移が畳まれて「Playing を指示したのに何も再開しない」に戻りうる。
            if (ready == StateChangeReturn.Async
                && target.GetState(out _, out _, ClockTime.FromMilliseconds(ReadyStateTimeoutMs))
                   == StateChangeReturn.Failure)
            {
                return false;
            }

            if (target.SetState(State.Playing) == StateChangeReturn.Failure)
                return false;

            _errorSinkSrc = null;
            return true;
        }
    }

    /// <summary>
    /// <see cref="RestartSinkSrc"/> を直列化する専用のロック。
    /// <c>_restartLock</c> を使ってはいけない理由はあちらの doc を参照。
    /// </summary>
    private readonly object _sinkSrcRestartLock = new();

    /// <summary>
    /// <see cref="RestartSinkSrc"/> が <c>READY</c> の到達を待つ上限(ms)。
    /// <c>Async</c> を返した要素にだけ効く。
    /// </summary>
    private const int ReadyStateTimeoutMs = 2_000;

    /// <summary>
    /// 要素単位の再開のあと、sink の <c>appsink</c> に実が来ることを
    /// <see cref="RestartPolicy.SinkSampleGraceMs"/> だけ確かめる。1 枚でも来たら true。
    ///
    /// <para>
    /// <b>これは保険である。</b> 本線は「EOS が bus に届く → <see cref="_sinkSawEos"/> が
    /// 立つ → 要素単位の再開を飛ばして作り直す」で、ここが要るのは EOS が
    /// 届かなかった回だけ。遷移が通っただけの再開を <c>result=ok</c> と報告すると、
    /// 連鎖はそこで終わり、映像が止まったまま誰も作り直さない。
    /// </para>
    /// <para>
    /// 数えるのは録画中かどうかに依らない <see cref="_sinkSamplesSeen"/> ──
    /// <see cref="_samplesSeenWhileRecording"/> は録画中しか進まないので、
    /// 常時稼働の sink 側の生死には使えない。
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task<bool> WaitForSinkSamplesAsync(
        long baseline, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + RestartPolicy.SinkSampleGraceMs;
        while (Interlocked.Read(ref _sinkSamplesSeen) == baseline)
        {
            if (deadline <= Environment.TickCount64)
                return false;

            try
            {
                await System.Threading.Tasks.Task
                    .Delay(SinkSamplePollMs, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <see cref="WaitForSinkSamplesAsync"/> の刻み(ms)。1 枚来た時点で抜けるので、
    /// 通常の復帰がこの刻みぶん遅れるだけ（映像源は 15fps 以上を前提にしている）。
    /// </summary>
    private const int SinkSamplePollMs = 100;

    /// <summary>
    /// プレビュー用のサンプルが取れたときに発火する。
    ///
    /// <para>
    /// <b>発火するのはプレビュー枝のストリーミングスレッド</b>（<c>appsink</c> の
    /// コールバック）で、<b>サンプルはハンドラを抜けた時点で破棄される</b>
    /// ── 購読側はその場で同期に消費し切ること（後で使うなら自前で複製する）。
    /// </para>
    /// <para>
    /// <b>購読側はブロックしてはいけない。</b> ここで待つとプレビュー枝の
    /// ストリーミングスレッドがそのぶん止まる。<c>Controller.GstEventRecorder_Preview</c> は
    /// <c>Monitor.TryEnter</c> で「取れなければ待たずに捨てる」形にしてある。
    /// </para>
    /// </summary>
    public event EventHandler<PreviewEventArgs>? Preview;

    protected virtual void OnPreview(Sample sample)
        => Preview?.Invoke(this, new(sample));


    /// <summary>
    /// FilenameTemplate から {キー名} で参照できるユーザー定義変数。
    /// 値を <see cref="string"/> に限定しているのは、設定ファイル(settings.json)へ
    /// System.Text.Json のソース生成で永続化するため（<c>object</c> 値は Native AOT で扱えない）。
    /// </summary>
    public static ConcurrentDictionary<string, string> TemplateVariables { get; } = new();

    /// <summary>
    /// TemplateVariables 変更時に発火する
    /// （<see cref="SetTemplateVariable"/>/<see cref="RemoveTemplateVariable"/> 経由の変更のみ）
    /// </summary>
    public static event EventHandler? TemplateVariablesChanged;

    /// <summary>
    /// 録画ファイルの保存先（絶対パス）。<see cref="FilenameTemplate"/> が相対パスのとき、
    /// これを基準に解決する。
    ///
    /// <para>
    /// <c>AppSettings.OutputDirectory</c> の static ミラー
    /// （<c>PreferredH264Encoder</c> と同じ形。GStreamer 層は設定クラスを参照できない）。
    /// 空欄のままなら実行ファイルのあるディレクトリになる。
    /// </para>
    /// <para>
    /// <b>プロセスのカレントディレクトリを基準にしてはいけない。</b> 常駐ワーカーは最初に
    /// 起動したシェルのカレントディレクトリをプロセス寿命ぶん引きずるので、
    /// 「誰がどこから起動したか」で出力先が変わってしまう（<c>AppDirectories</c> 参照）。
    /// </para>
    /// </summary>
    public static string OutputDirectory { get; set; }
        = Components.AppDirectories.BaseDirectory;

    /// <summary>テンプレート変数を設定し、変更イベントを発火する</summary>
    public static void SetTemplateVariable(string key, string value)
    {
        TemplateVariables[key] = value;
        TemplateVariablesChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>テンプレート変数を削除し、削除できた場合は変更イベントを発火する</summary>
    public static bool RemoveTemplateVariable(string key)
    {
        bool removed = TemplateVariables.TryRemove(key, out _);
        if (removed)
            TemplateVariablesChanged?.Invoke(null, EventArgs.Empty);
        return removed;
    }

    // FilenameTemplate のプレースホルダ({Now}, {Name}, {ENV.x}, TemplateVariables のキー)を展開する。
    // 実装は GStreamer 非依存の FilenameTemplate クラスにあり、ここでは現在の状態を束ねて渡すだけ。
    // 複数出現時も同一時刻になるよう、DateTime.Now は先頭で1回だけ取得する。
    private string FormatFilename(string template)
        => GStreamer.FilenameTemplate.Format(
            template,
            Name,
            System.DateTime.Now,
            TemplateVariables,
            Environment.GetEnvironmentVariable);

    /// <summary>
    /// 録画を開始する。
    /// </summary>
    /// <param name="trigger">
    /// 開始理由。sidecar の <c>trigger</c> にそのまま載る
    /// （<c>manual</c> / <c>uia:&lt;triggerId&gt;</c> / <c>remote</c> / <c>cli</c>）。
    /// 常時録画のセグメントは <c>continuous</c> で、この経路は通らない。
    /// </param>
    public void Start(string trigger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);

        lock (_stateLock)
        {
            // 直前の停止がまだ排出中なら待つ。待たずに開始すると、排出して Null へ
            // 落とす途中の _srcPipeline に対して SetState(Playing) を掛けることになり、
            // stop → start の即時連打で競合する。
            // **待ち切れなかったら開始しない** ── 排出タスクはまだ _srcPipeline を
            // 触っている（mux 詰まり）。そのまま StartCore へ進むと、防いだはずの競合が
            // タイムアウトを境に解禁される。開始できない事実を呼び出し側へ見せて拒否する
            // （Close の abandonedStop と同じく「触らない」を選ぶ）。
            if (!WaitForPendingStop())
            {
                LastError = "the previous stop is still draining";
                Components.ActivityLog.Error("recording.start fail",
                    $"recorder='{Name}' the previous stop is still draining; refused to start");
                throw new InvalidOperationException("The previous stop is still draining.");
            }
            StartCore(trigger);
        }
    }

    private void StartCore(string trigger)
    {
        System.Diagnostics.Debug.Assert(Monitor.IsEntered(_stateLock), "StartCore must run under _stateLock");

        if (!IsInitialized)
            throw new InvalidOperationException("Not Initialized");
        if (_IsRecording)
            throw new InvalidOperationException("Already started");

        // **控えるのは可否の検査を通ってから。** 手前で書くと、録画中に届いた開始要求
        // （「Already started」で弾く分）の理由が、走っている録画の sidecar に載る。
        _startTrigger = trigger;

        // 録画が始まる＝復帰後に録り直す意図は満たされた。
        _resumeAfterRecovery = false;
        _resumeTrigger = null;
        IsAwaitingRecoveryResume = false;

        // 新しい録画では前回の障害表示を消す（「今の状態」を表すプロパティなので）
        LastError = null;
        _srcThrottles.Error.Flush();
        _srcThrottles.Warning.Flush();

        string filename;
        try
        {
            filename = FormatFilename(FilenameTemplate);
            if (!Path.IsPathRooted(filename))
                filename = Path.Combine(OutputDirectory, filename);
            LastFilename = filename;

            var dirname = Path.GetDirectoryName(filename);
            if (dirname is not null && !Directory.Exists(dirname))
                Directory.CreateDirectory(dirname);
        }
        catch (Exception ex)
        {
            // テンプレートの書式誤り・保存先の作成失敗（外した USB ドライブ等）もここで記録する。
            // 記録と LastError を呼び出し側任せにすると、握り方しだいで無記録の失敗になる
            // （SetState の失敗と同じ扱いに揃える）。
            LastError = ex.Message;
            Components.ActivityLog.Error("recording.start fail",
                $"recorder='{Name}' template='{FilenameTemplate}' {ex.Message}");
            throw;
        }

        // GObject.Value は所有する（using で必ず解放する）。SetProperty は値を
        // 複製して渡すので、この Value の所有はこちらに残る。
        using (var location = GObject.Value.New(GObject.GType.String))
        {
            location.SetString(filename);
            _file?.SetProperty("location", in location);
        }

        // **計測のリセットは _IsRecording を立てる「前」に行う。**
        // 数えるのは _IsRecording が真の間だけなので、この順序なら
        // サンプルのコールバックと競合しない（逆順だと、立てた直後に数えた分を消しうる）。
        _samplesSeenWhileRecording = 0;
        _samplesPushed = 0;
        LastStopOutcome = RecordingStopOutcome.Ok;

        // 世代も _IsRecording より前に進める ── コールバックは _IsRecording（volatile）を
        // 見てから世代を読むので、この順序なら「録画中なのに世代が古いまま」は観測されない。
        _recordingSession++;

        IsRecording = _IsRecording = true;
        _recordingStartedAt = Environment.TickCount64;
        _recordingStartedAtUtc = DateTimeOffset.UtcNow;
        if (_srcPipeline!.SetState(State.Playing) == StateChangeReturn.Failure)
        {
            // 成功と失敗でイベント名を分ける（`recorder.init ok` / `recorder.init fail` と同じ規則）。
            // 同名にすると L2 が `recording.start` に掛ける正規表現が失敗行にも一致してしまう。
            LastError = "src pipeline refused to play";
            Components.ActivityLog.Error("recording.start fail", $"recorder='{Name}' file='{filename}' src pipeline refused to play");
            throw new InvalidOperationException("ERROR: pipeline doesn't want to play.");
        }

        // サムネイルは 1 回だけ要求する。撮るのはプレビュー枝で、完了は待たない
        // （録画中の一覧にもサムネイルが出る）。**録画が始まったと決まってから積む**
        // ── 手前の失敗経路（名前・保存先・SetState）で投げた場合に、
        // 生まれない録画への要求が残らない。
        _thumbnailRequests.Enqueue(filename);

        Components.ActivityLog.Info("recording.start", $"recorder='{Name}' file='{filename}'");
        // 障害を起こしたまま復帰していないソースがあれば、この機会に Ready→Playing で戻す。
        //
        // **実が流れているなら触らない。** RestartSinkSrc は Ready を経由する破壊的な
        // 操作（デバイスを閉じて開き直す）で、控えが残るのは「再開を試したが実が来なかった」
        // 回もある ── その窓（次の試行まで 10〜30 秒）にソースが自力で戻っていると、
        // ここで無条件に掛けるのは**動いているデバイスを落とす**ことになり、
        // 録画の頭がコマ落ちし、事前バッファも途切れる。
        if (RestartPolicy.SinkSampleGraceMs
            <= Environment.TickCount64 - System.Threading.Volatile.Read(ref _lastSinkSampleAt))
        {
            _ = RestartSinkSrc(out _);
        }
    }

    /// <summary><see cref="Start"/> の時刻（<c>recording.stop</c> の経過時間の算出用）。</summary>
    private long _recordingStartedAt;

    /// <summary><see cref="Start"/> の時刻（sidecar の <c>startTime</c>）。</summary>
    private DateTimeOffset _recordingStartedAtUtc;

    /// <summary>
    /// 走っている録画の開始理由（sidecar の <c>trigger</c>）。<see cref="StartCore"/> が控え、
    /// 停止の終わりで <see langword="null"/> へ戻す（sidecar を置かなかった回も降ろす）。
    /// 次の本の理由は <see cref="StartCore"/> が開始時に必ず上書きし、sidecar を書くのは
    /// その後なので、<b>この null 化は「録画していない間は空である」を保つための防御</b>である。
    /// </summary>
    private string? _startTrigger;

    /// <summary>
    /// 常時録画のセグメントを最初に見た時刻（sidecar の <c>startTime</c>）。
    /// 開始の通知（ストリーミングスレッド）と確定の報告（プールスレッド）が
    /// 別々に触るので並行辞書にする。<b>宿主のロックは取らない</b>
    /// ── 確定は <c>CloseCore</c> が <c>_stateLock</c> を持ったまま待つので、
    /// ここで <c>_stateLock</c> を取ると停止が固まる。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>
        _continuousSegmentStarts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// サムネイルを撮ってほしい録画ファイルのフルパス。
    ///
    /// <para>
    /// 積むのは録画ファイル名が決まった瞬間（<see cref="StartCore"/> と
    /// 常時録画のセグメント開始）で、取り出すのは<b>プレビュー枝のストリーミングスレッド</b>
    /// ── 別スレッドどうしなので並行キューにする。撮れるのは「要求の次に届いたフレーム」
    /// なので、事前バッファのぶん本編の先頭より後ろ（トリガ時点付近）になる。
    /// </para>
    /// </summary>
    private readonly ConcurrentQueue<string> _thumbnailRequests = new();

    /// <summary>
    /// 確定した appsrc の caps から採った映像の幅・高さ・フレームレート（分数）。
    /// <b>ストリーミングスレッドが書き、sidecar を書くスレッドが読む</b>ので volatile。
    /// 0 は「まだ確定していない」。
    /// </summary>
    private volatile int _capsWidth;

    /// <summary>確定した caps の高さ（<see cref="_capsWidth"/> と同じ規律）。</summary>
    private volatile int _capsHeight;

    /// <summary>確定した caps の <c>framerate</c> の分子（同上）。</summary>
    private volatile int _capsFramerateNumerator;

    /// <summary>確定した caps の <c>framerate</c> の分母（同上）。</summary>
    private volatile int _capsFramerateDenominator;

    /// <summary>
    /// この録画中に <c>appsink</c> から取り出せたサンプル数（<see cref="StartCore"/> で 0 に戻す）。
    /// <b>0 なら「エンコーダーから何も出て来なかった」</b> ── ソースが EOS で終わっている、
    /// あるいは sink パイプラインが止まっている。
    /// </summary>
    private int _samplesSeenWhileRecording;

    /// <summary>
    /// この録画中に実際に <c>appsrc</c> へ押し込めたバッファ数（<see cref="StartCore"/> で 0 に戻す）。
    /// <b>これが 0 なら MP4 には1フレームも入っていない。</b>
    /// <see cref="_samplesSeenWhileRecording"/> との比が原因を切り分ける ──
    /// 両方 0 ならサンプルが来ていない、見えているのに 0 なら I フレーム待ちで止まっている。
    /// </summary>
    private int _samplesPushed;

    /// <summary>
    /// 録画セッションの世代。<see cref="StartCore"/>（<c>_stateLock</c> 下）だけが増やし、
    /// sink の <c>appsink</c> コールバックは値の変化で「新しい録画が始まった」を検出して
    /// 事前バッファを押し込む。
    /// フラグ（<c>_IsRecording</c>）の false→true 遷移では代用できない ──
    /// 停止（排出完了まで）→開始がサンプル 1 枚の間隔以内に完了すると、
    /// コールバックは false のサンプルを一度も観測しないまま次のサンプルで true を見るため
    /// 遷移自体が消え、事前バッファの排出が走らず、前セッションの <c>StartPts</c> を
    /// 引き継いだ PTS で押し込んでしまう。
    /// </summary>
    private volatile int _recordingSession;

    /// <summary>
    /// <b>直近の停止がファイルとして何を残したか。</b>
    /// CLI（<c>stop-recording</c>）はこれを見て終了コードを決める ──
    /// <b>「終了コード 0 で使えないファイルを渡す」のが実害の本体</b>で、
    /// 呼び出し側のバッチは成否を終了コードで判定する前提になっている（両 README に記載）。
    ///
    /// <para>
    /// <b><see cref="RecordingStopOutcome.Empty"/> と
    /// <see cref="RecordingStopOutcome.NotFinalized"/> を分けることに意味がある。</b>
    /// 前者は中身が無いので<b>捨ててよい</b>が、後者は <c>mdat</c> にデータがある一方で
    /// <c>moov</c> が未確定なので<b>救済の余地がある</b> ── 呼び出し側の扱いが変わる。
    /// （終了コードを分ける基準は「再試行の可否」等、<b>呼び出し側の判断が変わるかどうか</b>
    /// である ── <c>src/README.md</c>「終了コードの一覧」の 2 と 5/6 の説明と同じ規則。）
    /// </para>
    /// <para>
    /// <b>これは「587 バイトの空 MP4 が残った」既知事象の原因を直したものではない。</b>
    /// 原因は未特定（再現待ち）で、これは<b>次に起きたときに黙って通り過ぎないようにする</b>
    /// 計測である。
    /// </para>
    /// </summary>
    public RecordingStopOutcome LastStopOutcome { get; private set; } = RecordingStopOutcome.Ok;

    /// <summary>
    /// 録画を停止し、排出（EOS → バス待ち → <c>SetState(Null)</c>）の完了を表す
    /// <see cref="System.Threading.Tasks.Task"/> を返す。<b>ファイルが確定するのはこのタスクの完了時点。</b>
    ///
    /// <para>
    /// <b><see cref="IsRecording"/> は呼び出しスレッドで同期的に倒す。</b>
    /// プールへ逃がすと、コマンドの実行可否（<c>CanStopRecording</c>）が
    /// 反転するまでに窓が開き、二重停止を弾けなくなる。
    /// 時間の掛かる排出だけをプールへ移す ── 呼び出しスレッドで排出まで行うと、
    /// 最大 <see cref="StopFinalizeTimeoutMs"/> ブロックしてしまう。
    /// </para>
    /// <para>
    /// <b>CLI はこのタスクを await すること。</b>
    /// <c>stop-recording X</c> の直後に <c>copy</c> するバッチが想定用途であり、
    /// コマンド復帰時に moov が確定している必要がある。
    /// </para>
    /// </summary>
    public System.Threading.Tasks.Task StopAsync()
    {
        lock (_stateLock)
        {
            // **早期 return より前に降ろす。** 作り直しのあいだは _IsRecording が false
            // なので、ここを下に置くと「抜けているあいだの停止」がどこにも届かず、
            // 復帰した瞬間に録画が勝手に再開する ── 利用者が止めた場合も、
            // UiaTrigger の停止条件が立った場合も同じである。
            CancelRecoveryResume("stop requested");

            if (!_IsRecording)
                return System.Threading.Tasks.Task.CompletedTask;

            long elapsedMs = _recordingStartedAt == 0 ? 0 : Environment.TickCount64 - _recordingStartedAt;
            IsRecording = _IsRecording = false;
            IsStopping = true;

            return _stopTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    StopDrainAndFinalize(elapsedMs);
                }
                catch (Exception ex)
                {
                    // fire-and-forget の Stop() から来た場合、再送出すると誰も観測しない
                    // 例外になる。結果は StopDrainAndFinalize の finally が
                    // `recording.stop ... result=error` として既に記録しているので、
                    // ここでは障害として1行足すだけにして畳む。
                    Components.ActivityLog.Error("recorder.error", $"recorder='{Name}' stop failed: {ex}");
                }
                finally
                {
                    IsStopping = false;
                }
            });
        }
    }

    /// <summary>
    /// 録画を停止する（完了を待たない）。UI のボタンなど、結果を待つ必要がない経路用。
    /// 完了を待つ必要がある経路は <see cref="StopAsync"/> / <see cref="StopAndWait"/> を使うこと。
    /// </summary>
    public void Stop() => _ = StopAsync();

    /// <summary>
    /// 録画を停止し、排出の完了を有界待ちする。時間内に終われば true。
    /// </summary>
    public bool StopAndWait(int timeoutMs) => StopAsync().Wait(timeoutMs);

    /// <summary>
    /// 進行中の停止（<see cref="StopAsync"/> がプールへ投げた排出）。
    /// <b>読み書きは必ず <c>_stateLock</c> の下で行う</b>ので volatile は不要。
    /// ロック外で読むと、書き込みが見えずに <see cref="WaitForPendingStop"/> が
    /// 素通りし、排出中のパイプラインを Dispose しうる。
    /// </summary>
    private System.Threading.Tasks.Task? _stopTask;

    /// <summary>
    /// 進行中の停止が終わるまで有界待ちする。状態遷移の入口
    /// （<see cref="Start"/> / <see cref="Close"/>、および <see cref="Initialize"/> から
    /// 呼ばれる <see cref="Close"/>）で、<b><c>_stateLock</c> を保持したまま</b>呼ぶ。
    ///
    /// <para>
    /// <b>排出タスクは <c>_stateLock</c> を取らない。</b> だからロックを保持したまま
    /// 待ってもデッドロックしない ── <c>CancelPendingRestart</c> が抱えているのと同じ
    /// 制約に見えるが、<b>あちらと違ってキャンセルして諦めることはできない</b>。
    /// 排出中のパイプラインを Dispose するとネイティブの二重解放になる。
    /// </para>
    /// <para>
    /// <b>それでもこの待ちは有界にする。</b> 「排出は <see cref="StopFinalizeTimeoutMs"/> で
    /// 有界だから必ず返る」は<b>成り立たない</b> ── 有界なのは
    /// EOS / Error の待ちだけで、<c>finally</c> の <c>SetState(State.Null)</c> は
    /// mux が詰まると無期限に掛かりうる（まさに <see cref="StopFinalizeTimeoutMs"/> が
    /// 存在する理由の状況）。無期限に待てば終了しなくなり、諦めて破棄すれば
    /// 使用中のオブジェクトを壊す。
    /// <b>そこで諦めた場合は破棄せず参照だけ落とす</b>（<see cref="CloseCore"/> の
    /// <c>abandonedStop</c>）── ネイティブリソースは漏れるが、クラッシュはしない。
    /// 漏れを選ぶのは、この状況が「プロセスがもう正常でない」ときにしか起きないため。
    /// </para>
    /// <returns>時間内に排出が完了すれば true。諦めた場合は false。</returns>
    /// </summary>
    private bool WaitForPendingStop()
    {
        System.Diagnostics.Debug.Assert(Monitor.IsEntered(_stateLock), "WaitForPendingStop must run under _stateLock");

        var pending = _stopTask;
        if (pending is null || pending.IsCompleted)
            return true;

        // 排出の上限 + SetState(Null) と後始末の余裕。
        if (pending.Wait(Math.Max(0, StopFinalizeTimeoutMs) + StopFinalizeSlackMs))
            return true;

        Components.ActivityLog.Warn("recording.stop slow",
            $"recorder='{Name}' the pending stop did not finish within "
            + $"{StopFinalizeTimeoutMs + StopFinalizeSlackMs}ms; abandoning the src pipeline without disposing it");
        return false;
    }

    /// <summary>
    /// 排出待ちの既定上限(ms)。ランチャーの結果待ちは 60 秒なので、
    /// これをそれに近づけると <c>stop-recording</c> が終了コード 2 を返し始める。
    /// 目安は 50000 以下。
    /// </summary>
    public const int DefaultStopFinalizeTimeoutMs = 5000;

    /// <summary>
    /// <see cref="WaitForPendingStop"/> が排出の上限に上乗せする余裕(ms)
    /// （<c>SetState(Null)</c> と後始末のぶん）。
    /// <b>実際に停止コマンドが専有しうるのは <c>StopFinalizeTimeoutMs</c> + この値。</b>
    /// </summary>
    public const int StopFinalizeSlackMs = 5000;

    /// <summary>
    /// <b>利用者に案内している <see cref="StopFinalizeTimeoutMs"/> の上限(ms)。</b>
    /// resw（<c>PropDesc_StopFinalizeTimeout</c>・en/ja）と <c>src/README.md</c> に
    /// 同じ数字が書いてある。
    ///
    /// <para>
    /// <b>これは独立に決めてよい値ではない。</b> ランチャーの結果待ち
    /// （<c>SingleInstanceManager.WorkerAcceptTimeoutMs</c>）から逆算したもので、
    /// <c>この値 + <see cref="StopFinalizeSlackMs"/> &lt; 結果待ち</c> が崩れると、
    /// <b>案内どおりに設定した利用者の <c>stop-recording</c> が終了コード 2 を返し始める</b>
    /// ── 設定画面が「50000 以下にしてください」と言いながら、その値で壊れる状態になる。
    /// 関係は <c>StopFinalizeBudgetTests</c>（L1）が機械的に守っている。
    /// </para>
    /// </summary>
    public const int MaxAdvisedStopFinalizeTimeoutMs = 50_000;

    /// <summary>
    /// 排出待ちの上限(ms)。アプリ層（<c>AppSettings.StopFinalizeTimeoutMs</c>）から設定する
    /// static ミラー（<c>GStreamer.GstSharpNet</c> は <c>AppSettings</c> を知らない設計のため、
    /// <see cref="PreferredH264Encoder"/> と同じ方式）。
    /// </summary>
    public static int StopFinalizeTimeoutMs { get; set; } = DefaultStopFinalizeTimeoutMs;

    /// <summary>
    /// EOS を送って src パイプラインを排出し、ファイルを確定させる。
    /// 呼び出し側で <c>_IsRecording</c> を false にしてから呼ぶこと。
    ///
    /// <para>
    /// <b>排出待ちは有界。</b> 無限待ちにすると、mux が詰まったとき
    /// 呼び出しスレッド（UI スレッドや CLI 経路）ごと永久にハングする。
    /// タイムアウトしても <c>SetState(Null)</c> は必ず実行し、結果を <c>result=</c> に残す。
    /// </para>
    /// <para>
    /// <b>待ちは同期の <c>Wait</c> にする（<c>await</c> にしない）。</b>
    /// 完了させるのは src バスの同期ハンドラ＝当のパイプラインの
    /// ストリーミングスレッドなので、継続がそこでインライン実行されると
    /// <c>finally</c> の <c>SetState(Null)</c> が自スレッドで走って固まる。
    /// </para>
    /// </summary>
    private void StopDrainAndFinalize(long elapsedMs)
    {
        // **武装は EndOfStream() より前。** 後にすると、EOS より先に届く Error を取りこぼす。
        var drain = new System.Threading.Tasks.TaskCompletionSource<StopDrainResult>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_busLock)
            _stopDrain = drain;

        string result = "ok";

        // **src パイプラインの状態は EOS を送る「前」に、待たずに読む。**
        // このパイプラインは filesink がプリロールするまで PLAYING に到達しないので、
        // 「PAUSED のまま pending=PLAYING」＝**1バイトも下流へ通っていない**ことの直接の証拠になる
        // （「587 バイトの空 MP4」既知事象で立てた仮説そのもの）。
        //
        // **待たないことが要点。** 到達待ちを録画開始側に置くと、
        // StartRecordingAll が UI スレッドで直列に回すため
        // 「レコーダー数 × 上限」ぶん UI が固まる ── E2E で「UIA の要素が 0 件」を
        // 引き起こす当のパターン。ここは停止時の観測に留める。
        string srcState = ReadSrcPipelineStateForLog();
        try
        {
            _appSrc?.EndOfStream();

            StopDrainResult drained = drain.Task.Wait(Math.Max(0, StopFinalizeTimeoutMs))
                ? drain.Task.Result
                : new(StopDrainSignal.Timeout, null, null);
            StopDrainSignal signal = drained.Signal;

            if (signal == StopDrainSignal.Timeout)
            {
                // mux が詰まった／EOS が返らない。ファイルは未確定（moov が書かれていない）
                // 可能性が高いので、その事実を残す。
                string detail = $"recorder='{Name}' file='{LastFilename}' "
                    + $"the source pipeline did not drain within {StopFinalizeTimeoutMs}ms; the file may be incomplete";
                Components.ActivityLog.Error("recording.stop timeout", detail);
                LastError = detail;
                try { ErrorOccurred?.Invoke(this, detail); } catch { }
            }
            else if (signal == StopDrainSignal.Error)
            {
                string detail = $"recorder='{Name}' file='{LastFilename}' {drained.ErrorMessage} debug={drained.Debug}";
                Components.ActivityLog.Error("recording.stop error", detail);
                LastError = detail;
            }

            // **1フレームも書けていないなら、成功として返さない。**
            // 排出そのものは綺麗に終わるので（EOS は返る）、ここを見ないと
            // 「終了コード 0・result=ok・中身の無い MP4」になる ── 「587 バイトの空 MP4」の症状。
            int seen = System.Threading.Volatile.Read(ref _samplesSeenWhileRecording);
            int pushed = System.Threading.Volatile.Read(ref _samplesPushed);
            if (pushed == 0)
            {
                // 切り分けの材料をそのまま残す（原因は未特定なので、次の1件で決められるように）。
                //   seen=0            → サンプルが一度も来ていない（ソースが EOS / sink が停止）
                //   seen>0 かつ pushed=0 → 来ているのに I フレーム待ちで止まっている
                //   srcState が PLAYING 以外 → 下流（h264parse / mp4mux / filesink）へ通っていない
                string detail = $"recorder='{Name}' file='{LastFilename}' "
                    + $"samplesSeen={seen} samplesPushed=0 srcState={srcState} "
                    + "no frame was ever muxed, so the file has no media data";
                Components.ActivityLog.Error("recording.stop empty", detail);
                LastError = detail;
                try { ErrorOccurred?.Invoke(this, detail); } catch { }
            }

            // result= と呼び出し側へ返す結果の決定は StopDrainRules（純粋関数。L1 が守る）。
            // 「空」で「未確定」を潰さない優先順位もそちらにある。
            (result, var outcome) = StopDrainRules.Classify(signal, pushed);
            LastStopOutcome = outcome;

            // **確定できた本だけに sidecar を置く。** 排出が時間切れ・エラーで終わった本は
            // moov が書かれていない可能性があり、そこへ sidecar を置くと索引が
            // 「開いて確かめなくてよい」と読んで録画中のものと区別できなくなる
            // （常時録画の OnContinuousSegmentFinalized と同じ規律）。
            if (result == "ok")
            {
                WriteSidecarInBackground(
                    LastFilename, _recordingStartedAtUtc, DateTimeOffset.UtcNow, trigger: _startTrigger);
            }

            // 理由は 1 本ぶんのもの。書いた後（sidecar を置かなかった回も）で降ろす。
            _startTrigger = null;
        }
        catch
        {
            // 例外で抜けた場合に result を "ok" のまま出さない。
            // `app.exit exitCode=0` が例外を握り潰した終了と区別できなかったのと同じ罠。
            //
            // **Outcome も一緒に倒す。** 排出の途中で例外が出たなら、ファイルが確定した
            // 保証はどこにも無い ── ここを忘れると「例外が出たのに終了コード 0」になる。
            result = "error";
            LastStopOutcome = RecordingStopOutcome.NotFinalized;

            // sidecar は書かない ── 未確定（NotFinalized）と決めた本に置くと、
            // 索引が「排出済み」と読む。
            throw;
        }
        finally
        {
            // 武装を解く。以後 src バスの Error は通常どおり recorder.error として記録され、
            // Eos は捨てられる。
            lock (_busLock)
                _stopDrain = null;

            // タイムアウトでもエラーでも必ず Null へ落とす（要素を再利用可能な状態にする）
            System.Diagnostics.Debug.Assert(!Monitor.IsEntered(_busLock), "SetState must not run under _busLock");
            _srcPipeline?.SetState(State.Null);
            FlushThrottles(_srcThrottles, "src");
            Components.ActivityLog.Info("recording.stop",
                $"recorder='{Name}' file='{LastFilename}' elapsedMs={elapsedMs} result={result}"
                + $" samplesPushed={System.Threading.Volatile.Read(ref _samplesPushed)}");
        }
    }

    /// <summary>
    /// src パイプラインの状態を<b>待たずに</b>読み、ログ用の1語にする。
    ///
    /// <para>
    /// <c>GetState</c> にタイムアウト 0 を渡すと、遷移が終わっていなければ
    /// <c>Async</c> と<b>その時点の state / pending</b> が返る。
    /// <c>Paused</c> のまま <c>pending=Playing</c> なら
    /// <b>filesink が一度もプリロールしていない</b>＝下流へ何も通っていない、と読める。
    /// </para>
    /// <para>
    /// 例外は握り潰して <c>unknown</c> を返す ── ここは診断のための1語であって、
    /// <b>これで停止処理を失敗させてはいけない</b>（ファイルの確定より優先されるものは無い）。
    /// </para>
    /// </summary>
    private string ReadSrcPipelineStateForLog()
    {
        try
        {
            var pipeline = _srcPipeline;
            if (pipeline is null)
                return "none";

            var ret = pipeline.GetState(out var state, out var pending, ClockTime.Zero);
            return ret == StateChangeReturn.Async ? $"{state}->{pending}" : state.ToString();
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>常時録画のセグメントに付ける <c>trigger</c>。</summary>
    private const string ContinuousTrigger = "continuous";

    /// <summary>
    /// 録画 1 本の sidecar（<c>&lt;録画ファイル名&gt;.json</c>）を<b>スレッドプールで</b>書く。
    ///
    /// <para>
    /// <b>録画パイプラインには影響させない。</b> 呼ぶのは排出が終わった後で、
    /// 書き込みそのものは別スレッド、失敗はログだけ ── sidecar は
    /// 「無くても動く」補助情報である（読む側はファイル名と <c>moov</c> から推定できる）。
    /// </para>
    /// <para>
    /// <b>既に在るものは上書きしない。</b> 録画の完了時に一度だけ書く形。
    /// </para>
    /// <para>
    /// <b>不変条件: sidecar 在り＝確定済み（moov 書込済み）。</b> 呼び出し側は排出が
    /// <c>ok</c> で終わったときだけ呼ぶこと ── 索引（<c>RecordingIndex</c>）は
    /// sidecar の有無で「開いて録画中か確かめる」を省く。
    /// </para>
    /// </summary>
    private void WriteSidecarInBackground(
        string? recordingPath, DateTimeOffset startTime, DateTimeOffset endTime, string? trigger)
    {
        if (string.IsNullOrEmpty(recordingPath))
            return;

        string path = recordingPath;
        string recorder = Name;
        int width = _capsWidth;
        int height = _capsHeight;
        int framerateNumerator = _capsFramerateNumerator;
        int framerateDenominator = _capsFramerateDenominator;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (!File.Exists(path))
                    return;

                string sidecarPath = Components.RecordingSidecar.PathFor(path);
                if (File.Exists(sidecarPath))
                    return;

                long durationMs = (long)(endTime - startTime).TotalMilliseconds;

                Components.RecordingSidecar.Write(sidecarPath, new Components.RecordingSidecar(
                    Components.RecordingSidecar.CurrentVersion,
                    recorder,
                    startTime,
                    endTime,
                    durationMs < 0 ? null : durationMs,
                    trigger,
                    0 < width ? width : null,
                    0 < height ? height : null,
                    0 < framerateNumerator && 0 < framerateDenominator
                        ? framerateNumerator / (double)framerateDenominator
                        : null));
            }
            catch (Exception ex)
            {
                Log(DebugLevel.Warning, $"failed to write the recording sidecar for '{path}': {ex.Message}");
            }
        });
    }

    /// <summary>サムネイルの出力幅の上限（一覧の 1 枚として要る大きさ）。</summary>
    private const int ThumbnailMaxWidth = 320;

    /// <summary>
    /// 溜まっているサムネイルの要求を、いま届いたプレビュー枝のフレーム 1 枚で満たす。
    ///
    /// <para>
    /// <b>ストリーミングスレッドで走る。</b> 変換と縮小は map の中で終える
    /// （生バイト列を持ち出して複製しない）。書き込みだけをスレッドプールへ逃がす。
    /// </para>
    /// <para>
    /// <b>best-effort。</b> caps が読めない・対応しない画素形式は要求を捨てて
    /// ログ 1 行で終わる（リトライしない ── 次のフレームでも結果は同じ）。
    /// 例外はすべてここで握る（録画へ波及させない）。
    /// </para>
    /// </summary>
    private void CaptureThumbnail(Sample sample)
    {
        try
        {
            string? format;
            int width;
            int height;

            // Caps は MiniObject、GetStructure(0) は Boxed の所有コピー ── どちらも解放する。
            using (var caps = sample.GetCaps())
            {
                using var structure = caps?.GetStructure(0);
                format = structure?.GetString("format");

                if (structure is null
                    || format is null
                    || !structure.GetInt("width", out width)
                    || !structure.GetInt("height", out height))
                {
                    DropThumbnailRequests();
                    Log(DebugLevel.Warning, "thumbnail.capture no caps");
                    return;
                }
            }

            Components.ThumbnailImage? image;
            using (var buffer = sample.GetBuffer())
            {
                if (buffer is null)
                {
                    DropThumbnailRequests();
                    Log(DebugLevel.Warning, "thumbnail.capture no buffer");
                    return;
                }

                using var map = buffer.Map(MapFlags.Read);
                if (!Components.ThumbnailImage.TryCreate(
                        map.Span, format, width, height, ThumbnailMaxWidth, out image)
                    || image is null)
                {
                    DropThumbnailRequests();
                    Log(DebugLevel.Warning, $"thumbnail.unsupported format={format} {width}x{height}");
                    return;
                }
            }

            // 1 枚を全員で共有する（不変な record なので複製は要らない）。
            Components.ThumbnailImage picture = image;

            while (_thumbnailRequests.TryDequeue(out string? path))
            {
                // **反復ごとに束縛し直す。** out 変数はループの外側の 1 つなので、
                // そのまま捕まえると全部の書き込みが最後のパスを見る。
                string target = path;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Components.RecordingSidecar.WriteThumbnail(target, picture);
                        Log(DebugLevel.Debug, $"thumbnail.written path={target}");
                    }
                    catch (Exception ex)
                    {
                        Log(DebugLevel.Warning, $"thumbnail.write failed: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            // **例外経路でも要求は捨てる。** 残すと、投げる原因（map できない buffer 等）が
            // 続く限りフレームごとに再試行とログが出る。撮れなかった 1 回で諦める。
            DropThumbnailRequests();
            Log(DebugLevel.Warning, $"thumbnail.capture failed: {ex.Message}");
        }
    }

    /// <summary>撮れないと分かった要求を捨てる（次のフレームで撮り直さない）。</summary>
    private void DropThumbnailRequests()
    {
        while (_thumbnailRequests.TryDequeue(out _))
        {
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    protected static void Log(DebugLevel level, string message,
        GObject.Object? @object = null,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string function = "")
        => DebugLogEx.Log(level, message, @object, file, line, function);


    // ファイナライザは定義しない。アンマネージドリソースは GStreamer 側のマネージドラッパー
    // （Pipeline / AppSink 等）が自身のファイナライザで解放するため、本クラスの Dispose(false) は
    // 何もすることがなく、ファイナライズキューに載せるコストだけが残るため。
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <b>volatile は必須</b> ── 立てるのは <see cref="Dispose(bool)"/>（UI/CLI スレッド）で、
    /// 読むのは復帰の連鎖（プールスレッド。<c>RestartLoopAsync</c> と
    /// <see cref="TryScheduleDeviceRebuild"/>）である。ロックを介さないので、
    /// volatile が無いと破棄済みの検査が見えないまま作り直しへ進みうる。
    /// </summary>
    private volatile bool _disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        // 破棄されるレコーダーを復帰の連鎖が録り直さないように、意図を先に降ろす。
        // **ミラーも一緒に降ろす** ── 残すと、まだ購読している VM が
        // 「録画中」を出したまま（ShowsAsRecording）になり、停止を押しても
        // CancelRecoveryResume が空振りするので二度と降りない。
        _resumeAfterRecovery = false;
        _resumeTrigger = null;
        IsAwaitingRecoveryResume = false;

        if (!_disposedValue)
        {
            // フラグは Close() より**前**に立てる。後に立てると、Close() の完了から
            // フラグが立つまでの間に _stateLock を取った復帰エスカレーションの
            // Initialize() が InitializeCore の Dispose 済み検査を素通りし、
            // 破棄済みのレコーダーを蘇生させる窓が残る。
            _disposedValue = true;

            if (disposing)
            {
                Close();
                _currentSettings?.PropertyChanged -= Settings_PropertyChanged;
            }
        }
    }

    public override string ToString() => Name;
}

public class PreviewEventArgs(Sample sample) : EventArgs
{
    public Sample Sample { get; init; } = sample;
}

public partial class GstEventRecorderCollection : ObservableCollection<EventRecorder>
{

}