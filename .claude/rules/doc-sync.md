# 文書とコードの同期テスト一覧

文書だけ・コードだけを直すと L1/L4 が赤になる組み合わせ。**必ず対で変更する**こと。

| テスト | 縛っているもの |
|---|---|
| `DocumentationDriftTests` | `src/README.md`「### 終了コードの一覧」⇔ `ExitCode_*` 定数（**双方向**: 行を消しても定数を消しても落ちる）／ルート README 言語対のコマンド表 ⇔ コマンド登録（双方向）／`README.md` と `README.ja.md` の見出しの深さの並び（SequenceEqual）／相互リンク `(README.ja.md)` / `(README.md)` の存在 |
| `StopFinalizeBudgetTests` | 停止の排出待ちの案内（resw の `PropDesc_StopFinalizeTimeout` en/ja と `src/README.md`）⇔ `MaxAdvisedStopFinalizeTimeoutMs` |
| `EncoderCatalogScriptSyncTests` | `EncoderCatalog` ⇔ `tools/Verify-GpuEncoders.ps1` のエンコーダー行 |
| `RuntimeClosureSeedSyncTests` | 閉包の種リスト ⇔ `tools/Get-GStreamerImportClosure.ps1` |
| `GpuVerifyScriptParsingTests` | `tools/Verify-GpuEncoders.ps1` 内の正規表現（スクリプトから取り出して .NET で実行するので、規則が2か所に書かれない） |
| `ThirdPartyLicenseTests` | `THIRD-PARTY-NOTICES.md` ⇔ `licenses/third-party/`（取得元・版・SHA256 の正本は `SOURCES.tsv` 1つ） |

`tools/` のスクリプトは参考資料ではなく**テスト対象**である。

## 移動・改名できないファイル

- `README.md` / `README.ja.md` / `src/README.md` ── テストが `RepositoryFiles.At` で
  パスを直接読む。
- `THIRD-PARTY-NOTICES.md` / `license.txt` ── csproj が発行物へ同梱し、
  `release.yml` がファイル名を名指しで検査する。
- `licenses/third-party/SOURCES.tsv` / `COMPONENTS.tsv` ── スクリプト・テスト・
  `release.yml` が読む正本。
- `.gitattributes` ── 消すとチェックアウトで LF→CRLF 変換が起こり、
  **手元だけ緑・CI だけ赤**になる（`core.autocrlf` が true の環境があるため）。
