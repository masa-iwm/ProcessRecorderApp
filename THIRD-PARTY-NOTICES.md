# Third-party notices

このファイルは **同梱（bundled）配布に含まれる第三者コンポーネント**の一覧です。

- **非同梱配布には適用されません。** あちらは GStreamer を同梱せず、利用者が別途
  インストールしたものを実行時に解決します（`GStreamerRuntimeLocator`）。
- 本体（ProcessRecorderApp）のライセンスは [`license.txt`](license.txt)（MIT）です。
  **MIT はこのアプリのコードに対するもので、同梱物には及びません。**

> **⚠ これは事実の棚卸しであって、法的助言ではありません。**

## ライセンス文の所在

各コンポーネントのライセンス**全文**は [`licenses/third-party/`](licenses/third-party/) に、
**上流の原文をそのまま**置いてあります（生成・要約したものではありません）。

> **このファイルは非同梱版の配布物にも入っています。** そちらには
> `licenses/third-party/` が**入りません** ── ここに挙げた第三者コンポーネントを
> 1つも含まないためです（下記「5.」）。以下のリンクはリポジトリを指しています。

- 取得元・版・SHA256 は [`licenses/third-party/SOURCES.tsv`](licenses/third-party/SOURCES.tsv)。
  取得は `tools/Fetch-ThirdPartyLicenses.ps1`、照合は `-Verify` で行えます。
- どのファイルがどのプロジェクト由来かは
  [`licenses/third-party/COMPONENTS.tsv`](licenses/third-party/COMPONENTS.tsv)（同梱 45 ファイル全件）。
- 同梱版の配布 zip では、これらは発行物直下の `licenses/third-party/` に入ります。
  `license.txt` と本ファイルは**同梱・非同梱の両方**に入ります。

> **公式インストーラにはライセンス文が1つも入っていません。**
> `gstreamer-1.0-mingw-x86_64-1.28.4.exe` が展開する **712 ファイルを全走査して 0 件**でした
> （同じ検索式をこのリポジトリに掛けると `license.txt` と本ファイルの 2 件が出ます）。
> したがって全文は**上流から取得する**しかなく、上記の仕組みはそのためのものです。

## 由来（provenance）

公式インストーラ **`gstreamer-1.0-mingw-x86_64-1.28.4.exe`** の
**Runtime / LGPL-only** 構成でインストールしたバイナリを、**改変せずに**、
**このアプリが実際に構築しうる要素だけ**へ絞ったものです。

| | ファイル数 | サイズ | zip |
|---|---:|---:|---:|
| インストール直後 | 349 | 256MB | 79.8MB |
| **同梱物（現在）** | **45** | **49.7MB** | **16.0MB** |

**一覧は、下記の種から辿った推移閉包そのものです**（それ以外は1件も入っていません）。
このアプリが読み込まない `webrtc` / `rtsp` / `rtspserver` / `sctp` / `mse` / `play` /
`player` / `transcoder` などのコアライブラリは含まれません。

**GPL のコンポーネントは含まれません**（`x264` / `x265` / `xvid` / `a52` / `dvdread` /
`mpeg2enc` などは1つも入っていない）。インストーラの LGPL-only 選択と、
削減後の実測の両方で確認しています。**`libstdc++` / `libgcc` は GPL-3.0 ですが
例外条項が付いています** ── 下記「1.」を参照。

### 一覧の作り方（再現手順）

1. 製品が構築しうる要素（`SrcPipelineBuilder` / `EncoderCatalog` / `EventRecorder` /
   `GstPreviewer`）から必要プラグインを決める（14 件）
2. **種はその 14 プラグインと、マネージド側が名前で読む3ライブラリだけ**
   ── `ImportResolver`（`libgstreamer-1.0-0.dll` / `libgstvideo-1.0-0.dll` /
   `libgobject-2.0-0.dll`）と `GStreamerRuntimeLocator`（`libglib-2.0-0.dll`）。
   加えて `tools/Verify-GpuEncoders.ps1` が実行する `gst-inspect-1.0.exe`
