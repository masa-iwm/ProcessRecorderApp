# テストハーネスの設計原則

この文書は、E2E テスト基盤（`tests/ProcessRecorderApp.E2E`）のうち L2（E2E・CLI）と L3（GUI・UIA）の「外せない設計」、およびテストの有効性検証の原則を記す。いずれも実測と実際に踏んだ罠が根拠であり、ハーネスに手を入れるときはここに反する変更をしないこと。

## L2（E2E・CLI）基盤の外せない設計

`tests/ProcessRecorderApp.E2E` は製品側プロジェクトを1つも参照しない。発行済みの `ProcessRecorderApp.exe` を外からプロセス起動し、CLI の終了コード・標準出力・生成された MP4・`activity.log` だけを見る。環境変数名や終了コードの値がテスト側にも定数として書いてあるのは重複ではなく「外から見た契約」の表明で、製品側の値は L1（`AppEnvironmentTests` / `CommandOutcomeTests`）が固定している。

| 部品 | 役割 |
|---|---|
| `PublishedApp` | 発行物の解決（既定は selfcontained の発行先、`PROCESSRECORDERAPP_E2E_PUBLISH_DIR` で上書き）と GStreamer レジストリのウォームアップ |
| `AppInstance` | 一時データディレクトリ＋固有キー接頭辞での隔離、常駐ワーカーの起動と後始末、CLI の実行 |
| `SettingsFile` / `RecorderSpec` | テスト用 `settings.json` の組み立て |
| `Mp4File` | ISO-BMFF を直接読む妥当性検査（`ftyp`/`moov`/`mdat`/`avcC` と、尺・サンプル数・先頭サンプルの同期性。chunked は `mvhd`/`stsz`/`stss`、fragmented は `moof` の `trun` から読む）と共有違反の検出 |
| `GstLaunchTool` | `gst-launch-1.0` で「答えの分かった入力」を作る（ロードした GStreamer の場所は `activity.log` の `gst.runtime` から辿る） |
| `ActivityLogFile` | `activity.log` の行を既知イベント名の最長一致で切り出す |

設計上、外せない点が5つある:

- **ウォームアップは必須。** 常駐ワーカーの初回起動は GStreamer のレジストリ構築で 10 秒を超える（実測 10〜30 秒）。ランチャーのコールドスタート経路はこれを吸収する長さを持つ（登録完了待ちは `SingleInstanceManager.Launcher.cs` の `StartResidentWorkerAndWaitForRegistration` で 120 秒。待ち切れなければ終了コード 1 ＝ `ExitCode_WorkerStartFailed`。終了コード 2 は結果通知待ちのタイムアウト、3 はワーカー既存時の直接起動で、この待ちとは別の経路）ので製品側は落ちないが、温めずに走ると最初にワーカーを起こしたテストが構築の 10〜30 秒を自分の予算の中で払い、タイミングを前提にした表明が歪む。そこで `PublishedApp` が使い捨てのインスタンスで1回温め、以降のテストが恩恵を受ける（起動直後の最初の `ping` が1回で通ることを見るテストは、レジストリが温まっていることを偽陽性の見積もりの前提にしている）。ワーカーは `--__resident-worker` で自前の子プロセスとして直接起動し、短い `ping`（1回 15 秒）を繰り返して待つ（全体の締め切り 180 秒。`StartWorkerAndWaitUntilReady`）── 直接起動なら後始末で確実に落とせ、標準エラーへ複写される activity.log も診断用に捕捉できる。1回の ping を長くして回数を減らさないこと ── ここは回数で拾う仕組みで、長くするとかえって弱くなる。
- **隔離が効いていることを先に確かめる。** `PROCESSRECORDERAPP_DATA_DIR` が効いていないと、テストは開発者の本物の `%LOCALAPPDATA%\ProcessRecorderApp` を書き換える。`AppInstance` は `ping` 成功後に一時ディレクトリの `activity.log` の実在を確認し、無ければそこで失敗させる（黙って本番データを相手に走らせない）。
- **後始末で常駐ワーカーを必ず落とす。** 残ると次の `dotnet publish` が MSB3027（発行先の DLL がロック）で落ち、1件の不安定なテストがビルド不能に化ける。自分が起動した子プロセスだけでなく、ランチャーが暗黙に起動したワーカー（コールドスタート経路。子ではない）も `activity.log` の `app.start pid=` から拾って落とす。
- **アセンブリ全体で直列化する**（`CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)`）。並列だと、同じキー接頭辞ならコマンドが他方のワーカーへ転送され、別の接頭辞なら複数本の GStreamer が CPU を奪い合って録画の尺が揺れる ── どちらも「製品の不具合」に見える形で落ちる。
- **イベント名の照合を前方一致で書かない。** 製品は成功と失敗をイベント名で分けている（`recording.stop` と `recording.stop timeout`、`recorder.init ok` と `recorder.init fail`）。前方一致だと「成功したか」を見るつもりの照合が失敗行にも一致する。`ActivityLogFile` は既知イベント名の表（`KnownEvents`）を持ち、最長一致で切り出す。

