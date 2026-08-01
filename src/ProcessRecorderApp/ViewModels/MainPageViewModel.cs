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
public partial class MainPageViewModel(DispatcherQueue dispatcherQueue, GstControllerViewModel gstController)
    : ObservableObject, IDisposable
{
    public static AppSettings Settings => AppSettings.Default;

    /// <summary>
    /// 録画エンジン。プロセス寿命で <see cref="App"/> が所有する（<see cref="GstControllerViewModel.Start"/>）。
    /// ここでは**受け取ってバインドするだけ**で、<see cref="Dispose"/> では破棄しない
    /// ── 画面の生成・破棄で常時バッファリングと録画が途切れないようにするため。
    /// </summary>
    public GstControllerViewModel GstController { get; init; } = gstController;

    public TemplateVariablesViewModel TemplateVariables { get; init; } = new(dispatcherQueue);

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

    public DispatcherCollection<string> LogItems
    {
        get
        {
            _logItems ??= new DispatcherCollection<string>(Program.LogItems, dispatcherQueue);
            return _logItems;
        }
    }
    private static DispatcherCollection<string>? _logItems;


    [RelayCommand]
    public static void ClearLog()
    {
        Program.LogItems.Clear();
    }

    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // 録画エンジンは破棄しない（プロセス寿命で App が所有する）。
                // 自分が初期化したプレビュー面だけを閉じる。
                GstController.ShutdownPreview();
                TemplateVariables.Dispose();
                // static キャッシュなので null 化まで行う。破棄済みのまま残すと、
                // ページが再生成されたときに Program.LogItems をミラーしない
                // 死んだコレクションを Log 画面へ返してしまう。
                _logItems?.Dispose();
                _logItems = null;
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
