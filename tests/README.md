# テスト

本リポジトリのテストは 4 層構成。CLI が一級の自動化面として存在するため、
README 記載の契約の大半は UI 自動化(UIA)を使わずに検証できる ── その方が桁違いに安定する。

| 層 | プロジェクト | 手段 | 対象 |
|---|---|---|---|
| L1 単体 | `ProcessRecorderApp.Tests` | xunit.v3（インプロセス） | 純ロジック（テンプレート展開・パイプライン組立・エンコーダー選択・トークナイザ・設定）と、実行では守れない不変条件のソース静的検査 |
| L2 E2E | `ProcessRecorderApp.E2E` | 発行済み exe を起動し CLI + 生成MP4 + activity.log を検証 | 録画の中核契約・終了コード・変数・永続化・常駐/多重起動 |
| L3 GUI | `ProcessRecorderApp.E2E` | FlaUI (UIA3) | GUI でしか触れない操作：プロパティ編集・画面切替・トレイ格納・正常終了パス・レコーダー管理・パイプライン編集ダイアログ・**表示言語の強制** |
| L4 ローカライズ | `ProcessRecorderApp.Tests` | `.resw` / README をファイルとして直接検証 ＋ L3 で言語強制 | en-US / ja-JP のキー整合・書式整合・参照キー実在・README 言語対・フォールバック |
| L2 ブラウザ | `ProcessRecorderApp.E2E` | headless Edge ＋ DevTools プロトコル（自前。`msedge` 不在なら Skip） | Web UI（`app.js`）でしか触れないもの：ログイン画面への切り替え・ゲストの取り消し・MSE の追いかけ再生 |

> **この文書や docs/ でコードを指すときは、行番号ではなく型名・メソッド名で書くこと。**
> 行番号は次の編集で腐るうえ、**腐ったことが目に見えない**。
> 危険なのは「存在しない行」ではなく「実在する別の行」を指す場合で、
> 読み手は間違いに気付けない。

## 実行

```powershell
dotnet build src/ProcessRecorderApp.slnx -c Release          # 警告なしで通ること
dotnet test  tests/ProcessRecorderApp.Tests -c Release       # L1 + L4(静的)
dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-selfcontained
dotnet test  tests/ProcessRecorderApp.E2E   -c Release       # L2 + L3（発行物に対して実行する）
```

> **GStreamer はリポジトリに同梱していない。** L2/L3 を回すには実機に
> **GStreamer(MinGW / MSVC)** か **MSYS2(UCRT64)** が入っている必要がある ──
> 起動時に探すのは**アプリではなくバインディング**（GstSharp.Net のローダー。
> 段の一覧は src/README.md の「GStreamer の解決経路」）。
> どこから読まれたかは `activity.log` の `gst.runtime` に出る。
> **段の組み立てそのもの（純粋関数としての優先順位）を検査する L1 はこのリポジトリには無い**
> ── バインディング側の `NativeInstallPlannerTests` が担う。こちらが見るのは
> 「この実機で実際にどこからロードされたか」だけ（L2 の `RuntimeResolutionTests`）。
> 同梱版を作るときだけ `tools/Fetch-GStreamerRuntime.ps1` で展開してから
> `-p:BundleGStreamerRuntime=true` で発行する。
>
> **同梱物に対しては L2 の大半を流せない。** ハーネスが
> `SettingsFile.DefaultEncoder = "x264enc"` を固定しており（尺とサイズを比較可能に
> 保つための意図的な設計）、同梱ランタイムに x264 は無い（GPL を持ち込まないため）。
> 通るのはエンコーダーに依存しないスモークだけ ── 詳細は
> [docs/coverage-gaps.md](../docs/coverage-gaps.md) の「同梱ランタイムに対する
> 録画系 E2E の大半」。

