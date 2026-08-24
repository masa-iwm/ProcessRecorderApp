# 自動では守られていないもの

ここに挙げるのは「テストが無い」箇所ではなく、**退行を注入しても既存のどのテスト層でも検出できないことが確認済み**の箇所の一覧である（テスト層の呼び分け: L1＝ユニット／ソース静的検査、L2＝発行物の CLI を回す E2E、L3＝UIA 経由の GUI 自動テスト、L4＝ドキュメント整合検査）。プロパティ編集・画面切替・トレイ格納・正常終了パス・レコーダーの追加/改名/削除・パイプライン編集ダイアログ・表示言語のマトリクスは自動で守られている（トレイメニューの文言は `TrayMenuTests` が守るが CI の必須ゲート外なので、下の一覧に載せている）。ここに載っている箇所を触るときは、push が緑でも退行していないとは言えないので、各項目に書いた対応する手動確認を行うこと。

## 一覧

### カメラ（`mfvideosrc`）のデバイス列挙

`GstIntrospect.GetVideoSourceDevices` が実際にデバイスを1台でも読めること。**どの層も検出できない**:

- L1 は GStreamer を初期化しない（`DebugThresholdTests` が「初期化前の挙動」を見ており、
  どれか1つのテストが初期化すると順序依存で壊れる）。
- 開発機と CI に**カメラが無い**。`gst_device_provider_get_devices` が空リストを返すので、
  1台ぶんの処理（表示名・caps の読み出しと解放）が1行も走らない。

実際にこれで2回落とした ── gir-core の `GList` 経路がヒープを壊す件（カメラのある機械でだけ
再現）と、`gst_device_provider_factory_get_by_name` の戻り値を factory と取り違えて
**選択肢が黙って空になる**件。どちらも緑のまま出荷された。

ここを触るときは、**カメラのある機械で SrcPipeline 編集画面を開き、`mfvideosrc` の
device-name / 解像度 / フレームレートの選択肢が実際に出ることを目で確かめる**こと。
`debug.log` の `video devices: count=` が件数を出すので、空なら 0 と分かる。

### `monitor-device-path` が実際にハンドルへ解決されて録れること

`MonitorSelection.Resolve` の規則そのものは L1（`MonitorSelectionTests`）が 5 通り全部を
縛っており、「一致しない → 初期化が失敗し理由にパスが出る」は L2
（`ErrorReportingTests`）が見る。**守られていないのは成功の脚**である:

- パスは機械ごとに違うので、**設定に書ける固定値が無い**（`monitor-index` と違い、
  E2E から「必ず在る値」を与えられない）。したがって「一致 → `monitor-handle=…` を
  渡して実際に PLAYING まで行く」経路は、どの層でも 1 度も走らない。
- `GstIntrospect.GetMonitors` が読む GObject プロパティ **`monitor-handle` が
  デバイス側に在ること**も、実物のプロバイダを起こさないと確かめられない。
  読めなければ静かに 0 になり、規則 5 へ倒れる ── `monitor-index` が書かれていれば
  縮退＋警告、書かれていなければ失敗である。前者では**退行しても「動くが指定が効かない」形で
  静かに壊れる**（`recorder.warning` と `LastError` の `[warning]` 接頭辞には出るが、
  録画そのものは別のモニターで続く）。
- 上の L2 は列挙が空の機械では**前提が無いので飛ばす**（`monitor.devices` の `count=`
  で判定）。緑だから踏んだとは限らない ── 実行結果の skip 件数を見ること。

ここを触ったら、**モニターが 2 台以上ある機械で**次を目で確かめること:

1. パイプライン編集ダイアログの `monitor-device-path` に、モニターの数だけ選択肢が出る
   （`activity.log` の `monitor.devices count=` が件数と `withPath=` を出す）
2. 1 台目以外のパスを選んで OK すると、**そのモニター**が録れる（`monitor-index` を
   書かなくても録れる）
3. 前の番号のモニターを抜いて再起動しても、録れる画面が入れ替わらない
4. 指定したモニターを抜くと `recorder.init fail` にそのパスが出て、挿し直すと復帰する

### カメラ設定（`CameraControls`）の COM 経路

`IAMVideoProcAmp` / `IAMCameraControl` を叩く経路は**開発機では 1 行も走らない**（カメラが無い）。
**自動で守られているのは書式だけ**（`CameraControlSettingsTests`（L1）── 解析・生成・往復・
未知キーの持ち越し・カタログの番号）。**E2E には `CameraControls` を触るテストが1件も無い。**
「…」ボタン（`[ValueBuilder("GstCameraControls")]`）は**ソースの種類に関わらず常に出る**ので、
画面キャプチャのレコーダーで押すと「このソースでは使えない」旨のエラーが出るだけになる
── これは意図した挙動（開けない理由を黙らせない）だが、**それを確かめるテストも無い**。

**カメラのある機械で 1 セッション、次を目で確かめること**（`docs/gpu-verification.md` と同じ
「実行＋レポート往復」でよい）。
**下の 10 項目は Logitech のカメラ 1 台で一度通してある**が、
`GetRange` が返す項目も自動の可否もドライバごとに違うので、
**この節に触れる変更をしたら機種を問わず流し直すこと**:

1. カメラ設定を開いて、`activity.log`（＝アプリ内 Log 画面にも出る）に
   `camera.open resolution=Ok … opened=True controls=<1 以上>` が出ること。
   **`camera.devices` は出ないことがある** ── `SrcPipeline` に `device-path` が
   書かれていれば逆引きの列挙自体が走らないため（それが正常）。
   名前・番号だけで書いている構成なら `camera.devices count= withPath=` も併せて出る
   （実測: `count=1 withPath=1`）
2. SrcPipeline 編集画面で device-name / 解像度 / フレームレートの選択肢が出る（既存の回帰）
3. **`device-path` が読めている** ── `gst-device-monitor-1.0 Video/Source` の出力と
   アプリが出す選択肢を突き合わせる。**ここは `gst_device_get_properties` /
   `gst_structure_free` を追加した箇所**で、解放関数を取り違えるとこの機械でだけヒープが壊れる
4. パイプライン編集ダイアログで OK したあとも `device-path` が残っていること
   （カタログに載せた理由そのもの。落ちるとカメラ設定が黙って効かなくなる）
5. **録画中に**カメラ設定ダイアログを開く → `GetRange` が実値を返す
   （Min/Max/Step/Default が全部 0 なら QI も `IMFGetService` も失敗している）
6. **スライダーを動かすとプレビューが実際に変わる** ←
   **`mfvideosrc` が開いている最中に別ハンドルからの制御が効くか**の唯一の判定。
   **1 機種で成立を確認済み**（Logitech のカメラ。`camera.open … opened=True controls=12`）だが、
   **成否はドライバ次第**なので機種を変えるたびに見ること。
   × なら代替は無い（`ksvideosrc` も制御を実装しておらず・`videobalance` も同梱に無い）ので、
   「録画停止中のみ開ける」へ縮退させること
