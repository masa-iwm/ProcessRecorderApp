using Microsoft.UI.Windowing;

namespace ProcessRecorderApp.Views;

/// <summary>
/// プレビューの全画面表示の出入りと、「いま全画面か」の共有状態。
///
/// <para>
/// <b>なぜ <c>AppWindow.Presenter.Kind</c> を直接見ないのか。</b>
/// 全画面へ入るとき、<c>SizeChanged</c> は<b>プレゼンターが差し替わる前に、
/// しかし新しい（画面いっぱいの）大きさで</b>発火する ── 実測（<c>diag.size</c> を
/// 仕込んで確認）:
/// </para>
/// <code>
/// state=Normal presenter=Overlapped size=1280x720   ← 起動時
/// state=Normal presenter=Overlapped size=1600x900   ← 全画面へ入った直後（まだ Overlapped）
/// state=Normal presenter=Overlapped size=1600x900
/// </code>
/// <para>
/// つまり <c>SizeChanged</c> の中でプレゼンターを見ても全画面だと分からず、
/// <c>MainWindow</c> が<b>画面いっぱいの大きさを <c>WindowWidth</c>/<c>WindowHeight</c> へ
/// 焼き込んでしまう</b>（次回起動が全画面サイズで開き、戻す手段は設定ファイルの手編集だけ）。
/// WinUIEx の <c>WindowState</c> も <c>Normal</c> のままなので当てにならない。
/// </para>
/// <para>
/// そこで<b>入る直前に旗を立て、プレゼンターの変化を観測してから降ろす</b>。
/// 降ろすのを遅らせるのは、復帰の途中に来る <c>SizeChanged</c> を巻き込まないため
/// （そのとき保存を飛ばしても、値は入る前と同じなので失うものは無い）。
/// </para>
/// </summary>
internal static class PreviewFullScreen
{
    /// <summary>いま全画面の最中か（<c>MainWindow</c> がウィンドウサイズの保存を止めるために見る）。</summary>
    public static bool IsActive { get; private set; }

    /// <summary>
    /// 全画面へ入る前のプレゼンターの実体。
    ///
    /// <para>
    /// <b>復帰にはこの実体を使う。</b> <c>SetPresenter(AppWindowPresenterKind.Overlapped)</c> は
    /// 元の実体を返さず<b>既定の新しい OverlappedPresenter を当てる</b>ので、
    /// それで戻すと最大化状態やリサイズ可否といった元の設定が失われる。
    /// </para>
    /// <para>
    /// <b>ここが持つ（呼び出し側に持たせない）。</b> 解除の経路は複数ある
    /// （Esc・ボタン・ダブルクリック・セクション切替・トレイ格納）ので、
    /// 呼び出し側に引き回させると<b>渡し忘れた経路だけ元の状態を失う</b>
    /// ── 実際にトレイ格納の経路だけが既定プレゼンターで戻していた。
    /// </para>
    /// </summary>
    private static AppWindowPresenter? _presenterBeforeFullScreen;

    /// <summary>全画面へ入る。既に全画面なら何もしない。</summary>
    public static void Enter(AppWindow appWindow)
    {
        if (appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            return;

        _presenterBeforeFullScreen = appWindow.Presenter;

        // **SetPresenter より前に立てる。** サイズ変更の通知は presenter の差し替えより先に来る。
        IsActive = true;
        try
        {
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        catch
        {
            // **失敗したら旗を必ず降ろす。** 立てたままにすると、以後このプロセスの
            // あいだウィンドウサイズが一切保存されなくなる（無音で・次回起動まで気付けない）。
            IsActive = false;
            _presenterBeforeFullScreen = null;
            throw;
        }
    }

    /// <summary>
    /// 全画面から戻る（入る前のプレゼンターの実体へ）。全画面でなければ何もしない。
    /// </summary>
    public static void Exit(AppWindow appWindow)
    {
        if (appWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
        {
            // 既に戻っているのに旗が立ったままなら整合させる（サイズ保存の再開）。
            NotifyPresenterChanged(appWindow);
            return;
        }

        if (_presenterBeforeFullScreen is { } previous)
            appWindow.SetPresenter(previous);
        else
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        _presenterBeforeFullScreen = null;

        // 旗はここでは降ろさない（NotifyPresenterChanged が観測してから降ろす）。
    }

    /// <summary>
    /// プレゼンターが実際に変わったことを知らせる（<c>MainWindow</c> の
    /// <c>AppWindow.Changed</c> から呼ぶ）。ここで初めて旗を実態に合わせる。
    /// </summary>
    public static void NotifyPresenterChanged(AppWindow appWindow)
        => IsActive = appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
}