デバッグ時は `PROCESSRECORDERAPP_E2E_KEEP=1` で一時ディレクトリが残る（`settings.json` / `activity.log` / `debug.log` / 生成 MP4 をそのまま調べられる）。

L2 の主な対象範囲:

| テストクラス | 対象 |
|---|---|
| `SmokeTests` | ハーネス自身の健全性（`ping`・隔離が効いていること・レコーダーの初期化） |
| `RuntimeResolutionTests` | GStreamer がどこからロードされたか（`gst.runtime`）。解決先が1つに決まり（`selected=` はバインディングの `GstInstallOrigin` の名前）、選んだディレクトリから実際にロードされ、本体と glib が同じ根から来ていること（`mixed=False`）。**ディレクトリを固定しない最後の段だけは照合を免除する**（`dir=(search-path)` ── ベアネームで OS のローダーに任せた場合）。特定のディレクトリは焼き込まない ── 正解は環境ごとに違う（開発機は MinGW インストール／CI は MSYS2／同梱リリースは `runtimes/`） |
| `OutputDirectoryTests` | 保存先（相対テンプレートが `OutputDirectory` の下に出ること・発行ディレクトリに漏れないこと）と古い mp4 の自動削除 |
| `PreBufferTests` | 事前バッファ（録画ボタンを押す前の映像が残ること）。アプリの中核契約 |
| `CliContractTests` | 終了コードの契約・`--set` `--get` の往復・`activate` でウィンドウが出ること |
| `RecordingTests` | 複数同時録画と MP4 の妥当性・コールドスタート・ファイル名テンプレートの展開・優先エンコーダー不在時のフォールスルー |
| `StopSynchronicityTests` | 停止の同期性（CLI 復帰直後にファイルが閉じていること・二重停止が弾かれること） |
| `StopOutcomeTests` | 停止が「使える成果物」を残したかを終了コードで区別すること（空の MP4 は専用の終了コードになる） |
| `ErrorReportingTests` | 書けない出力先での `recorder.error` / `recording.aborted`・ソース障害で復帰予約が積まれないこと |
| `PersistenceTests` | `--persist` した変数の永続化・明示していない変数はディスクに出ないこと・設定プロパティが保存で落ちないこと・`activity.log` の実ローテーション |
| `ResidentWorkerTests` | 常駐ワーカーの多重起動が弾かれること・コマンドを跨いでエンジンが作り直されないこと・ワーカー起動直後の最初のコマンドが1回で通ること（下記） |
| `StatusCommandTests` | `status` サブコマンド（録画の開始/停止が列に映ること・不健全なレコーダーで専用の終了コードと対象名が標準エラーに出ること） |
| `EncoderNegotiationTests` | ソースの画素形式がエンコーダーの受け付ける形式と違っても録画できること |
| `HighResolutionTests` | 高解像度でもパイプラインが動き出すこと（プレビュー枝の queue 満杯によるデッドロックの回帰） |
| `ThumbnailLayoutTests` | パディングの入った buffer から歪んでいないサムネイルが撮れること（`D3d12` 経路。`thumbnail.written source=meta` と画素の両方を見る。`thumbnail.*` は `activity.log` ではなく `myapp` カテゴリ＝`DebugLogFile` に出る） |

