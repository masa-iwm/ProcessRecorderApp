using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace ProcessRecorderApp.E2E;

/// <summary>MainPage の画面区分（製品側 <c>MainSection</c> と同じ並び）。</summary>
public enum UiSection
{
    Preview,
    Log,
    Variables,
    Settings,
}

/// <summary>
/// L3（GUI・UIA）の土台。<see cref="AppInstance"/> が起動した常駐ワーカーのウィンドウを
/// UIA で掴み、要素の探索と画面切替を提供する。
///
/// <para>
/// <b>要素を探す前に必ず画面を切り替え、かつ切り替えが成功したことを先に表明する。</b>
/// <c>Visibility=Collapsed</c> のパネル配下は UIA ツリーに現れないため、切り替えに失敗したまま
/// 要素を探すと「要素が無い」という形で落ちる ── 原因が製品なのか手順なのか区別が付かない。
/// <see cref="SwitchTo"/> は切替後にそのパネル固有の目印が見えることを確認してから返る。
/// </para>
///
/// <para>
/// <b>合成した Alt+F4 はこのアプリに届かない。</b> ウィンドウを閉じるときは
/// <see cref="CloseWindow"/>（タイトルバーの <c>AutomationId='Close'</c> を Invoke する）を使う。
/// </para>
/// </summary>
public sealed class AppUi : IDisposable
{
    /// <summary>要素が現れるまでの既定の待ち。WARP でのプレビュー初期化を挟むので短くしない。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// ウィンドウが現れるまでの待ち。初回は XAML の構築とプレビュー初期化を含む。
    ///
    /// <para>
    /// <b>60 秒では CI ランナーに届かない。</b>
    /// <c>ShutdownTests.CtrlClose_WhileRecording_FinalizesEveryFile</c> は
    /// <b>1280x720/30fps を録画している最中に</b>ウィンドウを出すので、
    /// 2 vCPU のランナーでは UI スレッドが飢えてアクティブ化が間に合わない。
    /// <b>これは打ち切り（異常検出の上限）であって下限の表明ではない</b>ので、
    /// 厚く取ってよい ── 緩めてよいのは打ち切りだけで、下限の表明は緩めない。
    /// </para>
    /// <para>
    /// <b>飢えの原因そのものは減らしてある</b> ──
    /// 上記のケースの録画は <see cref="RecorderSpec.AsBulkyButCheapToEncode"/>
    /// （バイト数は据え置きで画素数だけを落とす）を使う。
    /// <b>それでもこの 180 秒は縮めないこと</b>: ここは打ち切りであって、
    /// 短くして得るものが無い一方、ランナーの遅さは実行ごとに揺れる。
    /// </para>
    /// </summary>
    public static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(180);

    /// <summary>画面切替の目印。ここに挙げた要素が見えて初めて「切り替わった」と見なす。</summary>
    private static readonly Dictionary<UiSection, string> SectionMarkers = new()
    {
        [UiSection.Preview] = "TogglePaneButton",
        [UiSection.Log] = "ClearLogButton",
        [UiSection.Variables] = "VariablesTable",
        // Settings パネルは AppSettings を映す PropertyGridView。行の AutomationId は
        // CLR プロパティ名なので、ロケールに依存しない目印として使える。
        [UiSection.Settings] = "GstDebug",
    };

    /// <summary>ナビゲーション項目の AutomationId（WinUI は <c>x:Name</c> をそのまま AutomationId にする）。</summary>
    private static readonly Dictionary<UiSection, string> NavItems = new()
    {
        [UiSection.Preview] = "previewNavItem",
        [UiSection.Log] = "logNavItem",
        [UiSection.Variables] = "variablesNavItem",
    };

    private readonly UIA3Automation _automation;
    private readonly AppInstance _instance;

    public int ProcessId { get; }
    public Window Window { get; private set; }

    private AppUi(AppInstance instance, UIA3Automation automation, Window window, int processId)
    {
        _instance = instance;
        _automation = automation;
        Window = window;
        ProcessId = processId;
    }

    /// <summary>
    /// <c>activate</c> でウィンドウを表示させ、UIA で掴む。
    /// 常駐ワーカーの pid は <c>activity.log</c> の <c>app.start pid=</c> から取る
    /// ── コールドスタート経路ではランチャーが暗黙に起動したワーカーが子プロセスではないため。
    /// </summary>
    public static AppUi Activate(AppInstance instance)
    {
        var activate = instance.Run("activate");
        if (activate.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "activate が失敗しました。" + Environment.NewLine + activate + Environment.NewLine +
                instance.DiagnosticDump());
        }

