using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ProcessRecorderApp.SingleInstance;

/// <summary>
/// 「Activateせずサイレントに処理する」結果をユーザーに知らせるためのトースト通知。
/// タスクトレイアイコン自体はWinUIExに統一したため、通知はWindows App SDK標準の
/// AppNotificationManager（トースト通知）を使用する。
/// </summary>
public static class Notifications
{
    private static bool _registered;

    /// <summary>
    /// アンパッケージアプリがトースト通知を利用するために必要な登録処理。
    /// アプリ起動時に一度だけ呼び出す。
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        AppNotificationManager.Default.Register();
        _registered = true;
    }

    public static void ShowToast(string title, string body)
    {
        EnsureRegistered();

        var builder = new AppNotificationBuilder()
            .AddText(title)
            .AddText(body);

        AppNotificationManager.Default.Show(builder.BuildNotification());
    }
}
