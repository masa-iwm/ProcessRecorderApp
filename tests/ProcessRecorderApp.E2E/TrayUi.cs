using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3（GUI・UIA）のうち<b>ウィンドウの外</b>にあるもの ── タスクトレイアイコンの
/// 右クリックメニュー。<c>Category=Fragile</c> の由来がここにある。
///
/// <para>
/// <b>実現できる根拠（実測）。</b> このメニューはシェルの Win32 メニューではなく、
/// <c>SingleInstanceManager.OnTrayIconContextMenu</c> が組み立てる WinUI の
/// <c>MenuFlyout</c> である。したがってアプリのプロセスが所有する
/// <c>Microsoft.UI.Content.PopupWindowSiteBridge</c>（"PopupHost"）としてデスクトップ直下に現れ、
/// その中の <c>MenuItem</c> の Name は <c>.resw</c> の値そのものになる。
/// <b>プロセス ID で自分のメニューだと確定できる</b>のがこの経路の要点で、
/// 通知領域側の文言や並びには依存しない。
/// </para>
///
/// <para>
/// <b>ここが Fragile である理由は3つ。</b> いずれもシェル側の都合で、製品とは無関係:
/// <list type="number">
/// <item>Windows 11 では新しいトレイアイコンは既定で<b>オーバーフロー</b>に入る。
///   開くにはシェブランを押す必要があり、これは<b>トグル</b>なので
///   「開いているときに押すと閉じる」（ナビのサブメニューで踏んだのと同じ罠）。</item>
/// <item>どのアイコンが自分のものかは通知領域からは<b>確定できない</b>。
///   ツールチップは製品名（<see cref="TrayIconNameHint"/>）になるが、
///   死んだプロセスのアイコンが同じ名前で並ぶことがある。そのため
///   <b>右クリックしてみて、自分のプロセスのメニューが出たものが自分のアイコン</b>
///   と決める（名前は試す順序のヒントにしか使わない）。</item>
/// <item><b>物理的なマウスカーソルを動かす。</b> UIA の Invoke では
///   <c>WM_CONTEXTMENU</c> 相当が飛ばないため、実際に右クリックするしかない。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>ホバーしてから押す。</b> カーソルを置いてすぐ（250ms）右クリックすると
/// メニューが出ないことがあり、800ms 置くと安定した（実測）。
/// </para>
/// </summary>
public sealed class TrayUi : IDisposable
{
    /// <summary>
    /// メニューが出るまでに使ってよい総時間（候補を順に試すぶんを含む）。
    /// <b>厚く取る。</b> 常駐ワーカーを起動した直後は、シェルがまだ新しいアイコンを
    /// オーバーフローの一覧に載せていないことがある ── 前のケースが残した**死んだ
    /// プロセスのアイコン**が同じ名前（<see cref="TrayIconNameHint"/>）で並ぶので、
    /// 「1周して見つからない」は普通に起きる。周回できる回数が足りないと、
    /// **製品とは無関係な理由で赤になる**。
    /// </summary>
    public static readonly TimeSpan MenuBudget = TimeSpan.FromSeconds(150);

    /// <summary>
    /// 1つの候補を右クリックしてから、自分のメニューを諦めるまで。
    /// <b>短くして周回数を稼ぐ</b> ── 出るときは 1 秒以内に出る（実測）。
    /// </summary>
    private static readonly TimeSpan PerCandidateBudget = TimeSpan.FromSeconds(3);

    /// <summary>カーソルを置いてから押すまで。250ms では出ないことがある（実測）。</summary>
    private static readonly TimeSpan HoverBeforeClick = TimeSpan.FromMilliseconds(800);

    /// <summary>オーバーフローを開くまでに使ってよい時間（シェブランはトグルなので開き直しを含む）。</summary>
    private static readonly TimeSpan OverflowOpenBudget = TimeSpan.FromSeconds(12);

