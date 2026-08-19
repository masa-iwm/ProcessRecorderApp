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

**カメラのある機械で AOT 発行物に対し 1 回、次を目で確かめること**
（`docs/gpu-verification.md` と同じ「実行＋レポート往復」でよい）:

1. 録画中にカメラを抜く → `recorder.error` と
   `device.watch kind=camera provider=mfdeviceprovider monitor=yes` が出る
2. 挿し直す → `device.arrive kind=camera device='…'` が出て、
   `recorder.restart … wake=device-arrival` で復帰する（抜いてから 5 秒待たされない）
3. **抜いたまま 2 分放置**してから挿す → 詰み状態（パイプラインもバスも無い）から
   `recorder.restart … retrying the pipeline rebuild … wake=device-arrival` →
   `rebuild result=ok` で復帰すること。**ここが 60 秒の待ちだけで動いていないか**は
   `wake=device-arrival` の有無で見分ける
4. **抜き差しの後にデバイスの並びが壊れていないこと** ── パイプライン編集画面を開き、
   `monitor-index` を切り替えて解像度が実体と一致すること、カメラの
   `device-index` / `device-name` / `device-path` の組が実体と対応することを見る。
   プロバイダを started に保ったままにすると、ここが静かにずれる
   （理由は `docs/environment-facts.md`）。**復帰待ちのあいだだけずれうる**
   ── 復帰するか連鎖が終われば、リンガーの後にプロバイダを止めるので元へ戻る
5. モニターの側も 1 回 ── 監視対象のモニターのケーブルを抜き差しして、
   `device.watch kind=monitor provider=d3d12screencapturedeviceprovider monitor=yes` と
   `wake=device-arrival` が出ること

**利用者が別途入れた GStreamer に d3d12 プラグインが無い構成**も自動では踏めない
（開発機も CI もフル構成）。そのときは `device.watch … monitor=no` を出して
タイマーだけの復帰へ縮退するが、**その分岐を実際に流すテストは無い**。

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

### Mp4Probe.StartsOnASyncSample

`Mp4Probe.StartsOnASyncSample`（`stss` の先頭項目の検査）は**退行検出器ではなく不変条件の表明**である。これが緑であることを「この性質を壊す変更を検出できる」と読まないこと。