        int pid = ResolveWorkerPid(instance);
        var automation = new UIA3Automation();
        try
        {
            var window = FindWindow(automation, pid, WindowTimeout)
                ?? throw new InvalidOperationException(
                    $"pid {pid} のウィンドウが {WindowTimeout.TotalSeconds:F0} 秒以内に現れませんでした。" +
                    Environment.NewLine + instance.DiagnosticDump());

            var ui = new AppUi(instance, automation, window, pid);
            // Preview が既定の選択。ここが解決できないなら MainPage が Loaded まで
            // 到達していないので、以降のテストは全て「要素が無い」で落ちる。
            ui.WaitForElement(SectionMarkers[UiSection.Preview]);
            return ui;
        }
        catch
        {
            automation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <c>activity.log</c> の <c>app.start pid=</c> から生存している常駐ワーカーの pid を取る。
    /// <see cref="TrayUi"/>（ウィンドウを出さずトレイだけを相手にする）からも使う。
    /// </summary>
    internal static int ResolveWorkerPid(AppInstance instance)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var pids = ActivityLogFile.WorkerPids(instance.ReadActivityLog())
                .Where(IsLiveWorker)
                .ToList();
            if (pids.Count > 0)
                return pids[^1];
            Thread.Sleep(200);
        }

        throw new InvalidOperationException(
            "activity.log から生存している常駐ワーカーの pid を取れませんでした。" +
            Environment.NewLine + instance.DiagnosticDump());
    }

    private static bool IsLiveWorker(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited
                && process.ProcessName.Equals("ProcessRecorderApp", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Window? FindWindow(UIA3Automation automation, int pid, TimeSpan timeout)
    {
        // 必ず1回は見る（timeout が 0 でも「見ずに無い」と答えてはいけない）。
        var deadline = Stopwatch.StartNew();
        do
        {
            var match = automation.GetDesktop()
                .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                .FirstOrDefault(w => SafeProcessId(w) == pid);
            if (match is not null)
                return match.AsWindow();
            if (deadline.Elapsed >= timeout)
                break;
            Thread.Sleep(250);
        }
        while (deadline.Elapsed < timeout);
        return null;
    }

    private static int SafeProcessId(AutomationElement element)
    {
        try
        {
            return element.Properties.ProcessId.ValueOrDefault;
        }
        catch (Exception)
        {
            // 探索中に閉じたウィンドウ。除外すればよい。
            return -1;
        }
    }

    // ---- 要素の探索 ----

    /// <summary>
    /// <b>UIA の子要素の列挙に応答しないことが実測で分かっているサブツリー。</b>
    /// ここへは降りない。
    ///
    /// <para>
    /// Log 画面の <c>logListView</c> は <c>FindAllChildren()</c> が <b>25 秒待っても返らない</b>
    /// （他の要素はすべて 1 桁ミリ秒で応答する。階層ごとの実測で特定した）。
    /// 標準の <c>FindFirstDescendant</c> はツリーを網羅するため、Log 画面を表示している間は
    /// <b>目的の要素が別の場所にあっても</b>タイムアウトする ── 実際、
    /// <c>ClearLogButton</c>（logListView の兄弟）の探索がそれで落ちていた。
    /// そのため探索は自前の深さ優先walkで行い、このサブツリーだけ展開しない。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> OpaqueSubtrees = new(StringComparer.Ordinal)
    {
        "logListView",
    };

    /// <summary>AutomationId で要素を探す（見つからなければ null）。</summary>
    public AutomationElement? TryFindElement(string automationId, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var budget = timeout ?? DefaultTimeout;
        do
        {
            if (SearchTree(Window, automationId, 0) is { } element)
                return element;
            Thread.Sleep(150);
        }
        while (deadline.Elapsed < budget);
        return null;
    }

    /// <summary>
    /// 深さ優先で AutomationId を探す。<see cref="OpaqueSubtrees"/> には降りない。
    /// 探索中に消えた要素（ダイアログの開閉など）は無視して続行する。
    /// </summary>
    private static AutomationElement? SearchTree(AutomationElement root, string automationId, int depth)
    {
        if (depth > 24)
            return null;

        AutomationElement[] children;
        try
        {
            children = root.FindAllChildren();
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var child in children)
        {
            string id;
            try
            {
                id = child.Properties.AutomationId.ValueOrDefault ?? "";
            }
            catch (Exception)
            {
                continue;
            }

            if (id == automationId)
                return child;
            if (OpaqueSubtrees.Contains(id))
                continue;
            if (SearchTree(child, automationId, depth + 1) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>AutomationId で要素を探す（見つからなければ診断付きで失敗させる）。</summary>
    public AutomationElement WaitForElement(string automationId, TimeSpan? timeout = null)
        => TryFindElement(automationId, timeout)
           ?? throw new InvalidOperationException(
               $"AutomationId='{automationId}' の要素が見つかりませんでした。" + Environment.NewLine +
               DescribeVisibleElements() + Environment.NewLine + _instance.DiagnosticDump());

    /// <summary>要素が消えるまで待つ（トレイ格納やダイアログの終了の確認に使う）。</summary>
    public bool WaitUntilGone(string automationId, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var budget = timeout ?? DefaultTimeout;
        do
        {
            if (SearchTree(Window, automationId, 0) is null)
                return true;
            Thread.Sleep(150);
        }
        while (deadline.Elapsed < budget);
        return false;
    }

    /// <summary>同じ AutomationId を持つ要素をすべて集める（PropertyGrid は画面に2つある）。</summary>
    public IReadOnlyList<AutomationElement> FindAll(string automationId)
    {
        var found = new List<AutomationElement>();
        CollectMatches(Window, automationId, found, 0);
        return found;
    }

    private static void CollectMatches(AutomationElement root, string automationId, List<AutomationElement> found, int depth)
    {
        if (depth > 24)
            return;

        AutomationElement[] children;
        try
        {
            children = root.FindAllChildren();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var child in children)
        {
            string id;
            try
            {
                id = child.Properties.AutomationId.ValueOrDefault ?? "";
            }
            catch (Exception)
            {
                continue;
            }

            if (id == automationId)
                found.Add(child);
            if (OpaqueSubtrees.Contains(id))
                continue;
            CollectMatches(child, automationId, found, depth + 1);
        }
    }

    /// <summary>
    /// 要素の自動化名（＝ローカライズされた表示文言）。
    /// AutomationId はロケール非依存なので、<b>探すのは Id・照合するのは Name</b> という
    /// 使い分けになる（言語強制のマトリクスがまさにこれ）。
    /// </summary>
    public string GetAutomationName(string automationId) => NameOf(WaitForElement(automationId));

    /// <summary>
    /// 今 UIA ツリーに見えている自動化名をすべて集める。
    /// PropertyGrid の<b>カテゴリ見出し</b>のように AutomationId を持たない文言は、
    /// これでしか照合できない（見出しは <c>TextBlock</c> で、UIA の Name はその文字列）。
    /// </summary>
    public IReadOnlyList<string> VisibleTexts()
    {
        var names = new List<string>();
        CollectNames(Window, names, 0);
        return names;
    }

    private static void CollectNames(AutomationElement root, List<string> names, int depth)
    {
        if (depth > 24)
            return;

        AutomationElement[] children;
        try
        {
            children = root.FindAllChildren();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var child in children)
        {
            string name = NameOf(child);
            if (name.Length > 0 && !names.Contains(name))
                names.Add(name);

            string id;
            try
            {
                id = child.Properties.AutomationId.ValueOrDefault ?? "";
            }
            catch (Exception)
            {
                continue;
            }

            if (OpaqueSubtrees.Contains(id))
                continue;
            CollectNames(child, names, depth + 1);
        }
    }

    /// <summary>失敗時の診断。今 UIA ツリーに見えている AutomationId を列挙する。</summary>
    public string DescribeVisibleElements()
    {
        var ids = new List<string>();
        CollectIds(Window, ids, 0);
        ids.Sort(StringComparer.Ordinal);
        return $"--- 今見えている AutomationId ({ids.Count} 件) ---" + Environment.NewLine +
               string.Join(", ", ids);
    }

    private static void CollectIds(AutomationElement root, List<string> ids, int depth)
    {
        if (depth > 24)
            return;

        AutomationElement[] children;
        try
        {
            children = root.FindAllChildren();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var child in children)
        {
            string id;
            try
            {
                id = child.Properties.AutomationId.ValueOrDefault ?? "";
            }
            catch (Exception)
            {
                continue;
            }

            if (id.Length > 0 && !ids.Contains(id))
                ids.Add(id);
            if (OpaqueSubtrees.Contains(id))
                continue;
            CollectIds(child, ids, depth + 1);
        }
    }

    // ---- 画面切替 ----

    /// <summary>
    /// 画面を切り替え、<b>切り替わったことを確認してから</b>返る。
    /// 切り替えたつもりで失敗しているのが最も危険なので、ここで必ず表明する。
    /// </summary>
    public void SwitchTo(UiSection section)
    {
        if (section == UiSection.Preview)
        {
            SelectRecorder(null);
            return;
        }

        // NavigationView 組み込みの設定項目は AutomationId='SettingsItem'（実測）。
        string navId = section == UiSection.Settings ? "SettingsItem" : NavItems[section];
        Invoke(WaitForElement(navId));
        AssertSection(section);
    }

    private void AssertSection(UiSection section)
    {
        string marker = SectionMarkers[section];
        if (TryFindElement(marker) is null)
        {
            throw new InvalidOperationException(
                $"{section} 画面へ切り替えたつもりが、目印 '{marker}' が現れませんでした。" +
                Environment.NewLine + DescribeVisibleElements());
        }
    }

    /// <summary>
    /// Preview 画面へ切り替え、指定したレコーダーを選択する
    /// （<paramref name="recorderName"/> が null なら先頭のレコーダー）。
    ///
    /// <para>
    /// <b>Preview は <c>previewNavItem</c> を押しても選択されない</b>
    /// （<c>SelectsOnInvoked="False"</c>）。選択されるのは配下の Recorder サブ項目なので、
    /// 先に親を展開してから子を選ぶ。Recorder 項目は AutomationId が
    /// <c>RecorderNavItem</c> 共通で、個体は<b>自動化名（＝レコーダー名）</b>で見分ける
    /// ── 名前はユーザーデータで改名できるため AutomationId には焼き込まれていない。
    /// </para>
    /// <para>
    /// <b>名前を指定したときだけ、選択が PropertyGrid に反映されたことを表明する。</b>
    /// <c>null</c>（先頭を選ぶ）では「どれが選ばれたか」を照合しようがないためで、
    /// <see cref="SwitchTo"/> で Preview に戻す経路もこちらを通る。
    /// <b>レコーダーが複数ある状態で編集するテストは、必ず名前を指定して選ぶこと</b>
    /// ── さもないと、まだ前のレコーダーを映しているグリッドを編集しうる。
    /// </para>
    /// </summary>
    public void SelectRecorder(string? recorderName)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var candidates = RecorderNavItems();
            var match = recorderName is null
                ? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(e => NameOf(e) == recorderName);

            if (match is not null)
            {
                // **要素を取り直してからやり直す。** ナビ項目は改名や追加削除で作り直されるので、
                // 掴んだままの参照へ Invoke し続けても `RPC_E_SERVERFAULT` のままになる
                // ── フルスイートで実際にこれで落ちた（同じ要素に対する
                // やり直しだけでは足りず、探索ごとやり直す必要がある）。
                if (!TryInvoke(match, out var invokeFailure))
                {
                    if (deadline.Elapsed > DefaultTimeout)
                    {
                        // **最後の例外を必ず添える。** RPC_E_SERVERFAULT と RPC_E_DISCONNECTED と
                        // 「要素が無効」はまったく別の診断で、ここはこの層で何度も診断不足の
                        // 代償を払っている場所。
                        throw new InvalidOperationException(
                            $"レコーダー項目 '{recorderName ?? "(先頭)"}' を選べませんでした" +
                            $"（UIA のパターン呼び出しが失敗し続けています: {invokeFailure?.Message}）。" +
                            Environment.NewLine + DescribeVisibleElements());
                    }
                    Thread.Sleep(200);
                    continue;
                }
                AssertSection(UiSection.Preview);

                // **選択が PropertyGrid に反映されるまで待つ。** Invoke から
                // SelectedRecorder → PropertyGridView.SelectedObject → 行の再生成までは
                // 非同期に進むので、ここで待たないと「まだ前のレコーダーを映している
                // PropertyGrid」を編集してしまう。固定の sleep では取り切れない
                // （実際に AOT 発行物で1回だけ落ちた ── 速いほど手前に来るぶん危ない）。
                if (recorderName is not null && WaitForPropertyText("Name", recorderName) != recorderName)
                {
                    throw new InvalidOperationException(
                        $"レコーダー '{recorderName}' を選んだのに、PropertyGrid は " +
                        $"'{GetPropertyText("Name")}' を映したままです。" +
                        Environment.NewLine + DescribeVisibleElements());
                }
                return;
            }

            if (deadline.Elapsed > DefaultTimeout)
            {
                throw new InvalidOperationException(
                    $"レコーダー項目 '{recorderName ?? "(先頭)"}' が見つかりませんでした。" +
                    $" 見えている項目: [{string.Join(", ", candidates.Select(NameOf))}]" +
                    Environment.NewLine + DescribeVisibleElements());
            }
            Thread.Sleep(200);
        }
    }

    /// <summary>Recorder 項目に共通の AutomationId（製品側の <c>RecorderNavViewBehavior</c> と一致させる）。</summary>
    public const string RecorderNavItemAutomationId = "RecorderNavItem";

    /// <summary>
    /// ナビゲーションに指定した名前のレコーダー項目が現れるまで待ち、その時点の一覧を返す。
    /// 改名の反映は <c>PropertyChanged</c> 経由なので、読み取りが先行しうる。
    /// </summary>
    public IReadOnlyList<string> WaitForRecorderNavItemName(string expected, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var budget = timeout ?? DefaultTimeout;
        IReadOnlyList<string> names;
        do
        {
            names = RecorderNavItemNames();
            if (names.Contains(expected))
                return names;
            Thread.Sleep(150);
        }
        while (deadline.Elapsed < budget);
        return names;
    }

    /// <summary>ナビゲーションのレコーダー項目が指定件数になるまで待つ。</summary>
    public IReadOnlyList<string> WaitForRecorderNavItemCount(int expected, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var budget = timeout ?? DefaultTimeout;
        IReadOnlyList<string> names;
        do
        {
            names = RecorderNavItemNames();
            if (names.Count == expected)
                return names;
            Thread.Sleep(150);
        }
        while (deadline.Elapsed < budget);
        return names;
    }

    /// <summary>ナビゲーションに並んでいるレコーダー名（表示順）。</summary>
    public IReadOnlyList<string> RecorderNavItemNames() => [.. RecorderNavItems().Select(NameOf)];

    /// <summary>
    /// ナビゲーションの Recorder 項目を列挙する。
    ///
    /// <para>
    /// <b>見えていないときだけサブメニューを開き直す。</b> 製品の既定の
    /// <c>PaneDisplayMode</c> は Top で、Recorder 項目は Preview 配下の<b>フライアウト</b>に出る。
    /// フライアウトは <c>ContentDialog</c> の開閉などで閉じるため、
    /// 「最初に1回だけ開いて、あとは探し続ける」書き方だと、
    /// <b>閉じた瞬間に当たったときだけ待ち時間いっぱい空振りして落ちる</b>
    /// ── AOT 発行物で実際に踏んだ（`RecorderManagementTests` が
    /// 削除ダイアログの直後の <see cref="SelectRecorder"/> で 20 秒空振りした）。
    /// </para>
    /// <para>
    /// 逆に<b>見えているときに開き直してはいけない</b>。開いている項目への
    /// <c>Expand()</c> がトグルとして働くと、こちらが閉じる側の原因になる。
    /// </para>
    /// </summary>
    private IReadOnlyList<AutomationElement> RecorderNavItems()
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var found = FindAll(RecorderNavItemAutomationId);
            if (found.Count > 0 || deadline.Elapsed > SubMenuOpenBudget)
                return found;
            ExpandRecorderSubMenu();
        }
    }

    /// <summary>
    /// サブメニューを開くのに費やす上限。
    /// <b>1回 <c>Expand()</c> を呼んで諦めてはいけない</b> ── 開く操作そのものが
    /// 失敗することがあり、そのときは以後いくら探しても現れない
    /// （`AddRecorderNavItem` が 20 秒待っても見つからない形で実際に踏んだ）。
    /// </summary>
    private static readonly TimeSpan SubMenuOpenBudget = TimeSpan.FromSeconds(8);

    /// <summary>Preview のサブメニュー（Recorder 項目と「追加」）を開く。</summary>
    private void ExpandRecorderSubMenu()
    {
        var previewItem = WaitForElement(NavItems[UiSection.Preview]);
        if (!previewItem.Patterns.ExpandCollapse.IsSupported)
            return;
        previewItem.Patterns.ExpandCollapse.Pattern.Expand();
        Thread.Sleep(400);
    }

    private static string NameOf(AutomationElement element)
    {
        try
        {
            return element.Properties.Name.ValueOrDefault ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Invoke パターンがあれば Invoke、無ければ選択・クリックで代替する。
    ///
    /// <para>
    /// <b>COM の一過性の失敗は数回やり直す。</b> UIA のパターン呼び出しは、相手の UI スレッドが
    /// 詰まっていたり要素が作り直された瞬間に当たると
    /// <c>RPC_E_SERVERFAULT</c>(0x80010105) / <c>RPC_E_DISCONNECTED</c> 等で失敗する
    /// ── フルスイートで `SelectRecorder` の `Select()` が実際にこれで落ちた。
    /// <b>これは製品の不具合ではなく「1回で諦めている」というテスト側の欠陥</b>で、
    /// L3 でこれまで繰り返し踏んだ形（待ちの中にやり直しを入れていない）と同じ。
    /// </para>
    /// </summary>
    private static void Invoke(AutomationElement element)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                InvokeOnce(element);
                return;
            }
            catch (Exception) when (deadline.Elapsed < InvokeRetryBudget)
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>
    /// <see cref="Invoke"/> と同じだが、失敗したら例外にせず false を返す。
    /// <b>要素そのものが作り直されている場合、同じ参照へのやり直しでは永久に回復しない</b>
    /// ── 呼び出し側で<b>探索からやり直す</b>ために使う。
    /// </summary>
    private static bool TryInvoke(AutomationElement element, out Exception? failure)
    {
        try
        {
            InvokeOnce(element);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    private static void InvokeOnce(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return;
        }
        if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
            return;
        }
        element.Click();
    }

    /// <summary>UIA のパターン呼び出しが一過性の COM 失敗を返したときにやり直す時間。</summary>
    private static readonly TimeSpan InvokeRetryBudget = TimeSpan.FromSeconds(5);

    /// <summary>ボタン等を AutomationId で押す。</summary>
    public void Click(string automationId) => Invoke(WaitForElement(automationId));

    /// <summary>
    /// コンボボックスの現在の選択の<b>表示文字列</b>を返す（選択が無ければ空文字）。
    /// <b>表示であって保存値ではない</b> ── 表示名と値が違う項目があるので、
    /// 保存値を確かめたいなら settings.json を見ること。
    /// </summary>
    public string GetSelectedChoice(string automationId)
    {
        var combo = WaitForElement(automationId).AsComboBox();
        return combo.SelectedItem?.Name ?? "";
    }

    /// <summary>
    /// コンボボックスを開いて、選択肢の<b>表示名</b>を全部読む。
    ///
    /// <para>
    /// <b>WinUI のコンボボックスは開くまで項目を実体化しない</b>ので、
    /// 開かずに <c>Items</c> を読むと 0 件になる（エラーにはならない）。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ChoiceNames(string automationId)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            // **探索からやり直す。** 項目は作り直されるので、同じ参照に対する
            // やり直しでは回復しないことがある（L3 で繰り返し踏んだ形）。
            var combo = WaitForElement(automationId).AsComboBox();
            try
            {
                Expand(combo);
                var names = combo.Items.Select(i => i.Name).Where(n => !string.IsNullOrEmpty(n)).ToArray();
                if (0 < names.Length)
                {
                    Collapse(combo);
                    return names;
                }
            }
            catch (Exception) when (deadline.Elapsed < ChoiceBudget)
            {
                // 一過性の COM 失敗。開き直しからやり直す
            }

            if (ChoiceBudget <= deadline.Elapsed)
            {
                throw new InvalidOperationException(
                    $"コンボボックス '{automationId}' の選択肢が {ChoiceBudget.TotalSeconds:F0} 秒以内に現れませんでした。" +
                    Environment.NewLine + DescribeVisibleElements() + Environment.NewLine + _instance.DiagnosticDump());
            }
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// コンボボックスの選択肢を<b>表示名</b>で選ぶ。選ばれたことを表明してから返る。
    ///
    /// <para>
    /// <b>「開く」と「選ぶ」を1つの単位としてやり直す。</b> フライアウトはライトディスミスで、
    /// 開いた直後に閉じていることがある ── トレイメニューで同じ形に踏んでいる
    /// （<c>TrayUi.OpenAndReadMenuItemNames</c> の注記）。
    /// </para>
    /// <para>
    /// <b>選択できたことを最後に確認する。</b> 確認しないと、閉じただけで
    /// 何も選べていない状態を「成功」として通してしまう。
    /// </para>
    /// </summary>
    public void SelectChoice(string automationId, string displayName)
    {
        var deadline = Stopwatch.StartNew();
        Exception? last = null;

        while (deadline.Elapsed < ChoiceBudget)
        {
            var combo = WaitForElement(automationId).AsComboBox();
            try
            {
                Expand(combo);

                var match = combo.Items.FirstOrDefault(i => string.Equals(i.Name, displayName, StringComparison.Ordinal));
                if (match is null)
                {
                    // 開いてはいるが目的の項目が無い。閉じてやり直す
                    // （項目がまだ実体化していない場合がある）。
                    Collapse(combo);
                    Thread.Sleep(200);
                    continue;
                }

                match.Select();

                // 選択の反映は非同期。反映されるまで有界で待つ。
                var settle = Stopwatch.StartNew();
                while (settle.Elapsed < ChoiceSettleBudget)
                {
                    if (string.Equals(GetSelectedChoice(automationId), displayName, StringComparison.Ordinal))
                        return;
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(200);
            }
        }

        throw new InvalidOperationException(
            $"コンボボックス '{automationId}' で '{displayName}' を選べませんでした。" +
            $" 現在の選択: '{GetSelectedChoice(automationId)}'" +
            (last is null ? "" : Environment.NewLine + "直近の例外: " + last.Message) +
            Environment.NewLine + DescribeVisibleElements() + Environment.NewLine + _instance.DiagnosticDump());
    }

    /// <summary>
    /// <b>開いているときに <c>Expand()</c> を呼ばない。</b> トグルとして働くなら、
    /// こちらが閉じる側の原因になる（ナビのサブメニューで実際に踏んだ形）。
    /// </summary>
    private static void Expand(FlaUI.Core.AutomationElements.ComboBox combo)
    {
        if (combo.ExpandCollapseState != FlaUI.Core.Definitions.ExpandCollapseState.Expanded)
            combo.Expand();
    }

    private static void Collapse(FlaUI.Core.AutomationElements.ComboBox combo)
    {
        if (combo.ExpandCollapseState == FlaUI.Core.Definitions.ExpandCollapseState.Expanded)
            combo.Collapse();
    }

    /// <summary>コンボボックスを開いて項目を読む・選ぶまでの上限。</summary>
    private static readonly TimeSpan ChoiceBudget = TimeSpan.FromSeconds(20);

    /// <summary>選択が反映されるまでの上限。</summary>
    private static readonly TimeSpan ChoiceSettleBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// ナビゲーションの「レコーダーを追加」を押す（Preview サブメニューの末尾）。
    /// 表示文言はローカライズされるので AutomationId で特定する。
    /// </summary>
    public void AddRecorder()
    {
        // 「見えていないときだけ開く」「開けるまで繰り返す」のは
        // <see cref="RecorderNavItems"/> と同じ理由。**単純な Click に落とさないこと**
        // ── WaitForElement は探し続けるだけで開き直さないので、
        // 開く操作が1回失敗すると 20 秒待って「要素が無い」で落ちる。
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (TryFindElement("AddRecorderNavItem", TimeSpan.FromMilliseconds(300)) is { } item)
            {
                Invoke(item);
                return;
            }
            if (deadline.Elapsed > SubMenuOpenBudget)
            {
                throw new InvalidOperationException(
                    "「レコーダーを追加」の項目（AutomationId='AddRecorderNavItem'）が、" +
                    $"サブメニューを開き直しても {SubMenuOpenBudget.TotalSeconds:F0} 秒以内に現れませんでした。" +
                    Environment.NewLine + DescribeVisibleElements() + Environment.NewLine + _instance.DiagnosticDump());
            }
            ExpandRecorderSubMenu();
        }
    }

    /// <summary>
    /// <c>ContentDialog</c> の主ボタン（削除確認の「削除」）を押す。
    /// ボタンの表示文言はローカライズされるため、WinUI のテンプレート内の
    /// 名前 <c>PrimaryButton</c> で特定する。
    /// </summary>
    public void ConfirmDialog()
    {
        var button = TryFindElement("PrimaryButton", TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException(
                "確認ダイアログの主ボタン（AutomationId='PrimaryButton'）が見つかりませんでした。" +
                Environment.NewLine + DescribeVisibleElements());
        Invoke(button);
        Thread.Sleep(500);
    }

    /// <summary>
    /// AutomationId で指定した TextBox に値を入力して<b>確定</b>する
    /// （PropertyGrid の行に限らない汎用版。理由は <see cref="SetPropertyText"/> と同じ ──
    /// TwoWay バインドはフォーカスを失うまでソースへ書き戻さない）。
    /// </summary>
    public void SetText(string automationId, string value)
    {
        var box = WaitForElement(automationId).AsTextBox();
        box.Focus();
        box.Text = value;
        Keyboard.Press(VirtualKeyShort.TAB);
        Thread.Sleep(300);
    }

    /// <summary>AutomationId で指定した TextBox の現在値。</summary>
    public string GetText(string automationId) => WaitForElement(automationId).AsTextBox().Text;

    /// <summary><c>ContentDialog</c> の閉じるボタン（削除確認の「キャンセル」）を押す。</summary>
    public void CancelDialog()
    {
        var button = TryFindElement("CloseButton", TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException(
                "確認ダイアログの閉じるボタン（AutomationId='CloseButton'）が見つかりませんでした。" +
                Environment.NewLine + DescribeVisibleElements());
        Invoke(button);
        Thread.Sleep(500);
    }

    // ---- PropertyGrid の行 ----

    /// <summary>
    /// PropertyGrid の行を CLR プロパティ名で探す（行の AutomationId は
    /// <c>PropertyName</c> ＝ ロケール非依存）。
    ///
    /// <para>
    /// PropertyGrid は仮想化 ListView なので、画面外の行は UIA ツリーに現れない。
    /// 見つからない場合はスクロールしながら探す。
    /// </para>
    /// </summary>
    public AutomationElement WaitForPropertyRow(string propertyName, TimeSpan? timeout = null)
    {
        if (TryFindElement(propertyName, timeout ?? TimeSpan.FromSeconds(3)) is { } found)
            return found;

        // 仮想化で流れていった行を手繰り寄せる。
        // PropertyGrid の ListView は AutomationId='RootListView'（UserControl 内の x:Name）。
        foreach (var list in FindAll("RootListView"))
        {
            if (!list.Patterns.Scroll.IsSupported)
                continue;
            var scroll = list.Patterns.Scroll.Pattern;
            if (!scroll.VerticallyScrollable.ValueOrDefault)
                continue;

            scroll.SetScrollPercent(-1, 0);
            for (int step = 0; step <= 10; step++)
            {
                if (TryFindElement(propertyName, TimeSpan.FromMilliseconds(300)) is { } row)
                    return row;
                if (scroll.VerticalScrollPercent.ValueOrDefault >= 99)
                    break;
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                Thread.Sleep(150);
            }
        }

        throw new InvalidOperationException(
            $"PropertyGrid に '{propertyName}' の行が見つかりませんでした。" + Environment.NewLine +
            DescribeVisibleElements());
    }

    /// <summary>PropertyGrid の行（TextBox）の現在値。</summary>
    public string GetPropertyText(string propertyName)
        => WaitForPropertyRow(propertyName).AsTextBox().Text;

    /// <summary>
    /// 行の値が期待どおりになるまで待ち、最後に読めた値を返す。
    ///
    /// <para>
    /// <b>固定の <c>sleep</c> のあとに読んではいけない。</b> 入力から
    /// 「VM のセッター → 正規化 → PropertyChanged → TextBox の更新」までは非同期で、
    /// 所要時間は環境で変わる（AOT 発行物は selfcontained より速く、
    /// 手前のステップが先に来るぶんタイミングがずれる）。
    /// 期待値に達したら即座に返るので、待つこと自体は遅くならない。
    /// </para>
    /// </summary>
    public string WaitForPropertyText(string propertyName, string expected, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var budget = timeout ?? DefaultTimeout;
        string actual;
        do
        {
            actual = GetPropertyText(propertyName);
            if (actual == expected)
                return actual;
            Thread.Sleep(150);
        }
        while (deadline.Elapsed < budget);
        return actual;
    }

    /// <summary>
    /// PropertyGrid の行（TextBox）へ値を入力して<b>確定</b>する。
    ///
    /// <para>
    /// <b>Tab で確定させるのは必須。</b> WinUI の <c>TextBox.Text</c> は TwoWay バインドでも
    /// 既定ではフォーカスを失うまでソースへ書き戻さない。入力しただけで確認すると、
    /// 「UI には出ているがモデルには届いていない」状態を緑と誤読する
    /// ── 書き戻しガードを <c>==</c> に戻す退行（<see cref="PropertyEditingTests"/> の
    /// <c>EditingTheFilenameTemplate_ReachesTheModel_AndTheNextRecordingUsesIt</c> が守る）が
    /// まさにその失敗モード。
    /// </para>
    /// </summary>
    public void SetPropertyText(string propertyName, string value)
    {
        var box = WaitForPropertyRow(propertyName).AsTextBox();
        box.Focus();
        box.Text = value;
        // フォーカスを外してバインドを確定させる。
        Keyboard.Press(VirtualKeyShort.TAB);
        Thread.Sleep(300);
    }

    // ---- ウィンドウの開閉 ----

    /// <summary>
    /// タイトルバーの閉じるボタンを押す。
    /// <paramref name="holdControl"/> が true なら Ctrl を押しながら押す
    /// （＝トレイ格納ではなく本当にプロセスを終了させる経路）。
    ///
    /// <para>
    /// <b>合成した Alt+F4 は届かない。</b> <c>AutomationId='Close'</c> を Invoke する。
    /// Ctrl の状態は製品側が <c>InputKeyboardSource.GetKeyStateForCurrentThread</c> で
    /// 読むので、アプリのスレッドがキー入力を処理できるよう、
    /// <b>前面に出してから押し、少し待つ</b>。
    /// </para>
    /// </summary>
    public void CloseWindow(bool holdControl)
    {
        var close = WaitForElement("Close");

        if (!holdControl)
        {
            Invoke(close);
            return;
        }

        Window.SetForeground();
        Thread.Sleep(300);
        Keyboard.Pressing(VirtualKeyShort.CONTROL);
        try
        {
            // アプリのスレッドが WM_KEYDOWN を処理する余裕を作る。
            Thread.Sleep(400);
            Invoke(close);
            Thread.Sleep(400);
        }
        finally
        {
            Keyboard.Release(VirtualKeyShort.CONTROL);
        }
    }

    /// <summary>
    /// ウィンドウが UIA ツリーから消えるまで待つ（＝トレイ格納された）。
    /// プロセスが生きたまま隠されるので、プロセスの終了待ちでは判定できない。
    /// </summary>
    public bool WaitUntilWindowIsGone(TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (FindWindow(_automation, ProcessId, TimeSpan.Zero) is null)
                return true;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>常駐ワーカーのプロセスが終了するまで待つ。</summary>
    public bool WaitForProcessExit(TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true; // 既に終了している
        }
    }

    /// <summary>常駐ワーカーのプロセスがまだ生きているか。</summary>
    public bool IsProcessAlive => IsLiveWorker(ProcessId);

    public void Dispose() => _automation.Dispose();
}
