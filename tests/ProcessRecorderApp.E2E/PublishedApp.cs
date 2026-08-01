using System.Diagnostics;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// 発行済みアプリの場所を解決し、GStreamer のプラグインレジストリを1度だけ温める
/// アセンブリ共有のフィクスチャ。
///
/// <para>
/// <b>ウォームアップは必須。</b> 常駐ワーカーの<b>初回</b>起動は GStreamer が
/// プラグインレジストリを構築するため 10〜30 秒かかる（実測）。ランチャーの
/// コールドスタート経路はこれを吸収する長さを持つ（登録完了待ち 120 秒。
/// <c>StartResidentWorkerAndWaitForRegistration</c>）ので製品側は落ちないが、
/// 温めずに走ると<b>最初にワーカーを起こしたテストが構築コストを自分の予算の中で払い</b>、
/// タイミングを前提にした表明が歪む。そこでこのフィクスチャが使い捨てのインスタンスで
/// <b>アセンブリで1回だけ</b>温める。ワーカーを直接起動するのは、後始末で確実に落とせ、
/// 標準エラーへ複写される activity.log を診断用に捕捉できるため
/// （docs/test-harness.md「L2（E2E・CLI）基盤の外せない設計」）。
/// </para>
/// </summary>
public sealed class PublishedApp : IDisposable
{
    /// <summary>発行ディレクトリを上書きする環境変数（CI が AOT 発行物へ向けるために使う）。</summary>
    public const string PublishDirVariable = "PROCESSRECORDERAPP_E2E_PUBLISH_DIR";

    /// <summary>ウォームアップに与える全体の制限時間。レジストリ構築は実測で 10〜30 秒。</summary>
    private static readonly TimeSpan WarmupBudget = TimeSpan.FromMinutes(3);

    public string PublishDir { get; }
    public string ExePath { get; }

    /// <summary>ウォームアップの所要と試行回数（失敗時の診断に出す）。</summary>
    public string WarmupSummary { get; private set; } = "(not warmed)";

    public PublishedApp()
    {
        PublishDir = ResolvePublishDir();
        ExePath = Path.Combine(PublishDir, "ProcessRecorderApp.exe");

        if (!File.Exists(ExePath))
        {
            throw new InvalidOperationException(
                $"発行物が見つかりません: '{ExePath}'。" + Environment.NewLine +
                "  dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-selfcontained" + Environment.NewLine +
                $"を実行するか、環境変数 {PublishDirVariable} で発行ディレクトリを指定してください。");
        }

        Warmup();
    }

    /// <summary>
    /// 発行ディレクトリの解決。ビルド時に埋め込んだリポジトリルート基準で、
    /// 環境変数があればそちらを優先する。
    /// <b><see cref="AppContext.BaseDirectory"/> からの相対パスでは書かない</b>
    /// ── 出力ディレクトリの深さ（構成・TFM・RID）が変わった瞬間に壊れ、
    /// ローカルで緑のまま CI だけ赤になる。
    /// </summary>
    private static string ResolvePublishDir()
    {
        string? overrideValue = Environment.GetEnvironmentVariable(PublishDirVariable);
        if (!string.IsNullOrWhiteSpace(overrideValue))
            return Path.GetFullPath(overrideValue.Trim());

        return Path.GetFullPath(Path.Combine(
            RepositoryLayout.Root,
            "src", "ProcessRecorderApp", "bin", "Release", "win-x64", "publish", "selfcontained"));
    }

    /// <summary>
    /// 使い捨てのデータディレクトリとキー接頭辞で常駐ワーカーを1度起動し、
    /// <c>ping</c> が 0 を返すまで待ってから落とす。
    /// レジストリは（データディレクトリではなく）ユーザー単位のキャッシュに作られるため、
    /// 以降のテストが起動する全ワーカーがこの1回の恩恵を受ける。
    /// </summary>
    private void Warmup()
    {
        var sw = Stopwatch.StartNew();
        using var instance = AppInstance.Create(this, new SettingsFile(), startWorker: false);

        // ウォームアップ自身の ping には成功を要求しない（初回はレジストリ構築の 10〜30 秒を必ず踏む）。
        // 「いつか 0 を返す」ことだけを長い締め切りで待つ。
        int attempts = instance.StartWorkerAndWaitUntilReady(WarmupBudget);

        sw.Stop();
        WarmupSummary = $"{sw.Elapsed.TotalSeconds:F1}s / ping {attempts} 回";
    }

    public void Dispose() { }
}