3. そこから **PE のインポートを再帰的に辿って閉包を取る**
   ── 依存を推測で削ると、プラグインが**黙って blacklist される**。
   実行するのは [`tools/Get-GStreamerImportClosure.ps1`](tools/Get-GStreamerImportClosure.ps1)
4. 削減前と**同じ機械で**要素の有無を突き合わせ、失われた要素が無いことを確認する
   （`gst-inspect-1.0` を**毎回まっさらなレジストリで**走らせること。
   キャッシュが残っていると、もう読み込めない要素をそのまま列挙する）
5. 各プラグインの申告ライセンスと由来サブプロジェクトは
   `gst-inspect-1.0 <plugin>` の `License` 行・`Source module` 行

> **PE のインポートは「静的に import しているもの」しか映しません。**
> 実行時に名前で開く（`g_module_open` / `LoadLibrary`）ものは映らないので、
> 削除候補の名前が**残す 44 件のバイナリの中に文字列として現れないこと**も確認しています
> （`libgst….dll` の形と、`lib` と `.dll` を落とした gmodule の形の両方で 0 件）。

## GStreamer プラグイン（14）

いずれも GStreamer 1.28.4 の一部で、**申告ライセンスはすべて LGPL** です
（`gst-inspect-1.0` の `License` 行の実測値）。

| プラグイン | 由来（Source module） | 用途 |
|---|---|---|
| `coreelements` | gstreamer | `queue` / `tee` / `capsfilter` / `filesink` / `identity` |
| `app` | gst-plugins-base | `appsrc` / `appsink`（録画とプレビューの受け渡し） |
| `videoconvertscale` | gst-plugins-base | `videoconvert` |
| `videotestsrc` | gst-plugins-base | テストパターン（設定で選べるソース） |
| `typefindfunctions` | gst-plugins-base | 型判定 |
| `isomp4` | gst-plugins-good | `mp4mux` |
| `videoparsersbad` | gst-plugins-bad | `h264parse` |
| `d3d12` | gst-plugins-bad | 画面キャプチャ / 変換 / プレビュー / D3D12 エンコーダー |
| `d3d11` | gst-plugins-bad | D3D11 経路のエンコーダー |
| `mediafoundation` | gst-plugins-bad | カメラ入力・Media Foundation エンコーダー |
| `nvcodec` | gst-plugins-bad | NVIDIA NVENC |
| `qsv` | gst-plugins-bad | Intel Quick Sync |
| `amfcodec` | gst-plugins-bad | AMD AMF |
| `dwrite` | gst-plugins-bad | `dwriteclockoverlay`（時刻の焼き込み） |

> **`openh264` は同梱していません。** 理由は下記「2.」。

## GStreamer ライブラリ（17）

`libgstreamer-1.0-0.dll` を含む GStreamer 本体のライブラリ群で、
いずれも **GStreamer 1.28.4（LGPL-2.1-or-later）** の一部です。内訳:

| 由来 | 数 | ライセンス文 |
|---|---:|---|
| gstreamer（core） | 2 | `licenses/third-party/gstreamer/COPYING` |
| gst-plugins-base | 8 | `licenses/third-party/gst-plugins-base/COPYING` |
| gst-plugins-bad | 7 | `licenses/third-party/gst-plugins-bad/COPYING` |

`gst-inspect-1.0.exe` と `gst-launch-1.0.exe`（いずれも gstreamer core）も同梱しています
── 前者は `tools/Verify-GpuEncoders.ps1` が実機検証で実行するため、後者は利用者の機械で
パイプラインを単体再現する（アプリ抜きの切り分け）ため、どちらも閉包の種に入れてあります。

> **`gst-rtsp-server` は同梱していません**（閉包の外）。
> サブプロジェクトごと配布物に含まれず、ライセンス文もありません。

