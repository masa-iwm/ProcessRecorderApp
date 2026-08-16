# 同梱 GStreamer ランタイムの更新手順

同梱ランタイムは、公式ビルドの GStreamer を入れたツリーを、**アプリが実際に読み込む
推移閉包だけ**へ絞ったもの。Release アセットとしてこのリポジトリに置き、
`tools/Fetch-GStreamerRuntime.ps1` と `.github/workflows/release.yml` が取得する。

**形態は2つあり、どちらも配る。**

| 形態 | 元にするツリー | 同梱物 | 台帳 | 備考 |
|---|---|---|---:|---|
| `mingw` | 公式インストーラ（MinGW 64-bit / Runtime / **LGPL-only** 構成） | 46 ファイル・49.9MB | `licenses/third-party/COMPONENTS.tsv` | 自己完結 |
| `msvc` | 公式 MSVC ビルド（**フル構成**。LGPL-only に相当する選択肢が無い） | 44 ファイル・24.6MB | `licenses/third-party/COMPONENTS-msvc.tsv` | **VC++ 再頒布可能パッケージが要る** |

**中身は同じ製品面**（まっさらなレジストリでどちらも 16 プラグイン・268 件。blacklist 0）。
違いは3点だけ:

- **ファイル名**: MSVC 版は同じライブラリを `lib` 接頭辞なしで配る
  （`gstreamer-1.0-0.dll`）。閉包の種もスクリプトが両方の綴りを引く。
- **C/C++ ランタイム**: MinGW 版は `libstdc++-6.dll` / `libgcc_s_seh-1.dll` /
  `libwinpthread-1.dll` を同梱する。MSVC 版は同梱せず、`msvcp140.dll` /
  `vcruntime140.dll` / `vcruntime140_1.dll` を**利用者の機械から**解決する
  （＝前提条件。THIRD-PARTY-NOTICES.md「由来」の警告）。
- **`gstwinrt-1.0-0.dll`**: MSVC 版にだけ在る。Windows Graphics Capture を使えるように
  しているライブラリで、**`d3d12screencapturesrc` / `d3d11screencapturesrc` に
  `capture-api` プロパティが生えるのはこの形態だけ**
  （UI は `GstIntrospect.ElementHasProperty` で要素に訊いてから行を出す）。

## 命名規約

- タグ: MinGW は `gstreamer-runtime-v<GStreamer の版>`、MSVC は
  `gstreamer-runtime-msvc-v<版>`（例: `gstreamer-runtime-v1.28.6`、
  `gstreamer-runtime-msvc-v1.28.6`）。同じ版のまま中身を差し替える場合は `-r2` のような
  枝番を付ける。**`v` 単独で始まる名前にしないこと** ── `v*` は `release.yml` の
  タグトリガーに一致する。
- アセット: `gstreamer-runtime-win-x64-v<版>.zip` /
  `gstreamer-runtime-msvc-win-x64-v<版>.zip`

## 手順

