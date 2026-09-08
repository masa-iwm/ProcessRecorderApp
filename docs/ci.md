# CI とリリース

CI は2つのワークフローに分かれる。`build.yml` は push のたびに「壊れていないこと」を検証し、`release.yml` は「配れるもの」を作る。GStreamer の実行時ツリーはリポジトリに置かず、履歴でも追跡しないため、`build.yml` はランナーに MSYS2(UCRT64) 版を入れ、`release.yml` は削減済みランタイムを Release アセットから取得して同梱する。この違いが、それぞれのワークフローで検証できる範囲を決めている。

## build.yml の構成と理由

トリガーは全ブランチへの push と `workflow_dispatch`。`windows-latest` の3ジョブ構成で、発行の 2 ジョブ（`build-and-test` timeout 90 分・`publish-aot` timeout 120 分）が並走し、その両方を `needs` に取る `e2e`（timeout 60 分）が**形態（selfcontained / aot）× シャード（gui / web / core）の 6 ジョブの matrix**で走る。timeout は所要時間の見積もりではなく、ハングした run に既定の 6 時間を焼かせないための上限であり、意図的に厚く取る ── ランナーの Fragile 除外 E2E の実測は**直列 1 ジョブで約 21 分（196 件）**で、上限はその数倍を取ってある（シャード後の実測は初回 run の trx で差し替える）。打ち切りは「テスト結果」ではなく「何も分からない」なので、上限を薄くすると赤の切り分けが1サイクル遅れる。

**壁時計の見込み**: 直列で回すと E2E は 1 ジョブ 21 分（開発機では 196 件・約 29 分）掛かり、run 全体の長さはこれで決まる。シャードに割ると最長のシャードが律速になり、run 全体は約 17 分になる（実測: 発行の 2 ジョブが 6 分、e2e の各ジョブは固定費 2 分＋走行 gui 6 分・core 7.5 分・web 8.7 分）。代償は**固定費とランナー数**である ── matrix の 1 ジョブごとに checkout・.NET のセットアップ・artifact の download・MSYS2 の展開（この 1.5 分が固定費の大半）が掛かり、それが 6 本ぶん増える。短くなるのは壁時計であって、消費する計算時間ではない。

**その固定費を削る 2 つの仕掛け**:

- **NuGet のグローバルパッケージフォルダのキャッシュ**（`actions/cache`、`~/.nuget/packages`）を `build-and-test` と `publish-aot` の「Set up .NET」の直後に置く。キーは `nuget-<os>-<hashFiles>` で、入力は各 `*.csproj`・`Directory.Packages.props`・`Directory.Build.props`・`*.pubxml`・`nuget.config`・`global.json`（SDK の版が同梱の暗黙パッケージの版を決める）。復元キーは接頭辞 `nuget-<os>-` まで。**`dotnet restore` / `dotnet publish` の書き方は変えない** ── 発行に `--no-restore` を使えない理由（`.pubxml` にしか無い `PublishReadyToRun` / `PublishAot`）はそのまま残るので、キャッシュは復元を**速くするだけで省かない**。したがって**古いキーに当たっても壊れない**（足りないパッケージだけ取りに行く）。`e2e` ジョブには置かない ── 下記のとおり、あちらはもう何も復元しない。
- **E2E のテストアセンブリを artifact（`e2e-tests`）で渡す**。`build-and-test` の `-warnaserror` ビルドは slnx 全体なので E2E のテストアセンブリもそこで出来ており、その `bin` ディレクトリ（`tests/ProcessRecorderApp.E2E/bin/Release/net10.0-windows10.0.19041.0`。RID の段が無いのは csproj の `AppendRuntimeIdentifierToOutputPath=false`）をそのまま上げる。`e2e` ジョブは同じパスへ戻し、`tools/Run-E2E.ps1` を `-NoBuild` で呼ぶ。これで matrix の 6 ジョブから restore とビルドが丸ごと消える。**`-NoBuild` は dll を直接指す**（`dotnet test <bin>\ProcessRecorderApp.E2E.dll`）── `dotnet test <プロジェクト> --no-build` は `obj/project.assets.json` を要求し、artifact はそれを運ばないため。dll 形式は VSTest へ素通しになり、`--filter` の文法と選択結果は同じ（`Category=Gui&Category!=Fragile` は両形式で 52 件）。**このアセンブリはビルド時のワークスペースの絶対パスを埋め込んでいる**（csproj の `AssemblyMetadata "RepositoryRoot"`）── 別ジョブのランナーで動くのは GitHub の Windows ランナーが常に同じ `${{ github.workspace }}` へ checkout するからで、そこが食い違えば `Strings/<locale>/Resources.resw` をこの根から解決する `LanguageMatrixTests` が落ちる。

