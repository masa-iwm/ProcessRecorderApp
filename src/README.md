# ProcessRecorderApp: 実装ドキュメント

本ドキュメントは `src/` 配下のソリューション全体（5プロジェクト）を対象とした、実装者向けの
技術資料です。アプリの機能・使い方については、ルートの
[README.ja.md](../README.ja.md)（日本語）／[README.md](../README.md)（英語）を参照してください。

## ソリューション構成

`ProcessRecorderApp.slnx` の `src/` 配下は以下の5プロジェクトで構成されます
（ソリューションはほかに `tests/` の2プロジェクトを含みます）。

| プロジェクト | 役割 |
|---|---|
| `ProcessRecorderApp` | メインアプリ本体（エントリポイント、画面、CLI コマンド定義） |
| `SingleInstance` | 単一インスタンス制御（ランチャー/常駐ワーカー分離）・タスクトレイ常駐 |
| `GStreamer.GstSharpNet`（AssemblyName: `GStreamer`） | GStreamer による録画エンジン・プレビュー生成 |
| `Controls` | 再利用可能な WinUI コントロール（`NativeSwapChainPanel`、`PropertyGridView` など） |
| `Components` | 各プロジェクト共通の基盤（設定永続化、標準出力キャプチャなど） |

依存関係: `ProcessRecorderApp` → `SingleInstance` / `Controls` / `GStreamer.GstSharpNet` → `Components`。

## 要件と実装の対応

| 要件 | 実装 |
|---|---|
| .NET10 / C# / WinUI3 | `net10.0-windows10.0.19041.0`（動作対象OSの下限は `TargetPlatformMinVersion` `10.0.17763.0`）、Windows App SDK **2.3.1**、`UseWinUI` |
| MVVM 実装 | CommunityToolkit.Mvvm **8.4.2** |
| Native AOT発行対応 | `PublishAot=true` + `SelfContained=true`（アンパッケージ配布）。**これらの発行設定は `Properties/PublishProfiles/*.pubxml` にのみ存在し、`.csproj` には構成条件付きの `PublishAot` 等を置いていない**（Release構成に条件付けすると CI の Release ビルドがすべて AOT になり、ビルド時間が現実的でなくなるため）。`.csproj` にあるのは AOT 時の挙動を調整するプロパティ（`TrimMode=full` / `StripSymbols=true` / `OptimizationPreference` / `IlcMaxVectorTBitWidth` など）だけで、これらは発行時以外は無害 |
| 常時バッファリングによるイベント録画 | `GStreamer.GstSharpNet/EventRecorder.cs`（後述） |
| ライブプレビュー（D3D12 SwapChain） | `GStreamer.GstSharpNet/GstPreviewer.cs` + `Controls/Controls/NativeSwapChainPanel.cs`（後述） |
| 起動済インスタンスをActivate | `AppInstance.FindOrRegisterForKey` + `RedirectActivationToAsync`（`ShowWindow=true` を指定したコマンドのみ。素の起動・サブコマンドなしではActivateしない） |
| 引数付き起動で既存インスタンスに処理させる | `AppActivationArguments` をそのままリダイレクトし、`Activated` イベントで受信・解析 |
| 呼び出しプロセスは必ず終了、実処理インスタンスは終了しない（初回起動時も含む） | **ランチャー/常駐ワーカー分離アーキテクチャ**（後述） |
| 常駐ワーカー起動の排他制御 | 名前付き **Mutex** + 名前付き **EventWaitHandle**（後述） |
| タスクトレイ常駐・閉じる/最小化でトレイ格納 | **WinUIEx**（`WindowManager.IsVisibleInTray` 等、MIT License）を使用 |
| Win32 P/Invoke | **CsWin32**（`Microsoft.Windows.CsWin32`、MIT License）によるソース生成。使用箇所は `Components`・`GStreamer.GstSharpNet` のみ（後述） |
| コマンドライン解析 | **System.CommandLine 2.0.10**（MIT License）による解析（後述） |
| 常駐ワーカーでの処理失敗をランチャーの終了コードで識別 | 名前付き **EventWaitHandle** + **MemoryMappedFile** による結果通知（後述） |
| Variables 画面のキー/値グリッド | **WinUI.TableView 1.4.1**（MIT License） |
| 録画・プレビューエンジン | **GstSharp.Net**（`GstSharp.Net` / `GstSharp.Net.App` / `GstSharp.Net.Base` 1.28.1、GStreamer の .NET バインディング。nuget.org から取得する。後述「パッケージの取得元」） |
| en-US / ja-JP ローカライズ（OS表示言語に自動追従） | MRT Core（`.resw` + `resources.pri`）+ `x:Uid` + `Components/Localization.cs`（後述） |

---

## 録画エンジン（GStreamer.GstSharpNet）

本アプリの中核機能は、GStreamer をエンジンとした「常時バッファリング → 遡っての録画開始」
という**イベント録画**である。実装は主に `GStreamer.GstSharpNet/EventRecorder.cs` にある。

### 初期化（`Controller.cs`）

`Controller.StaticInitialize()`（`Program.Main` の常駐ワーカー初期化コールバックから呼ばれる）が、

- `Gst.App.GstApp.Initialize()`（ネイティブのロード + `gst_init` + App 型の登録）と
  `Gst.Base.GstBase.Initialize()`（`BaseSrc` 等の決定的な型登録）を呼ぶ
- **どこからロードされたか**を `activity.log` の `gst.runtime` に1行残す

ことで、GStreamer ネイティブライブラリをロードできるようにしている。
**在り処を決めるのはアプリではなくバインディングである**（下記）。

#### GStreamer の解決経路（GstSharp.Net のローダー）

配布形態が同梱・非同梱の2種類あるため、同梱物の決め打ちはできない。
**探索・ロード・混成の防止はすべて GstSharp.Net の `Gst.Interop.NativeLoader` が持つ**
── アプリ側のロケーターは無く、`Initialize()` にディレクトリも渡さない。
段は `Gst.Interop.GstInstallOrigin` の列挙子そのもので、上から順に試し、
本体を見つけた最初の段が勝つ。

| 段（`gst.runtime` の `selected=`） | 何を見るか |
|---|---|
| `ConfiguredSearchPath` | アプリが明示的に渡したディレクトリ。**本アプリは渡さない**ので選ばれない |
| `PathDirectory` | 元の `PATH` を順に走査し、本体を持つ最初のディレクトリ |
| `EnvironmentVariable` | `%GSTREAMER_1_0_ROOT_*%`（公式インストーラが設定する） |
| `Registry` | アンインストール情報から見つけた公式インストール |
| `DefaultInstallDirectory` | 公式インストーラの既定の導入先（ユーザー単位・機械単位） |
| `Msys2` | MSYS2 の MinGW ツリー |
| `BundledRuntime` | 同梱物 `<exe>\runtimes\{RID}\bin` |
| `ProcessSearchPath` | 最後の手段。ベアネームで OS のローダーに任せる（**ディレクトリを固定しない**） |

**`PATH` が最優先である意味**: 実行前に `PATH` へ置いた GStreamer は、レジストリに載った
インストールや同梱物より必ず優先される ── CI が MSYS2 の `ucrt64\bin` を `GITHUB_PATH` へ
足すだけで狙ったランタイムを踏めるのはこのためで、逆に「同梱物を配ったのに開発機の
GStreamer が使われる」という取り違えもここから起きる（どちらだったかは `gst.runtime` に出る）。
`Registry` / `DefaultInstallDirectory` が要るのは、**GStreamer をユーザー単位で
インストールするとインストーラが環境変数も `PATH` も設定しない**ため（1.28.6 で実測）。

**候補を全部 `PATH` に繋いではいけない。** 依存 DLL（`libglib-2.0-0.dll` 等）は
「読み込み元 DLL のあるディレクトリ」ではなく `PATH` の順で解決されるため、繋ぐと
「gstreamer は同梱物・glib は MSYS2」のような**混成**が起こりうる（症状はプラグインが
黙って blacklist されること）。ローダーは**勝った段のディレクトリ1つだけ**をピンし、
各モジュールを**そこから絶対パスで**ロードする。プラグインの依存解決のためにその `bin` を
`PATH` の先頭へ足すのもローダー側なので、**アプリは `PATH` を組み立てない**。
その代わり、`Initialize()` より前に `Gst.*` の API を1つでも呼ぶと、その呼び出しが
`gst_init` より前にネイティブを解決してピンしてしまう（初期化より前に走りうる経路は
`DebugLogEx.IsGstInitialized` で塞いである）。

**MSVC 版も MinGW 版と同じように選ばれる。** ファイル名は MinGW 版が
`libgstreamer-1.0-0.dll`、MSVC 版が `gstreamer-1.0-0.dll` と違うが、ローダーは両方の命名を
知っているので、どの段でも・**同梱物 `runtimes\{RID}\bin` でも** MSVC 版を読める。
どちらを読んだかは `gst.runtime` の `flavor=`（`MinGW` / `Msvc`）で分かる。

初期化に失敗したときは `activity.log` に **ERROR の `gst.runtime`**（ローダーが実際に試した
パスと例外）を
**MessageBox より先に**書く。この MessageBox はモーダルで、常駐ワーカーはメッセージループに
入る前にそこで止まる ── 誰も押さなければ `app.start` の1行しか残らない。
また `StaticInitialize` は `new App()` より前に走るので、`App.LogException` の
未処理例外ハンドラはまだ張られておらず、**この行が唯一の手がかりになる。**

### `EventRecorder`：2パイプライン構成とリングバッファ

1レコーダーにつき2本のパイプラインを持つ。

- **sinkパイプライン**（常時稼働）:
  `映像ソース → (D3D12変換/オーバーレイ) → tee → [プレビュー用 appsink] / [エンコーダー → h264parse → appsink name=sink]`
  （常時録画が有効なら 3 本目の枝 `appsink name=cont` が加わる。後述「常時録画」）
- **srcパイプライン**（録画中のみ稼働）:
  `appsrc name=src ! h264parse ! mp4mux faststart=true name=mux ! filesink name=file`

sink パイプラインの `appsink` に取り付けた **`new-sample` コールバック**
（`SetSimpleCallbacks(onNewSample:)`）がエンコード済み H.264 バッファを取り出し、
PTS付きで `ConcurrentQueue` の**リングバッファ**へ積み続ける。
`BufferDuration`（既定 `10_000` ms、`EventRecorderSettings.BufferDuration`）より
古いバッファは随時破棄する。

> **コールバックは枝のストリーミングスレッドで走る**（サンプルを取り出す専用スレッドは
> 1 本も持たない）。中では `TryPullSample(0)` で**空になるまで**汲み、空でも
> `FlowReturn.Ok` を返す ── `appsink` は 1 render につき 1 回しか呼ばないので、
> 1 回 1 枚にすると取りこぼし、`Eos` を返すと枝がそこで止まる。
> 例外も漏らさない（トランポリンが `FlowReturn.Error` へ変換し、サンプル 1 枚の失敗で
> パイプラインごとエラー停止する）。

録画開始（`IsRecording = true`）の**最初の1回だけ**、リングバッファ全体を直近のIフレームから
`appsrc`（srcパイプライン）へ流し込むことで、「録画開始前」の映像を含んだ MP4 が生成される
（`PushRecordBuffer` / `isIframeFound`）。以降は新規バッファのみを逐次流し込む。


#### パラメータセット（SPS/PPS）は全 IDR の直前で繰り返す（重要）

sink パイプラインのエンコーダー直後に **`h264parse config-interval=-1`** を置き、
出力キャップスに **`alignment=au`** を明示している。

中核契約は「録画は任意の瞬間にストリームの途中から開始できる」ことであり、
**その再開点には SPS/PPS が無ければならない**。リングバッファには数秒分しか残らないため、
ストリーム先頭で1回だけ送られたパラメータセットは録画開始時には既に捨てられている。
`config-interval=-1` は h264parse が保持しているパラメータセットを
**全ての IDR の直前に再挿入する**。

これが無いと、パラメータセットを繰り返さないエンコーダーでは src 側の `h264parse` が
全スライスのヘッダを解釈できず、`broken/invalid nal ... will be dropped` として
**全 NAL を捨てる**。エラーにはならないので、中身の無い MP4 が黙って残る。
NVIDIA 機の `nvh264enc` で実際に発生し、本対応で解消することを実機で確認済み。
エンコーダーがヘッダを繰り返すとは限らない ── `x264enc` / `openh264enc` / `mfh264enc` /
`d3d12h264enc` / `qsvh264enc` ではたまたま成立するだけで、暗黙の前提にはできない
（`x264enc` はキーフレーム10個に対し SPS/PPS を10回出すことを実測）。

`alignment=au` を明示するのは、`PushRecordBuffer` とリングバッファの PTS 退避が
「1バッファ＝1フレーム」を前提としているため。`nal` アラインメントに解決されると
バッファが NAL 単位になり、退避計算と I フレーム検出の前提が崩れる。

> 副次的な利点として、`h264parse` が挟まることで `avc` しか出せないエンコーダーでも
> 下流の `byte-stream` 要求を満たせるようになり、候補の互換性が広がる。

#### `appsrc` のキャップスは最初のサンプルから設定する

録画側のコールバックは最初のサンプルを取り出した時点で、`sample.GetCaps()`（＝sink 側で
**実際にネゴシエートされた**キャップス）を `appsrc` に設定する（1回だけ）。

これを行わないと、`h264parse` は H.264 エレメンタリストリームの框組み
（`stream-format` / `alignment`）を typefind で推測するしかない。推測が外れると
**全 NAL が `broken/invalid nal ... will be dropped` として捨てられ、
エラーにはならないまま中身の無い MP4 が出来上がる**
（NVIDIA 機の `nvh264enc` で実際に観測）。

> `_appSink.GetCaps()` を使ってはいけない ── これは appsink に設定された
> （テンプレート由来でしばしば `ANY` な）キャップスであって、ネゴシエート結果ではない。

### 常時録画（`ContinuousRecording`）

イベント録画とは別に、**別のフレームレート・別のエンコード設定で回り続け、
一定時間ごとにファイルが切り替わる**録画。事前バッファは効かない（設計どおり）。
レコーダーごとに有効／無効を切り替える。

`tee` の 3 本目の枝としてイベント録画と**同じキャプチャを共有する**
（`ContinuousBranch.Build`。キャプチャは 1 回で済み、2 本目のデバイスを開けない
ソースでも常時録画ができる）。

```
tee name=t
  ! [プレビュー]
 t. ! queue ! エンコーダー ! h264parse ! appsink name=sink        ← イベント録画
 t. ! queue leaky=downstream ! [videorate ! caps] ! [スケール ! caps]
      ! エンコーダー ! h264parse ! appsink name=cont async=false  ← 常時録画
```

枝の 3 つの決定と根拠:

- **`appsink name=cont` の `async=false` は必須。** sink が preroll を待つと、低い
  フレームレートのときこの枝が `PLAYING` 到達を握る ── `PlayingStateTimeoutMs` の
  doc が名指ししている唯一の誤検出形（低 fps × 出力の遅いエンコーダー）が常時録画そのもの。
  代わりに「枝が 1 フレームも出さない」が無音にならないよう、
  `ContinuousFirstSampleBudget`（フレームレートから逆算）を超えたら
  `ContinuousLastError` に出す。
- **常時枝の `queue` は `leaky=downstream`。** 詰まったときに `tee` を止めて録画を優先するのは
  **イベント録画の枝の役目**であり、**常時録画がイベント録画を道連れにしてはならない**。
  バイト・時間の上限を外すのはプレビュー枝と同じ理由（解像度に依存させない）。
- **`videorate` は `ContinuousFramerate` が空でないときだけ入れる。**
  `videorate`（`libgstvideorate.dll`）は**同梱ランタイムに入れてある**が、
  利用者が別途入れた GStreamer には無いことがある。無条件に書くと、
  **フレームレートを変えていない構成まで巻き添えで初期化に失敗する**。
  要求されたのに要素が無い場合は、`ParseLaunch` の
  `no element` ではなく「`videorate` が無い」と名指しで失敗させる
  （`EventRecorder.ResolveContinuousEncoder`）。この整合は
  `ContinuousRuntimeDependencyTests`（L1）が固定している。

#### 解像度の上書きには「上流の固定」が要る（重要）

拡縮そのものは変換段で行う（D3d12 は `d3d12convert`、System は `videoscale`。どちらも同梱済み）。
**問題は、枝の capsfilter が要求する幅・高さが上流へ伝播することである。**

拡縮できる要素は「素通し（passthrough）」を最も好むので、下流の固定された大きさを
**そのまま上流への希望として差し出す**。`tee` はすべての枝の希望を交差させるため、
その固定値が `tee` を越えてソースまで届き、ソースが任意の大きさを出せる場合
（`d3d12screencapturesrc` は `width:[1,2147483647]` を名乗る）**ソースが小さい方を選ぶ**
── プレビューもイベント録画も一緒に縮む。出来上がった MP4 は「妥当」なままなので、
大きさを直接読む以外に検出できない。

実測（`docs/environment-facts.md` に記録）:

| 構成 | プレビュー枝 |
|---|---|
| 3840x2160 のキャプチャ ＋ 常時枝 960x540 | **960x540**（本線まで縮む） |
| ソースの caps を 1920x1080 に固定 ＋ 常時枝 960x540（D3d12） | **960x540**（`tee` の手前の `d3d12convert` が吸収する） |
| 上に加えて **`tee` の手前の capsfilter も 1920x1080 に固定** | **1920x1080**（正しい） |

したがって製品の規則は 2 つ:

1. **ソースの caps が幅・高さを固定していないときは、解像度の上書きを捨てる**
   （`ContinuousBranch.SourceSizeIsPinned`）。理由と直し方（`SrcPipeline` の caps に
   `width` / `height` を書く）を `ContinuousLastError` と `recorder.continuous-init fail` に出す。
   **常時録画のために本線の録画を壊すことは許されない**ので、捨てるのは上書きの方である。
2. 枝で拡縮するときは **`tee` の手前も同じ大きさで固定する**（`BuildSinkPipeline` の
   `pinnedResolution`）。D3d12 経路は `tee` の手前に `d3d12convert` が居るので、
   ソース側の固定だけでは足りない。

> **画面キャプチャの解像度は、パイプライン編集ダイアログの解像度欄から選べる。**
> 選択肢は接続モニターの**物理ピクセル**（`GstIntrospect.GetMonitorResolutions`。
> `monitor-index` が選ばれていればそのモニターの値が先頭に出る）。
>
> **列挙は `d3d12screencapturedeviceprovider` に任せる**（自前の DXGI 走査は持たない）。
> 並びはプラグイン自身が `EnumAdapters1` × `EnumOutputs` を平坦化した順で、これは
> `d3d12screencapturesrc` が `monitor-index` を解く走査そのものである
> （`d3d11screencapturesrc` も同じ並び）。**読めなかったモニターも空文字で席を残す**
> ── 詰めると以降の番号が1つずつずれ、直そうとしている取り違えを作ってしまう。
>
> **大きさはデバイスの caps（`width` / `height`）が運ぶ。** キャプチャ側がこれから出す
> 大きさそのものなので、**プロセスの DPI 認識に依存しない** ── 自前の Win32
> （`EnumDisplaySettings` / `GetMonitorInfo`）で取っていた頃の DPI 仮想化の罠は
> 経路ごと無くなった。デバイスの properties の `desktop.coordinates` で代用しては
> いけない ── あちらが仮想化された値の方である（175% の機械で 2194x1234 と 3840x2160。
> 物理ピクセルを持つのは `display.coordinates`）。