## その他の第三者ライブラリ（12）

| ファイル | プロジェクト・版 | ライセンス | 全文（`licenses/third-party/` 配下） |
|---|---|---|---|
| `libglib-2.0-0.dll`, `libgobject-2.0-0.dll`, `libgio-2.0-0.dll`, `libgmodule-2.0-0.dll` | GLib 2.82.4 | LGPL-2.1-or-later | `glib/LGPL-2.1-or-later.txt` |
| `libintl-8.dll` | **proxy-libintl**（frida）0.5 | GNU Library GPL v2 or later | `proxy-libintl/COPYING` |
| `libffi-7.dll` | libffi（GStreamer の meson port）meson-3.2.9999.5 | MIT | `libffi/LICENSE` |
| `liborc-0.4-0.dll` | Orc 0.4.42 | BSD-2-Clause（一部の節は BSD-3-Clause） | `orc/COPYING` |
| `libpcre2-8-0.dll` | PCRE2 10.42 | BSD-3-Clause | `pcre2/LICENCE` |
| `libz-1.dll` | zlib 1.3.1 | zlib License | `zlib/LICENSE` |
| `libstdc++-6.dll`, `libgcc_s_seh-1.dll` | GCC runtime 14.2.0 | **GPL-3.0 with GCC Runtime Library Exception** | `gcc-runtime/COPYING3`, `gcc-runtime/COPYING.RUNTIME` |
| `libwinpthread-1.dll` | mingw-w64 winpthreads 12.0.0 | MIT（一部に BSD-3-Clause 由来の部分） | `mingw-w64-winpthreads/COPYING` |

> **`glib/` に置いてあるファイル名が `COPYING` でないのは上流の都合です。**
> GLib 2.82.4 の `COPYING` は中身が1行（`LICENSES/LGPL-2.1-or-later.txt`）の
> ポインタなので、実体である `LICENSES/LGPL-2.1-or-later.txt` の方を取得しています。
> **`pcre2/LICENCE` の綴りも上流のまま**（英国式）。

> **`libintl-8.dll` は GNU gettext ではありません。** 実体は
> **proxy-libintl**（Tor Lillqvist 作・frida が保守）で、`intl.dll` があれば委譲し、
> 無ければ何もしないスタブです（43KB）。**ライセンスは LGPL-2.1 ではなく
> 「GNU *Library* General Public License, Version 2」**（本文は上流の `COPYING`、
> ソースヘッダーは "either version 2 of the License, or (at your option) any later version"）。

`libstdc++-6.dll` は 26.5MB で**同梱物の半分以上（53%）**を占めます。
**削減で減らせるのはここではありません** ── 同梱物は改変しない方針なので、
`strip` も部分リンクもしていません。

## 版とソースの入手先

LGPL は「対応するソースの入手先を示すこと」を求めます。同梱しているのは
**下記の版そのもの**です（版は cerbero のレシピと、バイナリに埋め込まれた
版文字列の両方で確認しました）。

