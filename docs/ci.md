# CI とリリース

CI は2つのワークフローに分かれる。`build.yml` は push のたびに「壊れていないこと」を検証し、`release.yml` は「配れるもの」を作る。GStreamer の実行時ツリーはリポジトリに置かず、履歴でも追跡しないため、`build.yml` はランナーに MSYS2(UCRT64) 版を入れ、`release.yml` は削減済みランタイムを Release アセットから取得して同梱する。この違いが、それぞれのワークフローで検証できる範囲を決めている。

## build.yml の構成と理由

トリガーは全ブランチへの push と `workflow_dispatch`。`windows-latest` の2ジョブ構成で、`build-and-test`（timeout 90 分）と `publish-aot`（timeout 120 分）が並走する。timeout は所要時間の見積もりではなく、ハングした run に既定の 6 時間を焼かせないための上限であり、意図的に厚く取る ── 実測は、開発機のフルスイートが 57 件・8 分強（GPU 無し）。ランナーの Fragile 除外 E2E は**同じコードでも 4〜9 分の幅で揺れる**ので、このばらつきの大きさ自体が厚い上限の根拠になる。打ち切りは「テスト結果」ではなく「何も分からない」なので、上限を薄くすると赤の切り分けが1サイクル遅れる。

`build-and-test` の段の順序と理由:

1. **L4（ローカライズ・ドキュメント齟齬）を最初に置く。** 翻訳漏れやキーの綴り誤りは静かに壊れる種類の退行で、しかも検査は数秒で終わる。L4 のテストプロジェクトは Components / GStreamer.GirCore / SingleInstance しか参照しないので、XAML コンパイルを含む WinUI アプリ全体のビルドを待たずに弾ける。
   **このステップにも `TreatWarningsAsErrors=true` が要る。** このステップが Components / GStreamer.GirCore / SingleInstance / テストの4プロジェクトを Release でビルドしてしまい、後続の `-warnaserror` ビルドではそれらが「最新」と判定されてコンパイラが走らず、警告が再出力されない。付けないとこの4プロジェクトだけ 0 警告の保証が抜ける。
2. **`dotnet build -c Release -warnaserror`。** リポジトリの規約は 0 警告。トリミング／AOT 解析（`IsAotCompatible` / `EnableTrimAnalyzer`）は `src/Directory.Build.props` の条件無し PropertyGroup にあり、構成によらず常時有効 ── 「Release だから解析される」のではない。このステップの価値は `-warnaserror` で解析警告をエラーに昇格させる側にあり、AOT 非互換の混入はこれで落ちる。
3. **L1（単体テスト）。**
4. **`publish`（selfcontained）＋ 発行物に exe が実在することの確認。** 発行ステップに `--no-restore` を使ってはいけない ── `PublishReadyToRun` は `.pubxml` にしか書いていないので、プロファイル抜きの restore では ReadyToRun のランタイムパック（crossgen2）が復元されず `NETSDK1094` になる。
5. **MSYS2(UCRT64) で GStreamer を入れる**（`msys2/setup-msys2@v2`）。**`gst-plugins-ugly` は必須** ── E2E フィクスチャが `x264enc` を明示指定しており、抜けると `openh264enc` へ落ちる。openh264 の bitrate は bit/sec で x264 の kbit/sec と桁が違うため、生成サイズの前提（下記の 20MB 下限など）が丸ごとずれる。展開先はランナー任せなので `C:\msys64` を決め打ちせず、`steps.msys2.outputs.msys2-location` から `ucrt64\bin` を組み立てて `GITHUB_PATH` に足す（`GStreamerRuntimeLocator` の候補1「元の PATH」に効かせる）。本体 DLL と `libgstx264.dll` の存在はこのステップで確かめて早く落とす ── E2E まで持ち越すと「GStreamer が無い」のか製品の不具合なのかの切り分けに数十分かかる。ただしこの2点検査があるのは `build-and-test` だけで、`publish-aot` の同名ステップは本体 DLL しか確かめない ── AOT ジョブで `gst-plugins-ugly` が欠けると検査を素通りし、E2E で初めて表面化する。
6. **L2 + L3（E2E）を発行物に対して実行する。** ここで初めて「録画が実際にできること」「GUI が実際に操作できること」が検証される。`--filter "Category!=Fragile"` で `TrayMenuTests` だけを外す ── 通知領域のオーバーフローを物理的なマウスカーソルで操作するテストで、不安定さの原因がシェル側にあるため、赤くなっても製品の退行を意味しない。CI ランナーには GPU が無い（WARP）ので、フィクスチャは `Type=System` + `videotestsrc` + `x264enc` を明示設定して起動する ── これはエンコーダーの自動フォールバックが効いていることの実証にもなる。このステップは `TMP` / `TEMP` を `runner.temp` に固定し `PROCESSRECORDERAPP_E2E_KEEP` を立てる ── 既定の一時ディレクトリはランナーによって `runner.temp` と別の場所になり、そのままだと失敗時の成果物収集が空振りする。
7. **別ジョブ（`publish-aot`）で AOT 発行 ＋ AOT 発行物に対する L2 + L3。** 配布物が AOT（`release.yml`）なので、タグ限定ではなく常時流す（Fragile 除外は同じ）。AOT 固有の破損（リフレクション欠落）は発行時ではなく実行時に出る ── PropertyGrid のプロパティ列挙と設定 JSON のソース生成が危険域で、L1 では検出できない。このジョブはゲートである（`continue-on-error` は付けない ── run 単位の `success` 表示が赤いジョブを隠す誤読を防ぐ）。

