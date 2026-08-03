# 自動では守られていないもの

ここに挙げるのは「テストが無い」箇所ではなく、**退行を注入しても既存のどのテスト層でも検出できないことが確認済み**の箇所の一覧である（テスト層の呼び分け: L1＝ユニット／ソース静的検査、L2＝発行物の CLI を回す E2E、L3＝UIA 経由の GUI 自動テスト、L4＝ドキュメント整合検査）。プロパティ編集・画面切替・トレイ格納・正常終了パス・レコーダーの追加/改名/削除・パイプライン編集ダイアログ・表示言語のマトリクスは自動で守られている（トレイメニューの文言は `TrayMenuTests` が守るが CI の必須ゲート外なので、下の一覧に載せている）。ここに載っている箇所を触るときは、push が緑でも退行していないとは言えないので、各項目に書いた対応する手動確認を行うこと。

## 一覧

### 録画エンジンの寿命（App 所有）

録画エンジン（`Controller`＋全 `EventRecorder`＋常時稼働 sink パイプライン）はプロセス寿命で `App` が所有し、ページはそれを受け取ってバインドするだけ、という構造の退行。2 件の注入をいずれもどの層も検出できない ── 現在のアーキテクチャではプロセス生存中にページ破棄が起きないため、所有関係を壊しても外から観測できる差が出ない。この構造（`App.xaml.cs` の起動時初期化、`MainPageViewModel` が破棄しないこと）を変えるときは、発行物に対する手動確認で録画の開始・停止が通ることを確かめること。

### MainPage_Unloaded のデリゲート解除

`MainPage_Unloaded` が行う解除（`ConfirmRecorderRemovalAsync = null` / `recorderPropertyGrid.ValueBuilder = null`）。エンジン寿命とまったく同じ理由で観測できない（解除を削除する注入を実施して未検出）。プロセス寿命のエンジンにページ寿命のデリゲートを残すと、ページ破棄後の削除コマンドが破棄済みビジュアルツリー上でダイアログを出そうとする ── 防御として正しいので残すが、**テストが担保していると数えないこと**。

### パイプライン編集ダイアログの「コミットしてから初期化」の順序

`BuildValueAsync`（`MainPage.xaml.cs`）は生成した `SrcPipeline` を先にレコーダーへ反映（コミット）し、その後 `OnInitialize()` を呼ぶ。代入を消す注入は検出できたが、落ちたのは別のテストの「失敗の段」の表明であって、**順序そのものを見ている表明は無い**。順序を入れ替える変更をするときは、ダイアログ経路で新しいパイプラインが実際に使われて初期化されることを手動で確認すること。

### Log 画面への表示経路

Log 画面の表示は 2 経路ある。**既定は WebView2 の中の xterm.js**（`logTerminalView`）で、WebView2 ランタイムが無い／初期化に失敗したときだけ従来の `ListView`（`logListView`）に落ちる。**どちらの経路も E2E からは中身を読めない。**

- `logListView` は UIA の子要素列挙に応答しない（`FindAllChildren()` が 25 秒待っても返らない）。
- `logTerminalView` は WebView2 で、**ブラウザープロセス側に別の UIA ツリーを持つ**。さらに既定の WebGL レンダラーでは文字が GPU テクスチャなので、**アクセシブルテキストが 1 つも出ない**（DOM レンダラーへ落ちても子ツリーであることは変わらない）。`screenReaderMode` を有効にしても戻るのは行単位の読み上げだけで、`ListView` が出していた「項目を列挙・選択できる」UIA テキストには戻らない。

どちらも `AppUi.OpaqueSubtrees` に入れて降りない。**要素の存在と有効性だけは表明できる** ── `AppUi.SearchTree` は AutomationId の一致判定を降下判定より先に行うため、`OpaqueSubtrees` に入っていても要素自体は見つかる（`GuiSmokeTests.TheLogScreen_ShowsTheTerminal_NotTheFallbackList` がそれを見る）。

