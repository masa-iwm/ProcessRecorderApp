using System.Diagnostics;
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
        Assert.True(await browser.WaitUntilAsync($"!{IsHidden("mainSections")}", PageBudget, Ct), "画面が出ませんでした。");

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);

        Assert.True(
            await WaitForFirstRowAsync(browser, RowIsRecordingFragment, PlaybackBudget),
            "録画中のファイルが fragmented として一覧に出ませんでした。");

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
        Assert.True(await browser.WaitUntilAsync($"!{IsHidden("mainSections")}", PageBudget, Ct), "画面が出ませんでした。");

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);

        Assert.True(
            await WaitForFirstRowAsync(browser, RowIsRecordingFragment, PlaybackBudget),
            "録画中のファイルが fragmented として一覧に出ませんでした。");

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
}