7. 「自動」を入れると数値欄が無効になり、外すと手動値へ戻る
8. OK → settings.json に `CameraControls` が入る → **再起動しても再適用される**
   （`activity.log` の `camera.control`）
9. **カメラを抜いた状態**でカメラ設定を開く → **可視のエラーが出る**（アプリは落ちない）。
   ここで「録画は止まらない」は見ない ── カメラが無ければレコーダーはそもそも
   初期化できないので、止まるべき録画が存在しない（**両立しない条件だった**）。
   録画側を見たいなら別の筋道で確かめること: **録画中にカメラを抜く**と
   `recorder.error` と自動復帰（`recorder.restart`）が出て、
   カメラを挿し直すと復帰する
10. 別アプリ（Windows カメラ）が占有した状態で開く → 落ちない（UI も固まらない）

開発機で確かめられるのはここまで ── `MFCreateDeviceSource` が失敗して**可視のエラーになる**こと、
そして**録画が止まらない**こと。

**この経路は AOT 発行物でも一度も走っていない。** E2E は `videotestsrc` を使うので
`ApplyCameraControls` が早期 return し、COM には 1 バイトも触れない
（`SrcPipeline` が `mfvideosrc` でなければ何もしない、という設計そのものによる）。
CsWin32 が生成する COM 構造体・`MFStartup` / `MFCreateDeviceSource` の P/Invoke・
GUID 定数はいずれも**トリミングと AOT でこそ壊れる**種類のものなので、
**カメラのある機械での最初の 1 回は AOT 発行物（`win-x64-aot`。配布と同じ構成）で行うこと**
── selfcontained で通っても、配布物で通る保証にはならない。

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

E2E ハーネスは `SettingsFile.DefaultEncoder = "x264enc"` を固定しており、配布 zip に同梱するランタイム一式には x264 が無い。`release.yml` のフィルタは `FullyQualifiedName~SmokeTests|FullyQualifiedName~RuntimeResolutionTests`（`~` は部分一致）なので、同梱物に対して流れるのは `SmokeTests`・`GuiSmokeTests`（L3 の GUI スモークもここで走る）・`RuntimeResolutionTests` の 3 クラスで、**事前バッファ・停止の同期性・ファイル名テンプレートなどの録画系 E2E は同梱物に対しては一度も流れない**。非同梱版と共通のコードなので「アプリの不具合」は `build.yml` で捕まる。捕まらないのは「その runtimes の組み合わせでしか出ない問題」。なお停止結果の規則そのものはランタイム非依存の L1（`RecordingStopRulesTests`）が押さえている。**このスモークは MinGW 版・MSVC 版の同梱物それぞれに対して1回ずつ流れる**（`tools/Assert-SmokeSelection.ps1` が、どちらの回でも 3 クラスが実際に選ばれたことを見る）。

### MSVC 同梱版が要求する VC++ 再頒布可能パッケージ

MSVC 版の同梱ランタイムは `msvcp140.dll` / `vcruntime140.dll` / `vcruntime140_1.dll` を**同梱せず、利用者の機械から**解決する（MinGW 版は自前の libstdc++ / libgcc / libwinpthread を同梱するので自己完結）。**この前提が満たされない機械での挙動は、CI では一切踏めない** ── `windows-latest` には Visual Studio が入っており CRT は必ず在るため、release.yml のスモークが緑でも「再頒布可能パッケージが無くても動く」根拠にはならない。**GPU 実機検証も同じ** ── 流した機械には CRT が在り、そこで全ケース OK になっただけである。前提は THIRD-PARTY-NOTICES.md と README に明記してあるだけで、**実行時に検出して案内する経路も無い**。CRT の無い素の Windows で 1 回確かめること（症状は起動時のネイティブ DLL 解決の失敗で、アプリ側では捕まえられない）。

### `capture-api` が無いランタイムでの値の持ち越し

`capture-api` は WGC 対応のビルドにしか登録されない（同梱の MSVC 版には在り、MinGW 版には無い）。UI は `GstIntrospect.ElementHasProperty` で要素に訊き、無ければ行を出さないが、**いま設定に入っている値は捨てずにテキスト行として持ち越す**（`PipelineBuilderViewModel.RebuildForSource`）── これが外れると「MSVC 版で書いたパイプラインを MinGW 版の機械で開いて OK を押しただけで `capture-api` が消える」。**この分岐は自動テストで守られていない**: `PipelineBuilderViewModel` は WinUI アプリプロジェクト側なので L1 から参照できず、L2/L3 は片方のランタイムでしか走らない。カタログ側の宣言（2 ソースに `ConditionallyAvailable` で載っていること）だけは L1（`SrcPipelineBuilderTests`）が固定している。**持ち越しそのものは手で 1 回確認済み**だが、退行しても赤くならない ── ここを触るときは手で確かめ直すこと。

### 常時録画（`ContinuousRecording`）の実機・同梱面

3 つ、自動では守られていない面がある。

**(1) 高解像度での `PLAYING` 到達（1 回測定済み・自動では守られない）。** 常時録画を有効にすると `tee` の枝が 2 本から 3 本に増える。GPU 実機で `tools/Verify-HighResolution.ps1` の全 11 ケースが通ることを確認済み（結果は `docs/environment-facts.md`。4K でハードウェアエンコーダー 2 本同時を含む）。**ただしこれは CI で回らない一度きりの測定**なので、解像度や枝の構成、queue の方針、`appsink` の `async` を触る変更のときは**必ず流し直すこと**（手順は `docs/gpu-verification.md`）。開発機では GPU が無く 320x240 / 1280x720 までしか踏めない。

なお既定の測定で分かるのは**「分割が 1 回起きること」まで**である ── スクリプトは既定でセグメントが 2 本（＝閉じたもの 1 本）できた時点で待つのをやめる。**4K で長時間ローテーションを繰り返したときの挙動は未測定**。そこを見るときは走行時間を決めている `-ContinuousMinSegments` を上げること（`-ContinuousWaitSeconds` はその待ちの**上限**であって走行時間ではない。上げ忘れると要求本数に届かず失敗する）:

```powershell
.\tools\Verify-HighResolution.ps1 -PublishDir <発行ディレクトリ> -MonitorIndex 1 `
    -ContinuousMinSegments 20 -ContinuousWaitSeconds 180