**NuGet の復元には GitHub Packages の認証が要る。** `UiaTrigger.*` パッケージはルートの `nuget.config` が `packageSourceMapping` で GitHub Packages（読み取りにも認証必須）へ向けており、両ワークフローともワークフローレベルの `env` で `GITHUB_PACKAGES_USER`（`github.actor`）と `GITHUB_PACKAGES_TOKEN`（`secrets.GITHUB_TOKEN`）を与えている。ステップ単位にしないのは、復元が明示の `dotnet restore` だけでなく publish / test の暗黙復元（`--no-restore` にできない上記の理由）でも走るため。あわせて `permissions` に `packages: read` を明示する ── `release.yml` は `contents: write` を書いた時点で未記載スコープが none になるため、書かないと復元が 401/403 で落ちる。`build.yml` にも既定権限に依存しないよう明示してある。もし `GITHUB_TOKEN` で `UiaTrigger.*` の復元が 401/403 になる場合は、① UiaTrigger 側のパッケージ設定「Manage Actions access」に本リポジトリを追加する（恒久対処）、② それでも通らなければ read:packages の PAT をシークレット（例 `GH_PACKAGES_PAT`）に置き、両ワークフローの `GITHUB_PACKAGES_TOKEN` の値だけ差し替える（`nuget.config` は無変更）。

**AOT ジョブでは MSYS2 のステップを AOT 発行の「後」に置く。** この action は後続ステップの PATH を書き換える。ILCompiler は `findvcvarsall.bat` 経由で `vswhere` を PATH 前提で探すため、発行より前に PATH をいじると、リンカのパスが壊れて MSB3073（exit 123）になる罠を踏む。MSYS2 が要るのは E2E だけ。同じ理由で AOT 発行にも `--no-restore` は使えない（ILCompiler パッケージは `.pubxml` のプロファイル付き復元でしか入らない）。

両ジョブとも E2E の前に WER LocalDumps を武装する（DumpFolder を `runner.temp` 配下へ明示、DumpType=1 のミニダンプ）。書けたことを読み戻して `wer-status.log` に残す ── 武装できていない run の「ダンプが無い」を「クラッシュではない」と誤読しないため。AOT ではマネージドのスタックトレースが出ないので、ダンプが落ちた場所を知る唯一の手段になる。

## release.yml の構成と理由

トリガーは `v*` タグの push と `workflow_dispatch`。`build.yml` と分けるのは、同梱用の GStreamer ランタイムを毎回取得するため push のたびに走らせる価値が無いから。**発行は Native AOT（`win-x64-aot`。`build.yml` の AOT ジョブと同じ形態）で、配布するのは AOT 版のみ** ── selfcontained(ReadyToRun) は CI の検証用で配布しない。**同梱・非同梱の2つの zip** を作る:

- 非同梱（`ProcessRecorderApp-<tag>-win-x64.zip`）── 利用者側に GStreamer(MinGW) か MSYS2(UCRT64) が要る。軽い。
- 同梱（`ProcessRecorderApp-<tag>-win-x64-gstreamer.zip`）── 削減済み runtimes（45 ファイル・49.7MB）を同梱する。x264 と libav を含まないので GPL を持ち込まず、openh264 も特許の都合で含まない。同梱構成の `Type=System` は `mfh264enc` に落ちる。

**このジョブは MSYS2 を入れない。** そのため同梱物がランタイム解決の唯一の候補になり、`gst.runtime selected=bundled` を実際に踏める唯一の場所である ── 開発機や `build.yml` では、解決順（元の PATH → 環境変数 → レジストリ → MSYS2 → 同梱物）の都合で同梱物が必ず負けるため、ここでしか検証できない。流すのはエンコーダーに依存しないスモークだけ ── フィルタ `FullyQualifiedName~SmokeTests|FullyQualifiedName~RuntimeResolutionTests` は部分一致なので、実際に走るのは `SmokeTests`・`GuiSmokeTests`・`RuntimeResolutionTests` の 3 クラス。録画系 E2E が流れないのは、ハーネスが `SettingsFile.DefaultEncoder = "x264enc"` を固定しており同梱物に x264 が無いため（詳細は docs/coverage-gaps.md）。

検証の要点:

