# CLAUDE.md

事前バッファ付きのイベント録画アプリ（WinUI 3 + GStreamer、Windows x64）。
発行は Native AOT が既定。製品の概要はルートの [README.md](README.md)、
実装の詳細は [src/README.md](src/README.md)。

## 主要コマンド

```powershell
dotnet build src/ProcessRecorderApp.slnx -c Release -warnaserror   # 0 警告 0 エラーを維持する
dotnet test tests/ProcessRecorderApp.Tests -c Release              # L1（ユニット）+ L4（静的検査）
dotnet test tests/ProcessRecorderApp.E2E -c Release                # L2/L3 E2E（発行物を対象に実行）
dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-aot
```

パッケージの取得元は nuget.org と GitHub Packages（masa-iwm）の 2 つで、
**GstSharpBundle\* の restore には認証が要る**（手元は
`$env:NuGetPackageSourceCredentials_github = "Username=masa-iwm;Password=$(gh auth token)"`、
CI は build.yml が GITHUB_TOKEN で組み立てる。src/README.md「パッケージの取得元」）。

テストの層構成・E2E の前提と運用は [tests/README.md](tests/README.md)、
CI の構成と理由は [docs/ci.md](docs/ci.md)。

## 文書とコードの同期（壊しやすい）

主要な文書は L1/L4 テストが機械的に検査している。README 群や `src/README.md` を触る前に
[.claude/rules/doc-sync.md](.claude/rules/doc-sync.md) を読むこと。特に:

- `src/README.md` の「### 終了コードの一覧」は見出し文言・表形式を変えない（双方向で検査）
- `README.md` と `README.ja.md` は見出しの深さの並びが完全一致・相互リンク必須・
  コマンド表は登録済みサブコマンドと過不足なく一致
- `THIRD-PARTY-NOTICES.md`・`license.txt` はパス・ファイル名を変えない（発行物へ同梱）

## 規則

- コメント・文書には「現在の制約と実測値」だけを書く。経緯・日付・実行記録は書かない。
- PowerShell スクリプトの規則は [.claude/rules/powershell.md](.claude/rules/powershell.md)。
- 同梱 GStreamer ランタイムの更新手順は [docs/runtime-update.md](docs/runtime-update.md)。
  ランタイムからファイルを削る前の確認は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) の「6.」。
- テストのフィルタ式は**選択件数を必ず確かめる** ── 1件も選ばなくても `dotnet test` は
  成功で終わる。

## docs/

| ファイル | 内容 |
|---|---|
| [docs/test-harness.md](docs/test-harness.md) | L2/L3 テスト基盤の外せない設計と、テストの有効性検証の原則 |
| [docs/coverage-gaps.md](docs/coverage-gaps.md) | 自動では守られていないもの（該当箇所を触る前に読む） |
| [docs/ci.md](docs/ci.md) | CI とリリースの構成・流し方 |
| [docs/gpu-verification.md](docs/gpu-verification.md) | GPU 実機検証の手順とレポートの読み方 |
| [docs/environment-facts.md](docs/environment-facts.md) | 環境と実装の背景事実 |
| [docs/runtime-update.md](docs/runtime-update.md) | 同梱ランタイムの更新手順 |
| [docs/settings.schema.json](docs/settings.schema.json) | settings.json の JSON Schema（生成物。手で編集しない） |