`ResidentWorkerTests` の「エンジンが作り直されないこと」を、エンジンがページ寿命に依存しないことの回帰テストと読まないこと（その退行が観測できない理由は L3 の節の末尾を参照）。

「起動直後の最初のコマンドが1回で通ること」（`TheFirstCommandAfterTheWorkerStarts_SucceedsOnTheFirstAttempt`）は実バグの回帰テスト。ワーカーはインスタンスキーの登録から `Activated` の購読までの間に GStreamer の初期化を挟むため、この窓に届いたリダイレクトは購読者が居らず痕跡ゼロで捨てられ、ランチャーは結果通知を待ち切って終了コード 2 を返す（実測: ワーカー直接起動直後の `ping` が 4/4 とも 60.5 秒・終了コード 2、activity.log にその `cli` 行が一行も残らない ＝ 利用者から見て「起動直後のコマンドが黙って失われる」）。現在はランチャー側の受理待ち（`WaitUntilWorkerAcceptsCommands`）がこの窓を塞いでいる。「`ping` が成功すること」だけではこの退行は検出できない ── ハーネスは `ping` を繰り返すので、取りこぼしても後続の試行が必ず拾ってしまい、スイートは緑のまま所要時間だけが数倍に伸びる。だから「何回の試行で通ったか」を直接表明する。

**AOT 発行物に対しても同じスイートを流す。** AOT 固有のリフレクション欠落は L1 では見つからない。危険域は PropertyGrid のプロパティ列挙（リフレクション）と設定 JSON のソース生成で、前者は L3 の編集系、後者は `PersistenceTests` が実際に往復させる。`PROCESSRECORDERAPP_E2E_PUBLISH_DIR` で発行ディレクトリを差し替えるだけでよい:

```powershell
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src\ProcessRecorderApp -c Release /p:PublishProfile=win-x64-aot
$env:PROCESSRECORDERAPP_E2E_PUBLISH_DIR = "<repo>\src\ProcessRecorderApp\bin\Release\win-x64\publish\aot"
dotnet test tests\ProcessRecorderApp.E2E -c Release
```

AOT 発行物はランチャーの起動もコマンド往復も速く、同じ設定でも実効の録画窓が短くなる。タイミングを前提にした表明は AOT で先に破れる。

**事前バッファの判定は絶対値ではなく差分で行う。** 事前バッファ有り（3000ms）と無し（0ms）を同じ条件で録り、尺の差が 1.5 秒以上あることを表明する（遡れるのはリングバッファ内の最も古い I フレームまで＝2.0〜3.0 秒ぶん）。絶対値だけを見ると、エンコーダーの立ち上がりや GOP の位相を吸収するために下限を緩めざるを得ず、退行注入の結果（＝事前バッファが丸ごと消えた尺）と重なってしまう。GPU 無しの開発機での実測では、絶対値が実行ごとに 1 秒近く動くのに差は 2.667 秒で安定し、事前バッファを消す注入後の差は 0.267 秒 ── 揺れているのは CLI の往復ぶん（録画窓そのもの）で、事前バッファの寄与ではない。絶対値の下限は、対照側が壊れて短くなった場合に差分判定が緩むのを防ぐ補助としてだけ置く。

## L3（GUI・UIA）基盤の外せない設計

