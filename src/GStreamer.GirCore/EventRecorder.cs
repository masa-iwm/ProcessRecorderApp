using CommunityToolkit.Mvvm.ComponentModel;
using Gst;
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
    private Bus? _srcBus;
    private GstApp.AppSrc? _appSrc;
    private Element? _mux;
    private Element? _file;

    /// <summary>常時録画の枝の終端 appsink（sink パイプラインの一部）。</summary>
    private GstApp.AppSink? _continuousSink;

    /// <summary>常時録画エンジン。常時録画が無効か、枝を組めなかった場合は null。</summary>
    private ContinuousRecorder? _continuous;

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
    /// 録画中フラグ（<c>_pullSampleThread</c> が毎周回で読む）。
    /// <see cref="IsRecording"/> は UI 通知用のミラーで、こちらが実体。
    /// <b>volatile は必須</b> ── 書くのは UI スレッド（<see cref="Start"/> /
    /// <see cref="Stop"/>）、読むのは専用スレッドで、ロックを介さない。
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
    /// 直近に検出した障害（バスの Error / Warning、停止のタイムアウト等）。正常時は <see langword="null"/>。
    ///
    /// <para>
    /// バスの Error を**パースもログもせず捨てる**と、障害はどこにも現れない。
    /// ユーザーから見える唯一の症状が「録画ファイルが壊れている」になり、
    /// 原因の切り分けができなくなる。
    /// </para>
    /// <para>
    /// <b>このプロパティは専用スレッドから変更される。</b> 購読側（VM）は
    /// UI スレッドへマーシャリングすること ── <c>GstEventRecorderViewModel.Model_PropertyChanged</c>
    /// がそれを行っている。
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
    /// <b><c>PullSampleProc</c> / <c>PullPreviewProc</c> のループはこのロックを取らない。</b>
    /// <see cref="Close"/> はロックを保持したまま <c>Join(5000)</c> するため、
    /// pull ループが同じロックを待つとデッドロックする。
    /// pull 側は volatile フィールド（<see cref="_isAlive"/> / <see cref="_IsRecording"/>）の
    /// 読みだけで回すこと。
    /// </para>
    /// <para>
    /// <b><see cref="StopAsync"/> がプールへ投げる排出タスクもこのロックを取らない。</b>
    /// <see cref="Close"/> はロックを保持したままその完了を待つ（<see cref="WaitForPendingStop"/>）
    /// ため、排出側がロックを取るとデッドロックする。排出が触るのはネイティブの
    /// パイプライン・バスと <c>ActivityLog</c> だけに留めること。
    /// </para>
    /// </summary>
    private readonly object _stateLock = new();

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
    /// <c>GStreamer.GirCore</c> は <c>AppSettings</c> を知らない設計なので、
    /// <c>EventRecorder.TemplateVariables</c> と同じく static のミラーとして受け取る。
    /// </summary>
    public static string? PreferredH264Encoder { get; set; }

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
                    // **capsfilter を後ろに付けてもいけない。** 形式を決めずに
                    // 交渉させることが、まさにこの不具合を直している点である。
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
                                videoconvert ! {encoder}
                                """",
                    EventRecordingType.D3d12 => $""""
                                d3d12upload ! d3d12convert ! video/x-raw(memory:D3D12Memory), format=NV12{pinned} !
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
    /// パイプラインを構築して常時バッファリングを開始する。
    /// 状態遷移（<see cref="Initialize"/>/<see cref="Start"/>/<see cref="Stop"/>/<see cref="Close"/>）は
    /// <see cref="_stateLock"/> で直列化する。ロックは再入可能（同一スレッドの
    /// <c>Initialize → Close</c> は同じロックを取り直す）。
    /// </summary>
    public void Initialize()
    {
        lock (_stateLock)
            InitializeCore();
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
    /// ── その間に届いた CLI コマンドは <c>ActivationCommands.ReadyWaitTimeout</c> しか待たない。
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
            H264EncoderDef? continuousEncoder = withContinuous ? ResolveContinuousEncoder() : null;
            string continuousBranch = "";
            string? droppedOverride = null;
            string pinnedResolution = "";
            if (continuousEncoder is not null)
            {
                var plan = ContinuousBranch.Plan(
                    Type, continuousEncoder.LaunchString, continuousEncoder.NeedsSystemMemory,
                    ContinuousFramerate, ContinuousResolution,
                    ContinuousBranch.SourceSizeIsPinned(SrcPipeline),
                    ContinuousBranch.SourceFramerateIsPinned(SrcPipeline));
                continuousBranch = plan.Branch;
                droppedOverride = plan.DroppedOverride;
                // 枝の中で拡縮するなら、tee の手前も同じ大きさで固定しないと
                // 手前の変換が枝の要求を吸収して本線まで縮む。
                if (plan.AppliesResolution)
                    pinnedResolution = ContinuousBranch.SourceSize(SrcPipeline);
            }

            string SinkPipelineStr =
                BuildSinkPipeline(Type, SrcPipeline, encoder.LaunchString, encoder.NeedsSystemMemory,
                    continuousBranch, pinnedResolution);

            // 失敗したときに「何を組もうとしたのか」を残す。リンク失敗のメッセージは
            // 要素名しか言わないので、これが無いと caps の書き方の誤りを机上で追えない。
            _lastAttemptedSinkPipeline = SinkPipelineStr;
            const string SrcPipelineStr =
                "appsrc format=time name=src ! h264parse ! mp4mux faststart=true name=mux ! filesink name=file";

            _sinkPipeline = (Pipeline)Functions.ParseLaunch(SinkPipelineStr);
            _sinkPipeline.Name = "event-recorder-sink-pipeline";
            _sinkBus = _sinkPipeline.GetBus();
            _appSink = (GstApp.AppSink)_sinkPipeline.GetByName("sink")!;
            _previewSink = (GstApp.AppSink)_sinkPipeline.GetByName("preview")!;
            _srcPipeline = (Pipeline)Functions.ParseLaunch(SrcPipelineStr);
            _srcPipeline.Name = "event-recorder-src-pipeline";
            _srcBus = _srcPipeline.GetBus();
            _appSrc = (GstApp.AppSrc)_srcPipeline.GetByName("src")!;
            _mux = _srcPipeline.GetByName("mux")!;
            _file = _srcPipeline.GetByName("file")!;
#if false
        _sinkBus.OnSyncMessage += OnBusSyncMessage;
        _sinkBus.OnMessage += OnBusMessage;
        _srcBus.OnSyncMessage += OnBusSyncMessage;
        _srcBus.OnMessage += OnBusMessage;
#endif

            if (_sinkPipeline.SetState(State.Playing) == StateChangeReturn.Failure)
                throw new InvalidOperationException("ERROR: pipeline doesn't want to play.");

            WaitUntilPlaying(_sinkPipeline);

            // appsrc のキャップスはここでは設定しない。_appSink.GetCaps() は appsink に
            // 設定された（テンプレート由来の、しばしば ANY な）キャップスであって、
            // 実際にネゴシエートされた結果ではないため。
            // PullSampleProc が最初のサンプルの sample.GetCaps() から設定する。

            _isAlive = true;

            _pullSampleThread = new(PullSampleProc)
            {
                IsBackground = true
            };

            _pullPreviewThread = new(PullPreviewProc)
            {
                IsBackground = true
            };

            _pullPreviewThread?.Start();
            _pullSampleThread?.Start();

            ActualType = Type;
            ActualSrcPipeline = SrcPipeline;
            // 「実際に動いたエンコーダー」を読み取り専用プロパティへ出す。
            // 自動選択・フォールバックが起きた場合、UI 改修なしでその結果が見える。
            ActualEncodingProperties = encoder.LaunchString;

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
    /// <c>.dot</c> の保存先（<c>AppSettings.GstDebugDumpDotDir</c> の static ミラー）。
    /// 空なら書かない。<c>GStreamer.GirCore</c> は <c>AppSettings</c> を知らない設計なので、
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
            owner.IsContinuousRecording = running;
            owner.ContinuousLastFilename = currentFile;
            owner.ContinuousSegmentCount = segmentCount;
        }

        public void OnContinuousError(string message) => owner.ContinuousLastError = message;
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
            out var state, out var pending, (ulong)PlayingStateTimeoutMs * 1_000_000UL);

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

            // 常時録画のスレッドが降りるまで sink 側を壊してはいけない（cont appsink を読んでいる）。
            bool continuousStopped = continuousClose is null
                || continuousClose.Wait(StopFinalizeTimeoutMs + StopFinalizeSlackMs);

            _isAlive = false;
            bool pullStopped = _pullPreviewThread?.Join(5000) ?? true;
            pullStopped &= _pullSampleThread?.Join(5000) ?? true;
            pullStopped &= continuousStopped;
            _pullPreviewThread = null;
            _pullSampleThread = null;

            // pull スレッドが止まるまでの間も DrainBuses はバスを汲んでいるので、
            // teardown 中のエラーが ScheduleRestart で新しい復帰を積んでいることがある
            // ── 上の CancelPendingRestart はそれを知らない。Join の後にもう一度畳む。
            // 畳み残すと、直後の再初期化（Initialize は必ず Close を先に呼ぶ）で
            // _isAlive が立ち直った後に、旧セッション由来の復帰が走り出す。
            CancelPendingRestart();

            // リングバッファは**必ず空にする**（このインスタンスは次のセッションへ引き継がれる）。
            // 残すと前セッションの映像が次の事前バッファの先頭に混ざり、保持バイト数も
            // 持ち越されてサイズ基準の退避予算が狂う。
            // 解放するかどうかだけが pull の停止しだい ── 降りていないスレッドが
            // まだ要素を触っている可能性があるので、その場合は参照を落とすに留める
            // （パイプラインと同じ「リークするが落ちはしない」規律）。
            foreach (var b in _ringBuffer.DrainAll())
            {
                if (pullStopped)
                    b.Dispose();
            }

            // src パイプライン側
            if (abandonedStop || !pullStopped)
            {
                // 排出タスク（abandonedStop）または pull スレッドがまだ
                // _srcPipeline / _appSrc / _srcBus を触っている可能性がある。
                // ここで Dispose すると使用中のネイティブオブジェクトを壊す（＝クラッシュ）。
                // 参照だけ落として解放は諦める ── リークするが落ちはしない
                // （排出の abandonedStop と同じ規律を pull スレッド側にも適用する）。
                Components.ActivityLog.Warn("recorder.leak",
                    $"recorder='{Name}' " + (abandonedStop
                        ? "the src pipeline was still draining; leaked instead of disposed"
                        : "the pull threads did not stop in time; leaked the pipelines instead of disposing them"));
                _file = null;
                _mux = null;
                _appSrc = null;
                _srcBus = null;
                _srcPipeline = null;
                _errorSinkSrc = null;
            }
            else
            {
                _srcPipeline?.SetState(State.Null);
                _file?.Dispose();
                _file = null;
                _mux?.Dispose();
                _mux = null;
                _appSrc?.Dispose();
                _appSrc = null;
                _srcBus?.Dispose();
                _srcBus = null;
                _srcPipeline?.Dispose();
                _srcPipeline = null;
                _errorSinkSrc = null;
            }

            // sink パイプライン側（_previewSink は sink 側の要素なのでこちらで解放する）
            //
            // **SetState(Null) は pull が降りていなくても必ず実行する。** これは appsink を
            // フラッシュして、孤児スレッドがブロックしている TryPullSample を返させる
            // 唯一の信号であり、止めなければキャプチャとエンコードが誰にも管理されないまま
            // 走り続ける。状態遷移はスレッドセーフで、Dispose とは別物。
            _sinkPipeline?.SetState(State.Null);
            if (!pullStopped)
            {
                // TryPullSample / DrainBus が sink 側ネイティブをまだ触っている可能性がある
                // ので、Dispose はせず参照だけ落とす（recorder.leak は上で記録済み）。
                _previewSink = null;
                _appSink = null;
                _continuousSink = null;
                _sinkBus = null;
                _sinkPipeline = null;
            }
            else
            {
                _previewSink?.Dispose();
                _previewSink = null;
                _appSink?.Dispose();
                _appSink = null;
                _continuousSink?.Dispose();
                _continuousSink = null;
                _sinkBus?.Dispose();
                _sinkBus = null;
                _sinkPipeline?.Dispose();
                _sinkPipeline = null;
            }
        }
        finally
        {
            _closing = false;
        }
    }

    /// <summary>Close() の再入防止フラグ。</summary>
    private bool _closing;


    /// <summary>
    /// pull スレッドの継続条件。<b>volatile は必須</b> ── <see cref="Close"/> はこれを
    /// false にしてから <c>Join(5000)</c> する。可視性が保証されないと Join が
    /// タイムアウトするまで返らず、破棄が 10 秒（2スレッド分）遅れる。
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


    private Thread? _pullSampleThread;

    private void PullSampleProc()
    {
        int lastSeenSession = _recordingSession;
        ulong startPts = 0;
        bool isIframeFound = false;

        // src パイプライン（appsrc ! h264parse ! mp4mux ! filesink）の appsrc にキャップスを
        // 設定したか。未設定だと h264parse は H.264 エレメンタリストリームの框組み
        // （stream-format / alignment）を typefind で推測するしかない。
        // 推測が外れると全 NAL が "broken/invalid nal ... will be dropped" として捨てられ、
        // **エラーにはならないまま**中身の無い MP4 が出来上がる（実機の nvh264enc で観測）。
        // sink 側で実際にネゴシエートされたキャップスを渡して推測をやめさせる。
        bool appSrcCapsSet = false;
        void PushRecordBuffer(ulong bufferPts, Gst.Buffer buffer)
        {
            if (!isIframeFound)
            {
                if (buffer.HasFlags(BufferFlags.DeltaUnit))
                    return;
                else
                {
                    startPts = bufferPts;
                    isIframeFound = true;
                }
            }
            // PTS の巻き戻り（ソースの再起動）。符号なし減算のまま押し込むと約 2^64 ns 級の
            // PTS が mux へ渡り、当該録画のタイムスタンプが壊れる ── 退避側
            // （RecordingRingBuffer）と同じ「巻き戻りは起こる」前提を押し込み側にも適用し、
            // 次の I フレームから始点を取り直す。
            if (bufferPts < startPts)
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
                isIframeFound = false;
                return;
            }
            using MiniObject miniObject = new(Gst.Internal.MiniObjectOwnedHandle.FromUnowned(((GLib.BoxedRecord)buffer).GetHandle()));
            using MiniObject writableMiniObject = miniObject.MakeWritable()!;
            Gst.Buffer buf = new(Gst.Internal.BufferOwnedHandle.FromUnowned(((GLib.BoxedRecord)writableMiniObject)!.GetHandle()));
            buf.Handle.SetPts(bufferPts - startPts);
            buf.Handle.SetDts(Constants.CLOCK_TIME_NONE);
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
        while (_isAlive)
        {
            try
            {
                DrainBuses();

                using var sample = _appSink?.TryPullSample(100_000_000); // 100 ms
                if (sample is null)
                    continue;

                // 最初のサンプルで appsrc のキャップスを確定させる。
                // ここで設定するのは「バッファが1つも流れる前」に src パイプラインを
                // 構成しておくため（録画開始時はリングバッファの古いバッファから押し込むが、
                // H.264 エレメンタリストリームで解像度も不変なのでキャップスは同一）。
                // 途中で SetCaps すると下流の再ネゴシエーションが起きるので一度だけ。
                if (!appSrcCapsSet)
                {
                    var negotiated = sample.GetCaps();
                    if (negotiated is not null)
                    {
                        _appSrc?.SetCaps(negotiated);
                        appSrcCapsSet = true;
                        // GirCore の Gst.Caps は文字列化 API を公開していないため、構造体名だけを出す
                        // （実際のキャップス全体は GST_DEBUG のネゴシエーションログに出る）。
                        Log(DebugLevel.Info,
                            $"appsrc caps set from the negotiated sink caps ({negotiated.GetStructure(0)?.GetName() ?? "?"})");
                    }
                }

                using var buffer = sample.GetBuffer();
                if (buffer is null)
                    continue;

                var pts = buffer.Handle.GetPts();
                if (pts == Constants.CLOCK_TIME_NONE)
                    pts = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000);

                var copy = buffer.Copy()!;
                _ringBuffer.Enqueue(pts, copy, copy.GetSize());

                // 退避条件（時間・サイズの2本立て）と「直近の1件は必ず残す」ガードは
                // RecordingRingBuffer.Evict にあり、L1 が守っている。
                // 退避したバッファの解放（ネイティブ）はここで行う。
                ulong bufferDurationNs = (ulong)Math.Max(0, BufferDuration) * 1_000_000UL;
                foreach (var evicted in _ringBuffer.Evict(pts, bufferDurationNs, MaxRingBufferBytes))
                    evicted.Dispose();

                if (_IsRecording)
                {
                    // **「サンプルが見えた」はここで数える。** この行に到達している＝
                    // TryPullSample が実を返している＝エンコーダーが動いている。
                    // 逆に、ソースが EOS で終わっていると上の `sample is null` で
                    // continue し続けるので、**この下の事前バッファの排出も一度も走らない**
                    // ── 押し込みが 0 のまま録画が「成功」する経路（「587 バイトの空 MP4」の症状）。
                    System.Threading.Interlocked.Increment(ref _samplesSeenWhileRecording);

                    int session = _recordingSession;
                    if (session != lastSeenSession)
                    {
                        // 新しい録画セッションの最初の周回。前セッションの I フレーム検出と
                        // startPts を持ち越さず、リングバッファ全体（今 Enqueue した copy を
                        // 含む）を押し込む。フラグの false→true 遷移ではなく世代で検出する
                        // 理由は _recordingSession の doc を参照。
                        lastSeenSession = session;
                        isIframeFound = false;
                        foreach (var (bufferPts, b) in _ringBuffer)
                            PushRecordBuffer(bufferPts, b);
                    }
                    else
                        PushRecordBuffer(pts, copy);
                }
                else
                    isIframeFound = false;
            }
            catch (Exception ex)
            {
                Log(DebugLevel.Error, $"GstEventRecorder.PullSampleProc failed!\n{ex}", _sinkPipeline);
            }
        }

        // 抑制されたまま残っている件数を取りこぼさない
        FlushThrottles(_sinkThrottles, "sink");
        FlushThrottles(_srcThrottles, "src");
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
    /// 両方のバスに溜まったメッセージを**空になるまで**取り出して記録する。
    ///
    /// <para>
    /// <b>1周につき1件ではなく汲み切る。</b> GstBus のキューは既定で無制限なので、
    /// 洪水（<c>h264parse</c> が捨てた NAL ごとに Warning を出す等）の最中に
    /// 1周1件だと 10 件/秒しか抜けず、キューが際限なく積み上がる。
    /// </para>
    ///
    /// <para>
    /// <b>EOS の担当分けが重要。</b> <c>_srcBus</c> の <c>Eos</c> は
    /// <see cref="StopDrainAndFinalize"/> の専有で、ここでは**読まない**
    /// ── ここで先に取ってしまうと停止側が EOS を永久に（有界化後はタイムアウトまで）待ち、
    /// 「たまに停止が数秒かかる」という原因の追いにくい症状になる。
    /// <c>_sinkBus</c> の EOS は誰も待っていないので拾ってよい。
    /// </para>
    /// </summary>
    private void DrainBuses()
    {
        // sink 側: 常時稼働。EOS を待つ者がいないので Eos も拾う。
        DrainBus(_sinkBus, MessageType.Error | MessageType.Warning | MessageType.Eos, _sinkThrottles, "sink");

        // src 側: 録画中のみ存在する。ここで拾えるのが「録画中の filesink / mp4mux の障害」
        // ＝ディスク満杯・書込権限なし。
        //
        // **停止処理中は src バスに触らない。** StopDrainAndFinalize は EOS を送ってから
        // Eos|Error を待つが、ここが同じバスを汲んでいると**その Eos / Error を先に取ってしまい**、
        // 停止側はタイムアウトまで（有界化する前は永久に）待つことになる。
        // 実際にこれで停止スレッドが1本ハングし、recording.stop が出ず MP4 も確定しなかった。
        // 「録画中に src エラー → 中止」の経路は Error を検出した直後にここへ入るため、
        // この分担が無いと最も検出したい状況で必ず踏む。
        if (!_srcBusOwnedByStop)
            DrainBus(_srcBus, MessageType.Error | MessageType.Warning, _srcThrottles, "src");
    }

    private void DrainBus(Bus? bus, MessageType filter, BusThrottles throttles, string busName)
    {
        if (bus is null)
            return;

        // _isAlive も脱出条件に含める ── 「空になるまで汲む」は洪水対策として必須だが、
        // GstBus のキューは無制限なので、洪水の最中に Close が来た場合はここが有界でないと
        // Join(5000) に間に合わず、破棄が「リークして手放す」側へ倒れる。
        while (_isAlive)
        {
            var msg = bus.PopFiltered(filter);
            if (msg is null)
                return;

            using (msg)
                HandleBusMessage(msg, busName, throttles);
        }
    }

    /// <summary>バスメッセージ1件を分類して記録し、必要なら復帰・停止へつなぐ。</summary>
    private void HandleBusMessage(Message msg, string busName, BusThrottles throttles)
    {
        // メッセージの発信元。所有しない（owned: false）ラッパーで、実体は
        // パイプライン（Bin）が参照を持っているのでメッセージの解放後も有効。
        var srcObject = msg.Handle.GetSrc() == 0
            ? null
            : Gst.Object.NewFromPointer(msg.Handle.GetSrc(), false);
        string elementName = srcObject?.Name ?? "?";

        switch (msg.Type)
        {
            case MessageType.Error:
                {
                    msg.ParseError(out var gerror, out var debug);
                    string? message;
                    using (gerror)
                        message = gerror.Message;
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
                            _errorSinkSrc = erroredSource;
                            _errorSinkSrc.SetState(State.Ready);
                        }
                        ScheduleRestart(elementName);
                    }
                    break;
                }

            case MessageType.Warning:
                {
                    msg.ParseWarning(out var gerror, out var debug);
                    string? message;
                    using (gerror)
                        message = gerror.Message;

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
                Components.ActivityLog.Info("recorder.eos", $"recorder='{Name}' bus={busName} element='{elementName}'");
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
    /// <b>pull スレッドから <see cref="Stop"/> を直接呼んではいけない。</b>
    /// プールスレッドへ逃がし、そちらで通常の停止経路を通す。
    ///
    /// <para>
    /// 理由は2つあり、排出がプールで走る現在の形でも<b>この Task.Run は外せない</b>：
    /// </para>
    /// <list type="number">
    /// <item><see cref="StopAsync"/> は <c>_stateLock</c> を取る。<see cref="Close"/> は
    /// そのロックを保持したまま <c>_pullSampleThread.Join(5000)</c> するので、
    /// pull スレッドがロックを待つとデッドロックする（<c>_stateLock</c> の注記を参照）。</item>
    /// <item>排出は同じ <c>_srcBus</c> の EOS を待つ。pull スレッド上で待つと自己デッドロックする。</item>
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
    /// ソース障害からの自動復帰を予約する。
    ///
    /// <para>
    /// <b>保留中の復帰があれば積まずに return する。</b> 毎エラーごとに無条件に積むと、
    /// モニタを抜いたときの数十件の連続エラーに対して数十本の復帰試行が並走してしまう
    /// （<see cref="RestartPolicy"/> の注記を参照）。
    /// </para>
    /// </summary>
    private void ScheduleRestart(string elementName)
    {
        lock (_restartLock)
        {
            if (_restartCts is not null)
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
            _restartTask = System.Threading.Tasks.Task.Run(() => RestartLoopAsync(elementName, cts));
        }
    }

    /// <summary>
    /// 復帰の連鎖。成功するか、エスカレーション（パイプライン再生成）に至るまで、
    /// <see cref="RestartPolicy"/> の間隔で試行し続ける。
    /// 新しいエラーの到着に依存しない ── 一度だけエラーを出して沈黙するソース
    /// （ケーブルを抜いたモニタ）でも最後まで進む。
    /// </summary>
    private async System.Threading.Tasks.Task RestartLoopAsync(string elementName, CancellationTokenSource cts)
    {
        try
        {
            int attempt = 0;
            while (!cts.IsCancellationRequested)
            {
                attempt++;
                int delayMs = RestartPolicy.DelayForAttempt(attempt);
                Components.ActivityLog.Info("recorder.restart",
                    $"recorder='{Name}' element='{elementName}' attempt={attempt} scheduled in {delayMs}ms");

                await System.Threading.Tasks.Task.Delay(delayMs, cts.Token);

                // Close() 中なら、破棄済みのパイプラインに触る前にここで降りる。
                // _isAlive では代用できない ── 通常の Initialize() 待ちでも false になる。
                if (cts.IsCancellationRequested || !_isAlive)
                    return;

                int refused = _restartRefusals;
                string suppressed = 0 < refused ? $" suppressedErrors={refused}" : "";
                _restartRefusals = 0;

                if (RestartSinkSrc())
                {
                    _restartAttempt = 0;
                    Components.ActivityLog.Info("recorder.restart",
                        $"recorder='{Name}' element='{elementName}' attempt={attempt} result=ok{suppressed}");
                    return;
                }

                _restartAttempt = attempt;
                Components.ActivityLog.Warn("recorder.restart",
                    $"recorder='{Name}' element='{elementName}' attempt={attempt} result=failed{suppressed}");

                if (!RestartPolicy.ShouldEscalate(attempt))
                    continue;   // まだ諦めない。次の間隔で再試行する

                // 要素単位では戻せない状態（デバイスが別のキャップスで戻った・
                // エンコーダーが壊れた）とみなし、パイプラインごと作り直す。
                if (cts.IsCancellationRequested)
                    return;

                Components.ActivityLog.Warn("recorder.restart",
                    $"recorder='{Name}' escalating to a full pipeline rebuild after {attempt} failed attempts");
                _restartAttempt = 0;

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

                try
                {
                    Initialize();
                    Components.ActivityLog.Info("recorder.restart", $"recorder='{Name}' rebuild result=ok");
                }
                catch (Exception ex)
                {
                    Components.ActivityLog.Error("recorder.restart", $"recorder='{Name}' rebuild result=failed {ex.Message}");
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
    /// 障害を起こしたソース要素を Playing に戻す。復帰できたら true。
    ///
    /// <para>
    /// <b>false は「要素単位では戻せない」を意味する</b> ── 対象が無い場合
    /// （ソース以外の要素が壊れた場合）も false を返し、呼び出し側の
    /// エスカレーション（パイプライン再生成）へ進ませる。
    /// </para>
    /// </summary>
    private bool RestartSinkSrc()
    {
        var target = _errorSinkSrc;
        if (target is null)
            return false;

        if (target.SetState(State.Playing) == StateChangeReturn.Failure)
            return false;

        _errorSinkSrc = null;
        return true;
    }

    private Thread? _pullPreviewThread;

    private void PullPreviewProc()
    {
        while (_isAlive)
        {
            try
            {
                using var sample = _previewSink?.TryPullSample(100_000_000);
                if (sample is null)
                    continue;

                OnPreview(sample);
            }
            catch (Exception ex)
            {
                Log(DebugLevel.Error, $"GstEventRecorder.PullPreviewProc failed!\n{ex}", _sinkPipeline);
            }
        }
    }


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

    public void Start()
    {
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
            StartCore();
        }
    }

    private void StartCore()
    {
        System.Diagnostics.Debug.Assert(Monitor.IsEntered(_stateLock), "StartCore must run under _stateLock");

        if (!IsInitialized)
            throw new InvalidOperationException("Not Initialized");
        if (_IsRecording)
            throw new InvalidOperationException("Already started");

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

        using (GObject.Value location = new(filename))
            _file?.SetProperty("location", location);

        // **計測のリセットは _IsRecording を立てる「前」に行う。**
        // 数えるのは _IsRecording が真の間だけなので、この順序なら
        // 取り出しスレッドと競合しない（逆順だと、立てた直後に数えた分を消しうる）。
        _samplesSeenWhileRecording = 0;
        _samplesPushed = 0;
        LastStopOutcome = RecordingStopOutcome.Ok;

        // 世代も _IsRecording より前に進める ── pull スレッドは _IsRecording（volatile）を
        // 見てから世代を読むので、この順序なら「録画中なのに世代が古いまま」は観測されない。
        _recordingSession++;

        IsRecording = _IsRecording = true;
        _recordingStartedAt = Environment.TickCount64;
        if (_srcPipeline!.SetState(State.Playing) == StateChangeReturn.Failure)
        {
            // 成功と失敗でイベント名を分ける（`recorder.init ok` / `recorder.init fail` と同じ規則）。
            // 同名にすると L2 が `recording.start` に掛ける正規表現が失敗行にも一致してしまう。
            LastError = "src pipeline refused to play";
            Components.ActivityLog.Error("recording.start fail", $"recorder='{Name}' file='{filename}' src pipeline refused to play");
            throw new InvalidOperationException("ERROR: pipeline doesn't want to play.");
        }
        Components.ActivityLog.Info("recording.start", $"recorder='{Name}' file='{filename}'");
        // 障害で Ready へ落とされたままのソースがあれば、この機会に戻す。
        // 対象が無ければ false が返るだけで、通常の開始では何も起きない。
        _ = RestartSinkSrc();
    }

    /// <summary><see cref="Start"/> の時刻（<c>recording.stop</c> の経過時間の算出用）。</summary>
    private long _recordingStartedAt;

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
    /// pull スレッドは値の変化で「新しい録画が始まった」を検出して事前バッファを押し込む。
    /// フラグ（<c>_IsRecording</c>）の false→true 遷移では代用できない ──
    /// 停止（排出完了まで）→開始が pull 1 周回（サンプル 1 枚の間隔）以内に完了すると、
    /// pull スレッドは false の周回を一度も観測しないまま次のサンプルで true を見るため
    /// 遷移自体が消え、事前バッファの排出が走らず、前セッションの startPts を引き継いだ
    /// PTS で押し込んでしまう。
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
    /// <c>TimedPopFiltered</c> だけで、<c>finally</c> の <c>SetState(State.Null)</c> は
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
    /// 停止処理が <c>_srcBus</c> を専有している間 true。
    /// <see cref="DrainBuses"/> はこの間 src バスに触らない（詳細はそちらのコメント）。
    /// </summary>
    private volatile bool _srcBusOwnedByStop;

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
    /// static ミラー（<c>GStreamer.GirCore</c> は <c>AppSettings</c> を知らない設計のため、
    /// <see cref="PreferredH264Encoder"/> と同じ方式）。
    /// </summary>
    public static int StopFinalizeTimeoutMs { get; set; } = DefaultStopFinalizeTimeoutMs;

    /// <summary>
    /// EOS を送って src パイプラインを排出し、ファイルを確定させる。
    /// 呼び出し側で <c>_IsRecording</c> を false にしてから呼ぶこと。
    ///
    /// <para>
    /// <b>排出待ちは有界。</b> <c>CLOCK_TIME_NONE</c>（無限待ち）にすると、mux が詰まったとき
    /// 呼び出しスレッド（UI スレッドや CLI 経路）ごと永久にハングする。
    /// タイムアウトしても <c>SetState(Null)</c> は必ず実行し、結果を <c>result=</c> に残す。
    /// </para>
    /// </summary>
    private void StopDrainAndFinalize(long elapsedMs)
    {
        _srcBusOwnedByStop = true;
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

            ulong timeoutNs = (ulong)Math.Max(0, StopFinalizeTimeoutMs) * 1_000_000UL;
            using var msg = _srcBus?.TimedPopFiltered(timeoutNs, MessageType.Eos | MessageType.Error);

            StopDrainSignal signal = msg is null ? StopDrainSignal.Timeout
                : msg.Type == MessageType.Error ? StopDrainSignal.Error
                : StopDrainSignal.Eos;

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
                msg!.ParseError(out var gerror, out var debug);
                using (gerror)
                {
                    string detail = $"recorder='{Name}' file='{LastFilename}' {gerror.Message} debug={debug}";
                    Components.ActivityLog.Error("recording.stop error", detail);
                    LastError = detail;
                }
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
            throw;
        }
        finally
        {
            // タイムアウトでもエラーでも必ず Null へ落とす（要素を再利用可能な状態にする）
            _srcPipeline?.SetState(State.Null);
            _srcBusOwnedByStop = false;
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

            var ret = pipeline.GetState(out var state, out var pending, 0);
            return ret == StateChangeReturn.Async ? $"{state}->{pending}" : state.ToString();
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

#if false
    private void OnBusSyncMessage(Bus sender, Bus.SyncMessageSignalArgs e)
    {
        var message = e.Message;
        switch (message.Type)
        {
            case MessageType.Error:
                {
                    /* dump graph on error */
                    var pipeline = sender.Parent as Bin;
                    if (pipeline is not null)
                        Functions.DebugBinToDotFileWithTs(pipeline, DebugGraphDetails.All, $"{nameof(GstEventRecorder)}.{pipeline.Name ?? "unknown"}.error");

                    message.ParseError(out var err, out var debug);
                    using (err)
                    {
                        using var src = message.Handle.GetSrc() == 0 ? null : Gst.Object.NewFromPointer(message.Handle.GetSrc(), false);
                        if (src is not null)
                            Console.Error.WriteLine($"ERROR: from element {src.Name}: {err.Message}");
                        else
                            Console.Error.WriteLine($"ERROR: {err.Message}");
                        Console.Error.WriteLine($"Additional debug info:\n{debug}");
                    }
                    break;
                }
            default:
                break;
        }
    }

    private void OnBusMessage(Bus sender, Bus.MessageSignalArgs e)
    {
        var message = e.Message;
        switch (message.Type)
        {
            case MessageType.Info:
                {
                    using var src = message.Handle.GetSrc() == 0 ? null : Gst.Object.NewFromPointer(message.Handle.GetSrc(), false);
                    var name = src?.GetPathString();

                    message.ParseInfo(out var gerror, out var debug);
                    using (gerror)
                    {
                        if (debug is not null)
                            Console.Error.WriteLine($"INFO:\n{debug}");
                    }
                }
                break;
            case MessageType.Warning:
                {
                    using var src = message.Handle.GetSrc() == 0 ? null : Gst.Object.NewFromPointer(message.Handle.GetSrc(), false);
                    var name = src?.GetPathString();

                    /* dump graph on warning */
                    var pipeline = sender.Parent as Bin;
                    if (pipeline is not null)
                        Functions.DebugBinToDotFileWithTs(pipeline, DebugGraphDetails.All, $"{nameof(GstEventRecorder)}.{pipeline.Name ?? "unknown"}.warning");

                    message.ParseWarning(out var gerror, out var debug);
                    using (gerror)
                    {
                        Console.Error.WriteLine($"WARNING: from element {name}: {gerror.Message}");
                        if (debug is not null)
                            Console.Error.WriteLine($"Additional debug info:\n{debug}");
                    }
                }
                break;
            default:
                break;
        }
    }
#endif


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

    private bool _disposedValue;

    protected virtual void Dispose(bool disposing)
    {
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