```

**(2) `videorate` が無い GStreamer。** `ContinuousFramerate` を空でない値にすると枝に `videorate` が入る。**同梱ランタイムには入れてある**（`libgstvideorate.dll` / MSVC 版は `gstvideorate.dll`。`ContinuousRuntimeDependencyTests` が**両形態の台帳**との一致を固定する）が、**利用者が別途入れた GStreamer には無いことがある** ── そのとき「`videorate` が無い」と名指しで失敗して枝だけ落ちる経路（`EventRecorder.ResolveContinuousEncoder`）を、**実際に流す自動テストは無い**（開発機も CI もフル構成なので再現できない）。要素を意図的に隠したツリーで手で 1 回確かめること。

**(3) 上流を固定できないソースでの解像度の上書き。** `ContinuousResolution` が効くのは `SrcPipeline` の caps が `width` / `height` を固定しているときだけで、していなければ上書きは捨てられる（理由は `ContinuousLastError` に出る）。**この制限そのものは L1（`ContinuousBranchTests`）と L2（`AResolutionOverride_NeverShrinksTheEventRecording` / `OnTheD3d12Path_...`）が縛っている**が、**「利用者が caps に width/height を書いたら画面キャプチャでも期待どおり縮む」ことを実機で確かめてはいない**（開発機は GPU 無し）。画面キャプチャで常時録画の解像度を使うときは、一度目視で本線の解像度が落ちていないことを確かめること。

**(4) B フレームを出すエンコーダー。** `ContinuousEncodingProperties` を手書きして B フレームを有効にすると PTS が並べ替えられ、`ContinuousRecorder` は巻き戻しとして扱ってセグメントを切り直す（`continuous.error` に畳んで 1 行）。カタログの候補はすべて低遅延（B フレーム無し）なので**自動テストではこの経路に入らない**。同じ制約はイベント録画の `PushRecordBuffer` にもあり、そちらも同様に守られていない。
### 言語強制マトリクスのうちホストの表示言語と重なる行

`LanguageMatrixTests` は `PROCESSRECORDERAPP_LANG` で ja-JP / en-US / de-DE の 3 言語を回すが、**ホストの表示言語と一致する行はそのホストでは何も検証していない**（強制してもしなくても同じ表示になるため。表示言語 ja の開発機では ja-JP の行が、en-US の GitHub ランナーでは en-US と de-DE の行が空回りする）。**CI が別の表示言語のホストで走ることで初めて全行が実際に効く。** 言語解決を触るときは、どのホストでどの行が効いているかを意識すること。検出が生きているかは、言語強制フック（`ApplyLanguageOverride`）を無効化する注入で「ホストの表示言語と異なる行だけが赤になる」ことで確かめられる。この非対称の根拠は `LanguageMatrixTests` の冒頭 doc にも書いてある。

### Close() の破棄済みフィールドの null 化

`EventRecorder.Close()` は破棄したフィールドを必ず null 化して冪等にしてある。`Initialize` は先頭で `Close()` を呼んでから各フィールドを再代入するため、初期化が途中で失敗すると catch 内の `Close()` が「破棄済みのまま残ったフィールド」を再度触る（パイプライン編集ダイアログに不正な文字列を入れると到達する）── null 化が無いとネイティブオブジェクトの二重解放になる。`PipelineDialogTests` は「ダイアログ経路がクラッシュしないこと」「`LastError` が出ること」を守るが、**null 化を外す注入は検出できなかった**（実施済み・未検出）。null 化を外さないこと。テストが担保していると数えないこと。

### RecorderNavViewBehavior の到達不能分岐（Reset / TryBuildMenu 再入時の解除）

`Add` と `Remove` は `RecorderManagementTests` が実際に通すが、`Reset` 分岐は `Recorders.Clear()` を呼ぶ経路が製品に無く、`TryBuildMenu` は構築済みなら早期 return するため、**どちらも到達経路が存在しない**。`AppSettings.Reload()`（呼び出し元が無いまま `AppSettingsReloadTests` がソース照合で守っている）と同じ扱いで、将来 `Clear()` が生えたときの受け皿として残す（Reset 時点でコレクションが空になりうるため、解除対象を取り違えないための影リストごと）。**到達経路を探すのに時間を使わないこと。**

### ListViewCopyBehavior のポインタ購読の解除

`Uninitialize()` の `RemoveHandler` 3 本。**到達経路が存在しない。** 計測を挿して L3 の全セクション切替（Preview / Log / Variables / Settings）を通した実測では、`Initialize()` は **1 回だけ**（`OnAttached` の中から、`IsLoaded=false` のまま）呼ばれ、**`Uninitialize()` は一度も呼ばれない** ── パネル切替は Visibility で行うので Loaded/Unloaded が循環せず、ページ破棄はプロセス終了と同時だからである。したがって解除が効いているかどうかは外から観測できない。

`AddHandler` / `RemoveHandler` に渡すデリゲートを**フィールドで使い回す**のはこのためで、予防である ── その場で `new` すると、マネージド上は等値でも CsWinRT の CCW が別インスタンスになり、`RemoveHandler` が例外も出さずに解除に失敗しうる。**テストが担保していると数えないこと。** ページの寿命や再アタッチの形（Frame のナビゲーション、パネル切替を Visibility から Loaded/Unloaded へ変更）を導入するときは、ここが効き始める。

### ナビ項目の購読解除

`Recorders_CollectionChanged` の Remove で解除対象を取り違える退行。`MainPage_Unloaded` の解除漏れとまったく同じ理由で観測できない ── 削除されたレコーダーを後から改名する経路が無いため、解除漏れの結果が外に出ない。注入を実施して未検出であることを確認済み。防御として残すが、テストが担保していると数えないこと。

### DefaultLanguage / NeutralLanguage の明示

`src/Directory.Build.props` の `<DefaultLanguage>en-US</DefaultLanguage>` / `<NeutralLanguage>en-US</NeutralLanguage>`。**どちらを外しても実行時の挙動は変わらない**（2 件とも注入して未検出）。ただし 2 行の性質は非対称で、`DefaultLanguage` は WinUI/MSIX ツーリングが既に en-US を既定値として与えているため明示は既定の再掲（発行物は不変）、`NeutralLanguage` は未設定（空）だったため明示は実際の追加（アセンブリに `NeutralResourcesLanguageAttribute` のメタデータが増える）── 実効値は `dotnet msbuild -getProperty:` で確認でき、どちらかを消してよいか判断するときはこの区別が唯一の材料になる。書いてあるのは「暗黙の既定に依存しない」という宣言であって、退行検出器があるからではない ── **ツーリングの既定が変わったときは静かに壊れる。** 消さないこと。

### プレビューの全画面表示のうち L3 で押さえられない面

自動で守られているのは 2 つ ── 「全画面に入るとナビとプロパティペインが UIA から消え、
`PreviewSurface` は残る／`Esc` で戻る」と「全画面のあいだにウィンドウサイズを
settings.json へ焼き込まない」（どちらも `PreviewFullScreenTests`）。
後者は**実際に踏んだ退行**で、`SizeChanged` の中で `AppWindow.Presenter.Kind` を見る実装が
そのまま通ってしまった（サイズ変更の通知はプレゼンターの差し替えより先に来る。
詳細は `src/README.md` の「ウィンドウサイズを全画面の大きさで上書きしないこと」）。

守られていないのは次の 4 つで、ここを触ったら発行物で手動確認すること:

1. **右クリックメニューの中身** ── `MenuFlyout` は**別のトップレベル UIA ウィンドウ**に出る
   （`AppUi.SearchTree` はアプリのウィンドウを根に降りる）ので辿れず、
   右クリック自体も物理カーソルを要求する ＝ `Category=Fragile` 行き（CI の必須ゲート外）。
   **サブメニューを持つので見る点が増えている**:
   「プレビュー」にレコーダー一覧が出て選ぶと切り替わること、「フレーミンググリッド」に
   5 種が出て選ぶと線が変わること、**どちらも現在の値に印（チェック）が付いていること**、
   最後の項目で全画面へ入り／戻れること。
   **通常表示のときに「全画面を終了」が出ないこと**（文言が「全画面表示」であること）も
   併せて見る ── 出ていれば押しても何も起きない死に項目で、自動では一切観測できない。
   **レコーダーが 0 件のとき「プレビュー」が押せないこと**も見る（空のサブメニューは袋小路）。
   **全画面中にメニューを開いて、上下で項目を辿れる・左右でサブメニューを開閉できる・
   `Esc` で閉じられること**も見る ── 全画面のアクセラレータはウィンドウ全域に効き
   **ポップアップの既定のキー操作より先に取る**ので、開いているあいだは
   `IsPreviewMenuOpen` で止めている。止まっていないと、押した瞬間に背後の補助線や
   レコーダーが動き、**メニューの印は組み直されないので表示と実際がずれる**。
2. **左右キーでのレコーダー切替と、上下キーでの補助線切替** ── フォーカスの位置に依存する
   （アクセラレータはウィンドウ全域に効くが、フォーカスが WebView2 の中にあると届かない）。
   上下キーは**全画面でないときに一覧のキーボード操作を奪っていないこと**も併せて見る
   （奪っていると Settings 画面の項目を上下で辿れなくなる）。
3. **トレイ格納 → 復帰で全画面が解除されていること**（`App` の `WindowHiddenToTray`）。
   トレイ経路は `Category=Fragile` なので CI では走らない。**解除が効かないと、
   タイトルバーもナビも無い全画面で戻ってきて閉じる手段が消える。**
   あわせて**最大化 → 全画面 → トレイ格納 → 復帰で最大化のまま戻ること**を見る
   （プレゼンターの実体を `PreviewFullScreen` へ寄せた理由。経路ごとに戻り方が
   違っていないかの確認）。
4. **タイトルバーが実際に消えていること** ── `AppTitleBar` は `MainWindow` の要素で
   `AutomationId` を持たない。UIA から「消えた」ことは表明していない。

### 構図補助線（フレーミンググリッド）が実際に描かれること

`AppSettings.FramingGrid` の補助線は、**線が引かれていること自体を自動では観測できない**。
`Canvas` の子は `AutomationProperties.AccessibilityView="Raw"` にしてあり（そうしないと
`PreviewSurface` の子として E2E の要素列挙が汚れる）、そもそも「線が映像の縁と一致しているか」は
UIA からは分からない。自動で守られているのは**幾何の規則だけ**
（`FramingGridGeometryTests`（L1）── アスペクトフィットの矩形・4 種の線の位置・
すべての線が映像の矩形に収まること・映像の左上を基準にしていること）。

守られていないのは「その幾何が実際の描画に届いているか」で、ここを触ったら目視で確認すること:

1. 4 種（三分割・黄金比・十字・正方形）がそれぞれ出る
2. ウィンドウをリサイズしても線が映像の縁と一致し続ける
3. **映像とパネルのアスペクトが違うとき**（4:3 のカメラを横長のパネルで見る、または逆）
   **線が黒帯へはみ出さない** ── アスペクトが一致した構成では、パネル基準で書いてしまう
   退行と正しい実装が同じ結果になるので、この構成でしか目視で見分けられない
4. 高DPI（125% 以上）でずれない ── `NativeSwapChainPanel` の逆スケール行列は
   スワップチェーンにしか掛かっておらず、`Canvas` は DIP のままである。
   物理ピクセルを渡す退行はここでだけ見える
5. レコーダーを未初期化のものへ切り替えると線が消える（前のアスペクトが残らない）

### デバイス到着の監視（実デバイス → シグナルの脚）

自動復帰の待ちは、カメラや画面キャプチャなら**デバイスが戻ってきた時点で打ち切られる**
（`DeviceArrivalWatcher`）。この経路は 2 つの脚に分かれ、**自動で守られているのは後半だけ**である。

- **前半（実デバイス → シグナル）は自動では一度も走らない。** 上流のデバイスプロバイダの
  ホットプラグ通知に依存しており、**開発機にも CI にもカメラが無く、モニタの抜き差しもできない**
  （開発機は WARP ＋ RDP）。
- 後半（シグナル → 早期復帰）は L2 の `DeviceArrivalTests` が実測する。
  シグナルは `PROCESSRECORDERAPP_TEST_DEVICE_ARRIVAL` で外から起こしており、
  **実プロバイダには触れていない**。

**実機（カメラ・モニター 2 台・MSVC 同梱の AOT 発行物）で 1 度、全項目を通してある。**
`wake=device-arrival` が出た＝到着で起きた、出ていない＝タイマーで起きた、で読み分ける。
**この節に触れる変更をしたら、機種を問わず流し直すこと** ── 通知を出すのは上流の
プロバイダとドライバであって、こちらの都合では動かない。

| 見たこと | 結果 |
|---|---|
| 抜いたときに監視が張られる | **確認** ── カメラ `device.watch kind=camera provider='mfdeviceprovider' monitor=yes`、モニター `kind=monitor provider='d3d12screencapturedeviceprovider' monitor=yes` |
| 初期化に失敗しても連鎖が残る（詰みの解消） | **確認** ── `rebuild result=failed` の直後に `round=1 scheduled in 60000ms` が出て、次の周回で `rebuild result=ok repeated=1` に至った |
| **モニターの到着で早期に起きる** | **確認** ── 到着 2 件（`EV2360` / `LG Ultra HD`）が 1ms 差で届き、**1.503 秒後**に `retrying the pipeline rebuild round=1 wake=device-arrival` → `rebuild result=ok wake=device-arrival`。束ね 500ms ＋ 落ち着き待ち 1000ms の設計値と一致し、60 秒の待ちを **約 52 秒短縮**した |
| **カメラの到着で早期に起きる** | **確認** ── `device.arrive kind=camera` の **1.503 秒後**に `retrying the pipeline rebuild round=1 wake=device-arrival` → `rebuild result=ok wake=device-arrival`。モニターのときと**ミリ秒まで同じ遅れ**で、60 秒の待ちを **46.3 秒**短縮した |
| **RDP のセッション復帰で早期に起きる** | **確認（ただし試行は失敗する）** ── 到着の **1.5 秒後**に起きるところまでは抜き差しと同じだが、その試行は**全エンコーダーが `pipeline doesn't want to play`** になり、次の試行で成功した。画面構成が落ち着くまでに数秒かかるためで、**到着の直後は撮れるとは限らない**（この失敗の後の間隔を仕切り直すのが `RestartPolicy.RebuildDelayMs`） |
| 抜き差し後のデバイスの並び | **確認** ── 復帰後にパイプライン編集画面で見て、`monitor-index` と解像度、カメラの `device-index` / `device-name` / `device-path` の対応がいずれも実体と一致していた（監視は復帰待ちのあいだだけなので、見た時点ではプロバイダは既に停止している） |

