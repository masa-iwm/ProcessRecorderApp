using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ProcessRecorderApp.Views;

/// <summary>
/// 編集中のリモート利用者 1 人分。<b>渡された定義の写し</b>であり、
/// 確定するまで正本（<c>AppSettings.RemoteUsers</c>）には触れない。
/// </summary>
public sealed partial class RemoteUserRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial RemoteRole Role { get; set; } = RemoteRole.Viewer;

    /// <summary>
    /// パスワードのハッシュ（<c>RemoteUserRules</c> の形式）。
    /// <b>平文は持たない</b> ── 「パスワードを設定」を押した時点でハッシュ化し、
    /// 入力欄はその場で空にする。
    /// </summary>
    [ObservableProperty]
    public partial string PasswordHash { get; set; } = "";

    /// <summary>一覧に出す 1 行（名前と役割）。</summary>
    public string Display => $"{Name} / {RoleName(Role)}";

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Display));

    partial void OnRoleChanged(RemoteRole value) => OnPropertyChanged(nameof(Display));

    /// <summary>役割の表示名。キーは文字列リテラルで渡す（L4 が参照を拾えるように）。</summary>
    public static string RoleName(RemoteRole role) => role switch
    {
        RemoteRole.Admin => Localization.GetString("Resources/RemoteUserEditor_RoleAdmin"),
        RemoteRole.Operator => Localization.GetString("Resources/RemoteUserEditor_RoleOperator"),
        _ => Localization.GetString("Resources/RemoteUserEditor_RoleViewer"),
    };
}

/// <summary>
/// リモート利用者の一覧を手元で編集するダイアログ。
///
/// <para>
/// <b>渡されたリストには触れない。</b> 写しを編集し、確定したときだけ新しいリストを返す
/// （取り消しは <see langword="null"/>）。呼び出し側は返り値でまるごと差し替える
/// ── 要素の in-place 変更をしないのは <c>AppSettings.RemoteUsers</c> の運用そのものである。
/// </para>
/// <para>
/// <b>パスワードは押した時点でハッシュ化する</b>（<see cref="RemoteUserRules.HashPassword"/>）。
/// 平文はダイアログを閉じるまでに捨てる。ハッシュが無い利用者は確定できない
/// ── 空のまま保存すると「名前だけ在るのに誰も名乗れない」行が残るため。
/// </para>
/// </summary>
public sealed partial class RemoteUserEditorDialog : ContentDialog
{
    /// <summary>編集中の写し。</summary>
    private readonly ObservableCollection<RemoteUserRow> _users = [];

    /// <summary>確定した内容。取り消しなら <see langword="null"/> のまま。</summary>
    private IReadOnlyList<RemoteUserDefinition>? _result;

    /// <summary>編集欄へ選択行を流し込んでいるあいだ true（変更ハンドラの逆流を止める）。</summary>
    private bool _loading;

    private RemoteUserEditorDialog(IReadOnlyList<RemoteUserDefinition> current)
    {
        InitializeComponent();

        Title = Localization.GetString("Resources/RemoteUserEditor_Title");
        PrimaryButtonText = Localization.GetString("Resources/RemoteUserEditor_Save");
        CloseButtonText = Localization.GetString("Resources/RemoteUserEditor_Cancel");
        AddButton.Content = Localization.GetString("Resources/RemoteUserEditor_Add");
        RemoveButton.Content = Localization.GetString("Resources/RemoteUserEditor_Remove");
        SetPasswordButton.Content = Localization.GetString("Resources/RemoteUserEditor_SetPassword");
        NameLabel.Text = Localization.GetString("Resources/RemoteUserEditor_Name");
        RoleLabel.Text = Localization.GetString("Resources/RemoteUserEditor_Role");
        PasswordLabel.Text = Localization.GetString("Resources/RemoteUserEditor_Password");

        // 並びは列挙の値の順（Viewer=0 / Operator=1 / Admin=2）。
        // 選択位置をそのまま RemoteRole へ読み替えるので、順序を変えてはいけない。
        foreach (RemoteRole role in new[] { RemoteRole.Viewer, RemoteRole.Operator, RemoteRole.Admin })
            RoleCombo.Items.Add(RemoteUserRow.RoleName(role));

        UserList.ItemsSource = _users;
        foreach (RemoteUserDefinition user in current)
        {
            _users.Add(new RemoteUserRow
            {
                Name = user.Name,
                Role = user.Role,
                PasswordHash = user.PasswordHash,
            });
        }

        UserList.SelectedIndex = _users.Count > 0 ? 0 : -1;
        LoadSelectionIntoEditor();
    }