`build-and-test` の段の順序と理由:

1. **L4（ローカライズ・ドキュメント齟齬）を最初に置く。** 翻訳漏れやキーの綴り誤りは静かに壊れる種類の退行で、しかも検査は数秒で終わる。L4 のテストプロジェクトは Components / GStreamer.GstSharpNet / SingleInstance しか参照しないので、XAML コンパイルを含む WinUI アプリ全体のビルドを待たずに弾ける。
   **このステップにも `TreatWarningsAsErrors=true` が要る。** このステップが Components / GStreamer.GstSharpNet / SingleInstance / テストの4プロジェクトを Release でビルドしてしまい、後続の `-warnaserror` ビルドではそれらが「最新」と判定されてコンパイラが走らず、警告が再出力されない。付けないとこの4プロジェクトだけ 0 警告の保証が抜ける。
2. **`dotnet build -c Release -warnaserror`。** リポジトリの規約は 0 警告。トリミング／AOT 解析（`IsAotCompatible` / `EnableTrimAnalyzer`）は `src/Directory.Build.props` の条件無し PropertyGroup にあり、構成によらず常時有効 ── 「Release だから解析される」のではない。このステップの価値は `-warnaserror` で解析警告をエラーに昇格させる側にあり、AOT 非互換の混入はこれで落ちる。
3. **L1（単体テスト）。** 続けて **E2E のテストアセンブリを artifact（`e2e-tests`）へ上げる**（上記。`if-no-files-found: error`）── `e2e` ジョブはこれを受け取ってビルドせずに回す。
4. **`publish`（selfcontained）＋ 発行物に exe が実在することの確認。** 発行ステップに `--no-restore` を使ってはいけない ── `PublishReadyToRun` は `.pubxml` にしか書いていないので、プロファイル抜きの restore では ReadyToRun のランタイムパック（crossgen2）が復元されず `NETSDK1094` になる。
5. **発行物を artifact（`publish-selfcontained`）へ上げる。** E2E は別ジョブなので、発行ディレクトリはこの経路でしか渡らない。`if-no-files-found: error` にしてあるのは、空の artifact を配ると `e2e` 側が「発行物が無い」でしか落ちず、原因がこのジョブに在ることが見えなくなるため。
6. **別ジョブ（`publish-aot`）で AOT 発行 ＋ artifact（`publish-aot`）。** 配布物が AOT（`release.yml`）なので、タグ限定ではなく常時流す。このジョブはゲートである（`continue-on-error` は付けない ── run 単位の `success` 表示が赤いジョブを隠す誤読を防ぐ）。**MSYS2 をこのジョブに置かない**のは、`setup-msys2` が後続ステップの PATH を書き換え、ILCompiler が `findvcvarsall.bat` 経由で PATH から探す `vswhere` を隠して MSB3073（exit 123）を招くため。MSYS2 が要るのは E2E だけなので `e2e` ジョブに在る。同じ理由で AOT 発行にも `--no-restore` は使えない（ILCompiler パッケージは `.pubxml` のプロファイル付き復元でしか入らない）。**AOT のネイティブ PDB は 80MB あり、この artifact に毎回載る**（発行ディレクトリごと上げるため）── `e2e` 側がダンプの記号化に使う。

`e2e` ジョブ（matrix）:

1. **発行物とテストアセンブリを download-artifact で元と同じパスへ戻す**（`src/ProcessRecorderApp/bin/Release/win-x64/publish/<flavor>`。発行ディレクトリ名は形態名と同じなので `matrix.flavor` がそのままパスになる）。**artifact 経由にする理由は 2 つ** ── AOT をシャードごとに発行し直すと 1 ジョブあたり数分（開発機で 3.4 分）を捨てることになる。そしてランナーを分ければ、E2E 同士が CPU を奪い合わない（同じ機械にプロセスを 2 本並べると大半のテストが 1.1〜1.3 倍に伸びる）。テストアセンブリ（`e2e-tests`）も同様に `tests/ProcessRecorderApp.E2E/bin/Release/net10.0-windows10.0.19041.0` へ戻す ── **このジョブは restore もビルドもしない**。
2. **MSYS2(UCRT64) で GStreamer を入れる**（`msys2/setup-msys2@v2`）。**`gst-plugins-ugly` は必須** ── E2E フィクスチャが `x264enc` を明示指定しており、抜けると `openh264enc` へ落ちる。openh264 の bitrate は bit/sec で x264 の kbit/sec と桁が違うため、生成サイズの前提（下記の 20MB 下限など）が丸ごとずれる。展開先はランナー任せなので `C:\msys64` を決め打ちせず、`steps.msys2.outputs.msys2-location` から `ucrt64\bin` を組み立てて `GITHUB_PATH` に足す（バインディングの解決で最優先の段「元の `PATH` のディレクトリ走査」に効かせる）。本体 DLL・`libgstx264.dll`・`libgstopenh264.dll` の存在はこのステップで確かめて早く落とす ── E2E まで持ち越すと「GStreamer が無い」のか製品の不具合なのかの切り分けに数十分かかる。`libgstopenh264.dll`（`gst-plugins-bad`）を見るのは、録画トランスコードの E2E が `openh264dec` を名指しているため（`SoftwareDecoderRuntime`。CI はランタイムを差し替えず MSYS2 のまま使う）。**この検査は matrix の全ジョブに掛かる**ので、形態ごとに検査が抜ける枝は無い。
3. **WER LocalDumps を武装する**（DumpFolder を `runner.temp` 配下へ明示、DumpType=1 のミニダンプ）。書けたことを読み戻して `wer-status.log` に残す ── 武装できていない run の「ダンプが無い」を「クラッシュではない」と誤読しないため。AOT ではマネージドのスタックトレースが出ないので、ダンプが落ちた場所を知る唯一の手段になる。
4. **`tools/Run-E2E.ps1 -Shard <shard> -ExcludeFragile -NoBuild -PublishDir <flavor のパス>`。** ここで初めて「録画が実際にできること」「GUI が実際に操作できること」が検証される。**シャードのフィルタを YAML に書かない** ── 定義の唯一の出所は `tools/Run-E2E.ps1` で、ワークフローが渡すのは名前だけである。`--filter` は 1 件も選ばなくても `dotnet test` が成功で終わるので、**空振りはスクリプトが「合計 0 件は失敗」で落とす**。`-ExcludeFragile` が外すのは `TrayMenuTests` だけ ── 通知領域のオーバーフローを物理的なマウスカーソルで操作するテストで、不安定さの原因がシェル側にあるため、赤くなっても製品の退行を意味しない。CI ランナーには GPU が無い（WARP）ので、フィクスチャは `Type=System` + `videotestsrc` + `x264enc` を明示設定して起動する ── これはエンコーダーの自動フォールバックが効いていることの実証にもなる。このステップは `TMP` / `TEMP` を `runner.temp` に固定し `PROCESSRECORDERAPP_E2E_KEEP` を立てる ── 既定の一時ディレクトリはランナーによって `runner.temp` と別の場所になり、そのままだと失敗時の成果物収集が空振りする。E2E プロジェクトは他プロジェクトを参照しないので、テストアセンブリは `build-and-test` のビルドの副産物として出来ており、このジョブは `-NoBuild` で**その dll を直接** VSTest に渡す（`obj/` は要らない。埋め込みの `RepositoryRoot` が同じパスであることに依存する点は上記）。
5. **成果物は形態とシャードで名前を分ける**（`test-results-<flavor>-<shard>` ほか）。matrix の 6 ジョブが同じ名前で上げると衝突する。スクリプトは子プロセスの標準出力を `tests/ProcessRecorderApp.E2E/TestResults/e2e-<shard>.log` へ落とすので、**赤い回はこのファイルを見る**（子の console 出力はステップのログに出ない。要約表だけがステップに出る）。
6. **`fail-fast: false`。** 1 つのシャードが赤でも残りの標本を取る ── このスイートは同じコードで失敗数が揺れるため、他のシャードの結果に独立した価値がある。

