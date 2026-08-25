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
- `Short recordings` 節（尺不足）── 生成尺が録画窓より短いケースの一覧。原因はほぼ GOP 長であってフレーム落ちではない: 録画は最初の I フレームから始まるため、リングバッファ（`BufferDuration`。このスクリプトは 10000ms で走らせ、レポートにもその値が明記される）内に I フレームが1枚も無いと事前バッファが丸ごと捨てられる。このためカタログは GOP 長を**実際のフレームレートから 2 秒で逆算**して付ける（`EncoderCatalog.TargetKeyframeIntervalSeconds`。このスクリプトの 15fps のソースなら `gop-size=30`、既定の 30fps なら 60）。**フレーム数を固定すると低いレートで間隔が伸び切る** ── 実測では 5fps の常時録画枝が 12 秒間隔になり、セグメントが 2 倍に伸びた。例外が1つ: `PreferredH264Encoder` にカタログ外のファクトリ名を指定した場合、実機に在れば「ファクトリ名のみ・プロパティ無し」の候補として先頭に挿される（`EncoderCatalog.Resolve`）ため `gop-size` が付かず、ベンダ既定の長い GOP で走って事前バッファ消失が静かに再発しうる ── `selected encoder` にプロパティ無しの名前が出ていたら尺を必ず確認する。この指標は GOP の位相次第で同一構成でも数秒ばらつくノイズの多いものなので、合否判定には使わない。GPU 無しの WARP では正常でも出るが、GPU 実機で出たら調査対象。
- 採用された `LaunchString` を見て、`EncoderCatalog` の実機未確認エンコーダー（現在プロパティ無し）に確認済みのプロパティを追加できるか判断する。単位（kbit/sec か bit/sec か）は `gst-inspect-1.0.exe <要素名>` で必ず確認する。
- レポート冒頭の `All H.264 encoders gst-inspect reports` は実機の全列挙で、カタログが知らない要素が在れば `This machine has H.264 encoders the catalog does not know about` の警告が自動で入る（この向きの検査だけがカタログ漏れを検出できる）。逆向き ── カタログに在るのに実機に無い要素 ── はケースが作られないだけなので、意図した要素が検証されたかはケース一覧とこの行で確認する。手で `gst-inspect` を叩き直す必要はない。
- スクリプトは同梱ランタイムが実際に使われたかも検査し、PATH 上の別 GStreamer が先に見つかった場合はレポート冒頭に警告が入る（その場合の結果は別ランタイムについてのもの）。

## Verify-HighResolution.ps1

`tools/Verify-HighResolution.ps1`。高解像度で「`IsInitialized=on` / `LastError=null` なのに録画もプレビューも1フレームも出ない」循環待ちの回帰検証。流儀は `Verify-GpuEncoders.ps1` と同じ（無人・ASCII のみ・`System.Diagnostics.Process` 経由・`PROCESSRECORDERAPP_DATA_DIR` と `PROCESSRECORDERAPP_KEY_PREFIX` で完全隔離）。レポートは `high-resolution-report.md`、全ケース期待どおりなら終了コード 0。

守っている不変条件: プレビュー分岐の `queue` が既定の `max-size-bytes`（10485760）のままだと、このバイト上限は高解像度で「フレーム数の上限」に化ける ── queue は上限を超えていても1件目は必ず受け取るので、1 フレームが 5242880 バイト（上限の半分）を超えると常に 1 フレームしか保持できない。NV12/I420（幅×高さ×1.5 バイト）では約 3.5Mpx ＝ 2560x1440 以上がこの領域で、1440p の 1 フレーム（約 5.5MB）は上限 10MB を下回るのに該当し、4K（約 12.4MB）は上限そのものも超える。PAUSED 中はプレビュー appsink が preroll でブロックしているので queue は排出されず、満杯の queue が tee を塞ぎ、エンコーダーが枯渇し、録画 appsink が preroll せず、パイプラインは PLAYING に到達しない ── 循環待ち。対策は2点で、(1) プレビュー queue を leaky かつバイト・時間無制限にする、(2) 初期化は `SetState` の ASYNC 返答を成功扱いせず実際に PLAYING へ達するのを待つ。