`FlaUI.UIA3`（純 .NET の UIA3 ラッパー。別サーバープロセスが要らない）を使い、L2 の `AppInstance` の上に `AppUi` を重ねる。`activate` でウィンドウを出し、`activity.log` の `app.start pid=` から常駐ワーカーの pid を取って UIA で掴む。

設計上、外せない点が4つある:

- **`logListView` と `logTerminalView` の UIA サブツリーには絶対に降りない**（`AppUi.OpaqueSubtrees`）。Log 画面の ListView は `FindAllChildren()` が 25 秒待っても返らない（他の要素はすべて1桁ミリ秒で応答する）。標準の `FindFirstDescendant` はツリーを網羅するため、Log 画面を出している間は目的の要素が別の場所にあってもタイムアウトする。そのため探索は自前の深さ優先 walk で行い、このサブツリーだけ展開しない。一度でも列挙を試みると、その後は UIA セッション全体が使えなくなる（後続の探索が全て失敗する）ので、診断目的でも触らないこと。原因は未特定 ── スクロールの `VerticalViewSize` は 100（＝全件が収まっている）を返すので、件数説とは整合しない。 `logTerminalView`（WebView2）は**ブラウザープロセス側に別の UIA ツリーを持ち込む**ので、観測してからではなく先回りで同じ扱いにしてある ── 後から足すと、原因が別の変更に誤って帰属される。なお `SearchTree` は AutomationId の一致判定を降下判定より先に行うので、**入れても要素自体は見つかる**（存在と有効性は表明できる）。
- **Preview は `previewNavItem` を押しても選択されない**（`SelectsOnInvoked="False"`）。選択されるのは配下の Recorder サブ項目なので、先に親を展開してから子を選ぶ（`AppUi.SelectRecorder`）。Recorder 項目は AutomationId が `RecorderNavItem` 共通で、個体は自動化名（＝レコーダー名）で見分ける ── 名前はユーザーデータで改名できるため、AutomationId には焼き込まない。
- **画面を切り替えたら、切り替わったことを先に表明する**（`AppUi.SwitchTo`）。`Visibility=Collapsed` のパネル配下は UIA ツリーに現れないので、切替に失敗したまま要素を探すと「要素が無い」という形で落ち、原因が製品なのか手順なのか区別が付かない。目印の AutomationId は Preview=`TogglePaneButton` / Log=`ClearLogButton` / Variables=`VariablesTable` / Settings=`GstDebug`。
- **TextBox への入力は Tab で確定させる**（`AppUi.SetPropertyText`）。WinUI の `TextBox.Text` は TwoWay バインドでも既定ではフォーカスを失うまでソースへ書き戻さない。入力しただけで確認すると「UI には出ているがモデルには届いていない」状態を緑と誤読する。PropertyGrid は **Enter でも確定できる**が、そちらは `PropertyGridView.ValueTextBox_KeyDown` からの明示的なコミットで**経路が別**なので、Enter を見たいテストは `AppUi.SetPropertyTextWithEnter` を使う ── `SetPropertyText` では Enter の配線が外れても落ちない。

これに加えて、L3 の待ち方には共通の規則がある:

- 固定 sleep のあとに1回だけ数える・読む形にしない。「条件が満たされるまでの有界待ち」で書く（`WaitForPropertyText` / `WaitForRecorderNavItemName` 等）。期待値に達したら即座に返るので遅くならない。
- 待っている間に状態を作り直す操作が要るなら、それも待ちの中に入れる。ナビのサブメニュー（フライアウト）は、開いたあとに閉じることも、開く操作自体が効かないこともあり、どちらにも「見えていなければ開き直す」しか効かない（`SubMenuOpenBudget`＝8秒）。逆に、見えているときに `Expand()` を呼ばない ── トグルとして働けば、こちらが閉じる側の原因になる。
- 「出切った」を判定する安定窓は、事象の間隔より十分広く取る。エンコーダー候補の失敗は連続する事象の最大間隔が実測 1.72 秒あるため、窓は 8 秒・全体の上限は 90 秒（`PipelineDialogTests`）。「n 件出るまで待つ」形にはしない ── 候補の件数を焼き込むことになる。
- 単独実行で緑でも、フルスイートに混ぜてからが本番。前のケースが失敗して残した状態（開いたままのフライアウト等）で初めて出る欠陥がある。タイミング依存の失敗は「再実行したら通った」で済ませず、原因を特定して待ち条件を直すまでを対応とする。