**AOT 固有の破損（リフレクション欠落）は発行時ではなく実行時に出る** ── PropertyGrid のプロパティ列挙と設定 JSON のソース生成が危険域で、L1 では検出できない。だから matrix には `aot` の形態が要る（`selfcontained` だけでは、配るものが検証されない）。

**NuGet の復元に認証は要らない。** 取得元はルートの `nuget.config` が nuget.org 1 つに固定しており（`<clear />` でマシン/ユーザー設定のソースを遮断）、`UiaTrigger.*` も nuget.org から取る。`permissions` はどちらのワークフローも必要なものだけを明示する ── `build.yml` は `contents: read`、`release.yml` は Release へ添付するための `contents: write`。permissions を書いた時点で未記載スコープは none になるので、増やすときは明示すること。

**ブラウザ E2E（`WebUiBrowserTests`）は `windows-latest` に Edge が同梱されている前提で走る**（`%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe`）。**入っていない環境では Skip する**ので、緑は「走った」の根拠にならない ── skip 件数を見ること。

## release.yml の構成と理由

トリガーは `v*` タグの push と `workflow_dispatch`。`build.yml` と分けるのは、同梱用の GStreamer ランタイムを毎回取得するため push のたびに走らせる価値が無いから。**発行は Native AOT（`win-x64-aot`。`build.yml` の AOT ジョブと同じ形態）で、配布するのは AOT 版のみ** ── selfcontained(ReadyToRun) は CI の検証用で配布しない。**3つの zip** を作る（同梱ランタイムは MinGW 版と MSVC 版の2形態あり、どちらも配る）:

- 非同梱（`ProcessRecorderApp-<tag>-win-x64.zip`）── 利用者側に GStreamer(MinGW/MSVC) か MSYS2(UCRT64) が要る。軽い。
- 同梱 MinGW（`ProcessRecorderApp-<tag>-win-x64-gstreamer-mingw.zip`）── 削減済み runtimes（46 ファイル・49.9MB）を同梱する。**自己完結**（libstdc++ / libgcc / libwinpthread も入る）。
- 同梱 MSVC（`ProcessRecorderApp-<tag>-win-x64-gstreamer-msvc.zip`）── 同じ選択の MSVC ビルド（44 ファイル・24.6MB）。小さく、`capture-api`（WGC）が使えるが、**利用者の機械に VC++ 再頒布可能パッケージが要る**（`msvcp140` / `vcruntime140` / `vcruntime140_1` は同梱しない）。

同梱版はどちらも x264 と libav を含まないので GPL を持ち込まず、openh264 も特許の都合で含まない。同梱構成の `Type=System` は `mfh264enc` に落ちる。

**このジョブは MSYS2 を入れない。** そのため同梱物がランタイム解決の唯一の当たりになり、`gst.runtime selected=BundledRuntime` を実際に踏める唯一の場所である ── 開発機や `build.yml` では、解決順（元の `PATH` → 環境変数 → レジストリ → 既定の導入先 → MSYS2 → 同梱物）の都合で同梱物が必ず負けるため、ここでしか検証できない。流すのはエンコーダーに依存しないスモークだけ ── フィルタ `FullyQualifiedName~SmokeTests|FullyQualifiedName~RuntimeResolutionTests` は部分一致なので、実際に走るのは `SmokeTests`・`GuiSmokeTests`・`RuntimeResolutionTests` の 3 クラス。録画系 E2E が流れないのは、ハーネスが `SettingsFile.DefaultEncoder = "x264enc"` を固定しており同梱物に x264 が無いため（詳細は docs/coverage-gaps.md）。

検証の要点:

- 同梱ランタイムは**その形態の台帳**（`licenses/third-party/COMPONENTS.tsv` / `COMPONENTS-msvc.tsv`）と**過不足なく一致**することを見る。件数の下限では「多い分」を捕まえられず、ライセンス文の無いファイルが黙って混ざる。中身は `tools/Verify-BundledPublish.ps1` にあり、**形態ごとに1回ずつ**呼ぶ ── YAML に書き写すと必ず片方が古くなる。
- **MSVC 版の「VC++ 再頒布可能パッケージが要る」という前提は、ここでは踏めない。** `windows-latest` には Visual Studio が入っているので CRT は必ず在り、緑は「入っていなくても動く」の根拠にならない。
- **スモークが実際にテストを選べたことを、直後のステップが `release-smoke-<形態>.trx` で確認する**（`tools/Assert-SmokeSelection.ps1`。形態ごとに1回）。 `--filter` は1件も選ばなくても `dotnet test` が成功で終わるので、これが無いとクラスの改名・移動で**緑のまま無検証の zip を配る**。見るのは件数の下限ではなく「上の3クラスが結果に出ていること」── 下限では「1クラスだけ消えた」を捕まえられない。フィルタを変えるときは、このステップの期待クラス一覧も一緒に直すこと。
- ライセンス文はリポジトリのものとハッシュ一致まで確認する。**配布物にライセンス文が入っていることを見る唯一の場所**である（L1 の `ThirdPartyLicenseTests` が見られるのはリポジトリ内の整合だけ）。
- 非同梱側は `runtimes/` と `licenses/third-party/` が**入っていないこと**を確認する（「入っていないのが正しい」側の検証）。
- 非同梱の発行には `BundleGStreamerRuntime=false` を明示する。既定は「`runtimes/` に本体 DLL があれば同梱」なので、取得ステップとの順序が入れ替わると黙って同梱版になる。
- zip 名に使う `ref_name` は `/` と `\` を `-` に置換する。`workflow_dispatch` でブランチから流すと `feature/xxx` のような値になり、そのままでは `Compress-Archive` が存在しないディレクトリを指して落ちる。

## リリースの流し方（v* タグ）

**ドラフトで作り、中身を確かめてから公開する。** 公開してしまうと取り消す手段は削除しかなく、
それは公開済みの参照（Release とタグ）を巻き戻すことになる。ドラフトのうちは
捨てても外から見えた痕跡が残らない。

1. **先にドラフトの箱を作る**:
   `gh release create <tag> --target <フル SHA> --draft --prerelease --title "…" --notes-file <path>`
   - `--target` に短縮 SHA を渡すと `HTTP 422 Release.target_commitish is invalid` になる ──
     **フル SHA を使うこと**。
   - この手順が要るのは、ワークフロー最後の `gh release upload <tag>` が
     **既存の Release を要求する**ためである。
   - **`--draft` ではタグが作られない。** 作られるのは `untagged-…` の URL を持つ箱だけで、
     したがって**この時点では `release.yml` は走らない**。次の手順でタグを push して初めて
     両者が結び付き、ワークフローが動く。
   - 0.x のあいだは `--prerelease` も付ける（v0.1.0 以降そう扱っている）。
2. **タグを push する**: `git tag <tag> <フル SHA>` してから `git push origin <tag>`
   （`&&` で繋がない ── Windows PowerShell 5.1 では構文エラーになる）。これで `release.yml` が走り、出来上がった zip が 1. のドラフトへ添付される
   （`--clobber` なので流し直しても上書きされる）。タグはどのブランチのコミットに打ってもよい。
3. `workflow_dispatch` でも流せるが、Release への添付ステップは `refs/tags/v*` のときだけ
   実行される。dispatch 実行では zip はワークフローのアーティファクト（`packages`）としてだけ取れる。
4. **ランナーの自己申告（ステップの success）だけで済ませず**、`gh release download` で
   出来上がった zip を落として中身を数え直すこと ── **3 本とも**上がっているか、
   runtimes の件数がその形態の台帳（`COMPONENTS.tsv` / `COMPONENTS-msvc.tsv`）と一致するか、
   ライセンス文がリポジトリと SHA256 一致で入っているか、openh264 を含むファイルが 0 件か、
   同梱される exe の版がそのコミットを指しているか。**ここまでドラフトのままなので、
   食い違いが見つかったら公開せずに捨てられる。**
5. 確かめ終えてから公開する: `gh release edit <tag> --draft=false`

**切り直し（まだ公開していない場合）**は、ドラフトとタグを消してから 1. からやり直す:

```
gh release delete <tag> --yes
git push origin :refs/tags/<tag>
git tag -d <tag>
```

**公開してしまった後の切り直しは別物である。** 同じ手順で消せはするが、
消えるのは公開済みの Release とタグであって、取得した人の手元は戻らない。
版を上げて出し直す方が筋がよい ── 同じ版で中身が変わることになるためである。

## 運用上の注意

- **cancel-in-progress**: `build.yml` は `concurrency` で同一 ref の実行を1つに絞り、続けて push すると前の run が打ち切られる。前の run がキャンセルで終わるのは意図した動作であり、異常ではない。
- **アクションは Node 24 で走るメジャーに固定してある**（`actions/checkout@v7` / `actions/setup-dotnet@v6` / `actions/upload-artifact@v7` / `actions/download-artifact@v8`）。Node 20 のままだとランナーが強制的に Node 24 で走らせたうえで run ごとに警告注釈を出す。`upload-artifact` は **v6 以上でないと消えない** ── v5 は Node 24 に対応しただけで既定は Node 20 のままである。いずれも Runner 2.327.1 以上が要るので、self-hosted へ移すときはランナーの版を先に上げること。`msys2/setup-msys2@v2` は警告の対象外（すでに Node 24）。
- **ジョブ単位で conclusion を見ること。** run 単位の `success` は `continue-on-error` のジョブの失敗を隠す。現在ゲート外のジョブは無いが、確認の習慣として run の色ではなくジョブの色を見る。
- **E2E の打ち切りやタイミング依存の分岐で、ランナーだけで赤が続けて再現したら、それ以上ランナー上での再試行を重ねないこと。** その分岐は純粋関数へ切り出して L1 で守る ── ランナー上の再試行は標本1つに数十分かかり、しかも環境要因と製品の欠陥を区別できない。
- **下限の表明と打ち切りを区別する。** `StopSynchronicityTests` の生成サイズ 20MB は下限の表明なので、ランナーで届かなくても緩めず、録画時間かビットレートを上げて調整する（届かないと退行を検出できないテストになる）。較正の目安: 録画条件は 1280x720/30fps/20Mbit・20 秒で、開発機では 52〜55MB（下限の約 2.5 倍）出る ── ランナーの赤が退行か単なる能力不足かは、この余裕からの落ち幅で判断する。一方 `ShutdownTests` の `ExitBudget`（420 秒）は打ち切りなので、届かないなら緩めてよい ── 打ち切りは「テスト結果」ではなく「何も分からない」。ただし緩めてよいのはランナーの遅さが原因の場合だけで、切り分けは `activity.log` に `recording.stop` が出ているかで行う（出ていれば停止経路は動いていて遅いだけ、出ていなければ製品のハングを疑う）。対象の `CtrlClose_WhileRecording_FinalizesEveryFile` は録画しながら GUI を操作する唯一のケースのため、ソースは `AsBulkyButCheapToEncode`（640x360/15fps・約 20Mbit の `snow`）でバイト数＝検出力を据え置いたまま画素数だけ落としてある ── 録画時間・ビットレートとは別の、負荷だけを下げる第三の調整手段でもある。
- **クラッシュダンプのアーティファクトだけは `always()` で上げる。** ワーカーはテストを緑にしたまま死にうる（ハーネスがリトライで拾う）ため、緑の回の「ARMED かつダンプ0件」を見て初めてクラッシュ無しと言える。ダンプは2系統あり、WER LocalDumps は AOT でも効くが、`DOTNET_DbgEnableMiniDump` 由来の `.dmp` は CoreCLR の機能なので AOT 発行物では出ない。
- **失敗時診断の収集網は拡張子で決まる。** `build.yml` の `e2e` ジョブは `*.log` / `*.log.1` / `*.json` を拾うが、`release.yml` のスモーク診断が拾うのは `*.log` / `*.json`（と `TestResults/*.trx`）だけで、ローテート済みの `*.log.1` はそこでだけ黙って落ちる。新しい診断ファイルは `.log` か `.json` にすること。`.txt` にすると黙ってアップロードされず、無いことに気付けない。
- **`Activate_ShowsTheWindowWithoutFaulting` は環境起因で赤くなりやすい。** 対話セッション・デスクトップと WARP でのプレビュー初期化に依存する最初のテストで、赤でも製品の不具合とは限らない ── L3 全般と同じく、まずランナー側の制約を疑う。
- **`LanguageMatrixTests` はどの行が実質的な検査かがランナーの表示言語で入れ替わる。** ランナー（en-US）で `ja-JP` の行が落ちたら、それはランナーの制約ではなく製品の欠陥（ja-JP のリソースが発行物に載っていない）なので、環境起因の赤とは切り分けて扱う。