ケースは11件。前半6件は上記の循環待ちの回帰検証: 4K 画面キャプチャ構成（`d3d12screencapturesrc monitor-index=<n>`・`qsvh264enc rate-control=icq icq-quality=30 gop-size=60`・`BufferDuration=10000`）、`d3d12testsrc` による解像度スイープ（320x240 / 1920x1080 / 2560x1440 / 3840x2160 ── 1920x1080 以下は閾値未満、2560x1440 以上が「1 フレーム ＞ 5242880 バイト」の循環待ちの領域。スクリプト自身もこの式で各行に注記を付ける）、4K での自動選択（停止した候補が受理されず棄却され、次の候補が試されること）。スイープが `d3d12screencapturesrc` ではなく `d3d12testsrc` なのは、結果をその機械のモニタ構成に依存させないため。エンコーダー行はカタログと `EncoderCatalogScriptSyncTests` で結び付けてあり、カタログを変えるとテストが落ちてこの行の更新判断を迫る。

### 常時録画の 5 件（`tee` の枝が 3 本になったぶん）

**上の実測はすべて枝が 2 本のときのもの**なので、常時録画（`ContinuousRecording`）を入れた構成では取り直しが要る。追加ケースは、`d3d12testsrc` の 1920x1080 / 2560x1440 / 3840x2160 に常時録画を足したもの（同じフレームレート）、4K のイベント録画に 5fps・1280x720 の常時枝を足したもの（`videorate` ＋ スケーラーを通す、実運用で想定している形）、そして画面キャプチャ構成に 5fps の常時枝を足したもの。

見たいのは3つ:

- 3 本目の消費者が増えたことで、**録画側 appsink が `PlayingStateTimeoutMs` 内に preroll できなくなっていないか**（`never reached PLAYING` が出ないこと）。
- **常時枝が詰まってもパイプライン全体を道連れにしないこと**（枝の `queue` は `leaky=downstream`、`appsink` は `async=false`。設計どおりなら道連れにならない）。
- 4K で **2 本目のエンコーダーを作れるか**（GPU のセッション数・メモリの上限に当たらないか）。

常時録画の合否はイベント録画とは**別に**判定する（どちらが壊れたのか分かるように）。条件は `recorder.continuous-init ok` がちょうど1件・`... fail` が0件・**閉じたセグメントが1本以上**・閉じたセグメントがすべて構造的に妥当・`continuous.error` / `continuous.leak` / `continuous.overshoot` が0件。**ワーカーを kill した時点で開いていたセグメントは未確定で当たり前**なので数えない（CLI に正常終了のコマンドが無いため）。レポートの表には `segments (closed/bad)` の列が増える。

常時録画のセグメントは `R1_c00000.mp4` のように名前で分かれるので、イベント録画のファイルとは名前で数え分けている。

判定は `recorder.init ok` / `recorder.init fail` / `never reached PLAYING` の署名の有無と MP4 の構造で行う ── 製品自身がこの署名を **`activity.log`** に出すので、`.dot` ファイルを読む必要はない。スクリプトが署名を読むのも `activity.log` であって `debug.log` ではない（`Verify-GpuEncoders.ps1` が `gst.encoder` 行を読むのは `debug.log` 側 ── 2つのスクリプトで読むログが違う）。赤い実行を手で調べるときも作業ディレクトリの `activity.log` を見る。

この往復で最も無駄になりやすいのは「古いバイナリで走らせてしまうこと」。そのため発行物にリテラル文字列の目印が入っているかを先に検査し、無ければ警告して終了コード 1 を返す。目印は2つ ── `leaky=downstream`（循環待ちの修正）と `max-size-buffers=8`（常時録画の枝の queue。この機能が入ったビルドにしか無い）。