**`mfdeviceprovider` の到着通知は、物理的な抜き差しから数秒遅れる。** 別の回で
「抜いてから約 10 秒後に挿す」を試すと `device.arrive` は**抜いてから 20.1 秒後**に出た。
挿した時刻を計測していないので遅れの下限は確定できないが、**5 秒のバックオフでは
まず間に合わない**ことは実測から言える ── 最初の 2 回はどちらも 5 秒／60 秒の待ちを
待ち切ってタイマーで復帰し、`device.arrive` は `rebuild result=ok` の後に出ていた
（0.918 秒後・0.902 秒後。当初はアプリ自身がカメラを開いたことによる自己誘発を疑ったが、
**その仮説は取り下げる** ── 到着で確実に起きる回が観測できた以上、通知の遅れと
再試行の時刻がたまたま近かったと読む方が実データに合う）。

**だからタイマーの梯子は外せない。** 到着は「間に合えば早める」ものであって、
復帰の唯一の駆動源ではない。60 秒の待ち（`rebuildOnly`）では通知が十分に間に合う。

**RDP のセッション復帰では、到着の直後はまだキャプチャできないことがある。**
実測では、ケーブルの抜き差しは到着 +1.5 秒の試行で成功するのに対し、RDP の復帰は
**同じ +1.5 秒で全エンコーダーが `pipeline doesn't want to play` になり、次の試行で成功**する
── 画面構成が落ち着くまでに数秒かかるためで、到着通知そのものは正しく届いている。
落ち着き待ち（`EarlyWakeSettleMs`）を一律に延ばすのは誤り（抜き差しの側が遅くなる）なので、
**作り直しの間隔を到着で仕切り直す**（`RestartPolicy.RebuildDelayMs`。到着の後は
5s → 10s → 30s → 60s の梯子をやり直す）ことで追いかける。頭打ち固定だと、
この 1 回の失敗で**復帰が丸 1 分遅れる**。梯子そのものは L1、到着後に間隔が短くなることは
L2（`DeviceArrivalTests`）が押さえているが、**「到着の直後は撮れない」という現象自体は
自動では踏めない** ── 開発機でも CI でも RDP のセッションを張り直せない。

