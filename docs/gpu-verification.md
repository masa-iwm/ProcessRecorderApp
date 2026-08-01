# GPU 実機検証

開発機に GPU が無いため、GPU エンコーダー経路の最終確認は別 PC で行う。運用は「対象 PC へ発行物と検証スクリプトを持ち込み、1回実行して Markdown レポートを持ち帰る」の往復で完結する。スクリプトは `gst-inspect-1.0.exe` でその実機に存在する H.264 エンコーダーを列挙して検証ケースを機械的に生成するので、Intel / NVIDIA / AMD / GPU 無しのどのマシンでも同じコマンドで適切なケースが走る。手作業はゼロ。

## 何に GPU 実機が要るか

`qsvh264enc`（Intel）/ `nvh264enc`・`nvd3d11h264enc`・`nvautogpuh264enc`（NVIDIA）/ `amfh264enc`（AMD）/ `d3d12h264enc` ── カタログ（`EncoderCatalog.D3d12Candidates`）の GPU 専用候補6件 ── は、対応 GPU のある実機でしか要素が登録されない。ただし実機が要る範囲は小さい。

| 何を確かめるか | どこで確かめられるか |
|---|---|
| 候補の優先順位・`PreferredH264Encoder` の尊重とフォールスルー・`NeedsSystemMemory` | 単体テスト（プローブを注入するので GPU 不要・どのマシンでも同じ結果） |
| `D3d12` 経路で `d3d12download` の挿入が実際に効くこと | GPU 無しの実機で確認できる。`d3d12testsrc` / `d3d12download` / `d3d12swapchainsink` は WARP（ソフトウェアラスタライザ）でも動くため、`Type=D3d12` ＋ システムメモリ系エンコーダーの組み合わせがそのまま再現できる |
| 候補フォールバックループが実際に回ること | カタログの先頭候補に存在しないプロパティを一時注入して確認（GPU 不要） |
| `qsvh264enc` / `nvh264enc` / `nvd3d11h264enc` / `nvautogpuh264enc` / `amfh264enc` / `d3d12h264enc` が実際に有効な MP4 を出すこと | 対応 GPU のある実機が必要 |

GPU 無しで再現できる根拠: `d3d12testsrc ! video/x-raw(memory:D3D12Memory) ! x264enc` は `could not link ... can't handle caps` で失敗し（exit 1）、`d3d12download`（＋ `videoconvert`）を挟むと成功する（exit 0）。この「D3D12Memory をシステムメモリ系エンコーダーへ直結すると壊れる」失敗は要素不在・メモリフィーチャ不一致・未知プロパティと同様に `parse_launch` の時点で同期的に失敗するため、候補フォールバックの判定は GPU 無しでも確実に検証できる。

一方、最後の1行だけは実機が要る。`ParseLaunch` も `SetState` も CLI の終了コードもすべて成功のまま、無効な MP4 が黙って残る失敗モードが実在するためである。エンコーダーが SPS/PPS をストリーム先頭でしか送らない場合、リングバッファ経由で途中から始まる録画にはパラメータセットが届かず、`h264parse` が全 NAL を捨てる。このときバスに出るのは `Warning` だけで `Error` は出ない。この教訓から sink パイプラインは `h264parse config-interval=-1`（全 IDR 直前にパラメータセットを再挿入）と `alignment=au` を必ず含む（`EventRecorder` 参照）。「有効な MP4 が出るか」は実機で流してコンテナを検証するまで確定しない。

## Verify-GpuEncoders.ps1 の使い方

`tools/Verify-GpuEncoders.ps1`。無人実行。対象 PC に publish 出力一式とスクリプトをコピーして実行する。**配布物は Native AOT なので、実機検証も `win-x64-aot` で発行した出力（または配布 zip の展開物）に対して行う**（スクリプトの既定 `-PublishDir` も AOT の発行先を指す）。

