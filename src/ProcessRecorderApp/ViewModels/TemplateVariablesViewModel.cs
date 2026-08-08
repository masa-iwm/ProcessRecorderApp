using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using ProcessRecorderApp.GStreamer;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace ProcessRecorderApp.ViewModels;

/// <summary>
/// <see cref="EventRecorder.TemplateVariables"/> を表示・編集する Variables ペインの ViewModel。
/// static 辞書とUI(行コレクション)を双方向に同期する。
/// </summary>
public partial class TemplateVariablesViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>
    /// 自VMからの辞書書き込み中は true（変更イベントによる再構築を抑止する）。
    ///
    /// <para>
    /// <b>スレッドごとに持つ。</b> <c>TemplateVariablesChanged</c> は UI スレッドから
    /// 飛ぶとは限らない ── UIA トリガの発火は<b>監視ワーカースレッド</b>から
    /// <c>SetTemplateVariable</c> を呼ぶ（<c>UiaTriggerService.OnTriggerFired</c>）。
    /// ただのフィールドにすると、UI スレッドがセル編集中（<see cref="RunApplying"/> の中）に
    /// 届いた他スレッド発の変更が「自分の書き込み」と誤認されて捨てられ、
    /// <b>Variables 画面が実体とずれたまま残る</b>。
    /// スレッド単位なら「抑止するのは自分が今書いている経路だけ」になる。
    /// </para>
    /// </summary>
    [ThreadStatic]
    private static bool _isApplying;

    public ObservableCollection<TemplateVariableViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial TemplateVariableViewModel? SelectedItem { get; set; }

    public TemplateVariablesViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        Refresh();
        EventRecorder.TemplateVariablesChanged += TemplateVariables_Changed;
    }

    private void TemplateVariables_Changed(object? sender, EventArgs e)
    {
        // 自VMからの書き込みはイベント発火時点（同期呼び出し）のフラグで判定して無視する
        if (_isApplying)
            return;
        // CLI(--set)等はUIスレッド以外からも発火するためマーシャリングする
        _dispatcherQueue.TryEnqueue(Refresh);
    }

    /// <summary>辞書の現在の内容から行コレクションを再構築する</summary>
    private void Refresh()
    {
        SelectedItem = null;
        Items.Clear();
        foreach (var pair in EventRecorder.TemplateVariables.OrderBy(p => p.Key, StringComparer.Ordinal))
            Items.Add(new TemplateVariableViewModel(this, pair.Key, pair.Value));
    }

    /// <summary>空のキー/値行を追加する（キーが入力されるまで辞書には反映されない）</summary>
    [RelayCommand]
    private void Add()
    {
        var item = new TemplateVariableViewModel(this, string.Empty, string.Empty);
        Items.Add(item);
        SelectedItem = item;
    }

    /// <summary>選択中の行を削除し、辞書からも対応するキーを削除する</summary>
    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedItem is not { } item)
            return;
        Items.Remove(item);
        SelectedItem = null;
        item.RemoveFromDictionary();
    }

    /// <summary>
    /// 指定した行以外に、同じキーを持つ（または適用済みの）行があるか。
    /// キーの重複を許すと、(1) 新しい行への既存キー入力がその変数を空文字で潰す、
    /// (2) 同じキーを共有した行の片方を削除すると、残った行が表示している変数まで
    /// 実体と settings.json の両方から消える ── レコーダー名を
    /// <c>RecorderNaming.MakeUnique</c> で一意化しているのと同じ理由で、キーも一意に保つ。
    /// </summary>
    internal bool HasAnotherRowWithKey(TemplateVariableViewModel self, string key)
        => Items.Any(item => !ReferenceEquals(item, self) && item.Key == key);

    /// <summary>辞書への書き込みを、変更イベントによる再構築を抑止した状態で実行する</summary>
    internal void RunApplying(Action action)
    {
        // 入れ子で呼ばれても外側の抑止を壊さないよう、直前の値へ戻す
        bool previous = _isApplying;
        _isApplying = true;
        try
        {
            action();
        }
        finally
        {
            _isApplying = previous;
        }
    }

    public void Dispose()
    {
        EventRecorder.TemplateVariablesChanged -= TemplateVariables_Changed;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// テンプレート変数1件（キー/値）を表す行 ViewModel。
/// セル編集の確定時に <see cref="EventRecorder.TemplateVariables"/> へ書き戻す。
/// TableView 内部（ソート等）からのリフレクションアクセスに備え、
/// トリミング/AOT対応として GeneratedBindableCustomProperty を付与する。
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public partial class TemplateVariableViewModel : ObservableObject
{
    private readonly TemplateVariablesViewModel _owner;

    /// <summary>コンストラクタでの初期値設定中は true（辞書への書き戻しを抑止する）</summary>
    private readonly bool _initializing;

    /// <summary>辞書に適用済みのキー（未適用なら null）。リネーム時の旧キー削除に使う</summary>
    private string? _appliedKey;

    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>
    /// この変数を settings.json に残すか。
    ///
    /// <para>
    /// <b>既定は false。</b> テンプレート変数はセッション限りが基本で、永続化は
    /// ここか CLI の <c>--persist</c> で<b>明示的に指定したものだけ</b>が対象
    /// （意図的な仕様）。実体は
    /// <see cref="Settings.AppSettings.TemplateVariables"/> にキーが載っているかどうか。
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial bool IsPersistent { get; set; }

    public TemplateVariableViewModel(TemplateVariablesViewModel owner, string key, string value)
    {
        _owner = owner;
        _appliedKey = key.Length > 0 ? key : null;
        _initializing = true;
        Key = key;
        Value = value;
        IsPersistent = key.Length > 0 && Settings.AppSettings.Default.IsTemplateVariablePersistent(key);
        _initializing = false;
    }

    partial void OnIsPersistentChanged(bool value)
    {
        if (_initializing || Key.Length == 0)
            return;
        Settings.AppSettings.Default.SetTemplateVariablePersistent(Key, value);
    }

    partial void OnKeyChanged(string value)
    {
        if (_initializing)
            return;

        // 既存の行と重複するキーは受け付けず、元のキーへ差し戻す。
        // 受け付けると、この行の（多くは空の）Value が既存変数を黙って上書きし、
        // さらに削除時に共有キーの実体まで巻き添えで消える（HasAnotherRowWithKey の doc 参照）。
        if (value.Length > 0 && _owner.HasAnotherRowWithKey(this, value))
        {
            // 黙って戻さない ── 画面上は「入力が消えた」ようにしか見えないので、
            // 理由が残らないと利用者にも開発者にも説明できない。
            Components.ActivityLog.Warn("variables.duplicate-key",
                $"key='{value}' another row already uses this key; the edit was reverted");
            Key = _appliedKey ?? string.Empty;
            return;
        }

        string? previousKey = _appliedKey;
        // リネーム＝旧キー削除+新キー設定。キーが空になった場合は辞書から外すのみ
        _owner.RunApplying(() =>
        {
            if (_appliedKey is not null && _appliedKey != value)
                EventRecorder.RemoveTemplateVariable(_appliedKey);
            if (value.Length > 0)
            {
                EventRecorder.SetTemplateVariable(value, Value);
                _appliedKey = value;
            }
            else
            {
                _appliedKey = null;
            }
        });

        // 永続化の指定も改名に追随させる。追随させないと「保存する」の指定だけが
        // 古い名前に残り、settings.json に消えた変数のキーが残り続ける。
        // **static ストアを直した後に呼ぶ** ── 新しいキーの値をそこから取るため。
        if (previousKey is not null && previousKey != value)
        {
            if (value.Length > 0)
                Settings.AppSettings.Default.RenameTemplateVariablePersistence(previousKey, value);
            else
                Settings.AppSettings.Default.SetTemplateVariablePersistent(previousKey, false);
        }
    }

    partial void OnValueChanged(string value)
    {
        if (_initializing || Key.Length == 0)
            return;
        _owner.RunApplying(() => EventRecorder.SetTemplateVariable(Key, value));
    }

    /// <summary>この行に対応するキーを辞書から削除する（行削除時に呼ばれる）</summary>
    internal void RemoveFromDictionary()
    {
        if (_appliedKey is null)
            return;
        var key = _appliedKey;
        _appliedKey = null;
        _owner.RunApplying(() => EventRecorder.RemoveTemplateVariable(key));
        // 永続化の指定も外す。残すと settings.json に「もう存在しない変数」が残る。
        Settings.AppSettings.Default.SetTemplateVariablePersistent(key, false);
    }
}