**RDP のセッション復帰は三段階で戻る。** 実機の復帰ログでは、モニタの列挙
（`monitor.devices`）が次の順に変わる:

1. **0 台** ── 到着の直後はプロバイダが 1 台も返さない（`count=0`）
2. **台数は戻るが `device.path` が読めない** ── `count=2 withPath=0` のような形。
   デバイスは在るのに照合の材料が無い
3. **パスも読める** ── `count=2 withPath=2`。ここで初めて解決できる

**②で縮退させてはいけない。** 台数が戻っているので「モニタは在る」ように見えるが、
パスが読めない以上どれが指定されたモニタかは分からない ── 番号へ倒せば**別の画面を撮る**。
`MonitorSelection` は列挙が非空・不一致として**失敗**させる（規則 3）。①も同じで、
`monitor-index` が書かれていなければ**失敗**させる ── パスを取り除くだけだと選択
プロパティが 1 つも無い文字列になり、既定のモニタ（index 0）を黙って撮ることになる
（縮退できるのは戻せる先が書かれているときだけ。規則は `MonitorSelectionTests` が縛る）。
失敗は放置ではない ── ①も②も次の再試行で③へ進み、そこで正しいモニタに戻る。

**この三段階は自動では踏めない。** 開発機でも CI でも RDP のセッションを張り直せず、
列挙の中身も実機依存なので、L2/L3 では「0 台」「パスが読めない」「読める」を撃ち分けられない。
上の手動確認（`device.arrive kind=monitor` の後のログを読む）で、`monitor.devices` の
`count=` / `withPath=` がこの順に動くことと、途中の `recorder.init fail` の後に
最終的な `rebuild result=ok` へ至ることを見ること。

**録画の録り直しは、この実機確認より後に足した面である。** 上の回では
「復帰したのに録画だけ戻らない」ことを実測している（`recording.stop … result=ok
samplesPushed=211` ── ファイルは壊れていない）。その後、作り直しで畳んだ録画を
録り直すようにしたが、**その挙動は実機では確認していない**
── 自動で押さえているのは L2（`ErrorReportingTests.AfterRecovery_TheRecordingIsResumed`。
`recording.start` が 2 本出ること）と、L1 のソース静的検査（`RecoveryResumeTests`）である。

**抜けているあいだの停止が届くことも実機では未確認。** 作り直しのあいだは
`IsRecording` も `IsInitialized` も false なので、`RecordingCommandState.CanStop` が
`resumePending` を見なければ停止はどこにも届かない ── 利用者の停止も、
**UiaTrigger の停止条件**も同じである。規則は L1 が縛り、順序（`StopAsync` の早期 return
より前で取り消すこと）もソース静的検査で固定しているが、**窓が数秒しかないので
E2E からは踏めない**。カメラのある機械で次に触るときは、
抜いてから `round=1 scheduled in 60000ms` を確認し、その最中に停止してから挿し直して、
`not resuming the recording after the rebuild (stop requested)` が出て
録画が再開しないことを見ること。

**手動確認には RDP のセッション復帰も含めること。** 画面キャプチャのレコーダーを動かした
まま RDP のセッションを切り、しばらく置いてから接続し直して、`device.arrive kind=monitor` の
後のログを読む ── 到着 +1.5 秒の試行が失敗した場合に、次の案内が
`scheduled in 60000ms` ではなく `scheduled in 5000ms`（以降 10s / 30s）になり、
梯子のどこかで `rebuild result=ok` に至ること。抜き差しとは**落ち着くまでの時間が違う**ので、
ケーブルでの確認は代わりにならない。

