# 同梱 GStreamer ランタイムの更新手順

同梱ランタイムは NuGet パッケージ **GstSharpBundle.Windows.X64**
（[masa-iwm/GstSharpBundle](https://github.com/masa-iwm/GstSharpBundle) フォーク、
GitHub Packages 発行、GStreamer 公式 MSVC ビルドのフル構成）が供給する。
`GStreamer.GstSharpBundle.csproj` がパッケージ内の `gstreamer/win-x64/**` を
出力・発行ディレクトリへ複製する（バインディング本体は同フォークの
`GstSharpBundle` パッケージ）。

> 旧構成（公式 MinGW ビルドを閉包計算で削り、Release アセットとして配る方式。
> `tools/Fetch-GStreamerRuntime.ps1` / `tools/Get-GStreamerImportClosure.ps1` /
> `RuntimeClosureSeedSyncTests`）は GstSharpBundle への切り替えで撤去した。
> `release.yml` と `licenses/third-party/` の台帳はまだ旧構成の記述のままで、
> **この構成の発行物は台帳を作り直すまで配布しない**（MSVC フル構成は
> x264（GPL）や FFmpeg 系 DLL を含み、現行の THIRD-PARTY-NOTICES.md が
> カバーしていない）。

## 更新手順

1. フォーク（masa-iwm/GstSharpBundle）で GStreamer の版を上げる:
   - `GstSharpBundle.Windows.X64/gstreamer/win-x64/` を新しい公式 MSVC ビルドの
     ツリーで差し替える。
   - バインディング（`GstSharpBundle/Gst/generated/`）を必要に応じて再生成する
     （C ABI は後方互換なので、新 API を使わない限り再生成は必須ではない）。
   - `Directory.Build.props` の `VersionPrefix` / `VersionSuffix` を上げ、
     `dotnet pack` → GitHub Packages へ発行する。
2. このリポジトリの `Directory.Packages.props` で
   `GstSharpBundle` / `GstSharpBundle.Windows.X64` の版を上げる
   （**2 つの版は独立** ── ネイティブに変更が無ければ Windows.X64 は据え置きでよい）。
3. `dotnet restore` → ビルド → アプリを起動し、activity.log の `gst.runtime`
   （ロード元が想定どおりか・mixed=False か）と `gst.encoders`（Probe の結果）を確認する。
4. E2E（発行物に対する L2/L3）を流す。

## GPU 実機での確認

更新した同梱版で `tools/Verify-GpuEncoders.ps1` を GPU 実機で流す
（手順とレポートの読み方は [gpu-verification.md](gpu-verification.md)）。
ハードウェアが無い機械では `nvcodec` / `qsv` / `amfcodec` は要素を登録しないため、
GPU エンコーダーの生存は実機でしか確かめられない。