- 同梱ランタイムは `licenses/third-party/COMPONENTS.tsv` と**過不足なく一致**することを見る。件数の下限では「多い分」を捕まえられず、ライセンス文の無いファイルが黙って混ざる。
- **スモークが実際にテストを選べたことを、直後のステップが `release-smoke.trx` で確認する。** `--filter` は1件も選ばなくても `dotnet test` が成功で終わるので、これが無いとクラスの改名・移動で**緑のまま無検証の zip を配る**。見るのは件数の下限ではなく「上の3クラスが結果に出ていること」── 下限では「1クラスだけ消えた」を捕まえられない。フィルタを変えるときは、このステップの期待クラス一覧も一緒に直すこと。
- ライセンス文はリポジトリのものとハッシュ一致まで確認する。**配布物にライセンス文が入っていることを見る唯一の場所**である（L1 の `ThirdPartyLicenseTests` が見られるのはリポジトリ内の整合だけ）。
- 非同梱側は `runtimes/` と `licenses/third-party/` が**入っていないこと**を確認する（「入っていないのが正しい」側の検証）。
- 非同梱の発行には `BundleGStreamerRuntime=false` を明示する。既定は「`runtimes/` に本体 DLL があれば同梱」なので、取得ステップとの順序が入れ替わると黙って同梱版になる。
- zip 名に使う `ref_name` は `/` と `\` を `-` に置換する。`workflow_dispatch` でブランチから流すと `feature/xxx` のような値になり、そのままでは `Compress-Archive` が存在しないディレクトリを指して落ちる。

## リリースの流し方（v* タグ）

1. **先に Release の箱を作る**: `gh release create <tag> --target <フル SHA> --prerelease`。`--target` に短縮 SHA を渡すと `HTTP 422 Release.target_commitish is invalid` になる ── フル SHA を使うこと。この手順が要るのは、ワークフロー最後の `gh release upload <tag>` が**既存の Release を要求する**ためで、タグ push だけでは Release はできない。
2. タグが push されると `release.yml` が走る（`gh release create` のような API 経由のタグ作成でも `push` イベントは飛ぶ）。タグはどのブランチのコミットに打ってもよい。
3. `workflow_dispatch` でも流せるが、Release への添付ステップは `refs/tags/v*` のときだけ実行される。dispatch 実行では zip はワークフローのアーティファクト（`packages`）としてだけ取れる。
4. ランナーの自己申告（ステップの success）だけで済ませず、`gh release download` で出来上がった zip を落として中身を数え直すこと ── runtimes の件数が `COMPONENTS.tsv` と一致するか、ライセンス文がリポジトリと SHA256 一致で入っているか、openh264 を含むファイルが 0 件か。

## 運用上の注意

- **cancel-in-progress**: `build.yml` は `concurrency` で同一 ref の実行を1つに絞り、続けて push すると前の run が打ち切られる。前の run がキャンセルで終わるのは意図した動作であり、異常ではない。
- **アクションは Node 24 で走るメジャーに固定してある**（`actions/checkout@v7` / `actions/setup-dotnet@v6` / `actions/upload-artifact@v7`）。Node 20 のままだとランナーが強制的に Node 24 で走らせたうえで run ごとに警告注釈を出す。`upload-artifact` は **v6 以上でないと消えない** ── v5 は Node 24 に対応しただけで既定は Node 20 のままである。いずれも Runner 2.327.1 以上が要るので、self-hosted へ移すときはランナーの版を先に上げること。`msys2/setup-msys2@v2` は警告の対象外（すでに Node 24）。
- **ジョブ単位で conclusion を見ること。** run 単位の `success` は `continue-on-error` のジョブの失敗を隠す。現在ゲート外のジョブは無いが、確認の習慣として run の色ではなくジョブの色を見る。
- **E2E の打ち切りやタイミング依存の分岐で、ランナーだけで赤が続けて再現したら、それ以上ランナー上での再試行を重ねないこと。** その分岐は純粋関数へ切り出して L1 で守る ── ランナー上の再試行は標本1つに数十分かかり、しかも環境要因と製品の欠陥を区別できない。
- **下限の表明と打ち切りを区別する。** `StopSynchronicityTests` の生成サイズ 20MB は下限の表明なので、ランナーで届かなくても緩めず、録画時間かビットレートを上げて調整する（届かないと退行を検出できないテストになる）。較正の目安: 録画条件は 1280x720/30fps/20Mbit・20 秒で、開発機では 52〜55MB（下限の約 2.5 倍）出る ── ランナーの赤が退行か単なる能力不足かは、この余裕からの落ち幅で判断する。一方 `ShutdownTests` の `ExitBudget`（420 秒）は打ち切りなので、届かないなら緩めてよい ── 打ち切りは「テスト結果」ではなく「何も分からない」。ただし緩めてよいのはランナーの遅さが原因の場合だけで、切り分けは `activity.log` に `recording.stop` が出ているかで行う（出ていれば停止経路は動いていて遅いだけ、出ていなければ製品のハングを疑う）。対象の `CtrlClose_WhileRecording_FinalizesEveryFile` は録画しながら GUI を操作する唯一のケースのため、ソースは `AsBulkyButCheapToEncode`（640x360/15fps・約 20Mbit の `snow`）でバイト数＝検出力を据え置いたまま画素数だけ落としてある ── 録画時間・ビットレートとは別の、負荷だけを下げる第三の調整手段でもある。
- **クラッシュダンプのアーティファクトだけは `always()` で上げる。** ワーカーはテストを緑にしたまま死にうる（ハーネスがリトライで拾う）ため、緑の回の「ARMED かつダンプ0件」を見て初めてクラッシュ無しと言える。ダンプは2系統あり、WER LocalDumps は AOT でも効くが、`DOTNET_DbgEnableMiniDump` 由来の `.dmp` は CoreCLR の機能なので AOT 発行物では出ない。
- **失敗時診断の収集網は拡張子で決まる。** `build.yml` の2ジョブは `*.log` / `*.log.1` / `*.json` を拾うが、`release.yml` のスモーク診断が拾うのは `*.log` / `*.json`（と `TestResults/*.trx`）だけで、ローテート済みの `*.log.1` はそこでだけ黙って落ちる。新しい診断ファイルは `.log` か `.json` にすること。`.txt` にすると黙ってアップロードされず、無いことに気付けない。
- **`Activate_ShowsTheWindowWithoutFaulting` は環境起因で赤くなりやすい。** 対話セッション・デスクトップと WARP でのプレビュー初期化に依存する最初のテストで、赤でも製品の不具合とは限らない ── L3 全般と同じく、まずランナー側の制約を疑う。
- **`LanguageMatrixTests` はどの行が実質的な検査かがランナーの表示言語で入れ替わる。** ランナー（en-US）で `ja-JP` の行が落ちたら、それはランナーの制約ではなく製品の欠陥（ja-JP のリソースが発行物に載っていない）なので、環境起因の赤とは切り分けて扱う。