**利用者が別途入れた GStreamer に d3d12 プラグインが無い構成**も自動では踏めない
（開発機も CI もフル構成）。そのときは `device.watch … monitor=no` を出して
タイマーだけの復帰へ縮退するが、**その分岐を実際に流すテストは無い**。

### WGC（`capture-api=wgc`）での切断 → 復帰

画面キャプチャの WGC 経路は、**ディスプレイを切断してもエラーを出さない** ──
`recorder.error` は 1 行も出ず、sink バスへ `Eos` を流して黙る。だから復帰の引き金は
`recorder.eos` の側の予約だけであり、そこが外れると**この経路でだけ復帰が丸ごと効かなくなる**
（DXGI は `Internal data stream error` を出すので Error 分岐が拾い、症状が出ない）。

**自動では一度も踏めない。** 実機・実ディスプレイの抜き差しが要り、開発機は WARP ＋ RDP で、
CI にも物理ディスプレイが無い。さらに `capture-api` は WGC 対応のビルドにしか登録されない
（同梱の MSVC 版には在り、MinGW 版には無い）ので、**確認は MSVC 同梱版でしか行えない**。

自動で押さえているのは 2 面だけである:

- L1 のソース静的検査（`SinkEosRecoveryTests`）── EOS 分岐が `ScheduleRestart` を呼ぶこと、
  `_sinkSawEos` の印がその前に立つこと、種別（`DeviceKindRules.Classify`）で絞っていること。
- L2 の負の確認（`StopOutcomeTests`）── 有限のソース（`num-buffers`）の EOS の後に
  `recorder.restart` が 1 件も出ないこと。**種別の門が外れるとここが赤くなる**。

**手動確認の手順**（MSVC 同梱版・モニター 2 台以上、抜いてよい側を対象にする）:

1. パイプライン編集画面で画面キャプチャを選び、`capture-api` を `wgc` にして OK。
   `recorder.init ok` を確認する。
2. 対象モニターのケーブルを抜く。
3. `activity.log` に `recorder.eos` が出て、**その直後に `recorder.restart …
   attempt=1 scheduled in 5000ms watch=monitor` が続く**こと。ここで
   `device.watch kind=monitor provider='d3d12screencapturedeviceprovider' monitor=yes`
   が出ることも見る ── **`recorder.eos` の 1 行で止まっていたら退行**である。
4. 挿し直して `recorder.restart … rebuild result=ok` に至り、プレビューが戻ること。
   EOS を受けた連鎖は要素単位の再開を飛ばすので、出るのは `rebuild result=` の側になる。
   到着が間に合った回は `wake=device-arrival` が付く（付かなければタイマーで復帰した回）。
5. 録画中に抜いた場合は、`will be resumed once the pipeline is rebuilt` →
   `resuming the recording that the rebuild finalized` が出て録画が戻ること。

### 自動復帰のあとのプレビューの滑らかさ

**カメラを抜き差しして自動復帰したあと、プレビューがカタつくことがある**（実機で 1 度観測）。
レコーダーを再 `Initialize` すると解消する。

自動で守られていない ── 抜き差しは設定だけでは起こせず、開発機にはカメラも無い。
`EventRecorder` の自動復帰は**要素単位の再開**（エラー元を `Ready` → `Playing`）であり、
プレビュー用パイプライン（`appsrc ! queue ! d3d12swapchainsink`）は作り直されないので、
**再開の前後で不連続なタイムスタンプが `appsrc` に入る**ことが疑わしいが、
確かめられていない（再 `Initialize` で直るのは、そこでプレビュー面ごと組み直すため）。

なお **EOS を見た障害はデバイスの到着で作り直されるようになった**ので、この症状が出る窓自体は
縮んでいる ── `_sinkSawEos` が立っていれば要素単位の再開を飛ばして
`Initialize()` へ進み、挿し直しの約 1 秒後にプレビュー面ごと組み直される。
**因果の確認は依然として手動でしかできない。**

同じ時期に、プレビューが**毎フレーム `sample.GetCaps()` の戻り値を破棄していた**欠陥
（transfer none の借用参照。`GstPreviewer.UpdateVideoSize`）を直しているので、
**症状がこれで消える可能性はあるが、因果は確認できていない** ──
抜き差しの確認自体がこのとき初めて行われており、それ以前からの挙動かどうかも分かっていない。

ここを触るとき、あるいはカタつきが再発したときは、次の順で切り分けること:

1. 抜き差し → `activity.log` に `recorder.error` と `recorder.restart` が出ることを確認
2. その状態でカタつくか（＝要素単位の再開の直後か）
3. レコーダーの `Initialize` を実行して解消するか（＝プレビュー面の作り直しで直るか）
4. 直るなら、疑うのは**プレビュー用 `appsrc` に再開の不連続が伝わっていないこと**
   （`appsrc` を flush する／再開時にプレビュー面を組み直す、のどちらか）

### プレビューの実行時障害の観測（`preview.error`）

`Previewer` のバスの同期ハンドラ（`SubscribeBus`）は Error を `preview.error` に
残すが、**この経路を自動で踏ませる手段が無い**（D3D デバイスロスト等を設定だけで起こせない。
`NativeSwapChainPanel` の `SetSwapChain` 失敗も同様）。
プレビューは WARP でも成立してしまうため、E2E で観測できるのは
`PreviewPlaceholderTests` の「プレビューが無くても録画は続く」までである。ここを触ったら、
記録が消えていないかをソースで確認すること（無記録に戻すと「プレビューだけ黙って固まる」に戻る）。

### コンソール出力を Mutex の外へ出したこと

`SingleInstanceManager.Run` は結果の書き出しを `launcherMutex` の解放後に行う（conhost の
QuickEdit 選択中など、コンソール書き込みが無期限にブロックしうるため）。**この順序を戻す退行は
検出できない** ── 出力内容も終了コードも変わらず、変わるのは「他の CLI が待たされるかどうか」
だけで、それを踏ませるには人がコンソールで選択状態を作る必要がある。順序を変えないこと。

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

### リモート操作のブラウザ側（MSE の JavaScript）

`src/RemoteControl/wwwroot/app.js` の**プレビュー再生そのものは、どの層も 1 行も実行しない**。
L1 は `WebAssetManifestTests` が「資産の一覧が合っているか」を見るだけ、L2 は HTTP の応答
（fMP4 のバイト列）までしか見ず、L3（UIA）はブラウザを起動しない。すなわち
`MediaSource` / `SourceBuffer` の組み立て、`mode = 'sequence'`、**35 秒を超えたら末尾 30 秒へ
詰めるトリム**、3 秒以上遅れたときの追従、未読 16MB での打ち切り、**固定 1 秒の自動再接続**と
「非 OK 応答では張り直さない」判断は、すべて無検査である。JS の構文エラー 1 つで画面が
真っ白になっても push は緑になる。

