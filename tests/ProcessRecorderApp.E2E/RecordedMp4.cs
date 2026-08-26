using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// 「録画結果として受け入れられる MP4」の共通判定。
///
/// <para>
/// <b>妥当性だけを見てはいけない。</b> 録画が I フレーム以外から始まっていても
/// <c>ftyp</c>/<c>moov</c>/<c>mdat</c>/<c>avcC</c> は揃い、尺も入るので
/// 構造検査は通ってしまう（<c>h264parse config-interval=-1</c> が全 IDR の前に
/// パラメータセットを入れ直すため、なおさら「壊れていないように見える」）。
/// 実際、<c>PushRecordBuffer</c> の I フレーム待ちを外す注入は
/// <see cref="Mp4Probe.StartsOnASyncSample"/> を足すまで1件も検出できなかった。
/// </para>
/// <para>
/// <b>chunked（<c>faststart</c>）と fragmented のどちらでも使える。</b>
/// <see cref="Mp4Probe"/> が <c>moov</c> の <c>mvex</c> で形を見分け、fragmented では
/// <c>moof</c> の <c>trun</c> から尺・サンプル数・先頭サンプルの同期性を読む
/// ── <b>したがって呼び出し側は <c>FragmentedOutput</c> を明示しなくてよい</b>
/// （明示するのは chunked 固有の性質を見るテストだけ）。
/// </para>
/// <para>
/// <b>書き込み中のファイルには使わない。</b> 対象は停止・終了で確定したファイルだけで、
/// 追いかけ中の本文は <c>Fmp4Probe</c> が見る。
/// </para>
/// </summary>
public static class RecordedMp4
{
    /// <summary>録画ファイルとして成立していることを表明し、調べた内容を返す。</summary>
    public static Mp4Probe AssertUsable(string path, AppInstance instance, ITestOutputHelper? output = null)
    {
        var probe = Mp4File.Probe(path);
        output?.WriteLine(probe.ToString());

        Assert.True(probe.IsValid,
            "MP4 として成立していません: " + probe + Environment.NewLine + instance.DiagnosticDump());

        Assert.True(probe.StartsOnASyncSample,
            "録画がキーフレーム以外から始まっています（先頭が参照フレームの無いスライス）: " +
            probe + Environment.NewLine + instance.DiagnosticDump());

        return probe;
    }
}
