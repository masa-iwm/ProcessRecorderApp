using CommunityToolkit.Mvvm.ComponentModel;
using ProcessRecorderApp.GStreamer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ProcessRecorderApp.ViewModels;

/// <summary>
/// カメラ設定ダイアログの 1 行（1 項目）。
///
/// <para>
/// 値の変更はその場でカメラへ流す（<see cref="CameraControlViewModel"/> が購読する）。
/// <b>プレビューを見ながら合わせられること</b>がこの機能の価値なので、
/// OK を押すまで反映しない作りにはしない。
/// </para>
/// </summary>
public sealed partial class CameraControlItemViewModel : ObservableObject
{
    /// <summary>この行が指す項目の定義。</summary>
    public required CameraControlDef Def { get; init; }

    /// <summary>ドライバが申告した可動範囲。</summary>
    public required CameraControlRange Range { get; init; }

    /// <summary>画面に出す名前（resw から。無ければキーそのもの）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>このカメラが自動に対応しているか（対応していなければチェックを出さない）。</summary>
    public bool SupportsAuto => Range.SupportsAuto;

    /// <summary>
    /// 「自動」チェックの表示。<b><c>DataTemplate</c> の中では <c>x:Uid</c> も
    /// <c>IValueConverter</c> も使わない</b>方針なので、変換済みの値をここに置く
    /// （<c>x:Uid</c> は <c>DataTemplate</c> 内の要素には自動解決されない ── この repo の既知の制約）。
    /// </summary>
    public Microsoft.UI.Xaml.Visibility AutoVisibility
        => SupportsAuto ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>「自動」チェックの文言（ダイアログ側で解決して渡す）。</summary>
    public required string AutoLabel { get; init; }

    /// <summary>
    /// スライダーの刻み。<b>0 を渡さない</b> ── ドライバが 0 を申告することがあり、
    /// そのまま <c>Slider.StepFrequency</c> へ入れると値が動かなくなる。
    /// </summary>
    public double StepFrequency => Range.Step > 0 ? Range.Step : 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    public partial double Value { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManual))]
    public partial bool IsAuto { get; set; }

    /// <summary>自動のあいだはスライダーを触らせない（ドライバが値を無視するため）。</summary>
    public bool IsManual => !IsAuto;

    /// <summary>数値の表示（小数は出さない ── 値は整数である）。</summary>
    public string ValueText => ((int)Math.Round(Value)).ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>変更をダイアログへ知らせる（購読はビューモデル側）。</summary>
    public Action<CameraControlItemViewModel>? Changed { get; set; }

    partial void OnValueChanged(double value) => Changed?.Invoke(this);

    partial void OnIsAutoChanged(bool value) => Changed?.Invoke(this);

    /// <summary>ドライバの既定値へ戻す。</summary>
    public void ResetToDefault()
    {
        IsAuto = false;
        Value = Range.Default;
    }
}

/// <summary>
/// カメラ設定ダイアログの状態。
///
/// <para>
/// <b>行はドライバが実際に対応している項目だけ</b>（<c>GetRange</c> が通ったもの）。
/// カタログの 17 項目を機械的に並べると「動かせないつまみ」が並ぶ。
/// </para>
/// <para>
/// <b>開けなかったときは黙って空にしない。</b> <see cref="ErrorMessage"/> に理由を出す
/// ── 空のダイアログは「このカメラには何も無い」と「開けなかった」の区別が付かない。
/// </para>
/// <para>
/// <b>COM は UI スレッドで触らない。</b> デバイスを開くのも読むのも設定するのも
/// <see cref="CameraControlWorker"/> の専用スレッド 1 本の上で行う ──
/// <c>MFCreateDeviceSource</c> は占有中・低速ドライバで数秒返らないことがあり、
/// UI スレッドで同期に呼ぶとウィンドウ全体（プレビュー描画・録画ボタンを含む）が固まる。
/// 生成は <see cref="CreateAsync"/> だけを使うこと ── コンストラクターは
/// 素のデータ（<c>CameraControlSnapshot</c>）しか受け取らない。
/// </para>
/// </summary>
public sealed partial class CameraControlViewModel : ObservableObject, IDisposable
{
    private readonly CameraControlWorker? _worker;

    /// <summary>
    /// 開いたときの設定文字列。<b>確定時はここを土台にする</b> ── ゼロから組み直すと、
    /// <c>CameraControlSettings</c> が意図的に持ち越している未知キーと、
    /// このカメラが申告しない項目の指定が消える（<see cref="BuildResult"/> 参照）。
    /// </summary>
    private readonly string? _originalText;