```powershell
.\Verify-GpuEncoders.ps1 -PublishDir <publish 出力のパス>

# リポジトリごと持ち込む場合は publish 後に引数なしで実行できる
.\tools\Verify-GpuEncoders.ps1
```

パラメータ: `-PublishDir` / `-WorkDir` / `-RecordSeconds`（既定 3）/ `-GstDebug` / `-KeepWorkDir`。

スクリプトが自動で行うこと:

- 隔離用の一時データディレクトリを作り、`PROCESSRECORDERAPP_DATA_DIR` と `PROCESSRECORDERAPP_KEY_PREFIX` を設定する（両方設定しないと、対象 PC の開発者常駐インスタンスへコマンドが飛ぶ）。
- `gst-inspect-1.0.exe` で実機に存在する H.264 エンコーダーを列挙し、検証ケースを機械的に生成する。
- ケースごとに `settings.json` を生成 → 常駐ワーカー起動 → 録画 → 停止 → MP4 を ISO-BMFF の直接パースで検証（外部プロセス不要）。合格条件は `ftyp`・`moov`・`mdat` の各ボックスが在ること、`moov` 内に `avcC`（H.264 のデコーダー構成）が含まれること、`mvhd` から計算した再生時間が 0 より大きいこと。
- 初回起動は GStreamer のプラグインレジストリ構築で時間がかかるため（ランチャーの登録待ち上限は 120 秒）、最初の `ping` が 0 以外を返したら 3 秒おいて1回だけ自動で再試行する。
- `gst-inspect-1.0.exe` の全出力から H.264 エンコーダーを列挙し、カタログが知らない要素が実機に在れば警告する（後述）。
- `debug.log` から `gst.encoders` / `gst.encoder selected` / `gst.encoder candidate-failed` を抽出する。
- 失敗ケースがあれば `GstDebug=4` で自動再実行し、診断行をレポートの `Diagnostics for failed cases` 節に載せる。
- レポート `gpu-encoder-report.md` を出力し、いずれかのケースが失敗したら終了コード `1` を返す。失敗があれば作業ディレクトリ（settings.json / activity.log / debug.log / MP4）を残し、全ケース成功なら削除してレポートだけ `%TEMP%` にコピーする ── 作業ディレクトリが消えていること自体が全ケース成功の証跡になる。

生成されるケース:

| ケース | 意味 |
|---|---|
| `D3d12 / automatic selection` | 自動選択の中核契約。実機の GPU が何であれ `D3d12` で録画できること |
| `System / automatic selection` | CI が使う構成 |
| `D3d12 / PreferredH264Encoder=<実機にある GPU エンコーダー>` | 実機にある GPU エンコーダーごとに1ケース自動生成。指定どおり選択され、有効な MP4 が出ること |
| `D3d12 / PreferredH264Encoder=nosuchh264enc` | 存在しない指定でもフォールスルーして録画できること |
| `D3d12 / manual EncodingProperties=<システムメモリ系エンコーダー>` | D3D12 経路でシステムメモリ系エンコーダーを手動指定（`d3d12download` の挿入が要る経路）。エンコーダーは決め打ちではなく、カタログの `SystemCandidates`（`x264enc` / `openh264enc` / `mfh264enc`）のうち実機に在る先頭のものを使う ── GPL 系を含まない同梱ランタイムでは `mfh264enc` になる。3つとも無ければこのケースはスキップされ、「その実行では `d3d12download` が覆われない」と警告される（スキップは失敗ではないので、レポートが緑でもこの経路が未検証のことがある） |

スクリプトを改造するときの制約:

- `Start-Process -Wait` を使ってはいけない。プロセスツリー全体（＝終了しない常駐ワーカー）の終了を待つため永久に返らない。ランチャープロセス単体を `System.Diagnostics.Process` + `WaitForExit` で待つ。
- ネイティブ exe の呼び出しに `2>&1` を使ってはいけない。Windows PowerShell 5.1 では `NativeCommandError` になる。`System.Diagnostics.Process` で起動する。
- 出力・リテラルは ASCII のみにする。PowerShell 5.1 は BOM 無しの `.ps1` を ANSI として読むため、マシン間でコピーするスクリプトの非 ASCII は壊れる。
- エンコーダーの一覧はカタログと L1 の `EncoderCatalogScriptSyncTests` で固定されている: `$allEncoders`（問い合わせる全名）・`$gpuEncoders`（GPU 専用候補）・`$manualCandidates`（名前・プロパティ文字列・順序）・要素列挙の正規表現、さらに `Verify-HighResolution.ps1` のエンコーダー行まで。カタログの候補を足す・変えるとこれらのテストが落ちてスクリプト側の追随を強制する ── 「カタログを直してスクリプトの一覧を直し忘れる」事故の再発防止なので、テストを黙らせるのではなく一覧を直すこと。

## レポートの読み方

- `selected encoder` ── 実際に採用された起動文字列。GPU 実機ではここにカタログの GPU 専用候補6件（`d3d12h264enc` / `qsvh264enc` / `nvd3d11h264enc` / `nvh264enc` / `nvautogpuh264enc` / `amfh264enc`）のいずれかが出るはず。プロパティの付かない素のファクトリ名が出ていたら、それはカタログ外の `PreferredH264Encoder` 指定が尊重された候補（後述の GOP の例外に該当）。
- `retries`（`debug.log` の `failedAttempts`）── 0 以外なら、カタログのプロパティが実機で通らずプロパティ無しの再試行で成功したということ。その要素のカタログ定義を見直す合図。
- `Short recordings` 節（尺不足）── 生成尺が録画窓より短いケースの一覧。原因はほぼ GOP 長であってフレーム落ちではない: 録画は最初の I フレームから始まるため、リングバッファ（`BufferDuration`。このスクリプトは 2000ms で走らせ、レポートにもその値が明記される）内に I フレームが1枚も無いと事前バッファが丸ごと捨てられる。このためカタログの全候補は `gop-size=15`（`x264enc` は `key-int-max=15`。15fps で 1 秒 ＜ バッファ 2 秒）に固定してあり、これを崩すと尺不足が再発する。例外が1つ: `PreferredH264Encoder` にカタログ外のファクトリ名を指定した場合、実機に在れば「ファクトリ名のみ・プロパティ無し」の候補として先頭に挿される（`EncoderCatalog.Resolve`）ため `gop-size` が付かず、ベンダ既定の長い GOP で走って事前バッファ消失が静かに再発しうる ── `selected encoder` にプロパティ無しの名前が出ていたら尺を必ず確認する。この指標は GOP の位相次第で同一構成でも数秒ばらつくノイズの多いものなので、合否判定には使わない。GPU 無しの WARP では正常でも出るが、GPU 実機で出たら調査対象。
- 採用された `LaunchString` を見て、`EncoderCatalog` の実機未確認エンコーダー（現在プロパティ無し）に確認済みのプロパティを追加できるか判断する。単位（kbit/sec か bit/sec か）は `gst-inspect-1.0.exe <要素名>` で必ず確認する。
- レポート冒頭の `All H.264 encoders gst-inspect reports` は実機の全列挙で、カタログが知らない要素が在れば `This machine has H.264 encoders the catalog does not know about` の警告が自動で入る（この向きの検査だけがカタログ漏れを検出できる）。逆向き ── カタログに在るのに実機に無い要素 ── はケースが作られないだけなので、意図した要素が検証されたかはケース一覧とこの行で確認する。手で `gst-inspect` を叩き直す必要はない。
- スクリプトは同梱ランタイムが実際に使われたかも検査し、PATH 上の別 GStreamer が先に見つかった場合はレポート冒頭に警告が入る（その場合の結果は別ランタイムについてのもの）。

## Verify-HighResolution.ps1