この検査には罠が2つある。**(1) UTF-16 の境界**: アセンブリ中の文字列は偶数バイト境界に無いことがあるため、`Encoding.Unicode.GetString` をオフセット 0 だけに掛けると符号単位が1バイトずれて1件も一致せず、正しい発行物を「古いビルド」と誤報する。オフセット 0 と 1 の両方を試すこと（素の `grep` も ASCII として探すため UTF-16 には一致しない）。**(2) 発行の形**: 目印は非 AOT では `GStreamer.dll` に、**Native AOT ではネイティブイメージ（`ProcessRecorderApp.exe`）に**入る ── AOT 発行物には `GStreamer.dll` そのものが存在しないので、そちらだけを見ると**配布の既定形（AOT）では必ず「古いビルド」と誤報する**。両方を探すこと。実測で確認済み。

`-SmokeTest` は GPU の無い機械で「スクリプト自身」を検証するモードで、本件の回帰検証にはならない。緑の経路（`videotestsrc`）と赤の経路（`identity drop-probability=1.0` ── caps は通してバッファだけ捨てるので、リンクと状態遷移は成功するのに PLAYING へ達しない）の両方を回す。緑だけを回しても、停止を検出できないスクリプトは緑になるため。赤側の合格条件は「`never reached PLAYING` が 1 件以上・`recorder.init fail` がちょうど 1 件・`recorder.init ok` が 0 件」── CLI の終了コードは記録されるだけで、この判定には入らない。

パラメータ: `-PublishDir` / `-WorkDir` / `-RecordSeconds`（既定 4）/ `-MonitorIndex`（既定 1。製品側の `monitor-index` の既定は `0` なので、モニタが1台の機械では既定のまま流すと画面キャプチャのケースが範囲外の index になる ── 実機のモニタ構成に合わせて指定する）/ `-SmokeTest` / `-KeepWorkDir`。


### 本線のフレームレートが落ちる件の 3 件

常時録画に**フレームレート制限**（`ContinuousFramerate`）を掛けると、**本線（イベント録画）の
実 fps が落ちる**という報告がある（1920x1080@30 のカメラで、本線の MP4 が 12fps 程度）。
制限を切ると本線は 30fps 弱に戻る。

**交渉された caps では分からない。** 実測した `.dot` では本線・プレビューとも `30/1` のままで、
落ちているのは実際に入ったフレーム数の方である。そこでレポートには
**`event fps` の列**（`stsz` のフレーム数 ÷ `mvhd` の duration）を出す。

3 件は**常時録画の設定だけが違う**（ソースもイベント側のエンコーダーも同じ）:

| 行 | 常時録画 | 枝の中身 |
|---|---|---|
| `fps: ... continuous OFF (baseline)` | 無効 | ── |
| `fps: ... continuous ON, no framerate override` | 有効・解像度のみ | `d3d12convert` |
| `fps: ... continuous ON at 5fps (videorate)` | 有効・5fps＋解像度 | `videorate` ＋ `d3d12convert` |

読み方: 3 行の `event fps` が揃っていれば再現していない。**2 行目が揃っていて 3 行目だけ落ちるなら
`videorate` そのものが原因**（枝が増えたことでも 2 本目のエンコーダーでもない ── 2 行目の方が
エンコード量は多いのに落ちないため）。

#### これまでの実測（GPU 実機）

**1 巡目・2 巡目とも再現しなかった。** `event fps` は全行 30fps ちょうど:

| 変えた要因 | 結果 |
|---|---|
| 常時録画 OFF（基準） | 30 |
| 常時録画 ON・制限なし | 30 |
| 常時録画 ON・5fps（`videorate`） | 30 |
| 上に加えて常時側も `qsvh264enc`（QSV 2 セッション） | 30 |
| ソースをシステムメモリにする（`d3d12upload` を通す） | 30 |
| システムメモリ ＋ `videorate` ＋ QSV 2 セッション | 30 |
| 録画窓を 20 秒へ | 30 |

したがって **`videorate` を足しただけでは本線は落ちない。** アップロード経路も
QSV のセッション競合も単独では原因ではない。

#### 3 巡目（`-CameraName` を渡したときだけ走る）

合成ソースで出尽くしたので、報告された構成に残る差は 2 つ:

- **ソースが実物のカメラ**（`mfvideosrc`。1080p30 の生 NV12 は USB2 の帯域を超えるので、
  MF が MJPEG を展開している）