| プロジェクト | 版 | ソース（版を固定した実体） |
|---|---|---|
| GStreamer core | 1.28.4 | <https://gstreamer.freedesktop.org/src/gstreamer/gstreamer-1.28.4.tar.xz> |
| gst-plugins-base | 1.28.4 | <https://gstreamer.freedesktop.org/src/gst-plugins-base/gst-plugins-base-1.28.4.tar.xz> |
| gst-plugins-good | 1.28.4 | <https://gstreamer.freedesktop.org/src/gst-plugins-good/gst-plugins-good-1.28.4.tar.xz> |
| gst-plugins-bad | 1.28.4 | <https://gstreamer.freedesktop.org/src/gst-plugins-bad/gst-plugins-bad-1.28.4.tar.xz> |
| GLib | 2.82.4 | <https://download.gnome.org/sources/glib/2.82/glib-2.82.4.tar.xz> |
| proxy-libintl | 0.5 | <https://github.com/frida/proxy-libintl/archive/refs/tags/0.5.tar.gz> |
| libffi（meson port） | meson-3.2.9999.5 | <https://gstreamer.freedesktop.org/src/mirror/libffi/libffi-meson-3.2.9999.5.tar.bz2> |
| Orc | 0.4.42 | <https://gstreamer.freedesktop.org/src/orc/orc-0.4.42.tar.xz> |
| PCRE2 | 10.42 | <https://github.com/PhilipHazel/pcre2/releases/download/pcre2-10.42/pcre2-10.42.tar.bz2> |
| zlib | 1.3.1 | <https://zlib.net/fossils/zlib-1.3.1.tar.gz> |
| GCC runtime（libstdc++ / libgcc） | 14.2.0 | <https://ftp.gnu.org/gnu/gcc/gcc-14.2.0/gcc-14.2.0.tar.xz> |
| mingw-w64 winpthreads | 12.0.0 | <https://github.com/mingw-w64/mingw-w64/archive/refs/tags/v12.0.0.tar.gz> |

> **12 行すべて、実際に叩いて実体が返ることを確認しています**（HEAD 要求で
> `Content-Type` が `application/x-xz` / `x-bzip2` / `x-gzip` / `octet-stream`、
> かつ長さが取れること）。**`200 OK` だけでは足りません** ── ホストによっては
> `200` を返しながら bot 対策の HTML を返すことがあります（ライセンス文の取得で
> `gitlab.freedesktop.org` と `gcc.gnu.org` が実際にそうで、`SOURCES.tsv` が
> GitHub ミラーを使っているのはそのためです）。
> **GStreamer は4サブプロジェクトを別行にしてあります。** ディレクトリ索引
> （`.../src/`）1行で済ませると版が固定されず、**同梱 45 ファイルのうち 33 件**
> ＝最大の塊が最も弱い指し方になってしまうためです。

> **libffi・Orc・PCRE2・zlib・proxy-libintl の URL は、cerbero が
> `tarball_checksum` を付けて指定しているものと同一**です（`recipes/*.recipe`）。
> つまり**同梱バイナリがビルドされた当のアーカイブ**です。
> libffi についてはダウンロードした tarball の SHA256 が cerbero のピン留め値と
> 一致することを実測し、その中の `LICENSE` を取り出しています。
>
> GCC と mingw-w64 のツールチェーンは
> `mingw-12.0.0-gcc-14.2.0-windows-multilib.tar.xz`（cerbero の
> `bootstrap/windows.py` が SHA256 付きで指定）で入ります。
> 版はバイナリ内の `GCC: (GNU) 14.2.0` / `GCC: (Built by GStreamer ...)` とも一致します。

## 確認が要る点

### 1. `libstdc++` / `libgcc_s_seh` は GPLv3 だが、例外条項が付いている

**GCC Runtime Library Exception** により、GCC でコンパイルしたプログラムと一緒に
再配布しても、そのプログラムが GPL になることはありません。
**grep して「GPL がある」と驚く類の項目なので、ここに明記しておきます。**
`libstdc++-6.dll` は 26.5MB あり削減後の半分以上を占めますが、
C++ 実装のプラグイン（d3d12 / d3d11 / nvcodec / qsv / mediafoundation / dwrite）が
必要とするため外せません。

**全文は2本とも同梱しています**（`licenses/third-party/gcc-runtime/COPYING3` と
`COPYING.RUNTIME`）── 例外条項は GPL 本文とは別ファイルなので、片方だけでは足りません。

### 2. OpenH264 は同梱しない（著作権と特許が別の話のため）

`libopenh264` の**著作権ライセンスは BSD-2-Clause** です。一方 H.264 には特許があり、
Cisco のロイヤリティフリー枠は「**Cisco が公開しているバイナリを利用者側が取得する**」
形を前提にしています ── インストーラの `libopenh264-7.dll` は cerbero がソースから
ビルドしたもの（`recipes/openh264.recipe` / 2.6.0）で、**その枠の外**です。