`tools/Verify-HighResolution.ps1`。高解像度で「`IsInitialized=on` / `LastError=null` なのに録画もプレビューも1フレームも出ない」循環待ちの回帰検証。流儀は `Verify-GpuEncoders.ps1` と同じ（無人・ASCII のみ・`System.Diagnostics.Process` 経由・`PROCESSRECORDERAPP_DATA_DIR` と `PROCESSRECORDERAPP_KEY_PREFIX` で完全隔離）。レポートは `high-resolution-report.md`、全ケース期待どおりなら終了コード 0。

守っている不変条件: プレビュー分岐の `queue` が既定の `max-size-bytes`（10485760）のままだと、このバイト上限は高解像度で「フレーム数の上限」に化ける ── queue は上限を超えていても1件目は必ず受け取るので、1 フレームが 5242880 バイト（上限の半分）を超えると常に 1 フレームしか保持できない。NV12/I420（幅×高さ×1.5 バイト）では約 3.5Mpx ＝ 2560x1440 以上がこの領域で、1440p の 1 フレーム（約 5.5MB）は上限 10MB を下回るのに該当し、4K（約 12.4MB）は上限そのものも超える。PAUSED 中はプレビュー appsink が preroll でブロックしているので queue は排出されず、満杯の queue が tee を塞ぎ、エンコーダーが枯渇し、録画 appsink が preroll せず、パイプラインは PLAYING に到達しない ── 循環待ち。対策は2点で、(1) プレビュー queue を leaky かつバイト・時間無制限にする、(2) 初期化は `SetState` の ASYNC 返答を成功扱いせず実際に PLAYING へ達するのを待つ。

ケースは6件: 4K 画面キャプチャ構成（`d3d12screencapturesrc monitor-index=<n>`・`qsvh264enc rate-control=icq icq-quality=30 gop-size=15`・`BufferDuration=10000`）、`d3d12testsrc` による解像度スイープ（320x240 / 1920x1080 / 2560x1440 / 3840x2160 ── 1920x1080 以下は閾値未満、2560x1440 以上が「1 フレーム ＞ 5242880 バイト」の循環待ちの領域。スクリプト自身もこの式で各行に注記を付ける）、4K での自動選択（停止した候補が受理されず棄却され、次の候補が試されること）。スイープが `d3d12screencapturesrc` ではなく `d3d12testsrc` なのは、結果をその機械のモニタ構成に依存させないため。エンコーダー行はカタログと `EncoderCatalogScriptSyncTests` で結び付けてあり、カタログを変えるとテストが落ちてこの行の更新判断を迫る。

判定は `recorder.init ok` / `recorder.init fail` / `never reached PLAYING` の署名の有無と MP4 の構造で行う ── 製品自身がこの署名を **`activity.log`** に出すので、`.dot` ファイルを読む必要はない。スクリプトが署名を読むのも `activity.log` であって `debug.log` ではない（`Verify-GpuEncoders.ps1` が `gst.encoder` 行を読むのは `debug.log` 側 ── 2つのスクリプトで読むログが違う）。赤い実行を手で調べるときも作業ディレクトリの `activity.log` を見る。

この往復で最も無駄になりやすいのは「古いバイナリで走らせてしまうこと」。そのため発行物の `GStreamer.dll` に修正の目印（リテラル文字列 `leaky=downstream`）が入っているかを先に検査し、無ければ警告して終了コード 1 を返す。この検査には罠がある: アセンブリ中の UTF-16 文字列は偶数バイト境界に無いことがあるため、`Encoding.Unicode.GetString` をオフセット 0 だけに掛けると符号単位が1バイトずれて1件も一致せず、正しい発行物を「古いビルド」と誤報する。オフセット 0 と 1 の両方を試すこと（素の `grep` も ASCII として探すため UTF-16 には一致しない）。

