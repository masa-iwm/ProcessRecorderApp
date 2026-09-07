using Xunit;

// L2 のテストは1件ごとに常駐ワーカー（GStreamer を初期化する実プロセス）を起動する。
// 並列に走らせると N 本の GStreamer ワーカーが CPU を奪い合い、録画の尺が揺れて
// 「製品の不具合」に見える形で現れる ── 直列化の理由はこの CPU 競合だけである。
// **キー接頭辞の衝突は理由にならない**: AppInstance の KeyPrefix と DataDir は
// 起動ごとの GUID で、RemoteControlPort もどの E2E の settings でも 0（自動割り当て）なので、
// 別インスタンスへコマンドが転送されることはない。
// プロセスを分けた並走は tools/Run-E2E.ps1 の -Parallel で行う（伸びの実測はそこに書いてある）。
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
