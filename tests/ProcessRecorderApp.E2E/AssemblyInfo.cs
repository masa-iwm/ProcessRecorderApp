using Xunit;

// L2 のテストは1件ごとに常駐ワーカー（GStreamer を初期化する実プロセス）を起動する。
// 並列に走らせると
//   - 同じキー接頭辞ならコマンドが他方のワーカーへ転送され、
//   - 別のキー接頭辞なら N 本の GStreamer ワーカーが CPU を奪い合って録画の尺が揺れる。
// どちらも「製品の不具合」に見える形で現れるため、アセンブリ全体で直列化する。
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
