using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// Web UI（<c>src/RemoteControl/wwwroot/app.js</c>）を<b>本物のブラウザで</b>動かす層。
///
/// <para>
/// <b>ここだけが app.js を実行する。</b> L1 は資産の台帳を、L2 の他のクラスは HTTP の応答を
/// 見るだけで、L3（UIA）はブラウザを起こさない ── 画面の遷移も
/// <c>MediaSource</c> の追いかけ再生も、この層が無ければ 1 行も走らない。
/// </para>
/// <para>
/// <b>ブラウザはシステムの Edge を使い、判定はすべて <c>Runtime.evaluate</c> の値で行う</b>
/// （<see cref="EdgeCdp"/>）。画素は見ない ── 見るのは DOM と <c>&lt;video&gt;</c> の状態だけである。
/// </para>
/// <para>
/// <b>Edge が入っていない環境では Skip する。</b> 判定は起動を試す前に行い、理由に不在のパスを書く
/// （<c>RecordingFilesTests.AFileThatIsItselfALinkIsNotServed</c> と同じ流儀）。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class WebUiBrowserTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>製品が生成する形（Base64Url・43 文字）に合わせた固定トークン。</summary>
    private const string Token = "E2E-web-ui-browser-token-0123456789-abcdef0";

    private const string AdminUser = "admin";
    private const string AdminPassword = "pw-admin";

    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(30);

    /// <summary>ページの読み込みと、画面が切り替わるまでの待ち。</summary>
    private static readonly TimeSpan PageBudget = TimeSpan.FromSeconds(30);

    /// <summary>録画物が一覧に現れ、追いかけ再生が始まるまでの待ち。</summary>
    private static readonly TimeSpan PlaybackBudget = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex BindPattern = new(@"\bbind=([0-9.]+):(\d+)\b", RegexOptions.Compiled);

    // ---- 設定と起動 ----

    /// <summary>リモート操作を有効にし、配信 root を隔離ディレクトリへ向けた settings.json。</summary>
    private static SettingsFile RemoteBase(bool allowGuestRead)
    {
        return new SettingsFile
        {
            RemoteControlEnabled = true,
            // 127.0.0.1 に固定する（0.0.0.0 だと開発機と CI の LAN から到達できる）。
            RemoteControlBindAddress = "127.0.0.1",
            RemoteControlPort = 0,
            RemoteControlAccessToken = Token,
            RemoteControlAllowGuestRead = allowGuestRead,
        };
    }

    /// <summary>
    /// ログインを見るケースの構成。<b>利用者の作り方は既存の L2 認証テストと同じ</b>
    /// ── <see cref="RemoteUserSpec"/> の固定 PBKDF2 ハッシュを settings.json へ書く。
    /// </summary>
    private static SettingsFile LoginSettings(bool allowGuestRead)
    {
        var settings = RemoteBase(allowGuestRead);
        settings.RemoteUsers.Add(new RemoteUserSpec(AdminUser, RemoteUserSpec.AdminPasswordHash, "Admin"));
        settings.AddRecorder("R1");
        return settings;
    }

    /// <summary>追いかけ再生を見るケースの構成（fMP4 出力・レコーダー 1 台・ゲスト読み取り）。</summary>
    private static SettingsFile FragmentedSettings()
    {
        var settings = RemoteBase(allowGuestRead: true);
        settings.FragmentedOutput = true;
        settings.AddRecorder("R1");
        return settings;
    }

    /// <summary>DASH プレビューを見るケースの構成（ゲスト読み取り・レコーダー 1 台）。</summary>
    private static SettingsFile DashSettings()
    {
        var settings = RemoteBase(allowGuestRead: true);
        settings.AddRecorder("R1");
        return settings;
    }

    private static void UseIsolatedRoot(AppInstance instance)
        => instance.Settings.OutputDirectory = instance.RecordingsDir;

    private int WaitForPort(AppInstance instance)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartBudget)
        {
            foreach (string line in ActivityLogFile.Events(instance.ReadActivityLog(), "remote.start"))
            {
                var match = BindPattern.Match(ActivityLogFile.DetailOf(line));
                if (match.Success)
                {
                    output.WriteLine(line);
                    return int.Parse(match.Groups[2].Value);
                }
            }
            Thread.Sleep(200);
        }

        Assert.Fail(
            $"remote.start が {StartBudget.TotalSeconds:F0} 秒以内に現れませんでした。"
            + Environment.NewLine
            + string.Join(Environment.NewLine, ActivityLogFile.Events(instance.ReadActivityLog(), "remote.error"))
            + Environment.NewLine + instance.DiagnosticDump());
        return 0;
    }

    // ---- 画面を読むための式 ----

    /// <summary>`hidden` クラスで隠されているか（`showLogin` / `showApp` が切り替えるもの）。</summary>
    private static string IsHidden(string id)
        => $"document.getElementById('{id}').classList.contains('hidden')";

    /// <summary>`hidden` 属性で隠されているか（役割とゲストで出し分けるボタン）。</summary>
    private static string IsAttributeHidden(string id)
        => $"document.getElementById('{id}').hidden";

    private static string TextOf(string id)
        => $"document.getElementById('{id}').textContent";

    private static string Click(string id)
        => $"(function () {{ document.getElementById('{id}').click(); return true; }})()";

    /// <summary>
    /// 不変カルチャで書式化する。<b>測った値は環境の言語で小数点が変わらない形で残す</b>
    /// ── 出力は較正のデータとして読み直されるものであり、貼り付けた先で数として通らなくなる。
    /// </summary>
    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// いま <c>/api/me</c> を叩いたら返る HTTP の status（文字列）。
    /// <b>失敗も文字列にして返す</b> ── ここで例外を投げると、これを呼んでいる当の失敗の
    /// 診断が例外に置き換わって消える。
    /// <b>自前で 5 秒に切る</b>のも同じ理由で、応答が返らない相手だと評価そのものが
    /// CDP のコマンドの期限まで塞がり、診断が例外へ化ける。
    /// </summary>
    private const string FetchApiMeStatus = """
        fetch('/api/me', { credentials: 'same-origin', signal: AbortSignal.timeout(5000) })
          .then(function (response) { return String(response.status); })
          .catch(function (error) { return 'fetch failed: ' + error; })
        """;

    /// <summary>
    /// 本体（<c>mainSections</c>）が出るまで待ち、<b>出なかったときは画面の側の事実を添えて落とす</b>。
    ///
    /// <para>
    /// 画面は <c>/api/me</c> の 1 つの答えから組み立てられ、失敗はすべてログイン画面へ倒れる
    /// ── だから「出なかった」だけでは、名乗りが要ると言われたのか、要求そのものが
    /// 落ちたのかが区別できない。見るのは 3 つ: <c>loginSection</c> が出ているか・
    /// <c>loginError</c> の本文・<b>いま同じ要求を出したら返る status</b>。
    /// </para>
    /// </summary>
    private async Task WaitForMainSectionsAsync(EdgeCdp browser)
    {
        if (await browser.WaitUntilAsync($"!{IsHidden("mainSections")}", PageBudget, Ct)) { return; }

        bool loginHidden = await browser.EvaluateBoolAsync(IsHidden("loginSection"), Ct);
        string loginError = await browser.EvaluateStringAsync(TextOf("loginError"), Ct);
        string me = await browser.EvaluateStringAsync(FetchApiMeStatus, Ct);
        Assert.Fail(
            Inv($"画面が {PageBudget.TotalSeconds:F0} 秒以内に出ませんでした")
            + Inv($"（loginSection={(loginHidden ? "hidden" : "visible")}")
            + Inv($"; loginError='{loginError}'; GET /api/me -> {me}）。"));
    }

    /// <summary>一覧の 1 行目の「Play」を押す（隔離 root には録画物が 1 本しか無い）。</summary>
    private const string ClickFirstPlay = """
        (function () {
          var rows = document.querySelectorAll('#recordingsBody tr');
          if (rows.length === 0) { return false; }
          rows[0].getElementsByTagName('button')[0].click();
          return true;
        })()
        """;

    /// <summary>一覧の 1 行目の state 欄（`recording` / `fragmented` が並ぶ）。</summary>
    private const string FirstRowState = """
        (function () {
          var rows = document.querySelectorAll('#recordingsBody tr');
          return rows.length === 0 ? '' : rows[0].cells[3].textContent;
        })()
        """;

    private const string PlayerTime = "document.getElementById('player').currentTime";

    /// <summary>
    /// <c>seeking</c> を数え始める（<c>window.__seeks</c>）。再生が始まってから仕掛けるが、
    /// <b>追従の開始時の寄せが窓に入ることはある</b>（実測で、入る run と入らない run の両方がある）
    /// ── その 1 回は閾値の側で織り込んである。二度仕掛けても購読は 1 つのまま、数だけが 0 へ戻る。
    /// </summary>
    private const string StartCountingSeeks = """
        (function () {
          window.__seeks = 0;
          if (!window.__seekCounter) {
            window.__seekCounter = function () { window.__seeks++; };
            document.getElementById('player').addEventListener('seeking', window.__seekCounter);
          }
          return true;
        })()
        """;

    private const string SeekCount = "window.__seeks";

    /// <summary>再生を実時間の半分にする（追い付けない再生を作るため）。</summary>
    private const string HalfSpeed =
        "(function () { document.getElementById('player').playbackRate = 0.5; return true; })()";

    /// <summary>
    /// バッファの終端（＝取り込み済みの尺）。バッファが空なら -1。
    /// <c>complete</c>（<c>endOfStream()</c> 済み）で読めば、そのファイルの長さそのものになる。
    /// </summary>
    private const string BufferedEnd = """
        (function () {
          var ranges = document.getElementById('player').buffered;
          return ranges.length === 0 ? -1 : ranges.end(ranges.length - 1);
        })()
        """;

    /// <summary>
    /// バッファの範囲を人が読める形にしたもの（<c>[start, end) …</c>）。
    /// <b>先頭が 0 でなければトリムが走っている。</b>
    /// </summary>
    private const string BufferedRanges = """
        (function () {
          var ranges = document.getElementById('player').buffered;
          var text = 'buffered=';
          for (var i = 0; i < ranges.length; i++) {
            text += '[' + ranges.start(i).toFixed(3) + ',' + ranges.end(i).toFixed(3) + ') ';
          }
          return text + 'ended=' + document.getElementById('player').ended;
        })()
        """;

    /// <summary>
    /// バッファの先頭。<b>0 より大きければトリムが 1 度は削っている。</b>
    /// バッファが空なら -1。
    /// </summary>
    private const string BufferedStart = """
        (function () {
          var ranges = document.getElementById('player').buffered;
          return ranges.length === 0 ? -1 : ranges.start(0);
        })()
        """;

    /// <summary>ライブ端からの遅れ（バッファの終端 − 現在位置）。バッファが空なら -1。</summary>
    private const string LiveLag = """
        (function () {
          var player = document.getElementById('player');
          var ranges = player.buffered;
          return ranges.length === 0 ? -1 : ranges.end(ranges.length - 1) - player.currentTime;
        })()
        """;

    /// <summary>半速で測る標本の数（1 秒間隔なので、測る時間は 1 つ少ない秒数）。</summary>
    private const int SampleCount = 13;

    /// <summary>
    /// 半速の測定中に許すシークの回数。<b>実測 2〜5 回</b>（開始の寄せが入る run を含む）
    /// ── 遅れは毎秒 0.5 秒ずつ開くので、
    /// 起動点（3 秒）まで 3 秒かかり、寄せはそれより多くは起こらない。
    /// フラグメントごとに寄せる形は updateend の数だけ上がって 12 回前後になる。
    /// </summary>
    private const int MaxFollowSeeks = 8;

    /// <summary>
    /// 測定の終わりに許すライブ端からの遅れ。<b>実測 1.9〜3.5 秒</b>（寄せの起動点 3 秒＋
    /// フラグメント 1 秒で頭打ちになる）。<b>寄せが死ぬと遅れは開く一方で、12 秒で 6.9〜7.2 秒</b>。
    /// </summary>
    private const double MaxLiveLagSeconds = 5;

    /// <summary>
    /// 半速の測定中に進んでいてほしい秒数。<b>実測 9.5〜12.1 秒</b>（寄せがライブ端へ引き上げるため、
    /// 半速でも実時間に近く進む）。<b>寄せが死ぬと半速の再生そのままで 6.2 秒</b>にしかならない。
    /// </summary>
    private const double MinAdvanceSeconds = 7.5;

    /// <summary>
    /// 一覧を取り直しながら、1 行目の state 欄が述語を満たすまで待つ
    /// （述語は state の文字列を <c>s</c> として書く）。
    ///
    /// <para>
    /// <b>取り直しの直後に読んではいけない。</b> 「Refresh」は要求を出すだけで、
    /// 表が入れ替わるのはその応答が返ってからである ── 押した直後に読むと、
    /// <b>録画を止めた後でも直前の行（<c>recording</c> 付き）</b>が返る。
    /// </para>
    /// </summary>
    private static async Task<bool> WaitForFirstRowAsync(EdgeCdp browser, string predicate, TimeSpan budget)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < budget)
        {
            await browser.EvaluateBoolAsync(Click("loadRecordings"), Ct);
            if (await browser.WaitUntilAsync(
                $"(function () {{ var s = {FirstRowState}; return {predicate}; }})()",
                TimeSpan.FromSeconds(2), Ct))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 一覧が待ちに応えなかったときの<b>クライアント側の姿</b>。
    ///
    /// <para>
    /// <b><see cref="FirstRowState"/> だけでは足りない。</b> 「行が 1 つも無い」と
    /// 「行は在るが状態が違う」は、どちらも状態セルの空文字か別の文字列として現れ、
    /// 失敗の本文からは区別が付かない ── 取得の鎖（最新日 → 月の件数 → その日の一覧）の
    /// どこで止まったのかは、選ばれている日と「この日は空」の掲示に出る。
    /// サーバー側の姿は <c>DiagnosticDump()</c> が別に載せる。
    /// </para>
    /// </summary>
    private const string ListingDump = """
        (function () {
          function textOf(id) {
            var node = document.getElementById(id);
            return node === null ? '(no node)' : (node.hidden ? '(hidden)' : node.textContent);
          }
          var rows = document.querySelectorAll('#recordingsBody tr');
          var cells = [];
          if (rows.length !== 0) {
            for (var i = 0; i < rows[0].cells.length; i++) { cells.push(rows[0].cells[i].textContent); }
          }
          return 'rows=' + rows.length
            + ' day=' + textOf('recordingsDay')
            + ' root=' + textOf('recordingsRoot')
            + ' empty=' + textOf('recordingsEmpty')
            + ' truncated=' + textOf('recordingsTruncated')
            + ' first=[' + cells.join(' | ') + ']';
        })()
        """;

    /// <summary>録画中の fMP4 の行（state に `recording` と `fragmented` の両方が出る）。</summary>
    private const string RowIsRecordingFragment = "s.indexOf('recording') >= 0 && s.indexOf('fragmented') >= 0";

    /// <summary>録画の終わった fMP4 の行（`recording` が消えている）。</summary>
    private const string RowIsFinishedFragment = "s.indexOf('recording') < 0 && s.indexOf('fragmented') >= 0";

    // ---- (1) ログイン遷移 ----

    /// <summary>
    /// ゲスト読み取りが無い構成では、<b>トップを開いた時点でログインフォームが出る</b>こと
    /// （<c>/api/me</c> の 401 → <c>showLogin</c>）。Admin で名乗ると一覧の画面へ移り、
    /// 見出しに <c>name (role)</c> が出ること。
    ///
    /// <para>
    /// <b>「Cancel」が出ないこと</b>も併せて固定する ── 戻る先が 401 しか返さない画面なので、
    /// 出してはいけないボタンである。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSignInFormAppearsWithoutGuestReadingAndAdminGetsThroughIt()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, LoginSettings(allowGuestRead: false), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);

        Assert.True(
            await browser.WaitUntilAsync($"!{IsHidden("loginSection")}", PageBudget, Ct),
            "ゲスト読み取りが無いのにログインフォームが出ませんでした。");
        Assert.True(await browser.EvaluateBoolAsync(IsHidden("mainSections"), Ct), "本体が隠れていません。");
        Assert.True(
            await browser.EvaluateBoolAsync(IsAttributeHidden("loginCancel"), Ct),
            "戻る先が無いのに Cancel が出ています。");

        await browser.EvaluateBoolAsync($$"""
            (function () {
              document.getElementById('loginUser').value = '{{AdminUser}}';
              document.getElementById('loginPassword').value = '{{AdminPassword}}';
              document.getElementById('loginSubmit').click();
              return true;
            })()
            """, Ct);

        Assert.True(
            await browser.WaitUntilAsync($"!{IsHidden("mainSections")}", PageBudget, Ct),
            "ログインしても一覧の画面へ移りませんでした: " + await browser.EvaluateStringAsync(TextOf("loginError"), Ct));

        Assert.True(await browser.EvaluateBoolAsync(IsHidden("loginSection"), Ct), "フォームが残っています。");
        Assert.Equal($"{AdminUser} (Admin)", await browser.EvaluateStringAsync(TextOf("identityName"), Ct));
        // Admin なのでソースの欄が出る（役割による出し分けが実際に効いていること）。
        Assert.False(await browser.EvaluateBoolAsync(IsAttributeHidden("sourceSection"), Ct));
    }

    // ---- (2) ゲストと取り消し ----

    /// <summary>
    /// ゲスト読み取りが在る構成では、名乗らずに一覧が見え、「Log in」でフォームへ、
    /// 「Cancel」で閲覧へ戻れること。<b>戻る先が在るときだけ Cancel が出る</b>のが要点で、
    /// 出ない側は <see cref="TheSignInFormAppearsWithoutGuestReadingAndAdminGetsThroughIt"/> が見る。
    /// </summary>
    [Fact]
    public async Task GuestReadingShowsTheAppAndTheSignInFormCanBeCancelled()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, LoginSettings(allowGuestRead: true), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);

        Assert.True(
            await browser.WaitUntilAsync($"!{IsHidden("mainSections")}", PageBudget, Ct),
            "ゲスト読み取りが在るのに閲覧できませんでした。");
        Assert.Equal("guest (Viewer)", await browser.EvaluateStringAsync(TextOf("identityName"), Ct));
        Assert.False(await browser.EvaluateBoolAsync(IsAttributeHidden("loginButton"), Ct), "Log in が出ていません。");

        await browser.EvaluateBoolAsync(Click("loginButton"), Ct);
        Assert.True(
            await browser.WaitUntilAsync($"!{IsHidden("loginSection")}", PageBudget, Ct),
            "Log in を押してもフォームが出ませんでした。");
        Assert.False(
            await browser.EvaluateBoolAsync(IsAttributeHidden("loginCancel"), Ct),
            "戻る先が在るのに Cancel が出ていません。");

        await browser.EvaluateBoolAsync(Click("loginCancel"), Ct);
        Assert.True(
            await browser.WaitUntilAsync($"!{IsHidden("mainSections")}", PageBudget, Ct),
            "Cancel で閲覧へ戻りませんでした。");
        Assert.True(await browser.EvaluateBoolAsync(IsHidden("loginSection"), Ct), "フォームが残っています。");
    }

    // ---- (3) 追いかけ再生（B-5 の釘打ち） ----

    /// <summary>
    /// 録画中の fMP4 を一覧から再生できて位置が進み、停止後は <c>complete</c> で終わること。
    /// そして<b>同じ行を開き直したとき、先頭付近から再生が始まる</b>こと。
    ///
    /// <para>
    /// <b>後半が本題である。</b> ライブ端への追従が「もう伸びないファイル」にも効いていると、
    /// 開き直した再生が末尾 1 秒へ飛んですぐ終わる ── 画面上は「一瞬で再生が終わる」形で現れる。
    /// プロファイルは起動ごとの一時ディレクトリなので、<b>古い app.js が残っていた</b>という
    /// 別の説明は成り立たない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFinishedRecordingReopensAtItsBeginningInsteadOfItsEnd()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, FragmentedSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);

        Assert.True(
            await WaitForFirstRowAsync(browser, RowIsRecordingFragment, PlaybackBudget),
            "録画中のファイルが fragmented として一覧に出ませんでした: "
                + await browser.EvaluateStringAsync(FirstRowState, Ct)
                + Environment.NewLine + await browser.EvaluateStringAsync(ListingDump, Ct)
                + Environment.NewLine + instance.DiagnosticDump());

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");
        Assert.True(
            await browser.WaitUntilAsync($"0.5 < {PlayerTime}", PlaybackBudget, Ct),
            "録画中の追いかけ再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        // 停止するまで少し録っておく（開き直した再生に進む余地を残すため）。
        await Task.Delay(TimeSpan.FromSeconds(6), Ct);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        Assert.True(
            await browser.WaitUntilAsync(
                $"{TextOf("playerStatus")}.indexOf('complete') === 0", PlaybackBudget, Ct),
            "停止しても complete になりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        // **判定の基準は録画の長さそのものにする。** 「3 秒未満」のような固定のしきい値は
        // 録画が伸びるほど余裕が薄くなる（実測 4.11 秒 vs 3 秒）── ここは
        // 「末尾へ飛んでいないこと」が見たいのだから、末尾との比で見る。
        // `complete` は `endOfStream()` の後に出るので、バッファの終端が尺そのものである。
        double length = await browser.EvaluateNumberAsync(BufferedEnd, Ct);
        output.WriteLine($"recorded length {length:F2}s");
        Assert.True(2 < length, $"録画の長さが読めませんでした（{length:F2} 秒）。");

        // 同じ行を開き直す。一覧の state から `recording` が消えるのを待ってから押す
        // ── 消える前に押すと「録画中の再生」をもう一度見ることになる。
        await browser.EvaluateBoolAsync(Click("stopPlayer"), Ct);
        Assert.True(
            await WaitForFirstRowAsync(browser, RowIsFinishedFragment, PlaybackBudget),
            "停止しても一覧の行が録画中のままです: " + await browser.EvaluateStringAsync(FirstRowState, Ct));

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");
        Assert.True(
            await browser.WaitUntilAsync($"0 < {PlayerTime}", PlaybackBudget, Ct),
            "開き直した再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        double resumed = await browser.EvaluateNumberAsync(PlayerTime, Ct);
        output.WriteLine($"reopened at {resumed:F2}s of {length:F2}s");
        Assert.True(
            resumed < length / 2,
            $"開き直した再生が先頭より末尾に近い位置から始まっています（{length:F2} 秒のうち {resumed:F2} 秒）。");

        Assert.True(
            await browser.WaitUntilAsync($"{resumed.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} + 1 < {PlayerTime}",
                PlaybackBudget, Ct),
            "開き直した再生が進みませんでした。");
    }

    // ---- (3b) トリムが再生位置を巻き添えにしないこと ----

    /// <summary>
    /// <c>app.js</c> の <c>FOLLOW_TRIM_TRIGGER_SECONDS</c> の写し
    /// （<see cref="TheTrimTriggerHereMatchesTheScript"/> が突き合わせる）。
    /// クリップがこれを超えていなければ、下の検査はトリムを 1 度も踏まないまま緑になる
    /// （だから尺そのものも表明する）。
    /// </summary>
    private const double TrimTriggerSeconds = 70;

    /// <summary>
    /// トリムの起動点（<see cref="TrimTriggerSeconds"/>）を確実に超える尺。
    /// <b>実時間では作らない</b> ── ソースを非ライブで回して実時間より速く書く。
    /// </summary>
    private const int LongClipSeconds = 90;

    /// <summary>作るクリップのフレームレート。キーフレームは 2 秒に 1 枚（<c>key-int-max</c>）。</summary>
    private const int LongClipFps = 15;

    /// <summary>
    /// クリップのビットレート(kbit/s)。<see cref="LongClipThrottleBytesPerSecond"/> と対で
    /// 「取り込みに掛かる時間」を決める。
    ///
    /// <para>
    /// ソースを <c>pattern=snow</c> にしているのは、指定したビットレートを実際に使い切らせるため
    /// ── 既定の SMPTE バーはよく縮むので、指定した値に届かない。
    /// </para>
    /// </summary>
    private const int LongClipBitrateKbps = 6000;

    /// <summary>クリップの大きさ（ビットレートを実際に使い切らせるために要る）。</summary>
    private const string LongClipResolution = "width=640,height=480";

    /// <summary>
    /// ブラウザの取り込み速度の上限(bytes/s)。<b>この検査の成立条件そのものである。</b>
    ///
    /// <para>
    /// トリムが再生位置を巻き添えにするのは<b>取り込みの途中で</b>バッファの幅が起動点を
    /// 超えたときで、そこまでに掛かる時間は「起動点ぶんのバイト数 ÷ 取り込み速度」で決まる。
    /// 絞らないと取り込みが一瞬で終わり、<c>currentTime</c> がまだ安全域
    /// （<c>FOLLOW_TRIM_SAFETY_SECONDS</c> = 5 秒）に届かないうちにトリムの機会が過ぎるので、
    /// <b>直っていても壊れていても緑になる</b>。
    /// </para>
    /// <para>
    /// 6000kbit/s・90 秒（実測 約 63 MB）を 4 MB/s で取り込むと約 15 秒掛かり、その間に
    /// 「70 秒ぶんが溜まる」（約 12 秒）と「再生位置が安全域の 5 秒を越える」（約 6 秒）の
    /// 両方が起きる。取り込み全体は <see cref="PlaybackBudget"/> に収まる。
    /// </para>
    /// </summary>
    private const double LongClipThrottleBytesPerSecond = 4 * 1024 * 1024;

    /// <summary>クリップを書き終えるまでの上限（実測は 11 秒前後）。</summary>
    private static readonly TimeSpan ClipBudget = TimeSpan.FromSeconds(180);

    /// <summary>
    /// <c>wwwroot</c> の JavaScript にある <c>FOLLOW_TRIM_TRIGGER_SECONDS</c> の宣言。
    /// <b>行頭に錨を打たない</b> ── 打つと一致位置が必ず行頭になり、
    /// 下のコメント行の除外が「常に偽」へ倒れて何も守らなくなる。
    /// </summary>
    private static readonly Regex TrimTriggerDeclarationRegex = new(
        @"\bvar\s+FOLLOW_TRIM_TRIGGER_SECONDS\s*=\s*(\d+)\s*;", RegexOptions.Compiled);

    /// <summary>
    /// <b><see cref="TrimTriggerSeconds"/> は製品側の写しである。</b>
    /// 製品側だけを動かすと、クリップが起動点を超えなくなって
    /// <see cref="AFinishedRecordingLongerThanTheTrimTrigger_StillPlaysFromItsBeginning"/> が
    /// <b>トリムを踏まないまま緑になる</b> ── ここで機械的に突き合わせる。
    ///
    /// <para>
    /// <b>コメント行は除く</b> ── 素の走査は、その定数を説明しているコメント自身に一致しうる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheTrimTriggerHereMatchesTheScript()
    {
        // wwwroot の JavaScript を全部つなぐ。1 本だけを読むと、定数が別のファイルへ
        // 移った日に「違反 0 件」ではなく検査そのものが消える。
        string script = string.Join('\n', Directory
            .EnumerateFiles(Path.Combine(RepositoryLayout.Root, "src", "RemoteControl", "wwwroot"), "*.js")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(File.ReadAllText));

        var declarations = TrimTriggerDeclarationRegex.Matches(script)
            .Where(m => !IsCommentLine(script, m.Index))
            .ToArray();

        Assert.True(declarations.Length == 1,
            $"wwwroot の JavaScript に FOLLOW_TRIM_TRIGGER_SECONDS が {declarations.Length} 件見つかりました"
            + "（走査が壊れているか、宣言の書き方が変わっています）。");

        Assert.Equal(
            TrimTriggerSeconds,
            double.Parse(declarations[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 指定位置を含む行が、行頭からコメントで始まっているか
    /// （L1 の <c>SourceReferences.IsCommentLine</c> と同じ規則。
    /// テストプロジェクトが別なので参照は共有できない）。
    /// </summary>
    private static bool IsCommentLine(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        var line = text.AsSpan(lineStart, index - lineStart).TrimStart();
        return line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith("*");
    }

    /// <summary>
    /// <paramref name="path"/> へ <see cref="LongClipSeconds"/> 秒ぶんの fMP4 を書く。
    ///
    /// <para>
    /// <b>実時間を使わない</b>のが要点 ── <c>videotestsrc</c> を非ライブで
    /// <c>num-buffers</c> ぶんだけ回すので、<see cref="LongClipSeconds"/> 秒のクリップが
    /// 実測 11 秒前後（約 63 MB）で出来る。
    /// 形は製品の書き方に合わせる（<c>fragment-mode=dash-or-mss</c>・<c>fragment-duration=1000</c>）。
    /// </para>
    /// <para>
    /// <b>キーフレームは 2 秒に 1 枚。</b> トリムが再生位置を巻き添えにするのは
    /// <c>SourceBuffer.remove</c> が「次のランダムアクセス点まで」削るからで、
    /// キーフレームの間隔が詰まっていると壊れ方が見えない。
    /// </para>
    /// </summary>
    private async Task WriteLongFragmentedClipAsync(string launcher, string path, AppInstance instance)
    {
        var writing = Stopwatch.StartNew();

        await GstLaunchTool.RunAsync(
            launcher,
            [
                "videotestsrc",
                "pattern=snow",
                "num-buffers=" + (LongClipSeconds * LongClipFps).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "!",
                $"video/x-raw,format=I420,{LongClipResolution},framerate={LongClipFps}/1",
                "!",
                "x264enc",
                "bitrate=" + LongClipBitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "speed-preset=ultrafast",
                "key-int-max=" + (LongClipFps * 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "!",
                "h264parse",
                "!",
                "mp4mux",
                "fragment-duration=1000",
                "fragment-mode=dash-or-mss",
                "!",
                "filesink",
                // **区切りは '/' にする。** gst-launch はプロパティ値の '\' を
                // エスケープとして食うので、Windows のパスをそのまま渡すと別のパスになる。
                "location=" + path.Replace('\\', '/'),
            ],
            instance,
            "gst-registry-longclip.bin",
            ClipBudget,
            Ct);

        // **書けた時間も出す。** 文書とここの doc コメントが「実測」として書いている値は
        // これで読み直す（食い違いが残ると、次に読む人が別の速さを前提に組み立てる）。
        output.WriteLine(
            $"{Path.GetFileName(path)}: {new FileInfo(path).Length} bytes "
            + $"in {writing.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// <b>トリムの起動点を超える長さの完成ファイルでも、開き直した再生が先頭から進む。</b>
    ///
    /// <para>
    /// <see cref="AFinishedRecordingReopensAtItsBeginningInsteadOfItsEnd"/> が見るのは
    /// 数秒のファイルで、<c>FOLLOW_TRIM_TRIGGER_SECONDS</c>（70 秒）を踏まない
    /// ── <b>踏ませると別の壊れ方が出る</b>。バッファの幅が起動点を超えるとトリムが走り、
    /// <c>SourceBuffer.remove</c> は<b>次のランダムアクセス点まで</b>削るので、
    /// <c>FOLLOW_TRIM_SAFETY_SECONDS</c> の安全域を置かずに再生位置まで削らせると
    /// 再生中の GOP ごと消える。<c>MediaSource</c> は <c>endOfStream()</c> 済みで
    /// 待つものが無いため、要素は <c>currentTime = duration</c> へ飛んで動かなくなる
    /// （<b>安全域を外して実測</b>: 90 秒のクリップで <c>buffered.start</c> が 18.000、
    /// 位置は 90.00 に固定。安全域つきでは 12.000 と 16.27 で、位置は進み続ける）。
    /// </para>
    /// <para>
    /// <b>取り込みを絞るのがこの検査の成立条件である</b>
    /// （<see cref="LongClipThrottleBytesPerSecond"/>）── 絞らないと完成ファイルは一瞬で
    /// 落ちてきて、再生位置が安全域に届く前にトリムの機会が過ぎ、
    /// <b>直っていても壊れていても緑になる</b>。踏んだことは <c>buffered.start</c> で
    /// 見届ける（0 のままなら踏んでいない＝この検査は成立していない）。
    /// </para>
    /// <para>
    /// <b>実時間 70 秒は使わない。</b> クリップは <c>gst-launch-1.0</c> に非ライブで書かせる
    /// （実測 11 秒前後）。<b>ロードした GStreamer に <c>gst-launch-1.0.exe</c> か
    /// x264 のプラグインが無い場合は Skip する</b>ので、緑だから走ったとは限らない
    /// ── 実行結果の skip 件数を見ること。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFinishedRecordingLongerThanTheTrimTrigger_StillPlaysFromItsBeginning()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, FragmentedSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        Assert.True(instance.WaitForActivityLogEvent("gst.runtime", StartBudget),
            "gst.runtime が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        string? launcher = GstLaunchTool.FindLauncher(instance);
        Assert.SkipWhen(launcher is null,
            "ロードした GStreamer の bin に gst-launch-1.0.exe がありません"
            + "（gst.runtime の dir= を見て探しています）。");
        Assert.SkipUnless(GstLaunchTool.HasX264Plugin(launcher!),
            $"ロードした GStreamer に x264 のプラグインがありません（{GstLaunchTool.PluginDirectoryOf(launcher!)}）。");

        string clip = Path.Combine(instance.RecordingsDir, "long-clip.mp4");
        await WriteLongFragmentedClipAsync(launcher!, clip, instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.True(
            await WaitForFirstRowAsync(browser, RowIsFinishedFragment, PlaybackBudget),
            "作ったクリップが fragmented として一覧に出ませんでした: " + await browser.EvaluateStringAsync(FirstRowState, Ct));

        // **絞るのは一覧が出てから。** 一覧の JSON まで絞ると、待つ相手が増えるだけで
        // 何も確かめられない。
        await browser.ThrottleDownloadAsync(LongClipThrottleBytesPerSecond, Ct);

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");

        // **`complete` を待ってから位置を読む。** 壊れている実装でも読み込み中の一瞬は
        // 先頭付近を再生しており、そこで読むと偽の緑になる。
        Assert.True(
            await browser.WaitUntilAsync($"{TextOf("playerStatus")}.indexOf('complete') === 0", PlaybackBudget, Ct),
            "クリップを読み切れませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        double length = await browser.EvaluateNumberAsync(BufferedEnd, Ct);
        double position = await browser.EvaluateNumberAsync(PlayerTime, Ct);
        double bufferedStart = await browser.EvaluateNumberAsync(BufferedStart, Ct);
        output.WriteLine(
            $"clip length {length:F2}s, playing at {position:F2}s, "
            + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        Assert.True(TrimTriggerSeconds < length,
            $"クリップがトリムの起動点を超えていません（{length:F2} 秒）── この検査は成立しません。");
        // **トリムの証人。** 取り込みが速すぎるとトリムは 1 度も走らず、
        // 直っていても壊れていても位置は先頭付近に残る ── その緑を潰す。
        Assert.True(0 < bufferedStart,
            $"トリムを踏んでいません（buffered.start が {bufferedStart:F3}）── この検査は成立しません。");
        Assert.True(
            position < length / 2,
            $"トリムを踏んだ再生が末尾へ飛んでいます（{length:F2} 秒のうち {position:F2} 秒）。");

        Assert.True(
            await browser.WaitUntilAsync(
                $"{position.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} + 1 < {PlayerTime}",
                PlaybackBudget, Ct),
            $"トリムを踏んだ再生が進みません（{position:F2} 秒で止まっています）。");
    }

    // ---- (4) カタつきの代理指標（B-2 の釘打ち） ----

    /// <summary>
    /// 録画中の追いかけ再生で、ライブ端への寄せが<b>フラグメントごとに走らず、
    /// しかも遅れたときには効く</b>こと。
    ///
    /// <para>
    /// <b>実時間どおりに再生できている間は、寄せの有無を外から区別できない。</b>
    /// 毎フラグメント寄せる実装でも、条件（バッファ端が現在位置より先）が成り立たないので
    /// 寄せは起きない ── 実測で、そのままの速度では新旧どちらも <c>seeking</c> は 1 回・
    /// 位置は 1 秒に 1 秒進む。<b>そこで <c>playbackRate</c> を半分にして「追い付けない再生」を作る。</b>
    /// デコーダーが落伍した状況の代わりであり、B-2 が報告された状況そのものである。
    /// </para>
    /// <para>
    /// 遅れを作ると壊れ方が 2 つに分かれ、どちらも実測で区別できる:
    /// <b>(a) 寄せがフラグメントごとに走る</b>（<c>seeking</c> が updateend の数だけ上がる。
    /// 半速 12 秒で 12 回前後）、<b>(b) 寄せが最初の 1 回で死ぬ</b>（再生前のシークを
    /// <c>seeking</c> の監視が「利用者が操作した」と読んで追従を降ろす形。位置は半速のまま進み、
    /// 12 秒で 6.2 秒・遅れは 7.0 秒まで開く）。正しい実装は<b>2 回の寄せで 9.7 秒進み、
    /// 遅れは 3.5 秒に収まる</b>（いずれもこの機械での実測）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task FollowingTheLiveEdgeDoesNotSeekOnEveryFragment()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, FragmentedSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);

        Assert.True(
            await WaitForFirstRowAsync(browser, RowIsRecordingFragment, PlaybackBudget),
            "録画中のファイルが fragmented として一覧に出ませんでした: "
                + await browser.EvaluateStringAsync(FirstRowState, Ct)
                + Environment.NewLine + await browser.EvaluateStringAsync(ListingDump, Ct)
                + Environment.NewLine + instance.DiagnosticDump());

        // **緑の run でも 1 度は流す。** 診断が緑の run の出力にも姿を残すので、
        // 壊れた（空になった・形が変わった）ことに赤を待たずに気付ける。
        output.WriteLine(await browser.EvaluateStringAsync(ListingDump, Ct));

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");
        Assert.True(
            await browser.WaitUntilAsync($"0.5 < {PlayerTime}", PlaybackBudget, Ct),
            "追いかけ再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        // 追い付けない再生を作ってから数え始める（開始の寄せは数に入れない）。
        Assert.True(await browser.EvaluateBoolAsync(HalfSpeed, Ct), "再生速度を落とせませんでした。");
        Assert.True(await browser.EvaluateBoolAsync(StartCountingSeeks, Ct), "seeking を数え始められませんでした。");

        var samples = new List<double>();
        var lags = new List<double>();
        for (int i = 0; i < SampleCount; i++)
        {
            samples.Add(await browser.EvaluateNumberAsync(PlayerTime, Ct));
            lags.Add(await browser.EvaluateNumberAsync(LiveLag, Ct));
            if (i < SampleCount - 1)
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        // 数え終えてから止める（停止で最後のフラグメントが届くと、そこでの寄せまで数に入る）。
        double seeks = await browser.EvaluateNumberAsync(SeekCount, Ct);

        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        string trace =
            $"seeks={seeks:F0} pos=[{string.Join(", ", samples.Select(s => s.ToString("F2")))}]"
            + $" lag=[{string.Join(", ", lags.Select(s => s.ToString("F2")))}]";
        output.WriteLine(trace);

        // (a) 寄せがフラグメントごとに走っていないこと。
        Assert.True(seeks <= MaxFollowSeeks, $"半速の {SampleCount - 1} 秒で {seeks:F0} 回シークしました: {trace}");

        // (b) 寄せが死んでいないこと ── 遅れが開いたままにならず、
        // 半速の再生だけでは進めない量まで進んでいること。
        Assert.True(lags[^1] <= MaxLiveLagSeconds, $"ライブ端から {lags[^1]:F2} 秒遅れたままです: {trace}");

        double advanced = samples[^1] - samples[0];
        Assert.True(MinAdvanceSeconds <= advanced, $"半速のまま {advanced:F2} 秒しか進みませんでした: {trace}");

        // 寄せは前へしか行かないので、位置は戻らない。
        int backwards = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i] < samples[i - 1])
                backwards++;
        }

        Assert.True(backwards <= 1, $"再生位置が {backwards} 回戻りました: {trace}");
    }

    // ---- (5) DASH プレビュー ----

    /// <summary>画質切替を `dash` にする（選ぶだけ ── 押すのは行の Preview）。</summary>
    private const string SelectDashMode = """
        (function () {
          document.getElementById('previewMode').value = 'dash';
          return document.getElementById('previewMode').value === 'dash';
        })()
        """;

    /// <summary>1 行目の「Preview」を押す（開始/停止は役割で隠れるので、文言で選ぶ）。</summary>
    private const string ClickFirstPreview = """
        (function () {
          var rows = document.querySelectorAll('#recordersBody tr');
          if (rows.length === 0) { return false; }
          var buttons = rows[0].getElementsByTagName('button');
          for (var i = 0; i < buttons.length; i++) {
            if (buttons[i].textContent === 'Preview') { buttons[i].click(); return true; }
          }
          return false;
        })()
        """;

    private const string PreviewReadyState = "document.getElementById('previewPlayer').readyState";

    private const string PreviewTime = "document.getElementById('previewPlayer').currentTime";

    /// <summary>
    /// <b>最小 DASH クライアント（`app.js`）が本物のブラウザで実際に絵を出すこと。</b>
    ///
    /// <para>
    /// L2 の <c>DashPreviewTests</c> が見るのは HTTP の応答の形までで、
    /// <b>MPD を読んで `SourceBuffer` へ正しい順序・正しい時間軸で積めるか</b>は
    /// ここでしか走らない ── <c>timestampOffset</c> を 1 つ間違えるだけで、
    /// 応答はすべて 200 のまま <c>currentTime</c> が 1 秒も進まなくなる。
    /// </para>
    /// <para>
    /// 判定は<b>位置が実際に進むこと</b>で行う。<c>readyState</c> だけでは
    /// 「最初の 1 枚は出たが続かない」を通してしまう。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheDashPreviewPlaysInTheBrowser()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, DashSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        // 枝A のサンプルが出るまでは MPD が 503 のままなので、初期化を待ってから開く。
        Assert.True(instance.WaitForActivityLogEvent("recorder.init ok", PageBudget),
            "recorder.init ok が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.True(
            await browser.WaitUntilAsync("document.querySelectorAll('#recordersBody tr').length > 0", PageBudget, Ct),
            "レコーダーの一覧が出ませんでした。");

        Assert.True(await browser.EvaluateBoolAsync(SelectDashMode, Ct), "画質切替に dash がありません。");
        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPreview, Ct), "行に Preview のボタンがありません。");

        Assert.True(
            await browser.WaitUntilAsync($"2 <= {PreviewReadyState}", PageBudget, Ct),
            "DASH の再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));

        double before = await browser.EvaluateNumberAsync(PreviewTime, Ct);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        double after = await browser.EvaluateNumberAsync(PreviewTime, Ct);

        string state = await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct);
        output.WriteLine($"dash preview: {before:F2}s -> {after:F2}s, status='{state}'");

        Assert.True(1 <= after - before,
            $"2 秒のあいだに再生位置が {after - before:F2} 秒しか進みませんでした（status='{state}'）。");
        Assert.StartsWith("DASH: live", state, StringComparison.Ordinal);

        // 「Stop preview」は両モード共通の後始末で、状態表示は `stopped` に置き換わる
        // （空にはしない ── その文言は chunked のときから変わっていない）。
        Assert.True(await browser.EvaluateBoolAsync(Click("stopPreview"), Ct), "Stop preview を押せませんでした。");
        Assert.True(
            await browser.WaitUntilAsync($"{TextOf("previewStatus")} === 'stopped'", PageBudget, Ct),
            "Stop preview で配信が畳まれませんでした: " + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));
    }

    /// <summary>
    /// プレビューの <c>waiting</c>（＝バッファ切れの停止）を数え始める（<c>window.__praWaiting</c>）。
    /// <b>プレビューを開く前に仕掛ける</b> ── 開いてから仕掛けると、最初の停止が窓の外に落ちる。
    /// 二度仕掛けても購読は 1 つのまま、数だけが 0 へ戻る（<see cref="StartCountingSeeks"/> と同じ形）。
    /// </summary>
    private const string StartCountingPreviewWaiting = """
        (function () {
          window.__praWaiting = 0;
          if (!window.__praWaitingCounter) {
            window.__praWaitingCounter = function () { window.__praWaiting++; };
            document.getElementById('previewPlayer').addEventListener('waiting', window.__praWaitingCounter);
          }
          return true;
        })()
        """;

    private const string PreviewWaitingCount = "window.__praWaiting";

    /// <summary>
    /// プレビューのライブ端の余裕（<c>buffered.end</c> − <c>currentTime</c>）。バッファが空なら -1。
    /// <b>これが 1 秒を切ったまま推移する形が、DASH で止まり続けている姿である</b>
    /// ── セグメントは 1 秒に 1 つしか来ないので、1 秒未満の余裕では次が間に合わない。
    /// </summary>
    private const string PreviewCushion = """
        (function () {
          var video = document.getElementById('previewPlayer');
          var ranges = video.buffered;
          return ranges.length === 0 ? -1 : ranges.end(ranges.length - 1) - video.currentTime;
        })()
        """;

    /// <summary>滑らかさを測る秒数（1 秒ごとに 1 標本）。</summary>
    private const int DashSmoothnessSeconds = 12;

    /// <summary>
    /// <b>停止を数える末尾の標本数。</b> join の停止はこの手前に落ちる ── 空のリングへ
    /// join する 2〜3 回は前半に出る。<b>時刻で切らずに末尾で切る</b>のは、定常へ入る時刻が
    /// 機械によって前後するためで、見るのは「最後まで定常だったか」である。
    /// <b><see cref="DashSmoothnessSeconds"/> より小さく保つこと</b>
    /// ── 増分の基準はこの窓の 1 つ手前の標本である。
    /// </summary>
    private const int DashSteadySamples = 5;

    /// <summary>
    /// <b>一度は届かなければならないライブ端の余裕（全標本の最大）。</b>
    /// ライブ端の 0.5 秒手前へ寄せ続ける形では届かない ── そこでの余裕は寄せ先＋append の
    /// ジッタで頭打ちになる（実測の上限 1.05 秒）。届く側の実測の下限は 1.26 秒で、
    /// 閾値はその間から採ってある。
    /// <b>余裕は末尾では見ない</b> ── 寄せは前へしか跳べないので終わりの値はドリフトのぶん
    /// だけ振れる（実測 0.58〜1.98）。
    /// </summary>
    private const double DashPeakCushionSeconds = 1.2;

    /// <summary>
    /// <b>DASH プレビューがライブ端の手前で止まり続けないこと。</b>
    ///
    /// <para>
    /// <see cref="TheDashPreviewPlaysInTheBrowser"/> は「2 秒で 1 秒以上進む」しか見ないので、
    /// <b>止まっては跳ぶ</b>形（進みは足りているのに絵はカクついている）を通してしまう。
    /// 機構はこうである: 追従がライブ端の 0.5 秒手前へ寄せる → DASH は 1 秒セグメント×
    /// 1 秒ポーリングなので次のセグメントが間に合わず停止（<c>waiting</c>）→ 停止の間に
    /// 遅れが開いてまた寄せる、の周期。余裕は 0.5 秒に張り付き、<c>waiting</c> が数秒ごとに増える。
    /// </para>
    /// <para>
    /// 断定は 3 つ ── <b>進み</b>（止まっていない）・<b>末尾 5 標本での停止の増分</b>
    /// （定常で止まっていない）・<b>全標本のどこかで余裕が閾値に届くこと</b>
    /// （ライブ端に貼り付いていない）。<b>12 標本の軌跡は合否に関わらず 1 行で
    /// <c>output</c> へ出す</b> ── 緑の run も較正のデータになる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheDashPreviewPlaysWithoutStallingBehindTheLiveEdge()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, DashSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        Assert.True(instance.WaitForActivityLogEvent("recorder.init ok", PageBudget),
            "recorder.init ok が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.True(
            await browser.WaitUntilAsync("document.querySelectorAll('#recordersBody tr').length > 0", PageBudget, Ct),
            "レコーダーの一覧が出ませんでした。");

        // 計数は DASH を開く前に仕掛ける（開始直後の停止も数に入れる）。
        Assert.True(await browser.EvaluateBoolAsync(StartCountingPreviewWaiting, Ct), "waiting を数え始められませんでした。");
        Assert.True(await browser.EvaluateBoolAsync(SelectDashMode, Ct), "画質切替に dash がありません。");
        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPreview, Ct), "行に Preview のボタンがありません。");

        Assert.True(
            await browser.WaitUntilAsync($"{TextOf("previewStatus")}.indexOf('DASH: live') === 0", PlaybackBudget, Ct),
            "DASH の配信が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));

        double before = await browser.EvaluateNumberAsync(PreviewTime, Ct);
        double[] cushions = new double[DashSmoothnessSeconds];
        double[] stalls = new double[DashSmoothnessSeconds];
        for (int second = 1; second <= DashSmoothnessSeconds; second++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            cushions[second - 1] = await browser.EvaluateNumberAsync(PreviewCushion, Ct);
            double position = await browser.EvaluateNumberAsync(PreviewTime, Ct);
            stalls[second - 1] = await browser.EvaluateNumberAsync(PreviewWaitingCount, Ct);
            output.WriteLine(
                Inv($"dash smoothness t+{second,2}s: currentTime={position:F2}s ")
                + Inv($"cushion={cushions[second - 1]:F2}s waiting={stalls[second - 1]:F0}"));
        }

        double after = await browser.EvaluateNumberAsync(PreviewTime, Ct);
        string state = await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct);

        // 停止を数える末尾の窓。増分はその**手前の標本**を基準に取るので、窓ぶんの秒に
        // 起きた停止がすべて入る。`DashSteadySamples` は `DashSmoothnessSeconds` より
        // 小さいので `tail` は 1 以上（0 以下だと基準の添字が負になる）。
        Assert.True(0 < DashSmoothnessSeconds - DashSteadySamples,
            Inv($"末尾の窓（{DashSteadySamples}）が標本数（{DashSmoothnessSeconds}）以上です ── 基準の標本が取れません。"));
        int tail = DashSmoothnessSeconds - DashSteadySamples;
        double tailWaiting = stalls[DashSmoothnessSeconds - 1] - stalls[tail - 1];
        double peakCushion = cushions.Max();

        // **軌跡は合否に関わらず出す。** 緑の run も閾値を較正するためのデータである。
        string trace = string.Join(" ", Enumerable.Range(0, DashSmoothnessSeconds).Select(
            i => Inv($"t+{i + 1}:{cushions[i]:F2}/{stalls[i]:F0}")));
        output.WriteLine(Inv($"dash smoothness trace (cushion/waiting): {trace}"));
        output.WriteLine(
            Inv($"dash smoothness: {before:F2}s -> {after:F2}s (+{after - before:F2}s), ")
            + Inv($"last {DashSteadySamples} samples: waiting +{tailWaiting:F0}, ")
            + Inv($"peak cushion {peakCushion:F2}s, status='{state}'"));

        Assert.True(10 <= after - before,
            Inv($"{DashSmoothnessSeconds} 秒のあいだに再生位置が {after - before:F2} 秒しか進みませんでした（status='{state}'）。"));

        // **停止の総数では見ない ── 末尾の 5 標本だけを見る。** リースは要求されて
        // 初めて第 2 パイプラインを起こすので、最初の視聴者が居合わせるリングは必ず空に近く、
        // そこでは必ず何度か止まる ── `play()` そのもので 1 回、そこから余裕を育てるのにもう 1 回
        //（生きた配信では生産レート＝再生レートなので、余裕は**止まっている間にしか増えない**）。
        // その 2〜3 回は前半に出るので、**定常へ入ったあとの増分**だけを見る。
        // 寄せすぎて周期的に止まる形は、末尾でも止まり続ける。
        Assert.True(tailWaiting <= 1,
            Inv($"末尾 {DashSteadySamples} 標本でバッファ切れの停止が {tailWaiting:F0} 回ありました（1 回まで）── ")
            + Inv($"ライブ端に寄せすぎています（status='{state}'; {trace}）。"));

        // **余裕は全標本の最大で見る。** 終わりの値も末尾の窓も、寄せが前へしか跳べない
        // せいでドリフトのぶんだけ振れる ── 落ち着いた再生でも下がって終わる。
        // 寄せすぎている側は 0.5 秒へ引き戻され続けるので、どの標本もここへ届かない。
        Assert.True(DashPeakCushionSeconds <= peakCushion,
            Inv($"ライブ端の余裕が一度も {DashPeakCushionSeconds} 秒に届きませんでした（最大 {peakCushion:F2} 秒）── ")
            + Inv($"ライブ端に張り付いています（status='{state}'; {trace}）。"));

        Assert.True(await browser.EvaluateBoolAsync(Click("stopPreview"), Ct), "Stop preview を押せませんでした。");
        Assert.True(
            await browser.WaitUntilAsync($"{TextOf("previewStatus")} === 'stopped'", PageBudget, Ct),
            "Stop preview で配信が畳まれませんでした: " + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));
    }

    // ---- (6) 自前のコントロールバー ----

    /// <summary>
    /// バーの部品は id を持たない（<c>&lt;video&gt;</c> 2 つに同じバーが付くので、id は必ず衝突する）。
    /// <b>要素の親＝シェルのラッパ</b>なので、そこから <c>data-action</c> で引く。
    /// </summary>
    private static string ShellControl(string videoId, string action)
        => ShellPart(videoId, $"[data-action=\"{action}\"]");

    /// <summary>シェルのラッパの中の部品を、CSS の選択子で引く。</summary>
    private static string ShellPart(string videoId, string selector)
        => $"document.getElementById('{videoId}').parentNode.querySelector('{selector}')";

    private static string ClickShellControl(string videoId, string action)
        => $"(function () {{ var node = {ShellControl(videoId, action)}; if (node === null) {{ return false; }} node.click(); return true; }})()";

    /// <summary>開いているメニューの項目を、<c>data-*</c> の値で選ぶ。</summary>
    private static string ClickMenuItem(string videoId, string attribute, string value)
        => $$"""
            (function () {
              var item = document.getElementById('{{videoId}}').parentNode
                .querySelector('.player-menu [data-{{attribute}}="{{value}}"]');
              if (item === null) { return false; }
              item.click();
              return true;
            })()
            """;

    /// <summary>録画物が 1 本できるまで録る秒数（＋10 秒を踏める尺が要る）。</summary>
    private static readonly TimeSpan ClipRecordingTime = TimeSpan.FromSeconds(16);

    /// <summary>録画プレイヤーの画質メニューの holder（1 つしか無い一覧では隠れている）。</summary>
    private const string QualityMenuHidden =
        "document.getElementById('player').parentNode"
        + ".querySelector('[data-action=\"quality\"]').parentNode.hidden";

    /// <summary>いま何を変換して再生しているか（<c>PRA.player.transcodeState()</c>）。</summary>
    private const string TranscodeActive = "window.PRA.player.transcodeState().active";

    /// <inheritdoc cref="TranscodeActive"/>
    private const string TranscodeDump = "JSON.stringify(window.PRA.player.transcodeState())";

    /// <summary>起動時に読んだ能力（<c>GET /api/capabilities</c>）。</summary>
    private const string CapabilitiesDump = "JSON.stringify(window.PRA.core.state.capabilities)";

    /// <summary>
    /// <b>ハードウェア H.264 デコーダーの無い機械では、録画の画質メニューが出ないこと。</b>
    ///
    /// <para>
    /// <b>この機械で実際に走るのは false の経路だけである。</b> 変換の実体は GPU 実機でしか
    /// 動かないので（同梱ランタイムにソフトウェアの H.264 デコーダーは無い）、ここで固定するのは
    /// 「できないと分かっている機械が、できるふりをしないこと」── メニューに出した項目は
    /// 押せば 404 になるので、出さないことそのものが仕様である。
    /// </para>
    /// <para>
    /// 判定は 2 つ。<c>original</c> しか無い一覧は「1 つは選択肢ではない」の規則で
    /// メニューごと隠れ、<c>transcodeState().active</c> は false のまま
    /// ── 隠れているだけで裏では変換を始めている、という壊れ方を後者が捕まえる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRecordingPlayerOffersNoTranscodeWithoutAHardwareDecoder()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        // 完成した（moov の在る）ファイルで見る ── 録画中のものは、能力に関わらず
        // サーバーが断る側なので、メニューの判断が効いているかを分けられない。
        var settings = RemoteBase(allowGuestRead: true);
        settings.FragmentedOutput = false;
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        // **行が在ることも述語に入れる**（完成した非 fragmented の state 欄は空文字）。
        Assert.True(
            await WaitForFirstRowAsync(
                browser,
                "s === '' && document.querySelectorAll('#recordingsBody tr').length > 0",
                PlaybackBudget),
            "録画の終わったファイルが一覧に出ませんでした: "
                + await browser.EvaluateStringAsync(FirstRowState, Ct)
                + Environment.NewLine + await browser.EvaluateStringAsync(ListingDump, Ct)
                + Environment.NewLine + instance.DiagnosticDump());

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");
        Assert.True(
            await browser.WaitUntilAsync($"0 < {PlayerTime}", PlaybackBudget, Ct),
            "再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        output.WriteLine(await browser.EvaluateStringAsync(CapabilitiesDump, Ct));

        Assert.False(
            await browser.EvaluateBoolAsync(TranscodeActive, Ct),
            "この機械で変換再生が始まりました: " + await browser.EvaluateStringAsync(TranscodeDump, Ct));
        Assert.True(
            await browser.EvaluateBoolAsync(QualityMenuHidden, Ct),
            "録画の画質メニューが出ています（能力は "
                + await browser.EvaluateStringAsync(CapabilitiesDump, Ct) + "）。");
    }

    // ---- (6b) 録画の画質メニュー（変換が成立する機械での経路） ----

    /// <summary>
    /// 変換が成立する構成。<b>fragmented は切る</b> ── 変換の対象は完成したファイルだけで、
    /// 書き込み中のものはサーバーが断る側なので、メニューの判断を分けて見られない。
    /// </summary>
    private static SettingsFile TranscodeSettings(int limit)
    {
        var settings = RemoteBase(allowGuestRead: true);
        settings.FragmentedOutput = false;
        settings.RemoteAuxiliaryEncoderLimit = limit;
        settings.AddRecorder("R1");
        return settings;
    }

    /// <summary>
    /// ソフトウェアの H.264 デコーダーを持つランタイムで起こす（配信 root の隔離も併せて）。
    /// <b><c>AppInstance.Create</c> の <c>configure</c> でしか効かない</b>
    /// ── デコーダーの確認は常駐ワーカーの静的初期化で 1 回だけ行われる。
    /// </summary>
    private static void UseSoftwareDecoder(AppInstance instance)
    {
        UseIsolatedRoot(instance);
        SoftwareDecoderRuntime.Apply(instance);
    }

    /// <summary>
    /// 変換元にする録画の長さ。<b>10 秒要る</b> ── 変換を 5 秒から始め、そこから
    /// <see cref="TranscodeSeekSeconds"/> へ<b>戻る</b>ことでシークの作り直しを踏む形なので、
    /// 2 つの位置が離れていて、かつどちらの後ろにも再生が進む余地が要る。
    /// </summary>
    private static readonly TimeSpan TranscodeClipTime = TimeSpan.FromSeconds(10);

    /// <summary>変換を始める位置（選んだ時点の再生位置がそのまま始点になる）。</summary>
    private const double TranscodeOpenSeconds = 5;

    /// <summary>
    /// 変換再生をシークする位置。<b><see cref="TranscodeOpenSeconds"/> より前にする</b>
    /// ── パイプラインは始点から前へ読むだけなので、始点より手前は取り込みようがない。
    /// これで「まだ取っていない位置へのシーク」が取り込み速度に関わらず成立する
    /// （後ろへ跳ぶと、短いクリップは一瞬で全部届いていて作り直しを踏まない）。
    /// </summary>
    private const double TranscodeSeekSeconds = 3;

    /// <summary>変換の応答が届いて再生が進むまでの待ち。</summary>
    private static readonly TimeSpan TranscodeBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 枠が返るのを待つ長さ。<b>猶予（<c>TranscodeLimits.GraceMs</c>＝10 秒）は EOS から
    /// 数え始める</b>ので、手放した時刻から測れば足りる（SSE の畳み込みぶんを足してある）。
    /// </summary>
    private static readonly TimeSpan SlotBudget = TimeSpan.FromSeconds(45);

    /// <summary>SSE の <c>state</c> を 1 件読むまでの待ち（接続直後に 1 件届く）。</summary>
    private static readonly TimeSpan SseBudget = TimeSpan.FromSeconds(10);

    /// <summary>枠を握り続けるための再要求の間隔（猶予 10 秒より短くする）。</summary>
    private static readonly TimeSpan HoldRefreshInterval = TimeSpan.FromSeconds(5);

    /// <summary>離脱した後に「変換が始まらないこと」を見張る窓（始まるなら 1〜2 秒で始まる）。</summary>
    private static readonly TimeSpan LeaveWatchWindow = TimeSpan.FromSeconds(6);

    /// <summary>画質メニューを 1 度も開けなかったときの姿（比較の実際値として出る）。</summary>
    private const string NoQualityMenu = "(menu closed)";

    /// <summary>
    /// <b>開いている</b>画質メニューの中身。<c>data-quality</c>・ラベル・<c>disabled</c>・
    /// 選択印を 1 本の文字列にする（比較の実際値がそのまま失敗の説明になる）。
    ///
    /// <para>
    /// <b><c>[hidden]</c> を除くのが要点。</b> メニューを閉じても項目は DOM に残るので、
    /// 素で引くと<b>前回開いたときの姿</b>を読んでしまう ── <c>(busy)</c> が付いたか
    /// 消えたかを見るケースでは、そのまま偽の緑になる。
    /// </para>
    /// </summary>
    private const string QualityMenuDump = """
        (function () {
          var items = document.getElementById('player').parentNode
            .querySelectorAll('.player-menu:not([hidden]) [data-quality]');
          if (items.length === 0) { return '(menu closed)'; }
          var text = [];
          for (var i = 0; i < items.length; i++) {
            text.push(items[i].dataset.quality + '=' + items[i].textContent
              + (items[i].disabled ? ' [disabled]' : '')
              + (items[i].getAttribute('aria-checked') === 'true' ? ' [checked]' : ''));
          }
          return text.join(', ');
        })()
        """;

    /// <summary>
    /// 320x240 の録画が提供する画質。<b>2 つだけ</b> ── 元のままと、ソースより小さい
    /// プリセットが 1 つも無いときに残る最小の <c>360p</c>（高さはサーバーが 240 へ丸める）。
    /// </summary>
    private const string QualityMenuOffered = "original=Original [checked], 360p=360p";

    /// <summary>枠が塞がっているときの同じメニュー（描かれるが押せない）。</summary>
    private const string QualityMenuBusy = "original=Original [checked], 360p=360p (busy) [disabled]";

    /// <summary>
    /// 状態表示が<b>この変換のもの</b>になっているか。<b>2 つの文言を受ける</b>
    /// ── 変換中（<c>transcoding 360p...</c>）と、応答を読み切った後
    /// （<c>complete (transcode 360p)</c>）。<b>5 秒ぶんの変換は 1 秒足らずで届き切る</b>ので、
    /// 前者だけを待つと「速すぎて間に合わなかった」で赤くなる（実測）。どちらも画質の名前を
    /// 含み、元のままの再生では決して現れない。
    /// </summary>
    private const string PlayerStatusNamesTheTranscode = """
        (function () {
          var text = document.getElementById('playerStatus').textContent;
          return text.indexOf('transcoding 360p') >= 0
            || text.indexOf('complete (transcode 360p)') >= 0;
        })()
        """;

    /// <inheritdoc cref="TranscodeActive"/>
    private const string TranscodeQuality = "window.PRA.player.transcodeState().quality";

    /// <inheritdoc cref="TranscodeActive"/>
    private const string TranscodeStart = "window.PRA.player.transcodeState().start";

    /// <summary>再生位置を直接書く（バーを経由しない ── 見たいのは要素のシークそのもの）。</summary>
    private static string AssignPlayerTime(double seconds)
        => "(function () { document.getElementById('player').currentTime = "
         + seconds.ToString("0.###", CultureInfo.InvariantCulture)
         + "; return true; })()";

    /// <summary>
    /// <b>1 つのタスクの中で</b>「360p を選ぶ」と「ライブのページへ移る」を続けて行う。
    ///
    /// <para>
    /// <b>要求が飛んでいる最中の離脱を決定的に作るための形である。</b> <c>fetch</c> の応答は
    /// 別のタスクでしか届かないので、この式が終わった直後に走る <c>hashchange</c> は
    /// <b>必ず</b>「要求中（<c>transcodePhase === 'requesting'</c>）」の窓に当たる
    /// ── 2 回の評価に分けると、速い応答が先に届いて窓を外す。
    /// </para>
    /// <para>
    /// <b>押せない項目は false で返す。</b> 枠の空きがブラウザへ届く前のメニューは
    /// <c>360p</c> を <c>(busy)</c> の無効な項目として描いており、<c>click()</c> は
    /// 何も起こさない ── 要求が 1 本も飛ばないまま「離脱の窓」を外したことが、
    /// 押せたことと区別できなくなる。
    /// </para>
    /// </summary>
    private const string ChooseTranscodeAndLeave = """
        (function () {
          var item = document.getElementById('player').parentNode
            .querySelector('.player-menu:not([hidden]) [data-quality="360p"]');
          if (item === null || item.disabled) { return false; }
          item.click();
          location.hash = '#/live';
          return true;
        })()
        """;

    /// <summary>1 行目に印を付ける（<see cref="FirstRowIsFresh"/> と対）。</summary>
    private const string MarkFirstRow = """
        (function () {
          var rows = document.querySelectorAll('#recordingsBody tr');
          if (rows.length === 0) { return false; }
          rows[0].dataset.stale = '1';
          return true;
        })()
        """;

    /// <inheritdoc cref="MarkFirstRow"/>
    private const string FirstRowIsFresh = """
        (function () {
          var rows = document.querySelectorAll('#recordingsBody tr');
          return 0 < rows.length && rows[0].dataset.stale !== '1';
        })()
        """;

    /// <summary>ライブのページへ移り終えたか（<c>hashchange</c> のルータが走った後）。</summary>
    private const string RecordingsPageIsHidden = "document.getElementById('pageRecordings').hidden";

    /// <summary>トークンを付けて Viewer として通すクライアント（ゲスト読み取りに頼らない）。</summary>
    private static HttpClient CreateAuthorizedClient(int port)
    {
        var client = CreateClient(port);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + Token);
        return client;
    }

    /// <summary>
    /// 変換が成立するランタイムで走っていることの<b>断定</b>（黙って skip しない）。
    ///
    /// <para>
    /// 落ちたときに読むべきものを全部メッセージに入れる ── どの <c>bin</c> を PATH の
    /// 先頭に置いたか、ランタイムを実際にどこから読んだか（<c>gst.runtime</c>）、名指しが
    /// 採られたか（<c>gst.decoders</c> の <c>preferred=</c> / <c>used=</c>）。
    /// </para>
    /// </summary>
    private async Task AssertTranscodeIsAvailableAsync(HttpClient client, AppInstance instance)
    {
        using var response = await client.GetAsync("api/capabilities", Ct);
        string text = await response.Content.ReadAsStringAsync(Ct);
        output.WriteLine($"{(int)response.StatusCode} {text}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(text);
        bool transcode = body.RootElement.GetProperty("transcode").GetBoolean();
        var decoder = body.RootElement.GetProperty("decoder");
        string reported = decoder.ValueKind == JsonValueKind.Null ? "null" : decoder.GetString()!;

        if (!transcode || !string.Equals(reported, SoftwareDecoderRuntime.Decoder, StringComparison.Ordinal))
        {
            Assert.Fail(
                $"変換が成立していません: transcode={transcode} decoder={reported}"
                + $"（期待は transcode=true decoder={SoftwareDecoderRuntime.Decoder}）。"
                + Environment.NewLine + SoftwareDecoderRuntime.Describe()
                + Environment.NewLine + RuntimeDiagnostics(instance));
        }
    }

    /// <summary>ランタイムの解決・デコーダーの確認・変換の開始をログから抜き出す。</summary>
    private static string RuntimeDiagnostics(AppInstance instance)
    {
        var log = instance.ReadActivityLog();
        var text = new StringBuilder();

        foreach (string name in new[] { "gst.runtime", "gst.decoders", "transcode.start" })
        {
            var lines = ActivityLogFile.Events(log, name);
            if (lines.Count == 0)
            {
                text.AppendLine($"{name}: 0 行");
                continue;
            }

            foreach (string line in lines)
                text.AppendLine(line);
        }

        return text.ToString();
    }

    /// <summary>
    /// 完成した録画の相対パスを一覧から取る。
    /// <b><c>height</c> が出るまで待つ</b> ── 高さは sidecar から来るので、行が現れた時点では
    /// まだ入っていないことがあり、それを見た画質メニューはプリセットを全部並べる（実測）。
    /// </summary>
    private async Task<string> FinishedRecordingPathAsync(HttpClient client)
    {
        var deadline = Stopwatch.StartNew();
        string last = "(1 度も一覧を読めていない)";

        while (deadline.Elapsed < PlaybackBudget)
        {
            using var response = await client.GetAsync("api/recordings", Ct);
            last = await response.Content.ReadAsStringAsync(Ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var body = JsonDocument.Parse(last);
                foreach (var file in body.RootElement.GetProperty("files").EnumerateArray())
                {
                    if (!file.GetProperty("inProgress").GetBoolean()
                        && file.TryGetProperty("height", out var height)
                        && height.ValueKind == JsonValueKind.Number
                        && 0 < height.GetInt32())
                    {
                        output.WriteLine($"transcode source: {file}");
                        return file.GetProperty("path").GetString()!;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        Assert.Fail(
            $"高さの入った完成した録画が一覧に出ませんでした。最後の応答: {last}");
        return "";
    }

    /// <summary>
    /// いまの空き枠（<c>auxiliaryEncodersFree</c>）。<b>SSE にしか出ない</b>ので、
    /// 1 件読むためだけに接続する（接続した直後に現在の状態が 1 件届く）。
    /// </summary>
    private async Task<int> ReadFreeSlotsAsync(HttpClient client)
    {
        using var events = await client.GetAsync(
            "api/events", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);

        using var stream = await events.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);

        string state = await ServerSentEvents.ReadDataAsync(reader, "state", SseBudget, Ct);
        using var snapshot = JsonDocument.Parse(state);
        return snapshot.RootElement.GetProperty("auxiliaryEncodersFree").GetInt32();
    }

    /// <summary>空き枠が <paramref name="expected"/> になるまで引く。</summary>
    private async Task WaitForFreeSlotsAsync(HttpClient client, int expected, string what)
    {
        var deadline = Stopwatch.StartNew();
        int last = -1;

        while (deadline.Elapsed < SlotBudget)
        {
            last = await ReadFreeSlotsAsync(client);
            if (last == expected)
            {
                output.WriteLine($"{what}: free={last}（{deadline.Elapsed.TotalSeconds:F1}s）");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        Assert.Fail(
            $"{what}: 空き枠が {SlotBudget.TotalSeconds:F0} 秒以内に {expected} になりませんでした"
            + $"（最後に読めたのは {last}）。");
    }

    /// <summary>
    /// 枠を握り続ける。<b>同じ <c>session</c> で 5 秒ごとに要求し直す</b>
    /// ── 変換は 1〜2 秒で EOS に達し、猶予が過ぎれば枠は返ってしまうので、
    /// 「応答を開いたまま」では往復の長いブラウザ操作のあいだ持たない。同じ session の
    /// 再要求は前のパイプラインを畳んで貸出を引き継ぐので、空きは 0 のまま保たれる。
    ///
    /// <para>
    /// <b>失敗を投げずに記録する。</b> 呼び出し側の <c>finally</c> で待つので、ここで
    /// 例外にすると本題の失敗をこれが上書きしてしまう ── 起きたことは
    /// <paramref name="log"/> に積み、呼び出し側が本題の後で表明する。
    /// </para>
    /// </summary>
    private static async Task KeepHoldingAsync(
        HttpClient client, string path, HttpResponseMessage first, List<string> log, CancellationToken ct)
    {
        var held = first;
        try
        {
            while (true)
            {
                await Task.Delay(HoldRefreshInterval, ct);
                var next = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
                log.Add(((int)next.StatusCode).ToString(CultureInfo.InvariantCulture));
                held.Dispose();
                held = next;
            }
        }
        catch (OperationCanceledException)
        {
            // 呼び出し側が止めた（本題は終わっている）。
        }
        catch (Exception error)
        {
            log.Add("例外: " + error.Message);
        }
        finally
        {
            held.Dispose();
        }
    }

    /// <summary>
    /// 一覧を取り直させ、<b>取り直されたことを見届ける</b>。
    ///
    /// <para>
    /// <b>Play が渡すのは、いま表を作っている応答の行そのものである。</b> 画質メニューが
    /// 何を並べられるかはその行の <c>height</c> で決まり、<b><c>height</c> は sidecar に
    /// しか無い</b> ── sidecar の届く前の応答で押すと、高さを知らないメニューが
    /// プリセットを全部並べる（実測）。だから「サーバー側に高さが出た」ことを確かめた後で
    /// 表を作り直させ、その作り直しを見届けてから押す。
    /// </para>
    /// <para>
    /// 見届け方は行に付けた印で行う ── <c>renderRows</c> は行ごと入れ替えるので、印の
    /// 消えた行が現れたことが「この取り直しの応答で作られた表」の証拠になる。
    /// 「Refresh を押した」だけでは、応答が返る前の古い表を読むことになる。
    /// </para>
    /// </summary>
    private static async Task<bool> RefreshListingAsync(EdgeCdp browser, TimeSpan budget)
    {
        Assert.True(await browser.EvaluateBoolAsync(MarkFirstRow, Ct), "一覧に行がありません。");
        Assert.True(await browser.EvaluateBoolAsync(Click("loadRecordings"), Ct), "Refresh を押せませんでした。");
        return await browser.WaitUntilAsync(FirstRowIsFresh, budget, Ct);
    }

    /// <summary>画質メニューを開いて中身を読む（開いたままにする）。</summary>
    private static async Task<string> OpenQualityMenuAsync(EdgeCdp browser)
    {
        Assert.True(
            await browser.EvaluateBoolAsync(ClickShellControl("player", "quality"), Ct),
            "バーに画質メニューのボタンがありません。");
        return await browser.EvaluateStringAsync(QualityMenuDump, Ct);
    }

    /// <summary>画質メニューを閉じる（同じボタンが開閉を兼ねる）。</summary>
    private static async Task CloseQualityMenuAsync(EdgeCdp browser)
        => Assert.True(
            await browser.EvaluateBoolAsync(ClickShellControl("player", "quality"), Ct),
            "画質メニューを閉じられませんでした。");

    /// <summary>
    /// メニューの中身が <paramref name="expected"/> になるまで、開いては閉じて引く。
    /// <b>成立したときはメニューを開いたまま返す</b>（そのまま項目を押せるように）。
    /// 返すのは最後に読めた中身で、比較は呼び出し側が行う ── 失敗の本文に実際の姿が出る。
    /// </summary>
    private static async Task<string> WaitForQualityMenuAsync(
        EdgeCdp browser, string expected, TimeSpan budget)
    {
        var deadline = Stopwatch.StartNew();
        string last = NoQualityMenu;

        while (deadline.Elapsed < budget)
        {
            last = await OpenQualityMenuAsync(browser);
            if (last == expected)
                return last;

            await CloseQualityMenuAsync(browser);
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        return last;
    }

    /// <summary>枠を握る側が使う変換の URL（ブラウザと同じ経路）。</summary>
    private static string TranscodePath(string recording, string session)
        => $"api/recording-transcode/{recording}?start=0&q=360p&session={session}";

    /// <summary>
    /// <b>画質メニューが実際に変換再生へ切り替え、位置を持ち越すこと。</b>
    ///
    /// <para>
    /// <b>ブラウザで変換再生が成立するのはここだけである。</b> 応答を MSE へ流し込む形
    /// （<c>mode='sequence'</c> ＋ <c>timestampOffset</c>）も、取り込んでいない位置への
    /// シークがパイプラインを作り直す形も、L1 と API 級の E2E は 1 行も実行しない。
    /// </para>
    /// <para>
    /// <b>シークは前へ戻る。</b> 変換は始点から前へ読むだけなので、始点より手前は
    /// 取り込みようがない ── 短いクリップでも「まだ取っていない位置」が確実に作れる
    /// （後ろへ跳ぶと、全部届いた後では作り直しを踏まずに緑になる）。
    /// </para>
    /// <para>
    /// <b>最後に離脱を見る。</b> 画質を選んだ<b>同じタスクの中で</b>ページを移ると、
    /// <c>hashchange</c> は必ず「要求が飛んでいる最中」に当たる ── その窓で止めていないと、
    /// 誰も見ていないページへ応答が届き、タブが開いているあいだ枠を握り続ける。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRecordingQualityMenuTranscodesAndKeepsThePositionAcrossQualities()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        // 枠は 1 つ。離脱で枠が返ることを空きの回復で見るので、上限は小さいほど鋭い。
        using var instance = AppInstance.Create(app, TranscodeSettings(limit: 1), configure: UseSoftwareDecoder);
        int port = WaitForPort(instance);
        using var client = CreateAuthorizedClient(port);

        await AssertTranscodeIsAvailableAsync(client, instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        // **録画のページへ移っておく。** 起動時の経路はライブなので、ここを踏まないと
        // 最後の `#/live` への遷移が `hashchange` を起こさない。
        Assert.True(await browser.EvaluateBoolAsync(GoToRecordings, Ct), "録画のページへ移れませんでした。");

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        await Task.Delay(TranscodeClipTime, Ct);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        Assert.True(
            await WaitForFirstRowAsync(
                browser,
                "s === '' && document.querySelectorAll('#recordingsBody tr').length > 0",
                PlaybackBudget),
            "録画の終わったファイルが一覧に出ませんでした: "
                + await browser.EvaluateStringAsync(FirstRowState, Ct)
                + Environment.NewLine + await browser.EvaluateStringAsync(ListingDump, Ct)
                + Environment.NewLine + instance.DiagnosticDump());

        // **sidecar が届いてから押す**（理由は RefreshListingAsync）。
        output.WriteLine("recording: " + await FinishedRecordingPathAsync(client));
        Assert.True(
            await RefreshListingAsync(browser, PlaybackBudget),
            "一覧が作り直されませんでした: " + await browser.EvaluateStringAsync(ListingDump, Ct));

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");
        Assert.True(
            await browser.WaitUntilAsync($"0 < {PlayerTime}", PlaybackBudget, Ct),
            "再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        // ---- メニューの中身 ----

        Assert.True(
            await browser.WaitUntilAsync($"{QualityMenuHidden} === false", PageBudget, Ct),
            "画質メニューが出ません（能力は "
                + await browser.EvaluateStringAsync(CapabilitiesDump, Ct) + "）。");

        // **変換の始点を先に決める。** 選んだ時点の再生位置がそのまま始点になるので、
        // ここを固定しないと、後で戻る先（3 秒）を追い越したまま始まることがある。
        Assert.True(
            await browser.WaitUntilAsync($"{TranscodeOpenSeconds} < {BufferedEnd}", PlaybackBudget, Ct),
            $"録画物が {TranscodeOpenSeconds} 秒ぶん読み込まれませんでした: "
                + await browser.EvaluateStringAsync(BufferedRanges, Ct));
        Assert.True(
            await browser.EvaluateBoolAsync(AssignPlayerTime(TranscodeOpenSeconds), Ct),
            "再生位置を書けませんでした。");

        Assert.Equal(QualityMenuOffered, await OpenQualityMenuAsync(browser));

        // ---- 360p を選ぶ ----

        Assert.True(
            await browser.EvaluateBoolAsync(ClickMenuItem("player", "quality", "360p"), Ct),
            "画質メニューに 360p の項目がありません。");

        Assert.True(
            await browser.WaitUntilAsync(TranscodeActive, TranscodeBudget, Ct),
            "変換再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct)
                + Environment.NewLine + RuntimeDiagnostics(instance));
        Assert.Equal("360p", await browser.EvaluateStringAsync(TranscodeQuality, Ct));
        Assert.True(
            await browser.WaitUntilAsync(PlayerStatusNamesTheTranscode, TranscodeBudget, Ct),
            "状態表示が変換のものになりませんでした: "
                + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        output.WriteLine(
            $"transcode opened at {await browser.EvaluateNumberAsync(TranscodeStart, Ct):F2}s: "
            + await browser.EvaluateStringAsync(TranscodeDump, Ct));

        double before = await browser.EvaluateNumberAsync(PlayerTime, Ct);
        Assert.True(
            await browser.WaitUntilAsync(
                $"{(before + 1).ToString("R", CultureInfo.InvariantCulture)} < {PlayerTime}", TranscodeBudget, Ct),
            $"変換再生の位置が進みませんでした（{before:F2} 秒のまま）: "
                + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        // ---- 取り込んでいない位置へ戻る ----

        // **成立条件そのものである。** 跳び先が既に届いていれば要素はその媒体で答えてしまい、
        // パイプラインの作り直しは 1 度も起きない（直っていても壊れていても緑になる）。
        double bufferedStart = await browser.EvaluateNumberAsync(BufferedStart, Ct);
        Assert.True(
            TranscodeSeekSeconds < bufferedStart,
            $"跳び先（{TranscodeSeekSeconds} 秒）が既に取り込まれています"
                + $"（buffered.start={bufferedStart:F2}）── この検査は成立していません。");

        Assert.True(
            await browser.EvaluateBoolAsync(AssignPlayerTime(TranscodeSeekSeconds), Ct),
            "再生位置を書けませんでした。");
        Assert.True(
            await browser.WaitUntilAsync(
                $"Math.abs({TranscodeStart} - {TranscodeSeekSeconds}) <= 0.5", TranscodeBudget, Ct),
            "シークがパイプラインを作り直しませんでした: "
                + await browser.EvaluateStringAsync(TranscodeDump, Ct));
        Assert.True(
            await browser.WaitUntilAsync(
                $"{(TranscodeSeekSeconds + 0.2).ToString("R", CultureInfo.InvariantCulture)} < {PlayerTime}",
                TranscodeBudget, Ct),
            "跳んだ先で変換再生が進みません: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        // ---- 元のままへ戻す ----

        Assert.True(
            await browser.EvaluateBoolAsync(ClickShellControl("player", "quality"), Ct),
            "バーに画質メニューのボタンがありません。");
        Assert.True(
            await browser.EvaluateBoolAsync(ClickMenuItem("player", "quality", "original"), Ct),
            "画質メニューに original の項目がありません。");

        Assert.True(
            await browser.WaitUntilAsync($"{TranscodeActive} === false", TranscodeBudget, Ct),
            "変換が止まりませんでした: " + await browser.EvaluateStringAsync(TranscodeDump, Ct));
        Assert.True(
            await browser.WaitUntilAsync($"2.5 <= {PlayerTime}", TranscodeBudget, Ct),
            "元のままへ戻したときに位置が引き継がれませんでした"
                + $"（{await browser.EvaluateNumberAsync(PlayerTime, Ct):F2} 秒）: "
                + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        double resumed = await browser.EvaluateNumberAsync(PlayerTime, Ct);
        output.WriteLine($"back on the original at {resumed:F2}s");
        Assert.True(
            await browser.WaitUntilAsync(
                $"{(resumed + 0.5).ToString("R", CultureInfo.InvariantCulture)} < {PlayerTime}", TranscodeBudget, Ct),
            "元のままへ戻した再生が進みません: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        // ---- 要求中の離脱 ----

        // 変換を手放したので、猶予が過ぎれば枠は返る（返っていないと下の断定が鈍る）。
        await WaitForFreeSlotsAsync(client, 1, "元のままへ戻した後");

        // **ブラウザ側が空きを受け取るまで待って開く。** サーバーが空きを返した直後の
        // メニューはまだ `(busy)` を描いており（空きは SSE のデバウンス越しに届く）、
        // その項目は押せない ── 押せないまま進むと、要求が 1 本も飛ばずに
        // 「離脱の窓」を外したものが下の 3 つの断定を全部通ってしまう。
        // 成立したときはメニューを開いたまま返るので、ここで開き直してはいけない。
        Assert.Equal(
            QualityMenuOffered, await WaitForQualityMenuAsync(browser, QualityMenuOffered, SlotBudget));

        // 離脱で消えない観測点。TranscodeStreams はパイプラインを組んだ時点で記録し、
        // ブラウザは応答の頭が届くまで要求を打ち切らない（要求中は AbortController を
        // まだ握っていない）ので、この 1 件は必ず出る。
        int startsBefore = ActivityLogFile.Events(instance.ReadActivityLog(), "transcode.start").Count;

        Assert.True(
            await browser.EvaluateBoolAsync(ChooseTranscodeAndLeave, Ct),
            "画質メニューの 360p を押せませんでした: "
                + await browser.EvaluateStringAsync(QualityMenuDump, Ct));
        Assert.True(
            await browser.WaitUntilAsync(RecordingsPageIsHidden, PageBudget, Ct),
            "ライブのページへ移れませんでした。");

        Assert.False(
            await browser.WaitUntilAsync(TranscodeActive, LeaveWatchWindow, Ct),
            "離脱した後で変換が始まりました: " + await browser.EvaluateStringAsync(TranscodeDump, Ct));
        await WaitForFreeSlotsAsync(client, 1, "離脱した後");

        // 「要求は出た → 止めた → 枠は返った」の 1 つ目。これが増えていなければ、
        // 上の 2 つは「何も起きなかった」ことしか語っていない。
        Assert.Equal(
            startsBefore + 1,
            ActivityLogFile.Events(instance.ReadActivityLog(), "transcode.start").Count);
    }

    /// <summary>
    /// <b>他人が枠を握っているあいだ、画質メニューは <c>(busy)</c> を描いて押させないこと。</b>
    ///
    /// <para>
    /// 断りは<b>落とすのではなく描く</b> ── 項目が消えるメニューは壊れて見える。
    /// 元のままは常に選べる（ディスクのバイトはエンコーダーを使わない）ので、
    /// 無効になるのはプリセットだけである。
    /// </para>
    /// <para>
    /// <b>枠は握り続ける。</b> 応答を開いたままにするだけでは、変換が EOS に達して猶予が
    /// 過ぎた時点で空いてしまう ── ブラウザの操作は往復が長いので、同じ <c>session</c> の
    /// 再要求で貸出を引き継がせ続ける（<see cref="KeepHoldingAsync"/>）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRecordingQualityMenuShowsBusyWhileAnotherSessionHoldsTheSlot()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, TranscodeSettings(limit: 1), configure: UseSoftwareDecoder);
        int port = WaitForPort(instance);
        using var client = CreateAuthorizedClient(port);

        await AssertTranscodeIsAvailableAsync(client, instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        await Task.Delay(TranscodeClipTime, Ct);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        Assert.True(
            await WaitForFirstRowAsync(
                browser,
                "s === '' && document.querySelectorAll('#recordingsBody tr').length > 0",
                PlaybackBudget),
            "録画の終わったファイルが一覧に出ませんでした: "
                + await browser.EvaluateStringAsync(FirstRowState, Ct)
                + Environment.NewLine + await browser.EvaluateStringAsync(ListingDump, Ct)
                + Environment.NewLine + instance.DiagnosticDump());

        // **sidecar が届いてから押す**（理由は RefreshListingAsync）。
        string recording = await FinishedRecordingPathAsync(client);
        Assert.True(
            await RefreshListingAsync(browser, PlaybackBudget),
            "一覧が作り直されませんでした: " + await browser.EvaluateStringAsync(ListingDump, Ct));

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");
        Assert.True(
            await browser.WaitUntilAsync($"0 < {PlayerTime}", PlaybackBudget, Ct),
            "再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        Assert.True(
            await browser.WaitUntilAsync($"{QualityMenuHidden} === false", PageBudget, Ct),
            "画質メニューが出ません（能力は "
                + await browser.EvaluateStringAsync(CapabilitiesDump, Ct) + "）。");

        // ---- 別 session が枠を握る ----

        string holdPath = TranscodePath(recording, "hold");

        // **応答の始末は表明の前後で切れ目なく持つ。** 通れば KeepHoldingAsync が引き取り、
        // 落ちればここで閉じる ── 開いたままの応答は変換のパイプラインを握り続ける。
        var holdLog = new List<string>();
        using var stopHolding = new CancellationTokenSource();
        var first = await client.GetAsync(holdPath, HttpCompletionOption.ResponseHeadersRead, Ct);
        Task holding;
        try
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            holding = KeepHoldingAsync(client, holdPath, first, holdLog, stopHolding.Token);
        }
        catch
        {
            first.Dispose();
            throw;
        }

        string busy;
        try
        {
            busy = await WaitForQualityMenuAsync(browser, QualityMenuBusy, SlotBudget);
        }
        finally
        {
            stopHolding.Cancel();
            await holding;
        }

        // 保持そのものが壊れていたら、上の結果は「枠が塞がっていた」ことの証拠にならない。
        Assert.Empty(holdLog.FindAll(code => code != "200"));
        Assert.Equal(QualityMenuBusy, busy);
        await CloseQualityMenuAsync(browser);

        // ---- 手放せば戻る ----

        Assert.Equal(QualityMenuOffered, await WaitForQualityMenuAsync(browser, QualityMenuOffered, SlotBudget));
        await CloseQualityMenuAsync(browser);
    }

    /// <summary>
    /// 追いかけ再生で開いた本の構成（fMP4・変換が成立するランタイム・枠 1 つ）。
    /// </summary>
    private static SettingsFile FragmentedTranscodeSettings()
    {
        var settings = RemoteBase(allowGuestRead: true);
        settings.FragmentedOutput = true;
        settings.RemoteAuxiliaryEncoderLimit = 1;
        settings.AddRecorder("R1");
        return settings;
    }

    /// <summary>
    /// 録画中に開いた本の画質メニュー。<b>高さは 0 のまま</b> ── 高さは sidecar から来るので、
    /// 録画中の行を開いた時点では入っておらず、行を開き直さない限り入らない。高さが分からない
    /// 本にはプリセットを全部並べる（サーバーが実 caps に対して解決し直す）。
    /// </summary>
    private const string QualityMenuOfferedForUnknownHeight =
        "original=Original [checked], 1080p=1080p, 720p=720p, 480p=480p, 360p=360p";

    /// <summary>
    /// <b>録画中に開いた本が完了したら、画質メニューにプリセットが出ること。</b>
    ///
    /// <para>
    /// 変換できるのは完成した本だけなので、録画中に開いた再生のメニューは
    /// <b>元のまま 1 つ</b>で、項目が 1 つのメニューは描かれない。問題はその後で、
    /// 開いた行の <c>inProgress</c> を 1 度写したきりにすると、<b>停止しても</b>
    /// メニューは出ないままになる ── 利用者から見ると「この本だけ画質を選べない」形で、
    /// 一覧へ戻って開き直すまで直らない。
    /// </para>
    /// <para>
    /// 追いかけ再生は伸びが止まったことを自分で観測している（<c>X-In-Progress</c> と
    /// 索引の <c>inProgress</c>）ので、<b>開いたままでも</b>メニューは出る。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRecordingQualityMenuOffersPresetsOnceTheFollowedRecordingFinishes()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(
            app, FragmentedTranscodeSettings(), configure: UseSoftwareDecoder);
        int port = WaitForPort(instance);
        using var client = CreateAuthorizedClient(port);

        await AssertTranscodeIsAvailableAsync(client, instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);
        Assert.True(await browser.EvaluateBoolAsync(GoToRecordings, Ct), "録画のページへ移れませんでした。");

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);

        Assert.True(
            await WaitForFirstRowAsync(browser, RowIsRecordingFragment, PlaybackBudget),
            "録画中のファイルが fragmented として一覧に出ませんでした: "
                + await browser.EvaluateStringAsync(FirstRowState, Ct)
                + Environment.NewLine + await browser.EvaluateStringAsync(ListingDump, Ct)
                + Environment.NewLine + instance.DiagnosticDump());

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");
        Assert.True(
            await browser.WaitUntilAsync($"0.5 < {PlayerTime}", PlaybackBudget, Ct),
            "録画中の追いかけ再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        // 録画中は元のまま 1 つきり ── メニューそのものが描かれない。
        // **この断定が先に立たないと、下の緑は「最初から出ていた」ことしか語らない。**
        Assert.True(
            await browser.EvaluateBoolAsync(QualityMenuHidden, Ct),
            "録画中の本に画質メニューが出ています: " + await browser.EvaluateStringAsync(QualityMenuDump, Ct));

        // 停止するまで少し録っておく（完了後の再生に進む余地を残すため）。
        await Task.Delay(TimeSpan.FromSeconds(6), Ct);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        Assert.True(
            await browser.WaitUntilAsync(
                $"{TextOf("playerStatus")}.indexOf('complete') === 0", PlaybackBudget, Ct),
            "停止しても complete になりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        // ---- ここが本題 ----

        Assert.True(
            await browser.WaitUntilAsync($"{QualityMenuHidden} === false", PlaybackBudget, Ct),
            "完了しても画質メニューが出ません（開いた行の inProgress が写したままになっている）: "
                + await browser.EvaluateStringAsync(CapabilitiesDump, Ct));

        Assert.Equal(
            QualityMenuOfferedForUnknownHeight,
            await WaitForQualityMenuAsync(browser, QualityMenuOfferedForUnknownHeight, SlotBudget));
        await CloseQualityMenuAsync(browser);
    }

    /// <summary>
    /// <b>バーの「+10s」が実際に位置を 10 秒進め、速度メニューが <c>playbackRate</c> を変える。</b>
    ///
    /// <para>
    /// 検査は<b>非 fragmented の完成ファイル</b>で行う ── 追いかけ再生（MSE）だと、
    /// 位置が進んだ理由が「ボタン」なのか「ライブ端への寄せ」なのかを外から区別できない。
    /// 素の <c>&lt;video src&gt;</c> なら寄せは走らないので、進んだぶんはボタンのものである。
    /// </para>
    /// <para>
    /// <b>閾値ではなく速さで判定する。</b> 3 秒の窓で 9 秒進むことは等速の再生では起こらないので、
    /// 「押さなくても進む」で緑になる余地が無い。
    /// </para>
    /// <para>
    /// 速度の 1.0 への自動復帰（ライブ端に付いたら戻す）は<b>ここでは見ない</b> ──
    /// 追いかけ再生の端は毎秒動くので、判定の瞬間に端との差が 0.5 秒未満である保証が作れない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheBarSkipsForwardTenSecondsAndItsMenuSetsTheSpeed()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        // **fragmented は明示的に切る**（製品の既定は ON）── 追いかけ再生（MSE）だと
        // 位置が進んだ理由をボタンとライブ端への寄せに分けられない。
        var settings = RemoteBase(allowGuestRead: true);
        settings.FragmentedOutput = false;
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        await Task.Delay(ClipRecordingTime, Ct);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        // **行が在ることも述語に入れる。** 完成した非 fragmented の state 欄は空文字なので、
        // 「`recording` を含まない」だけだと**行が 1 つも無い状態でも真になる**。
        Assert.True(
            await WaitForFirstRowAsync(
                browser,
                "s === '' && document.querySelectorAll('#recordingsBody tr').length > 0",
                PlaybackBudget),
            "録画の終わったファイルが一覧に出ませんでした: " + await browser.EvaluateStringAsync(FirstRowState, Ct));

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");

        // **バッファが 12 秒ぶん来てから押す。** 任意シークは fragment 索引が要り、
        // 非 fragmented のファイルには索引が無い ── ＋10 秒はバッファの中へ丸められるので、
        // 届いていなければ丸められて動かない。
        Assert.True(
            await browser.WaitUntilAsync($"12 < {BufferedEnd}", PlaybackBudget, Ct),
            "録画物が 12 秒ぶん読み込まれませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        double before = await browser.EvaluateNumberAsync(PlayerTime, Ct);
        Assert.True(await browser.EvaluateBoolAsync(ClickShellControl("player", "forward-10"), Ct),
            "バーに +10s のボタンがありません。");

        string invariant = (before + 9).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(
            await browser.WaitUntilAsync($"{invariant} < {PlayerTime}", TimeSpan.FromSeconds(3), Ct),
            $"+10s で位置が進みませんでした（{before:F2} 秒のまま）: "
            + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        // 速度メニュー: ボタンで開き、1.5 の項目を押す。
        Assert.True(await browser.EvaluateBoolAsync(ClickShellControl("player", "speed"), Ct),
            "バーに速度メニューのボタンがありません。");
        Assert.True(await browser.EvaluateBoolAsync(ClickMenuItem("player", "speed", "1.5"), Ct),
            "速度メニューに 1.5 の項目がありません。");

        Assert.Equal(1.5, await browser.EvaluateNumberAsync("document.getElementById('player').playbackRate", Ct));
    }

    /// <summary>
    /// <b>ライブの画質メニューが <c>#previewMode</c> を書き、配信が DASH へ切り替わる。</b>
    ///
    /// <para>
    /// <c>&lt;select&gt;</c> は画面から隠してあり、値を書くのはこのメニューだけになった
    /// ── ここが切れると、DASH のプレビューへ行く道が UI から消える。
    /// 切り替わったことは <c>previewStatus</c> が DASH の状態表示になることで見る
    /// （<see cref="TheDashPreviewPlaysInTheBrowser"/> と同じ流儀）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheLiveQualityMenuSwitchesThePreviewToDash()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, DashSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        Assert.True(instance.WaitForActivityLogEvent("recorder.init ok", PageBudget),
            "recorder.init ok が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.True(
            await browser.WaitUntilAsync("document.querySelectorAll('#recordersBody tr').length > 0", PageBudget, Ct),
            "レコーダーの一覧が出ませんでした。");

        // メニューは走っているプレビューの画質を差し替えるものなので、まず既定（録画画質）で開く。
        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPreview, Ct), "行に Preview のボタンがありません。");
        Assert.True(
            await browser.WaitUntilAsync($"{TextOf("previewStatus")}.indexOf('streaming') === 0", PageBudget, Ct),
            "録画画質のプレビューが始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));

        Assert.True(await browser.EvaluateBoolAsync(ClickShellControl("previewPlayer", "quality"), Ct),
            "プレビューのバーに画質メニューのボタンがありません。");
        // **`dash` という項目は無い。** DASH は 1 つの画質ではなく画質の族なので、
        // 項目が名乗るのはサーバーが知っている id である ── 設定の 4 値そのままが `custom`。
        Assert.True(await browser.EvaluateBoolAsync(ClickMenuItem("previewPlayer", "quality", "custom"), Ct),
            "画質メニューに custom の項目がありません。");

        Assert.Equal("dash", await browser.EvaluateStringAsync("document.getElementById('previewMode').value", Ct));
        Assert.True(
            await browser.WaitUntilAsync($"{TextOf("previewStatus")}.indexOf('DASH') === 0", PageBudget, Ct),
            "画質メニューで DASH へ切り替わりませんでした: " + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));

        Assert.True(await browser.EvaluateBoolAsync(Click("stopPreview"), Ct), "Stop preview を押せませんでした。");

        // **止めたらバーも止まった見た目に戻る。** `src` を外して `load()` しても
        // `currentSrc` には直前の blob: URL が残る（Chromium）ので、そこを見て判定すると
        // 止めたはずのプレイヤーに LIVE バッジと動く時計が居座り、待機中の表示も出ない。
        // プレビューを一度走らせたあとで見ることに意味がある ── 走らせなければ
        // `currentSrc` は空のままで、この退化は起きない。
        Assert.True(
            await browser.WaitUntilAsync(
                $"{ShellControl("previewPlayer", "play")}.disabled"
                + $" && {ShellPart("previewPlayer", ".player-idle")}.hidden === false"
                + $" && {ShellPart("previewPlayer", ".player-badge")}.hidden === true",
                PageBudget,
                Ct),
            "プレビューを止めてもバーが動いているままです: "
            + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));
    }

    /// <summary>画質の一覧がサーバーから届いたか（取得は投げっぱなしなので、押す前に待つ）。</summary>
    private const string QualitiesLoaded = "PRA.player.previewQualityState() !== null";

    /// <summary>
    /// 配信中の Representation が、サーバーが答えた <paramref name="id"/> の解決値と一致するか。
    /// <b>期待値はサーバーの数値そのもの</b> ── 解決の算術をテストへ写さない。
    /// </summary>
    private static string RepresentationMatches(string id) => $$"""
        (function () {
          var state = PRA.player.previewQualityState();
          var offered = PRA.player.representations();
          if (state === null || offered.length === 0) { return false; }
          var preset = null;
          for (var i = 0; i < state.qualities.length; i++) {
            if (state.qualities[i].id === '{{id}}') { preset = state.qualities[i]; }
          }
          return preset !== null && offered[0].width === preset.width && offered[0].height === preset.height;
        })()
        """;

    /// <summary>
    /// <b>画質メニューでプリセットを選ぶと、配信物がその解像度になる。</b>
    ///
    /// <para>
    /// <b>トークンで入る。</b> 切り替えはそのレコーダーの<b>全視聴者</b>に効くので
    /// <c>Operator</c> 以上に限ってあり、ゲスト（＝<c>Viewer</c>）ではプリセットの項目が
    /// 押せない ── ここを guest のまま書くと、押せないボタンを押して待つテストになる。
    /// </para>
    /// <para>
    /// 判定は <b><c>Representation</c> の幅・高さがサーバーの答えと一致すること</b>で行う。
    /// この機械のソース解像度は決め打ちにできず、プリセットの意味は「ソースに対する相対」
    /// だからである（<c>DashPreviewTests</c> の同名の検査と同じ流儀）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheLiveQualityMenuPicksAPresetAndTheRepresentationFollows()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, DashSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        Assert.True(instance.WaitForActivityLogEvent("recorder.init ok", PageBudget),
            "recorder.init ok が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        using var client = CreateClient(port);
        await using var browser = await EdgeCdp.LaunchAsync(Ct);

        // `?token=` は Admin のセッションを配る入口（302 のあとに index.html が出る）。
        await browser.NavigateAsync($"http://127.0.0.1:{port}/?token={Token}", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.True(
            await browser.WaitUntilAsync("document.querySelectorAll('#recordersBody tr').length > 0", PageBudget, Ct),
            "レコーダーの一覧が出ませんでした。");

        try
        {
            // メニューは走っているプレビューの画質を差し替えるものなので、まず既定（録画画質）で開く。
            Assert.True(await browser.EvaluateBoolAsync(ClickFirstPreview, Ct), "行に Preview のボタンがありません。");
            Assert.True(
                await browser.WaitUntilAsync($"{TextOf("previewStatus")}.indexOf('streaming') === 0", PageBudget, Ct),
                "録画画質のプレビューが始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));

            // 一覧の取得は投げっぱなしなので、届く前に開くとプリセットの項目がまだ無い。
            Assert.True(await browser.WaitUntilAsync(QualitiesLoaded, PageBudget, Ct),
                "画質の一覧が届きませんでした: " + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));

            Assert.True(await browser.EvaluateBoolAsync(ClickShellControl("previewPlayer", "quality"), Ct),
                "プレビューのバーに画質メニューのボタンがありません。");
            Assert.True(await browser.EvaluateBoolAsync(ClickMenuItem("previewPlayer", "quality", "360p"), Ct),
                "画質メニューに 360p の項目がありません。");

            // 切り替えは POST → モード切替の順なので、`#previewMode` は少し遅れて `dash` になる。
            Assert.True(
                await browser.WaitUntilAsync("document.getElementById('previewMode').value === 'dash'", PageBudget, Ct),
                "プリセットを選んでも DASH へ切り替わりませんでした: "
                + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));

            Assert.True(
                await browser.WaitUntilAsync(RepresentationMatches("360p"), PlaybackBudget, Ct),
                "配信物が 360p の解決値になりませんでした: "
                + await browser.EvaluateStringAsync("JSON.stringify(PRA.player.representations())", Ct)
                + " / " + await browser.EvaluateStringAsync("JSON.stringify(PRA.player.previewQualityState())", Ct));

            // 状態表示は「いま配信しているもの」（manifest の `X-Dash-Quality`）を名乗る。
            Assert.True(
                await browser.WaitUntilAsync(
                    $"{TextOf("previewStatus")}.indexOf('DASH: live (360p)') === 0", PlaybackBudget, Ct),
                "状態表示が配信中の画質を名乗りません: "
                + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));

            // 設定どおりへ戻す ── `custom` はクランプしないので、既定の 1280x720 がそのまま出る。
            Assert.True(await browser.EvaluateBoolAsync(ClickShellControl("previewPlayer", "quality"), Ct),
                "プレビューのバーに画質メニューのボタンがありません（2 度目）。");
            Assert.True(await browser.EvaluateBoolAsync(ClickMenuItem("previewPlayer", "quality", "custom"), Ct),
                "画質メニューに custom の項目がありません。");

            Assert.True(
                await browser.WaitUntilAsync(
                    "PRA.player.representations().length > 0 && PRA.player.representations()[0].height === 720",
                    PlaybackBudget,
                    Ct),
                "設定の高さ（720）へ戻りませんでした: "
                + await browser.EvaluateStringAsync("JSON.stringify(PRA.player.representations())", Ct));

            Assert.True(await browser.EvaluateBoolAsync(Click("stopPreview"), Ct), "Stop preview を押せませんでした。");
            Assert.True(
                await browser.WaitUntilAsync($"{TextOf("previewStatus")} === 'stopped'", PageBudget, Ct),
                "Stop preview で配信が畳まれませんでした: "
                + await browser.EvaluateStringAsync(TextOf("previewStatus"), Ct));
        }
        finally
        {
            // **override はプロセスに残る。** 画面が壊れている場合でも戻せるよう、
            // 後始末は UI ではなく API から行う。
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/recorders/R1/preview/quality");
            request.Headers.Add("Authorization", "Bearer " + Token);
            request.Headers.Add("X-PRApp-Client", "1");
            request.Content = new StringContent("{\"id\":\"custom\"}", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, Ct);
            output.WriteLine($"restore custom: {(int)response.StatusCode}");
        }
    }

    /// <summary>
    /// <b>ブラウザ標準のコントロールが出ておらず、自前のバーが両方の要素に付いている。</b>
    /// 両方が同時に出ると操作が二重になり、片方も出ないと再生を止める手段が画面から消える。
    /// </summary>
    [Fact]
    public async Task NeitherPlayerShowsNativeControlsAndBothCarryTheBar()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, DashSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.False(
            await browser.EvaluateBoolAsync("document.getElementById('player').hasAttribute('controls')", Ct),
            "#player に controls が残っています。");
        Assert.False(
            await browser.EvaluateBoolAsync("document.getElementById('previewPlayer').hasAttribute('controls')", Ct),
            "#previewPlayer に controls が残っています。");

        foreach (string id in new[] { "player", "previewPlayer" })
        {
            Assert.True(
                await browser.EvaluateBoolAsync(
                    $"document.getElementById('{id}').parentNode.querySelector('.player-bar') !== null", Ct),
                $"#{id} に .player-bar が付いていません。");
        }

        // 未選択のうちはバーが押せない（押せると、何も読み込んでいない要素に指示が飛ぶ）。
        Assert.True(
            await browser.EvaluateBoolAsync($"{ShellControl("player", "forward-10")}.disabled", Ct),
            "録画を選ぶ前からバーが有効になっています。");
    }

    // ---- (7) 任意シーク（fragment 索引） ----

    /// <summary>
    /// <see cref="FragmentedSettings"/> を約 20Mbit のソースにしたもの。
    /// <b>取り込みを絞って「追い付けない取り込み」を作れる大きさが要る</b>
    /// ── 既定の 320x240 の SMPTE バーは数十 kbit にしかならず、
    /// ブラウザの取り込みをそこまで絞ると 1 フラグメントの取得も終わらない。
    /// </summary>
    private static SettingsFile BulkyFragmentedSettings()
    {
        var settings = RemoteBase(allowGuestRead: true);
        settings.FragmentedOutput = true;
        settings.AddRecorder("R1").AsBulkyButCheapToEncode();
        return settings;
    }

    /// <summary>
    /// ブラウザの取り込み速度の上限(bytes/s)。ソースの約 20Mbit に対して 4Mbit なので、
    /// <b>録画は取り込みより速く伸びる</b> ── 索引が示す尺とバッファの終端が離れていき、
    /// 「まだ取っていない位置」が作れる。1 フラグメント（1 秒・約 2.5MB）の取得は
    /// この速度で約 5 秒で、<see cref="PlaybackBudget"/> に収まる。
    /// </summary>
    private const double SeekThrottleBytesPerSecond = 512 * 1024;

    /// <summary>索引が届いてシークバーが操作できるようになったか。</summary>
    private const string SeekBarIsEnabled = """
        (function () {
          var bar = document.getElementById('player').parentNode.querySelector('[data-action="seek"]');
          return bar !== null && !bar.disabled;
        })()
        """;

    /// <summary>シークバーを動かす（利用者のドラッグと同じ <c>input</c> を出す）。</summary>
    private static string DragSeekBar(double seconds)
        => $$"""
            (function () {
              var bar = document.getElementById('player').parentNode.querySelector('[data-action="seek"]');
              bar.value = '{{seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}';
              bar.dispatchEvent(new Event('input'));
              return true;
            })()
            """;

    /// <summary>
    /// <b>バッファの終端より先へシークバーを動かす。</b> 索引が示す尺（バーの <c>max</c>）は
    /// 取り込みより先へ行っているので、その手前で「まだ取っていない位置」が選べる。
    /// 選べなければ −1（＝取り込みが絞れていない＝この検査は成立していない）。
    /// </summary>
    private const string DragBeyondBuffered = """
        (function () {
          var video = document.getElementById('player');
          var bar = video.parentNode.querySelector('[data-action="seek"]');
          var ranges = video.buffered;
          var end = ranges.length === 0 ? 0 : ranges.end(ranges.length - 1);
          var target = Math.min(Number(bar.max) - 1.5, end + 4);
          if (target < end + 2) { return -1; }
          window.__target = target;
          bar.value = String(target);
          bar.dispatchEvent(new Event('input'));
          return target;
        })()
        """;

    /// <summary>索引の尺とバッファの終端の差（＝まだ取っていない秒数）。</summary>
    private const string UnfetchedSeconds = """
        (function () {
          var video = document.getElementById('player');
          var bar = video.parentNode.querySelector('[data-action="seek"]');
          var ranges = video.buffered;
          return Number(bar.max) - (ranges.length === 0 ? 0 : ranges.end(ranges.length - 1));
        })()
        """;

    /// <summary>
    /// <b>録画中のファイルの任意の位置へシークできること。</b> ファイルは <c>mvhd</c> の尺が 0 で
    /// <c>sidx</c> も持たないので、これが成り立つのは<b>索引が届いているときだけ</b>である
    /// ── 索引が無ければシークバーは操作できないままで、この検査は最初の待ちで落ちる。
    ///
    /// <para>
    /// 前半は<b>取り込み済みの位置</b>（1 秒）へ戻る道: 位置がそこへ移り、再生が続き、
    /// ライブ復帰でライブ端付近へ戻る。
    /// </para>
    /// <para>
    /// 後半が<b>この波の本体</b>である ── 取り込みを絞って「索引は伸びるのに取り込みが
    /// 追い付かない」状態を作り、<b>まだ 1 バイトも取っていない位置</b>へ跳ぶ。
    /// 供給側は取り込みを畳んで索引が指すフラグメントから取り直すので、
    /// <c>buffered.start</c> が先頭から離れる（＝古い媒体が解放された証人）。
    /// 絞らないと取り込みがライブ端に貼り付いて跳び先が作れず、
    /// <see cref="DragBeyondBuffered"/> が −1 を返してそこで落ちる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSeekBarReachesAPositionThatHasNotBeenFetched()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, BulkyFragmentedSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);

        Assert.True(
            await WaitForFirstRowAsync(browser, RowIsRecordingFragment, PlaybackBudget),
            "録画中のファイルが fragmented として一覧に出ませんでした: "
                + await browser.EvaluateStringAsync(FirstRowState, Ct)
                + Environment.NewLine + await browser.EvaluateStringAsync(ListingDump, Ct)
                + Environment.NewLine + instance.DiagnosticDump());

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");
        Assert.True(
            await browser.WaitUntilAsync($"0.5 < {PlayerTime}", PlaybackBudget, Ct),
            "追いかけ再生が始まりませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        // 索引が届くまではシークバーは表示だけで、操作はできない。
        Assert.True(
            await browser.WaitUntilAsync(SeekBarIsEnabled, PlaybackBudget, Ct),
            "索引が届かずシークバーが操作できないままです: "
            + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        // ---- 前半: 取り込み済みの位置へ戻る ----

        Assert.True(await browser.EvaluateBoolAsync(DragSeekBar(1), Ct), "シークバーがありません。");
        Assert.True(
            await browser.WaitUntilAsync($"Math.abs({PlayerTime} - 1) < 0.5", PageBudget, Ct),
            $"1 秒へ移りませんでした（{await browser.EvaluateNumberAsync(PlayerTime, Ct):F2} 秒）: "
            + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        // 止まっていないこと（位置が移っただけで再生が死んでいれば、ここで落ちる）。
        Assert.True(
            await browser.WaitUntilAsync($"1.5 < {PlayerTime}", PageBudget, Ct),
            "シークした先で再生が進みません: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        // ---- ライブ復帰 ----

        Assert.True(await browser.EvaluateBoolAsync(ClickShellControl("player", "live"), Ct),
            "バーにライブ復帰のボタンがありません。");
        Assert.True(
            await browser.WaitUntilAsync($"{LiveLag} < 3", PageBudget, Ct),
            $"ライブ端へ戻りませんでした（遅れ {await browser.EvaluateNumberAsync(LiveLag, Ct):F2} 秒）。");

        // ---- 後半: まだ取っていない位置へ跳ぶ ----

        await browser.ThrottleDownloadAsync(SeekThrottleBytesPerSecond, Ct);

        Assert.True(
            await browser.WaitUntilAsync($"6 < {UnfetchedSeconds}", PlaybackBudget, Ct),
            $"取り込みが遅れていません（未取得 {await browser.EvaluateNumberAsync(UnfetchedSeconds, Ct):F2} 秒）"
            + " ── この検査は成立していません。");

        double target = await browser.EvaluateNumberAsync(DragBeyondBuffered, Ct);
        output.WriteLine($"seek target {target:F2}s, " + await browser.EvaluateStringAsync(BufferedRanges, Ct));
        Assert.True(0 < target, "バッファの外の跳び先が作れませんでした ── この検査は成立していません。");

        Assert.True(
            await browser.WaitUntilAsync("Math.abs(" + PlayerTime + " - window.__target) < 0.7", PageBudget, Ct),
            $"取っていない位置（{target:F2} 秒）へ位置が移りませんでした"
            + $"（{await browser.EvaluateNumberAsync(PlayerTime, Ct):F2} 秒）: "
            + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        // **取り直しの証人。** 位置を書くだけなら一瞬で終わるので、そこで見ても何も言えない
        // ── 媒体が跳び先に届いて初めて、供給側が張り替わったと言える。
        // 先頭から溜めたままなら 0 のまま、取り直せていなければ空のままになる。
        Assert.True(
            await browser.WaitUntilAsync($"0.5 < {BufferedStart}", PlaybackBudget, Ct),
            $"跳んだ先の媒体が届きません（{await browser.EvaluateStringAsync(BufferedRanges, Ct)}）。");
        output.WriteLine("after the seek: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        Assert.True(
            await browser.WaitUntilAsync("window.__target + 0.5 < " + PlayerTime, PlaybackBudget, Ct),
            "跳んだ先で再生が進みません: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);
    }

    /// <summary>プレイヤーの状態表示がエラーになっているか（<c>status()</c> が付ける class）。</summary>
    private const string PlayerStatusIsError =
        "document.getElementById('playerStatus').className.indexOf('error') >= 0";

    /// <summary>2 度目のシーク先(秒)。<see cref="LongClipSeconds"/> の中ほど。</summary>
    private const double SecondSeekSeconds = 40;

    /// <summary>
    /// <b>読み切ったファイルでも、シークが何度でも効くこと。</b>
    ///
    /// <para>
    /// 読み切った <c>MediaSource</c> は <c>endOfStream()</c> で <c>ended</c> になっているが、
    /// <b><c>ended</c> は終着点ではない</b> ── そこで <c>SourceBuffer.remove()</c> を呼ぶと
    /// MSE の規定どおり <c>open</c> へ戻り、<c>sourceopen</c> が<b>もう一度</b>配送される。
    /// 任意シークは remove から始まるので、これはシークのたびに起こる。
    /// 開始時のリスナーを付けっぱなしにしていると、そこで 2 本目の <c>SourceBuffer</c> を足し、
    /// <b>読み尽くした応答本体</b>で供給の周回をもう 1 つ始める（<c>getReader()</c> が投げる）
    /// ── シークが握っている状態が新しい方に差し替わり、以後のシークが壊れる。
    /// </para>
    /// <para>
    /// <b>位置だけを見ていては気付けない。</b> 1 度目のシークは古い側の周回が最後まで
    /// 面倒を見るので、<b>壊れていても位置は 1 秒へ移り、再生も進む</b>。証人は 2 つ:
    /// <b>画面に失敗が出ていないこと</b>（<b>実測</b>: リスナーを付けっぱなしにすると
    /// <c>Failed to execute 'addSourceBuffer' … reached the limit of SourceBuffer objects</c>
    /// が状態表示へ出る）と、<b>2 度目のシークも効くこと</b>（ブラウザが 2 本目を
    /// 受け入れる場合は、シークが握る状態がそちらへ差し替わっている）。
    /// </para>
    /// <para>
    /// <b>クリップは起動点（70 秒）より長く、取り込みは絞る</b>
    /// （<see cref="LongClipThrottleBytesPerSecond"/>）── トリムが先頭を削っていないと
    /// 先頭付近が最初から <c>buffered</c> の中にあり、シークは位置を書くだけで終わって
    /// <c>remove()</c> を通らない（＝この検査は成立しない）。踏んだことは
    /// <c>buffered.start</c> で見届ける。
    /// </para>
    /// <para>
    /// <b>ロードした GStreamer に <c>gst-launch-1.0.exe</c> か x264 のプラグインが無い場合は
    /// Skip する</b>ので、緑だから走ったとは限らない ── 実行結果の skip 件数を見ること。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFinishedRecording_CanBeSeekedAgainAfterItWasReadToTheEnd()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, FragmentedSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);

        Assert.True(instance.WaitForActivityLogEvent("gst.runtime", StartBudget),
            "gst.runtime が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        string? launcher = GstLaunchTool.FindLauncher(instance);
        Assert.SkipWhen(launcher is null,
            "ロードした GStreamer の bin に gst-launch-1.0.exe がありません"
            + "（gst.runtime の dir= を見て探しています）。");
        Assert.SkipUnless(GstLaunchTool.HasX264Plugin(launcher!),
            $"ロードした GStreamer に x264 のプラグインがありません（{GstLaunchTool.PluginDirectoryOf(launcher!)}）。");

        string clip = Path.Combine(instance.RecordingsDir, "long-clip.mp4");
        await WriteLongFragmentedClipAsync(launcher!, clip, instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        Assert.True(
            await WaitForFirstRowAsync(browser, RowIsFinishedFragment, PlaybackBudget),
            "作ったクリップが fragmented として一覧に出ませんでした: " + await browser.EvaluateStringAsync(FirstRowState, Ct));

        await browser.ThrottleDownloadAsync(LongClipThrottleBytesPerSecond, Ct);

        Assert.True(await browser.EvaluateBoolAsync(ClickFirstPlay, Ct), "一覧に行がありません。");

        // `complete` ＝ endOfStream() 済み ＝ MediaSource は `ended`。ここからが対象である。
        Assert.True(
            await browser.WaitUntilAsync($"{TextOf("playerStatus")}.indexOf('complete') === 0", PlaybackBudget, Ct),
            "クリップを読み切れませんでした: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        Assert.True(
            await browser.WaitUntilAsync(SeekBarIsEnabled, PlaybackBudget, Ct),
            "索引が届かずシークバーが操作できないままです: "
            + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));

        double bufferedStart = await browser.EvaluateNumberAsync(BufferedStart, Ct);
        output.WriteLine("read to the end: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));
        Assert.True(1 < bufferedStart,
            $"トリムを踏んでいません（buffered.start が {bufferedStart:F3}）── 先頭が既に取ってあると"
            + " シークは位置を書くだけで終わり、この検査は成立しません。");

        // ---- 1 度目: 先頭へ戻る（remove から始まる道） ----

        Assert.True(await browser.EvaluateBoolAsync(DragSeekBar(1), Ct), "シークバーがありません。");
        Assert.True(
            await browser.WaitUntilAsync($"Math.abs({PlayerTime} - 1) < 0.5", PlaybackBudget, Ct),
            $"1 秒へ移りませんでした（{await browser.EvaluateNumberAsync(PlayerTime, Ct):F2} 秒）: "
            + await browser.EvaluateStringAsync(BufferedRanges, Ct));
        Assert.True(
            await browser.WaitUntilAsync($"1.5 < {PlayerTime}", PlaybackBudget, Ct),
            "シークした先で再生が進みません: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        Assert.False(await browser.EvaluateBoolAsync(PlayerStatusIsError, Ct),
            "シークが失敗を出しました: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));
        output.WriteLine("after the first seek: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        // ---- 2 度目: ここで、差し替わった状態が出る ----

        Assert.True(await browser.EvaluateBoolAsync(DragSeekBar(SecondSeekSeconds), Ct), "シークバーがありません。");
        Assert.True(
            await browser.WaitUntilAsync(
                $"Math.abs({PlayerTime} - {SecondSeekSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}) < 0.7",
                PlaybackBudget, Ct),
            $"2 度目のシークで {SecondSeekSeconds:F0} 秒へ移りませんでした"
            + $"（{await browser.EvaluateNumberAsync(PlayerTime, Ct):F2} 秒）: "
            + await browser.EvaluateStringAsync(BufferedRanges, Ct));
        Assert.True(
            await browser.WaitUntilAsync(
                $"{(SecondSeekSeconds + 1).ToString("R", System.Globalization.CultureInfo.InvariantCulture)} < {PlayerTime}",
                PlaybackBudget, Ct),
            "2 度目のシークの先で再生が進みません: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));

        Assert.False(await browser.EvaluateBoolAsync(PlayerStatusIsError, Ct),
            "2 度目のシークが失敗を出しました: " + await browser.EvaluateStringAsync(TextOf("playerStatus"), Ct));
        output.WriteLine("after the second seek: " + await browser.EvaluateStringAsync(BufferedRanges, Ct));
    }

    // ---- (7) 録画一覧のページ（カレンダー・絞り込み・自動更新） ----

    /// <summary>
    /// 一覧のページを見るケースの構成。<b>レコーダーは 2 台</b>で、録画するのは片方だけ
    /// ── 絞り込みが「効いている」と言うには、外れる側が要る。
    /// </summary>
    private static SettingsFile ListingSettings()
    {
        var settings = RemoteBase(allowGuestRead: true);
        settings.AddRecorder(UsedRecorder);
        settings.AddRecorder(UnusedRecorder);
        return settings;
    }

    /// <summary>
    /// <see cref="ListingSettings"/> と同じ 2 台に<b>製品既定のファイル名テンプレート</b>を
    /// 与えたもの。
    ///
    /// <para>
    /// <b>ハーネスの既定（<c>{Name}_{Now:HHmmssfff}.mp4</c>）ではレコーダー絞り込みの
    /// 半分が無検査になる。</b> sidecar の無い行（＝録画中の行）のレコーダー名は
    /// <c>RecordingIndex.FilenamePattern</c>（<c>yyyyMMdd_HHmmss_&lt;名前&gt;.mp4</c>）
    /// でしか決まらず、ハーネスの形はこれに当たらないので名前が空になる。
    /// そこだけを見るこのケースに限って製品既定を入れる
    /// （<b>ハーネスの既定は変えない</b> ── ms 精度は同一秒の衝突を避けるためにある）。
    /// </para>
    /// </summary>
    private static SettingsFile ProductTemplateListingSettings()
    {
        var settings = RemoteBase(allowGuestRead: true);
        settings.AddRecorder(UsedRecorder).FilenameTemplate = ProductFilenameTemplate;
        settings.AddRecorder(UnusedRecorder).FilenameTemplate = ProductFilenameTemplate;
        return settings;
    }

    /// <summary>製品既定のファイル名テンプレート（<c>EventRecorder.FilenameTemplate</c> の既定）。</summary>
    private const string ProductFilenameTemplate = "{Now:yyyyMMdd_HHmmss}_{Name}.mp4";

    private const string UsedRecorder = "R1";
    private const string UnusedRecorder = "R2";

    /// <summary>録画を回す時間（<c>mp4mux</c> が意味のある長さを書くのに足りる最小限）。</summary>
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// sidecar が書かれ、索引を通って一覧へ出るまでの上限
    /// （<c>RecordingDeliveryTests.SidecarBudget</c> と同じ性質のもの）。
    /// </summary>
    private static readonly TimeSpan SidecarBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <b>Refresh を押さずに</b>一覧が入れ替わるまでの上限。実測はおよそ 2 秒
    /// （索引の 500ms デバウンス → SSE → 画面側の 1 秒デバウンス → 取得）。
    /// </summary>
    private static readonly TimeSpan AutoRefreshBudget = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static HttpClient CreateClient(int port)
        => new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = RequestTimeout };

    /// <summary>
    /// 書き込み要求を出す。<b>ここが「開始手段」そのものである</b> ── HTTP から始めた録画の
    /// <c>trigger</c> は <c>remote</c> になり、それを一覧の Trigger 欄で確かめる。
    /// </summary>
    private static async Task PostAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Authorization", "Bearer " + Token);
        request.Headers.Add("X-PRApp-Client", "1");

        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>HTTP から 1 本録って止める（<c>trigger</c> は <c>remote</c> になる）。</summary>
    private static async Task RecordOverHttpAsync(HttpClient client)
    {
        await PostAsync(client, $"api/recorders/{UsedRecorder}/start");
        await Task.Delay(RecordingWindow, Ct);
        await PostAsync(client, $"api/recorders/{UsedRecorder}/stop");
    }

    /// <summary>録画のページへ移る（<c>hashchange</c> がルータとこのページの読み込みを回す）。</summary>
    private const string GoToRecordings =
        "(function () { location.hash = '#/recordings'; return true; })()";

    private static string FirstRowCell(int index) => $$"""
        (function () {
          var rows = document.querySelectorAll('#recordingsBody tr');
          return rows.length === 0 ? '' : rows[0].cells[{{index}}].textContent;
        })()
        """;

    private const string RowCount = "document.querySelectorAll('#recordingsBody tr').length";

    /// <summary>1 行目のサムネイルの <c>src</c>（<c>&lt;img class="thumb"&gt;</c> でなければ空）。</summary>
    private const string FirstRowThumbnailSrc = """
        (function () {
          var rows = document.querySelectorAll('#recordingsBody tr');
          if (rows.length === 0) { return ''; }
          var image = rows[0].cells[0].getElementsByTagName('img')[0];
          return image && image.className.indexOf('thumb') >= 0 ? image.getAttribute('src') : '';
        })()
        """;

    private const string TableHeadings = """
        (function () {
          var cells = document.querySelectorAll('#recordingsTable thead th');
          var out = [];
          for (var i = 0; i < cells.length; i++) { out.push(cells[i].textContent); }
          return out.join('|');
        })()
        """;

    /// <summary>選ばれている日が、ブラウザの今日と同じ日付であること。</summary>
    private const string SelectedDayIsToday = """
        (function () {
          var day = document.querySelector('#calendarGrid .calendar-day.selected');
          if (day === null) { return false; }
          var now = new Date();
          var pad = function (value) { return value < 10 ? '0' + value : String(value); };
          return day.dataset.date === now.getFullYear() + '-' + pad(now.getMonth() + 1) + '-' + pad(now.getDate());
        })()
        """;

    /// <summary>選ばれている日のバッジの数字（バッジが無ければ 0）。</summary>
    private const string SelectedDayBadge = """
        (function () {
          var badge = document.querySelector('#calendarGrid .calendar-day.selected .badge');
          return badge === null ? 0 : Number(badge.textContent);
        })()
        """;

    /// <summary>選ばれている日のセルが押せなくなっていること（その日に 1 件も無い）。</summary>
    private const string SelectedDayIsDisabled = """
        (function () {
          var day = document.querySelector('#calendarGrid .calendar-day.selected');
          return day !== null && day.disabled;
        })()
        """;

    /// <summary>
    /// 月のセルが 1 つ以上あって、そのすべてが押せないこと。
    /// <b>0 件を真にしない</b> ── 描画に失敗した月と「録画が無い月」は別である。
    /// </summary>
    private const string EveryDayIsDisabled = """
        (function () {
          var days = document.querySelectorAll('#calendarGrid .calendar-day');
          if (days.length === 0) { return false; }
          for (var i = 0; i < days.length; i++) { if (!days[i].disabled) { return false; } }
          return true;
        })()
        """;

    /// <summary>絞り込みの <c>&lt;select&gt;</c> に <paramref name="name"/> を入れて <c>change</c> を出す。</summary>
    private static string SelectRecorder(string name) => $$"""
        (function () {
          var select = document.getElementById('recordingsRecorder');
          select.value = '{{name}}';
          select.dispatchEvent(new Event('change'));
          return select.value === '{{name}}';
        })()
        """;

    /// <summary>設定に居る 2 台が絞り込みの選択肢に並んだか（先頭の「All recorders」を含めて 3 つ）。</summary>
    private const string RecorderOptionsAreReady =
        "document.getElementById('recordingsRecorder').options.length >= 3";

    /// <summary>
    /// 一覧を取り直しながら <paramref name="expression"/> が真になるまで待つ。
    /// <see cref="WaitForFirstRowAsync"/> と同じ理由で<b>押した直後には読まない</b>。
    /// </summary>
    private static async Task<bool> WaitWithRefreshAsync(EdgeCdp browser, string expression, TimeSpan budget)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < budget)
        {
            await browser.EvaluateBoolAsync(Click("loadRecordings"), Ct);
            if (await browser.WaitUntilAsync(expression, TimeSpan.FromSeconds(2), Ct))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 録画のページが<b>カレンダーで選ばれた 1 日ぶん</b>を出すこと ── 今日が選ばれ、
    /// その日のバッジが件数を持ち、行にサムネイル・開始理由・状態が並ぶこと。
    ///
    /// <para>
    /// <b>開始理由は開始した手段で決まる。</b> ここは HTTP から始めているので <c>remote</c>
    /// で、CLI から始めたものは <c>cli</c> になる（<see cref="RecordingDeliveryTests"/> の担当）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRecordingsPageShowsTheCalendarAndTheDaysRecordings()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, ListingSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        await RecordOverHttpAsync(client);
        await browser.EvaluateBoolAsync(GoToRecordings, Ct);

        // **Refresh を押す前に**行が出ること。ページを開いた時点の取得だけで
        // 一覧が埋まる形を固定する ── 先に押してしまうと、開いただけでは空のまま
        // （日が決まらず 0 行）でも気付けない。
        Assert.True(
            await browser.WaitUntilAsync($"1 <= {RowCount}", AutoRefreshBudget, Ct),
            $"Refresh を押さずに行が出ませんでした（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）: "
                + instance.DiagnosticDump());

        // Trigger 欄が埋まるのは sidecar が索引へ届いてから ── そこまで待てば、
        // 同じ行の他の欄（尺・状態・サムネイル）も確定した値になっている。
        Assert.True(
            await WaitWithRefreshAsync(browser, $"{FirstRowCell(5)} === 'remote'", SidecarBudget),
            "確定した録画の Trigger が remote になりませんでした: "
                + await browser.EvaluateStringAsync(FirstRowCell(5), Ct)
                + Environment.NewLine + instance.DiagnosticDump());

        Assert.Equal(
            "Recording|Size|Started|State|Duration|Trigger|",
            await browser.EvaluateStringAsync(TableHeadings, Ct));

        // カレンダー: 今月が出ていて、今日が選ばれ、その日の件数がバッジに出ていること。
        string month = await browser.EvaluateStringAsync(TextOf("calendarMonth"), Ct);
        output.WriteLine("calendar month: " + month);
        Assert.Contains(DateTime.Now.Year.ToString(CultureInfo.InvariantCulture), month, StringComparison.Ordinal);
        Assert.True(await browser.EvaluateBoolAsync(SelectedDayIsToday, Ct),
            "今日が選ばれていません（選択日: " + await browser.EvaluateStringAsync(
                "(function () { var d = document.querySelector('#calendarGrid .calendar-day.selected');"
                + " return d === null ? '(none)' : d.dataset.date; })()", Ct) + "）。");
        Assert.True(1 <= await browser.EvaluateNumberAsync(SelectedDayBadge, Ct), "今日のバッジに件数が出ていません。");

        Assert.True(1 <= await browser.EvaluateNumberAsync(RowCount, Ct), "選んだ日の行がありません。");

        // **サムネイルは sidecar とは別の道で来る**ので、Trigger が載ったことをもって
        // PNG も在るとは言えない（撮るのはプレビュー枝、書くのはスレッドプール ──
        // 排出の完了より早いことも遅いこともある）。ここは別に待つ。
        Assert.True(
            await WaitWithRefreshAsync(
                browser, $"{FirstRowThumbnailSrc}.indexOf('/api/recording-thumbnails/') >= 0", SidecarBudget),
            "1 行目にサムネイルが出ませんでした（src: "
                + await browser.EvaluateStringAsync(FirstRowThumbnailSrc, Ct) + "）。");

        // 状態欄は今までどおりの文字列のまま。**空は期待しない** ── 既定は fragmented
        // なので、録画が終わっても `fragmented` は残る。
        string state = await browser.EvaluateStringAsync(FirstRowState, Ct);
        Assert.DoesNotContain("recording", state, StringComparison.Ordinal);
        Assert.Contains("fragmented", state, StringComparison.Ordinal);
    }

    /// <summary>
    /// レコーダーの絞り込みと月の移動が、<b>一覧とカレンダーの両方</b>を狭めること。
    ///
    /// <para>
    /// 月を動かしても<b>行は残る</b>のが要点である ── 選んだ日は選んだままで、
    /// 別の月を見ているあいだはその日に強調が付かないだけである。
    /// </para>
    /// <para>
    /// <b>最後に録画中の行へも絞り込みを掛ける。</b> sidecar がまだ無い行のレコーダー名は
    /// ファイル名からしか決まらないので、ここだけ製品既定のテンプレートで走らせる
    /// （<see cref="ProductTemplateListingSettings"/>）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRecorderFilterAndTheMonthNavigationNarrowTheList()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(
            app, ProductTemplateListingSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        await RecordOverHttpAsync(client);
        await browser.EvaluateBoolAsync(GoToRecordings, Ct);

        Assert.True(await WaitWithRefreshAsync(browser, $"{RowCount} === 1", SidecarBudget),
            $"録画が一覧に 1 行として出ませんでした（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）: "
                + instance.DiagnosticDump());

        // 選択肢は SSE の state から埋まるので、値を書く前に並んだことを確かめる。
        Assert.True(await browser.WaitUntilAsync(RecorderOptionsAreReady, PageBudget, Ct),
            "絞り込みにレコーダーが並びませんでした。");

        string original = await browser.EvaluateStringAsync(TextOf("calendarMonth"), Ct);

        // ---- 録画していないレコーダーへ絞る ----

        Assert.True(await browser.EvaluateBoolAsync(SelectRecorder(UnusedRecorder), Ct), "絞り込みを変えられません。");
        Assert.True(await browser.WaitUntilAsync($"{RowCount} === 0", PageBudget, Ct),
            $"使っていないレコーダーで絞っても行が残っています（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）。");
        Assert.False(await browser.EvaluateBoolAsync(IsAttributeHidden("recordingsEmpty"), Ct),
            "行が無いのに「無い」と言っていません。");
        Assert.True(await browser.EvaluateBoolAsync(SelectedDayIsDisabled, Ct),
            "カレンダーが別のレコーダーの録画の日を数えたままです。");

        // ---- 全部へ戻す ----

        Assert.True(await browser.EvaluateBoolAsync(SelectRecorder(string.Empty), Ct), "絞り込みを戻せません。");
        Assert.True(await browser.WaitUntilAsync($"{RowCount} === 1", PageBudget, Ct),
            $"絞り込みを戻しても行が戻りません（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）。");
        Assert.True(await browser.EvaluateBoolAsync(IsAttributeHidden("recordingsEmpty"), Ct),
            "行が在るのに「無い」と言っています。");

        // ---- 前の月へ（選んだ日はそのまま） ----

        await browser.EvaluateBoolAsync(Click("calendarPrev"), Ct);
        Assert.True(
            await browser.WaitUntilAsync(
                $"{TextOf("calendarMonth")} !== {JsonSerializer.Serialize(original)} && {EveryDayIsDisabled}",
                PageBudget, Ct),
            "前の月へ移りませんでした（" + await browser.EvaluateStringAsync(TextOf("calendarMonth"), Ct) + "）。");
        Assert.Equal(1, await browser.EvaluateNumberAsync(RowCount, Ct));

        // ---- 元の月へ ----

        await browser.EvaluateBoolAsync(Click("calendarNext"), Ct);
        Assert.True(
            await browser.WaitUntilAsync(
                $"{TextOf("calendarMonth")} === {JsonSerializer.Serialize(original)}", PageBudget, Ct),
            "元の月へ戻りませんでした（" + await browser.EvaluateStringAsync(TextOf("calendarMonth"), Ct) + "）。");
        Assert.True(await browser.EvaluateBoolAsync(SelectedDayIsToday, Ct), "戻った月で今日の強調が消えています。");

        // ---- 録画中の行にも絞り込みが効くこと ----
        //
        // sidecar はまだ書かれていないので、この行のレコーダー名はファイル名からしか
        // 決まらない（製品既定のテンプレートで走らせている理由）。

        await PostAsync(client, $"api/recorders/{UsedRecorder}/start");
        Assert.True(await WaitWithRefreshAsync(browser, $"{RowCount} === 2", SidecarBudget),
            $"録画中の行が出ませんでした（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）: "
                + instance.DiagnosticDump());
        Assert.Contains("recording", await browser.EvaluateStringAsync(FirstRowState, Ct), StringComparison.Ordinal);

        Assert.True(await browser.EvaluateBoolAsync(SelectRecorder(UnusedRecorder), Ct), "絞り込みを変えられません。");
        Assert.True(await browser.WaitUntilAsync($"{RowCount} === 0", PageBudget, Ct),
            $"録画中の行が別のレコーダーの絞り込みに残っています（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）。");

        Assert.True(await browser.EvaluateBoolAsync(SelectRecorder(UsedRecorder), Ct), "絞り込みを変えられません。");
        Assert.True(await browser.WaitUntilAsync($"{RowCount} === 2", PageBudget, Ct),
            $"録画中の行が自分のレコーダーの絞り込みから落ちています（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）: "
                + instance.DiagnosticDump());

        await PostAsync(client, $"api/recorders/{UsedRecorder}/stop");
    }

    /// <summary>
    /// <b>sidecar が届いたことで</b>一覧が自動で描き直されること ── SSE の
    /// <c>completed</c>／<c>updated</c>（索引の 500ms デバウンスで 1 回に纏まり得る）が
    /// 「取り直す合図」として実際に配線されていること。
    ///
    /// <para>
    /// <b>Refresh を押さない。</b> 行そのものは録画を始めた時点（<c>added</c>）で出るので、
    /// ここで見るのは<b>その後に埋まる欄</b>である ── <c>Trigger</c> は sidecar からしか
    /// 来ないので、録画中は空欄で、確定してから <c>remote</c> になる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheListFillsInTheTriggerWithoutARefreshWhenTheSidecarArrives()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, ListingSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        await browser.EvaluateBoolAsync(GoToRecordings, Ct);
        await PostAsync(client, $"api/recorders/{UsedRecorder}/start");

        // 行が出るところまでは押してよい（見たいのはこの先である）。
        Assert.True(await WaitWithRefreshAsync(browser, $"{RowCount} === 1", SidecarBudget),
            $"録画中の行が出ませんでした（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）: "
                + instance.DiagnosticDump());
        Assert.NotEqual("remote", await browser.EvaluateStringAsync(FirstRowCell(5), Ct));

        // ---- ここから先はボタンを押さない ----

        await Task.Delay(RecordingWindow, Ct);
        await PostAsync(client, $"api/recorders/{UsedRecorder}/stop");

        Assert.True(
            await browser.WaitUntilAsync($"{FirstRowCell(5)} === 'remote'", SidecarBudget, Ct),
            "sidecar が届いても Trigger 欄が自動で埋まりませんでした（"
                + await browser.EvaluateStringAsync(FirstRowCell(5), Ct) + "）: "
                + instance.DiagnosticDump());
    }

    /// <summary>
    /// <b>Refresh を押さずに</b>一覧が追いつくこと ── SSE の <c>recording</c> が
    /// 「取り直す合図」として実際に配線されていること。
    ///
    /// <para>
    /// ここでボタンを押してはいけない。押すと、自動で取り直しているのか
    /// 押したから取り直したのかを見分けられなくなる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheListRefreshesItselfWhenARecordingIsAdded()
    {
        Assert.SkipUnless(EdgeCdp.IsAvailable, EdgeCdp.UnavailableReason);

        using var instance = AppInstance.Create(app, ListingSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        await WaitForMainSectionsAsync(browser);

        await RecordOverHttpAsync(client);
        await browser.EvaluateBoolAsync(GoToRecordings, Ct);

        Assert.True(await WaitWithRefreshAsync(browser, $"{RowCount} === 1", SidecarBudget),
            $"1 本目が一覧に出ませんでした（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）: "
                + instance.DiagnosticDump());

        // ---- ここから先はボタンを押さない ----

        await PostAsync(client, $"api/recorders/{UsedRecorder}/start");
        Assert.True(await browser.WaitUntilAsync($"{RowCount} === 2", AutoRefreshBudget, Ct),
            $"2 本目が自動で一覧に出ませんでした（{await browser.EvaluateNumberAsync(RowCount, Ct):F0} 行）: "
                + instance.DiagnosticDump());

        // 並びは開始時刻の降順なので、始めたばかりのものが 1 行目である。
        Assert.Contains("recording", await browser.EvaluateStringAsync(FirstRowState, Ct), StringComparison.Ordinal);

        await Task.Delay(RecordingWindow, Ct);
        await PostAsync(client, $"api/recorders/{UsedRecorder}/stop");

        Assert.True(
            await browser.WaitUntilAsync(
                $"(function () {{ var s = {FirstRowState}; return {RowIsFinishedFragment}; }})()",
                AutoRefreshBudget, Ct),
            "止めても一覧が自動で追いつきませんでした: " + await browser.EvaluateStringAsync(FirstRowState, Ct)
                + Environment.NewLine + instance.DiagnosticDump());
    }
}