`-SmokeTest` は GPU の無い機械で「スクリプト自身」を検証するモードで、本件の回帰検証にはならない。緑の経路（`videotestsrc`）と赤の経路（`identity drop-probability=1.0` ── caps は通してバッファだけ捨てるので、リンクと状態遷移は成功するのに PLAYING へ達しない）の両方を回す。緑だけを回しても、停止を検出できないスクリプトは緑になるため。赤側の合格条件は「`never reached PLAYING` が 1 件以上・`recorder.init fail` がちょうど 1 件・`recorder.init ok` が 0 件」── CLI の終了コードは記録されるだけで、この判定には入らない。

パラメータ: `-PublishDir` / `-WorkDir` / `-RecordSeconds`（既定 4）/ `-MonitorIndex`（既定 1。製品側の `monitor-index` の既定は `0` なので、モニタが1台の機械では既定のまま流すと画面キャプチャのケースが範囲外の index になる ── 実機のモニタ構成に合わせて指定する）/ `-SmokeTest` / `-KeepWorkDir`。

## 未検証の項目

検証済みの範囲（対になる事実）: 同梱ランタイムを NVIDIA/Intel の実機で流した検証は
全ケース OK で、GPU 専用候補（`d3d12h264enc` / `qsvh264enc` / `nvd3d11h264enc` /
`nvh264enc` / `nvautogpuh264enc`）が選ばれて有効な MP4 を出すこと、`System` の自動選択が
`mfh264enc` を選んで有効な MP4 を出すことまで観測済み。残っているのは以下。

- `amfh264enc`（AMD）── AMD GPU の機械が無く未検証。AMD 機で `tools/Verify-GpuEncoders.ps1` を1回流せばケースは自動生成される。カタログに実在しない名前を書いても例外も警告も出ずに候補から黙って消え、録画は他候補で成立してしまうため、「録画できている」ことは名前が正しい証拠にならない ── レポート冒頭の `All H.264 encoders gst-inspect reports` 行に `amfh264enc` が載り、専用ケースが OK になっていることまで確認する。確認済みのプロパティをカタログへ足すときは `EncoderCatalogScriptSyncTests` が落ちてスクリプト側の一覧の追随を迫るので、一緒に直す。
- `nvd3d11h264enc`（NVIDIA の D3D11 モード）── カタログでは `NeedsSystemMemory: true`。このフラグの実際の意味は「システムメモリが要る」ではなく「`d3d12download` を挿す」である。**メモリ交渉について未解決の食い違いが1件ある**: 「`d3d12download` は下流に合わせて出力を変えるので、D3D11 を受ける要素が下流に居れば `memory:D3D11Memory` で折り合い、CPU への往復は起きない」という説がある一方、現行の `EventRecorder.BuildSinkPipeline` は `d3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert !` とシステムメモリを capsfilter で明示固定しており、`memory:SystemMemory` は明示のフィーチャなので `memory:D3D11Memory` とは一致せず、この経路に交渉の余地は無い（＝毎フレーム GPU→CPU の往復が入る）。どちらの記述が正しいかの判定には NVIDIA 実機が要り、**未解決のまま**。録画の正しさではなく性能の問題なので録画自体は成立するが、「最適である」とは書けない（`EncoderCatalog.cs` の `nvd3d11h264enc` 定義の doc コメントも同じ食い違いを記している）。決着させる手順: NVIDIA 機で、`video/x-raw(memory:SystemMemory)` の capsfilter を外した形と現行の形を比べ、`gst.encoder selected` と実際の交渉結果（`.dot`）を見る。あわせて `gst-inspect-1.0 nvd3d11h264enc` の sink caps を確認し、D3D12Memory を直接受けるなら `NeedsSystemMemory: false`（`d3d12download` すら不要）が正しくなる。
- スクリプトは実在する要素からしかケースを作らないため、無い要素は黙って検証されない（走らなかったことは FAILED としては現れない）。意図した要素が本当に検証されたかは、レポートのケース一覧と冒頭の `All H.264 encoders gst-inspect reports` 行で確認する（どちらもレポート内にあり、手で `gst-inspect` を叩く必要はない）。
