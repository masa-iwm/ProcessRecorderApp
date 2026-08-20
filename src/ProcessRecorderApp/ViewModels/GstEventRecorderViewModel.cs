using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.GStreamer;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace ProcessRecorderApp.ViewModels
{
    public partial class GstEventRecorderViewModel : ObservableObject, IDisposable, IPropertyAccess
    {
        internal EventRecorder Model { get; init; }

        internal GstEventEventRecorderCollectionViewModel Owner { get; init; }

        public GstEventRecorderViewModel(EventRecorder model, GstEventEventRecorderCollectionViewModel owner)
        {
            Model = model;
            Model.PropertyChanged += Model_PropertyChanged;
            Owner = owner;

            Name = model.Name;
            BufferDuration = model.BufferDuration;
            FilenameTemplate = model.FilenameTemplate;
            LastFilename = model.LastFilename;
            IsRecording = model.IsRecording;
            IsStopping = model.IsStopping;
            IsInitialized = model.IsInitialized;
            Type = model.Type;
            SrcPipeline = model.SrcPipeline;
            EncodingProperties = model.EncodingProperties;
            CameraControls = model.CameraControls;
            CameraControlsLastError = model.CameraControlsLastError;
            ActualType = model.ActualType;
            ActualSrcPipeline = model.ActualSrcPipeline;
            ActualEncodingProperties = model.ActualEncodingProperties;
            LastError = model.LastError;

            ContinuousRecording = model.ContinuousRecording;
            ContinuousFramerate = model.ContinuousFramerate;
            ContinuousResolution = model.ContinuousResolution;
            ContinuousEncodingProperties = model.ContinuousEncodingProperties;
            ContinuousFilenameTemplate = model.ContinuousFilenameTemplate;
            ContinuousSegmentSeconds = model.ContinuousSegmentSeconds;
            IsContinuousRecording = model.IsContinuousRecording;
            ContinuousLastFilename = model.ContinuousLastFilename;
            ContinuousLastError = model.ContinuousLastError;
            ActualContinuousEncodingProperties = model.ActualContinuousEncodingProperties;
            ContinuousSegmentCount = model.ContinuousSegmentCount;
        }

        /// <summary>
        /// Model の変更を VM へ写す。
        ///
        /// <para>
        /// <b>UI スレッドへマーシャリングする。</b> <c>EventRecorder.LastError</c> があるため、
        /// <c>PropertyChanged</c> は GStreamer のストリーミングスレッド
        /// （バスの同期ハンドラ・<c>appsink</c> のコールバック）やプールスレッド
        /// （停止の排出）からも発火する。ここで直接 VM のプロパティを触ると、
        /// バインドされた UI 要素をワーカースレッドから更新して落ちる。
        /// </para>
        /// <para>
        /// <c>HasThreadAccess</c> の高速パスを持たせているのは、既存の呼び出しの大半が
        /// 元から UI スレッド（設定変更・CLI 経路）だからで、そこに余計な遅延を入れないため。
        /// </para>
        /// </summary>
        private void Model_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var queue = Owner.DispatcherQueue;
            if (queue is not null && !queue.HasThreadAccess)
            {
                queue.TryEnqueue(() => ApplyModelChange(e.PropertyName));
                return;
            }
            ApplyModelChange(e.PropertyName);
        }

        private void ApplyModelChange(string? propertyName)
        {
            switch (propertyName)
            {
                case nameof(Model.Name): if (Name != Model.Name) Name = Model.Name; break;
                case nameof(Model.BufferDuration): if (BufferDuration != Model.BufferDuration) BufferDuration = Model.BufferDuration; break;
                case nameof(Model.FilenameTemplate): if (FilenameTemplate != Model.FilenameTemplate) FilenameTemplate = Model.FilenameTemplate; break;
                case nameof(Model.LastFilename): if (LastFilename != Model.LastFilename) LastFilename = Model.LastFilename; break;
                case nameof(Model.IsRecording): if (IsRecording != ModelShowsAsRecording) IsRecording = ModelShowsAsRecording; break;
                // 復帰待ちは表示上の「録画中」も動かす（トグルを切れる状態に保つため）。
                case nameof(Model.IsAwaitingRecoveryResume):
                    if (IsAwaitingRecoveryResume != Model.IsAwaitingRecoveryResume)
                        IsAwaitingRecoveryResume = Model.IsAwaitingRecoveryResume;
                    if (IsRecording != ModelShowsAsRecording)
                        IsRecording = ModelShowsAsRecording;
                    break;
                case nameof(Model.IsStopping): if (IsStopping != Model.IsStopping) IsStopping = Model.IsStopping; break;
                case nameof(Model.IsInitialized): if (IsInitialized != Model.IsInitialized) IsInitialized = Model.IsInitialized; break;
                case nameof(Model.Type): if (Type != Model.Type) Type = Model.Type; break;
                case nameof(Model.SrcPipeline): if (SrcPipeline != Model.SrcPipeline) SrcPipeline = Model.SrcPipeline; break;
                case nameof(Model.EncodingProperties): if (EncodingProperties != Model.EncodingProperties) EncodingProperties = Model.EncodingProperties; break;
                case nameof(Model.CameraControls): if (CameraControls != Model.CameraControls) CameraControls = Model.CameraControls; break;
                case nameof(Model.CameraControlsLastError): if (CameraControlsLastError != Model.CameraControlsLastError) CameraControlsLastError = Model.CameraControlsLastError; break;
                case nameof(Model.ActualType): if (ActualType != Model.ActualType) ActualType = Model.ActualType; break;
                case nameof(Model.ActualSrcPipeline): if (ActualSrcPipeline != Model.ActualSrcPipeline) ActualSrcPipeline = Model.ActualSrcPipeline; break;
                case nameof(Model.ActualEncodingProperties): if (ActualEncodingProperties != Model.ActualEncodingProperties) ActualEncodingProperties = Model.ActualEncodingProperties; break;
                case nameof(Model.LastError): if (LastError != Model.LastError) LastError = Model.LastError; break;
                case nameof(Model.ContinuousRecording): if (ContinuousRecording != Model.ContinuousRecording) ContinuousRecording = Model.ContinuousRecording; break;
                case nameof(Model.ContinuousFramerate): if (ContinuousFramerate != Model.ContinuousFramerate) ContinuousFramerate = Model.ContinuousFramerate; break;
                case nameof(Model.ContinuousResolution): if (ContinuousResolution != Model.ContinuousResolution) ContinuousResolution = Model.ContinuousResolution; break;
                case nameof(Model.ContinuousEncodingProperties): if (ContinuousEncodingProperties != Model.ContinuousEncodingProperties) ContinuousEncodingProperties = Model.ContinuousEncodingProperties; break;
                case nameof(Model.ContinuousFilenameTemplate): if (ContinuousFilenameTemplate != Model.ContinuousFilenameTemplate) ContinuousFilenameTemplate = Model.ContinuousFilenameTemplate; break;
                case nameof(Model.ContinuousSegmentSeconds): if (ContinuousSegmentSeconds != Model.ContinuousSegmentSeconds) ContinuousSegmentSeconds = Model.ContinuousSegmentSeconds; break;
                case nameof(Model.IsContinuousRecording): if (IsContinuousRecording != Model.IsContinuousRecording) IsContinuousRecording = Model.IsContinuousRecording; break;
                case nameof(Model.ContinuousLastFilename): if (ContinuousLastFilename != Model.ContinuousLastFilename) ContinuousLastFilename = Model.ContinuousLastFilename; break;
                case nameof(Model.ContinuousLastError): if (ContinuousLastError != Model.ContinuousLastError) ContinuousLastError = Model.ContinuousLastError; break;
                case nameof(Model.ActualContinuousEncodingProperties): if (ActualContinuousEncodingProperties != Model.ActualContinuousEncodingProperties) ActualContinuousEncodingProperties = Model.ActualContinuousEncodingProperties; break;
                case nameof(Model.ContinuousSegmentCount): if (ContinuousSegmentCount != Model.ContinuousSegmentCount) ContinuousSegmentCount = Model.ContinuousSegmentCount; break;
            }
        }

        /// <summary>
        /// レコーダーの表示名。
        ///
        /// <para>
        /// <b>UI からの改名だけを一意化する。</b> CLI のレコーダー指定は「完全一致・先勝ち」なので、
        /// 同じ名前が2つあると2つ目には CLI から永久に到達できない
        /// （規則は <see cref="RecorderNaming.MakeUnique"/>）。
        /// </para>
        /// <para>
        /// モデル発の反映（ctor の初期化・<see cref="ApplyModelChange"/>）は<b>一意化しない</b>。
        /// ここで一意化すると、重複した名前を含む settings.json を読んだだけで改名が走り、
        /// デバウンス保存が「起動しただけで設定ファイルを書き換える」動きになる
        /// （実際に踏んだことのある形の不具合である）。
        /// </para>
        /// <para>
        /// <c>[ObservableProperty]</c> をやめて手書きにしてあるのは、
        /// <b>格納する前に</b>値を正規化する必要があるため
        /// （<c>OnNameChanged</c> の中で再代入すると、UI が一瞬だけ重複した名前を表示する）。
        /// <c>EventRecorderSettings.BufferDuration</c> と同じ理由・同じ形。
        /// </para>
        /// </summary>
        [Description("PropDesc_Rec_Name")]
        public string Name
        {
            get => _name;
            set
            {
                string normalized = Model.Name == value
                    ? value
                    : RecorderNaming.MakeUnique(value, Owner.Where(r => r != this).Select(r => r.Name));

                if (!SetProperty(ref _name, normalized))
                    return;
                if (Model.Name != normalized)
                    Model.Name = normalized;
            }
        }
        private string _name = "";

        /// <summary>
        /// 事前バッファ長(ms)。Model / Settings と同じく、ここでも
        /// <see cref="EventRecorderSettings.ClampBufferDuration"/> で丸める
        /// （3箇所すべてで丸めないと、範囲外の値が UI 上に残り続けるケースが生じる）。
        /// </summary>
        [Description("PropDesc_Rec_BufferDuration")]
        public int BufferDuration
        {
            get => _bufferDuration;
            set
            {
                if (SetProperty(ref _bufferDuration, EventRecorderSettings.ClampBufferDuration(value))
                    && Model.BufferDuration != _bufferDuration)
                {
                    Model.BufferDuration = _bufferDuration;
                }
            }
        }
        private int _bufferDuration;

        [Description("PropDesc_Rec_FilenameTemplate")]
        [ObservableProperty]
        public partial string FilenameTemplate { get; set; }
        partial void OnFilenameTemplateChanged(string value)
        {
            if (Model.FilenameTemplate != value)
                Model.FilenameTemplate = value;
        }
        [ReadOnly(true)]
        [Description("PropDesc_Rec_LastFilename")]
        [ObservableProperty]
        public partial string? LastFilename { get; private set; }

        /// <summary>
        /// <b>モデルから見た「録画セッション中か」。</b> 復帰待ちも録画中として扱う
        /// ── そうしないとトグルが切れた状態で表示され、<b>利用者に切る手段が無くなる</b>。
        /// 規則そのものは L1 が守れるところ（<see cref="RecordingCommandState"/>）に置く。
        /// </summary>
        private bool ModelShowsAsRecording
            => RecordingCommandState.ShowsAsRecording(Model.IsRecording, Model.IsAwaitingRecoveryResume);

        [Description("PropDesc_Rec_IsRecording")]
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartRecording))]
        [NotifyPropertyChangedFor(nameof(CanStopRecording))]
        [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
        public partial bool IsRecording { get; set; }
        partial void OnIsRecordingChanged(bool value)
        {
            if (ModelShowsAsRecording == value)
                return;

            // PropertyGrid の直接編集はコマンドの CanExecute を通らないので、ここでも同じ
            // 可否（CanStart/StopRecording）を検査する。素通しにすると、未初期化のレコーダーで
            // Start() が投げて VM とモデルが食い違ったまま残り（生成 setter は OnChanged が
            // 投げると PropertyChanged を発火しないため、以後の差分判定が全て偽になる）、
            // 排出中は WaitForPendingStop が UI スレッドを最大 StopFinalizeTimeoutMs 塞ぐ。
            if (value ? !CanStartRecording : !CanStopRecording)
            {
                IsRecording = ModelShowsAsRecording;   // 差し戻し（画面の値とモデルは常に一致させる）
                return;
            }

            try
            {
                if (value)
                    Model.Start();
                else
                    Model.Stop();
            }
            catch (Exception)
            {
                // 失敗の記録と LastError は EventRecorder 側が済ませている。
                // ここでは表示をモデルへ差し戻すだけでよい。
                IsRecording = ModelShowsAsRecording;
            }
        }

        /// <summary>
        /// 停止を受け付けてから排出が終わるまで true（<see cref="EventRecorder.IsStopping"/> のミラー）。
        /// 排出中は録画を開始できない ── <see cref="CanStartRecording"/> がこれを見る。
        /// </summary>
        [ReadOnly(true)]
        [Browsable(false)]
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartRecording))]
        [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
        public partial bool IsStopping { get; private set; }

        /// <summary>
        /// 自動復帰の作り直しで畳んだ録画を、復帰したら録り直す予定があるか
        /// （<see cref="EventRecorder.IsAwaitingRecoveryResume"/> のミラー）。
        ///
        /// <para>
        /// <b>停止を届かせるために要る。</b> 作り直しのあいだは
        /// <c>IsRecording</c> も <c>IsInitialized</c> も false なので、これを見ないと
        /// <see cref="CanStopRecording"/> が false になり、利用者の停止も
        /// UiaTrigger の停止条件も<b>どこにも届かないまま</b>録画が再開する。
        /// </para>
        /// </summary>
        [ReadOnly(true)]
        [Browsable(false)]
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStopRecording))]
        [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
        public partial bool IsAwaitingRecoveryResume { get; private set; }

        [ReadOnly(true)]
        [Description("PropDesc_Rec_IsInitialized")]
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartRecording))]
        [NotifyPropertyChangedFor(nameof(CanStopRecording))]
        [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
        public partial bool IsInitialized { get; private set; }

        [Description("PropDesc_Rec_Type")]
        [ObservableProperty]
        public partial EventRecordingType Type { get; set; }
        partial void OnTypeChanged(EventRecordingType value)
        {
            if (Model.Type != value)
                Model.Type = value;
        }

        [ValueBuilder("GstSrcPipeline")]
        [Description("PropDesc_Rec_SrcPipeline")]
        [ObservableProperty]
        public partial string? SrcPipeline { get; set; }
        partial void OnSrcPipelineChanged(string? value)
        {
            if (Model.SrcPipeline != value)
                Model.SrcPipeline = value;
        }

        [Description("PropDesc_Rec_EncodingProperties")]
        [ObservableProperty]
        public partial string? EncodingProperties { get; set; }
        partial void OnEncodingPropertiesChanged(string? value)
        {
            if (Model.EncodingProperties != value)
                Model.EncodingProperties = value;
        }

        /// <summary>
        /// カメラ（<c>mfvideosrc</c>）のフォーカス・明るさ等。
        /// 「…」ボタンから専用ダイアログを開く（<c>MainPage.BuildValueAsync</c>）。
        /// <b>読み取り専用にはしない</b> ── 手で <c>brightness=128</c> と書ける道を残す
        /// （<c>SrcPipeline</c> / <c>OutputDirectory</c> と同じ判断）。
        /// </summary>
        [Description("PropDesc_Rec_CameraControls")]
        [ValueBuilder("GstCameraControls")]
        [ObservableProperty]
        public partial string? CameraControls { get; set; }
        partial void OnCameraControlsChanged(string? value)
        {
            if (Model.CameraControls != value)
                Model.CameraControls = value;
        }

        [ReadOnly(true)]
        [Description("PropDesc_Rec_CameraControlsLastError")]
        [ObservableProperty]
        public partial string? CameraControlsLastError { get; private set; }

        [ReadOnly(true)]
        [Description("PropDesc_Rec_ActualType")]
        [ObservableProperty]
        public partial EventRecordingType ActualType { get; private set; }
        [ReadOnly(true)]
        [Description("PropDesc_Rec_ActualSrcPipeline")]
        [ObservableProperty]
        public partial string? ActualSrcPipeline { get; private set; }
        [ReadOnly(true)]
        [Description("PropDesc_Rec_ActualEncodingProperties")]
        [ObservableProperty]
        public partial string? ActualEncodingProperties { get; private set; }

        /// <summary>
        /// 直近に検出した障害（正常時は空）。読み取り専用行として PropertyGrid に出す。
        /// これが無いと、録画が壊れていることを知る手段が「出来上がった MP4 を見る」しかない。
        /// </summary>
        [ReadOnly(true)]
        [Description("PropDesc_Rec_LastError")]
        [ObservableProperty]
        public partial string? LastError { get; private set; }

        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ContinuousRecording")]
        [ObservableProperty]
        public partial bool ContinuousRecording { get; set; }
        partial void OnContinuousRecordingChanged(bool value)
        {
            if (Model.ContinuousRecording != value)
                Model.ContinuousRecording = value;
        }

        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ContinuousFramerate")]
        [ObservableProperty]
        public partial string ContinuousFramerate { get; set; } = "";
        partial void OnContinuousFramerateChanged(string value)
        {
            if (Model.ContinuousFramerate != value)
                Model.ContinuousFramerate = value;
        }

        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ContinuousResolution")]
        [ObservableProperty]
        public partial string ContinuousResolution { get; set; } = "";
        partial void OnContinuousResolutionChanged(string value)
        {
            if (Model.ContinuousResolution != value)
                Model.ContinuousResolution = value;
        }

        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ContinuousEncodingProperties")]
        [ObservableProperty]
        public partial string? ContinuousEncodingProperties { get; set; }
        partial void OnContinuousEncodingPropertiesChanged(string? value)
        {
            if (Model.ContinuousEncodingProperties != value)
                Model.ContinuousEncodingProperties = value;
        }

        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ContinuousFilenameTemplate")]
        [ObservableProperty]
        public partial string ContinuousFilenameTemplate { get; set; } = "";
        partial void OnContinuousFilenameTemplateChanged(string value)
        {
            if (Model.ContinuousFilenameTemplate != value)
                Model.ContinuousFilenameTemplate = value;
        }

        /// <summary>
        /// 常時録画のセグメント長(秒)。Model / Settings と同じく、ここでも
        /// <see cref="EventRecorderSettings.ClampContinuousSegmentSeconds"/> で丸める
        /// （3 箇所すべてで丸めないと、範囲外の値が UI 上に残り続ける）。
        /// </summary>
        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ContinuousSegmentSeconds")]
        public int ContinuousSegmentSeconds
        {
            get => _continuousSegmentSeconds;
            set
            {
                if (SetProperty(ref _continuousSegmentSeconds,
                        EventRecorderSettings.ClampContinuousSegmentSeconds(value))
                    && Model.ContinuousSegmentSeconds != _continuousSegmentSeconds)
                {
                    Model.ContinuousSegmentSeconds = _continuousSegmentSeconds;
                }
            }
        }
        private int _continuousSegmentSeconds;

        [ReadOnly(true)]
        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_IsContinuousRecording")]
        [ObservableProperty]
        public partial bool IsContinuousRecording { get; private set; }

        [ReadOnly(true)]
        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ContinuousLastFilename")]
        [ObservableProperty]
        public partial string? ContinuousLastFilename { get; private set; }

        [ReadOnly(true)]
        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ContinuousLastError")]
        [ObservableProperty]
        public partial string? ContinuousLastError { get; private set; }

        [ReadOnly(true)]
        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ActualContinuousEncodingProperties")]
        [ObservableProperty]
        public partial string? ActualContinuousEncodingProperties { get; private set; }

        [ReadOnly(true)]
        [Category("PropCat_Continuous")]
        [Description("PropDesc_Rec_ContinuousSegmentCount")]
        [ObservableProperty]
        public partial int ContinuousSegmentCount { get; private set; }


        [property: Description("PropDesc_Rec_StartRecording")]
        [RelayCommand(CanExecute = nameof(CanStartRecording))]
        public void StartRecording() => Model.Start();
        /// <summary>
        /// 判定規則そのものは <see cref="RecordingCommandState"/>（純粋関数）にある
        /// ── この VM は L1 から参照できないため、規則をここに書くと誰も守れない。
        /// </summary>
        [Browsable(false)]
        public bool CanStartRecording
            => RecordingCommandState.CanStart(Model.IsInitialized, Model.IsRecording, Model.IsStopping);


        [property: Description("PropDesc_Rec_StopRecording")]
        [RelayCommand(CanExecute = nameof(CanStopRecording))]
        public void StopRecording() => Model.Stop();

        /// <summary>
        /// 録画を停止し、<b>ファイルが確定するまで待つ</b>。CLI 経路はこちらを使う
        /// （<c>stop-recording X</c> の直後に <c>copy</c> するバッチが想定用途）。
        /// </summary>
        public System.Threading.Tasks.Task StopRecordingAsync() => Model.StopAsync();

        /// <summary>
        /// <b>直近の停止がファイルとして何を残したか。</b> CLI はこれを見て終了コードを決める
        /// （<c>ActivationCommands.ExitCode_RecordingProducedNothing</c> /
        /// <c>ExitCode_RecordingNotFinalized</c>）。
        ///
        /// <para>
        /// <c>[Browsable(false)]</c> にしてある ── PropertyGrid には
        /// <see cref="LastError"/> として同じ事実がすでに文章で出るので、
        /// 行を増やすと同じことを2箇所で言うことになる。
        /// </para>
        /// </summary>
        [Browsable(false)]
        public RecordingStopOutcome LastStopOutcome => Model.LastStopOutcome;

        [Browsable(false)]
        public bool CanStopRecording
            => RecordingCommandState.CanStop(
                Model.IsInitialized, Model.IsRecording, Model.IsAwaitingRecoveryResume);

        [property: Description("PropDesc_Rec_Initialize")]
        [RelayCommand]
        public void OnInitialize() => Model.Initialize();


        public IEnumerable<PropertyInfo> GetProperties() => typeof(GstEventRecorderViewModel).GetProperties(BindingFlags.Instance | BindingFlags.Public);


        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Model.PropertyChanged -= Model_PropertyChanged;
                }

                disposedValue = true;
            }
        }

        // ファイナライザは定義しない（Dispose(false) が何も解放しないため）。
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    public partial class GstEventEventRecorderCollectionViewModel : ReadOnlyDispatcherCollection<GstEventRecorderViewModel>
    {
        private readonly WeakReference<GstEventEventRecorderCollectionViewModel> _weakThis;
        private readonly INotifyCollectionChanged _notifier;

        public GstEventEventRecorderCollectionViewModel(IList<EventRecorder> source, DispatcherQueue dispatcherQueue)
            : base(new(dispatcherQueue))
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(dispatcherQueue);

            if (source is not INotifyCollectionChanged notifier)
                throw new ArgumentException($"{nameof(source)} must implement {nameof(INotifyCollectionChanged)}.", nameof(source));

            foreach (var model in source) Items.Add(Convert(model));

            _weakThis = new(this);
            _notifier = notifier;
            notifier.CollectionChanged += OnSourceCollectionChanged;
        }

        /// <summary>
        /// ソース（<c>Controller.Recorders</c>）のミラーを停止して保持中の VM を破棄する。
        /// エンジン破棄時に、<see cref="OnSourceCollectionChanged"/>（<c>async void</c>＋
        /// ディスパッチャ投入）が破棄途中のコレクションに対して走らないように、
        /// ソース側を触る前に呼ぶ。冪等。
        ///
        /// 基底の <see cref="ReadOnlyDispatcherCollection{T}"/> が内部
        /// <see cref="DispatcherCollection{T}"/> に張った購読は残る（プロセス終了時にしか
        /// 到達しないため実害が無い）。完全な teardown ではない点に注意。
        /// </summary>
        public void Detach()
        {
            _notifier.CollectionChanged -= OnSourceCollectionChanged;
            foreach (var item in Items)
                item.Dispose();
        }

        private async void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_weakThis.TryGetTarget(out var target))
            {
                if (sender is INotifyCollectionChanged collection)
                    collection.CollectionChanged -= OnSourceCollectionChanged;
                return;
            }

            ArgumentNullException.ThrowIfNull(e);
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    var vm = Convert((EventRecorder)e.NewItems?[0]!);
                    await DispatcherQueue.EnqueueAsync(() => Items.Insert(e.NewStartingIndex, vm));
                    break;
                case NotifyCollectionChangedAction.Move:
                    await DispatcherQueue.EnqueueAsync(() => ((ObservableCollection<GstEventRecorderViewModel>)Items).Move(e.OldStartingIndex, e.NewStartingIndex));
                    break;
                case NotifyCollectionChangedAction.Remove:
                    Items[e.OldStartingIndex].Dispose();
                    await DispatcherQueue.EnqueueAsync(() => Items.RemoveAt(e.OldStartingIndex));
                    break;
                case NotifyCollectionChangedAction.Replace:
                    Items[e.NewStartingIndex].Dispose();
                    var replaceVm = Convert((EventRecorder)e.NewItems?[0]!);
                    await DispatcherQueue.EnqueueAsync(() => Items[e.NewStartingIndex] = replaceVm);
                    break;
                case NotifyCollectionChangedAction.Reset:
                    foreach (var item in Items)
                        item.Dispose();

                    await DispatcherQueue.EnqueueAsync(Items.Clear);
                    break;
                default:
                    throw new ArgumentException("Unknown NotifyCollectionChangedAction", nameof(e));
            }
        }

        GstEventRecorderViewModel Convert(EventRecorder model) => new(model, this);

        public GstEventRecorderViewModel? this[EventRecorder? value] => this.FirstOrDefault(vm => vm.Model?.Equals(value) == true);
    }
}