したがって**「録画イベントがアプリ内の Log 画面に出ること」は L3 では検証できない**。`activity.log` への書き出しは L2（`PersistenceTests` ほか）が押さえているので、抜けているのは「アプリ内 Log 画面への表示経路」だけ。ここを触ったら目視で確認すること ── 発行物を起動して録画イベントを発生させ、Log 画面に行が増えることと、同じイベントが `activity.log` にも出ていることを突き合わせる。

**自動で押さえられるようになった分**: 有界化・改行境界での破棄・破棄行数の勘定・`ReadLine` 等価の行分割・CR の潰し方は L1（`LogBufferTests` / `LogLineSplittingTests`）が見る。「WebView2 が実際に起きて JS が走った」ことと採用レンダラーは `activity.log` の `log.terminal` を通して L3 が見る。**残るギャップは「端末に文字が描かれていること」そのもの**で、これは目視でしか確かめられない（レンダラーが WebGL か DOM かも画面からは見分けが付かない ── GPU 無し・RDP で SwiftShader に落ちても描画は成功する）。

### トレイメニューの文言のローカライズ（CI ゲート外）

`TrayMenuTests` がウィンドウの外のメニューを実際に開いて resw の値と照合するが、`Category=Fragile` で **CI の必須ゲート（`--filter "Category!=Fragile"`）からは外してある**。守られるのは手元でフィルタなしで回したときだけで、「push すれば守られる」とは読まないこと。除外の理由はシェル側（オーバーフローのトグル・物理カーソルでの右クリック）であって製品ではない。

### トレイアイコンのツールチップ

`MainWindow` のコンストラクタで `Title = AppTitleBar.Title` を `AttachWindow` より前に明示的に設定している。**この行を消しても自動テストでは落ちない** ── キャプションは XAML の TitleBar 読み込み時点で製品名に自己修正するため、外から観測できる差は「トレイアイコンのツールチップが WinUI 3 の既定値 `WinUI Desktop` のままになる」ことだけで、そこへ届くのは `Category=Fragile` の `TrayUi` 経路のみ（CI では走らない）。消す・動かすときは UIA で通知領域の項目名を読み、`Process Recorder App` になることを実測で確かめること ── 合成入力は不要で、通知領域（オーバーフローのトップレベルウィンドウを含む。シェル側のクラス名は E2E の `TrayUi.FindOverflowWindow()` がクラス名の部分一致 `"Overflow"` で拾う）を走査して名前を読むだけでよい。

### RedirectActivationTo の待ちの有界性

ランチャーの `RedirectActivationTo` は `RedirectActivationToAsync` を `Task.Wait` の上限 30 秒（`RedirectTimeoutMs = 30_000`）で待つ。`RedirectActivationToAsync` は WinAppSDK の API で**テストから投げさせることも止めることもできない**ため、回帰テストは書けない。上限を外すと、転送の失敗・停止の瞬間に呼び出し元が `launcherMutex` を握ったまま永久ブロックし、**同じ keyPrefix・同じセッションの CLI が全部、無言で止まる**（実利用では keyPrefix は 1 つなので操作手段が丸ごと失われる）。ここを触るときは `RedirectActivationToAsync` の呼び出し直前に永久に返らない待ち（`Task.Delay(Timeout.Infinite)`）を注入して `ping` で再実測し、**必ず「注入なしの対照」を一緒に測ること** ── 壊れ方が「正常系が STA のポンプ喪失でハングする」形なので、注入側だけ見ても気付けない。現行実装の実測: 注入側は終了コード 2・90,435ms（転送打ち切り 30 秒＋結果待ち 60 秒）、注入なしの対照は 418〜582ms・終了コード 0。

### ワーカー受理イベントの初期化順序（静的検査でのみ担保）