ここを触ったら、発行物を起動して**実際に Chrome か Edge で開く**こと。DevTools の Console に
例外が出ていないこと、映像が出ること、30 秒以上放置しても `video.buffered` が伸び続けないこと、
アプリ側でレコーダーを停止 → 再開したときに自動で再接続することを目で確かめる。

### リモート操作のブラウザ側（録画の追いかけ再生）

同じく `src/RemoteControl/wwwroot/app.js` の**録画の追いかけ再生（`startFollow` 以下）は、
どの層も 1 行も実行しない**。L2 が見るのはサーバー側だけ ── `fragmented` の一覧、
`X-In-Progress` / `X-Codecs`、`Range` の 416 と total の伸び、停止後のファイルが読めること
── で、`MediaSource` の組み立て、`mode = 'segments'`、`Range: bytes=<next>-` での追跡、
**416 を「まだ伸びていない」として 1 秒待つ判断**、`endOfStream()` の条件、ライブ端への追従と
**利用者のシークで追従をやめる**こと、70 秒を超えたら末尾 60 秒へ詰めるトリムは、すべて無検査である。

ここを触ったら、発行物を起動して**実際に Chrome か Edge で開く**こと。`FragmentedOutput` を
ON にしたレコーダーで録画を始め、一覧の「Play」が録画中でも押せて映像が出ること、
放置しても伸び続けること（時間表示が進む）、途中で手動シークすると先頭へ引き戻されなくなること、
録画を止めると「complete」で終わり、一覧を再取得した完了済みの行も同じように再生できることを
目で確かめる。

### `FragmentedOutput` は既定 off なので、E2E の大半は fMP4 経路を踏まない

`FragmentedOutput` の既定は `false` で、**既定構成の録画物・パイプライン文字列は 1 バイトも
変わらない**（そのための既定である）。裏返すと、fMP4 の経路を踏むのは
`RecordingDeliveryTests` の 2 件（設定を明示的に ON にしたもの）だけで、
事前バッファ・停止の同期性・自動復帰・常時録画・強制終了といった録画系 E2E は
**すべて `faststart` 側だけを見ている**。fMP4 側の停止の排出や自動復帰との組み合わせは
無検査なので、`FragmentedOutput` を実運用で使う前に、その構成で一度手で確かめること。

### プレビューの 4 設定は配信に配線されていない

`EventRecorderSettings` の `PreviewWidth` / `PreviewHeight` / `PreviewFps` /
`PreviewBitrateKbps` は、**保存・PATCH・丸めだけが実装されており、どこも読んでいない**
（消費者は DASH プレビュー）。L1 が丸めを、L2 が PATCH と GET のメタを見るので
「設定として壊れていない」ことは守られているが、**値を変えても配信物は 1 バイトも変わらない**
── ブラウザで映像を見ながら値を動かしても何も起きないのが現在の正しい挙動である。

配線したら、この節を消して「実際にその解像度・フレームレート・帯域で配信されること」を
見る層を書くこと。

### リモート操作のブラウザ側（ログイン UI と役割による出し分け）

同じく `src/RemoteControl/wwwroot/app.js` の**ログイン画面への切り替えと、役割に応じた
コントロールの出し分けは、どの層も 1 行も実行しない**。L2 は HTTP の応答（401 / 403 と
`/api/me` の中身）までしか見ず、ブラウザは誰も起動しない。すなわち

- `401` を受けたときにフォームへ切り替え、SSE（`EventSource`）とプレビューを止めること
- ログイン成功後に `/api/me` を引き直し、`Operator` 未満で開始/停止のボタンが消え、
  `Admin` 未満で設定の欄が無効になること
- ヘッダーの `name (role)` とログアウト、ゲスト（`guest=true`）のときの「ログイン」ボタン

はすべて無検査である。**サーバー側が正しく 403 を返していても、画面が押せるボタンを出し続ける
という退行は緑のまま通る**（押した結果が 403 になるだけなので、被害は出ないが使えない画面になる）。

**ソースの編集欄（`sourceSection`）と、レコーダー設定の読み取り専用表示も同じく無検査**である。
L2 が見るのは HTTP の応答（`GET /api/sources` の中身・`PUT …/source` の 400/403/409・
項目メタの `remoteEditable`）までで、

- `Admin` 以外でセクションごと消えること（`applyPermissions`）
- `kind` に応じて `<select>` と `<input type=text>` を出し分け、空欄は要求に入れないこと
- `remoteEditable=false` の欄が `disabled` になり、`applyRecorderSettings` の本文から落ちること
  （**`markWrite` を通していない**のが要点 ── 通すと `permit` が Admin で有効へ戻す）
- SSE が切れたときに `GET /api/me` を 1 回だけ叩くこと、ゲストのログイン画面に「Cancel」が出ること

は誰も実行しない。

ここを触ったら、発行物を起動して**実際に Chrome か Edge で開く**こと。3 つの役割で順にログインし、
出るボタンが変わること・ログアウトでフォームへ戻ること・そのあと同じ Cookie で読めないこと
（DevTools の Network で 401）を目で確かめる。ソースの欄は `Admin` で 1 度適用し、
レコーダーの設定画面の `SrcPipeline` が変わることまで見る。

### プレビューの初画までの時間

L2 の `PreviewStreamTests` が確かめているのは「**最初の 3 つの `moof` のどれかが同期始まり**」
であって、初画までの時間ではない。これは緩和した条件である ── fragment は IDR に揃わないので
「1 つ目が必ず同期」とは書けない。したがって、GOP が伸びる／`fragment-duration` を変える／
途中参加の先渡しが壊れる、といった**「映るまでが遅くなる」退行は検出できない**（映りさえすれば
緑になる）。実測したいときは、ブラウザで開いてから最初のフレームが出るまでを手で計ること。
定常の遅延はおおむね 2〜3 秒である。

### Teardown 後の自動再開と、再接続後の Init

`Teardown` で muxer を畳んだあと、購読者が残っていれば次のサンプルで muxer が建て直される。
この**「2 本目の muxer」と、再接続したクライアントが受け取る 2 つ目の Init** は、どの層でも
検査していない（L2 は 1 本の接続を張って落とすところまでしか見ない）。

なお `Teardown` の直後、完了済みの購読が `_subs` に残っているあいだは `_wantMux` が真のままで、
`preview.stream-start` / `preview.stream-stop` の対が**1 組余分に出る**ことがある。これは
既知で許容している挙動であり、ログを読むときの誤読の元になる。