- **レコーダーが 2 台同時**で、どちらも常時録画が有効（イベント 2 ＋ 常時 2 ＝ 4 セッション）

4 行を足す ── カメラ単独の基準、カメラ ＋ 制限なし（報告では動く方）、
カメラ ＋ 5fps（報告では落ちる方）、そして**カメラ ＋ 画面キャプチャの 2 台同時**。
`event fps` は R1（カメラ）のものを採る。

```powershell
.\tools\Verify-HighResolution.ps1 -PublishDir <発行物> -MonitorIndex 2 -CameraName 'HD Pro Webcam C920'
```

**カメラ単独の基準が既に 30 を割っているなら、カメラ自体が天井**であり常時録画とは無関係。
2 台同時の行だけが落ちるなら、原因は**同時に走るセッション数**であって `videorate` ではない。

#### 3 巡目で再現した（実カメラ）── そして 4 巡目へ

| 行 | 本線 fps |
|---|---|
| カメラ単独・常時録画なし | 29.9 |
| カメラ ＋ 常時録画（制限なし・960x540 の縮小あり） | 29.9 |
| **カメラ ＋ 常時録画 5fps** | **11.9** |
| カメラ ＋ 画面キャプチャの 2 台同時、両方 5fps | 11.7 |

差は **`videorate` の 1 要因だけ**である。縮小だけなら 29.9fps のまま（下流の仕事は
むしろ多い）。**合成ソースでは出ない** ── 実カメラと `d3d12screencapturesrc` でだけ出る。

**外れた仮説と、それでも残した変更。** 「既定の `videorate` が次のフレームまで
前のバッファを保持し、`tee` の手前のプールを掴む」と見て `drop-only=true` を入れたが、
**12.4fps で変わらなかった**。`drop-only=true` 自体は残している ── 常時録画は下げる
方向にしか使わないので複製は要らず、ソースが止まったとき複製フレームを書庫へ入れる
方が困るため。**fps の件は未解決である。**

4 巡目（`-CameraName` を渡すと走る）は「`videorate` の存在そのものか、レートを
下げることか」を分ける:

| 行 | 見たいこと |
|---|---|
| 常時 `30/1`（ソースと同じ） | `videorate` は入るが変換しない。**落ちるなら要素の存在自体が引き金** |
| 常時 `15/1` | 落ち幅がレート比に追随するか |
| 常時 `5/1`・縮小なし | スケーラーが噛んでいないか |
| 常時 `5/1` ＋ `GST_DEBUG=videorate:5,queue:4,mfvideosrc:4` | `videorate` の drop と `queue` の詰まりを `debug.log` から読む |

最後の行のログが本命 ── **フレームがどこで消えているか**（ソースが出していないのか、
どこかの `queue` が詰まっているのか）が直接見える。

#### 決着 ── `appsink` の `sync=true` だった

4 巡目で閾値が見えた:

| 常時録画 | 本線 fps |
|---|---|
| `30/1`（`videorate` は入るが変換しない） | 29.9 |
| `15/1` | 29.9 |
| `5/1`・縮小なし | **12.3** |
| `5/1` ＋ 縮小 | **12.3** |

**`videorate` の存在自体は無関係**（30/1 で入っていても落ちない）。**スケーラーも無関係**。
**比例ではなく閾値**である（15 は平気で 5 で壊れる）。

決め手は利用者の観察 ── **`d3d12testsrc` でもプレビューだけフレームレートが落ちて見える**。
合成ソースでは録画側の MP4 は 30fps を保つので、**録画物の fps だけを見ていると当たらない**。
「ソースが出していない」では説明できず、**シンクが待たされている**形だと分かった。

原因: 取り出す側の `appsink` が既定の `sync=true` のままだった。GStreamer は
**枝のどれか 1 本が申告した latency をパイプライン全体へ設定する**。常時枝を 5fps に
すると 1 フレームが 200ms になり、枝のエンコーダーの申告が大きくなる。15fps なら
1 フレームが 3 分の 1 なので出ない。

