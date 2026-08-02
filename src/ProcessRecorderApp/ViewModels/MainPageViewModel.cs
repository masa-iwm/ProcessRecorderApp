using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.Settings;
using System.Collections.Specialized;
using System.ComponentModel;
using WinUIEx;

namespace ProcessRecorderApp.ViewModels;

/// <summary>
/// MainPage ViewModel using CommunityToolkit.Mvvm partial property syntax.
/// Uses <see cref="ObservableProperty"/> for change notification and
/// <see cref="RelayCommand"/> for command binding.
/// </summary>
public partial class MainPageViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// プライマリコンストラクタにしていないのは、<see cref="GstController"/> の
    /// <c>PropertyChanged</c> を購読する場所が要るため
    /// （<see cref="ReloadSettingsCommand"/> の可否が録画状態で変わる）。
    /// </summary>
    public MainPageViewModel(DispatcherQueue dispatcherQueue, GstControllerViewModel gstController)
    {
        GstController = gstController;
        TemplateVariables = new(dispatcherQueue);
        GstController.PropertyChanged += GstController_PropertyChanged;
    }

    private void GstController_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GstControllerViewModel.IsIdleAll))
            ReloadSettingsCommand.NotifyCanExecuteChanged();
    }

    public static AppSettings Settings => AppSettings.Default;

    /// <summary>
    /// 録画エンジン。プロセス寿命で <see cref="App"/> が所有する（<see cref="GstControllerViewModel.Start"/>）。
    /// ここでは**受け取ってバインドするだけ**で、<see cref="Dispose"/> では破棄しない
    /// ── 画面の生成・破棄で常時バッファリングと録画が途切れないようにするため。
    /// </summary>
    public GstControllerViewModel GstController { get; init; }

    public TemplateVariablesViewModel TemplateVariables { get; init; }

    /// <summary>NavigationView で選択中の画面区分。各パネルの Visibility は下の計算プロパティにバインドする。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewSelected))]
    [NotifyPropertyChangedFor(nameof(IsLogSelected))]
    [NotifyPropertyChangedFor(nameof(IsVariablesSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    public partial MainSection SelectedSection { get; set; } = MainSection.Preview;

    public bool IsPreviewSelected => SelectedSection == MainSection.Preview;
    public bool IsLogSelected => SelectedSection == MainSection.Log;
    public bool IsVariablesSelected => SelectedSection == MainSection.Variables;
    public bool IsSettingsSelected => SelectedSection == MainSection.Settings;

    /// <summary>NavigationView のメニュー（フライアウト/オーバーレイペイン）が開いているか。</summary>
    [ObservableProperty]
    public partial bool IsNavMenuOpen { get; set; }

    /// <summary>プロパティペインの折りたたみ状態（UI 状態、AppSettings に永続化）。レイアウトへの反映は View 側の x:Bind 関数で行う。</summary>
    [ObservableProperty]
    public partial bool IsPropertyPaneCollapsed { get; set; } = AppSettings.Default.IsPropertyPaneCollapsed;
    partial void OnIsPropertyPaneCollapsedChanged(bool value)
    {
        if (AppSettings.Default.IsPropertyPaneCollapsed != value)
            AppSettings.Default.IsPropertyPaneCollapsed = value;
    }

    [RelayCommand]
    private void TogglePropertyPane() => IsPropertyPaneCollapsed = !IsPropertyPaneCollapsed;

    /// <summary>
    /// Log 画面の最下部追従。<b>表示経路が 2 つある（端末とリスト）ので、状態はここが持つ</b>
    /// ── どちらか一方に持たせると、切り替えたときにトグルの表示と実際がずれる
    /// </summary>
    [ObservableProperty]
    public partial bool IsLogFollowEnabled { get; set; } = true;

    /// <summary>
    /// WebView2 を諦めてリスト表示に落ちた。<see cref="LogTerminalView.FallbackActivated"/> で立つ。
    /// 立つと Log 画面が注記と <c>ListView</c> を出し、<c>ListViewCopyBehavior</c> の Ctrl+C も戻る
    /// </summary>
    [ObservableProperty]
    public partial bool IsLogFallbackActive { get; set; }

    [RelayCommand]
    public static void ClearLog()
    {
        // 消えたことは世代番号で表示側へ伝わる（別経路で「消せ」を送ると
        // 直後の書き込みを追い越して新しい行まで消しかねない）
        LogBuffer.Shared.Clear();
    }

    /// <summary>
    /// 再読み込みの確認を View へ委譲するためのコールバック（ダイアログ表示は View の責務）。
    /// true を返した場合のみ読み直す。未設定なら確認なしで読み直す。
    /// </summary>
    public Func<Task<bool>>? ConfirmSettingsReloadAsync { get; set; }

    /// <summary>
    /// 再読み込みの後に View 側で作り直すもののコールバック
    /// （PropertyGrid の選択肢はレコーダー名を焼き込んでいるので、作り直さないと古い名前が残る）。
    /// </summary>
    public Action? SettingsReloaded { get; set; }

    /// <summary>
    /// 録画中・排出中は設定を読み直させない。<see cref="AppSettings.Reload"/> は
    /// <c>Recorders</c> を <c>Clear()</c> してから詰め直すので、動いているエンジンが
    /// その場で破棄され、UI スレッドが排出の完了まで固まる。
    /// </summary>
    public bool CanReloadSettings => GstController.IsIdleAll;

    /// <summary>settings.json を読み直して画面へ反映する（Settings 画面のボタン）。</summary>
    [RelayCommand(CanExecute = nameof(CanReloadSettings))]
    private async Task ReloadSettingsAsync()
    {
        if (ConfirmSettingsReloadAsync is not null && !await ConfirmSettingsReloadAsync())
            return;

        // **先に選択を外す。** Recorders の Reset では SelectedRecorder は null にならず、
        // 自動選択は「null のときだけ」動く（ctor の Recorders.PropertyChanged 参照）。
        // ここで外さないと、再読み込み後も破棄済みのレコーダー VM を指したままになる。
        GstController.SelectedRecorder = null;

        Settings.Reload();
        SettingsReloaded?.Invoke();
    }

    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // 録画エンジンは破棄しない（プロセス寿命で App が所有する）が、
                // エンジンの方が長生きするので購読は必ず外す。
                GstController.PropertyChanged -= GstController_PropertyChanged;
                // 自分が初期化したプレビュー面だけを閉じる。
                GstController.ShutdownPreview();
                TemplateVariables.Dispose();
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
