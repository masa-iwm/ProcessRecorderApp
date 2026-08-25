---
paths:
  - "README*.md"
  - "src/README.md"
  - "docs/**"
  - "tests/**"
  - "tools/**"
  - "licenses/**"
  - "THIRD-PARTY-NOTICES.md"
  - "**/*.resw"
  - "src/ProcessRecorderApp/Assets/Terminal/**"
---

# 文書とコードの同期テスト一覧

文書だけ・コードだけを直すと L1/L4 が赤になる組み合わせ。**必ず対で変更する**こと。

| テスト | 縛っているもの |
|---|---|
| `DocumentationDriftTests` | `src/README.md`「### 終了コードの一覧」⇔ `ExitCode_*` 定数（**双方向**: 行を消しても定数を消しても落ちる）／ルート README 言語対のコマンド表 ⇔ コマンド登録（双方向）／`README.md` と `README.ja.md` の見出しの深さの並び（SequenceEqual）／相互リンク `(README.ja.md)` / `(README.md)` の存在 |
| `StopFinalizeBudgetTests` | 停止の排出待ちの案内（resw の `PropDesc_StopFinalizeTimeout` en/ja と `src/README.md`）⇔ `MaxAdvisedStopFinalizeTimeoutMs` |
| `LauncherBudgetTests` | `LauncherMutexTimeoutMs` ⇔ その根拠の算術（受理 `WorkerAcceptTimeoutMs` ＋ コールドスタート `ColdStartRegistrationTimeoutMs` ＋ 転送 `RedirectTimeoutMs` ＋ 結果待ち）。上限が最悪保持の**2 本分は吸収し 3 本分は吸収しない**ことを両側から縛る（構成要素も上限も、どちらへ動かしても落ちる）。**文書の数値そのものは縛っていない** ── doc コメントのリテラルは対で直すこと |
| `CleanupIntervalBudgetTests` | `RecordingCleanupScheduler.MaximumIntervalHours` ⇔ resw の `PropDesc_RecordingCleanupIntervalHours`（en/ja）と `src/README.md` の設定表／`Task.Delay` の上限（約 1,193 時間）を超えないこと |
| `RecorderSettingsMirrorTests` | `EventRecorderSettings` の各プロパティ ⇔ 4 箇所の手書きミラー（`EventRecorder` の switch と ctor、`GstEventRecorderViewModel` の switch と ctor）／`AppSettings` のコレクション差し替え時の要素購読。**縛るのは「名前が本文に出るか」だけ**（語境界つきの出現検査）なので `case nameof(X): break;` でも緑になる ── 写し方の正しさは L3 のプロパティ往復で見ること。**5 箇所目の手書き `tests/ProcessRecorderApp.E2E/SettingsFile.cs` の `RecorderSpec` / `ToJson` は縛っていない**（忘れると E2E が古い形の settings.json を書く） |
| `CampbellPaletteTests` | `CampbellPalette` の選択色（XAML 形式 ⇔ CSS 形式）／`terminal.js` に色のリテラルを持たせない |
| `ContinuousSegmentBudgetTests` | `EventRecorderSettings.MinContinuousSegmentSeconds` / `MaxContinuousSegmentSeconds` ⇔ resw の `PropDesc_Rec_ContinuousSegmentSeconds`（en/ja）と `src/README.md` の常時録画の設定表（3 面。定数・文言・文書のどれを動かしても落ちる） |
| `PreviewSettingBudgetTests` | プレビュー配信の 4 設定の `Min*` / `Max*` 定数 ⇔ resw の `PropDesc_Rec_Preview*`（en/ja）と `src/README.md` の設定表（3 面）。**照合は数字の境界つき**（素の部分一致だと `PreviewFps` の下限 `1` が `160` / `2160` に紛れて無検査になる） |
| `ContinuousRuntimeDependencyTests` | 常時録画が使う GStreamer 要素 ⇔ `licenses/third-party/COMPONENTS*.tsv`（**形態ごとの台帳を全部**見る。MSVC 版は `lib` 接頭辞が無いので名前を導いて照合する）。**`videorate` が同梱に在ること**（別 fps はこれが無いと使えない）と、「`ContinuousFramerate` が空なら `videorate` を出さない」ガード（`ContinuousBranch.RequiresVideorate` と `EventRecorder.ResolveContinuousEncoder`）の存在を固定する。**開発機と CI はフル構成の GStreamer なので、この対応が無いと同梱配布でだけ壊れる** |
| `DashPreviewPipelineTests` | `DashPreviewStream.BuildPipeline` の凍結トークン（`fragment-mode=dash-or-mss` / `config-interval=-1` / caps の並び）⇔ `FragmentDurationMs`／`EventRecorder` のソーステキスト（枝A のドレインで `_dash?.OnRawSample(sample);` が**ちょうど 1 回・`OnPreview(sample);` の直前**／`CloseCore` が quiesce → `live?.Close()` → `dash?.Close()` の順）／`DashPreviewStream.cs` に `_stateLock` が現れないこと・`_muxLock` を `lock` で取らないこと。**パイプラインを直したらこのテストも対で直す** |
| `DashRoutesTests` | `DashManifest.MediaTemplate` / `InitializationTemplate` ⇔ `DashRoutes.TryParse` が受ける名前（テンプレートを展開したものが必ず受理されること）。**片方だけ動かすと、クライアントが MPD どおりに要求したものが 404 になる** |
| `WebAssetManifestTests` | `Components.DashPreviewReasons.Starting` ⇔ `app.js` 内の同じリテラル（503 の本文と完全一致で比較しているので、片方を書き換えるとブラウザが開始直後に諦める）。併せて `WebAssets.Manifest` ⇔ ディスクの `wwwroot`（双方向）・第三者 JS ゼロ・`index.html` の参照先 |
| `EncoderCatalogScriptSyncTests` | `EncoderCatalog` ⇔ `tools/Verify-GpuEncoders.ps1` のエンコーダー行 |
| `RuntimeClosureSeedSyncTests` | 閉包の種リスト ⇔ `tools/Get-GStreamerImportClosure.ps1`／種 ⇔ **形態ごとの台帳**（MSVC 命名は `lib` を落として導く。スクリプト側の導出が消えると赤になる） |
| `GpuVerifyScriptParsingTests` | `tools/Verify-GpuEncoders.ps1` 内の正規表現（スクリプトから取り出して .NET で実行するので、規則が2か所に書かれない） |
| `ThirdPartyLicenseTests` | `THIRD-PARTY-NOTICES.md` ⇔ `licenses/third-party/`（取得元・版・SHA256 の正本は `SOURCES.tsv` 1つ）／**形態ごとの台帳** `COMPONENTS*.tsv` ⇔ `THIRD-PARTY-NOTICES.md` の内訳の表（`\| MinGW \| 46 \| 15 \| 17 \| 12 \|` の形の行を台帳から数え直して照合。列は 総数・プラグイン・GStreamer ライブラリ・その他）／ディスクの `COMPONENTS*.tsv` ⇔ テスト側の `Flavors`（過不足なく一致。増やして直し忘れるとその形態が無検査になる）／`release.yml` が**両方の台帳**と発行物を突き合わせていること |
| `SettingsSchemaTests`（L2） | `docs/settings.schema.json` ⇔ アプリが settings.json の隣へ書く `settings.schema.json`（**発行物を起動して実際に書かせたものと突き合わせる**）／同じ保存で書かれた settings.json のキー集合 ⇔ スキーマの `properties`（過不足なく一致）／settings.json の `$schema` が相対参照であること。**L1 では検証できない**（`AppSettings` と `AppSettingsJsonContext` は WinUI アプリプロジェクト側）。機構そのものは L1 の `JsonSettingsBaseTests` |
| `TerminalAssetPinTests` | `src/ProcessRecorderApp/Assets/Terminal/SOURCES.md` ⇔ 同 `vendor/` の実ファイル（**双方向**・SHA256）／版表記 ⇔ `THIRD-PARTY-NOTICES.md`／`.gitattributes` の `vendor/** -text` の存在。**xterm.js は `licenses/third-party/` の台帳には載せられない**（同梱・非同梱の両方に入るため。理由は `SOURCES.md`） |

`tools/` のスクリプトは参考資料ではなく**テスト対象**である。

## 移動・改名できないファイル

- `README.md` / `README.ja.md` / `src/README.md` ── テストが `RepositoryFiles.At` で
  パスを直接読む。
- `THIRD-PARTY-NOTICES.md` / `license.txt` ── csproj が発行物へ同梱し、
  `release.yml` がファイル名を名指しで検査する。
- `licenses/third-party/SOURCES.tsv` / `COMPONENTS.tsv` / `COMPONENTS-msvc.tsv` ── スクリプト・テスト・
  `release.yml` が読む正本。
- `docs/settings.schema.json` ── L2 の `SettingsSchemaTests` がこのパスを直接読む。
  更新はアプリを一度起動して設定を保存し、書かれた `settings.schema.json` で上書きする
  （手で編集しない ── 正本は `AppSettings` の型そのもの）。
- `.gitattributes` ── 消すとチェックアウトで LF→CRLF 変換が起こり、
  **手元だけ緑・CI だけ赤**になる（`core.autocrlf` が true の環境があるため）。