録画も常時録画も取り出した AU に自分で PTS を付け直して mux するので、`appsink` が
クロックを待つ理由は無い。3 つとも `sync=false` にして解消（利用者の実機で確認）。
`ContinuousBranchTests.EveryAppsinkWePullOurselves_DoesNotSyncToTheClock` が固定する。

**開発機では再現できない。** GPU が無い機械（WARP）では、`videotestsrc` → `d3d12upload` の形でも
`d3d12testsrc` でも、`videorate` の有無で throughput に差が出ないことを実測済み
（640x360 で 24.2 / 24.4 / 25.2 fps、1920x1080 ではパイプライン自体が 7.3fps で頭打ち）。
GPU 実機でしか切り分けられない。

なお `.dot` で見えていた直接の詰まりは、**イベント枝の `queue` が既定の `max-size-bytes`
（10MB）を超えて満杯**になっていたこと（1080p NV12 は 1 フレーム約 3.1MB なので 4 枚で超える）。
これは設計どおりの背圧で、律速はその下流（エンコーダー）にある。

### 走らせ方（常時録画を含む）

GPU 実機に**発行物一式**（`dotnet publish -p:PublishProfile=win-x64-aot` の出力）とこのリポジトリの `tools/` を持ち込み、PowerShell で:

```powershell
# 1. まずハーネス自身を確かめる（GPU 不要・1分程度）。3 件すべて OK で終了コード 0 になること。
.\tools\Verify-HighResolution.ps1 -SmokeTest -PublishDir <発行ディレクトリ>

# 2. 本番。11 ケース。既定の録画窓 4 秒＋常時録画の待ちで 10〜15 分程度。
.\tools\Verify-HighResolution.ps1 -PublishDir <発行ディレクトリ> -MonitorIndex 1
```