### プレビューの異常経路（`caps changed` / `pts rewind` / splitter fault / `preview.leak`）

`preview.stream-stop` の 6 つの理由のうち、L2 が実際に踏むのは正常系の `no subscribers` と
`close` だけである。**`caps changed`（SPS/PPS の変化）・`pts rewind`（PTS の巻き戻し）・
`Fmp4SegmentSplitter` の破綻（`splitter fault` → `preview.stream-error`）・
`preview.leak`（`Monitor.TryEnter(_muxLock, 5000)` の失敗）は、どれも 1 行も走らない** ──
起こすには解像度が途中で変わる映像源や、mp4mux が壊れたバイト列を吐く状況が要る。
`Fmp4SegmentSplitter` の破綻検出そのもの（`Fmp4SegmentSplitterTests`）は L1 が単体で縛っているが、
**それが `preview.stream-error` と再作成へ繋がる配線**は無検査である。

ここを触ったら、`identity error-after` などで人為的に障害を起こして
`activity.log` に `preview.stream-error` が出ること、そのあとブラウザが自動で映像を取り戻すことを
確かめる。

### `MaxTotalSubscribers`（複数レコーダーに跨る全体上限）

`PreviewStreamLimits.MaxSubscribersPerRecorder = 4` は L2 が踏む（1 レコーダーに 5 本目を
張ると 503）。**`MaxTotalSubscribers = 8` のほうは踏んでいない** ── 複数のレコーダーへ
合計 9 本を張る必要があり、E2E の既定構成にレコーダーが足りない。上限そのものを消しても、
片方だけ消しても緑のままである。

### E2E の `Mp4File` の同期判定は製品規則の写し

`tests/ProcessRecorderApp.E2E/Mp4File.cs` の `StartsWithSync` は、製品側
`Fmp4SegmentSplitter.StartsWithSync` の規則（`trun.first_sample_flags` →
`trun.sample_flags[0]` → `tfhd.default_sample_flags` → `trex.default_sample_flags`）を**手で
写したもの**である。したがって**両方が同じように間違っていれば緑になる**。独立した検査は
qtdemux（GStreamer 側のパーサ）が読めることだけで、そちらはフラグの意味までは見ない。
規則を直すときは、必ず両側を対で直したうえで、実際のブラウザで再生できることを確かめること。

### `WebAssetManifestTests` が捕まえないもの

`app.js` については `<script src="http` と `import ` の 2 つの文字列しか見ていない。
したがって **動的 `import(...)` と CSS の `@import`** は素通りする ── どちらも
「第三者のスクリプトはゼロ」「外部への参照はゼロ」という前提を壊すのに、
`THIRD-PARTY-NOTICES.md` の更新を促す仕組みは何も動かない。`wwwroot` に何かを足すときは、
外部を参照していないことを目で確かめること。

### `RecordingCleanup` と `RecordingFiles` の列挙規則の一致

自動削除（`RecordingCleanup`）と配信（`RecordingFiles`）は、**同じ規則で同じ集合を見ることを
意図している**（`"*"` ＋ 自前の拡張子比較、リパースポイントのディレクトリへ降りない）。
配信側だけがファイル自身のリパースポイントを追加で外す ── これは意図的な差である。
**この「意図した一致」を縛るテストは無い。** 片方の規則だけを変えると、
「一覧には出るが自動削除されない」「削除されるのに一覧に出ない」という食い違いが静かに生まれる。
`RecordingCleanup.Extension` を共有しているのは一致の一部でしかない。どちらかを触るときは、
もう一方も読むこと。

### 別 PC・Windows Firewall 越しの到達

L2 は同じマシンのループバックでサーバーを叩く。**別 PC のブラウザから実際に届くこと**
（`0.0.0.0` バインドが効いていること、Windows Firewall の受信規則を入れれば通ること、
`?token=` → Cookie → 302 の一巡がブラウザで成立すること、一覧 → 再生 → シーク →
ダウンロードが通ること）は、どの層も確かめていない ── CI にも開発機にも 2 台目が無い。

ここを触ったら、実際に別 PC のブラウザで一巡すること。Firewall の規則はアプリが登録しないので、
`New-NetFirewallRule` で自分で入れる（ルート [README.md](../README.md) に例がある）。

### 終了コード 17 の HTTP 経路と `PUT /api/variables/{key}` の `%2F`

**終了コード 17（確定できなかった MP4）の HTTP 経路は未検証**である ── CLI 側の
`StopOutcomeTests` と同じ理由で、17 を確実に起こす手段が無い。写像（17 → 422 ＋ `filename`）は
L1 が縛るが、`RemoteApiException` から `ErrorDto.Filename` へ実際に運ばれることは走っていない。

`PUT /api/variables/{key}` に `/` を含むキー（`%2F`）を渡したときの復号挙動も未検証である。
CLI 側は `/` 入りのキーを受けるので、ここだけ受け付けない／別のキーとして解釈される可能性がある。

### レコーダー設定 PATCH の非対称（この項目は性格が違う）

**これは退行の穴ではなく、意図してそうした設計判断**である。アプリ設定の PATCH は
`RemoteEditableAppSettings` の 9 キーだけを通す**許可リスト**であるのに対し、
レコーダー設定の PATCH は `RemoteDeniedRecorderSettings`（`SrcPipeline`・`EncodingProperties`・
`ContinuousEncodingProperties`・`FilenameTemplate`・`ContinuousFilenameTemplate`）だけを断る
**拒否リスト**で、**残りのプロパティはトークン所持者が書ける**。拒否しているのは
「実行内容そのもの」（前の 3 つ ── エンコーダーの指定も `parse_launch` へ生で補間される）と
「`OutputDirectory` の外へ書ける」（後の 2 つ）に当たるものだけで、**同じ性格の設定を
足したときに拒否リストへ載せ忘れると穴になる**（L1 の
`EveryPipelineTextRecorderSettingIsDenied` が拾えるのは名前がこの形の設定だけ）。さらに `GET /api/recorders/{id}/settings` は**無認証で**
拒否キーの値も返す（読み取りは絞っていない）。

「トークン所持者はローカル操作者と同等」「読み取りは LAN に見える」という前提でこうしてあり、
ルート [README.md](../README.md) の「Remote control from a browser」／
「リモート操作（ブラウザから）」に明記してある。
**この非対称を「バグ」として片側だけ直すと、README の記述と食い違う** ── 直すなら文書も対で直すこと。

### Mp4Probe.StartsOnASyncSample

`Mp4Probe.StartsOnASyncSample`（`stss` の先頭項目の検査）は**退行検出器ではなく不変条件の表明**である。これが緑であることを「この性質を壊す変更を検出できる」と読まないこと。