> **`show-cursor=true` はプロセスごと落としうる（上流の欠陥）。**
> カーソル形状を組み立てる `PtrInfo::BuildTexture` の中で `abort()` に至る。
> **MinGW 版の GStreamer で起き、MSVC 版では起きない**（`gst-launch` の1行で再現。
> アプリは関与していない）。**アプリ側では捕捉できない。** 既定の `false` のままにすること。
> **同梱ランタイム（MinGW 版は `gstreamer-runtime-v1.28.6-r2` 以降、MSVC 版は
> `gstreamer-runtime-msvc-v1.28.6` 以降）にはこの修正を当てた d3d12 プラグインを積んでいる**が、**非同梱配布は利用者の GStreamer をそのまま使うので
> 従来どおり**（修正は上流のリリースにはまだ入っていない）。
> **カーソルを写したいなら `d3d11screencapturesrc` を選べる**
> （パイプラインの編集ダイアログのソース一覧にある）。**上流の D3D11 側は
> 同じ処理が元から正しい**。ただしこの要素は**拡縮できない** ──
> caps でモニターの実寸以外を要求すると `Internal data stream error` になる。
> 詳細と、MSVC 版へ替えても「隠れるだけ」でありうる理由は
> [docs/environment-facts.md](../docs/environment-facts.md)、
> 改変版の中身は [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

#### フレームレートの上書きにも「上流の固定」が要る

`videorate` の要求も同じく `tee` を越えて伝播する。実測: ソースの caps に `framerate` を
書かずに枝を `5/1` にすると、**プレビュー枝も `5/1` になった**（本線ごと 5fps に落ちる）。
したがって規則は解像度と同じ ── **ソースの caps が `framerate` を固定していなければ
上書きを捨てる**（`ContinuousBranch.SourceFramerateIsPinned`）。

ただし固定に必要なのは**ソース側の caps だけ**である。解像度と違い、`tee` の手前の
`d3d12convert` はレートを変えられないので吸収しない（実測: ソース `framerate=15/1` 固定で
`tee` は 15/1 のまま、枝だけ 5/1）。カタログのソースはすべて `framerate` の caps 欄を
持つので、パイプライン編集ダイアログで組んだ構成では既に固定されている。

#### フレームレートは分数で書く

caps の `framerate` は `GstFraction` なので、`5` と書くと `(int)5` として読まれ、
**どの要素も扱えない caps になってリンクに失敗する**
（実測: `could not link videorate0 to ..., can't handle caps video/x-raw, framerate=(int)5`）。
設定には素の数字も書けるが、枝を組む前に `ContinuousBranch.TryNormalizeFramerate` が
`5` → `5/1` へ正規化する。読めない値は上書きを捨てて理由を残す。

#### 分割（セグメント）

**`splitmuxsink` は使わない**（同梱ランタイムに `libgstmultifile.dll` が無い）。
`ContinuousRecorder`（C#）が `appsink name=cont` から H.264 のアクセスユニットを引き、
セグメントごとに `appsrc ! h264parse ! mp4mux ! filesink` を作り直す。

切り替えは
(1) 次のセグメント用パイプラインを先に `PLAYING` にし、
(2) 旧パイプラインへ EOS を送って**排出はプールスレッドへ逃がし**、
(3) 当のキーフレームは新しい方へ押し込む
── フレームの欠落はゼロで、`splitmuxsink async-finalize` と同じことを既存の要素だけで行う。
排出中のパイプラインは 2 本までに制限する（`ContinuousRecorder.MaxFinalizersInFlight`）。

**切り替えるのはキーフレームだけ**（`SegmentRotationRules.ShouldRotate`）。
非キーフレームで切ると次のセグメントの先頭が P フレームになり、書き出し側の
I フレームゲートが次の I まで捨てる ── そのぶんの映像が丸ごと欠ける。
したがって**GOP が長いとその分だけ超過する**。自動選択のエンコーダーは
`EncoderCatalog.GopSize` が入るので数フレーム分で収まるが、
`ContinuousEncodingProperties` を手書きするなら GOP を必ず固定すること。
超過は `SegmentRotationRules.IsOvershooting`（「2 倍」と「＋30 秒」の厳しい方）で
検出して `continuous.overshoot` に残す。

セグメントの書き出しに **`faststart=true` は付けない** ── faststart は EOS のあとに
ファイル全体を書き直すので、数分ごとの切り替えでそれをやると分割のたびに I/O が跳ねる。
常時録画のセグメントは書庫であって、先頭からのシークの即応性は要らない。

**ファイル名はセグメントごとに展開し直す**（`{Now}` が毎回変わるので自然に一意になる）。
それでも直前のセグメント、**または まだ排出中のセグメント**と同じ名前になった場合は
通し番号を足す ── 排出は非同期なので、同じ名前で `filesink` を開くと
**まだ書き終わっていないファイルを切り詰める**（`{Now:HHmm}` だけのテンプレートで起こりうる）。

**常時録画側の失敗はイベント録画を巻き添えにしない**（隔離契約）。枝は同じ `ParseLaunch` に
同居するので、`InitializeCore` は**2 段**で組む ── 枝つきで失敗したら、
同じエンコーダー候補で**枝なしをもう一度**試し、成功したら `IsInitialized` は立てたうえで
理由を `ContinuousLastError` と `recorder.continuous-init fail` に残す。
常時録画側のエンコーダーに独自の候補チェーンは持たせない（先頭 1 件のみ）。

| 設定 | 既定 | 意味 |
|---|---|---|
| `ContinuousRecording` | `false` | 常時録画を行うか。**反映は `Initialize` で効く**（枝は sink パイプラインの文字列そのもの） |
| `ContinuousFramerate` | 空欄 | 常時録画のフレームレート（`5/1`）。空欄ならイベント録画と同じ。**空欄でないときだけ `videorate` を入れる** |
| `ContinuousResolution` | 空欄 | 常時録画の解像度（`1280x720`）。空欄ならイベント録画と同じ |
| `ContinuousEncodingProperties` | 空欄 | 常時録画のエンコーダー起動文字列。空欄なら自動選択。手書きするなら GOP を固定すること |
| `ContinuousFilenameTemplate` | `{Now:yyyyMMdd_HHmmss}_{Name}_c{Segment}.mp4` | セグメントごとに展開し直す（`{Now}` は毎回変わる） |
| `ContinuousSegmentSeconds` | `600` | 分割間隔（秒）。**5 未満は 5、86400 を超える値は 86400** として扱う（セグメントが長いほど異常終了で失う範囲が広がる。書き込み中のファイルは常に未確定） |

読み取り専用の観測値は `IsContinuousRecording` / `ContinuousLastFilename` /
`ContinuousLastError` / `ActualContinuousEncodingProperties` / `ContinuousSegmentCount`。

`activity.log` のイベント名は `continuous.start` / `continuous.finalize` /
`continuous.finalize backlog` / `continuous.overshoot` / `continuous.error` / `continuous.stop` /
`continuous.leak`（一覧は後述の activity.log の表）。

**セグメントも既存の自動削除の対象**（`.mp4` を更新時刻で掃く）。書き込み中のセグメントは
更新時刻が常に最新なので消えない。ただし保持は日単位・掃除間隔は最短 1 時間で、
**その間のディスク枯渇に対する防御は無い**（容量監視はこのアプリには無い）。
### 録画種別とパイプライン文字列

`EventRecordingType { System, D3d12 }`（既定 `D3d12`）で、GPU経路とCPU経路を切り替える。

| 種別 | 変換・オーバーレイ | エンコーダー |
|---|---|---|
| `D3d12` | `d3d12upload/convert` → `dwriteclockoverlay` | `EncoderCatalog` が実機に存在するものから自動選択 |
| `System` | `dwriteclockoverlay` | 同上 |

> **`clockoverlay`（pango）へ戻してはいけない。** `clockoverlay` は `libgstpango` の要素で、
> 描画が pangocairo → cairo → Direct2D と流れる（cairo が DirectWrite バックエンドで
> ビルドされている場合）。この経路は**複数スレッドから同時に叩かれると D2D の内部キャッシュを壊し**、
> **レコーダー2本で録画するだけでワーカーがアクセス違反で落ちる**（実測済み）。
> `dwriteclockoverlay` は cairo を経由せず DirectWrite を直接使う。
> 詳細と決定的な再現手順は [docs/environment-facts.md](../docs/environment-facts.md)。

### H.264 エンコーダーの自動選択（`EncoderCatalog`）

エンコーダーを決め打ちにすると、その要素が無い機械では `Initialize()` が失敗して
**録画そのものができない**。そのため `GStreamer.GstSharpNet/EncoderCatalog.cs` が実機に
存在するエンコーダーを優先順に並べ、`EventRecorder.Initialize()` が上から順に実際に試す。

優先順位:

| 種別 | 優先順位 |
|---|---|
| `D3d12` | `d3d12h264enc` → `qsvh264enc` → `nvd3d11h264enc` → `nvh264enc` → `nvautogpuh264enc` → `amfh264enc` → `mfh264enc` → `openh264enc` → `x264enc` |
| `System` | `x264enc` → `openh264enc` → `mfh264enc` |

設計上の要点:

- **プローブは `Gst.ElementFactory.Find`**。`Controller.StaticInitialize()` の末尾
  （`Gst.App.GstApp.Initialize` の後）で1回だけ実行する。GStreamer の GPU 系プラグイン
  （qsv / d3d12 / nvcodec / amfcodec）は、プラグイン自体はロードされても**対応ハードウェアが
  無ければ要素ファクトリを登録しない**ため、`Find` が null を返すことがそのまま
  「この実機では使えない」という正しい判定になる。
- **`NeedsSystemMemory`**。`parse_launch` は変換要素を自動挿入しないため、`D3d12` 経路で
  `EncoderCatalog` の `NeedsSystemMemory: true` の候補（`x264enc` / `openh264enc` /
  `mfh264enc` / `nvh264enc` / `nvd3d11h264enc` / `nvautogpuh264enc` / `amfh264enc`）を
  使う場合は、その手前に `d3d12download ! videoconvert !` を挿入する。
  **行き先を `video/x-raw(memory:SystemMemory)` で固定してはいけない** ── 明示の
  フィーチャは `memory:D3D11Memory` と一致せず、**D3D11 を受けられるエンコーダー相手でも
  毎フレーム GPU→CPU の往復を強制する**（NVIDIA 実機で実測。固定を外すと
  `nvd3d11h264enc` 相手では `memory:D3D11Memory` で折り合う）。`videoconvert` の caps は
  `video/x-raw(ANY)` なので交渉を妨げず、形式変換が要るときだけ働く
  （実測: `openh264enc` 相手だと NV12 → I420 になる）。
  これが無いと `ParseLaunch` が「リンクできない」で失敗する ── **まさに直したかった
  AMD / NVIDIA 機で壊れる経路**。
- **「存在する」≠「動く」**。候補は上から順に `ParseLaunch` → `SetState(Playing)` まで実際に試し、
  失敗したら `Close()` して次へ進む。実測では、メモリフィーチャ不一致・未知のプロパティ・
  要素の不在はいずれも **`ParseLaunch` の時点で同期的に失敗**する（`SetState` まで到達しない）。
- **プロパティ付き候補は、失敗したらプロパティ無しでもう一度試す**（`ExpandAttempts`）。
  実機未確認の GPU エンコーダーでプロパティ名や単位が違っていても、そのエンコーダー自体を
  取りこぼさないための保険。
- **プロパティの単位はエンコーダーごとに違う**。`x264enc` / `mfh264enc` の `bitrate` は
  **kbit/sec** だが `openh264enc` は **bit/sec**。数値をコピーすると 2000 bit/sec（＝2kbps）に
  なって実質壊れるため、`FactoryName` とは別に `LaunchString` を持たせている。
  実機で確認できていない GPU 系エンコーダーには、後述の GOP 長以外のプロパティを付けていない。

#### GOP 長は `BufferDuration` より十分に短くなければならない（重要）

全候補に GOP 長を明示している（`gop-size` / `x264enc` は `key-int-max`）。
**これは画質設定ではなく、アプリの中核契約を成立させるための制約**。

録画開始時、`PushRecordBuffer` は最初の I フレームが見つかるまでバッファを捨て続ける
（[EventRecorder.cs](GStreamer.GstSharpNet/EventRecorder.cs) の `isIframeFound`）。
リングバッファ（`BufferDuration`）の中に I フレームが1枚も無いと、
**事前バッファが丸ごと捨てられたうえ、次の I フレームが来るまでのライブ映像まで失われる**。
つまり「録画ボタンを押す前の映像が残る」というアプリの中核価値が、
エラーも警告も無いまま静かに消える。

**GOP 長はフレーム数ではなく「秒」で決める。** `EncoderCatalog.TargetKeyframeIntervalSeconds`
（2 秒）を実際のフレームレートに掛けたものが `gop-size` になる ── 本線はソースの caps の
`framerate`、常時録画の枝は `ContinuousFramerate`（無ければソース側）から取る。
既定の 30fps なら 60 フレーム、15fps なら 30 フレーム、常時枝が 5fps なら 10 フレーム。

**フレーム数を固定してはいけない。** 実測（同じ 60 フレーム固定で走らせた場合）:

| 経路 | フレームレート | GOP 間隔 | 起きたこと |
|---|---|---|---|
| 常時録画の枝 | 5fps | 12秒 | 5 秒のセグメントが**キーフレーム待ちで 10 秒へ**（`continuous.overshoot`） |
| イベント録画 | 15fps | 4秒 | 事前バッファ 3 秒の構成で、押してから 4 秒ぶん録画が始まらない（3 秒の窓のうち残ったのは 1.467 秒） |

以前の実測（15fps / `BufferDuration`=2000ms / 録画窓3秒 / `tools/Verify-GpuEncoders.ps1`）:

| エンコーダー設定 | GOP 間隔 | 生成尺 |
|---|---|---|
| `qsvh264enc gop-size=64` | 4.27秒 | **1.067秒** |
| `x264enc key-int-max=30` | 2.0秒（＝バッファ長と同値） | 3.2秒 / 5.067秒（GOP の位相で変動） |
| `mfh264enc gop-size=30` | 2.0秒 | 2.0〜2.4秒 |
| `gop-size=15` | 1.0秒 | 4.3〜6.1秒（＝録画窓＋事前バッファ） |

`EncoderCatalogResolveTests` が、**目標間隔からのずれが 1 フレーム未満であること**と
**既定のフレームレートで GOP 2 本ぶんが既定の事前バッファに収まること**を縛る。

> **`BufferDuration` を GOP 間隔（2 秒）の 2 倍より小さくすると、事前バッファは効かなくなる。**
> 既定は 10 秒なので通常は問題にならない。

> `EncodingProperties` / `ContinuousEncodingProperties` で手動指定する場合は
> **自分で GOP を固定すること** ── 指定した文字列がそのまま使われるので、
> ここで説明した逆算は効かない。長い GOP を指定すると事前バッファもセグメント分割も
> 効かなくなるが、アプリは警告を出さない。
- 採用されたエンコーダーは、読み取り専用の `ActualEncodingProperties` に出る（UI 改修不要）。
  加えて `Console.Error` へ `gst.encoders` / `gst.encoder selected` / `gst.encoder candidate-failed`
  の各行を出力する（`StandardStreamRedirector` が捕捉し、アプリ内 Log 画面と
  `AppSettings.DebugLogFile` の両方へ届く）。`DebugLogEx.Log` は `gst_debug_log` 経由で
  `GST_DEBUG` 未設定だと何も出さないため、**選択結果の可視化には使えない**。

優先順位の上書き:

- `AppSettings.PreferredH264Encoder`（要素ファクトリ名。空欄で自動選択）を指定すると、
  その要素が実機に存在する場合のみ先頭へ移動する。存在しない場合は**黙って自動選択へ
  フォールスルー**する（設定ミスで録画が一切できなくなる方が有害なため）。
  カタログに無い要素名でも、実機に存在すれば尊重する。
- レコーダーごとの `EncodingProperties`（自由形式の文字列）を指定した場合は、
  **その1件のみが候補**となりフォールバックしない（手動指定を常に優先し、
  黙って別の設定で録画しない）。失敗時は `IsInitialized=false` のままとなる。

### GPU エンコーダー経路の実機確認

開発機に GPU が無いため、GPU 依存エンコーダーの最終確認は別 PC で
`tools/Verify-GpuEncoders.ps1` を1回実行して行う（無人実行・レポート往復）。
何に実機が要るか（大半は WARP で足りる）・スクリプトの使い方・レポートの読み方・
改造時の制約は [docs/gpu-verification.md](../docs/gpu-verification.md)。
映像ソース（`SrcPipeline`）は GStreamer のパイプライン文字列そのものを保持しており、画面
キャプチャ(`d3d12screencapturesrc` / `d3d11screencapturesrc`)・カメラ(`mfvideosrc`)・テストパターン
(`d3d12testsrc`/`videotestsrc`)などを組み立てて指定する（UI 上は Pipeline Builder ダイアログ、
`ProcessRecorderApp/Views/PipelineBuilderDialog.xaml` 経由。ソースの候補・プロパティ定義は
`GStreamer.GstSharpNet/SrcPipelineBuilder.cs`、カメラ/モニタの実機列挙は `GstIntrospect.cs`）。

### モニタの指定（`monitor-device-path`）

画面キャプチャ要素（`d3d12screencapturesrc` / `d3d11screencapturesrc`）が持つ選択手段は
`monitor-index` と `monitor-handle` の 2 つだけで、**パスを受け取るプロパティは無い**。
`monitor-index` は位置依存で、前のモニタを抜くと番号が詰まって別の画面を録り始める。
`monitor-handle` は実行時の `HMONITOR` なので設定に保存できない。一方でデバイスプロバイダは
各モニタに `device.path`（`\\?\DISPLAY#...` の形）を付けており、これは物理モニタ＋端子ごとに
安定している（上流自身が再 probe 後の同一性判定に使っている）。

したがって**パスで保存し、パイプラインを組む時点でハンドルへ解決する**のが唯一の道になる。
`monitor-device-path` は `SrcPipelineBuilder` のカタログにだけ在る**アプリの擬似プロパティ**で、
実要素には存在しない ── 残したまま `gst_parse_launch` へ渡すと `no property` で落ちる
（カメラの `device-path` と発想は同じだが、あちらは実要素のプロパティである点が違う）。

解決は `MonitorSelection.Resolve`（純粋関数。規則は L1 の `MonitorSelectionTests` が全数を縛る）で、
`EventRecorder.InitializeCore` の**エンコーダー候補ループより前で 1 回だけ**行う ──
候補ループの中で解くと、同じ失敗が候補の数だけ繰り返される。モニタの列挙
（`GstIntrospect.GetMonitors`）は**パス指定が在るときにしか走らせない**
（`MonitorSelection.RequiresMonitors`）── `InitializeCore` はレコーダーごと・復帰のたびに
走るので、無条件に列挙するとテストソースのレコーダーまでデバイスプロバイダを起こし続ける。

| 状況 | 結果 |
|---|---|
| 指定が無い | 文字列を 1 文字も変えない |
| 一致するモニタが在り、ハンドルも読めた | `monitor-device-path=…` を `monitor-handle=…` へ置き換え、`monitor-index` を取り除く（パス指定が番号より優先） |
| モニタは列挙できたのに一致しない | **初期化を失敗させる**（`LastError` と `recorder.init fail` に指定されたパスが入る） |
| モニタが 1 台も列挙できない | パスだけ取り除いて `monitor-index` を残し、**警告**（`recorder.warning` と `LastError` の `[warning]` 接頭辞） |
| 一致したがハンドルを読めない | 同上（縮退＋警告） |

> **一致しないときに番号へ縮退させてはいけない。** 指定されたモニタが今つながっていない
> のだから、番号で代用すると**黙って別の画面を録り始める** ── 直そうとしている取り違えを
> 自分で作ることになる。失敗させれば復帰の連鎖（`TryScheduleDeviceRebuild` は要素名で種別を
> 引く）が拾い、モニタが戻れば作り直される。
> **逆に、列挙が空のときは失敗させてはいけない** ── 利用者の GStreamer に d3d12 が
> 無いだけの機械で、番号なら録れるのに 1 フレームも録れなくなる。縮退したことは画面からは
> 分からないので、そのときは黙らせずに警告として残す。

> **書き換えるのは対象のトークンだけ。** `Parse` → `Assemble` の往復では実現できない ──
> あちらはカタログに載っている caps とプロパティしか書き戻さず、ソースより後ろの中間要素
> （`! identity ! videoconvert` 等）を丸ごと落とす。そのため `MonitorSelection` は
> `SplitOutsideQuotes` でソース要素のセグメントだけを取り出し、解析と同じ `KeyValueRegex` で
> 対象のトークンを置き換え、残りは原文のまま返す。

解決後の文字列は `ActualSrcPipeline`（「実際に使った値」）に出る。編集ダイアログの選択肢は
`GstIntrospect.GetMonitors()` の `Path` で、**解像度欄と同じ列挙の射影**である ──
列挙を 2 回走らせると、その間に構成が変わったときに解像度の行とパスの行が別のモニタを
指しうる。列挙の結果そのものは `monitor.devices`（`count=` / `withPath=`）に出る。

### カメラ設定（`mfvideosrc` のフォーカス・明るさ等）

レコーダー設定 `CameraControls`（例 `brightness=128;focus=30;focus-auto=false`）で、
カメラのフォーカス・明るさ・ホワイトバランス等を指定する。編集は PropertyGrid の
「…」ボタン（`[ValueBuilder("GstCameraControls")]`）から開く専用ダイアログ
（`Views/CameraControlDialog.xaml`）。

> **GStreamer 側に手段が無い。** 実機の 1.28.6 で `gst-inspect-1.0 mfvideosrc` を確認したところ、
> 公開プロパティは `device-index` / `device-name` / `device-path` / `blocksize` / `num-buffers`
> などだけで、**カメラ制御のプロパティは 1 つも無く、`Implemented Interfaces:` の節そのものが
> 出ない**（＝ `GstColorBalance` も `GstPhotography` も未実装。対照として `videobalance` は
> `GstColorBalance` を出す）。`ksvideosrc` も同じで、しかも `libgstwinks.dll` は
> 同梱ランタイム（`licenses/third-party/COMPONENTS.tsv` / `COMPONENTS-msvc.tsv`）に無い。
> したがって **Windows のカメラ制御 COM（`IAMVideoProcAmp` / `IAMCameraControl`）を
> 自前で叩くのが唯一の道**である。

構成は 3 層:

| 層 | ファイル | 役割 |
|---|---|---|
| 書式（純粋） | `GStreamer.GstSharpNet/CameraControlSettings.cs` | `Parse` / `Format` / **`Merge`**、候補 17 項目のカタログ。**COM に触れない** |
| COM | `GStreamer.GstSharpNet/CameraControl.cs` | MF のデバイスソースから制御インターフェイスを取り出して `GetRange` / `Get` / `Set`。デバイスパスの解決（`ResolveDevicePath`）もここ |
| スレッド | `GStreamer.GstSharpNet/CameraControlWorker.cs` | セッションを**専用スレッド 1 本の上だけ**で開き・使い・畳む |
| UI | `ProcessRecorderApp/Views/CameraControlDialog.xaml`(.cs) + `ViewModels/CameraControlViewModel.cs` | 対応項目だけをスライダーで出す |

**自動テストできるのは書式の層だけ**（`CameraControlSettingsTests`）── 開発機にカメラが無いので
COM 経路は動かせない（[docs/coverage-gaps.md](../docs/coverage-gaps.md)）。

決定と根拠:

- **17 項目を個別のレコーダー設定にしない。** レコーダー設定を 1 つ増やすたびに手書きのミラーが
  4 箇所（`RecorderSettingsMirrorTests`）＋ E2E の `RecorderSpec` に要るので、
  17 個なら 85 箇所になる。`SrcPipeline` と同じ「そのまま持つ文字列」にする。
  **`[ReadOnly(true)]` は付けない** ── 手で書ける道を残す。
- **COM は CsWin32 に生成させる**（`NativeMethods.txt` へ追記。`NativeMethods.json` の
  `allowMarshaling: false` により `lpVtbl` を持つ構造体として生成される ＝ AOT 安全）。
  手書き vtable にしない理由は 2 つとも実際に踏みうる形である:
  `GetRange` / `Set` / `Get` の C の `long` は **4 バイト（C# の `int`）**で、
  `nint` や `long` にするとスタックの隣を書き潰して後から無関係な場所で落ちる。
  IID も `IAMVideoProcAmp` が `C6E13360-…`、`IAMCameraControl` が `C6E13370-…` と 1 文字違い。
- **`QueryInterface` と `IMFGetService` の両方を試す。** 直接の QI が通らない実装があり、
  Windows の作法は `IMFGetService::GetService(MF_MEDIASOURCE_SERVICE, …)`（KSPROXY 経由）。
  片方だけにすると、通るカメラと通らないカメラで挙動が割れる。
- **メディアソースは解放の前に `Shutdown` する。** MF がそう求めており、省くと
  ワーカーキューと Frame Server のハンドルがプロセス終了まで残る ── 適用は初期化のたびに
  走るので、自動復帰が繰り返される環境では積み上がり、カメラが「使用中」のままになりうる。
  ただし取り出した制御インターフェイスはソースが生きている前提なので、
  **`TryOpen` の場では畳めない** ── 所有権を `CameraControlSession` へ渡し、
  `Dispose` で「制御 IF を解放 → ソースを `Shutdown` → `Release`」の順に畳む。
- **COM は UI スレッドで触らない。** `MFCreateDeviceSource` はデバイスを実際に起動するので、
  占有中・低速ドライバでは数秒返らない ── ダイアログのコンストラクターで同期に呼ぶと、
  「…」を押してから画面が出るまでウィンドウ全体（プレビュー描画・録画ボタンを含む）が固まる。
  `CameraControlWorker` が**専用スレッド 1 本**を持ち、開く・読む・設定する・畳むのすべてを
  その上で行う（スレッドプールにしないのは、開いたスレッドと `Set` するスレッドが
  別になりうるため ── ポインタを 1 スレッドに閉じる制約が崩れる）。
  ビューモデルの生成は `CameraControlViewModel.CreateAsync` だけを使い、
  行の読み出しは `ReadAll()` で**1 往復にまとめる**。
- **デバイスは `device-path`（MF のシンボリックリンク）で開く。**
  `device-name` / `device-index` からの逆引きには頼らない ── `mfdeviceprovider` の並びと
  MF の列挙順が一致する保証はどこにも無い。
  **解決規則は `CameraControl.ResolveDevicePath` の 1 箇所**にあり、適用側
  （`EventRecorder.ApplyCameraControls`）と編集ダイアログ側（`MainPage.BuildCameraControlsAsync`）が
  同じものを使う ── 分けて書くと**ダイアログで触るカメラと初期化時に設定が当たるカメラがずれる**
  （入力に `ActualSrcPipeline` を見るか否かで実際に食い違っていた）。
  入力は**実際に動いている構成を優先**する（`EventRecorder.CameraSourcePipeline`）。
  そのため **`device-path` を `SrcPipelineBuilder` の `mfvideosrc` カタログに載せてある**
  ── 載せないと、`Parse` は読めても `CarryOver` とダイアログの行が
  カタログ定義のプロパティしか通さないので、**パイプライン編集ダイアログを一度開いて OK した
  時点で黙って落ちる**（そしてカメラ設定が効かなくなる）。
  値の選択肢は `GstIntrospect.GetVideoSourceDevices()` の `Path`。
  **列挙は `DeviceProviderFactory.GetByName("mfdeviceprovider")` を直接使う**
  ── `DeviceMonitor` は `ksvideosrc` など他のプロバイダーも列挙するので同じカメラが重複し、
  `mfvideosrc` の `device-index` の並びとも揃わない。`device-path` はデバイスの properties の
  `device.path`（`mfdeviceprovider` が付けるキー）から読む。**所有権はバインディングが持つ**
  ── `GList` は要素を控えてからスパインを解放する経路で扱われ、`Caps` / `Structure` は
  破棄すべき所有オブジェクトとして返る（GirCore でカメラのある機械だけヒープが壊れていた
  経路は GstSharp.Net preview.2 で解消済み）。
- **適用は `InitializeCore` の末尾（PLAYING 到達後）に 1 回。** ここに置くのは、UI からの
  `Initialize()` だけでなく**自動復帰のエスカレーションでも設定が戻る**ようにするため。
  ソースが `mfvideosrc` でない／設定が空なら**デバイスを開きもせずに戻る**
  （`_stateLock` を握ったまま走るため）。
  **失敗しても初期化は落とさない** ── 理由を `CameraControlsLastError` と
  `activity.log` の `camera.control` に残すだけにする（常時録画の隔離契約と同じ判断）。
- **ダイアログのスライダーはその場でカメラへ届く。** プレビューを見ながら合わせられることが
  この機能の価値なので、OK まで反映しない作りにはしない。行は `GetRange` が通った項目だけ
  （カタログの 17 項目を機械的に並べると「動かせないつまみ」が並ぶ）。
  **開けなかったときは黙って空にせず理由を出す** ── 空のダイアログでは
  「このカメラには何も無い」と「開けなかった」の区別が付かない。
  1 行も出せなかったときは **OK でも何も確定しない**（空文字を返すとそれまでの設定を消す。
  「開けなかった」と「空にしたい」は別物である）。
  **確定は `CameraControlSettings.Merge` を通す** ── ダイアログはそのカメラが申告した項目しか
  行に持たないので、ゼロから組み立てると**未知キー**と**そのカメラには無いが設定には
  書いてある項目**が黙って消える。`Parse`/`Format` しか通らない L1 の表明では捕まらないので、
  合成そのものを純粋層へ置いて `Merge_*` のテストで縛ってある。

> **`mfvideosrc` が開いている最中でも、別ハンドルからの制御は効く（実測）。**
> 録画・プレビューが動いている最中にスライダーを動かすと、その場でプレビューの画が変わる
> ── Windows の Frame Server がカメラの共有オープンを許すため。
> 実測環境: Logitech のカメラ 1 台（`camera.open … opened=True controls=12`）。
>
> ただし**成否はドライバ次第**で、これは 1 機種での測定にすぎない。効かないカメラに
> 当たった場合、**代替は無い**（`ksvideosrc` も制御を実装しておらず、`videobalance` も
> 同梱ランタイムに無い ── いずれも実測）ので、そのときは
> 「録画停止中のみ開ける」へ縮退させるしかない。
> 確認手順は [docs/coverage-gaps.md](../docs/coverage-gaps.md)。

### エラー時の自動リスタート

sink パイプラインのバスを購読しているハンドラ（`HandleBusMessage`）は `MessageType.Error` を
検知すると、エラー元の要素（`_errorSinkSrc`）を `State.Ready` にして復帰を予約する
（`ScheduleRestart`）。**`Ready` への遷移はプールスレッドへ逃がす** ── ハンドラは post 元＝
当の要素のストリーミングスレッドで走るので、インラインで落とすと自スレッドの復帰を待って固まる。
復帰は `RestartLoopAsync` が `RestartPolicy` に従って実行し、間隔は **5s → 10s → 30s → 60s
で頭打ち**、3回連続で失敗したらパイプラインごと `Initialize()` し直す（詳細は「自動復帰」の節）。
画面キャプチャ対象モニタの切断など、ソース側の一時的な異常からの自動復帰を狙ったもの。

**間隔は上限であって、待ち切る義務ではない。** 映像源がカメラ（`mfvideosrc`）か画面キャプチャなら、
`DeviceArrivalWatcher` がデバイスプロバイダのホットプラグ通知を購読し、
**デバイスが戻ってきた時点で待ちを打ち切る**（`RestartPolicy.EarlyWakeSettleMs` だけ置いてから試す）。
判定は `DeviceKindRules`、監視できない構成ではタイマーだけの挙動へ縮退する。

### ファイル名テンプレート（`FormatFilename`）

`FilenameTemplate`（既定 `"{Now:yyyyMMdd_HHmmss}_{Name}.mp4"`）を、`Start()` 実行時に以下の
プレースホルダで展開する（`{キー}` または `{キー:書式}`、`FilenameTemplate.PlaceholderRegex`）。

- `{Now}` — 録画開始時刻（`IFormattable` の書式指定子が使える）
- `{Name}` — レコーダー名
- `{ENV.変数名}` — 環境変数
- `{Segment}` — **常時録画のセグメント番号**（5桁0詰め）。`ContinuousFilenameTemplate` の
  展開時だけ辞書へ重ねる（イベント録画の `FilenameTemplate` では未登録キーとして原文のまま残る）。
  書式指定（`{Segment:000}`）は効かない ── `FilenameTemplate.Format` の書式は
  `IFormattable` にしか適用されず、この値は `string` だから（桁揃えは呼び出し側で済ませてある）。
- 上記以外 — `EventRecorder.TemplateVariables`（`static ConcurrentDictionary<string, string>`）
  に登録されたユーザー定義変数。CLI の `--set`/`--get`（後述）と Variables 画面
  （`TemplateVariablesViewModel.cs`）、UIA トリガの発火（`{トリガID}`。後述
  「UIA トリガ（UiaTrigger 連携）」）はこの同じ辞書を操作する。
  **この辞書が実行時の真実**であり、settings.json の `TemplateVariables` は
  その保存／復元のための器。
  値の型が `string` なのは Native AOT の都合 ── `object` 値は
  System.Text.Json のソース生成で扱えない。

  **永続化は明示指定したものだけ**（意図的な仕様）。`--set` はセッション限りで、
  settings.json に残すには CLI の `--persist キー` か Variables 画面の「保存」列で
  指定する（外すのは `--no-persist キー`）。
  実装は**別のフラグを持たず、`AppSettings.TemplateVariables` にキーが載っているかどうか**で
  表す。したがって settings.json の形は従来と同じで、既存のファイルは全キーが
  「永続化指定済み」として素直に読める（移行も `DataVersion` の更新も要らない）。
  `AppSettings.Save()` は**既に載っているキーの値を static ストアから取り直すだけ**で、
  載っていないキーを足すことはしない ── だから `SetTemplateVariablePersistent(key, true)` は
  その場でキーを追加する必要がある（追加を忘れると `--persist` が黙って何もしない）。

展開後、ファイル名に使えない文字（`\ / : * ? " < >  |`）は全角文字へ変換し
（対応表に無い文字は `_`）、**相対パスは `AppSettings.OutputDirectory` を基準に解決**、
存在しないディレクトリは自動作成する。解決結果は `LastFilename` に保持される。

> **基準はカレントディレクトリにしない。** 常駐ワーカーは**最初に起動した
> シェルのカレントディレクトリをプロセス寿命ぶん引きずり**
> （`SingleInstanceManager.Launcher` は `WorkingDirectory` を指定していない）、
> 2回目以降の CLI 実行は既存のワーカーへ転送されるだけなので、CWD 基準だと
> **「さっきと同じ場所で叩いたのに前と違うところに出る」**という形で壊れる。
> 解決規則は `Components.AppDirectories`（空欄＝実行ファイルのあるディレクトリ、
> 相対パスもそこからの相対）。`GstDebugDumpDotDir` と同じ規約で、実装も共有している。

### 保存先と自動削除（`AppSettings`）

| 設定 | 既定 | 意味 |
|---|---|---|
| `OutputDirectory` | 空欄 | 録画の保存先。空欄なら実行ファイルのあるディレクトリ。相対パスもそこからの相対。Settings 画面では「…」でフォルダー選択ダイアログが開く（後述） |
| `RecordingRetentionDays` | `0` | この日数を過ぎた mp4 を自動削除する。**0 なら削除しない** |
| `RecordingCleanupIntervalHours` | `6` | 自動削除の間隔（時間）。1 未満は 1、**1000 を超える値は 1000** として扱う（`Task.Delay` の上限 ≒ 1,193 時間より手前で頭打ちにする。超えると周回が例外死して保持期限が無音で効かなくなる） |

削除は `Components.RecordingCleanup.Sweep`（サブフォルダーも再帰的に探す。判定は更新時刻。
**リパースポイント［ジャンクション・シンボリックリンク］には降りない** ── リンク先は
保存先の外の実体でありうるため。スキップした事実は `cleanup.error` に 1 行残る）。
**空フォルダーの削除は「実際にファイルを消したフォルダーとその祖先」に限る**
── 既定の保存先が実行ファイルのあるディレクトリなので、無条件に空フォルダーを掃除すると
インストール先まで巻き込む。保存先そのものは決して消さない。
1件の削除失敗（録画中のファイルはロックされている）で全体を止めず、理由を記録して続行する。

保存先は Settings 画面の「…」から**フォルダー選択ダイアログ**で選べる
（`MainPage.PickOutputDirectoryAsync`）。**UWP の `Windows.Storage.Pickers` ではなく
Windows App SDK 側の `Microsoft.Windows.Storage.Pickers` を使う** ── あちらはアンパッケージ配布だと
`InitializeWithWindow` で HWND を注入しないと実行時に落ちるが、こちらは `WindowId` を
コンストラクターで受け取るので HWND を持ち回らずに済む。ウィンドウは
`XamlRoot.ContentIslandEnvironment.AppWindowId` から辿る（ページに `Window` を参照させると、
プロセス寿命のウィンドウとページの寿命が絡む）。**行は読み取り専用にしていない**
── 空欄（＝実行ファイルのあるディレクトリ）と相対パスはダイアログでは表せないので、
直接入力の道を残してある。取り消すと値は変わらない。

周回は `Services.RecordingCleanupScheduler`（`Task.Run` ＋ `Task.Delay` ＋
`CancellationTokenSource`。`EventRecorder` の自動復帰の連鎖と同じ形）。
**起動直後に1回掃いてから、以後は間隔ごと。** 起動時の1回があることで
「数時間動かし続けないと効かない」を避けられ、同時に**ファイルの更新時刻を過去に倒して
起動するだけで L2 が検証できる**（時間の細工もテスト専用の環境変数も要らない）。
設定は毎回読み直すので、変更は次の周回から効く。

---

## プレビュー（d3d12swapchainsink → SwapChainPanel）

プレビューは **DXGI コンポジションスワップチェーン**を経由する方式
（SwapChainPanel + `d3d12swapchainsink`）で描画する
（ネイティブ子 HWND の直接生成は使わない）。

- `GStreamer.GstSharpNet/GstPreviewer.cs`（`Previewer`）: 選択中レコーダーの映像だけを受け取り、
  `appsrc ! queue ! d3d12swapchainsink name=sink sync=false` というパイプラインで描画する。
  `d3d12swapchainsink` は `IDXGIFactory2::CreateSwapChainForComposition` により
  **HWND を持たない**コンポジションスワップチェーンを生成し、そのハンドルは
  `sink` 要素の `swapchain` プロパティ（読み取り専用ポインタ）から取得する
  （`GetSwapChainHandle()`）。リサイズは `resize` アクションシグナル（`EmitResize`）で行う
  （`swapchain-width`/`height` は読み取り専用のため）。`PushSample` は `appsrc` の
  `CurrentLevelBuffers < 10` を満たす場合のみ供給し、バッファの無制限な滞留を防ぐ。
- `Controls/Controls/NativeSwapChainPanel.cs`: `SwapChainPanel` を継承し、
  `SwapChainHandle` 依存関係プロパティにハンドルを設定すると、
  `ISwapChainPanelNative::SetSwapChain`（IID `63aad0b8-...`）でパネルへバインドする。
  `.As<T>()`/`GeneratedComInterface` は Native AOT で不安定なため、パネルの `IUnknown` を
  直接 `QueryInterface` し、vtable スロット3を関数ポインタとして呼び出す**手書きCOM相互運用**を
  採用している。あわせて高DPI環境向けに `IDXGISwapChain2::SetMatrixTransform`
  （IID `a8be2ac4-...`、vtable スロット34）で逆スケール行列を適用し、物理ピクセル解像度の
  バッファをパネルの論理(DIP)サイズへ縮小合成させる（行わないと高DPI環境でバッファがパネル外に
  はみ出す）。パネルのサイズ・DPI変更は `SwapChainSizeRequested` イベントで通知し、
  実際のバッファリサイズ（GStreamer側の責務）は呼び出し側に委ねる。
- `ProcessRecorderApp/Views/MainPage.xaml.cs`: `InitializePreview()` で選択レコーダーの
  `Preview` イベントを購読し、`CompositionTarget.Rendering` で `TryBindSwapChain()` を
  リトライしながらスワップチェーンハンドルが生成されるのを待ってバインドする。
  `SwapChainSizeRequested` を `Previewer.ResizeSwapChain` へ転送する。
  プレビュー面はページ寿命で、`MainPage_Unloaded` → `MainPageViewModel.Dispose()` →
  `ShutdownPreview()` で閉じる（録画エンジンは破棄しない。「録画エンジンとプレビュー面の寿命」節を参照）。

### 全画面表示

プレビューだけを画面いっぱいに出す（F11 ／ プレビューのダブルクリック ／
プロパティペインのヘッダーの全画面ボタン。`Esc` で解除）。
入ると **タイトルバー・`NavigationView` のペイン・プロパティペイン・`GridSplitter`** が消える。
全画面中は**左右キーでレコーダーを切り替え**、**上下キーで構図補助線を切り替える**。

右クリックのメニューは**「プレビュー」（レコーダーの選択）・「フレーミンググリッド」（補助線の
選択）・全画面の出入り**の3項目で、前の2つはサブメニューを持つ。開くたびに組み直す
（項目を保持して購読を張らない ── レコーダーは追加・削除・改名されるので、作り置きすると
解除対象を取り違える）。**空のサブメニューは押させない**（開いても何も無い袋小路になる）。
このメニューは通常表示でも開けるので、**最後の項目は状態で文言を入れ替える**
（全画面中は「全画面を終了」、そうでなければ「全画面表示」）── 「全画面を終了」を
無条件に置くと押しても何も起きない死に項目になり、しかもフライアウトは
別のトップレベル UIA ウィンドウなので**死んでいることを E2E では検出できない**。

- **正本は `AppWindow.Presenter`。** `MainPageViewModel.IsPreviewFullScreen` は
  `AppWindow.Changed`（`DidPresenterChange`）から写すだけの読み取り専用の値で、
  永続化もしない。VM を正本にすると、View を通らない解除（トレイ格納）と真偽がずれる。
- **戻すときは入る前のプレゼンターの実体を使う。**
  `SetPresenter(AppWindowPresenterKind.Overlapped)` は元の実体を返さず
  **既定の新しい `OverlappedPresenter` を当てる**ので、それで戻すと最大化状態や
  リサイズ可否といった元の設定が失われる。
  **その実体は `PreviewFullScreen` が持つ（呼び出し側に引き回させない）** ──
  解除の経路は複数ある（Esc・ボタン・ダブルクリック・セクション切替・トレイ格納）ので、
  引数で渡す形にすると**渡し忘れた経路だけ元の状態を失う**。
  実際にトレイ格納の経路だけが既定プレゼンターで戻していた。
- **プロパティペインの折りたたみ状態（`IsPropertyPaneCollapsed`）は書き換えない。**
  あれは `AppSettings` へ永続化されるので、全画面のために畳むと抜けた後も畳んだままになり
  settings.json にも残る。代わりに既存の `x:Bind` 関数
  （`PaneColumnWidth` / `PaneColumnMinWidth` / `PaneContentVisibility` /
  `PaneHeaderOrientation`）が第2引数として全画面状態を受け取る ──
  全画面は折りたたみの上に重ねる**表示の上書き**である。
- **`KeyboardAccelerator` は `ScopeOwner` を持たずウィンドウ全域に効く**
  （`ListViewCopyBehavior` の doc）。したがって F11 / `Esc` / 左右 / 上下は
  `Invoked` の中で弾くのではなく **`IsEnabled` 自体をゲート**にする
  ── そうしないと `Esc` が `ContentDialog` の閉じる操作を、左右キーが PropertyGrid や
  ComboBox のキー操作を、**上下キーが一覧のキーボード操作そのもの**を奪う。
  上下は左右と条件が同じでも**別のプロパティ（`CanCycleFramingGrid`）で受ける**
  ── 奪う相手が違うので、片方の条件を変えたときにもう片方が巻き添えにならないようにする。
- **右クリックメニューが開いているあいだは `Esc` と上下左右を止める**（`IsPreviewMenuOpen`）。
  アクセラレータは**ポップアップの既定のキー操作より先に取る**ので、止めないと
  **開いたメニューを上下で辿れず、左右でサブメニューを開けず、`Esc` で閉じられない**
  ── 押すと背後の補助線やレコーダーが動き、しかも**メニューの印は開いたときのままなので
  表示と実際がずれる**。メニューはサブメニューを持つので、キーで辿れることに意味がある。
  戻すのは `Closed`（項目の選択・領域外の押下・`Esc` の**どの閉じ方でも**上がる）。
- **トレイ格納時に解除する**（`App.OnLaunched` の `WindowHiddenToTray`）。
  解除しないと、トレイから戻したときに**タイトルバーもナビも無い全画面**で現れて
  閉じる手段が消える。

#### ウィンドウサイズを全画面の大きさで上書きしないこと（重要）

`MainWindow_SizeChanged` は通常サイズを `WindowWidth` / `WindowHeight` へ保存するが、
**全画面へ入るときの `SizeChanged` は「プレゼンターが差し替わる前に、しかし画面いっぱいの
大きさで」発火する**。実測（一時的に `diag.size` を仕込んで確認）:

```
state=Normal presenter=Overlapped size=1280x720   ← 起動時
state=Normal presenter=Overlapped size=1600x900   ← 全画面へ入った直後（まだ Overlapped）
state=Normal presenter=Overlapped size=1600x900
```

つまり **`SizeChanged` の中で `AppWindow.Presenter.Kind` を見ても全画面だと分からない**
（WinUIEx の `WindowState` も `Normal` のまま）。素直に書くと画面いっぱいの大きさが
settings.json へ焼き込まれ、**次回起動が全画面サイズで開く** ── 戻す手段は
設定ファイルの手編集だけになる。

そこで `Views/PreviewFullScreen.cs`（静的）が **入る直前に旗を立て、
プレゼンターの変化を観測してから降ろす**。`MainWindow_SizeChanged` はその旗を見る。
**旗が立ったまま戻れない経路を作らないこと** ── `SetPresenter` が失敗したら `Enter` が
旗を降ろし、`Exit` は既に全画面でなければ旗を実態に合わせ直す。立ったままにすると、
以後このプロセスのあいだ**ウィンドウサイズが一切保存されなくなる**
（無音で、次回起動まで気付けない）。
降ろすのを遅らせるのは復帰の途中に来る `SizeChanged` を巻き込まないためで、
そのとき保存を飛ばしても値は入る前と同じなので失うものは無い。
全画面の出入りは**必ずこのクラスを通す**こと（`MainPage` と `App` の両方が使う）。
L3 の `PreviewFullScreenTests.FullScreen_DoesNotOverwriteTheSavedWindowSize` が縛る。

### 構図補助線（フレーミンググリッド）

`AppSettings.FramingGrid`（`None` / `Thirds` / `GoldenRatio` / `Crosshair` / `Square`。既定は
`None`）で、プレビューへ Windows 標準のカメラアプリと同じ補助線を重ねる。
**アプリ全体の設定であってレコーダーごとではない** ── プレビュー面はプロセス内に1面しかなく、
見えているのは常に選択中の1台だからである。

選択肢の正本は `Components/FramingGridChoices.cs`。設定画面のコンボボックス・プレビューの
右クリックメニュー・全画面中の上下キーが**同じ一覧と同じ並び**を使う（並びがそのまま巡回順）。
**表示は訳すが保存値は列挙型の名前のまま**にするため、列挙型のプロパティに
`ChoiceListAttribute` を付けている ── `PropertyGridView` はこの属性を列挙型の判定より先に見る。
保存値が名前であることは変わらないので `docs/settings.schema.json` は動かない。
**訳した文言が settings.json に入ってしまう**種類の壊れ方は L1 では見えないので、
発行物を操作して保存結果を読む `FramingGridChoiceUiTests`（L3）で押さえる。

**線はパネル全体ではなく「映像の矩形」に引く。** `d3d12swapchainsink` は
`force-aspect-ratio` の既定が `true` で、スワップチェーンはパネル全面を占めるものの
**映像はその中でアスペクトフィットされ、余った上下（左右）は `border-color` で塗られる**。
パネル全体に引くとその帯の上にも線が乗り、構図の目安にならない。
矩形と線の算出は純粋関数 `Components/FramingGridGeometry.cs`（`Fit` / `Lines`）にあり、
**この機能で自動テストできるのはここだけ**（`FramingGridGeometryTests`。
線が実際に描かれていることは UIA からは見えない ──
[docs/coverage-gaps.md](../docs/coverage-gaps.md)）。

映像の表示サイズは `Previewer.PushSample` が `sample.GetCaps()`（＝**実際にネゴシエートされた**
キャップス。`_sink.GetCaps()` ではない）から読み、**変化したときだけ** `VideoSizeChanged` を
発火する（毎フレーム通知するとディスパッチャを埋める）。`pixel-aspect-ratio` が 1:1 でなければ
幅へ掛けて表示幅にする ── シンクが保つのは表示アスペクトなので、画素のままだと線が縁とずれる。
**レコーダーを切り替えたら `Previewer.ResetVideoSize()` で「未知」へ戻す**
（`Controller.OnSelectedRecorderChanged`）── 戻さないと、次のフレームが届くまで
前のレコーダーのアスペクトで線が引かれたままになる（未初期化のレコーダーへ切り替えた場合は
フレームが来ないので残り続ける）。

描画は `MainPage.xaml` の `NativeSwapChainPanel` の**子**に置いた `Canvas`（`framingGrid`）。

- **兄弟ではなく子にする。** `SwapChainPanel` は `Panel` なので子を持て、子はスワップチェーンの
  上に合成される。子にすればパネルの `Visibility` をそのまま継承するので、
  「映像は畳んだのに線だけ残る」が構造的に起こらない ── 兄弟にすると Visibility の
  二重管理になり、`PreviewPlaceholderTests` が名指しで警戒している
  「半透明のオーバーレイ・z 順の誤りは全部緑になる」形に自分から入る。
- **`AutomationProperties.AccessibilityView="Raw"` は必須。** 付けないと `PreviewSurface` の
  子として UIA ツリーに現れ、E2E の要素列挙が汚れる。`IsHitTestVisible="False"` で
  当たり判定も持たせない。
- **座標は DIP。** `NativeSwapChainPanel` が高DPI対策の逆スケール行列を掛けているのは
  **スワップチェーンだけ**で、その上に載る XAML の子は論理座標のままである
  ── `SwapChainSizeRequested` が渡す値（物理ピクセル）を使うと高DPI環境でずれる。
  `MainPage.UpdateFramingGrid` は `ActualWidth` / `ActualHeight` を渡す。

---

## UI 構成

- **`Views/MainPage.xaml`(.cs)**: `NavigationView`（既定 `PaneDisplayMode=Top`）による画面切り替え。
  セクションは `Preview` / `Log` / `Variables` / `Settings`（`ViewModels/MainSection.cs`）。
  - `Behaviors/RecorderNavViewBehavior.cs` が `Preview` メニュー配下にレコーダーごとの
    サブ項目＋「レコーダー追加」項目を動的生成・同期する。
  - **「レコーダー追加」は「既定値から新規」か「既存の1台をコピー」かを尋ねる**
    （`MainPage.ChooseRecorderTemplateAsync`。取り消すと1台も増えない）。
    レコーダーが0台のときは尋ねずに既定値で追加する。
    複製は **`AppSettings.CloneRecorder`（ソース生成 JSON の往復）**で行う ──
    **プロパティを1つずつ書き写す `Clone()` を書いてはいけない**。レコーダー設定は既に
    手書きのミラーを4箇所持っており（`RecorderSettingsMirrorTests`）、手書きの複製は
    **検出器の無い5つ目のミラー**になって静かに腐る（増やしたプロパティが写らない、
    という形で。例外もテストの赤も出ない）。
    名前は写したまま追加し、**一意化は既存の流れ（`TryEnqueue` の中の
    `RecorderNaming.MakeUnique`）に任せる** ── 「コピー元の名前は既定値ではないから
    ctor の上書きには当たらない」は成り立たない（ちょうど `Recorder` という名前の
    レコーダーをコピーすれば当たる）。
  - Preview 画面: `Controls/PropertyGridView`（選択レコーダーのプロパティ編集、`Actual*` 系は
    読み取り専用で実行中の実値を表示）＋ `NativeSwapChainPanel`。`SrcPipeline` の値ビルダー
    ボタンから `Views/PipelineBuilderDialog.xaml`（`ViewModels/PipelineBuilderViewModel.cs`）を開く。
  - Log 画面: 既定は `Controls/LogTerminalView`（WebView2 の中の xterm.js）。
    `StandardStreamRedirector` が捕捉した出力を**生のまま**流すので、`
` による行上書き・
    カーソル制御・256 色/TrueColor がそのまま効く。WebView2 が使えないときだけ
    従来の `ListView`（`Controls/Behaviors/AnsiText`）へ落ちる。
    配色は両経路とも Windows Terminal Campbell（`Components/CampbellPalette` が正本）、
    フォントは Consolas 12。詳細は下記「Log 画面のターミナル表示」の節。
    右上の「グラフを保存」（`MainPageViewModel.SaveDebugGraphsCommand`）は、いま生きている
    パイプライン（全レコーダーの sink/src とプレビュー）の `.dot` を
    `GstDebugDumpDotDir`（空欄ならデータディレクトリ）へ書き、結果を `gst.dot` に残す。
    **初期化に失敗したときも同じ設定の場所へ `<名前>.init-failed.dot` を書く**
    （`EventRecorder.WriteFailureGraph`。破棄の前に書く ── `Close()` を過ぎると
    「どこまで組めていたか」を見る手段が無くなる）。`GstDebugDumpDotDir` が空欄なら
    **何も書かない**（頼まれてもいない場所へファイルを撒かないため）。
    書けるのは<b>パイプラインが出来たあとの失敗</b>だけで、`ParseLaunch` のリンク失敗では
    パイプラインそのものが無いので `.dot` は原理的に作れない ── その場合の手がかりは
    `gst.encoder candidate-failed` に添える**パイプライン文字列**の方である
    （要素名しか言わないリンクエラーを、実際に書いた caps と突き合わせられる）。
    **`gst_debug_bin_to_dot_file` は使えない** ── あちらは `gst_init` の時点で控えた
    `priv_gst_dump_dot_dir` しか見ず、未設定なら無言で何も書かない（既定の起動がそれ）。
    代わりに `DebugBinToDotData` で受け取ってアプリ側が書く（`GStreamer/DebugLogEx.WriteDotFile`）。
  - Variables 画面: `WinUI.TableView` による Key/Value グリッド（`TemplateVariablesViewModel.cs`）。
  - Settings 画面: `PropertyGridView` で `Settings/AppSettings.Default` を編集。
    右上の「再読み込み」ボタン（`MainPageViewModel.ReloadSettingsCommand`）が
    `AppSettings.Reload()` を呼ぶ ── 手で settings.json を直したときの反映口。
    **録画中・排出中は無効**（`GstControllerViewModel.IsIdleAll`。レコーダーを
    丸ごと作り直すため）で、押すと確認ダイアログが出る。
    再読み込みで反映されないものが2つある: ウィンドウサイズとプロパティペインの
    折りたたみ状態（起動時に1回だけ読む）、そして**保存の指定をしていない
    テンプレート変数は消える**（`OnLoaded()` が実行時ストアを作り直すため。
    確認ダイアログの本文がこれを指す）。
    `GstDebug` は再読み込みでも変更でも即時反映される（`DebugLogEx.TrySetThreshold` が
    `gst_debug_set_threshold_from_string` を呼ぶ）。ただし**起動時だけは経路が違い**、
    `ApplyStartupEnvironmentVariables` が環境変数として渡すので、外部で `GST_DEBUG` が
    設定済みならそちらが勝つ。`GstDebugDumpDotDir` も「グラフを保存」に対しては
    即時反映されるが、**GStreamer 内部のダンプ先は `gst_init` で確定する**ので
    そちらに効かせるには起動時の指定が要る（GStreamer 1.28.6 に変更する API は無い）。
- **ViewModel**: `ViewModels/GstControllerViewModel.cs`（`static Current` を保持。全レコーダーの
  一括開始/終了、CLI コマンドからも参照される）、`GstEventRecorderViewModel.cs`
  （レコーダー単位、`CanStartRecording`/`CanStopRecording` などの実行可否ガード）、
  `MainPageViewModel.cs`。
- タスクトレイ挙動（Show/Quit メニュー、閉じる/最小化のトレイ格納）は下記「タスクトレイ常駐（WinUIEx）」の節を参照。

### 録画エンジンとプレビュー面の寿命

アプリの中核価値は「トレイ常駐中の常時バッファリング」であり、これを画面のライフサイクルから
切り離すため、所有者を次の2層に明確に分けている。

| 対象 | 寿命 | 所有者 |
|---|---|---|
| **録画エンジン** = `GstControllerViewModel`（`Controller` + 全 `EventRecorder` + 常時稼働 sink パイプライン + 設定ミラーリング） | **プロセス寿命** | `App` |
| **プレビュー面** = `Previewer` / `d3d12swapchainsink` / スワップチェーン | ページ寿命 | `MainPage` |

- 生成は `App.OnLaunched` の `GstControllerViewModel.Start(dispatcherQueue)`。**`new MainWindow()` より前**に呼ぶ。
  起動順序は `Program.Main` → `StartResidentWorker` → `Controller.StaticInitialize()`
  → `Gst.App.GstApp.Initialize` → `Application.Start` → `new App()` → `OnLaunched` であり、
  この時点で GStreamer は初期化済み。アクティベーション転送は `TryEnqueue` 経由でメッセージループが
  回るまで処理されないため、**最初の CLI コマンド処理より前に `Current` が必ず設定される**。
- `Start()` は `Current ??= new(...)` であり、`Current` への登録はここだけで行う（ctor では設定しない）。
  ctor は `private`。
- 破棄は `AppWindow.Destroying`（トレイ格納は `Closing` をキャンセルするだけなので、
  本当の終了時のみ発火する）で `Save()` → `engine.Dispose()` の順に、**単一のハンドラ**で行う
  （複数ハンドラの発火順に依存させない）。
- `MainPage` はエンジンに**バインドするだけで破棄しない**。`MainPageViewModel.Dispose()` は
  `GstController.ShutdownPreview()`（＝自分が初期化したプレビュー面だけを閉じる）を呼ぶ。
  ここを `GstController.Dispose()` にすると、画面の破棄で録画エンジンごと止まる。
- `MainPage_Unloaded` では**プロセス寿命オブジェクトに張ったページ寿命のデリゲートを必ず外す**
  （`ConfirmRecorderRemovalAsync = null`、`recorderPropertyGrid.ValueBuilder = null`）。
  外さないと死んだページと `XamlRoot` が永久に参照され、後から CLI／トレイ経由で削除コマンドが
  走ったときに破棄済みビジュアルツリー上で `ContentDialog` を出そうとする。
- `Controller` のプレビュー面 4 メンバ（`InitializePreview` / `ShutdownPreview` /
  `GetSwapChainHandle` / `ResizeSwapChain`）はいずれも**冪等**で、`_previewGate` ロックで保護する。
  `GstEventRecorder_Preview` は各レコーダーのプレビュー枝の `appsink` コールバック
  （＝枝のストリーミングスレッド）上で走り、`ShutdownPreview` は
  UI スレッドで走るため、無保護だと `PushSample` 実行中に `appsrc` が破棄されてネイティブクラッシュする。
  プレビュー投入側は `Monitor.TryEnter` を使い、初期化／破棄と競合したフレームは待たずに捨てる
  （プレビュー枝のストリーミングスレッドをブロックしない）。
- `InitializePreview()` は**失敗しても例外を投げずログのみ**とし、ウィンドウは開いたまま続行する
  （WARP などプレビューが成立しない環境で、録画という中核機能を止めないため）。

---

## ローカライズ（en-US / ja-JP）

UI文言は WinUI 3 / Windows App SDK 標準の **MRT Core**（`.resw` + `resources.pri`）で管理し、
Windows の表示言語に自動追従する（アプリ内に言語切替UIは無い）。

- **リソースファイル**: `ProcessRecorderApp/Strings/{en-US,ja-JP}/Resources.resw`、
  `Controls/Strings/{en-US,ja-JP}/ControlsResources.resw`。`.resw` は既定の `PRIResource` グロブで
  自動的に取り込まれる（`.csproj` に明示的な `ItemGroup` を追加すると `NETSDK1022`（アイテムの
  重複）でビルドエラーになるため追加しない）。
- **共通ヘルパー**: `Components/Localization.cs`。`Microsoft.Windows.ApplicationModel.Resources.ResourceManager`
  （`Application`/`Window` の生成に依存しないため、ランチャー/CLI 経路からも呼べる）をラップし、
  - `GetString(path)`: 解決できなければ例外（＝実装側のキー誤りとして早期検知する設計）
  - `GetStringOrFallback(path, fallback)`: 解決できなければ `fallback` を返す（後述の Category/Description
    間接解決で使用）
  を提供する。`path` は `"{リソースファイル名}/{キー名}"`（例: `"Resources/Cli_RecorderNotAvailable"`）。
  参照プロジェクト（`Controls`）の resw は、マージ後の PRI で `"Controls/ControlsResources/{キー名}"`
  という階層になる（`makepri dump` で実機確認済み）。
- **XAML の静的文言**は `x:Uid` で解決する（例: `<Button x:Uid="MainPage_Clear" .../>` に対し resw
  キー `MainPage_Clear.Content`）。**ただし `x:Uid` は `<DataTemplate>` 内の要素には自動解決されない**
  （`UserControl.Resources` で定義されたテンプレートは、ページ本体の通常の `Connect()` 経路を通らない
  ため）。`Controls/Controls/PropertyGridView.xaml` の `CommandEditTemplate`/`CollectionEditTemplate`
  内のボタン（Exec/Add/Remove）はこの制約に該当し、`x:Bind` による静的プロパティ参照
  （`Controls/Controls/PropertyGridStrings.cs`）で解決している。
- resw のキー名にドット（`.`）を含めると、`ResourceMap.GetValue` は `/` 区切りのネストした
  サブツリーとして解決する（`Uid.Property` 形式は `x:Uid` の自動解決用の慣習であり、C# から直接
  `GetValue`/`GetString` する場合はキー名にドットを含めない、または `/` に読み替える必要がある）。
  `PropertyGridStrings.cs` から参照するキーはすべてドット無しのフラット名にしている。
- **コード側の動的文言**（CLI出力、確認ダイアログ、`RecorderNavViewBehavior` の「Add recorder」等）は
  `Localization.GetString("Resources/キー名"[, 引数...])` を直接呼び出す。
- `PropertyGridView`（`Controls/Controls/PropertyGridView.xaml.cs`）が表示する `[Category]`/
  `[Description]` 属性値は、`System.ComponentModel` の制約上コンパイル時定数（平文）しか持てないため、
  **属性値をリソースキーとして扱い、表示時に `Localization.GetStringOrFallback` で解決**する
  （未登録キー・平文どちらを指定しても動作するフォールバック設計）。`AppSettings`/
  `GstEventRecorderViewModel`/`EventRecorderSettings` の `[Category("PropCat_Debug")]`/
  `[Description("PropDesc_Rec_Name")]` 等がこれに該当する。
- アプリが自動保存する内部ウィンドウ状態（`AppSettings.WindowWidth`/`WindowHeight`/`SettingsWidth`/
  `IsPropertyPaneCollapsed`）は `[Browsable(false)]` で PropertyGrid から非表示にしている
  （ローカライズ対象外）。
- **値の確定は「フォーカスを失ったとき」と「Enter」の2経路ある。** 前者は TwoWay バインドの
  既定、後者は `PropertyGridView.ValueTextBox_KeyDown` が `PropertyGridItem.CommitFromEnter` を
  呼ぶ。Enter で確定した直後にフォーカスが外れると**バインドが同じ文字列をもう一度書き戻す**
  ので、`CommitFromEnter` はその 1 回を目印（`_enterEcho`）で無視する
  ── 無視しないと「入れ直した」と誤認してエラー表示を畳み、**Enter で出した指摘が
  離れただけで消える**。入力エラーは、変換できない値を入れると出て、表示値は直前の値へ
  差し戻される（＝画面の値とモデルは常に一致する）。

---

## アーキテクチャ：ランチャー / 常駐ワーカー分離

exeを実行すると、必ず最初は **「ランチャー」** として動作する（`SingleInstanceManager.Run`、
`SingleInstance/SingleInstanceManager.Launcher.cs`）。ランチャー自身が常駐インスタンスになることはない。

```
exe 実行（Program.Main）
  │
  ├─ SingleInstanceManager.IsWorkerBootstrap(args)?
  │     Yes（引数が "--__resident-worker" のみ） → StartResidentWorker() へ
  │     No  → SingleInstanceManager.Run() へ（＝ランチャーとして動作）
  │
  Run():
  ├─ ActivationCommands.TryHandleInLauncher(args) でヘルプ/バージョン/パースエラーを先に処理
  │     該当すれば、ここでランチャーが直接表示して終了（常駐ワーカーへは委譲しない）
  ├─ 名前付きMutex（Names.LauncherMutex）を取得（排他区間の開始）
  ├─ 既に常駐ワーカーが起動中？
  │     Yes → ⓪ そのワーカーが**リダイレクトを受け取れる状態になるまで待つ**
  │              （Names.WorkerAcceptingEvent。理由は下記「受理可能になるまで待つ」）
  │           → 引数をRedirectActivationAndGetResultで転送 → 結果を受信
  │           → Mutex解放 → **結果をコンソールへ書く** → ランチャー終了
  │     No  → ① 一旦キー登録を解除（UnregisterKey）
  │           ② 自分自身のexeを "--__resident-worker" 付きで別プロセス起動（UseShellExecute=true）
  │           ③ 常駐ワーカーが送る名前付きイベント(Names.WorkerReadyEvent)のSetを待機
  │           ④ Main()が受け取った引数配列(rawArgs)が空でなければ、
  │              常駐ワーカーへ引数をRedirectActivationAndGetResultで転送
  │              （空の場合は「単なるアプリ起動」とみなし転送しない）
  │           ⑤ Mutex解放 → **結果をコンソールへ書く** → ランチャー終了（常駐ワーカーは終了しない）
```

常駐ワーカー側（`--__resident-worker` 付きで起動されたプロセス）の順序は次のとおり。
**この順序が要点で、間違えると「キーは登録済みなのに受け取れない」窓ができる**（後述）。

```
StartResidentWorker():
  ├─ ① AppInstance.FindOrRegisterForKey でキーを登録      ← ここから「ワーカーが居る」と見える
  │      （負けたら ExitCode_WorkerAlreadyRunning=3 で即終了）
  ├─ ② WorkerAcceptingEvent を作成して Reset（「まだ受け取れない」の宣言）
  ├─ ③ initializing?.Invoke() → ActivityLog 初期化・app.start・GStreamer StaticInitialize()
  └─ ④ Application.Start → App ctor → SingleInstanceManager ctor
           ├─ Activated += OnActivationRedirected                ← ここで初めて受け取れる
           ├─ WorkerAcceptingEvent を Set（ランチャーの ⓪ を解放）
           └─ WorkerReadyEvent を Set（コールドスタートしたランチャーの ③ を解放）
```

以後 `Activated` イベントで他プロセス（＝次回以降のランチャー）からのリダイレクトを
受け続ける（`OnActivationRedirected` → `HandleActivation`）。

このため、**初回起動でインスタンスが存在しない場合でも**、ランチャープロセスは処理完了後に
必ず終了し、実処理を行う常駐ワーカーだけが生き残る。

### 常駐ワーカー起動の排他制御（Mutex + EventWaitHandle）

- **名前付き `Mutex`**（`Names.LauncherMutex(keyPrefix)`）: 「常駐ワーカーの有無を確認し、
  必要なら起動する」という一連の処理（`Run` の本体）全体を排他制御する。これにより、
  複数のランチャープロセスが同時に起動されても、この重要区間に同時に入れるのは常に
  1プロセスだけであることが保証される。
  **コンソールへの書き出しはこの区間の外**（解放後）で行う ── conhost の QuickEdit 選択中や
  読み手の止まったパイプでは書き込みが無期限にブロックしうるので、含めると
  その間の CLI が全部 10 分待たされて終了コード `6` になる。代償として、
  同時に走る 2 本の CLI の出力は交錯しうる（機械可読な出力を読むバッチは 1 本ずつ実行する）。
- **名前付き `EventWaitHandle`**（`Names.WorkerReadyEvent(keyPrefix)`・自動リセット）:
  **コールドスタートしたランチャー**が「自分が起こしたワーカーが立ち上がった」ことを検知する。
  常駐ワーカーは `SingleInstanceManager` コンストラクターで `Activated` の購読を済ませた
  **直後**にSetし、ランチャーは `WaitOne(timeout)` で待つだけでよく、ポーリングは行わない。
- **名前付き `EventWaitHandle`**（`Names.WorkerAcceptingEvent(keyPrefix)`・**手動リセット**）:
  **リダイレクト経路**が「相手のワーカーがもう受け取れる」ことを確認するために使う。
  ワーカーの生存中ずっとシグナル状態を保つ必要があるので `WorkerReadyEvent` とは別に持つ
  （あちらは一度きり・自動リセット）。詳細は次節。

#### 受理可能になるまで待つ（`WorkerAcceptingEvent`）

**「キーが登録済み」は「コマンドを受け取れる」を意味しない。** 上の①と④の間には
`app.start` のログと GStreamer の `StaticInitialize()`（初回はプラグインレジストリ構築で
10 秒を超えうる）が挟まる。ランチャーの既存ワーカー判定は `IsCurrent` 1点だけなので、
**この窓へリダイレクトすると購読者が居ないぶん痕跡ゼロで捨てられ**、ランチャーは結果通知を
待ち切って `ExitCode_WorkerResultTimeout` を返す ──
**利用者から見れば「アプリ起動直後のコマンドが黙って失われる」**。

そこでリダイレクト経路では、転送の前に `WorkerAcceptingEvent` を待つ。
**受理待ちと結果待ちは 60 秒の予算を共有する**（使った分を差し引く）── 直列に積むと
ワーカーが購読前に固まったときに `LauncherMutex` の占有が倍増し、
その間に来た CLI が全部止まるため。打ち切っても例外にはせず、そのまま転送へ進む。

回帰テストは `ResidentWorkerTests.TheFirstCommandAfterTheWorkerStarts_SucceedsOnTheFirstAttempt`
（**「ping が成功する」ではなく「何回目で通ったか」を見る** ── ハーネスが ping を
繰り返すので、成否だけを見ると1回目の取りこぼしを2回目が隠してしまう）。

名前付きオブジェクトの実体名は、`SingleInstanceManager.Launcher.cs` 内の private クラス
`Names` が `keyPrefix`（`Program.KeyPrefix` = `nameof(ProcessRecorderApp)` = `"ProcessRecorderApp"`）
から生成する（`{prefix}.SingleInstanceKey` / `.LauncherMutex` / `.WorkerReadyEvent` /
`.WorkerAcceptingEvent` / `.CommandResultReadyEvent` / `.CommandResultMap`）。
変更する場合は `Components.AppEnvironment.DefaultKeyPrefix` を書き換える
（`Program.KeyPrefix` はその委譲で、実行時は `PROCESSRECORDERAPP_KEY_PREFIX` が優先する）。ワーカー起動フラグの文字列 `"--__resident-worker"` は
`SingleInstanceManager.WorkerFlag`（private const）で固定。

### 引数の有無の判定について

`AppActivationArguments`（`AppInstance.GetCurrent().GetActivatedEventArgs()`）から復元される
コマンドライン文字列は、Win32起動の場合exeパス自体を含むことがあり「引数が空かどうか」の判定には
使えない。そのため `SingleInstanceManager.Run` では、常駐ワーカーへリダイレクトするかどうかの
判定に **`Main(string[] args)` が受け取った生の引数配列（`rawArgs`）の `Length`** を使用している
（`AppActivationArguments` 自体は、実際にリダイレクトする際のペイロードとしてのみ使用）。

## 常駐ワーカーでの処理失敗をランチャーの終了コードで識別する

常駐ワーカーへリダイレクトした引数の処理（`start-recording` コマンドなど）が失敗した場合、
呼び出し元のランチャープロセスの**終了コード（プロセス Exit Code）**でそれを識別できる。
バッチファイルやCIなど、呼び出し側が `%ERRORLEVEL%` / `$LASTEXITCODE` を見て成否判定したい
シナリオを想定している。コンソールサブシステムでビルドしているため、対話コマンドプロンプトからの
直接実行でもそのまま取得できる（後述の補足を参照）。

### 終了コードの一覧

| 終了コード | 意味 | 定義箇所 |
|---:|---|---|
| `0` | 成功（`Activate`/`Silent`/`SilentWithToast`/`ActivateWithToast` など） | — |
| `1` | 常駐ワーカーの起動自体に失敗（プロセス起動失敗・起動完了通知のタイムアウト） | `SingleInstanceManager.ExitCode_WorkerStartFailed` |
| `2` | 常駐ワーカーへ処理を委譲したが、結果通知がタイムアウトした（成否不明） | `SingleInstanceManager.ExitCode_WorkerResultTimeout` |
| `3` | 常駐ワーカーが既に起動済みの状態で、さらに常駐ワーカーとして起動された（内部フラグ `--__resident-worker` を付けた手動起動など。2つ目の常駐ワーカーは起動されない） | `SingleInstanceManager.ExitCode_WorkerAlreadyRunning` |
| `4` | 不明なサブコマンド・引数（パースエラー）。ランチャーがエラーメッセージを表示して終了し、常駐ワーカーへはリダイレクトされない | `ActivationCommands.ExitCode_InvalidArguments` |
| `5` | 常駐ワーカーが終了処理に入っていて、コマンドは**一度も実行されなかった**（`2` と違い成否は不明ではない ── **安全に再試行できる**） | `SingleInstanceManager.ExitCode_WorkerShuttingDown` |
| `6` | 先行するランチャーが排他制御を握ったまま返らず、10 分待っても順番が回ってこなかった。コマンドは**一度も実行されていない**。**正常な待ち行列でここへ来ることは無い**ので、出たら調査対象 | `SingleInstanceManager.ExitCode_LauncherBusy` |
| `10`以上 | 常駐ワーカー内でのコマンド処理が失敗（既定値は`10`。コマンドごとに任意の値を指定可能） | `CommandOutcome.DefaultFailureExitCode` |
| `11` | `--get` / `--persist` / `--no-persist` で指定したキーがテンプレート変数に未定義。**コマンド本体が既に失敗している場合はそちらの終了コードを優先し**、未定義キーの指摘は標準エラーにのみ出る（より重い失敗をタイプミスで隠さないため） | `ActivationCommands.ExitCode_VariableNotDefined` |
| `12` | 録画コマンド実行時、常駐ワーカー起動直後でまだ ViewModel が準備できていない（8秒待っても未生成） | `ActivationCommands.ExitCode_RecorderNotAvailable` |
| `13` | 指定したインデックス／名前に一致するレコーダーが存在しない | `ActivationCommands.ExitCode_RecorderNotFound` |
| `14` | 現在の状態では録画を開始／終了できない（`CanStart/StopRecording` が false）。`start-recording-all` は**1 台も開始できなかった場合**（開始可能なレコーダーが無い／全台が開始で失敗した）もこれを返す ── 単体の `start-recording` が同じ失敗で非 0 を返すのに `-all` だけ 0 になるのを避けるため | `ActivationCommands.ExitCode_RecordingNotExecutable` |
| `15` | `status` で、初期化されていない／直近の障害が残っているレコーダーが1つ以上あった | `ActivationCommands.ExitCode_RecorderUnhealthy` |
| `16` | `stop-recording` / `stop-recording-all` で、排出は綺麗に終わったが**MP4 に1フレームも入っていなかった**（`-all` は**今回停止した分**のうち1本でも該当すれば返す。録画していなかったレコーダーの前回の結果は見ない）。**捨ててよい**。切り分けは `activity.log` の `recording.stop empty`（`samplesSeen` / `samplesPushed` / `srcState`） | `ActivationCommands.ExitCode_RecordingProducedNothing` |
| `17` | `stop-recording` / `stop-recording-all` で、**排出（ファイルの確定）が完了しなかった** ── 打ち切り・バスのエラー（ディスク満杯・書込権限）・排出中の例外。**捨てる前に救済を検討できる**（`mdat` にデータはあるが `moov` が未確定）。`activity.log` の `recording.stop timeout` / `recording.stop error` | `ActivationCommands.ExitCode_RecordingNotFinalized` |
| `99` | 常駐ワーカー内でコマンド処理中に予期しない例外が発生した | `SingleInstanceManager.UnexpectedErrorExitCode` |

`1`/`2`/`3`/`5`/`6` は単一インスタンス層（`SingleInstanceManager`）、`4` はアプリ
（`ActivationCommands`）が予約している値のため、`CommandOutcome.Failure(exitCode: ...)` で
独自の失敗コードを指定する場合は `CommandOutcome.DefaultFailureExitCode`（`10`）以上
（かつ上の表に載っている予約値と衝突しない値）を使うこと。

**`16` と `17` の違いは「捨てていいか」**。どちらも「停止処理は走ったが成果物が使えない」で、
**共通して終了コード 0 を返してはいけない**（バッチが空／壊れたファイルを運んでしまう）。
分けているのは扱いが変わるからで、`16` は**メディアデータが無いと断定できる**ので捨ててよいが、
`17` は `mdat` にデータがある一方で `moov` が未確定なので**修復を試す余地がある**。
**両方に該当する場合は `17`**（中身の有無を断定できるのは、排出が綺麗に終わった場合だけ）。

**`2` と `5`/`6` の違いは再試行の可否**。`2`（結果通知のタイムアウト）は
**コマンドが実行されたかどうか分からない**ので、`start-recording` のような
副作用のあるコマンドを機械的に再試行してよいとは限らない。`5`（ワーカーが終了処理中）と
`6`（先行するランチャーが返らない）は**一度も実行されていない**ことが分かっているので、
**そのまま再試行してよい**。バッチから叩く場合はこの3つを分けて扱うこと。

### 実装：名前付き EventWaitHandle + MemoryMappedFile による結果通知

`AppInstance.RedirectActivationToAsync` は「アクティブ化要求を届けたこと」までしか示さず、
届いた先での実処理（コマンドの実行結果）を呼び出し元へ返す仕組みは持っていない。
そこで本プログラムでは、常駐ワーカー側の処理結果（終了コード・コンソール出力）をランチャーへ
送り返すための専用チャネルを、以下の2つの名前付きカーネルオブジェクトで実装している。

この2つと、それを使う手順は `SingleInstanceManager.CommandResultChannel`
（`SingleInstance/SingleInstanceManager.CommandResultChannel.cs`）に閉じている
── レイアウトの定義元はこの型だけで、ランチャー側もワーカー側も同じ定数を使う。

- **名前付き `EventWaitHandle`**（`Names.CommandResultReadyEvent`）: 常駐ワーカーが
  コマンド処理を完了した合図。常駐ワーカー側（`SingleInstanceManager.HandleActivation` →
  `ReportCommandResult` → `CommandResultChannel.Publish`）が処理完了後にSetし、
  ランチャー側（`RedirectActivationAndGetResult` → `TryWaitForResult`）が待つ。
- **名前付き `MemoryMappedFile`**（`Names.CommandResultMap`）: 相関ID・終了コード・
  コンソール出力文字列を受け渡すための共有メモリ。レイアウトは
  `[requestId:Guid][resultId:Guid][終了コード:int][標準出力バイト長:int][標準エラー出力バイト長:int][標準出力 UTF-8][標準エラー出力 UTF-8]`
  （容量は `CommandResultChannel.Capacity`。出力に使えるのは合計 64KB）。
  ランチャーが結果を読み取り、終了コードを自身の終了コードに反映し、
  出力文字列を呼び出し元コンソールの標準出力／標準エラー出力へ出力する。
  **合計 64KB を超えた出力は無言で切り詰められる**（標準出力を優先するため、
  標準エラーが丸ごと落ちることもある。終了コードは変わらない）── `--get` 全件や
  `status` の出力をバッチで機械的に消費する場合は、この上限を前提にすること。

ランチャーは常駐ワーカーへリダイレクトする**前**にこれらのオブジェクトを作成するため、
常駐ワーカー側は常に既存のものを開くだけでよく、作成順序の競合は起きない。

### コマンドごとの相関ID（`requestId`）

**このオブジェクトは固定名なので、結果には必ず相関IDを刻む。**
ランチャーが `Guid.NewGuid()` を `requestId` スロットへ書いてからリダイレクトし、
常駐ワーカーは**リダイレクトが届いた瞬間**にそれを読んで消す（`ClaimRequestId`）。
結果を書くときは中身を全部書いた**後に** `resultId` として同じ値を刻み、
ランチャーは `resultId` が自分の `requestId` と一致する通知だけを受け取る
（一致しないものは捨てて待ち直す。予算は延長しない）。

> **「`LauncherMutex` が `Run` 全体を直列化しているから相関 ID は不要」とはならない。**
> Mutex が直列化するのは**ランチャー側だけ**である。
> ワーカーのコマンド処理は UI スレッドの `DispatcherQueue` 上で走り、
> **ランチャーが結果待ちを打ち切って Mutex を放した後も続く** ── 通知は Mutex の外から来る。
> そのため、打ち切られた（終了コード `2`）コマンドの結果が、後から張られた**次のコマンドの
> チャネルへ届き、その次のコマンドの答えとして読まれる**。終了コードも標準出力も丸ごと
> 入れ替わるので、**「遅い」ではなく「間違った答えを返す」壊れ方**であり、
> 打ち切り時間の調整では直らない。
> 回帰テストは L1 の `CommandResultChannelTests`
> （**E2E では検出できない** ── 上限 60 秒は製品の `const` でテストから短くできないため）。

結果通知がタイムアウトした場合（常駐ワーカーがハングしている等）は `ExitCode_WorkerResultTimeout`
（`2`）を返し、それ以外の異常（コマンド処理中の予期しない例外）は常駐ワーカー側で握りつぶした
うえで `UnexpectedErrorExitCode`（`99`）として報告されるため、いずれの場合も常駐ワーカー自体が
落ちることはない。

`CommandOutcome`（`SingleInstance/CommandOutcome.cs`）は `ShowWindow` / `ToastTitle` /
`ToastMessage` に加え、`ConsoleOutput` / `ConsoleError` / `ExitCode` を持つ `readonly record struct`。
`Activate()` / `Silent()` / `SilentWithToast()` / `ActivateWithToast()` / `Failure()` の
ファクトリメソッドで生成し、`with` 式で `ConsoleOutput` 等を追加設定する。

## Win32 P/Invoke（CsWin32）

CsWin32（`Microsoft.Windows.CsWin32`、MIT License）は以下の2箇所でのみ使用している
（メインの `ProcessRecorderApp` プロジェクトには `NativeMethods.txt` が無く、参照もしていない）。

- `Components/NativeMethods.txt`: `CreatePipe` / `CreateNamedPipe` / `CreateFile` /
  `GetStdHandle` / `SetStdHandle` / `DuplicateHandle` / `GetCurrentProcess` など
  （`StandardStreamRedirector.cs` の標準入出力キャプチャ用）。
- `GStreamer.GstSharpNet/NativeMethods.txt`: `MessageBox`（`Controller.StaticInitialize` の
  初期化エラー表示用）。

`NativeMethods.txt` にAPI名を1行ずつ列挙するだけで、正確な型定義付きのP/Invokeコードが
ビルド時に自動生成される（`Windows.Win32.PInvoke` の静的メソッドとして呼び出し可能）。
Native AOTとも問題なく組み合わせられる。使用するAPIを追加したくなった場合は、該当プロジェクトの
`NativeMethods.txt` にAPI名を追記するだけでよい（手書きのP/Invoke宣言は不要）。

ウィンドウ前面化（`SetForegroundWindow`/`ShowWindow` 相当）は WinUIEx の拡張メソッド
（`Window.Restore()` / `Window.SetForegroundWindow()`、
`SingleInstanceManager.ShowMainWindow`）で行う。

## タスクトレイ常駐（WinUIEx）

タスクトレイアイコン、およびウィンドウの表示/最小化制御には
[WinUIEx](https://github.com/dotMorten/WinUIEx)（2.9.2、MIT License）を使用している。
実装は `SingleInstanceManager.AttachWindow`（`SingleInstance/SingleInstanceManager.cs`）に集約されている。

- `AttachWindow` で `WindowManager.Get(window).IsVisibleInTray = true` を設定し、
  タスクトレイアイコンを表示する（アイコンはウィンドウのタスクバーアイコンを流用）。
- **起動直後は `Window.Activate()` を一切呼び出さず**、`WindowManager.WindowState = WindowState.Minimized`
  を設定するだけに留めている（WinUIEx公式ドキュメントの "Launch-To-Tray" パターン）。
  プレビューが SwapChainPanel 描画のため、起動時の強制表示は不要
  （`App.OnLaunched` のコメント参照）。デバッガーアタッチ時のみ
  開発確認用に `Restore()` + `SetForegroundWindow()` で表示する。
- `WindowManager.WindowStateChanged`（`OnWindowStateChanged`）イベントで最小化を検知し、
  `AppWindow.Hide()` を呼んでタスクバーにも残さずトレイへ完全に格納する（最小化ボタン対応）。
- `AppWindow.Closing`（`OnAppWindowClosing`）イベントをキャンセルして `Hide()` することで、
  閉じるボタン(×)もトレイ格納として扱う。**Ctrl キーを押しながら**閉じるボタンを押した場合は、
  キー登録解除のうえ実際に閉じる（トレイへは格納しない）。
  この分岐規則は `ShouldExitOnClose`（純粋関数）に切り出してあり、L1 の `CloseToTrayTests`
  が検証している。**判定は必ず `CoreVirtualKeyStates.Down` フラグで行うこと** ──
  `CoreVirtualKeyStates` は `[Flags]`（`None`=0 / `Down`=1 / `Locked`=2）で、`!= None` と
  書くと `Locked` にも一致する。Ctrl が `Locked` と報告される環境が実在し
  （切断中の RDP セッションで実測）、そこでは Ctrl に触れていなくても閉じるボタンで
  プロセスが終了していた ── **常駐バッファリングごと落ちる**ため中核価値を静かに損なう。
- タスクトレイアイコンのコンテキストメニュー（右クリック）は `WindowManager.TrayIconContextMenu`
  （`OnTrayIconContextMenu`）イベントで `MenuFlyout` を組み立てて表示する（項目: **Show** / **Quit**）。
  実際にアプリを終了する（`ExitApplication`）際は、事前に `_allowRealClose = true` を設定して
  `Closing` のトレイ格納挙動を解除する。
  **これはシェルの Win32 メニューではなく WinUI の `MenuFlyout`** なので、アプリのプロセスが
  所有する `Microsoft.UI.Content.PopupWindowSiteBridge` として現れる ── そのおかげで
  UIA から中身を読める（自動テスト `TrayMenuTests`）。
  **`ExitApplication` は Ctrl+閉じる（`OnAppWindowClosing`）とは別の経路**で、
  この右クリックメニューからしか通らない。
- **終了経路は2つとも `BeginShutdown()` を通る。** ここで
  ①「終了を決めた」旗（`_shuttingDown`）を立て → ②キーを解除する、という順序と、
  **`Activated` の購読を外さない**ことがこのメソッドの本体である。
  購読を外すと、キー解除の直前にキーの持ち主を見終わっていたランチャーのリダイレクトが
  **購読者不在で痕跡ゼロに消え**、そのランチャーは結果通知を上限（最大60秒）まで待って
  「成否不明」の `2` を返す。購読を残せば同じリダイレクトへ**「一度も実行していない」`5`**
  を即答できる（`ExitCode_WorkerShuttingDown`）。`_shuttingDown` は
  `TryEnqueue` が `false` を返す区間より**手前**を受け持つ ──
  終了を決めてから `DispatcherQueue` が実際に止まるまでの間はまだキューが受け付けるので、
  そこへ積むと「委譲できた」ように見えて結果が返らない。
  ソース上の順序は L1 の `ShutdownRedirectHandlingTests` が固定している
  （**実行では守れない** ── 隣り合う2文の間へリダイレクトを差し込むのは
  プロセス間のレースそのもの）。

「Activateせずサイレントに処理する」結果の通知には、WinUIExのトレイ機能とは別に
Windows App SDK標準のトースト通知（`AppNotificationManager`）を使用している
（`SingleInstance/Notifications.cs`）。

> **既知の制約**：Windows App SDK 2.3.1のセルフコンテインド（アンパッケージ）配布では、
> `AppNotificationManager.Register()` が `Microsoft.WindowsAppRuntime.Insights.Resource.dll`
> を読み込めず `COMException (0x8007007E)` で失敗することがある。このDLLはインストール
> 済みのWindows App Runtime（AppXフレームワークパッケージ）内にのみ存在し、セルフコンテインド
> 配布用のどのNuGetパッケージにも含まれていないことを確認済み（`dotnet build`/`dotnet publish`
> のいずれでも再現し、Debugビルド固有の問題ではない）。Windows App SDK側の既知の不具合
> ([microsoft/WindowsAppSDK#6071](https://github.com/microsoft/WindowsAppSDK/issues/6071)、
> 1.8.6/2.1でも報告あり・未解決)と見られる。
>
> トースト通知はあくまで補助的な結果通知であるため、`SingleInstanceManager`側で
> `Notifications.ShowToast` の失敗をベストエフォートで握りつぶし（`TryShowToast`）、
> コマンド本体の処理結果・終了コードには影響しないようにしている。そのため上記の環境では
> **トースト通知は表示されないものの**、録画・ログ記録・終了コードはすべて正しく動作する。

## 引数の仕様：System.CommandLine + コマンドレジストリ方式（拡張しやすい設計）

起動引数は将来増えていくことを想定し、[System.CommandLine](https://github.com/dotnet/command-line-api)
（2.0.10、MIT License、.NET Foundation / dotnet/command-line-api）を使ってサブコマンド形式で
解析している（`ProcessRecorderApp/ActivationCommands.cs`）。

```
起動引数（コマンドライン文字列）
  │  ActivationTokenizer.ExtractCommandLine / Tokenize / StripExecutablePath
  │  … 文字列をトークン配列 string[] に分解（引用符対応）するだけ
  ▼
string[]
  │  ActivationCommands.Parse()
  │  … System.CommandLine の RootCommand/Subcommands で解析し、
  │    マッチしたサブコマンドの SetAction を実行、CommandOutcome を返す
  ▼
CommandOutcome { ShowWindow, ToastTitle, ToastMessage, ConsoleOutput, ConsoleError, ExitCode }
  │  SingleInstanceManager.HandleActivation()
  │  … ShowWindow なら ShowMainWindow()、ToastTitle/Message があれば Notifications.ShowToast()、
  │    ConsoleOutput/ConsoleError/ExitCode は ReportCommandResult() でランチャーへ返す
  ▼
実際の画面反映・呼び出し元コンソールへの出力・終了コード
```

- `SingleInstance/ActivationTokenizer.cs`: コマンドライン文字列を `string[]` に分解するだけ
  （トークナイザー）。実際のコマンド名・オプション・引数の意味づけは一切行わない。
- `ActivationCommands.cs`: **System.CommandLine の `RootCommand`/`Command` を組み立てて
  解析・実行するレジストリ**。コマンド体系そのものは `BuildRootCommand(Action<CommandOutcome> setOutcome, out ...)`
  が組み立て、`Parse()`（常駐ワーカーからの実行用）と `TryHandleInLauncher()`（ランチャーからの
  ヘルプ/バージョン/パースエラー判定用、後述）の両方から共有される。新しい引数（サブコマンド）を
  追加したい場合は、他のサブコマンドと同様に `new Command(...)` を作り `SetAction` の中で
  `setOutcome(...)` を呼んで結果を設定し `rootCommand.Subcommands.Add(...)` するだけでよい。

現在登録済みのコマンド:

| コマンド | ウィンドウ表示 | 内容 |
|---|:---:|---|
| （サブコマンドなし） | ✕ | Silent（ウィンドウの表示はタスクトレイアイコンの「Show」から行う） |
| （未知のサブコマンド・引数のパースエラー） | ✕ | ランチャーがエラー表示して終了コード`4`で終了。常駐ワーカーへはリダイレクトされない |
| `ping` | ✕ | `activity.log` に記録するだけ（「通知しない引数」の例） |
| `activate` | ○ | ウィンドウを表示するだけ |
| `start-recording-all` / `stop-recording-all` | ✕ | 全レコーダーの録画開始／終了。stdout に `名前\tファイル名` の一覧（**どちらも出すのは今回開始／停止した分だけ**。対象外のレコーダーの `LastFilename` は前回の録画を指すため。開始／停止できなかったレコーダーは名前つきで標準エラーへ出る） |
| `start-recording <target>` / `stop-recording <target>` | ✕ | 個別レコーダーの録画開始／終了。`target` は数値ならインデックス(0始)、それ以外は名前 |
| `status` | ✕ | レコーダーごとの状態を1行ずつ出力（8 列。自由記述の障害は必ず最後）。不健全なものがあれば終了コード `15`（後述）。**`録画中` は `EventRecorder.IsRecording` の実体を出す** ── VM の同名プロパティは復帰待ちを畳んだ表示用の値なので使わない。復帰待ちは独立した列にする（畳むと「いまフレームが録れているか」を機械から判定できない） |
| `--set Key=Value`（`Recursive`、繰返可） | — | `EventRecorder.TemplateVariables` へ設定（他コマンドと併用可）。**セッション限り** |
| `--get [Key...]`（`Recursive`） | — | テンプレート変数を取得（`Parse()` 内でコマンド本体実行後に処理） |
| `--persist Key`（`Recursive`、繰返可） | — | その変数を settings.json に残す（`--set` の直後・コマンド本体より前に処理）。未定義キーは終了コード `11` |
| `--no-persist Key`（`Recursive`、繰返可） | — | 永続化の指定を外す。変数自体はセッション中は残る |

#### レコーダー名による解決は「完全一致・先勝ち」

`start-recording <target>` / `stop-recording <target>` の名前解決は**序数での完全一致・先勝ち**
（数値はインデックスとして解釈し、名前へはフォールバックしない。規則は
`GStreamer.GstSharpNet/RecorderCliRules.ResolveTargetIndex` にあり L1 が守る）。
したがって**同名のレコーダーが2つあると、2つ目には CLI から永久に到達できない**
（画面上は普通に2件並んで見えるため、「コマンドが効かない」ではなく
「毎回1つ目が動く」という気付きにくい形で現れる）。

これを防ぐため、**追加時と UI からの改名時に名前を一意化する**
（`GStreamer.GstSharpNet/RecorderNaming.cs` の `MakeUnique`。衝突したら ` (2)` / ` (3)` … を付ける）。
規則は純粋関数として切り出してあり L1（`RecorderNamingTests`）が守る。
比較は序数なので `Recorder` と `recorder` は衝突しない ── CLI の解決も序数であり、
どちらにも到達できるものを勝手に改名しないため。

**モデル発の反映（設定ファイルの読み込み・モデル側の変更の写し）では一意化しない。**
手で編集した `settings.json` に重複があっても改名は走らない
── 起動しただけで設定ファイルを書き換えないことを優先している
（デバウンス自動保存が入っているため、改名すればそのまま永続化される）。

#### `--get` の出力

複数のキーは `--get A --get B` と**繰り返し指定**する。`--get A B` は通らない
（`AllowMultipleArgumentsPerToken` を有効にしていないため。有効にすると
`--get A ping` の `ping` までキーとして飲み込まれ、`Recursive = true` にして
サブコマンドと併用できるようにした意味が失われる）。

- キー指定あり: 値のみを**指定した順に**1行ずつ標準出力へ。
  未定義のキーは行を出さず、代わりに標準エラー出力へ
  `Cli_VariableNotFound`（キー名を含む）を1行出し、終了コード `11` を返す
  ── 未定義のキーは stdout の行数が減るだけなので、行の位置から特定できない。
- キー指定なし: 全件を `Key=Value` 形式で標準出力へ（キーの序数昇順）。
  1件も無い場合は標準エラー出力へ `Cli_NoVariables` を出す。
  **これは失敗ではないので終了コードは変えない**（stdout は機械可読な形のまま保つ）。

#### `status` の出力

標準出力は**1行1レコーダー**で、TAB 区切りの8列:

```
名前	初期化済み	録画中	復帰待ち	直近のファイル	常時録画	常時録画のファイル	直近の障害
R1	True	False	False	C:\rec\R1_120050511.mp4	on	C:\rec\R1_c00003.mp4	
R2	False	False	False		off		ERROR: pipeline doesn't want to play.
```

- 真偽値は `bool.ToString()`（`True`/`False`）で、**カルチャに依存しない**。
- **常時録画の列は1語**（`off` / `on` / `pending` / `error`。`RecorderCliRules.ContinuousState`）。
  自由記述の列を2つ持てないため畳んである ── 理由は `ContinuousLastError`（PropertyGrid）と
  `activity.log` の `continuous.*` にある。`pending` は「有効だが最初のフレームがまだ」で、
  「そもそも設定していない」（`off`）とは別のこと。
- **常時録画の状態は終了コードに効かない。** 常時録画の失敗がイベント録画の健全性を
  汚さないのが隔離契約で（`IsHealthy` は見ていない）、既存のスクリプトの挙動も変えない。
- **自由記述である「直近の障害」は必ず最後の列**に置く。途中に置くと、理由文に TAB が
  混ざったときに後続の列の意味がずれる。改行と TAB は空白へ潰し、1レコーダー＝1行を崩さない
  （`ActivityLog` と同じ規約）。
- 初期化されていない、または直近の障害が残っているレコーダーが1つでもあれば、
  **どれがなぜ不健全なのか**を標準エラー出力へ1行ずつ出し（`Cli_RecorderError`）、
  終了コード `15` を返す ── `--get` の未定義キーと同じ考え方で、
  「不健全なものがある」だけでは調べようがないため。
- **「直近の障害」は今も壊れているという意味ではない。** `EventRecorder.LastError` は
  次の録画開始で消えるので、ここに出るのは「最後に起きたことが障害だった」ことを表す。
- レコーダーが1件も無い場合は標準出力・標準エラーとも空で終了コード `0`
  （「不健全なレコーダーは無い」が正しい答え）。

**過去の障害を理由に `start-recording` を失敗させることはしない。** `status` は
観測のためのコマンドで、既存のスクリプトの挙動は変えない（意図的な仕様）。
`start-recording` が失敗するのは今この瞬間に開始できないとき（`14`）だけ。

> 「トースト通知を伴うコマンド」を追加する場合は `CommandOutcome.SilentWithToast(...)` を
> `SetAction` から返せばよい。

`ActivationCommands.Parse` は、実処理（`ParseCore`）の後に必ず `cli` イベントを
`activity.log` へ記録する（コマンドラインと終了コード）。**記録するのは常駐ワーカー側だけ**で、
ランチャーが自前で処理する `--help` / `--version` / パースエラーは記録されない
── `activity.log` の書き手を1プロセスに限るための意図的な設計（後述）。

録画コマンド（`start-recording[-all]`/`stop-recording[-all]`）は、常駐ワーカー起動直後の
コールドスタート時に録画エンジン（`GstControllerViewModel.Current`）がまだ生成されていない
可能性があるため、`WaitForControllerAsync`（最大8秒、`ReadyWaitTimeout`。ランチャー側の
結果待ちタイムアウト **60 秒**より短く設定し、通知の猶予を
残す）で UI スレッドを解放しつつ準備完了を待つ。

待ち条件は `Current is not null` ではなく **`Current is { IsReady: true }`**。`Current` が
非 `null` でも、`Recorders`（`Controller.Recorders` のディスパッチャ経由ミラー）への挿入が
まだキューに残っている可能性があり、`start-recording-all` が「開始できるレコーダーが無い」
（終了コード `14`）と誤判定しうる。`IsReady` は ctor 末尾の `TryEnqueue` で立てる
（`TryEnqueue` は投入順に実行されるため、挿入群の後に必ず走る）。

System.CommandLineを使うことで、`--help` の自動生成、型安全な引数（`Argument<T>`/`Option<T>`）、
存在しないサブコマンドや型の合わない値を指定した場合のエラーメッセージなども無償で得られる
（パースエラーはランチャー側の `TryHandleInLauncher` が検出してエラー表示＋終了コード`4`で終了する。
常駐ワーカー側の `Parse` に万一パースエラーの引数が届いた場合も、Activateせず `Failure` として扱われる）。

### ヘルプ表示（`--help`）・バージョン表示（`--version`）・不明な引数のエラーはランチャー側で処理する

常駐ワーカーは非表示のバックグラウンドプロセスであり、コンソールを持たない（または、既に
起動済みの常駐ワーカーへリダイレクトされた場合は、呼び出し元とは全く別のコンソールに
紐づいている）ため、`ActivationCommands.Parse()` を常駐ワーカー側で実行して `--help`/`--version`
やパースエラーメッセージの出力を得ても、それを呼び出したユーザーのコンソールには届かない。

そのため `--help`/`-h`/`-?` 等のヘルプ表示要求、`--version` によるバージョン表示要求、および
不明なサブコマンド・引数（パースエラー）だけは特別扱いし、`SingleInstanceManager.Run` の先頭
（名前付きMutexの取得や常駐ワーカーとの通信を行う前）で
`ActivationCommands.TryHandleInLauncher(rawArgs)` を呼び出し、該当する場合は
ランチャー自身がその場で表示して終了する（常駐ワーカーへは一切リダイレクトされず、
未起動の場合に常駐ワーカーが起動されることもない）。終了コードは、
ヘルプ/バージョン表示なら `0`、パースエラーなら `4`（`ExitCode_InvalidArguments`）。

`TryHandleInLauncher` は `BuildRootCommand` で組み立てた同じコマンド体系を使って引数を
パースし、`ParseResult.Action` が `System.CommandLine.Help.HelpAction`（ヘルプ）かどうか、
または `System.CommandLine.VersionOption`（バージョン）を宣言元に持つかどうか、および
`ParseResult.Errors`（パースエラー）の有無で判定する。
`--version` の実際のアクション型 `VersionOption.VersionOptionAction` は `internal` なネスト型で
直接参照できないため、`Action.GetType().DeclaringType == typeof(VersionOption)` という
公開型経由の比較で判定している。`process --help` のようなサブコマンド単位のヘルプ要求も
同様に判定できる。

#### 補足：コンソールサブシステム（OutputType=Exe）と終了コード・コンソール出力

このアプリは WinUI3 アプリだが、`OutputType=Exe`（コンソールサブシステム）としてビルドしている。
GUIサブシステム（`OutputType=WinExe`）だと、**対話コマンドプロンプト（cmd.exe）がプロセスの終了を
待たずに即座にプロンプトへ戻る**ため、`echo %ERRORLEVEL%` で終了コードを取得できない
（バッチファイル内や `start /wait` 経由では取得できるが、対話プロンプトの直接実行では取得できない）。
コンソールサブシステムであれば、対話プロンプトからの直接実行でもシェルがプロセスの終了を待ち、
`%ERRORLEVEL%` / `$LASTEXITCODE` をそのまま取得できる。また、標準入出力を呼び出し元コンソールから
自然に継承するため、ヘルプ/エラーメッセージの表示に `AttachConsole(ATTACH_PARENT_PROCESS)` を
呼び出す必要もない。

トレードオフとして、エクスプローラーやスタートアップなどGUIからの起動時にはコンソールウィンドウが
一瞬表示されるが、ランチャーは処理完了後すぐに終了するため許容している。常駐ワーカーは
`WindowStyle = Hidden` で起動されるため、コンソールウィンドウは表示されない。

なお、常駐ワーカーの起動には `UseShellExecute = true`（ShellExecuteEx経由）を使用している。
`UseShellExecute = false`（CreateProcess直接）だと、.NETは常に `bInheritHandles=TRUE` で子プロセスを
生成するため、呼び出し元シェルがパイプやリダイレクトを使っていた場合（例: `app.exe | find "x"`）に
そのハンドルが長時間稼働する常駐ワーカーへ漏れ、ランチャー終了後もシェルが常駐ワーカーの終了まで
待ち続けてしまうためである（ハンドルを継承しない ShellExecuteEx 経由ではこの問題は起きない）。

## UIA トリガ（UiaTrigger 連携）

別アプリの UI 要素の出現・削除・プロパティ変化（UI Automation）を、テンプレート変数の更新と
録画の開始・停止のトリガにする。監視はライブラリ
[UiaTrigger](https://github.com/masa-iwm/UiaTrigger)（`UiaTrigger.Core` /
`UiaTrigger.Picker.WinUI`、NuGet 参照）が行い、アプリ側で UiaTrigger の型に触れるのは
`Services/UiaTriggerService.cs` と `MainPage.BuildSettingsValueAsync`（編集ボタン）の 2 箇所だけ。
発火 1 回を「書くべき変数」「実行すべきアクション」へ写す規則は
`Components/TriggerFiringRules.cs`（純関数）にあり、L1（`TriggerFiringRulesTests` /
`TriggerAssignmentReconcilerTests`）が守る。

### 発火で何が起こるか

- **変数の反映（常時・無条件）**: `{トリガID}` = NewValue を
  `EventRecorder.SetTemplateVariable` へ書く。句が 2 つ以上ある複合トリガでは、値が読めた句
  （Matched / NotMatched）だけ `{トリガID.句名}` = 句の値も書く
  （Unreadable / NotEvaluated は書かない ── 読めていない値で既存の変数を潰さない）。
  書いた変数は Variables 画面とファイル名テンプレートから見える。
- **録画アクション（設定したものだけ）**: `UiaTriggerAssignments`（トリガ ID ごとの行）の
  Action と対象レコーダー（空欄 = 全レコーダー一括）に従う。
  行の増減はトリガ一覧の編集に自動追随する（`TriggerAssignmentReconciler`。
  手動で行を増減する UI は無い）。

処理順は**変数 → アクション**。テンプレートの展開は録画開始の瞬間（`EventRecorder.Start`）
なので、この順でだけ「発火した値がその録画のファイル名に載る」が成立する。

#### エッジ（立ち上がり／立ち下がり）

トリガはピッカーで「停止時も通知」を入れると、**条件が成立しなくなったとき**にも発火する
（`TriggerOn.WhileMatching` のときだけ設定できる）。アプリはこれを
`TriggerFireEdge`（`Rising` / `Falling`）へ写し、割り当てごとに実行する操作を決める:

| Action | 立ち上がり（条件成立） | 立ち下がり（不成立化） |
|---|---|---|
| なし | — | — |
| 開始 | 録画を開始 | **—** |
| 停止 | 録画を停止 | **—** |
| 条件成立中のみ録画 | 録画を開始 | 録画を停止 |

**「開始」「停止」を立ち下がりで実行しないことが要点。** エッジを見ないと、
「停止時も通知」を入れたトリガに「開始」を割り当てたときに**要素が消えた瞬間にも録画が始まる**。
規則は `TriggerFiringRules.ResolveActions` にあり L1 が守る。写像は「立ち下がり以外はすべて
立ち上がり」なので、ライブラリのライフサイクルが増えても安全側に落ちる。

変数の反映はエッジに関係なく常に行う（立ち下がりでも `NewValue` は入る ──
要素が消えた場合は最後に見えた値）。

### スレッドと寿命

- `UiaTriggerService` は常駐ワーカーで 1 つ（`App.OnLaunched` で生成、`AppWindow.Destroying` で
  **エンジンより先に** Dispose ── 以後レコーダーへ新規アクションを積ませない）。
- 発火は UiaTrigger の監視ワーカースレッドで直列に届く。変数ストアと `ActivityLog` は
  スレッドセーフなのでそこから直接呼び、録画アクションだけを `TryEnqueue`（**戻り値を検査**。
  `SingleInstanceManager.OnActivationRedirected` と同じ規律）で UI スレッドへ運ぶ。
  開始・停止は必ず `CanStart/CanStopRecording(All)` を通す ── 通さないと例外か、
  排出待ち（`WaitForPendingStop`）で UI スレッドが最大約 10 秒固まる。停止後は
  `LastStopOutcome` を検査し、使えない成果物（Empty / NotFinalized）は成功として記録しない。
- 設定変更（トリガ定義・有効スイッチ）は「新モニタを `StartAsync` できてから旧を破棄」。
  定義エラー（`ArgumentException`）や壊れた設定では現状を維持し、アプリの中核（録画）を
  殺さない。トリガ 0 件・無効スイッチでは監視スレッド自体を作らない
  （E2E はトリガを設定しないので自動的に不活性になる）。
- **「条件成立中のみ録画」で自動開始した録画は追跡する**（`_autoStarted`。UI スレッド専用）。
  監視の構成が変わるたびに、**そのトリガの不成立化を今も追えるか**で止めるかどうかを決める
  （`ReconcileAutoStartedAsync`）。1 つの規則で 3 つの場合を覆う:
  監視を止めた → 全部止める／トリガ編集で入れ替わったが当のトリガは健在 → 止めない
  （`TriggerMonitorOptions.FireOnInitialMatch` が**既定 true**で再評価する）／
  **当のトリガが消えた・「停止時も通知」が外れた・割り当てが変わった → 止める**
  （立ち下がりが永久に来ないので、これが無いと録画が残り続ける）。
  `Dispose`（アプリ終了）では止めない ── `engine.Dispose()` が録画を確定させる。
  - **限界**: 追跡はレコーダー名の集合であって録画セッションの同一性ではない。
    トリガで始めた録画を手動で止め、手動で録り直すと、その録画も自動停止に巻き込まれうる。
  - モニタには**世代**（`_monitorEpoch`）を持たせ、退役したモニタの発火が後から実行されるのを弾く
    ── 発火の処理は `WaitForControllerAsync` を待つので、「開始が積まれた直後に監視が止まり、
    後始末が先に走り抜けてから開始が再開する」順序が成立しうる。そうなると
    **トリガを切ったのに録画が回り続け、追跡もされていない**状態になる。

### 設定の持ち方

- トリガ定義の正本は `AppSettings.UiaTriggers`（settings.json に内包。`TriggerDefinition` は
  素の POCO で、`AppSettingsJsonContext` のグラフ走査がそのまま扱う）。編集は Settings 画面の
  `UiaTriggerList` 行の「…」ボタン → `TriggerListEditorWindow`（**非モーダル**。開いている
  あいだは再入ガードで 2 枚目を開かせない）だけで、確定は**リストごとの差し替え**
  ── その `PropertyChanged` がデバウンス保存と監視の再起動を連鎖させる。
- `UiaTriggerList` 行は**現在の件数を表示するだけで直接は編集できない**
  （`[ReadOnly(true)]` ＋ `[ValueBuilder]` ＝「ビルダーでのみ変更できる」）。
  `PropertyGridView` の `BuilderTextEditTemplate` がこの意味を実装しており、
  読み取り専用でもテキストを灰色にせず（選択・コピーを残す）「…」は押せる
  ── ほかの編集種別は従来どおり無効化される。**意図的な差なので揃えないこと。**
  件数は `OnUiaTriggersChanged` と `OnLoaded` で同期する（settings.json に
  `UiaTriggers` キーが無いと setter が走らないので、読み込み完了時の 1 回が要る）。
- 割り当ての選択肢（Action・対象レコーダー）は `MainPage.ProvideChoices` が供給する
  （保存値は英字のまま・表示だけローカライズ。`PropertyGridChoice` の Value/Display 分離）。
  選択肢は**項目の生成時に 1 回だけ**確定するので、開いたままの一覧にはレコーダーの改名が
  反映されない（コレクションの変更や SelectedObject の再代入で作り直される）。
- **「条件成立中のみ録画」は、そのトリガが不成立化を通知できて初めて完結する。**
  できない設定のままだと「開始したのに止まらない」になるので、選択肢の表示に括弧書きで注記する
  （`TriggerAction_WhileCannotStop`。`PreferredH264Encoder` が実在しないエンコーダー名に
  注記するのと同じ形）。判定は `UiaTriggerService.CanCompleteWhileRecording`。
  **どの行の選択肢かを知る必要がある**ので、`PropertyGridView.ChoiceProvider` は
  キーと現在値に加えて**対象オブジェクト**（コレクションでは要素）を渡す。
  トリガを編集したら `ChoiceProvider` を再代入して項目を作り直す ── そうしないと
  ピッカーで「成立しなくなった時も通知」を変えても注記が古いまま残る。
- トリガ ID が `[\w.-]+`（.NET の `\w` は日本語を含む。ドット・ハイフンも可 ──
  ハイフンは UiaTrigger のトリガ ID に自然に入る）の外だと、変数は書かれるが
  テンプレートの `{キー}` から参照できない ── `trigger.name warn` をキーごとに 1 回記録する。

### 発火しないとき

監視はイベント購読式で、**相手アプリが UIA のイベントを上げなければ発火しない**。
プロパティによっては PropertyChanged を一切上げないアプリがあり、その場合はトリガ単位の
`PollInterval`（ピッカーで設定できる）で解決済み要素だけが読み直される。
**「条件成立中のみ録画」の立ち下がりも同じ制約を受ける** ── 止まらないときはまず
`PollInterval` の未設定を疑う（症状が「機能が壊れている」と見分けがつかない）。

「停止時も通知」を `WhileMatching` 以外のライフサイクルに付けた定義はライブラリが拒否し、
`StartAsync` が例外になる → `trigger.monitor fail` が出て**旧モニタが続投する**
（＝設定を変えたのに反映されない、という形で現れる）。

経緯の診断は activity.log の `trigger.*` イベントで行う:

| イベント | 意味 |
|---|---|
| `trigger.monitor start` / `trigger.monitor stop` / `trigger.monitor fail` | 監視の開始（トリガ数付き）・停止・起動失敗（定義エラー等。旧モニタは続投） |
| `trigger.fire` | 発火（ID・**エッジ**・NewValue）。`edge=Falling` が「条件が成立しなくなった」 |
| `trigger.resolve` | 対象要素の解決状態の変化 |
| `trigger.start` / `trigger.stop` / `trigger.stop failed` | 実行した録画アクション（停止は成果物の使える/使えないでイベント名を分ける）。監視の構成変更に伴う自動停止は `reason=monitor-stop` が付く |
| `trigger.assign warn` | 「条件成立中のみ録画」を割り当てたのに、そのトリガが不成立化を通知できない（`WhileMatching` ＋「停止時も通知」になっていない）── **開始しても止まらない** |
| `trigger.action skip` / `trigger.action fail` / `trigger.action drop` | Can* ガードで弾いた / 対象レコーダー不在・エンジン未準備 / ディスパッチャ停止中 |
| `trigger.name warn` | テンプレートから参照できないキー |
| `trigger.error` | 監視・発火処理の例外 |

## Log 画面のターミナル表示（xterm.js / WebView2）

Log 画面は既定で **WebView2 の中の xterm.js** に描く。`ListView` は WebView2 が使えないときの
フォールバックとして残してある。

### なぜ端末なのか

捕捉している出力は GStreamer のデバッグ出力そのもので、`\r` による行上書き（進捗表示）や
カーソル制御を含む。従来は `StreamReader.ReadLine()` で行に切っていたため、
**それらは UI へ届く前に失われていた**。生のバイト列をそのまま端末へ流せば、
解釈は端末がやる ── 256 色・TrueColor も自前パーサーの範囲に縛られない。

### 経路

```
パイプ → StandardStreamRedirector（生バイト→ UTF-8 増分デコード）
   ├→ LogBuffer（有界リング。唯一の保管場所）→ LogTerminalView →（post）→ xterm.js
   └→ IncrementalLineSplitter → debug ログファイル／元 stderr への複写
```

行に切るのはデバッグログファイルと元標準エラーへの複写のためだけで、表示側は通らない。

### 同梱アセット

`Assets/Terminal/` に置き、発行物へそのまま運ぶ（`ProcessRecorderApp.csproj` の `Content`）。
`vendor/` の中身は上流の配布物を**無改変**で置いたもので、取得元・版・SHA256 は
[`Assets/Terminal/SOURCES.md`](ProcessRecorderApp/Assets/Terminal/SOURCES.md) が正本。
UMD なのでバンドラーは要らず、グローバル名は `Terminal` / `FitAddon.FitAddon` /
`WebglAddon.WebglAddon`。

- **`licenses/third-party/` には入れない。** あちらは同梱 GStreamer 専用の台帳で、
  `ThirdPartyLicenseTests` がディスクと `SOURCES.tsv` を、`release.yml` が `COMPONENTS*.tsv` と
  発行物の `runtimes/win-x64` を双方向で突き合わせる。xterm.js は同梱・非同梱の**両方**に
  入るので、載せると必ず赤になる。ライセンス文は `Assets/Terminal/vendor/` に同梱する。
- **`ExcludeFromSingleFile=true` は必須。** `SetVirtualHostNameToFolderMapping` は
  ディスク上の実ディレクトリしか受け取らないので、単一ファイル発行でバンドルへ埋めると解決できない。
- `.gitattributes` で `-text` にしてある。改行を書き換えると `SOURCES.md` の SHA256 が合わなくなる。

### 起動の順序（この順序でないと成立しない）

1. Log 画面が表示されたときに初めて初期化する（`IsActive`）。
   見ていない画面のためにブラウザープロセスを起こさない。
2. `CoreWebView2Environment.GetAvailableBrowserVersionString()` → 空/例外ならランタイム不在。
   ここで諦めればブラウザーを 1 本も起こさずに済む。
3. `DefaultBackgroundColor` を **`EnsureCoreWebView2Async` より前に**入れる
   （コントローラー生成時に一度だけ流し込まれる。既定は白なので入れないと黒画面に白が閃く）。
4. ユーザーデータフォルダーは `%LOCALAPPDATA%\ProcessRecorderApp\WebView2`。
   既定（実行ファイルの隣）のままだと `Program Files` 配下で書き込めず初期化ごと失敗する。
5. **初期化は一度きり**（`_initAttempted`）。失敗経路では WinUI 内部の生成中フラグが戻らず、
   2 回目の `EnsureCoreWebView2Async` は永久に返らない。
6. `EnsureCoreWebView2Async` が**正常完了しても `CoreWebView2` が null のまま**という経路がある
   ── ランタイム不在の主経路はこれで、例外ではない。
7. `SetVirtualHostNameToFolderMapping` で `https://log-terminal.invalid/` に写して `Navigate`。
   `file://` は使わない。
8. ハンドシェイクは 3 往復: JS `h`（スクリプトが走った）→ C# `i`（配色と上限）→
   JS `y`（端末が出来た）→ C# `w`（ここで初めて本文）。
   `y` より前に本文を送らないのは、初期サイズ 80×24 のまま書き込むと長い行が誤った桁で折り返されるため。
9. JS 側は **`term.open()` を `loadAddon(WebglAddon)` より前**に呼ぶ。逆にすると
   `activate()` が `onWillOpen` へ遅延され、**try/catch を素通りして DOM レンダラーへ落ちられない**。

`AddHostObjectToScript` は使わない（IDispatch/リフレクション経由で Native AOT では成立しない。
`NativeSwapChainPanel` と同じ方針）。ブリッジは `PostWebMessageAsString` と
`WebMessageReceived` だけで、区切りは U+001F。JSON を通さないので
`JsonSerializerContext` を増やさずに済む。

### 背圧と上限

`PostWebMessageAsString` はプロセスを跨ぐマーシャルで、`term.write()` は JS 側の内部キューに積む。
どちらも投げっぱなしにすると洪水でキューが片側だけ伸びる。そこで
**33ms 周期で 1 通ずつ送り、`term.write` のコールバックが返す ack を待ってから次を送る**。
ack が 5 秒来なければ 1 通落ちたものとして進め、3 回続いたらリスト表示へ落ちる
（`LogTerminalView.AckTimeoutMilliseconds` / `MaxConsecutiveAckTimeouts`）。

カーソルは**絶対位置**（追記した総文字数・総行数）なので、ack を待つあいだにバッファが
溢れても特別扱いが要らない ── 次の `Read` が破棄行数を返し、印が正しく入る。

| 上限 | 単位 | 既定 | 持ち主 |
|---|---|---:|---|
| `MaxLines` | セグメント（＝行） | 10000 | `AppSettings.LogScrollbackLines` → `LogBuffer.MaxLines` |
| `MaxChars` | 保持文字総数 | 8MiB | `LogBuffer`。改行を一切吐かない生産者ではテールが無限に伸びるため必要 |
| `MaxSegmentChars` | 1 行の長さ | 64KiB | `LogBuffer`。超えたら改行を待たずに確定する |
| `scrollback` | xterm の行数 | `MaxLines` と同値 | JS。ずらすと退避点が 2 つになり「N 行破棄」が嘘になる |

**破棄マーカー（`Log_LinesDropped`）が数えるのは「一度も表示側へ渡らないまま消えた行」だけ**である。
表示済みの行がスクロールバックから押し出されるのは欠落ではないので数えない。

### フォールバック

初期化の失敗（上記 2/3/6）・`CoreProcessFailed`・JS 側の致命的エラーのいずれでも
`FallBackToListView()` に集約する。`ViewModel.IsLogFallbackActive` が立つと、
XAML がターミナルを畳んで注記と `ListView` を出し、`ListViewCopyBehavior.IsActive` を true にする。

**`ListViewCopyBehavior.IsActive` は必須。** `KeyboardAccelerator` は `ScopeOwner` を持たない
＝ウィンドウ全域に効くので、ターミナル表示中に有効だと向こうの選択コピー（Ctrl+C）を
横取りして `Handled` にしてしまう。

フォールバックでも上限・破棄マーカー・全文コピー・自動スクロールは効く。失われるのは
`\r` による行の**部分**上書きだけで、`LogText.TakeAfterLastCr` により「最後の `\r` 以降」を採る
（`ListView` は行を書き換えられないため。既知の制約）。

### コピー

- 選択範囲は **Ctrl+C と右クリックメニューの「コピー」**の両方から採れる。どちらも
  JS が範囲を C# へ返し、**クリップボードへ入れるのは C# 側**
  （`navigator.clipboard` の可否に依存させない）。文言はリスト表示のときと同じ
  `Controls/ControlsResources/Common_Copy` を引く。
  - 右クリックメニューは WebView2 の既定メニューを**無効にするのではなく**、
    `ContextMenuRequested` で中身を「コピー」1 項目に差し替える
    ── 無効にすると `ContextMenuRequested` 自体が来なくなり、独自の項目も出せない。
  - 項目は**常に有効**にする。メニューは同期に組み立てるので選択の有無を
    その場で問い合わせられず、状態を先回りで持つと「選択しているのに押せない」形で壊れる。
- 「すべてコピー」の正本は **`LogBuffer.Snapshot()`** であって端末の描画バッファではない
  ── 後者はそのときのウィンドウ幅で折り返された結果なので、貼り付け内容がウィンドウサイズで
  変わってしまい、フォールバック経路では実装もできない。
  整形は `AnsiEscape.Strip` → `LogText.FlattenCarriageReturns` → `TrimEnd()`。

### UIA からは中身が読めない

WebView2 はブラウザープロセス側に別の UIA ツリーを持ち、WebGL レンダラーでは文字が
GPU テクスチャになるため**アクセシブルテキストが 1 つも出ない**。E2E は
`AppUi.OpaqueSubtrees` でこのサブツリーへ降りない。表示内容の確認は目視で行う
（[docs/coverage-gaps.md](../docs/coverage-gaps.md) の「Log 画面への表示経路」）。
代わりに `activity.log` の `log.terminal` が「起きたこと」と「どちらのレンダラーか」を残す。

## 設定・ログの保存先

- アプリ設定: `%LOCALAPPDATA%\ProcessRecorderApp\settings.json`
  （`Settings/AppSettings.cs`。`JsonSettingsBase<T>`（`Components`）による source-generated JSON。
  ウィンドウサイズ、Preview 画面のプロパティペイン幅・折りたたみ状態、`NavigationView` の
  ペイン表示モード、レコーダー削除時の確認要否、レコーダー一覧（`EventRecorderSettings`）、
  保存先と自動削除（`OutputDirectory` / `RecordingRetentionDays` /
  `RecordingCleanupIntervalHours`）、テンプレート変数（`TemplateVariables`）、
  UIA トリガ（`UiaTriggers` / `UiaTriggerAssignments` / `UiaTriggersEnabled`）等）。

  **この場所に `settings.json` が無いときだけ、実行ファイルの隣の `settings.json` を
  「種」として読む**（`AppSettings.SeedFilePath` → `JsonSettingsBase.LoadOrCreate` の
  `seedFilePath`）。配布物へ初期設定を同梱して「展開して起動すれば構成済み」にするための口で、
  読んだことは `activity.log` の `settings.seed` に 1 行残る。

  規則は 3 つ:

  - **条件は「本体が無い」であって「本体が壊れている」ではない。** 壊れていたら
    従来どおり退避して既定値へ倒す ── ここで種へ倒すと、一時的に読めなかっただけの
    利用者の設定が同梱物で黙って置き換わる。
  - **種は読むだけ**（複写も退避もしない）。本体は最初の保存で生まれる。
    種が壊れていても `.bad` へ退避しない ── 退避は `File.Move` であり、
    実行ファイルの隣は読み取り専用でありうるし複数の利用者で共有されうる。
  - **見るのは `AppEnvironment.DataDirectory` の不在**であって `%LOCALAPPDATA%` の不在ではない。
    こうしてあるので `PROCESSRECORDERAPP_DATA_DIR` による E2E の隔離がそのまま効く。
    種の在り処は `AppDirectories.BaseDirectory`（`Environment.ProcessPath` のディレクトリ。
    単一ファイル発行で展開先を指す `AppContext.BaseDirectory` は使わない）。

  読めた種には `IsFirstRun=false` を与える ── 種は「初回の既定値」であって
  「初回そのもの」ではないので、初回だけ働く `OnLoaded()` の処理を種の内容へ重ねない。
  `Reload()` にも同じ種を渡してあるが、**本体が在れば効かない**ので、
  再読み込みで種が読まれるのは「手で `settings.json` を消した」場合だけである。

  **書式は「人が開いて手で直せること」を優先している** ── インデント付き・非 ASCII を
  `\uXXXX` へ逃がさない・UTF-8（BOM 無し）。指定は `AppSettings.SettingsTypeInfo` の
  1 か所（`Encoder` は属性では書けないのでコンテキストをインスタンス生成している）。
  裏返しの制約として、**手で直した後に ANSI／Shift-JIS で保存すると壊れた JSON になり**、
  `JsonSettingsBase.LoadOrCreate` がそれを黙って既定値へフォールバックする
  （＝全設定が初期化されたように見える）。編集は UTF-8 のまま保存すること。

  **列挙は名前で書く** ── `PaneDisplayMode`（`"Top"` 等）とレコーダーの `Type`
  （`"System"` / `"D3d12"`）。数値だと手で開いても意味が読めず、宣言の並びを変えた瞬間に
  既存ファイルの意味が黙って変わる。変換器は `JsonStringEnumConverter<T>` の**総称版**
  （非総称版は実行時リフレクションを要求するので Native AOT で使えない）を、
  自前の列挙は型に・WinUI の列挙はプロパティに指定している。
  **読み取りは数値も受ける**ので、数値で書かれた古いファイルもそのまま読める
  （`DataVersion` は列挙ではないので整数のまま）。

  手で直す側の支えとして、**同じディレクトリへ JSON Schema を随伴させる**
  （`settings.schema.json`。`JsonSettingsBase.SaveSchema` が設定本体を書いた直後に書き、
  内容が同じなら書き直さない）。生成は `System.Text.Json.Schema.JsonSchemaExporter` で、
  **`JsonTypeInfo` を受ける多重定義だけを使う** ── `JsonSerializerOptions` と `Type` を
  受ける側は実行時リフレクションで契約を作るため Native AOT で警告になり、しかも
  本体の直列化とは別の解決器を通るのでソース生成側と食い違ったスキーマを出しうる。
  設定本体の先頭には `$schema`（`AppSettings.SchemaReference`）が入り、隣のファイルを
  **相対参照**で指す。これを実体のあるプロパティとして宣言しているのは、宣言しないと
  手書きの `$schema` が `ExtensionData` に落ち、書き出し側と合わせて**同じキーが2回出る
  壊れた JSON** になるため。読み込んだ値は上書きしない（別のスキーマを指せる）。

  同じ内容を [docs/settings.schema.json](../docs/settings.schema.json) としてリポジトリにも
  登録してある ── **設定の形の変更が差分としてレビューに乗る**ようにするため
  （プロパティの増減・列挙の値・型の変化がここに出る）。ずれは L2 の
  `SettingsSchemaTests` が検出する。更新はアプリを一度起動して設定を保存し、
  書かれた `settings.schema.json` で上書きする。

  保存契機は3つ: **ウィンドウ破棄時・トレイ格納時・変更から約1秒のデバウンス**
  （`AttachAutoSave`。`AppSettings.PropertyChanged` / `Recorders.CollectionChanged` /
  `EventRecorder.TemplateVariablesChanged` が契機）。
  デバウンスが無いと、**トレイ常駐中の変更が強制終了で失われる**
  ── 常駐アプリなので「ウィンドウを閉じて終わる」とは限らない。
  実際の書き込みは `DispatcherQueue` で UI スレッドへ戻して行う（`Recorders` は
  UI スレッドが変更する `ObservableCollection<T>` のため）。
  なお `TemplateVariablesChanged` を契機に残してあるのは、**変数の値が変わったときに
  永続化指定済みのキーの値を書き直すため**。`--set` しただけの変数が保存されるわけではない
  （`Save()` が既に載っているキーしか触らない。上記「ファイル名テンプレート」を参照）。

  `DataVersion` は **1**。**未リリースで利用者がいないため、後から増えたプロパティ
  （`PreferredH264Encoder` / `StopFinalizeTimeoutMs` / `TemplateVariables`）も
  すべて版 1 の一部として扱う** ── 移行すべき既存ファイルが世の中に無い以上、
  開発中の増減を版に反映しても意味が無いため。いずれも加算的なので、
  それらが無い古い開発中のファイルも既定値でそのまま読める。
  `OnLoaded()` の先頭に `Migrate()`（現時点は版を揃えるだけの no-op）を置いてあり、
  **リリース後に非加算的な変更を入れるときはここに書く**（そのとき初めて 2 へ上げる）。

> **`AppSettings.Reload()` に追記を忘れないこと。** 全プロパティを手書きでコピーしており、
> 追記漏れは「`Reload()` で黙って既定値に戻る」形で現れる。これは
> `AppSettingsReloadTests.EveryPersistedProperty_IsCopiedByReload`（L1）が守っている
> ── `ProcessRecorderApp.Tests` は WinUI アプリプロジェクトを参照できないため、
> ソースをテキストとして読む方式。
- アクティビティログ: `%LOCALAPPDATA%\ProcessRecorderApp\activity.log`
  （`Components/ActivityLog.cs`。詳細は次節）。
- アプリ内 Log 画面の内容は上記とは別で、`Components/StandardStreamRedirector.cs` が
  GStreamer のネイティブ/マネージド標準出力・標準エラー出力を捕捉し（CsWin32の
  `CreatePipe`/`GetStdHandle`等を使用）、`Components/LogBuffer.cs`（**有界リング**）へ
  流し込んだものを表示している（既定では永続化されない。DEBUGビルドのみ `debug.txt` にも記録）。
  保持行数の上限は `AppSettings.LogScrollbackLines`（既定 10000、100〜1,000,000 に丸める）。
  **`Reload()` への複写を忘れないこと** ── `AppSettingsReloadTests` が全プロパティを機械的に見る。

保存先の実体は `Components/AppEnvironment.cs` の `DataDirectory` が解決する（後述）。

## `activity.log`（アクティビティログ）

「いつ録れて、いつ失敗したか」がプロセス終了後も残ることを目的とするユーザー向けのログ。
実装は `Components/ActivityLog.cs`（`Components` は全プロジェクトから参照できる唯一の適所）。

書式は固定・インバリアント。1イベント＝1行で、詳細に改行が含まれる場合（例外の
`ToString()` 等）はタブへ潰して行指向の読み取りを壊さない。L2 E2E が正規表現で読むため
**ローカライズしない**（＝ `.resw` キーを増やさない）。

```
2026-07-26 12:00:51.233 INFO recording.start recorder='R1' file='C:\rec\R1_120050511.mp4'
<yyyy-MM-dd HH:mm:ss.fff> <INFO|WARN|ERROR> <イベント名> <詳細>
```

記録するイベント（**契約**。増減させる場合は E2E の `ActivityLogFile.KnownEvents` と、
ルート README の activity.log の説明も同時に揃える）。
成功と失敗は**イベント名で分ける**（`recorder.init ok` / `recorder.init fail` 等）
── 同名にすると L2 が掛ける正規表現が失敗行にも一致してしまうため:

| イベント | 水準 | 記録される場所 | 内容 |
|---|---|---|---|
| `app.start` | INFO | `Program.Main`（ワーカー分岐） | pid とデータディレクトリ |
| `app.exit` | INFO | `Program.Main`（`StartResidentWorker` から復帰後） | pid と終了コード |
| `app.error` | ERROR | `App.LogException`（未処理例外の3ハンドラ）／`SingleInstanceManager.HandleActivation`（コマンド処理の予期しない例外。終了コード 99 と対で残る） | 発生源と例外の全文 |
| `gst.runtime` | INFO / **ERROR** | `Controller.StaticInitialize` | ローダーが勝った段（`selected=`。`GstInstallOrigin` の名前）・系統（`flavor=`。`MinGW` / `Msvc`）・ピンしたディレクトリ（`dir=`。ベアネームで解決した段では `(search-path)`）・実際にロードされた本体と GLib のパス（`core=` / `glib=`。どちらの命名でも探す）・混成の有無（`mixed=`）、末尾に人間向けの出所説明（`source=`）。**`source=` は空白を含む自由文なので必ず末尾に置く** ── 途中に置くと「次のフィールド名まで」で値を切る読み手（`RuntimeResolutionTests.Field` と `tools/Verify-GpuEncoders.ps1`）が壊れる。1回のみ。初期化に失敗した場合は ERROR で、ローダーが実際に試したパス（`attemptedPaths=[...]`）と例外の全文が付く |
| `cleanup.run` | INFO | `RecordingCleanupScheduler` | 古い mp4 の自動削除の結果（保存先・削除数・解放バイト数・削除したフォルダー数・失敗数）。**何もしなかった周回は出さない** |
| `cleanup.error` | WARN | 同上 | 削除できなかった理由（1件1行・上限あり）。ロック中のファイルなど |
| `gst.encoders` | INFO | `Controller.StaticInitialize` | プローブ結果（存在/欠落と候補順）。1回のみ |
| `gst.encoder selected` | INFO | `EventRecorder.Initialize` | 実際に採用されたエンコーダーとメモリ要件・失敗した試行数 |
| `gst.encoder candidate-failed` | WARN | 同上 | 候補が落ちた理由（要素が無い／リンク不可／未知のプロパティ） |
| `gst.encoder fallback-from` | WARN | 同上 | フォールバックが起きた場合の全失敗の一覧 |
| `gst.typefallback` | INFO | `Controller.StaticInitialize`（診断の購読） | バインディングがネイティブのインスタンスを基底型のラッパーで包んだ（`instance=` / `wrapped-as=`）。型が未登録である兆候で、`msg.Src is BaseSrc` のような型判定が黙って外れる原因になる |
| `gst.callback` | ERROR | 同上 | ネイティブのコールバック境界で捕捉された未処理例外（`GstSharp.UnhandledCallbackException`）。`appsink` の `new-sample` とバスの同期ハンドラは自前で例外を握るので、ここに出るのは**その握りをすり抜けたもの**だけ ── コールバックの中の障害はここにしか現れない |
| `recorder.init ok` / `recorder.init fail` | INFO / ERROR | `GstControllerViewModel.AddRecorderFor` | レコーダーの初期化結果 |
| `recording.start` / `recording.start fail` | INFO / ERROR | `EventRecorder.Start` | レコーダー名と**解決済み**ファイル名 |
| `recording.stop` | INFO | `EventRecorder.StopDrainAndFinalize` | レコーダー名・ファイル名・経過ミリ秒・`result=ok｜timeout｜error` |
| `recording.stop timeout` / `recording.stop error` | ERROR | 同上 | 排出が上限内に終わらなかった／排出中にエラーが出た場合の詳細 |
| `recording.stop empty` | ERROR | 同上 | 1フレームも mux されず MP4 にメディアデータが無い（`samplesSeen` / `samplesPushed` / `srcState` で原因を切り分ける。終了コード 16 の根拠） |
| `recording.stop slow` | WARN | `EventRecorder.Close` の待ち | 進行中の排出が上限＋余裕の中で終わらず、src パイプラインを破棄せずに手放した |
| `recorder.leak` | WARN | `EventRecorder.Close` | ネイティブを安全に破棄できず、解放を諦めた（クラッシュ回避のための意図的なリーク）。原因は 3 つ ── **排出中の src パイプライン**（`abandonedStop`）、**上限内に `NULL` へ降りなかった sink パイプライン**（quiesce の失敗。この場合はコールバックの解除もリングバッファの解放も行わない）、**予算内に片付かなかった常時録画**（排出中のセグメントを置いたまま先へ進む）。いずれの場合も `SetState(Null)` だけは必ず実行する |
| `recording.aborted` | ERROR | `EventRecorder.HandleBusMessage` | 録画中に src 側バスがエラーを報告したため録画を中止した |
| `recorder.error` | ERROR | `EventRecorder.HandleBusMessage` | **両方のバス**の `Error`（バス名・要素名・メッセージ・debug 情報） |
| `recorder.warning` | WARN | 同上 | 両方のバスの `Warning`。連続する同一内容は畳んで `repeated=N` を添える |
| `recorder.eos` | INFO | 同上 | sink 側バスの `Eos`。**これも自動復帰の引き金**（種別の付く映像源のときだけ予約する。「自動復帰」の節）── WGC の画面キャプチャは切断してもエラーを出さず、この行だけを出す |
| `recorder.restart` | INFO / WARN | 同上／`RestartSinkSrc` | 自動復帰の予約と、その結果（`ok` / `failed`）。監視できる映像源なら `watch=camera｜monitor` が付き、デバイスの到着で待ちを打ち切った回は `wake=device-arrival` が付く。**作り直しだけを試す連鎖は `attempt=` ではなく `round=`**（1 周ごとに新しい連鎖になるので `attempt` は常に 1 になる）で、待ちの案内は 1 周目と約 1 時間ごとにしか出さない（1 分に 1 行では `activity.log` を数日で使い切る）。録画を畳んで録り直すときは `will be resumed once the pipeline is rebuilt` / `resuming the recording that the rebuild finalized` / `not resuming the recording after the rebuild (…)` の 3 種が出る ── 直後の `recording.start` が利用者の操作か復帰かは、この行があるかどうかで見分ける |
| `device.watch` | INFO / WARN | `DeviceArrivalWatcher` | デバイス到着の監視を張った／止めた（`kind=` と `provider=`）。WARN は**監視できない**構成（プロバイダが無い・`CanMonitor()` が false・起動に失敗）で、タイマーだけの復帰へ縮退したことを意味する |
| `device.arrive` | INFO | 同上 | デバイスプロバイダが到着（`device-added` / `device-changed`）を報告した。連続する同一内容は畳んで `repeated=N` を添える |
| `recorder.continuous-init ok` / `recorder.continuous-init fail` | INFO / WARN | `EventRecorder.StartContinuous` ／ `InitializeCore` ／ `InitializeWith` | 常時録画の枝を組めた（エンコーダー・fps・解像度・分割間隔）／組めなかったので**枝だけ落とした**（イベント録画は無事＝隔離契約）。**上書きだけを捨てた場合も同じ名前で出す**（読めないフレームレート・上流が固定されていないのに指定された解像度）── どちらも「設定が黙って効いていない」という同じ事故だからである |
| `continuous.start` | INFO | `ContinuousRecorder.OpenSegment` | 常時録画のセグメントを開いた（ファイル名・通し番号・分割間隔） |
| `continuous.finalize` | INFO | `ContinuousRecorder.FinalizeSegment` | セグメントを確定させた（`result=ok｜timeout｜error`） |
| `continuous.finalize backlog` | WARN | `ContinuousRecorder.WaitForFinalizers` | 排出中のセグメントが上限に達したまま予算内に片付かなかった |
| `continuous.overshoot` | WARN | `ContinuousRecorder.OnContSample` | 分割点でキーフレームが来ず、セグメントが設定値を大きく超えた（原因はほぼ GOP 長。1セグメントにつき1行） |
| `continuous.error` | ERROR / WARN | `ContinuousRecorder` ／ `EventRecorder.CloseCore` | 常時録画側の障害（最初のフレームが来ない・排出の失敗・PTS の巻き戻し・押し込みの拒否）。**`recorder.error` とは別にする** ── 常時録画の障害でイベント録画の状態表示を汚さないため |
| `continuous.stop` | INFO | `ContinuousRecorder.Close` | 常時録画を止めた（書いたセグメント数） |
| `continuous.leak` | WARN | 同上 | 進行中のサンプル処理が上限内に抜けず、最後のセグメントの確定と書き出しパイプラインの解放を諦めた（`recorder.leak` と同じ規律。quiesce が成功していれば起こらない） |
| `log.terminal` | INFO / WARN | `LogTerminalView` | Log 画面のターミナルが起きた（`ready renderer=webgl｜dom`）／WebView2 を諦めてリスト表示に落ちた（`fallback=list`）。**どちらのレンダラーで描いているかは画面から見分けが付かない**ので、ここが唯一の観測点になる |
| `log.file error` | ERROR | `Program.Main`（`ApplyLogFile`） | `DebugLogFile` を開けなかった（切断されたドライブ・権限など）。保存は諦めて捕捉は継続する ── 投げると未処理例外ハンドラの購読前なので、不正なパス1つで常駐ワーカーが起動できなくなる |
| `gst.debug` | INFO | `DebugLogEx.TrySetThreshold` | `GstDebug` の変更を実行中に適用した（`threshold='...'`）。適用先は GStreamer の内部状態だけで画面にもファイルにも痕跡が残らないので、ここが唯一の観測点になる |
| `gst.dot` | INFO / ERROR | `MainPageViewModel.SaveDebugGraphs` ／ `Controller.WriteDebugGraphs` | Log 画面の「グラフを保存」の結果（`dir='...' files=N`）／個々のパイプラインの書き出し失敗 |
| `preview.error` | ERROR | `Previewer` のバスのハンドラ（`SubscribeBus`）／`NativeSwapChainPanel.SetPanelSwapChain` | プレビューパイプラインの実行時障害（D3D デバイスロスト等。1 パイプラインにつき 1 行）と、スワップチェーンのパネルへのバインド失敗。録画は止めない方針のため復帰は試みない ── ここが「プレビューだけ黙って固まった／黒いまま」の唯一の観測点になる |
| `variables.duplicate-key` | WARN | `TemplateVariableViewModel.OnKeyChanged` | Variables 画面で既存の行と重複するキーを入力したため、元のキーへ差し戻した（重複を許すと既存の値を空文字で潰し、片方の削除で実体まで消える） |
| `settings.load` | ERROR | `AppSettings.ReportLoadFailure` | settings.json を読めず既定値へ倒れた（読めなかったファイルは `.bad` へ退避） |
| `settings.seed` | INFO | `AppSettings.ReportSeedUsed` | 保存先に settings.json が無く、**実行ファイルの隣の settings.json を既定設定（種）として読んだ**。無記録だと「設定した覚えのない初期値で始まった」ことを追えない |
| `camera.open` | INFO | `CameraControlWorker.OpenAsync` | カメラ設定を開いたときの解決結果（`resolution=` / `device=` / `opened=` / `controls=`）。**開くたびに必ず 1 行出る** ── `camera.devices` は `device-path` が既に書かれていれば走らないので、そちらだけでは通常の構成で何も分からない。「カメラ設定が効かない」ときに最初に見る行 |
| `camera.devices` | INFO | `GstIntrospect.GetVideoSourceDevices` | カメラのデバイス列挙の結果（`count=` と、`device-path` を読めた数 `withPath=`）。**`DebugLogEx` では見えない**（`gst_debug_log` 経由なので `GST_DEBUG` 未設定では 1 行も出ない）ため activity.log へ出す ── カメラが 1 台も見えないのか、見えているがパスが読めないのかを切り分ける唯一の手掛かり |
| `monitor.devices` | INFO | `GstIntrospect.GetMonitors` | モニターの列挙の結果（`count=` と、`device.path` を読めた数 `withPath=`）。**0 台でも 1 行出す** ── `monitor-device-path` を解決できなかったとき、列挙そのものが空だったのか一致しなかっただけなのかは、この行でしか区別できない |
| `camera.control` | INFO / WARN / ERROR | `EventRecorder.ApplyCameraControls` | カメラ設定（`CameraControls`）を当てた／当てられなかった（`device-path` が無い・デバイスを開けない・ドライバが弾いた）。**録画は止めない**ので、ここが唯一の観測点になる |
| `settings.save` | ERROR | `AppSettings.ReportSaveFailure` | settings.json を書けなかった（ディスク満杯・一時ロック・権限）。書けなかった変更は次の保存契機で改めて書かれる |
| `cli` | INFO | `ActivationCommands.Parse` | コマンドラインと終了コード。**例外で抜けた場合も必ず出る**（そのときの終了コードは 99） |
| `ping` | INFO | `ping` コマンド | 生存確認 |

**書き手は常駐ワーカープロセスだけ**。`File.AppendAllText` は `FileShare.Read` で開くため、
ランチャーと常駐ワーカーが同時に追記すると片方が `IOException` になる。`ActivityLog` は
例外を絶対に投げない設計なので、その失敗は**黙って行が消える**形で現れる ── そのため
`cli` の記録もランチャーではなく常駐ワーカー側で行い、書き手を1プロセスに限っている。

`Initialize(directory, mirrorToConsole)` の `mirrorToConsole` は `Console.Error` への複写で、
アプリ内 Log 画面（`StandardStreamRedirector`）と `AppSettings.DebugLogFile` にも同じ行を届ける。
**常駐ワーカーでのみ true**。ランチャーの標準エラーはユーザーのコンソールそのもので、
CLI 出力を汚染するため有効にしてはいけない。

1MB を超えると `activity.log.1` へ退避する（世代は1つだけ保持）。

## バスメッセージの処理とレコーダーの健全性

バスは **`Bus.SubscribeSyncDrop` で購読する**（`EventRecorder.SubscribeBus`）。**ポーリングは
行わず、バスを汲むためのスレッドも持たない。**

**`Bus.Message` / `AddWatch` は使えない。** どちらも `GMainLoop` からメッセージを配送する
仕組みで、このアプリはメインループを回していないので 1 件も発火しない。メインループ無しで
push 型に受けられるのは、バスの同期ハンドラだけである。

### どのバスを、いつ購読するか

| バス | 購読 | 解除 |
|---|---|---|
| `_sinkBus`（常時稼働） | `InitializeWith`（`PLAYING` 到達後・1 回だけ） | `CloseCore` |
| `_srcBus`（録画中のみ稼働） | 同上（gate は **sink の** `PLAYING` 到達） | 同上 |
| 常時録画のセグメント | `ContinuousRecorder.OpenSegment`（`NULL` 状態のうちに） | `FinalizeSegment` |
| プレビュー | `Previewer.Initialize`（`PLAYING` へ上げる前） | `Close` |

解除はどれも購読（`SubscribeSyncDrop` が返す `IDisposable`）の `Dispose` である。

- **録画のたびに掛け直さない。** `GstPipeline` は READY→NULL でバスを flushing 化する
  （`auto-flush-bus` 既定 true）ので、停止中の src バスには post が届かない。
- **sink / src の購読は `PLAYING` 到達より後**に置く ── エンコーダー候補のフォールバックで
  落ちた候補のメッセージが `recorder.error` として残らない挙動を、この位置で保っている。
- **1 本のバスが持てる同期ハンドラは 1 つ**で、生きた購読があるバスへの 2 本目の購読は
  `InvalidOperationException` になる（`Dispose` 後の再購読は通る）。このアプリは
  1 バス 1 購読なので、この例外に当たる経路は無い。
- 購読は `Bus` のラッパーを強参照で持ち、`Dispose` は冪等。**フィールドを null 化する前に
  `Dispose` する**規律は残すが、解除の成否はフィールドに依存しない。
- **解除は `_busLock` を保持せずに行う。** 解除そのものはバスのロックの下で行われるので
  実行中のハンドラと競合しないが、解除が返った時点で走り出していたハンドラはまだ走っている
  （`CloseCore` が解除の直後にもう一度 `CancelPendingRestart` を掛けるのはそのため）。

### キューは伸びない（配送 → Drop → 解放）

購読より後に post されたメッセージは**キューに入らない**。バインディングがハンドラへ配送し、
`GST_BUS_DROP` を返して poster の参照まで落とす ── アプリ側に残骸の回収は要らない。

- **汲み切りが要るのは購読前のバックログだけ。** sink / src は `PLAYING` 到達後に購読するので、
  それ以前に post された分がキューに残っている。`SubscribeBus` は `_busLock` の下で
  「購読 → `Pop` で汲み切る」を続けて行う（`Pop` の戻りは所有権つきなので必ず解放する）。
- 常時録画のセグメントとプレビューは `NULL` 状態で購読するのでバックログが無く、汲み切りもしない。

### ハンドラの中でしてはいけないこと

ハンドラは **post 元＝当の要素のストリーミングスレッド**で走る。したがって

- **`SetState` をインラインで呼ばない**（自スレッドの復帰を待って固まる）。障害要素を
  `Ready` へ落とす復帰の予約は `Task.Run` へ逃がす。
- **`_stateLock` を取らない・`Join` / `Wait` をしない。** 取ってよいのは `_busLock` だけで、
  ロック順序は `_stateLock` → `_busLock` → `_restartLock` の一方向のみ。
- **例外を漏らさない**（ここはネイティブのトランポリンの中）。漏れた例外はバインディングが
  捕捉し、メッセージは所有権の後始末込みで `Drop` される（キューは伸びない）が、
  **その 1 件は分類も記録も排出待ちの完了もされずに終わる**。ハンドラが自前で catch
  するのはそのためで、1 件の失敗で以後のメッセージまで落とさない。
  自前の catch をすり抜けたものは `gst.callback` に残る。

### 待ち手はバスを汲まない

停止の排出（`StopDrainAndFinalize`）と常時録画のセグメント確定（`FinalizeSegment`）は、
EOS を送ったあと `TaskCompletionSource` を**待つだけ**で、自分ではバスに触らない。
完了させるのは当のバスの同期ハンドラである（`_stopDrain` / `SegmentWriter.Drain`。
**武装は `EndOfStream()` より前** ── 後にすると EOS より先に届く Error を取りこぼす）。

**読み手をハンドラ 1 つに保つこと。** 同じバスに読み手が 2 人いると、待ち手が欲しい
`Eos` / `Error` をもう一方が先に取ってしまい、待ち手は上限まで待たされる。実際に
「録画中に src エラー → 中止」の経路でこれを踏み、**停止スレッドが 1 本ハングしたまま
`recording.stop` が出ず MP4 も確定しなかった**。最も検出したい状況で必ず踏む競合なので、
停止側・確定側にバスを汲ませる形へ戻さないこと。

- 武装中の src バスの `Error` は、ハンドラでは報告しない ── 報告は停止側の
  `recording.stop error` 1 箇所だけにする（両方から出すと同じ障害が `recorder.error` /
  `recording.aborted` と二重に出て、E2E の件数の表明が壊れる）。
- 武装していない src バスの `Eos` は黙って捨てる（`recorder.eos` は sink バス専用の印で、
  停止のたびに出るものではない）。

### 洪水対策

`Warning` は洪水になる。GPU 実機の `nvh264enc` では `h264parse` が**捨てた NAL 1個ごとに**
`broken/invalid nal ... will be dropped` を出していた。素通しにすると `activity.log` の
1MB ローテーションを数秒で食い潰し、**原因を突き止めるためのログが原因自身に流し去られる**。
`BusMessageThrottle`（純粋ロジック。L1 の `BusMessageThrottleTests` が検証）が
連続する同一内容を畳み、`repeated=N` を添えて報告する。
`MaxSuppressedInARow` 件ごとに1行出して沈黙し続けないようにしてある。

洪水でも**取りこぼしは起きない** ── ハンドラは post 1 件につき 1 回呼ばれる。畳むのは
`activity.log` の行だけで、件数は `repeated=N` に残る。
`Observe` は GStreamer のストリーミングスレッドから、`Flush` は停止・破棄の経路から
呼ばれるため、`BusMessageThrottle` は自前のロックで内部状態を直列化する
（常時録画の `pts-rewind` / `push-rejected` も同じ仕組みをサンプルのコールバックから使う）。

### 停止の有界化

排出待ちは `AppSettings.StopFinalizeTimeoutMs`（既定 5000ms）で有界。
無限待ちにすると、mux が詰まったとき呼び出しスレッドごと永久にハングする。
待つのはバスの同期ハンドラが完了させる `TaskCompletionSource` で、**同期の `Wait` で待つ**
── `await` にすると継続がストリーミングスレッドでインライン実行され、`finally` の
`SetState(Null)` が自スレッドで走って固まる。
タイムアウトしても `SetState(Null)` は `finally` で必ず実行する ──
実測では、その結果として排出が完了しなかったケースでも `moov` まで書かれた MP4 が残った。
**ランチャーの結果待ち（60秒）に近づけないこと**（超えると終了コード 2 が出始める。目安 50000 以下）。

> **ランチャーの待ちの値の根拠**: 結果待ち **60 秒**・常駐ワーカーの準備完了待ち
> **120 秒**（`SingleInstanceManager.Launcher.cs`）。結果待ちを 10 秒程度に縮めると、
> 録画コマンドがワーカー側で行う**最大 8 秒の準備完了待ち**の上に 2 秒しか余裕が無く、
> CI ランナーでは `activate` に 12.6 秒かかる実測があるため足りない。準備完了待ちは
> **常駐ワーカーの初回起動が GStreamer のプラグインレジストリ構築で 10 秒を超える**
> ことを吸収する。どちらも「異常を検出するための上限」であって正常系の待ち時間ではない
> ── 通常のコマンドはミリ秒で返る。

### 状態遷移の直列化

`Initialize` / `Start` / `Stop` / `Close` は `_stateLock` で直列化する
（UI スレッド・プールスレッド・CLI 経路の複数から呼ばれるため）。
**`appsink` のコールバックとバスのハンドラはこのロックを取らない** ── `Close()` はロックを
保持したまま sink パイプラインを `NULL` へ落とし、**実行中のコールバックの復帰を待つ**
（`CloseCore` の quiesce）。コールバック側が同じロックを待った瞬間にデッドロックする。
コールバックは `volatile` フィールド（`_isAlive` / `_IsRecording`）の読みだけで回し、
ハンドラが取ってよいのは `_busLock` だけ。

`LastError` は GStreamer のストリーミングスレッドから変更されるため、購読側は UI スレッドへ
マーシャリングする
（`GstEventRecorderViewModel.Model_PropertyChanged` が `HasThreadAccess` の高速パス付きで行う）。

### 自動復帰

エラー1件ごとに無条件で復帰タスクを積んではいけない ── 1つの障害で GStreamer は
複数のエラーを出すため（実測では 60 秒で 41 件）、素朴に積むと同数の復帰試行が並走する。

復帰は `RestartPolicy`（純粋ロジック。L1 の `RestartPolicyTests` が検証）に従う:

- **1本のタスク（`RestartLoopAsync`）が連鎖を最後まで所有する。**
  「1回試して失敗したら予約し直す」再帰にしてはいけない ── 呼び直しの時点で
  `_restartCts` はまだ自分自身なので「already scheduled」で拒否され、
  **次のエラーが飛んで来たときにしか次の試行が走らなくなる**。
  エラーを数件出して以後は沈黙するソース（ケーブルを抜いたモニタなど）では、
  1回目の失敗で永久に止まる。
- **保留中の復帰があれば積まない。** 拒否の記録も予約1回につき1行だけで、
  件数は実行時に `suppressedErrors=N` として報告する（拒否ログ自体が洪水になるため）。
- 間隔は **5s → 10s → 30s → 60s で頭打ち**。最初を短くするのは、一瞬の切断
  （ケーブルの接触・モード切替）で30秒待たせないため。
- **試行回数は無制限。** 監視対象のモニタが1時間抜けていても、戻ってきたら復帰すべき。
- **3回続けて失敗したら `Initialize()` でパイプラインごと作り直す。**
  デバイスが別のキャップスで戻ってきた場合、要素単位の再 Playing では復帰できない。
- 障害要素が**ソース以外でも必ず予約する**。ソースに限定すると、エンコーダーが壊れた
  場合に何も起きず毎フレームのエラーを出し続ける。
  要素単位で戻せない障害は、エスカレーションでパイプラインごと作り直すのが唯一の手段。
- **sink バスの `Eos` でも予約する。** 復帰の引き金は Error だけではない ──
  **切断してもエラーを出さず、EOS だけを出して黙るソースがある**。実例は画面キャプチャの
  WGC 経路（`capture-api=wgc`）で、ディスプレイを切断しても `recorder.error` は 1 行も出ず
  `recorder.eos` だけが出る（DXGI は `Internal data stream error` を出すので Error 分岐が拾う）。
  EOS で予約しないと `_sinkSawEos` の印が立つだけで誰も読まないまま終わり、
  連鎖が張られない＝**デバイス到着の監視も張られない**ので、復帰の仕組みが丸ごと効かない。
  印は予約より**先**に立てる（連鎖が `mustRebuild` の判断に読む）。
  **印を畳むのはパイプラインを組む前**（`InitializeWith` の先頭）── バスの購読より後ろで
  畳むと、作り直した直後に出た EOS の印を消してしまい、その EOS が予約した連鎖が
  要素単位の再開から始まって**戻っていないのに `result=ok`** と報告される。
  **ただし EOS の予約は種別の付く映像源に限る**（`DeviceKindRules.Classify`）── カメラ・
  画面キャプチャの EOS は切断以外にありえないが、有限のテストパターン
  （`videotestsrc num-buffers=N`）やファイルの EOS は**正常終了**であり、
  作り直すと同じ有限ストリームを永久に回し続けることになる。
  **Error 分岐は絞らない** ── あちらは何かが壊れた印なので、ソースの種類によらず復帰を試す。
  Error と EOS が同じミリ秒で届く構成（DXGI）では、後から来た方が
  `ScheduleRestart` の「already scheduled」で拒否されるので二重にはならない。
- **デバイスの到着で待ちを打ち切る。** 監視は復帰待ちのあいだだけ
  （`DeviceArrivalWatcher.Acquire` の参照カウント）で、**プロバイダを永続的に started に
  してはいけない** ── `gst_device_provider_get_devices` は started の間だけキャッシュを返し、
  抜き差しの後は probe の順とずれるので、`GstIntrospect.GetMonitorResolutions` の
  `monitor-index` と解像度の対応が壊れる。取得はプールスレッド（連鎖の先頭）で行う
  ── `ScheduleRestart` は `_busLock` を保持したストリーミングスレッドで走るので、
  そこでネイティブのデバイス列挙を走らせてはいけない。
  **試行回数の数え方は変えない** ── 早く起きた回も通常の 1 回として数える。
  起きた理由は `wake=device-arrival` として記録する（`reason=` は
  「なぜ作り直すか」に使っているので別のキーにする）。
- **到着は束ねてから起こす**（`DeviceArrivalWatcher.ArrivalQuietMs` = 500ms、
  上限 `ArrivalMaxDeferMs` = 3 秒）。モニターの抜き差し・解像度変更・RDP の再接続は
  いずれも `WM_DISPLAYCHANGE` を数回続けて起こし、プロバイダはそのたびに再 probe して
  差分を post する。**1 件ごとに起こすと、まだ再構成の途中の機械へ試行を掛ける**ことになる。
  上限が要るのは飢えを防ぐため ── 静穏時間より短い間隔で到着が続く限り、
  静穏だけに頼ると永久に起こさない。
- **早期に打ち切れる回数には上限を置く**（`RestartPolicy.MaxEarlyWakesPerChain` = 2）。
  **`EscalateAfterAttempts`（3）より小さいこと**を L1 が縛る ── 同じにすると、
  到着の連打だけでエスカレーションの予算を使い切れてしまい、本来 5s + 10s + 30s の
  45 秒に散っていた 3 回が数秒で尽きて、**まだ落ち着いていない機械へパイプライン
  全再生成を掛ける**ことになる。上限に達したら 1 行だけ記録して、以後はバックオフを待ち切る。
  作り直しだけの連鎖は 1 周につき待ちが 1 回しか無いので、この上限には触れない。
- **落ち着き待ちを置く**（`RestartPolicy.SettleAfterArrivalMs`）。列挙に出た＝開けるとは
  限らない ── デバイスインターフェイスの到着通知はドライバが使える状態になる前に飛びうるし、
  ディスプレイの再構成は `WM_DISPLAYCHANGE` の時点ではまだ途中でありうる。
  置く時間は**元の待ちを超えない**（超えると「早期復帰」が待ち切るより遅くなる）。
- **作り直しで畳んだ録画は録り直す。** `Initialize()` は先頭の `Close()` で進行中の録画を
  確定させる（ファイルは壊れない）ので、控えておかないと**復帰しても録画だけが戻らない**
  ── 常時録画は `InitializeWith` の末尾で作り直されるのに、イベント録画だけ再開しない、
  という非対称になる。意図は `_resumeAfterRecovery` としてレコーダーが持ち、
  **作り直しの何周を跨いでも生き残る**（デバイスが 2 分抜けていれば連鎖は何周も回る）。
  消えるのは「止められたとき」「録り直したとき」「破棄されたとき」の 3 つだけ。
  録り直しは**新しいファイル**になる（事前バッファは空なので先頭の巻き戻しは無い）。
- **抜けているあいだの停止を届かせること。** 作り直しのあいだは `IsRecording` も
  `IsInitialized` も false になるので、`RecordingCommandState.CanStop` が
  `resumePending` を見ていないと、**利用者が止めても UiaTrigger の停止条件が立っても
  どこにも届かず、復帰した瞬間に録画が再開する**。`StopAsync` は
  `!_IsRecording` の早期 return より**前**で取り消すこと（順序は L1 が縛る）。
  録り直し側も**意図の検査から `Start()` までを `_stateLock` の下で切れ目なく**行う ──
  分けると、`Initialize()` の握るロックを待って止まっている停止を追い越して開始でき、
  **利用者が止めた録画が戻ってくる**。
  画面では復帰待ちも録画中として見せる（`ShowsAsRecording`）── 見せないとトグルが
  切れた状態で表示され、切る手段が無くなる。
- **`Initialize()` が失敗しても連鎖を絶やさない。** 失敗した時点では
  パイプラインもバスも無い＝**二度とエラーが飛ばない**ので、そこで諦めると
  デバイスを挿し直しても永久に復帰しない。`Initialize()` の `catch` が
  `TryScheduleDeviceRebuild` で「作り直しだけを試す連鎖」（`rebuildOnly`）を張る
  ── 間隔は `RestartPolicy.MaxDelayMs`（60 秒）＋デバイス到着で早期。
  対象は**種別の付く映像源に限る**（テストソースや打ち間違いのパイプラインを
  永久に再試行しても、戻ってくるものが無い）。この経路は
  「起動時にカメラが無い」「デバイス不在のままエスカレーションした」の両方を同時に直す。
  `ScheduleRestart` は**キャンセル済みの `_restartCts` を空き枠として扱う**
  ── `CancelPendingRestart` は 2 秒で諦めるので、古い連鎖が `Initialize()` の中で
  `_stateLock` を待っているとキャンセル済みの予約が残る。そこで拒否すると
  **連鎖が 1 本も無い状態**になり、直したはずの詰みが戻る。
- `Close()` は保留中の復帰を**先にキャンセル**して有界待ちする。放置すると最大60秒後に
  破棄済みのパイプラインへ `SetState` したり `Initialize()` を呼んだりする。
  復帰タスクは `Initialize()` の中で `_stateLock` を取るため、
  キャンセルせずに待つとデッドロックする（`CancelPendingRestart` のコメント参照）。
  待ちは 2 秒で諦める。ループ側の `IsCancellationRequested` 確認は `Delay` 中の
  中断用で、**Dispose 済みレコーダーの蘇生はこれでは防げない**（確認とロック取得の
  間に窓がある）── 蘇生は `InitializeCore` 先頭の Dispose 済み検査が拒否する。
  `_isAlive` では代用できない ── 通常の `Initialize()` 待ちでも false になるため。
  エスカレーションで `Initialize()` を呼ぶ直前には、ループが自分の連鎖の所有権を
  畳む ── 畳まないと `Initialize()` 内の `CancelPendingRestart` が
  **実行中の自分自身を 2 秒待つ**。

### 停止の非同期化

停止は「**受付**」と「**排出**」に分かれる。

- **受付は呼び出しスレッドで同期的**に行い、`IsRecording` をその場で false にする。
  プールへ逃がすと `CanStopRecording` が反転するまでに窓が開き、二重停止を弾けなくなる。
- **排出（EOS → バス待ち → `SetState(Null)`）はプールスレッド**へ移す。
  呼び出しスレッド（UI スレッド・CLI 経路）で行うと最大 `StopFinalizeTimeoutMs` ブロックする。

API は `StopAsync()`（完了を表す `Task` を返す）/ `Stop()`（fire-and-forget）/
`StopAndWait(timeoutMs)` の3つ。UI のボタンは `Stop()`、**CLI は `StopAsync()` を await する**
── `stop-recording X` の直後に `copy` するバッチが想定用途であり、
コマンド復帰時に `moov` が確定している必要がある。
`stop-recording-all` は `Task.WhenAll` で**並行に**待つ（直列にするとレコーダー数 ×
`StopFinalizeTimeoutMs` でランチャーの結果待ち **60 秒**を超える）。

排出中は `IsStopping` が true になる。この間は
**`IsRecording` も `IsStopping` も見て開始を止める**（`RecordingCommandState.CanStart`）──
通してしまうと `Start()` が排出の完了を待ち、剥がしたはずのブロックが戻る。

> **ロック順序（重要）**: `Close()` は `_stateLock` を保持したまま排出の完了を待つ。
> したがって**排出タスクは `_stateLock` を取ってはいけない**。
> 自動復帰と違い**キャンセルして諦めることはできない** ── 排出中のパイプラインを
> `Dispose` するとネイティブの二重解放になるため、必ず待ち切る。
> 排出が触ってよいのは、ネイティブのパイプライン／バスと `ActivityLog` だけ。
> `_stopTask` の読み書きも必ず `_stateLock` の下で行う。
>
> 終了経路（`CloseCore`）の排出だけは**同期のまま**。この直後に `Dispose` するため。

> PropertyGrid の `IsRecording` 行（編集可能）も同じ可否を通る ── セッター
> （`OnIsRecordingChanged`）が `CanStart/StopRecording` を検査し、不可なら値をモデルへ
> 差し戻す。加えて `EventRecorder.Start` 自身も、排出待ちを待ち切れなかった場合は
> 開始を拒否する（`recording.start fail`）── 排出中のパイプラインへの `SetState` を
> タイムアウト境界で解禁しないため。

## テスト用の環境変数

いずれも**自動テストのためだけ**に存在するフックで、未設定なら通常起動の挙動は変わらない。
解決規則は `Components/AppEnvironment.cs` にまとまっており、L1 テスト
（`AppEnvironmentTests`）が規則そのものを検証している。

| 環境変数 | 効果 | 無いと何が困るか |
|---|---|---|
| `PROCESSRECORDERAPP_DATA_DIR` | `settings.json` / `activity.log` の保存先ディレクトリを差し替える（相対パスは絶対化） | E2E テストが実ユーザーの `%LOCALAPPDATA%\ProcessRecorderApp` を上書きし、テスト同士も干渉して再現性が無くなる |
| `PROCESSRECORDERAPP_KEY_PREFIX` | 単一インスタンス制御に使う名前付き Mutex / EventWaitHandle / MemoryMappedFile / `AppInstance` キーの接頭辞を差し替える | テスト実行中に開発者の常駐インスタンスが居ると、テストのコマンドがそちらへ転送される（逆にテストの常駐ワーカーが開発者の操作を奪う） |
| `PROCESSRECORDERAPP_LANG` | 表示言語を BCP-47 タグで強制する（**`Microsoft.Windows.Globalization`**`.ApplicationLanguages.PrimaryLanguageOverride`）。`Program.Main` の先頭、リソース解決より前に適用する。不正なタグは警告を出して無視する | OS の表示言語を切り替えないと ja-JP / en-US / フォールバック（例: de-DE）の各経路を検証できない。GitHub ランナーは en-US 固定なので ja-JP が永久に未検証になる |
| `PROCESSRECORDERAPP_MIRROR_STDERR` | `1`/`true` で、捕捉した標準出力・標準エラーを差し替え前の標準エラーへも複写する（`StandardStreamRedirector`） | 標準ストリームを捕捉へ差し替えた後は、外からプロセスを起動した側が出力を1行も受け取れず、E2E ハーネスが診断を失う |
| `PROCESSRECORDERAPP_TEST_DEVICE_ARRIVAL` | `1`/`true` で、名前付きイベント `{キー接頭辞}-DeviceArrival` のシグナルを**デバイスの到着として扱う**（`DeviceArrivalWatcher`）。実際のデバイスプロバイダには一切触れない | 開発機にも CI にも**カメラが無く、モニタの抜き差しもできない**ので、「到着で復帰の待ちを打ち切る」経路がどのテスト層でも1行も実行されない |

> **`Windows.Globalization` ではなく `Microsoft.Windows.Globalization` を使うこと。**
> 前者（OS 側の WinRT API）は**パッケージ ID を要求する**ため、アンパッケージ配布の
> 本アプリでは必ず `0x80073D54`（「プロセスにパッケージ ID がありません」）で失敗し、
> **この機能全体が無言で効かない**（前者を呼ぶと、例外も警告も出ないまま
> 言語強制フックが一切動かないことを実測済み）。
> 後者は WinAppSDK がアンパッケージ前提で用意した同名 API で、
> MRT Core の解決（`x:Uid` を含む）に効く。

ニュートラル言語は `src/Directory.Build.props` の `DefaultLanguage` / `NeutralLanguage`
（いずれも `en-US`）で宣言している。`DefaultLanguage` は WinUI/MSIX ツーリングが
既に `en-US` を既定値として与えているため明示しても挙動は変わらないが、
暗黙の既定に依存しないよう書いてある。

いずれもプロセス起動時に1度だけ解決する（起動後に環境変数を変えても反映されない）。

## UI 自動化のための `AutomationId`

UIA テストは要素を `AutomationProperties.Name` で探すと壊れる ── 本アプリの `Name` は
すべて `.resw` 由来で**ロケール依存**だからである。そのためロケール非依存の
`AutomationId` を主要要素に付けている。

- `x:Name` を持つ要素は WinUI が既定で `x:Name` を `AutomationId` として公開するため、
  明示指定は不要（`navView` / `previewNavItem` / `logNavItem` / `variablesNavItem` /
  `logListView` / `logTerminal` / `recorderPropertyGrid` / `settingsPanel` / `swapChainPanel` など）。
- `x:Name` を持たない要素・`DataTemplate` 内の要素にのみ明示的に付与する。

| 要素 | `AutomationId` |
|---|---|
| 一括開始／終了ボタン | `StartAllButton` / `StopAllButton` |
| プロパティペイン開閉／レコーダー削除 | `TogglePaneButton` / `RemoveRecorderButton` |
| Log 画面の自動スクロール／全文コピー／グラフ保存／クリア | `AutoScrollToggle` / `CopyAllLogButton` / `SaveDotFilesButton` / `ClearLogButton` |
| Log 画面のターミナル／フォールバックの注記 | `logTerminalView` / `LogTerminalUnavailableText` |
| Variables 画面の追加／削除／表 | `AddVariableButton` / `RemoveVariableButton` / `VariablesTable` |
| Settings 画面の再読み込み | `ReloadSettingsButton` |
| Variables 表の編集用テキストボックス | `VariableKeyEditor` / `VariableValueEditor` |
| ナビの「Add recorder」項目 | `AddRecorderNavItem` |
| **PropertyGrid の各行の値エディタ** | **元の CLR プロパティ名**（`FilenameTemplate` / `BufferDuration` など。`PropertyGridItem.PropertyName`） |
| PropertyGrid の「…」ボタン | `<プロパティ名>.Build`（例: `SrcPipeline.Build`） |
| PropertyGrid の入力エラー表示 | `<プロパティ名>.Error`（`Visibility=Collapsed` のときは UIA ツリーに出ないので、**有無がそのまま表示の有無**） |
| パイプライン編集ダイアログ | `PipelineSourceCombo` / `PipelineSpecifyCaps` / `PipelineGeneratedText` |
| 同ダイアログの各行 | 値エディタ＝GStreamer のプロパティ名／caps フィールド名、有効チェック＝`<名前>.Enabled` |

PropertyGrid の行が最重要である。行は `PropertyGridItem` の `DataTemplate` 生成であり、
これが無いと「`FilenameTemplate` の TextBox に入力する」を言語非依存で書けない。

レコーダーのナビ項目には `AutomationId` を付けていない ── 表示名はユーザーデータ
（リネーム可能）であり、生成時に焼き込むと改名後に古い ID が残って却って壊れるため、
表示名で引く。`ContentDialog` の OK/キャンセルボタンは WinUI 既定テンプレートの
`PrimaryButton` / `SecondaryButton` / `CloseButton` がそのまま `AutomationId` になる。

### ウィンドウの外（タスクトレイのメニュー）

トレイアイコンの右クリックメニューには `AutomationId` を付けられない
（`MenuFlyoutItem` を実行時に組み立てているうえ、探索の起点がシェル側にあるため）。
代わりに**プロセス ID で自分のメニューだと確定する** ── このメニューは WinUI の
`MenuFlyout` なので、アプリのプロセスが所有する
`Microsoft.UI.Content.PopupWindowSiteBridge` としてデスクトップ直下に現れる。
自動テストは `tests/ProcessRecorderApp.E2E/TrayUi.cs`（`Category=Fragile`）にあり、
Windows 11 のオーバーフローの開閉と**物理的なマウスカーソルでの右クリック**を伴うため
CI の必須ゲートからは外してある。トレイ自動化の罠（`Shell_NotifyIconGetRect` は
プロセス外から使えない等）は [docs/test-harness.md](../docs/test-harness.md) の L3 の節にある。

## ビルド／発行

### 置き場所のフルパスを短く保つこと（**最初に踏みやすい**）

**リポジトリを深い場所に置くとビルドできない。** MakePri（PRI の展開）が
`MAX_PATH`（260 文字）を超えられず、次の形で失敗する:

```
WINAPPSDKEXPANDPRICONTENT : error PRI175: 0x80070003 - MakePri failed with error: 指定されたパスが見つかりません。
WINAPPSDKEXPANDPRICONTENT : error PRI222: 0x80070003 - Unspecified error occurred.
error APPX0002: Task 'WinAppSdkExpandPriContent' failed. Could not find file
  '...\src\ProcessRecorderApp\obj\Debug\net10.0-windows10.0.19041.0\CommunityToolkit.WinUI.Controls.LayoutTransformControl.pri.xml'
```

`0x80070003` は `ERROR_PATH_NOT_FOUND`。**「ファイルが無い」と言っているが、実際には
パスが長すぎて作れていない** ── メッセージが原因を指していないので、
`obj` を消す・restore し直すといった方向へ時間を使いやすい。

- **リポジトリルート以下だけで 175 文字を使う**
  （`src\ProcessRecorderApp\obj\Debug\<TFM>\CommunityToolkit.WinUI.Controls.LayoutTransformControl.pri.xml`）。
  つまり**ルートのフルパスに使えるのは実質 80 文字程度**である。
- **GitHub の「Download ZIP」は特に危ない。** 展開すると
  `<リポジトリ名>-<ブランチ名>` というフォルダができ、同名のフォルダへ展開すると
  **二重になる**。このリポジトリのブランチ名は長いので、それだけで 110 文字使う。
- **実測**: `C:\Users\<user>\Downloads\` の下に上記の二重フォルダを作ると
  obj のパスが **313 文字**になり、上の3エラーが再現する。
  同じコミットを `C:\src\_v` へ clone して Debug ビルドすると **0 警告 0 エラー**。
  **＝コミット側の問題と区別できる**（切り分けはこの順でやること）。
- **CI は踏まない**（`D:\a\ProcessRecorderApp\ProcessRecorderApp\` は 28 文字）ので、
  **これは CI の緑では守られない。**
- 対処は**置き場所を浅くする**こと（`C:\src\...` など）。
  なお `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled` を立てても
  **MakePri に効くかは未検証**。

### パッケージの取得元

取得元は **nuget.org 1 つ**で、リポジトリルートの `nuget.config` が固定する。
`<clear />` でマシン/ユーザー設定のソースを遮断する ── 手元にだけ登録された
フィードから同名パッケージが解決されると、CI と手元で別の中身をビルドしうるため。

- **復元に認証は要らない。** `GstSharp.Net*`（録画エンジンのバインディング）も
  `UiaTrigger.*` も nuget.org 発行なので、資格情報の受け渡しは手元にも CI にも無い。
- 振り分けの正本は `packageSourceMapping` で、**パッケージ版の集中管理（次項）では
  マッピングが無いと `NU1507` になる**。`GstSharp.Net*` は `*` と別に書いてあるが、
  取得元を明示して残すためのもので解決結果は変わらない。
- `GstSharp.Net*` は prerelease なので、版は `Directory.Packages.props` に明示する
  （`--prerelease` 無しの `dotnet add package` では拾われない）。

### パッケージ版の集中管理

NuGet パッケージのバージョンは**リポジトリルートの `Directory.Packages.props`** だけが
持つ（`ManagePackageVersionsCentrally=true`）。各 `.csproj` は
`<PackageReference Include="..." />` と、`PrivateAssets` / `IncludeAssets` /
`ExcludeAssets` などのメタデータだけを書く。

- csproj に `Version` を書くと **`NU1008`**、`Directory.Packages.props` に無いものを
  参照すると **`NU1010`** で復元が落ちる。どちらもビルドより前に分かる。
- **置き場所はリポジトリルートでなければならない。** SDK はプロジェクトのディレクトリから
  上へ辿って最初の 1 つを使うので、`src/` に置くと `tests/` の 2 プロジェクトへ効かない
  （`src/Directory.Build.props` は逆に `src/` 配下だけへ効かせたいので、
  こちらをルートへ上げてはいけない ── テストホストが `UseWinUI` / `IsAotCompatible` を
  継承してしまう。2 つのファイルの探索は互いに独立している）。
- `CentralPackageTransitivePinning` は有効にしていない。有効にすると推移的依存が
  直接依存へ昇格して解決結果が変わる。

### 必要な Windows SDK

TFM は `src/Directory.Build.props` で **`net10.0-windows10.0.19041.0`** と定義している
（テストの2プロジェクトも同じ値を各 `.csproj` で宣言しており、変えるときは揃えること）。
`TargetPlatformVersion`（＝`net10.0-windows` に続くバージョン）は「**ビルドに使う**
Windows SDK 参照のバージョン」であり、動作対象OSの下限は独立した
`TargetPlatformMinVersion`（`10.0.17763.0`）が決める。

- SDK 参照は `WindowsSdkPackageVersion`（**`10.0.19041.87`**）で NuGet の参照パックに
  ピン留めしてある ── これにより `Microsoft.Windows.CsWinRT` のパッケージ参照が不要になり、
  ローカルの Windows SDK（`Platforms\UAP\<ver>\Platform.xml`）にも依存しない
  （理由の詳細は `src/Directory.Build.props` のコメント）。
- 発行プロファイル（`*.pubxml`）には `TargetFramework` を**書かない**。書くと定義元が
  二重化し、SDK 更新のたびに片方だけ取り残されて発行が失敗する。

### 構成

- `ProcessRecorderApp.csproj`: `OutputType=Exe`、`WindowsPackageType=None`
  （MSIXを使わないアンパッケージ配布）。`PlatformTarget=x64` と `RuntimeIdentifiers=win-x64`
  は `src/Directory.Build.props`、`WindowsAppSDKSelfContained=true` は発行プロファイル側にある。
  `DISABLE_XAML_GENERATED_MAIN` を定義し、WinUI3標準の自動生成 `Main()` を無効化して
  `Program.cs` 内のカスタム `Main()` を使用する。
- Native AOT の発行設定（`PublishAot`/`PublishTrimmed`/`PublishReadyToRun`/`SelfContained`）は
  **発行プロファイル（`Properties/PublishProfiles/*.pubxml`）にのみ**存在する。
  `.csproj` に構成条件付きで置くことはしない（Release ビルドが常に AOT になり CI が回らなくなる）。
  一方 `IsAotCompatible` / `EnableTrimAnalyzer`（`src/Directory.Build.props`）は常時有効で、
  AOT 非互換の混入は Release ビルドの解析警告として検出される。
- `Properties/PublishProfiles/` に AOT・フレームワーク依存・セルフコンテインド・
  シングルファイルの各発行プロファイルを用意。
- `GStreamer.GstSharpNet` プロジェクトの `runtimes/win-x64` は **リポジトリには入っていない**
  （サイズが大きいため追跡しない）。同梱ビルドを作るときだけ
  `tools/Fetch-GStreamerRuntime.ps1` でここへ展開する。csproj 側は
  `None Include="runtimes\**"`（`Content` にすると `GStreamer/runtimes` にも複写される）で、
  `CopyToOutputDirectory`/`CopyToPublishDirectory=PreserveNewest`、
  単一ファイル発行時は `ExcludeFromSingleFile=true` で除外される
  （同梱DLL群はネイティブライブラリのため単一exeへは埋め込めない）。
  同梱しない場合の解決順は「GStreamer の解決経路」を参照。
- ログは `%LOCALAPPDATA%\ProcessRecorderApp\activity.log` に記録される。
- メインウィンドウの閉じるボタン(×)・最小化ボタンは、アプリを終了せずタスクトレイへ格納する
  （終了はタスクトレイアイコンの右クリックメニュー「Quit」から、または Ctrl+× から行う）。
