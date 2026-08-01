# PowerShell スクリプトの規則

対象: `tools/*.ps1` と、このリポジトリで書くすべての PowerShell。

- **Windows PowerShell 5.1 で動くこと。** `&&` / `||` のパイプライン連鎖、三項演算子、
  `??` / `?.` は使えない。
- **BOM 無しの .ps1 は 5.1 では ANSI として読まれる。** スクリプト内のリテラルと出力は
  英語（ASCII）で書く。日本語を書くと 5.1 実行時に文字化けする。
- `ConvertFrom-Json` へ渡すテキストは UTF-8 を明示して読む
  （`[IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)`）。
- `Set-Content -Encoding utf8` は BOM を付ける（5.1）。BOM が混入すると困る出力
  （コミットメッセージ等）には使わない。
- `Start-Process -Wait` は使わない ── プロセスツリー全体を待つため、常駐プロセスを
  起動する用途では返ってこない。
- ネイティブ実行ファイルに対する `2>&1` は使わない ── 5.1 は stderr の各行を
  ErrorRecord に包み、終了コードが 0 でも `$?` が `$false` になる。