L2 は発行物を外から叩くので、**先に publish が要る**（発行物が無ければフィクスチャが
その旨を出して即座に失敗する）。**製品コード・`wwwroot`・`AppSettings`・リモート API を
触ったら、L2 の前に必ず
`dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-selfcontained`
を回すこと** ── `PublishedApp` は**既存の発行物を読むだけで再発行しない**ので、
忘れると**古いバイナリに対して緑になる**（変更を入れたのにテストが通った、という
最も気付きにくい形になる）。AOT 発行物に対して流すときは
`PROCESSRECORDERAPP_E2E_PUBLISH_DIR` で発行ディレクトリを差し替える。
デバッグ時は `PROCESSRECORDERAPP_E2E_KEEP=1` で一時ディレクトリを残せる。

ブラウザ E2E（`WebUiBrowserTests`）も同じプロジェクトに入っている。**ブラウザ自動化の
パッケージは足していない** ── BCL の `ClientWebSocket` とシステムの `msedge.exe` だけで
DevTools プロトコルを話す（`EdgeCdp`）。プロファイルは 1 起動につき 1 つの一時ディレクトリなので、
「古い `app.js` がキャッシュに残っていた」という結末が起こりえない。**Edge が入っていない環境では
Skip する**ので、緑だから走ったとは限らない ── 実行結果の skip 件数を見ること。

プレビュー配信も同じプロジェクトが見る。`PreviewStreamTests` は録画済みの H.264 をそのまま包む
fMP4 の配信（`GET /api/recorders/{id}/preview.mp4`）を、発行物へ本物の HTTP で繋いで
検分する ── init が先に来ること・購読者の上限・配信しても録画が変わらないこと。

`DashPreviewTests` は DASH 配信（`GET /api/recorders/{id}/dash/{file}`）を同じやり方で見る
── 開始直後の 503 `starting` が 200 に変わること、manifest と init と全セグメントが配られること、
リングが有界でリースが切れれば畳まれること、配信中に設定を変えると新しい generation になること、
配信しても録画が無傷であること、知らない相手とゲストが 404 / 401 になること。**ここが第 2
パイプラインを初めて実際に走らせる層である**（L1 が縛るのはパイプライン文字列と純関数だけ）。

L3（GUI・UIA）は同じプロジェクトに入っている。**対話セッションとデスクトップが要る**
（切断中の RDP セッションでも UIA から要素を辿れることは確認済み。ただし
`Category=Fragile` は物理カーソルを使うため対話セッションでしか回せない）。

**CI が回すのは `Category=Fragile` を除いた部分集合:**

```powershell
dotnet test tests/ProcessRecorderApp.E2E -c Release --filter "Category!=Fragile"
```

`Category=Fragile` は `TrayMenuTests` の 4 件だけ。**除外の理由は製品ではなくシェル側にある**
（通知領域のオーバーフローを開き、**物理的なマウスカーソルを動かして**右クリックする）。
手元では**フィルタなしで回す** ── ただしカーソルが取られるので、他の作業をしながら回さないこと。

> **フィルタが空振りしていないことを必ず確かめること。** トレイト式が1件も選ばなくても
> `dotnet test` は成功で終わるので、**CI のゲートが丸ごと no-op になっても緑になる**。
> フィルタを書いたら、選ばれた件数が期待どおりかを実行結果の合計件数で確認する。

## 詳細

設計原則と運用の詳細は docs/ にある:

| 文書 | 内容 |
|---|---|
| [docs/test-harness.md](../docs/test-harness.md) | L2/L3 基盤の外せない設計・待ち方の規則・テストの有効性検証の原則（注入→検出→revert） |
| [docs/coverage-gaps.md](../docs/coverage-gaps.md) | **自動では守られていないもの。** ここに載っている箇所を触る前に必ず読む |
| [docs/ci.md](../docs/ci.md) | CI の段の順序と理由・リリースの流し方・ランナーで赤くなりやすいものの切り分け |
| [docs/gpu-verification.md](../docs/gpu-verification.md) | GPU 実機検証（`tools/Verify-GpuEncoders.ps1` / `Verify-HighResolution.ps1`）の手順とレポートの読み方 |
| [docs/environment-facts.md](../docs/environment-facts.md) | GStreamer・Windows・PowerShell の環境的事実（テストの形を決めている制約） |