    /// <summary>
    /// 一覧を編集する。確定なら新しいリスト、取り消しなら <see langword="null"/>。
    /// </summary>
    public static async Task<IReadOnlyList<RemoteUserDefinition>?> EditAsync(
        XamlRoot xamlRoot, IReadOnlyList<RemoteUserDefinition> current)
    {
        ArgumentNullException.ThrowIfNull(current);

        RemoteUserEditorDialog dialog = new(current) { XamlRoot = xamlRoot };
        try
        {
            await dialog.ShowAsync();
            return dialog._result;
        }
        finally
        {
            // 取り消しでも例外でも、平文を残したまま閉じない。
            dialog.PasswordInput.Password = "";
        }
    }

    private RemoteUserRow? Selected => UserList.SelectedItem as RemoteUserRow;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSelectionIntoEditor();

    /// <summary>選択行の値を編集欄へ流し込む。パスワード欄は行を移るたびに空にする。</summary>
    private void LoadSelectionIntoEditor()
    {
        _loading = true;
        try
        {
            RemoteUserRow? row = Selected;
            // StackPanel は Control ではないので IsEnabled を持たない。編集欄を1つずつ切る。
            bool hasRow = row is not null;
            NameBox.IsEnabled = hasRow;
            RoleCombo.IsEnabled = hasRow;
            PasswordInput.IsEnabled = hasRow;
            SetPasswordButton.IsEnabled = hasRow;
            RemoveButton.IsEnabled = hasRow;
            NameBox.Text = row?.Name ?? "";
            RoleCombo.SelectedIndex = row is null ? -1 : (int)row.Role;
            PasswordInput.Password = "";
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnNameBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && Selected is { } row)
            row.Name = NameBox.Text;
    }

    private void OnRoleComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && Selected is { } row && RoleCombo.SelectedIndex >= 0)
            row.Role = (RemoteRole)RoleCombo.SelectedIndex;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_users.Count >= RemoteUserRules.MaxUsers)
        {
            ShowError(Localization.GetString("Resources/RemoteUserEditor_TooManyUsers", RemoteUserRules.MaxUsers));
            return;
        }

        RemoteUserRow row = new();
        _users.Add(row);
        UserList.SelectedItem = row;
        ClearError();
        NameBox.Focus(FocusState.Programmatic);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;

        _users.Remove(row);
        UserList.SelectedIndex = _users.Count > 0 ? 0 : -1;
        ClearError();
    }

    /// <summary>
    /// 入力されたパスワードをその場でハッシュ化し、平文の入力欄を空にする。
    /// </summary>
    private void OnSetPasswordClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;

        string password = PasswordInput.Password;
        if (string.IsNullOrEmpty(password))
        {
            ShowError(Localization.GetString("Resources/RemoteUserEditor_PasswordRequired", row.Name));
            return;
        }

        row.PasswordHash = RemoteUserRules.HashPassword(password);
        PasswordInput.Password = "";
        ClearError();
    }

    /// <summary>
    /// 確定。妥当でなければ閉じずに理由を出す（<c>args.Cancel</c>）。
    /// </summary>
    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!TryBuildResult(out IReadOnlyList<RemoteUserDefinition>? edited))
        {
            args.Cancel = true;
            return;
        }

        _result = edited;
        PasswordInput.Password = "";
    }

    /// <summary>
    /// 編集中の写しを検証して確定形へ変換する。
    /// 人数の上限・名前の妥当性・名前の重複（序数比較）・パスワード未設定を拒否する。
    /// </summary>
    private bool TryBuildResult(out IReadOnlyList<RemoteUserDefinition>? edited)
    {
        edited = null;

        if (_users.Count > RemoteUserRules.MaxUsers)
        {
            ShowError(Localization.GetString("Resources/RemoteUserEditor_TooManyUsers", RemoteUserRules.MaxUsers));
            return false;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (RemoteUserRow row in _users)
        {
            if (!RemoteUserRules.IsValidName(row.Name))
                return Reject(row, Localization.GetString("Resources/RemoteUserEditor_InvalidName", row.Name));

            if (!seen.Add(row.Name))
                return Reject(row, Localization.GetString("Resources/RemoteUserEditor_DuplicateName", row.Name));

            if (!RemoteUserRules.IsWellFormedHash(row.PasswordHash))
                return Reject(row, Localization.GetString("Resources/RemoteUserEditor_PasswordRequired", row.Name));
        }

        // 具体型（配列）を経由する ── コレクション式を IReadOnlyList<T> へ直接向けると
        // 実体の型が決まらず、トリミング／AOT で CsWinRT1032 になる。
        RemoteUserDefinition[] built =
        [
            .. _users.Select(row => new RemoteUserDefinition
            {
                Name = row.Name,
                Role = row.Role,
                PasswordHash = row.PasswordHash,
            })
        ];
        edited = built;
        return true;
    }

    /// <summary>拒否した行を選び直してから理由を出す（どの行が悪いのか分かるように）。</summary>
    private bool Reject(RemoteUserRow row, string message)
    {
        UserList.SelectedItem = row;
        ShowError(message);
        return false;
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void ClearError()
    {
        ErrorBar.Message = "";
        ErrorBar.IsOpen = false;
    }
}
