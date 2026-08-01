# 環境と実装の背景事実

このファイルは、環境・外部要素（GStreamer / Windows / PowerShell）に関する恒久的な事実をまとめる。いずれも実装・テストの形を決めている制約であり、「なぜこう書いてあるか」の根拠がここにある。同じ事実の多くは実装側のコメント（`src/GStreamer.GirCore/EventRecorder.cs`、`src/SingleInstance/SingleInstanceManager.Launcher.cs`、`tools/` の検証スクリプトなど）にも書かれている。同じ事実を二重に持っている前提で、片方を直したらもう片方も直すこと。

## GStreamer と環境の事実

- **GPU 系エンコーダーは、対応ハードウェアが無い機械では要素ファクトリが登録されない。** `qsvh264enc` / `d3d12h264enc` / `nvh264enc` / `amfh264enc` はプラグイン DLL（`libgstqsv.dll` / `libgstd3d12.dll` / `libgstnvcodec.dll` / `libgstamfcodec.dll`）自体はロードできるため、DLL の有無では使えるかどうかを判定できない。`Gst.ElementFactory.Find` が null を返すことが、そのまま正しい「この機械では使えない」判定になる。エンコーダー自動選択のプローブ（`EncoderCatalog.Probe`、`Controller.StaticInitialize()` の末尾で実行）はこの仕様を利用している。GPU 実機での確認手順は [gpu-verification.md](gpu-verification.md)。
- **エンコーダーの直前には `videoconvert` を置く。外すと実機でだけ壊れる**（`EventRecorder.BuildSinkPipeline`）。`parse_launch` は変換要素を自動挿入しないため、ソースの画素形式がエンコーダーの sink caps に無いと `could not link queue1 to <enc>0` で初期化そのものが失敗する。`videoconvert` を置くと、下流が受け付ける形式へ交渉して合わせてくれる。`srcPipeline` 内の `videoconvert` は代わりにならない（典型的なソースは capsfilter で形式を固定して終わるため、必要なのは tee/queue より後・エンコーダーの直前）。後ろに capsfilter を付けてもいけない ── 形式を決めずに交渉させることが、まさにこの不具合を直している点である。実例: ハードウェアの MediaFoundation MFT は I420 を受けないため、これが無いと `Type=System` の自動選択が `mfh264enc` で `recorder.init fail` になる一方、同じ機械で `Type=D3d12` の `mfh264enc` 手動指定は通る（あちらには `d3d12download ! … ! videoconvert !` が在り、差はそこだけ）。GPU 無しの機械でも `openh264enc`（I420 のみ）＋ NV12 ソースで同じ形の失敗を再現できる。テストや検証スクリプトが `format=I420` を固定していると、試す全エンコーダーが I420 を受けるためこの失敗は再現しない ── 検証側の入力が偶然そろっていると、欠陥は何層あっても通る。なお同梱ランタイムは `x264enc`（GPL のため LGPL-only 構成に含まれない）も `openh264enc` も持たず、`System` 系の自動選択は実質 `mfh264enc` になるため、同梱配布はこの制約を確実に踏む。
- **MP4 の検証は ISO-BMFF を直接パースする方が安定する。** `gst-discoverer` は外部プロセスの起動と `GST_PLUGIN_PATH` 等の環境整備を要求する。`ftyp` / `moov`（内部の `mvhd`）/ `mdat` / `avcC` をトップレベルのアトムから直接読めば、依存ゼロで「本物の MP4 か・H.264 トラックがあるか・尺は何秒か」が取れる（実装は `tests/ProcessRecorderApp.E2E/Mp4File.cs`）。`gst-discoverer` 自体はフルインストールの GStreamer には含まれるが、同梱ランタイムは実際に読み込む閉包だけ（45 ファイル・プラグイン 14 本。全数は `licenses/third-party/COMPONENTS.tsv`）に絞ってあり、`gst-discoverer-1.0.exe` もその依存 `libgstplayback.dll` も入っていない。ISO-BMFF 直接パースはどちらの環境でも成立する。
- **常駐ワーカーの初回起動は 10 秒を超えることがある。** GStreamer のプラグインレジストリ構築が初回だけ挟まるため。ランチャーの待ち（`StartResidentWorkerAndWaitForRegistration`）はこのために上限を 120 秒に取ってある。起動時間や初回コマンドの成否を計測するテスト・スクリプトは、計測対象のケースより前に「レジストリを温める1回の起動」を必ず入れること。
- **PowerShell の `Start-Process -Wait` は使わない。** これはプロセスツリー全体の終了を待つため、常駐ワーカーが残る本アプリでは永久に返らない。検証スクリプトはランチャープロセス単体を `System.Diagnostics.Process` + `WaitForExit` で待つ（`tools/Verify-GpuEncoders.ps1` / `tools/Verify-HighResolution.ps1` が実例）。

