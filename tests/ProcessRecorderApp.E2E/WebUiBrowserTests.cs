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
    /// <c>app.js</c> の <c>FOLLOW_TRIM_TRIGGER_SECONDS</c> の宣言。
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
        string script = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "src", "RemoteControl", "wwwroot", "app.js"));

        var declarations = TrimTriggerDeclarationRegex.Matches(script)
            .Where(m => !IsCommentLine(script, m.Index))
            .ToArray();

        Assert.True(declarations.Length == 1,
            $"app.js の FOLLOW_TRIM_TRIGGER_SECONDS が {declarations.Length} 件見つかりました"
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
    /// <c>activity.log</c> の <c>gst.runtime</c> が書いた <c>dir=</c>（実際にロードした
    /// GStreamer の <c>bin</c>）から <c>gst-launch-1.0.exe</c> を探す。
    /// <b>特定のディレクトリを焼き込まない</b>（開発機・CI・同梱で正解が違う）。
    /// ベアネームで解決した段（<c>dir=(search-path)</c>）には固定の場所が無いので null。
    /// </summary>
    private static string? FindGstLaunch(AppInstance instance)
    {
        foreach (string line in ActivityLogFile.Events(instance.ReadActivityLog(), "gst.runtime"))
        {
            // 値にはディレクトリ（空白を含みうる）が入るので、空白では切れない
            // ── 次のフィールド名の直前までで切る（RuntimeResolutionTests と同じ規則）。
            var match = Regex.Match(ActivityLogFile.DetailOf(line), @"\bdir=(.*?)(?=\s+\w[\w:.]*=|$)");
            if (!match.Success)
                continue;

            string directory = match.Groups[1].Value.Trim();
            if (directory == "(search-path)")
                continue;

            string launcher = Path.Combine(directory, "gst-launch-1.0.exe");
            if (File.Exists(launcher))
                return launcher;
        }

        return null;
    }

    /// <summary>launcher の隣のプラグイン ディレクトリ（<c>..\lib\gstreamer-1.0</c>）。</summary>
    private static string PluginDirectoryOf(string launcher)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(launcher)!, "..", "lib", "gstreamer-1.0"));

    /// <summary>
    /// <c>x264enc</c> のプラグインが launcher の隣にあるか。
    ///
    /// <para>
    /// <b><c>gst-launch-1.0.exe</c> があることは x264 があることを意味しない。</b>
    /// 同梱ランタイムが解決に勝つ機械では、launcher は在るのに GPL のプラグインだけが
    /// 無い ── クリップを書けずに「製品の欠陥」に見える形で落ちる。
    /// 名前は形態で変わる（MinGW は <c>lib</c> 接頭辞つき、MSVC は無し）。
    /// </para>
    /// </summary>
    private static bool HasX264Plugin(string launcher)
    {
        string plugins = PluginDirectoryOf(launcher);
        return File.Exists(Path.Combine(plugins, "libgstx264.dll"))
            || File.Exists(Path.Combine(plugins, "gstx264.dll"));
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
        var start = new ProcessStartInfo(launcher)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(launcher)!,
        };

        foreach (string argument in new[]
        {
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
        })
        {
            start.ArgumentList.Add(argument);
        }

        // 開発機には複数の GStreamer が同居しうる。launcher の隣の lib\gstreamer-1.0 を
        // 名指しして、レジストリのキャッシュも隔離する（システム側のものを書き換えない）。
        string pluginDir = PluginDirectoryOf(launcher);
        if (Directory.Exists(pluginDir))
        {
            start.Environment["GST_PLUGIN_PATH"] = pluginDir;
            start.Environment["GST_PLUGIN_SYSTEM_PATH"] = pluginDir;
            start.Environment["GST_PLUGIN_PATH_1_0"] = pluginDir;
            start.Environment["GST_PLUGIN_SYSTEM_PATH_1_0"] = pluginDir;
        }
        start.Environment["GST_REGISTRY"] = Path.Combine(instance.DataDir, "gst-registry-longclip.bin");

        var writing = Stopwatch.StartNew();
        using var process = Process.Start(start)!;

        // 両方を同時に汲む（片方だけを待つと、もう片方のパイプが埋まって止まる）。
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);

        using var kill = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        kill.CancelAfter(ClipBudget);
        try
        {
            await process.WaitForExitAsync(kill.Token);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"gst-launch-1.0 が {ClipBudget.TotalSeconds:F0} 秒で終わりませんでした。");
        }

        string tail = await stdout + Environment.NewLine + await stderr;
        Assert.True(process.ExitCode == 0, $"gst-launch-1.0 が {process.ExitCode} で終わりました:{Environment.NewLine}{tail}");
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

        string? launcher = FindGstLaunch(instance);
        Assert.SkipWhen(launcher is null,
            "ロードした GStreamer の bin に gst-launch-1.0.exe がありません"
            + "（gst.runtime の dir= を見て探しています）。");
        Assert.SkipUnless(HasX264Plugin(launcher!),
            $"ロードした GStreamer に x264 のプラグインがありません（{PluginDirectoryOf(launcher!)}）。");

        string clip = Path.Combine(instance.RecordingsDir, "long-clip.mp4");
        await WriteLongFragmentedClipAsync(launcher!, clip, instance);

        await using var browser = await EdgeCdp.LaunchAsync(Ct);
        await browser.NavigateAsync($"http://127.0.0.1:{port}/", PageBudget, Ct);
        Assert.True(await browser.WaitUntilAsync($"!{IsHidden("mainSections")}", PageBudget, Ct), "画面が出ませんでした。");

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
        Assert.True(await browser.WaitUntilAsync($"!{IsHidden("mainSections")}", PageBudget, Ct), "画面が出ませんでした。");

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
}