- 冒頭の `Fixed build : YES` と `Continuous : YES` を必ず確認する。どちらかが NO なら、それ以降の結果は無意味（古いバイナリ）。
- 終了コード 0 なら全ケース期待どおり。1 なら `FAILED cases: N` と作業ディレクトリのパスが出るので、そのディレクトリごと持ち帰る（`activity.log` と `debug.log` が入っている）。
- レポートは `high-resolution-report.md`。全ケース OK のときは `%TEMP%` にコピーして作業ディレクトリを消すので、**レポートだけ持ち帰れば足りる**。
- `-MonitorIndex` は画面キャプチャの 2 ケースだけに効く。範囲外を指定すると状態遷移が失敗して `IsInitialized=false` になるので、他の失敗と区別が付く。
- セグメント長は `-ContinuousSegmentSeconds`（既定 5 秒）。
- **走行時間を決めるのは `-ContinuousMinSegments`（既定 2）** ── 常時録画のケースは
  「この本数のセグメントができるまで」待つ。既定の 2 は**分割が 1 回起きることしか見ていない**。
  ローテーションを繰り返させたいときはこちらを上げる:

  ```powershell
  # 常時録画の各ケースで 20 本回す（各ケース ＋約 100 秒。全体で 10 分ほど延びる）
  .\tools\Verify-HighResolution.ps1 -PublishDir <発行ディレクトリ> -MonitorIndex 1 `
      -ContinuousMinSegments 20 -ContinuousWaitSeconds 180
  ```

- `-ContinuousWaitSeconds`（既定 45）は**その待ちの上限**であって走行時間ではない。
  上限に達しても要求本数に届かなかったケースは**失敗**になり、レポートにその旨が出る
  ── 短く終わった長回しが緑に見えないようにするため。本数を上げたら上限も一緒に上げること
  （目安: `本数 × セグメント長 + 30 秒`）。
## 未検証の項目

検証済みの範囲（対になる事実）:

**両形態とも** ── 同梱ランタイムを NVIDIA/Intel の実機で流した `Verify-GpuEncoders.ps1` は
全ケース OK（`retries` も全件 0）。GPU 専用候補（`d3d12h264enc` / `qsvh264enc` /
`nvd3d11h264enc` / `nvh264enc` / `nvautogpuh264enc`）が選ばれて有効な MP4 を出すこと、
`System` の自動選択が `mfh264enc` を選んで有効な MP4 を出すことまで観測済み。
**同梱物が実際に当たっていること**もレポート冒頭の読み込み元
（`<発行ディレクトリ>/runtimes/win-x64/bin`）で確認している。

**MSVC 版だけ** ── `capture-api` を実機で確認済み。開発機で取れているのは
`gst-launch-1.0` の `d3d12screencapturesrc capture-api=dxgi` / `=wgc` が
それぞれ 60 フレーム・EOS・終了コード 0 まで（WARP ＋ RDP。`wgc` の方が立ち上がりが遅く、
同じ 60 フレームに約 5 秒かかる）で、**そこから先 ── WGC とエンコーダーを繋いだ経路 ──
は実機で確認した**。`Verify-GpuEncoders.ps1` は `capture-api` を触らない
（既定の `dxgi` で回る）ので、上の全ケース OK はこの経路を含まない。

**MinGW 版だけ** ── **GOP の逆算を実機で見ている**（15fps のソースで `gop-size=30`、
5fps の常時枝で `gop-size=10`、手動指定は文字列のまま＝`gop-size=60`）。
`Verify-HighResolution.ps1`（26 ケース全件 OK・`continuous.overshoot` 無し）も
MinGW 版で流したもの。

残っているのは以下。

- **VC++ 再頒布可能パッケージが無い機械での MSVC 版 ── 未検証。** 実機検証を流した機械には CRT が在ったので、この前提が欠けたときの挙動は観測できていない（`docs/coverage-gaps.md` を参照）。
- `amfh264enc`（AMD）── AMD GPU の機械が無く未検証（MinGW 版・MSVC 版のどちらでも同じ。実機のレポートでは両方とも `missing=[amfh264enc,openh264enc,x264enc]`）。AMD 機で `tools/Verify-GpuEncoders.ps1` を1回流せばケースは自動生成される。カタログに実在しない名前を書いても例外も警告も出ずに候補から黙って消え、録画は他候補で成立してしまうため、「録画できている」ことは名前が正しい証拠にならない ── レポート冒頭の `All H.264 encoders gst-inspect reports` 行に `amfh264enc` が載り、専用ケースが OK になっていることまで確認する。確認済みのプロパティをカタログへ足すときは `EncoderCatalogScriptSyncTests` が落ちてスクリプト側の一覧の追随を迫るので、一緒に直す。
- （解決済み）`nvd3d11h264enc` のメモリ交渉 ── `tools/Verify-NvD3d11Memory.ps1` を NVIDIA 実機で流して決着した。sink caps は `video/x-raw(memory:D3D11Memory)` と素の `video/x-raw` だけで **D3D12Memory は受けない**（download は必須）。一方で、`video/x-raw(memory:SystemMemory)` の capsfilter を外すと **`d3d12download` の src もエンコーダーの sink も `memory:D3D11Memory` で折り合う** ── つまり CPU 往復を強いていたのはこちらの capsfilter であり、現行の形は `d3d12download ! videoconvert !` にしてある（`videoconvert` の caps は `video/x-raw(ANY)` なので交渉を妨げない）。**この形で NVIDIA/Intel 実機の全ケースが OK であることまで確認済み** ── `nvd3d11h264enc` / `nvh264enc` / `nvautogpuh264enc` の専用ケースがいずれも選ばれ、`retries` 0 で有効な MP4 を出す。
- **DASH プレビューの第 2 パイプラインを実 GPU のエンコーダー候補で流すこと ── 未検証（手動）。** L2 の `DashPreviewTests` が通すのは、その機械で `EncoderCatalog` の先頭に来た候補 1 つだけである。GPU 機では Web UI の画質切替を `dash` にして絵が出ること、`activity.log` の `dash.stream-start` の `encoder=` が期待した GPU 候補になっていること、`dash.stream-error` が出ていないことを 1 度目で確かめる。
- スクリプトは実在する要素からしかケースを作らないため、無い要素は黙って検証されない（走らなかったことは FAILED としては現れない）。意図した要素が本当に検証されたかは、レポートのケース一覧と冒頭の `All H.264 encoders gst-inspect reports` 行で確認する（どちらもレポート内にあり、手で `gst-inspect` を叩く必要はない）。
