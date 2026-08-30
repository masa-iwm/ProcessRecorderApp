# テスト

本リポジトリのテストは 4 層構成。CLI が一級の自動化面として存在するため、
README 記載の契約の大半は UI 自動化(UIA)を使わずに検証できる ── その方が桁違いに安定する。

| 層 | プロジェクト | 手段 | 対象 |
|---|---|---|---|
| L1 単体 | `ProcessRecorderApp.Tests` | xunit.v3（インプロセス） | 純ロジック（テンプレート展開・パイプライン組立・エンコーダー選択・トークナイザ・設定）と、実行では守れない不変条件のソース静的検査 |
| L2 E2E | `ProcessRecorderApp.E2E` | 発行済み exe を起動し CLI + 生成MP4 + activity.log を検証 | 録画の中核契約・終了コード・変数・永続化・常駐/多重起動 |
| L3 GUI | `ProcessRecorderApp.E2E` | FlaUI (UIA3) | GUI でしか触れない操作：プロパティ編集・画面切替・トレイ格納・正常終了パス・レコーダー管理・パイプライン編集ダイアログ・**表示言語の強制** |
| L4 ローカライズ | `ProcessRecorderApp.Tests` | `.resw` / README をファイルとして直接検証 ＋ L3 で言語強制 | en-US / ja-JP のキー整合・書式整合・参照キー実在・README 言語対・フォールバック |
| L2 ブラウザ | `ProcessRecorderApp.E2E` | headless Edge ＋ DevTools プロトコル（自前。`msedge` 不在なら Skip） | Web UI（`wwwroot` 配下の JS 一式）でしか触れないもの：ログイン画面への切り替え・ゲストの取り消し・MSE の追いかけ再生・変換再生の画質切替 |

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
「古い JS（`wwwroot` 配下の一式）がキャッシュに残っていた」という結末が起こりえない。**Edge が入っていない環境では
Skip する**ので、緑だから走ったとは限らない ── 実行結果の skip 件数を見ること。

プレビュー配信も同じプロジェクトが見る。`PreviewStreamTests` は録画済みの H.264 をそのまま包む
fMP4 の配信（`GET /api/recorders/{id}/preview.mp4`）を、発行物へ本物の HTTP で繋いで
検分する ── init が先に来ること・購読者の上限・配信しても録画が変わらないこと。

`DashPreviewTests` は DASH 配信（`GET /api/recorders/{id}/dash/{file}`）を同じやり方で見る
── 開始直後の 503 `starting` が 200 に変わること、manifest と init と全セグメントが配られること、
リングが有界でリースが切れれば畳まれること、配信中に設定を変えると新しい generation になること、
配信しても録画が無傷であること、知らない相手とゲストが 404 / 401 になること。**ここが第 2
パイプラインを初めて実際に走らせる層である**（L1 が縛るのはパイプライン文字列と純関数だけ）。

`TranscodeTests` は録画トランスコードの**成立する側**を見る ── 変換された本文が
`ftyp`+`moov` に続く `moof`/`mdat` であること、要求した位置から始まること、高さがソースへ
丸められること、ライブ DASH と 1 つの補助エンコーダー枠を取り合うこと、同じ `session` での
シークが枠を引き継ぐこと。加えて**プリセットの fps がソースの実 fps を上回る本**が通ること
（ソース `15/1` で **sidecar を消したもの**、ソース `89/3` で sidecar 有りの 2 通り）と、
**常時録画のセグメント**（枝を 160×120・5fps にし、720p を要求したもの）の sidecar が枝の実体を持ち、
そのセグメントが変換できること。

> **前提: ソフトウェアの H.264 デコーダーを持つ GStreamer が要る。** 製品のデコーダー候補は
> ハードウェアだけなので、GPU の無い機械では `SoftwareDecoderRuntime` が
> ワーカーの `PATH` の**先頭**へ別のランタイムの `bin` を置き（ローダーは PATH の走査を
> 最優先で解決し、勝った `bin` のランタイムを丸ごと選ぶ）、
> `PROCESSRECORDERAPP_H264_DECODER` で要素名を名指す。
>
> - 開発機 ── 公式のフルインストール `%LOCALAPPDATA%\Programs\gstreamer\1.0\mingw_x86_64\bin`
>   が在ればそれを自動で使う。別の場所なら `PROCESSRECORDERAPP_E2E_GST_BIN` で指す
>   （**空文字を明示すると「そのまま」**＝実行環境の解決に任せる）。
> - CI ── MSYS2(UCRT64) の `gst-plugins-bad` に `libgstopenh264.dll` が入っているので、
>   `PROCESSRECORDERAPP_E2E_GST_BIN` は設定しない（そのままで `openh264dec` が在る）。
> - 名指しするデコーダーは既定 `openh264dec`。`PROCESSRECORDERAPP_E2E_H264_DECODER` で変えられる。
>
> **能力は断定する。** 変換が成立しなければ各ケースは冒頭で落ちる（黙って skip しない）
> ── 失敗の本文に `bin` と名指しと `gst.runtime` / `gst.decoders` / `transcode.start` の行が入る。
> **同梱ランタイムの上では変換の経路は依然として 1 行も走らない**（同梱物としての true 経路は
> `tools/Verify-Transcode.ps1`）。
> **GPU の在る機械で `openh264dec` が無ければ `TranscodeTests` の 6 件と
> `WebUiBrowserTests` の変換の 2 件は落ちる** ── その場合は
> `PROCESSRECORDERAPP_E2E_H264_DECODER` にその機械のデコーダー（`d3d11h264dec` など）を置く。

