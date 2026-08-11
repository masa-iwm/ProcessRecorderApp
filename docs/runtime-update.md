# 同梱 GStreamer ランタイムの更新手順

同梱ランタイムは、公式インストーラ（MinGW 64-bit / Runtime / **LGPL-only** 構成）で入れた
バイナリを、**アプリが実際に読み込む推移閉包だけ**へ絞ったもの。Release アセットとして
このリポジトリに置き、`tools/Fetch-GStreamerRuntime.ps1` と
`.github/workflows/release.yml` が取得する。

## 命名規約

- タグ: `gstreamer-runtime-v<GStreamer の版>`（例: `gstreamer-runtime-v1.28.6`）。
  同じ版のまま中身を差し替える場合は `-r2` のような枝番を付ける。
  **`v` 単独で始まる名前にしないこと** ── `v*` は `release.yml` のタグトリガーに一致する。
- アセット: `gstreamer-runtime-win-x64-v<版>.zip`

## 手順

1. 新しいインストーラ（Inno Setup 6）を **Runtime / LGPL-only 構成**で無人導入し、
   `tools/Get-GStreamerImportClosure.ps1` で閉包を再計算する。
   種（プラグイン一覧と名前で読むライブラリ）は製品コードと同期して
   おり、ずれると `RuntimeClosureSeedSyncTests`（L1）が落ちる。`objdump -p` が必要
   （公式 MinGW 版には付属しない。MSYS2 のものを使う）。

   ```powershell
   $parts = 'base_system_1_0','gstreamer_1_0_capture','gstreamer_1_0_codecs',
            'gstreamer_1_0_core','gstreamer_1_0_encoding','gstreamer_1_0_system'
   $components = (($parts | ForEach-Object { $_; $_ + '\runtime' }) -join ',')
   Start-Process .\gstreamer-1.0-mingw-x86_64-<版>.exe -ArgumentList '/VERYSILENT','/NORESTART',
       '/SUPPRESSMSGBOXES','/CURRENTUSER',"/DIR=$env:LOCALAPPDATA\Programs\gstreamer\1.0\mingw_x86_64",
       "/COMPONENTS=$components",'/TASKS=environment_variables,registry_install_dir'
   ```

   この選択で展開されるのは 349 ファイル・254MB（1.28.6 実測。`unins000.*` を除く）。
   `*_gpl` / `*_restricted` / `libav` / `base_crypto` を入れないのが LGPL-only の実体で、
   入っている構成は `HKCU:\...\Uninstall\*_is1` の `Inno Setup: Selected Components` で読める。
   アンインストーラも `unins000.exe /VERYSILENT` で無人化できるが、**どちらも途中で
   プロセスが入れ替わる**ので、終了コードではなく `unins000.exe` の有無で完了を見ること。

   > **この構成には `x264enc` が無い。** E2E ハーネスは
   > `SettingsFile.DefaultEncoder = "x264enc"` を固定しているので、作業が終わったら
   > 開発機は GPL を含む全構成へ戻すこと（同じ `/COMPONENTS` に `*_gpl` /
   > `*_restricted` / `libav` / `base_crypto` などを足す）。戻さないと L2 E2E の大半が落ちる。

2. ファイルを削る前の確認 4 点を通す ── `THIRD-PARTY-NOTICES.md`「6.」。
3. `licenses/third-party/COMPONENTS.tsv` を新しい一覧で差し替える。版が変わった場合は
   `SOURCES.tsv`・ライセンス全文・`THIRD-PARTY-NOTICES.md` の版表記も更新する
   （`ThirdPartyLicenseTests`（L1）が検査する）。
4. zip を作る ── ルートは `win-x64/`、**エントリ区切りは `/`**（PowerShell 5.1 の
   `ZipFile.CreateFromDirectory` は `\` で書き、規格違反なので読み替えが黙って外れる）。
   SHA256 を採取する。
5. Release を作ってアセットを上げる:
   `gh release create gstreamer-runtime-v<版> <zip> --target <フル SHA> --prerelease`
   （`--target` に短縮 SHA を渡すと HTTP 422 になる。フル SHA かブランチ名を渡す）。
6. 既定値を**2箇所、対で**更新する:
   - `tools/Fetch-GStreamerRuntime.ps1` ── `-Uri` と `-Sha256` の既定値
   - `.github/workflows/release.yml` ── `GSTREAMER_RUNTIME_TAG` / `GSTREAMER_RUNTIME_ASSET`
7. **唯一のゲート**（この整合を見る自動テストは無い）:
   `gh release download <新タグ>` で落とし、`tools/Fetch-GStreamerRuntime.ps1
   -ArchivePath <zip>` を **`-Sha256` を指定せずに**通す（＝`release.yml` と同じ呼び方）。
   既定値と実物がずれていると release の fetch がハッシュ不一致で落ちる。
   仕上げに引数なしで実行し、素の HTTP ダウンロードでも通ることを確かめる。

## GPU 実機での確認

更新した同梱版で `tools/Verify-GpuEncoders.ps1` を GPU 実機で流す
（手順とレポートの読み方は [gpu-verification.md](gpu-verification.md)）。
ハードウェアが無い機械では `nvcodec` / `qsv` / `amfcodec` は要素を登録しないため、
GPU エンコーダーの生存は実機でしか確かめられない。