L3 の主な対象範囲。各ケースは「これを落とす退行」を先に決めてから表明を書く（決められないものはゲートではなく不変条件の表明なので、そう明記する）:

| テストクラス | 対象 |
|---|---|
| `GuiSmokeTests` | ハーネスの健全性（ウィンドウが出ること・4画面すべてが切り替わること）。ここが緑にならないうちは、以降の赤を製品の不具合と読まない |
| `PropertyEditingTests` | `FilenameTemplate` の編集がモデルまで届くこと・`BufferDuration` のクランプが画面に返ること・レコーダー名の一意化が UI・ナビ・CLI まで通ること |
| `RecorderManagementTests` | レコーダーの追加→改名→削除の一巡・削除確認ダイアログのキャンセルで消えないこと・追加した名前が既存と衝突しないこと |
| `TemplateVariablePersistenceUiTests` | Variables 画面の「保存」列。チェックボックスから `AppSettings` までの結線は GUI からしか触れない |
| `PipelineDialogTests` | SrcPipeline 編集支援ダイアログ（OK で反映・キャンセルで不変）・不正なパイプラインでクラッシュしないこと・失敗の段の表明（`AssertFailedAtStateChange`。パース時ではなく状態遷移時に落ちていること。段を取り違えるとテストは緑でも意図した経路を一度も通らない） |
| `LanguageMatrixTests` | 表示言語の強制マトリクス（ja-JP / en-US / de-DE。de-DE は en-US へフォールバック）・PropertyGrid のカテゴリ見出し・CLI のメッセージ |
| `TrayMenuTests`（`Category=Fragile`） | トレイメニューの文言のローカライズと「表示」「終了」の経路。ウィンドウの外にある唯一の L3 で、赤の理由はシェル側にあるため CI の必須ゲートには入れない（`--filter "Category!=Fragile"`。トレイト式が1件も選ばなくても `dotnet test` は成功で終わるので、式が実際に何件選ぶかは確認してから使う） |
| `EncoderChoiceUiTests` | 優先エンコーダーの選択肢（表示名≠保存値でも保存されるのは値の方であること・一覧に無い値を持って開いても失わないこと）。属性をリフレクションで読む経路が発行物で生きていることの検査を兼ねる |
| `PreviewPlaceholderTests` | 未初期化のレコーダーを選んだときに前のレコーダーの映像が残らないこと。プレビュー面とプレースホルダーの両方向を見る ── 片方だけでは「常に片側」の実装が通る |
| `ShutdownTests` | Ctrl+閉じる での正常終了・録画中の終了・Ctrl なしの閉じるはトレイ格納。`app.exit exitCode=0` だけを見ない ── 未処理例外が握り潰されてもプロセスは 0 で終わるので、`app.error` が無いことと、保存された `settings.json` の中身（レコーダー名の配列）までセットで表明する |

**言語強制のマトリクスは、ホストの表示言語で「効く行」が入れ替わる。** 言語強制が全く効いていなくても、ホストの表示言語と一致する行は緑のまま通る（アプリは何もしなくてもその言語で表示するため）:

| ホスト | 効く行 | 何も検証していない行 |
|---|---|---|
| 表示言語 ja の開発機 | en-US / de-DE | ja-JP |
| GitHub ランナー（en-US） | ja-JP | en-US / de-DE |