1. 元のツリーを用意し、`tools/Get-GStreamerImportClosure.ps1` で閉包を再計算する。
   種（プラグイン一覧と名前で読むライブラリ）は製品コードと同期しており、ずれると
   `RuntimeClosureSeedSyncTests`（L1）が落ちる。

   **MinGW 版** ── 新しいインストーラ（Inno Setup 6）を **Runtime / LGPL-only 構成**で
   無人導入する。`objdump -p` が必要（公式 MinGW 版には付属しない。MSYS2 のものを使う）。

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
   > **MSVC 版のフル構成ツリーを入れているだけでは代わりにならない** ── 解決順は
   > 元の `PATH` が最優先で、そこに MinGW 版が居る限りそちらが勝つ（`gst.runtime` の
   > `selected=` / `flavor=` で実際にどちらを踏んだか分かる）。

   **MSVC 版** ── **LGPL-only に相当する選択肢が無い**ので、フル構成のツリー
   （`x264` も `libav` も入っている。1.28.6 実測で 828 ファイル・349MB）から絞る。
   したがって「GPL を持ち込まない」の根拠は**閉包の実測だけ**になる ──
   `-SeedPlugins` を**必ず明示**すること（既定は木の全プラグインを種にするので、
   GPL プラグインまで引き込む）。`objdump` の代わりに Visual Studio の `dumpbin` が使える
   （MSYS2 が無い機械でも動く。`-Dumpbin` を渡すとそちらを使う）。

   ```powershell
   $seeds = 'gstamfcodec.dll','gstapp.dll','gstcoreelements.dll','gstd3d11.dll','gstd3d12.dll',
            'gstdwrite.dll','gstisomp4.dll','gstmediafoundation.dll','gstnvcodec.dll','gstqsv.dll',
            'gsttypefindfunctions.dll','gstvideoconvertscale.dll','gstvideoparsersbad.dll',
            'gstvideorate.dll','gstvideotestsrc.dll'
   tools\Get-GStreamerImportClosure.ps1 `
       -RuntimeRoot "$env:LOCALAPPDATA\Programs\gstreamer\1.0\msvc_x86_64" `
       -Dumpbin '<VS>\VC\Tools\MSVC\<版>\bin\Hostx64\x64\dumpbin.exe' `
       -SeedPlugins $seeds -OutDir out
   ```

   **`external.txt` を必ず読むこと。** 木の外へ出る名前は全部 Windows のシステム DLL で
   なければならない ── MSVC 版はここに `msvcp140.dll` / `vcruntime140.dll` /
   `vcruntime140_1.dll` が出る（`api-ms-win-crt-*` は Windows 10 以降の UCRT なので
   OS の一部だが、この3本は**再頒布可能パッケージ**であって OS の一部ではない）。
   増えていたら、同梱するか前提条件として文書化するかを決める。

2. ファイルを削る前の確認 4 点を通す ── `THIRD-PARTY-NOTICES.md`「6.」。
   **形態をまたいで突き合わせられる**のがいちばん強い ── 削減済みの両ツリーで
   `gst-inspect-1.0` の要素一覧を取ると**完全に一致する**はずで、
   一致しなければどちらかの閉包が狭い。
3. その形態の台帳（`COMPONENTS.tsv` / `COMPONENTS-msvc.tsv`）を新しい一覧で差し替える。
   版が変わった場合は `SOURCES.tsv`・ライセンス全文・`THIRD-PARTY-NOTICES.md` の版表記も
   更新する（`ThirdPartyLicenseTests`（L1）が検査する ── 件数の内訳の表も**形態ごとに
   1 行**あり、台帳から数え直して突き合わせる）。
4. zip を作る ── ルートは `win-x64/`、**エントリ区切りは `/`**（PowerShell 5.1 の
   `ZipFile.CreateFromDirectory` は `\` で書き、規格違反なので読み替えが黙って外れる）。
   SHA256 を採取する。
5. 上流のバイナリを差し替えた場合（パッチを当てた自前ビルドなど）は、
   **パッチを `patches/` へ置き**、`THIRD-PARTY-NOTICES.md` に改変の事実・対応する
   ソース・ツールチェーン・公式ビルドとの同一性の実測を書く（LGPL の
   「対応するソースを示す」義務は、改変した側では **上流の tarball だけでは満たない**）。
   差し替えたファイルは **import ・閉包・要素一覧を公式ビルドと突き合わせる**
   （**形態ごとに別のツールチェーンでビルドするので、SHA256 もツールチェーンも別に書く**）。
6. Release を作ってアセットを上げる:
   `gh release create gstreamer-runtime[-msvc]-v<版> <zip> --target <フル SHA> --prerelease`
   （`--target` に短縮 SHA を渡すと HTTP 422 になる。フル SHA かブランチ名を渡す）。
7. 既定値を**4箇所、対で**更新する:
   - `tools/Fetch-GStreamerRuntime.ps1` ── `$assets` の該当形態の `Uri` と `Sha256`
   - `.github/workflows/release.yml` ── `GSTREAMER_RUNTIME_TAG` /
     `GSTREAMER_RUNTIME_ASSET`（MinGW）、`GSTREAMER_RUNTIME_TAG_MSVC` /
     `GSTREAMER_RUNTIME_ASSET_MSVC`（MSVC）
8. **唯一のゲート**（この整合を見る自動テストは無い）:
   `gh release download <新タグ>` で落とし、`tools/Fetch-GStreamerRuntime.ps1
   -Flavor <形態> -ArchivePath <zip>` を **`-Sha256` を指定せずに**通す
   （＝`release.yml` と同じ呼び方）。既定値と実物がずれていると release の fetch が
   ハッシュ不一致で落ちる。仕上げに `-ArchivePath` なしで実行し、素の HTTP ダウンロードでも
   通ることを確かめる。**両形態とも通すこと** ── 展開先は同じ
   `runtimes/win-x64` なので、最後に流した形態がその場に残る。

## GPU 実機での確認

更新した同梱版で `tools/Verify-GpuEncoders.ps1` を GPU 実機で流す
（手順とレポートの読み方は [gpu-verification.md](gpu-verification.md)）。
**形態ごとに 1 回ずつ**流すこと ── スクリプトは両方の命名を引くので同じ手順で通る。
ハードウェアが無い機械では `nvcodec` / `qsv` / `amfcodec` は要素を登録しないため、
GPU エンコーダーの生存は実機でしか確かめられない。