「単一インスタンスのキー登録に**負けた側**の早期 return が `_workerAcceptingEvent` の生成・リセットより**前**にある」という不変条件。負けた側がリセットすると生きているワーカーのシグナルを消し、CLI が毎回上限の 60 秒待たされる。実行で守るには「キー登録に負けるワーカー」を狙って作る必要があり、プロセス起動のレースそのものなので不可能 ── `WorkerAcceptingEventOrderTests`（L1）が**ソースをテキストとして**突き合わせて守る。この形の先例は `AppSettingsReloadTests`（`Reload()` の手書きコピー漏れを同じくソース照合で守る）で、新しい静的検出器は同じ形で書く。罠: 位置比較の `IndexOf` は**コメント行を除外すること** ── 直前のコメントが同じ語を含むため、素で書くと何も検出しないテストになる（注入で実際に踏んだ）。検査ヘルパーを共有化したときは、共有先すべての検出器の注入をやり直すこと。

### 終了処理中のコマンドへの即答（終了コード 5）

終了処理に入った常駐ワーカーが、届いたリダイレクトに `ExitCode_WorkerShuttingDown`（5）で即答する挙動（`OnActivationRedirected` で `_shuttingDown` が立っているか `TryEnqueue` が失敗したとき）。L4 が「終了コードが `src/README.md` の表にあること」「メッセージが両ロケールにあること」を、`ShutdownRedirectHandlingTests`（L1・ソース静的検査）が「2 つの終了経路が同じ入口を通ること」「旗を立てるのがキー解除より前であること」「終了経路が `Activated` の購読を外さないこと」（`Activated -=` を復活させると L1 が赤になる。5 で即答する経路を到達可能に保つ唯一の防護）を守っているが、**挙動そのものは守られていない** ── 終了経路は購読を外さないので、実際の窓は「ランチャーがキーの持ち主を見終わってから `_shuttingDown` が立つ（または DispatcherQueue が止まる）まで」に届くリダイレクトであり、narrow race で E2E から決定論的に踏ませられない。ここを触るときは、`TryEnqueue` が必ず false になる使い捨ての細工（コミットしないこと）を入れた発行物へ `ping` を打って再実測すること ── 期待は終了コード 5・約 0.7 秒で、退行すると 60 秒待って「成否不明」の 2 になる。

### 同梱ランタイムに対する録画系 E2E の大半

E2E ハーネスは `SettingsFile.DefaultEncoder = "x264enc"` を固定しており、配布 zip に同梱するランタイム一式には x264 が無い。`release.yml` のフィルタは `FullyQualifiedName~SmokeTests|FullyQualifiedName~RuntimeResolutionTests`（`~` は部分一致）なので、同梱物に対して流れるのは `SmokeTests`・`GuiSmokeTests`（L3 の GUI スモークもここで走る）・`RuntimeResolutionTests` の 3 クラスで、**事前バッファ・停止の同期性・ファイル名テンプレートなどの録画系 E2E は同梱物に対しては一度も流れない**。非同梱版と共通のコードなので「アプリの不具合」は `build.yml` で捕まる。捕まらないのは「その runtimes の組み合わせでしか出ない問題」。なお停止結果の規則そのものはランタイム非依存の L1（`RecordingStopRulesTests`）が押さえている。

### 言語強制マトリクスのうちホストの表示言語と重なる行

`LanguageMatrixTests` は `PROCESSRECORDERAPP_LANG` で ja-JP / en-US / de-DE の 3 言語を回すが、**ホストの表示言語と一致する行はそのホストでは何も検証していない**（強制してもしなくても同じ表示になるため。表示言語 ja の開発機では ja-JP の行が、en-US の GitHub ランナーでは en-US と de-DE の行が空回りする）。**CI が別の表示言語のホストで走ることで初めて全行が実際に効く。** 言語解決を触るときは、どのホストでどの行が効いているかを意識すること。検出が生きているかは、言語強制フック（`ApplyLanguageOverride`）を無効化する注入で「ホストの表示言語と異なる行だけが赤になる」ことで確かめられる。この非対称の根拠は `LanguageMatrixTests` の冒頭 doc にも書いてある。