したがって3言語すべてを回すことに意味があり、1言語に減らしてはいけない。期待値は `UiResources` がリポジトリの `.resw` から読む ── テストソースに訳文を焼き込むと、翻訳を直しただけで赤になるうえ、「resw の値が画面まで届いているか」という本来の主張からずれる。判定に日本語リテラルを使うのは UIA の Name（プロセス内の文字列）だけに限る。CLI の出力は「en-US の ASCII 断片が在るか無いか」だけで判定する ── 標準エラーのバイト列はコンソールのコードページに依存し、UTF-8 として読むと化ける。「非 ASCII が含まれること」も判定に使わない ── cp437/1252 のランナーでは符号化できない文字が `?`（0x3F）に潰れ、親側でどうデコードしても復元できないため、ランナーでだけ偽の赤が出る。

**トレイアイコンの位置は `Shell_NotifyIconGetRect` では取れない。** 「アイコンの矩形をシェルに直接聞けば、名前の一致も他アプリのアイコンの試行も要らない」に見えるが、プロセスの外からは1件も返らない（実測: 対象プロセスのトップレベルウィンドウ全部 × uID 0〜63 で全滅）。WinUIEx のトレイアイコンの所有ウィンドウが `EnumWindows` に出ないメッセージ専用ウィンドウのためと見られる。通知領域は UIA で辿るしかない（`TrayUi`）── この API を再試行しないこと。

**エンジンがページ寿命に依存しないことの退行は、L3 でも観測できない。** `MainWindow` は起動時に `MainPage` へ1回だけ遷移し、以後別ページへ遷移しない。4画面は `MainPage` 内の `Visibility` バインドのパネルであってページ遷移ではなく、トレイ格納もページを Unload しない。したがってページの Unload に紐づく破棄はウィンドウ破棄（＝プロセス終了）でしか走らず、「画面を切り替えて戻る間も録画が途切れないこと」を見るテストは退行の有無にかかわらず同じ結果になる ── 何も検出しない緑のテストにしかならないので、書かない。

## テストの有効性検証の原則

「テストが通った」ではなく「テストが退行を検出できる」ことまでを確認する。何も検出しないテストは、無いよりも有害（安心して壊せてしまう）。

手順は「退行を注入 → 対象テストが確実に落ちることを確認 → revert」。注入は使い捨てブランチか `git stash` 上でのみ行い、絶対にコミットしない。

- 「この注入はこのテストを落とす」という予測は、実行するまで当てにならない。予測したテストとは別のテストが、別の理由で落ちることがある。落ちた場所と理由まで確認して初めて「検出できる」と言える。
- ソースを文字列として検査するテストは、必ずコメント行を除外し（`SourceReferences.IsCommentLine` を直接呼ぶか、内部でそれを使う `SourceMethodBody.IndexOfCode` / `ContainsCode` を経由する）、注入で1回落としてから完成とみなす。素の `IndexOf` / `Regex` は、そのコードを説明しているコメント自身に一致する ── コメントは当然その識別子を含むので、構造的にほぼ必ず起こる。該当するテストは固定の一覧では管理せず、`SourceReferences.IsCommentLine` と `SourceMethodBody.` の呼び出し元を grep で洗い出すのを正とする（現在は直接呼ぶ `AppSettingsReloadTests` / `DashStopReasonTests` / `DocumentationDriftTests` / `EncoderCatalogScriptSyncTests` / `PlayingStateBudgetTests` と、`SourceMethodBody` 経由の `WorkerAcceptingEventOrderTests` / `ShutdownRedirectHandlingTests` / `ContinuousRuntimeDependencyTests` / `LivePreviewPipelineTests`）。検査ヘルパーを共有化したときは、共有先すべての検出器の注入をやり直すこと（docs/coverage-gaps.md）。
- revert に `git checkout <file>` を使うときは、そのファイルの「注入以外の変更」が全部コミット済みかを先に確かめる。未コミットの本編集ごと消える。混在しているなら退避コピーから戻す。