    private CameraControlViewModel(
        CameraControlWorker? worker,
        string? originalText,
        IReadOnlyList<CameraControlSnapshot> snapshots,
        string? errorMessage,
        Func<string, string> localize)
    {
        _worker = worker;
        _originalText = originalText;
        ErrorMessage = errorMessage;
        Items = [];

        var settings = CameraControlSettings.Parse(originalText);
        foreach (var snapshot in snapshots)
        {
            // 現在の設定 → カメラの現在値 → 既定値 の順に採用する。
            var saved = settings.Get(snapshot.Def.Key);
            var item = new CameraControlItemViewModel
            {
                Def = snapshot.Def,
                Range = snapshot.Range,
                // キーはカタログがリテラルで持つ（L4 の参照検出のため。CameraControlDef 参照）
                DisplayName = localize(snapshot.Def.ResourceKey),
                AutoLabel = localize("Resources/CameraDialog_Auto"),
                Value = saved.Value ?? snapshot.Value,
                IsAuto = saved.Auto ?? snapshot.Auto,
            };
            // 代入が終わってから購読する（初期値の設定でカメラへ書き戻さない）。
            item.Changed = OnItemChanged;
            Items.Add(item);
        }

        if (Items.Count == 0 && ErrorMessage is null)
            ErrorMessage = localize("Resources/CameraDialog_NoControls");
    }

    /// <summary>
    /// デバイスを解決して開き、行を読み込む。<b>UI スレッドは止めない</b>
    /// ── 解決も、開くのも、読むのも専用スレッドで行い、待つのは <c>await</c> で行う。
    /// </summary>
    /// <param name="srcPipeline">レコーダーの映像ソースのパイプライン文字列。</param>
    /// <param name="current">現在の設定文字列。</param>
    /// <param name="localize">完全修飾キーから文言を引く関数。</param>
    public static async Task<CameraControlViewModel> CreateAsync(
        string? srcPipeline, string? current, Func<string, string> localize)
    {
        var (worker, resolution) = await CameraControlWorker.OpenAsync(srcPipeline);
        if (worker is null)
        {
            string key = resolution switch
            {
                CameraDeviceResolution.NotACamera => "Resources/CameraDialog_NotACamera",
                CameraDeviceResolution.NoDevicePath => "Resources/CameraDialog_NoDevicePath",
                CameraDeviceResolution.DeviceNotFound => "Resources/CameraDialog_DeviceNotFound",
                _ => "Resources/CameraDialog_CannotOpen",
            };
            return new CameraControlViewModel(null, current, [], localize(key), localize);
        }

        // 項目ごとに往復させず 1 回でまとめて読む。
        var snapshots = await worker.UseAsync(session => session.ReadAll());
        return new CameraControlViewModel(worker, current, snapshots ?? [], null, localize);
    }

    /// <summary>表示する行（ドライバが対応している項目だけ）。</summary>
    public ObservableCollection<CameraControlItemViewModel> Items { get; }

    /// <summary>開けなかった／項目が無かった理由。無ければ null。</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>理由の表示・非表示（x:Bind 用）。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    private void OnItemChanged(CameraControlItemViewModel item)
    {
        // **待たずに投函する** ── つまみを動かすたびに UI スレッドを止めない。
        // 失敗しても止めない（ドライバが弾く値はある）。次の操作で直せる。
        // 値は今の UI スレッド上で読み切ってから渡す（COM 側へ VM を渡さない）。
        int value = (int)Math.Round(item.Value);
        bool auto = item.IsAuto;
        var def = item.Def;
        _worker?.Post(session => session.TrySet(def, value, auto));
    }

    /// <summary>
    /// 現在の行の内容を設定文字列にする（OK で確定する値）。
    ///
    /// <para>
    /// <b>ゼロから組み立てない。</b> 開いたときの文字列を読み直したものへ上書きする
    /// ── 新規インスタンスから作ると、<c>CameraControlSettings</c> が意図的に持ち越している
    /// <b>未知キー</b>（新しい版で増えた項目・手書きの指定）と、<b>このカメラが申告しない
    /// 項目の指定</b>（別のカメラ用に入れてあった値）が黙って消える。
    /// これは L1 の <c>UnknownOrUnreadableEntries_SurviveTheRoundTrip</c> が縛っている
    /// 不変条件そのもので、L1 は <c>Parse</c>/<c>Format</c> しか通さないため
    /// <b>この経路の取りこぼしは検出できない</b>。
    /// </para>
    /// </summary>
    public string BuildResult()
        => CameraControlSettings.Merge(
            _originalText,
            Items.Select(item => (
                item.Def.Key,
                (int)Math.Round(item.Value),
                item.SupportsAuto ? item.IsAuto : (bool?)null)));

    public void Dispose() => _worker?.Dispose();
}