### Close() の破棄済みフィールドの null 化

`EventRecorder.Close()` は破棄したフィールドを必ず null 化して冪等にしてある。`Initialize` は先頭で `Close()` を呼んでから各フィールドを再代入するため、初期化が途中で失敗すると catch 内の `Close()` が「破棄済みのまま残ったフィールド」を再度触る（パイプライン編集ダイアログに不正な文字列を入れると到達する）── null 化が無いとネイティブオブジェクトの二重解放になる。`PipelineDialogTests` は「ダイアログ経路がクラッシュしないこと」「`LastError` が出ること」を守るが、**null 化を外す注入は検出できなかった**（実施済み・未検出）。null 化を外さないこと。テストが担保していると数えないこと。

### RecorderNavViewBehavior の到達不能分岐（Reset / TryBuildMenu 再入時の解除）

`Add` と `Remove` は `RecorderManagementTests` が実際に通すが、`Reset` 分岐は `Recorders.Clear()` を呼ぶ経路が製品に無く、`TryBuildMenu` は構築済みなら早期 return するため、**どちらも到達経路が存在しない**。`AppSettings.Reload()`（呼び出し元が無いまま `AppSettingsReloadTests` がソース照合で守っている）と同じ扱いで、将来 `Clear()` が生えたときの受け皿として残す（Reset 時点でコレクションが空になりうるため、解除対象を取り違えないための影リストごと）。**到達経路を探すのに時間を使わないこと。**

### ナビ項目の購読解除

`Recorders_CollectionChanged` の Remove で解除対象を取り違える退行。`MainPage_Unloaded` の解除漏れとまったく同じ理由で観測できない ── 削除されたレコーダーを後から改名する経路が無いため、解除漏れの結果が外に出ない。注入を実施して未検出であることを確認済み。防御として残すが、テストが担保していると数えないこと。

### DefaultLanguage / NeutralLanguage の明示

`src/Directory.Build.props` の `<DefaultLanguage>en-US</DefaultLanguage>` / `<NeutralLanguage>en-US</NeutralLanguage>`。**どちらを外しても実行時の挙動は変わらない**（2 件とも注入して未検出）。ただし 2 行の性質は非対称で、`DefaultLanguage` は WinUI/MSIX ツーリングが既に en-US を既定値として与えているため明示は既定の再掲（発行物は不変）、`NeutralLanguage` は未設定（空）だったため明示は実際の追加（アセンブリに `NeutralResourcesLanguageAttribute` のメタデータが増える）── 実効値は `dotnet msbuild -getProperty:` で確認でき、どちらかを消してよいか判断するときはこの区別が唯一の材料になる。書いてあるのは「暗黙の既定に依存しない」という宣言であって、退行検出器があるからではない ── **ツーリングの既定が変わったときは静かに壊れる。** 消さないこと。

### UIA トリガの実発火経路（手動確認のみ）

「別アプリの UI が実際に変化 → `TriggerFired` → 変数反映・録画開始/停止」という end-to-end は、相手アプリと UIA イベントのタイミングに依存するため、このリポジトリの E2E では流していない。実 UIA での監視・発火そのものは UiaTrigger リポジトリ側の実 UIA テスト（RealUia.Tests）が担保しており、アプリ側で守るべき「発火 1 回を変数とアクションへ写す規則」は L1（`TriggerFiringRulesTests` / `TriggerAssignmentReconcilerTests`）が守る。**守られていないのはその間の配線**（`UiaTriggerService` の購読・TryEnqueue・Can* ガード・`MainPage` のエディタ起動）と、**`UiaTriggerService` だけが持つ状態**（世代 `_monitorEpoch` による退役モニタの発火の排除、`_autoStarted` の追跡と自動停止）── これらは WinUI アプリのプロジェクトにあるため L1 から参照できない。