## 4K・高解像度での循環待ちとプレビュー枝

高解像度では「初期化は成功・エラー無しなのに、録画もプレビューも1フレームも進まない」というデッドロックが起きる。機構は循環待ちである:

1. `queue` の既定は `max-size-bytes=10485760`（10MB）。queue は上限超過でも1件目は必ず受け取るため、1フレームが 5,242,880 B（10MB の半分）を超えると「常に1フレームしか持てない queue」に化ける。I420 では 3,495,254 画素＝2560x1440 以上が境界（4K の1フレームは 12,441,600 B）。
2. プレビューの `appsink` は PAUSED の間プリロールで止まっている → プレビュー枝の queue が排出されず、満杯の queue が `tee` を止める。
3. エンコーダーにフレームが届かない。エンコーダーは最初の1フレームを出すまでに数フレーム溜める（例: `qsvh264enc` の `async-depth` 既定は 4）ため、出力が1つも出ない。
4. 録画側 `appsink name=sink` がプリロールできず、パイプラインは PLAYING に到達しない → プレビューの `appsink` も止まったまま → 2 へ戻る。

実測（製品と同形のパイプラインで解像度以外を固定）: 320x240（1フレーム 115,200 B、queue に 91 フレーム）／1280x720（1,382,400 B、7）／1920x1080（3,110,400 B、3）は 0.39〜0.49 秒で PLAYING に到達する。2560x1440（5,529,600 B）と 3840x2160（12,441,600 B）は queue が1フレームしか持てず、15 秒経っても到達しない。

このため:

- **プレビュー枝の queue は `queue leaky=downstream max-size-buffers=1 max-size-bytes=0 max-size-time=0` にする**（`EventRecorder` の `PreviewQueue` 定数）。これで 2560x1440 は 0.67 秒、3840x2160（4K）は 0.49 秒で到達する。`leaky=downstream` は古い方を捨てて最新フレームだけを通す。バイト数と時間の上限を外すのは、判定を解像度に依存させないため。プレビューが背圧を掛ける側であってはならないという意図は `appsink` 側の `max-buffers=1 drop=true` に既にあり、`appsink` が PAUSED で止まる窓ではそれが効かないので、手前の queue にも同じ意図を書く。
- **エンコーダー枝の queue は既定のままにする。** 高解像度ではこちらも実質1フレームぶんの余裕しか無くなる（leaky 化後の 4K の `.dot` ではエンコーダー枝の `queue1` が 12,441,600 B＝1フレームを保持している）が、エンコーダーが実際に排出するので循環はせず、遅延にとどまる ── 満杯になることとデッドロックすることは別である。詰まった場合に `tee` を止めるのは録画を優先する正しい背圧であり、両方を leaky にすると録画フレームを黙って捨てるようになる。この形は `BuildSinkPipelineTests.EveryType_MakesThePreviewBranchQueueUnableToBlockTheTee` / `EveryType_KeepsTheEncoderBranchQueueBlocking` が固定している。
- **初期化の成否は `SetState(Playing)` の戻り値では判定できない。** 実際に PLAYING へ到達したことを `GetState` で待つ（`EventRecorder.WaitUntilPlaying`、上限 `PlayingStateTimeoutMs` = 5000 ms）。`gst-launch` の標準出力 "Setting pipeline to PLAYING" も到達を意味しない。到達の有無は `GST_DEBUG_DUMP_DOT_DIR` の `PAUSED_PLAYING.dot` の有無で判定できる。

