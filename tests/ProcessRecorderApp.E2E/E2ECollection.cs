using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L2 の全テストクラスが属する唯一のコレクション。
/// <see cref="PublishedApp"/>（発行物の解決と GStreamer レジストリのウォームアップ）を共有し、
/// かつ並列実行を止める ── 常駐ワーカーを同時に複数起動すると、
/// 録画の尺が CPU 競合で揺れて「製品の不具合」に見える形で落ちる。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2ECollection : ICollectionFixture<PublishedApp>
{
    public const string Name = "ProcessRecorderApp E2E";
}