ここを触ったら発行物で手動確認すること。相手はメモ帳でよく、**タイトル文字列ではなく要素の出現／消滅**を監視対象にする（この開発機では `SetWindowText` が UIA イベントにならない）── `Ctrl+S` で「名前を付けて保存」ダイアログを出し `Esc` で閉じるのが、副作用が無く手で確実に出し入れできる。トリガは `WhileMatching` ＋ `Always` 条件 ＋「停止時も通知」ON ＋ **`PollInterval` 1 秒**（未設定だと「立ち下がりの機能が壊れている」と見分けがつかない）。

1. 発火で `trigger.fire` が activity.log に出て、Variables 画面に `{トリガID}` が現れる
2. 割り当て（開始/終了）で録画が実際に動き `trigger.start` / `trigger.stop` が出る
3. 録画中の再発火が `trigger.action skip` になる
4. **割り当て「開始」のトリガが、立ち下がり（`edge=Falling`）では開始しない**（負の確認）
5. 割り当て「条件成立中のみ録画」で、条件成立で開始し不成立化で停止する
6. 「条件成立中のみ録画」で録画中に、有効スイッチを OFF にする／そのトリガだけを削除すると、
   `trigger.stop … reason=monitor-stop` で自動停止する

なお **「立ち下がりが届かない」ことそのものは検出できない**（イベントも poll も落ちた場合）。定義の不備（`WhileMatching` ＋「停止時も通知」になっていない）だけは `trigger.assign warn` が知らせる。将来 E2E 化するなら、相手アプリを別途起動せず**アプリ自身のウィンドウを監視対象にする**案が有力。

### デバッグ用パスのピッカー（「…」の先のダイアログ）

**この項目だけ性格が違う。** 他の項目は「退行を注入しても検出できなかった」という実測だが、
こちらは**そもそも自動で押せない**（下記）ので注入実験に到達していない。

`GstDebugDumpDotDir` / `DebugLogFile` / `OutputDirectory` の「…」が開く
`Microsoft.Windows.Storage.Pickers` のダイアログは E2E で押していない ── **押すとネイティブの
モーダルが開き、閉じるまでテストが止まる**。E2E が見ているのは
`GstDebugLiveTests.TheDebugPathRowsOfferABuilderButton`、すなわち
「`[ValueBuilder]` の配線が届いて Builder 行（＝「…」ボタン付き）になっていること」までである。

**守られていないのは `MainPage.BuildSettingsValueAsync` の分岐から先**
（`PickDirectoryAsync` / `PickLogFileAsync` の中身、`SuggestedStartFolder` の与え方、
取り消し時に現在値を保つこと）。

なお**ダイアログは UIA のデスクトップ直下の子として現れない** ── 開いたことを
「新しいウィンドウが増えた」で検出しようとすると、**開いているのに 0 件**という
紛らわしい結果になる（実測）。確かめたいときは製品側に一時的な `ActivityLog` を差し込み、
`PickSingleFolderAsync` / `PickSaveFileAsync` の**入りと出**を見るのが確実
（出が来なければモーダルが立っている。`Esc` で `result=null` が返る）。

ここを触ったら発行物で手動確認すること:

1. 3 つの行の「…」がそれぞれ**フォルダー選択**／**フォルダー選択**／**ファイル保存**を開く
2. 現在値が指す場所から開く（空欄・相対パスでも例外にならない）
3. 取り消すと値が変わらない
4. 選ぶと絶対パスが入り、直接入力の空欄・相対パスも従来どおり通る（`[ReadOnly]` にしていない）

### Mp4Probe.StartsOnASyncSample

`Mp4Probe.StartsOnASyncSample`（`stss` の先頭項目の検査）は**退行検出器ではなく不変条件の表明**である。これが緑であることを「この性質を壊す変更を検出できる」と読まないこと。