そこで `libgstopenh264.dll` と `libopenh264-7.dll` の2件は同梱物に含めていません。

- **バイナリの依存関係としては、他の同梱物に影響しません。** `libopenh264-7.dll` を
  import しているのは `libgstopenh264.dll` 1件のみです。
- **`openh264enc` は製品のカタログには残っています。** 非同梱版では利用者の
  GStreamer 側に存在しうるためで、`EncoderCatalog` からは除いていません。

### 3. LGPL の義務

同梱物は **改変せず動的リンク（DLL）** しています。したがって

- 対応するソースの入手先を示すこと ── **上記「版とソースの入手先」に版を固定した
  URL で記載**
- **利用者が DLL を差し替えられる状態を保つこと** ── 現状そうなっています。
  `GStreamerRuntimeLocator` は PATH・環境変数・レジストリ・MSYS2 を同梱物より
  **優先**するので、利用者は自分のビルドに差し替えられます
- **各ライセンス文を同梱すること** ── **対応済み**（`licenses/third-party/`。
  ただし下記「4.」の限界がある）

### 4. 検証の分担（リポジトリ内と配布 zip）

- **リポジトリの中の整合**は `ThirdPartyLicenseTests`（L1）が見ます ──
  `licenses/third-party/` の過不足と、全文が上流の原文のままであること。
  **見ているのはリポジトリの中だけ**です。
- **配布 zip 側**は `.github/workflows/release.yml` が担います ── 発行ディレクトリの
  同梱ランタイムを `COMPONENTS.tsv` と突き合わせ、ライセンス文と `license.txt`・
  本ファイルの存在を確認してから zip を作ります。同梱物の smoke テストは
  **件数を見ること** ── 0 件を選ぶフィルタでも `dotnet test` は成功で終わります。
- zip には**長さ 0 のディレクトリエントリ**が入ります
  （`runtimes/win-x64/` と `runtimes/win-x64/lib/`）。
  **エントリ数と実ファイル数は別物**なので、数えるときは長さで分けること。

### 5. 非同梱配布という選択肢

非同梱版はここに挙げたものを**1つも含みません**（`license.txt` と本ファイルだけが入ります）。

### 6. 同梱ランタイムからファイルを削るときに確かめること

依存を推測で削ると、プラグインが**黙って blacklist される**（起動は成功し続ける）ため、
削る前に必ず次の4点を確かめます。

| 確かめること | 方法 | 合格条件 |
|---|---|---|
| 閉包の計算が正しいか | **削減前の全ファイルの木**に同じ種を通す（`tools/Get-GStreamerImportClosure.ps1`） | 削減済みの木から出した閉包と**件数・総バイト数が完全一致** |
| 名前で動的に開かれていないか | 削除候補の名前を、残すバイナリ全体から文字列検索 | **0 件**（`libgst….dll` の形・`lib` と `.dll` を落とした gmodule の形の両方） |
| 要素が失われていないか | 削減前後で `gst-inspect-1.0` の要素一覧を比較（**毎回まっさらなレジストリで**。キャッシュが残っていると、もう読み込めない要素をそのまま列挙する） | 要素一覧が一致し、blacklist が**両方 0 件** |
| アプリが実際に録れるか | 削減版を同梱して発行し、`tools/Verify-GpuEncoders.ps1` を流す | 全ケース OK・有効な MP4 |

> **要素の一致は「その機械で登録される要素」の一致です。** `nvcodec` / `qsv` /
> `amfcodec` はハードウェアが無いと要素を1つも登録しないので、GPU の無い機械での
> 照合には現れません（プラグインの読み込み自体の成否は blacklist の件数が根拠）。
> それらは GPU 実機で確かめます ── 手順は `docs/gpu-verification.md`。