    // --- シェル側の識別子。いずれも Windows 11 24H2 で実測した値 ---
    private const string TaskbarClassName = "Shell_TrayWnd";
    private const string TrayButtonClassName = "SystemTray.NormalButton";
    private const string OverflowIconAutomationId = "NotifyItemIcon";
    private const string OverflowWindowClassMarker = "Overflow";

    /// <summary>アプリのメニューを載せている WinUI のポップアップ。</summary>
    private const string PopupClassMarker = "PopupWindowSiteBridge";

    /// <summary>
    /// 試す順序のヒントにだけ使う名前。WinUIEx は
    /// <c>WindowManager.AddToTray()</c> で <c>AppWindow.Title</c> をツールチップに渡す
    /// （<b>登録時の1回きり</b>で、以後 <c>AppWindow.Title</c> には追従しない）。
    ///
    /// <para>
    /// ツールチップの実体は <c>AppWindow.Title</c> で決まる（<c>MainWindow</c> の ctor で
    /// 設定している）。<c>MainWindow.xaml</c> の <c>&lt;TitleBar Title="..."&gt;</c> は
    /// コントロールの見た目を変えるだけでキャプションには効かない（microsoft-ui-xaml#10557）
    /// ── ctor で設定しないと WinUI 3 の既定値 <c>"WinUI Desktop"</c> のままになる。
    /// </para>
    /// <b>一致しなくても失敗にはしない</b> ── 最終的な判定はプロセス ID で行う。
    /// </summary>
    private const string TrayIconNameHint = GuiSmokeTests.WindowTitle;

    private readonly UIA3Automation _automation = new();
    private readonly AppInstance _instance;

    /// <summary>メニューを所有しているはずの常駐ワーカーの pid。</summary>
    public int ProcessId { get; }

    private TrayUi(AppInstance instance, int processId)
    {
        _instance = instance;
        ProcessId = processId;
    }

    /// <summary>
    /// 常駐ワーカー（<c>activity.log</c> の <c>app.start pid=</c>）に紐づける。
    /// <b>ウィンドウは出さない</b> ── Launch-to-Tray のまま、トレイだけを相手にする。
    /// </summary>
    public static TrayUi AttachTo(AppInstance instance) =>
        new(instance, AppUi.ResolveWorkerPid(instance));

    /// <summary>
    /// トレイアイコンを右クリックしてメニューを開き、そのポップアップを返す。
    /// 既に開いていれば開き直さない。
    ///
    /// <para>
    /// <b>周回のたびにオーバーフローを開き直す。</b> 開いたままのオーバーフローは
    /// <b>中身が古いまま</b>で、後から登録されたアイコン（＝このテストが起動した
    /// 常駐ワーカーのもの）が現れない。<b>フルスイートで実際にこれで落ちた</b>
    /// ── 前のケースが表明で落ちてオーバーフローを開いたまま終わり、次のケースが
    /// その古い一覧を相手に右クリックし続けた（成功する実行では「表示」を押した拍子に
    /// フォアグラウンドが移ってオーバーフローが閉じるので、たまたま毎回新しくなっていた）。
    /// </para>
    /// </summary>
    public AutomationElement OpenMenu()
    {
        if (FindOwnMenu() is { } already)
            return already;

        var deadline = Stopwatch.StartNew();
        var tried = new List<string>();

        for (int round = 0; ; round++)
        {
            // 1周目は名前のヒントに一致するものだけ ── 他のアプリのトレイメニューを
            // 無用に開かないため。見つからなければ2周目以降で全部試す。
            CloseOverflow();
            foreach (var icon in TrayIconCandidates(hintOnly: round == 0))
            {
                tried.Add(FirstLineOfName(icon));

                if (!TryRightClick(icon))
                    continue;

                if (WaitForOwnMenu(PerCandidateBudget) is { } menu)
                    return menu;

                // 自分のものではなかった（別アプリのメニューが開いているかもしれない）。
                DismissMenu();
            }

            if (deadline.Elapsed > MenuBudget)
            {
                throw new InvalidOperationException(
                    $"トレイアイコンを右クリックしても、pid {ProcessId} が所有するメニューが " +
                    $"{MenuBudget.TotalSeconds:F0} 秒以内に現れませんでした。" +
                    $" 試したアイコン: [{string.Join(" | ", tried)}]" +
                    Environment.NewLine + _instance.DiagnosticDump());
            }

            // 候補が0件のまま回ることがある（オーバーフローがまだ描かれていない等）。
            // この場合 foreach も即座に返るので、眠らないと締め切りまで CPU を焼き続ける。
            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// メニューを開いて項目名を読む。<b>「開く」と「読む」を1つの単位としてやり直す。</b>
    ///
    /// <para>
    /// このフライアウトはライトディスミス（フォーカスが移ると閉じる）なので、
    /// <see cref="OpenMenu"/> が返った直後に消えていることがある
    /// ── 実際にフルスイートで「メニューが開いていません」で落ちた。
    /// <b>開いた状態を跨いで2回呼ぶ形にしない。</b>
    /// </para>
    /// </summary>
    public IReadOnlyList<string> OpenAndReadMenuItemNames()
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            OpenMenu();
            try
            {
                var names = MenuItemNames();
                if (names.Count > 0)
                    return names;
            }
            catch (Exception) when (deadline.Elapsed < MenuBudget)
            {
                // 読んでいる途中で閉じた。開き直す。
            }

            if (deadline.Elapsed >= MenuBudget)
            {
                throw new InvalidOperationException(
                    $"トレイメニューを開いて項目を読むまでを {MenuBudget.TotalSeconds:F0} 秒以内に" +
                    "完了できませんでした（開いた直後に閉じている）。" +
                    Environment.NewLine + _instance.DiagnosticDump());
            }
            Thread.Sleep(300);
        }
    }