`WebUiBrowserTests` は同じ前提で**ブラウザ側**を見る 2 件を持つ ──
`TheRecordingQualityMenuTranscodesAndKeepsThePositionAcrossQualities`（画質メニューが変換再生へ
切り替わり、取り込んでいない位置へのシークがパイプラインを作り直し、元のままへ戻しても位置が
残ること、そして**要求が飛んでいる最中にページを離れても枠を返すこと**）と
`TheRecordingQualityMenuShowsBusyWhileAnotherSessionHoldsTheSlot`（他人が枠を握っているあいだ
項目が `(busy)` で無効になり、手放せば戻ること）。**変換を MSE へ流し込む形を実行するのは
この 2 件だけである。**

**上書きを置かないインスタンスでは false の経路しか走らない。** 変換にはハードウェア H.264
デコーダーが要り、同梱ランタイムにソフトウェアの H.264 デコーダーは無いので、開発機と CI では
`GET /api/capabilities` が `transcode:false` を返す。

> **GPU 機では次の 3 件が赤になる。** どれも「変換できない」という到達点そのものを断定して
> いるためで、それが意図である ── 「どちらでもよい」にすると、能力検出が壊れて常に true を
> 返すようになっても緑のままになる。
>
> - `RemoteControlTests.TheCapabilitiesReportNoTranscodeOnAMachineWithoutAHardwareDecoder`
>   ── `GET /api/capabilities` の `transcode` が **false であることを断定**する。
> - `RemoteControlTests.TheTranscodeEndpointValidatesItsQueryBeforeTheCapability`
>   ── 検査の順序を見るテストだが、**最後の 2 つの到達点が 404 `transcode unavailable` で
>   あることも断定**している（GPU 機ではここが 200 になる）。
> - `WebUiBrowserTests.TheRecordingPlayerOffersNoTranscodeWithoutAHardwareDecoder`
>   ── 画質メニューの holder が **hidden であることを断定**している（GPU 機では出る）。
>
> GPU 機ではこの 3 件が赤になることを見込んだうえで、true の経路は
> `tools/Verify-Transcode.ps1`（[docs/gpu-verification.md](../docs/gpu-verification.md)）で見る。

`RemoteAuxiliaryEncoderLimit`（補助エンコーダー枠の上限）を書く E2E は 2 件:
`RemoteControlTests.TheCapabilitiesReportNoTranscodeOnAMachineWithoutAHardwareDecoder`
（3 を書き、9 への PATCH が 8 に丸められること）と
`DashPreviewTests.TheLiveDashHoldsOneAuxiliaryEncoderPerRecorder`
（1 を書き、レコーダー 2 台で 2 台目が 409 `auxiliary encoder busy` になること）。
`TranscodeTests` は 6 件のうち 2 件が 1 を書く（変換とライブ DASH の取り合い・シークの引き継ぎ）。
`WebUiBrowserTests` の変換の 2 件も 1 を書く（離脱で枠が返ること・`(busy)` の表示）。

トランスコードまわりだけを回すときのフィルタと**実測の選択件数**
（`TranscodeTests` 6 件＋`RemoteControlTests` 42 件＋`DashPreviewTests` 7 件＝**55 件**・約 6 分）:

```powershell
dotnet test tests/ProcessRecorderApp.E2E -c Release `
  --filter "FullyQualifiedName~TranscodeTests|FullyQualifiedName~RemoteControlTests|FullyQualifiedName~DashPreviewTests"
```

リモート操作まわりだけを回すときのフィルタと**実測の選択件数**（この 4 クラスで 76 件・約 12 分）:

```powershell
dotnet test tests/ProcessRecorderApp.E2E -c Release `
  --filter "FullyQualifiedName~DashPreviewTests|FullyQualifiedName~WebUiBrowserTests|FullyQualifiedName~RemoteControlTests|FullyQualifiedName~SettingsSchemaTests"
```

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