## clockoverlay と Direct2D（使ってはいけない理由）

時刻焼き込みに `clockoverlay`（pango）を使ってはいけない。録画パイプラインは `dwriteclockoverlay` を使う。

- `clockoverlay` は `libgstpango` の要素で、グリフ描画は pangocairo → cairo →（cairo が DirectWrite バックエンドでビルドされている場合）Direct2D と流れる。
- cairo はこの経路で `D2D1_FACTORY_TYPE_SINGLE_THREADED` のファクトリを**プロセス共通のグローバルに1つだけ**作る。単一スレッド用ファクトリは呼び出し側が直列化する責任を負うが、cairo はそれをしない。したがって**複数スレッドから同時に描くと D2D の内部状態（グリフキャッシュ）を壊す**。
- 本アプリはレコーダーごとにストリーミングスレッドが1本あるため、**2本以上のレコーダーで録画するだけで再現する**。録画パイプラインはウィンドウ表示と無関係に回るので、UI 操作は関係ない。症状はワーカープロセスが終了ログも残さず消えることで、実測した終了コードは `0xC0000005`（アクセス違反）／`0xC0000374`（グリフキャッシュの二重解放）／`0xC0000409`（CFG の間接呼び出しチェック失敗）。
- 再現は cairo のビルド構成に依存する。MSYS2 ビルドの cairo は D2D 経路を採るので落ちる。GStreamer 公式 MinGW ビルド（同梱ランタイムの元）の cairo は D2D 経路を採らないので落ちない。つまり「手元で落ちない」は安全を意味せず、cairo が DirectWrite バックエンドの環境の利用者が同じクラッシュを踏む。
- **決定的な再現手順があり、オーバーレイまわりを触るときはまずこれを回す。** アプリを一切含まない `gst-launch-1.0` の1プロセス内に、`videotestsrc num-buffers=400 ! videoconvert ! video/x-raw,format=I420,width=640,height=360,framerate=30/1 ! <オーバーレイ> ! fakesink sync=false` のチェーンを複数本並べ（各チェーンが独立したストリーミングスレッドを持つ）、チェーン数だけを変えて各 8 回実行し、非ゼロ終了を数える。
  `gst-launch-1.0.exe` は同梱ランタイムにも含めてあるので、同梱配布の実機でもこの再現を実行できる。cairo が D2D 経路の GStreamer（MSYS2 ビルド等）では、pango 版はチェーン1本で 0/8・4本で 8/8（`0xC0000005`）、dwrite 版は1本も4本も 0/8 ── 分離は完全で、所要は数秒×8回。E2E スイートで確かめようとしないこと（桁違いに遅く、結果も揺れる）。
- `dwriteclockoverlay`（`gst-plugins-bad` の dwrite プラグイン、`libgstdwrite.dll`。同梱ランタイムにも含まれる）は cairo を経由せず DirectWrite を直接使う。sink caps は `video/x-raw(ANY)` なのでシステムメモリ経路でも使える。プロパティは `time-format` / `auto-resize` / `font-family` / `font-size`（`font-desc` は pango 専用なので無い）。
- 見た目の差は小さいが同一ではない。字形の寸法は同一（同一文字列の描画幅 332 px で一致）。違いはアンチエイリアス方式で、pango 版はサブピクセル（ClearType 相当。発光画素の 47.7% に色が付く）、dwrite 版はグレースケール（0%）。描画原点は dwrite 版が 8 px 左・16 px 上（左上のタイムスタンプなので実用上の影響は無い）。黒一色ソースを符号化した MP4 サイズは、オーバーレイ無し 5,474 B／pango 16,477 B／dwrite 13,008 B で、差は色差平面に載る情報量で説明できる（描画されていることの確認にも使える）。
- テストで pango 版への回帰を検出するときは**部分文字列照合にしないこと**。`dwriteclockoverlay` は文字列として `clockoverlay` を含むため、`Assert.Contains("clockoverlay", ...)` は差し替えを検出しない。区切りまで含めて照合する。
