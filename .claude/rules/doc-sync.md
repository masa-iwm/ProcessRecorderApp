# 文書とコードの同期テスト一覧

文書だけ・コードだけを直すと L1/L4 が赤になる組み合わせ。**必ず対で変更する**こと。

| テスト | 縛っているもの |
|---|---|
| `DocumentationDriftTests` | `src/README.md`「### 終了コードの一覧」⇔ `ExitCode_*` 定数（**双方向**: 行を消しても定数を消しても落ちる）／ルート README 言語対のコマンド表 ⇔ コマンド登録（双方向）／`README.md` と `README.ja.md` の見出しの深さの並び（SequenceEqual）／相互リンク `(README.ja.md)` / `(README.md)` の存在 |
| `StopFinalizeBudgetTests` | 停止の排出待ちの案内（resw の `PropDesc_StopFinalizeTimeout` en/ja と `src/README.md`）⇔ `MaxAdvisedStopFinalizeTimeoutMs` |
| `LauncherBudgetTests` | `LauncherMutexTimeoutMs` ⇔ その根拠の算術（受理 `WorkerAcceptTimeoutMs` ＋ コールドスタート `ColdStartRegistrationTimeoutMs` ＋ 転送 `RedirectTimeoutMs` ＋ 結果待ち）。上限が最悪保持の**2 本分は吸収し 3 本分は吸収しない**ことを両側から縛る（構成要素も上限も、どちらへ動かしても落ちる）。**文書の数値そのものは縛っていない** ── doc コメントのリテラルは対で直すこと |
| `CleanupIntervalBudgetTests` | `RecordingCleanupScheduler.MaximumIntervalHours` ⇔ resw の `PropDesc_RecordingCleanupIntervalHours`（en/ja）と `src/README.md` の設定表／`Task.Delay` の上限（約 1,193 時間）を超えないこと |
| `RecorderSettingsMirrorTests` | `EventRecorderSettings` の各プロパティ ⇔ 4 箇所の手書きミラー（`EventRecorder` の switch と ctor、`GstEventRecorderViewModel` の switch と ctor）／`AppSettings` のコレクション差し替え時の要素購読 |
| `CampbellPaletteTests` | `CampbellPalette` の選択色（XAML 形式 ⇔ CSS 形式）／`terminal.js` に色のリテラルを持たせない |
| `EncoderCatalogScriptSyncTests` | `EncoderCatalog` ⇔ `tools/Verify-GpuEncoders.ps1` のエンコーダー行 |
| `RuntimeClosureSeedSyncTests` | 閉包の種リスト ⇔ `tools/Get-GStreamerImportClosure.ps1` |
| `GpuVerifyScriptParsingTests` | `tools/Verify-GpuEncoders.ps1` 内の正規表現（スクリプトから取り出して .NET で実行するので、規則が2か所に書かれない） |
| `ThirdPartyLicenseTests` | `THIRD-PARTY-NOTICES.md` ⇔ `licenses/third-party/`（取得元・版・SHA256 の正本は `SOURCES.tsv` 1つ） |
| `SettingsSchemaTests`（L2） | `docs/settings.schema.json` ⇔ アプリが settings.json の隣へ書く `settings.schema.json`（**発行物を起動して実際に書かせたものと突き合わせる**）／同じ保存で書かれた settings.json のキー集合 ⇔ スキーマの `properties`（過不足なく一致）／settings.json の `$schema` が相対参照であること。**L1 では検証できない**（`AppSettings` と `AppSettingsJsonContext` は WinUI アプリプロジェクト側）。機構そのものは L1 の `JsonSettingsBaseTests` |
| `TerminalAssetPinTests` | `src/ProcessRecorderApp/Assets/Terminal/SOURCES.md` ⇔ 同 `vendor/` の実ファイル（**双方向**・SHA256）／版表記 ⇔ `THIRD-PARTY-NOTICES.md`／`.gitattributes` の `vendor/** -text` の存在。**xterm.js は `licenses/third-party/` の台帳には載せられない**（同梱・非同梱の両方に入るため。理由は `SOURCES.md`） |

`tools/` のスクリプトは参考資料ではなく**テスト対象**である。

## 移動・改名できないファイル

- `README.md` / `README.ja.md` / `src/README.md` ── テストが `RepositoryFiles.At` で
  パスを直接読む。
- `THIRD-PARTY-NOTICES.md` / `license.txt` ── csproj が発行物へ同梱し、
  `release.yml` がファイル名を名指しで検査する。
- `licenses/third-party/SOURCES.tsv` / `COMPONENTS.tsv` ── スクリプト・テスト・
  `release.yml` が読む正本。
- `docs/settings.schema.json` ── L2 の `SettingsSchemaTests` がこのパスを直接読む。
  更新はアプリを一度起動して設定を保存し、書かれた `settings.schema.json` で上書きする
  （手で編集しない ── 正本は `AppSettings` の型そのもの）。
- `.gitattributes` ── 消すとチェックアウトで LF→CRLF 変換が起こり、
  **手元だけ緑・CI だけ赤**になる（`core.autocrlf` が true の環境があるため）。