    /// <summary>開いているメニューの項目名（表示順）。</summary>
    public IReadOnlyList<string> MenuItemNames() =>
        [.. MenuItems().Select(item => item.Properties.Name.ValueOrDefault ?? "")];

    /// <summary>開いているメニューの <c>MenuItem</c>（表示順）。</summary>
    public IReadOnlyList<AutomationElement> MenuItems()
    {
        var popup = FindOwnMenu()
            ?? throw new InvalidOperationException("メニューが開いていません。先に OpenMenu() を呼んでください。");

        return MenuItemsIn(popup);
    }

    /// <summary>
    /// ポップアップの中の <c>MenuItem</c>。ポップアップの中身は数項目しかないので
    /// 標準の子孫探索で十分速い（<c>logListView</c> のような応答しないサブツリーは無い）。
    /// </summary>
    private static IReadOnlyList<AutomationElement> MenuItemsIn(AutomationElement popup)
    {
        try
        {
            return [.. popup.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))];
        }
        catch (Exception)
        {
            // 探索中に閉じた。呼び出し側は「まだ出ていない」として扱えばよい。
            return [];
        }
    }

    /// <summary>
    /// メニュー項目を位置で選ぶ。閉じていたら開き直す
    /// （<see cref="OpenAndReadMenuItemNames"/> と同じ理由）。
    /// </summary>
    public void InvokeMenuItem(int index)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            OpenMenu();
            try
            {
                var items = MenuItems();
                if (index < items.Count)
                {
                    items[index].Patterns.Invoke.Pattern.Invoke();
                    return;
                }
                if (items.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"メニュー項目 [{index}] がありません（{items.Count} 件: " +
                        $"[{string.Join(" | ", items.Select(i => i.Properties.Name.ValueOrDefault))}]）。");
                }
            }
            catch (Exception) when (deadline.Elapsed < MenuBudget)
            {
                // 開いた直後に閉じた／要素が作り直された。開き直す。
            }

            if (deadline.Elapsed >= MenuBudget)
            {
                throw new InvalidOperationException(
                    $"メニュー項目 [{index}] を {MenuBudget.TotalSeconds:F0} 秒以内に押せませんでした。" +
                    Environment.NewLine + _instance.DiagnosticDump());
            }
            Thread.Sleep(300);
        }
    }

    /// <summary>開いているメニュー（自分のものとは限らない）を Esc で閉じる。</summary>
    public void DismissMenu()
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(400);
    }

    /// <summary>
    /// アプリのメインウィンドウがデスクトップ直下に現れるまで待つ
    /// （トレイメニューの「表示」を押した後の確認に使う）。
    /// </summary>
    public bool WaitForMainWindow(TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        do
        {
            bool visible = OwnTopLevelWindows()
                .Any(w => !ClassNameOf(w).Contains(PopupClassMarker, StringComparison.Ordinal));
            if (visible)
                return true;
            Thread.Sleep(250);
        }
        while (deadline.Elapsed < timeout);
        return false;
    }

    // ---- 通知領域 ----

    /// <summary>
    /// 右クリックしてみる候補（オーバーフローの中と、昇格済みの通知領域の両方）。
    /// <paramref name="hintOnly"/> のときは <see cref="TrayIconNameHint"/> に一致するものだけを返す
    /// ── これは「他のアプリのアイコンを無用に右クリックしない」ための絞り込みであって、
    /// 判定条件ではない（一致が無ければ次の周回で全部試す）。
    /// </summary>
    private IReadOnlyList<AutomationElement> TrayIconCandidates(bool hintOnly)
    {
        var candidates = new List<AutomationElement>();
        candidates.AddRange(OverflowIcons());
        candidates.AddRange(PromotedIcons());

        var hinted = candidates.Where(icon => FirstLineOfName(icon) == TrayIconNameHint).ToArray();
        if (hintOnly)
            return hinted;

        return [.. hinted, .. candidates.Except(hinted)];
    }

    private IReadOnlyList<AutomationElement> OverflowIcons()
    {
        var overflow = EnsureOverflowOpen();
        if (overflow is null)
            return [];

        return [.. overflow.FindAllDescendants(cf => cf.ByAutomationId(OverflowIconAutomationId))];
    }

    /// <summary>
    /// 通知領域に昇格しているアイコン。シェブラン（＝オーバーフローを開くボタン）は
    /// <b>アイコン画像を子に持たない</b>ので、そこで見分ける。
    /// </summary>
    private IReadOnlyList<AutomationElement> PromotedIcons()
    {
        var taskbar = FindTaskbar();
        if (taskbar is null)
            return [];

        return [.. taskbar
            .FindAllDescendants(cf => cf.ByClassName(TrayButtonClassName))
            .Where(HasIconImage)];
    }

    private AutomationElement? FindTaskbar() =>
        _automation.GetDesktop()
            .FindAllChildren()
            .FirstOrDefault(w => ClassNameOf(w) == TaskbarClassName);

    /// <summary>
    /// オーバーフローを開いて返す（開けなければ null）。
    ///
    /// <para>
    /// <b>開いているときにシェブランを押さないこと。</b> トグルなので、押すと閉じる。
    /// これは L3 の他の箇所（ナビのサブメニューの <c>Expand()</c>）で踏んだのと同じ罠で、
    /// 「1回押して、あとは待つだけ」にすると<b>閉じた瞬間に当たったときだけ</b>落ちる。
    /// </para>
    /// </summary>
    private AutomationElement? EnsureOverflowOpen()
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (FindOverflowWindow() is { } open)
                return open;

            if (deadline.Elapsed > OverflowOpenBudget)
                return null;

            var taskbar = FindTaskbar();
            var chevron = taskbar?
                .FindAllDescendants(cf => cf.ByClassName(TrayButtonClassName))
                .FirstOrDefault(b => !HasIconImage(b));
            if (chevron is null)
                return null;

            try
            {
                chevron.Patterns.Invoke.Pattern.Invoke();
            }
            catch (Exception)
            {
                // シェルの要素は探索中に作り直されることがある。次の周回で取り直す。
            }
            Thread.Sleep(700);
        }
    }

    private AutomationElement? FindOverflowWindow() =>
        _automation.GetDesktop()
            .FindAllChildren()
            .FirstOrDefault(w => ClassNameOf(w).Contains(OverflowWindowClassMarker, StringComparison.Ordinal));

    /// <summary>
    /// オーバーフローが開いていれば閉じる。<b>開き直して中身を取り直すために必要</b>
    /// ── 開いたままの一覧には、後から登録されたアイコンが現れない。
    /// </summary>
    private void CloseOverflow()
    {
        if (FindOverflowWindow() is null)
            return;

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (FindOverflowWindow() is null)
                return;
            Thread.Sleep(200);
        }
        // 閉じられなくても続行する（開いたままでも見つかることはある）。
    }

    private static bool HasIconImage(AutomationElement button)
    {
        try
        {
            return button.FindAllDescendants(cf => cf.ByControlType(ControlType.Image)).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>カーソルを置いてから右クリックする。要素が消えていたら false。</summary>
    private static bool TryRightClick(AutomationElement icon)
    {
        Rectangle bounds;
        try
        {
            bounds = icon.Properties.BoundingRectangle.ValueOrDefault;
        }
        catch (Exception)
        {
            return false;
        }
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        Mouse.MoveTo(center);
        Thread.Sleep(HoverBeforeClick);
        Mouse.RightClick(center);
        return true;
    }

    // ---- 自分のポップアップ ----

    private AutomationElement? WaitForOwnMenu(TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        do
        {
            if (FindOwnMenu() is { } menu)
                return menu;
            Thread.Sleep(250);
        }
        while (deadline.Elapsed < timeout);
        return null;
    }

    /// <summary>
    /// <b>「ポップアップが在る」ではなく「項目が入っている」まで見る。</b>
    /// ポップアップのウィンドウは <c>MenuFlyout</c> の中身が組み上がる<b>前に</b>現れる
    /// ── フルスイートで実際にここで落ちた（項目 0 件を読んで
    /// 「期待 [Show | Quit] / 実際 []」）。単独実行では間に合っていたので、
    /// <b>「1回見て、あとは待つだけ」ではなく、欲しい条件そのものを待つ</b>。
    /// </summary>
    private AutomationElement? FindOwnMenu()
    {
        foreach (var window in OwnTopLevelWindows())
        {
            if (!ClassNameOf(window).Contains(PopupClassMarker, StringComparison.Ordinal))
                continue;
            if (MenuItemsIn(window).Count > 0)
                return window;
        }
        return null;
    }

    /// <summary>常駐ワーカーが所有するデスクトップ直下のウィンドウ。</summary>
    private IReadOnlyList<AutomationElement> OwnTopLevelWindows() =>
        [.. _automation.GetDesktop().FindAllChildren().Where(w => SafeProcessId(w) == ProcessId)];

    private static int SafeProcessId(AutomationElement element)
    {
        try
        {
            return element.Properties.ProcessId.ValueOrDefault;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string ClassNameOf(AutomationElement element)
    {
        try
        {
            return element.Properties.ClassName.ValueOrDefault ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// 通知領域のアイコン名は複数行のことがある（タスクマネージャーは CPU/メモリを並べる）。
    /// 診断と順序付けには1行目だけを使う。
    /// </summary>
    private static string FirstLineOfName(AutomationElement element)
    {
        try
        {
            string name = element.Properties.Name.ValueOrDefault ?? "";
            return name.Split('\n')[0].Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// <b>開いたままのものを必ず閉じてから終わる。</b> 表明が落ちてメニューや
    /// オーバーフローを開いたまま抜けると、<b>次のケースが古い一覧を相手に走る</b>
    /// ── フルスイートで実際にこの連鎖で2件目が落ちた。
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (FindOwnMenu() is not null)
                DismissMenu();
            CloseOverflow();
        }
        catch (Exception)
        {
            // 後始末はベストエフォート。ここでの失敗でテストの結果を書き換えない。
        }
        _automation.Dispose();
    }
}
