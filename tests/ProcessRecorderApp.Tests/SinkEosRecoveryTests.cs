using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>sink バスの EOS が自動復帰を予約する形のままであること</b>を、
/// ソースをテキストとして固定する。
///
/// <para>
/// <b>EOS だけを出して黙るソースがある。</b> 画面キャプチャの WGC 経路
/// （<c>capture-api=wgc</c>）は、ディスプレイを切断しても Error を一度も出さず
/// sink バスへ EOS を流して終わる。ここの予約が消えると <c>_sinkSawEos</c> の印が
/// 立つだけで誰も読まないまま終わり、<b>WGC の画面キャプチャで復帰が丸ごと効かなくなる</b>
/// ── 連鎖が張られないので、デバイス到着の監視（<c>device.watch</c>）も張られない。
/// </para>
/// <para>
/// <b>外しても他は何も赤くならない。</b> DXGI 経路は切断時に
/// <c>Internal data stream error</c> を出すので Error 分岐だけで復帰でき、
/// WGC の切断は<b>実機とディスプレイの抜き差しが要る</b>ので自動では踏めない
/// （docs/coverage-gaps.md「デバイス到着の監視」）。つまりこの退行は、
/// この検査以外の誰も気付けない。
/// </para>
/// </summary>
public class SinkEosRecoveryTests
{
    private static string EventRecorderSource =>
        File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "EventRecorder.cs"));

    private const string HandleBusMessageSignature =
        "private void HandleBusMessage(Message msg, string busName, BusThrottles throttles)";

    /// <summary>
    /// <c>case MessageType.Eos:</c> から先だけを切り出す。
    ///
    /// <para>
    /// <b>メソッド全体を見てはいけない。</b> Error 分岐にも <c>ScheduleRestart(elementName)</c> が
    /// あり（あちらは種別で絞らない ── 何かが壊れた印なので、ソースの種類によらず復帰を試す）、
    /// switch では Error が Eos より前に来る。全体に対する <c>Contains</c> は
    /// <b>EOS 側の予約を消しても緑のまま通ってしまう</b>。
    /// </para>
    /// </summary>
    private static string EosBranch()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, HandleBusMessageSignature);

        int eos = SourceMethodBody.IndexOfCode(body, "case MessageType.Eos:");
        Assert.True(eos >= 0,
            "HandleBusMessage の EOS 分岐が見つからない（switch の形を変えた可能性がある）。"
            + "変えたなら、この検査も一緒に直すこと。");

        return body[eos..];
    }

    /// <summary>
    /// <c>case MessageType.Error:</c> から次の <c>case</c> の手前までを切り出す。
    /// </summary>
    private static string ErrorBranch()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, HandleBusMessageSignature);

        int error = SourceMethodBody.IndexOfCode(body, "case MessageType.Error:");
        Assert.True(error >= 0,
            "HandleBusMessage の Error 分岐が見つからない（switch の形を変えた可能性がある）。"
            + "変えたなら、この検査も一緒に直すこと。");

        int next = SourceMethodBody.IndexOfCode(body[error..], "case MessageType.Warning:");
        Assert.True(next >= 0,
            "Error 分岐の終わり（case MessageType.Warning:）が見つからない。"
            + "並びを変えたなら、この検査も一緒に直すこと。");

        return body.Substring(error, next);
    }

    /// <summary>
    /// <b>sink バスの Error ハンドラが状態遷移を起こさないこと。</b>
    ///
    /// <para>
    /// basesrc は flow error のあと、Error を post したのと<b>同じ</b>ストリーミング
    /// スレッドから EOS を押す（post → push の順）。ハンドラの中で（別スレッドへ
    /// 逃がす場合も含めて）その要素を <c>Ready</c> へ落とすと、pad が deactivate＝
    /// flushing になり <b>EOS が捨てられて bus に届かない</b> ── <c>_sinkSawEos</c> が
    /// 立たないまま要素単位の再開が <c>result=ok</c> を返し、作り直しへ進まなくなる。
    /// </para>
    /// <para>
    /// <b>速い機械では踏めない。</b> 手元（Error→EOS が 1〜5 ms）では 15/15 再現せず、
    /// 2 vCPU の CI でだけ決定的に先行した。実行で縛れる形が無いので、ソースで固定する。
    /// </para>
    /// </summary>
    [Fact]
    public void TheSinkErrorHandler_DoesNotChangeTheElementState()
    {
        string branch = ErrorBranch();

        Assert.False(SourceMethodBody.ContainsCode(branch, "SetState(State.Ready)"),
            "sink バスの Error ハンドラが要素を Ready へ落としている。"
            + Environment.NewLine
            + "**この後に basesrc が押す EOS が flushing で捨てられる** ──"
            + Environment.NewLine
            + "bus に EOS が来ないので _sinkSawEos が立たず、要素単位の再開が"
            + Environment.NewLine
            + "result=ok を返して作り直し（reason=eos）へ進まなくなる。"
            + Environment.NewLine
            + "戻すのは復帰試行（RestartSinkSrc）の側で、あちらはプールスレッドで走る。");

        Assert.False(SourceMethodBody.ContainsCode(branch, "RestartSinkSrc()"),
            "sink バスの Error ハンドラが要素単位の再開をその場で呼んでいる。"
            + Environment.NewLine
            + "ここは壊れた要素自身のストリーミングスレッドなので、"
            + Environment.NewLine
            + "自スレッドの復帰を待って固まるうえ、EOS も flushing で消える。");

        Assert.True(SourceMethodBody.ContainsCode(branch, "_errorSinkSrc = erroredSource;"),
            "障害要素の控え（_errorSinkSrc）が無くなっている。"
            + Environment.NewLine
            + "控えないと復帰試行は対象なしの false になり、要素単位の再開が丸ごと効かない。");
    }

    /// <summary>
    /// sink バスの EOS が復帰を予約すること。順序も見る ──
    /// 印（<c>_sinkSawEos</c>）は予約より<b>前</b>に立てなければならない。
    /// </summary>
    [Fact]
    public void TheSinkEos_SchedulesARestart()
    {
        string branch = EosBranch();

        int scheduled = SourceMethodBody.IndexOfCode(branch, "ScheduleRestart(elementName)");
        Assert.True(scheduled >= 0,
            "sink バスの EOS が復帰を予約しなくなっている。"
            + Environment.NewLine
            + "**WGC の画面キャプチャ（capture-api=wgc）で復帰が丸ごと効かなくなる** ──"
            + Environment.NewLine
            + "あの経路は切断で Error を出さず EOS だけを出すので、予約する者が他に居ない。"
            + Environment.NewLine
            + "印が立つだけで誰も読まないまま終わり、デバイス到着の監視も張られない。"
            + Environment.NewLine
            + "DXGI では Error 分岐が拾うので、他のテストは 1 つも赤くならない。");

        int flag = SourceMethodBody.IndexOfCode(branch, "_sinkSawEos = true;");
        Assert.True(flag >= 0,
            "sink バスの EOS の印（_sinkSawEos = true;）が見つからない。"
            + "改名したなら、この検査も一緒に直すこと。");

        Assert.True(flag < scheduled,
            "印を立てるのが予約より後になっている。"
            + Environment.NewLine
            + "連鎖はこの印を mustRebuild の判断に読むので、この順序では"
            + Environment.NewLine
            + "**要素単位の再開を試して失敗する回**が挟まる（作り直しへ直行しなくなる）。");
    }

    /// <summary>
    /// <b>予約は「戻ってくるデバイス」に限ること。</b>
    ///
    /// <para>
    /// カメラ・画面キャプチャの EOS は切断以外にありえないので作り直すのが正しいが、
    /// 有限のテストパターン（<c>videotestsrc num-buffers=N</c>）やファイルの EOS は
    /// <b>正常終了</b>であり、作り直すと同じ有限ストリームを無限に回し続けることになる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheRestartOnEos_IsGatedByTheDeviceKind()
    {
        string branch = EosBranch();

        int gate = SourceMethodBody.IndexOfCode(
            branch, "DeviceKindRules.Classify(ActualSrcPipeline ?? SrcPipeline) != DeviceKind.None");
        Assert.True(gate >= 0,
            "EOS の予約が種別で絞られていない。"
            + Environment.NewLine
            + "有限のソース（videotestsrc num-buffers=N）やファイルの EOS は正常終了なので、"
            + Environment.NewLine
            + "無条件に予約すると**同じ有限ストリームを 5 秒ごとに永久に作り直す**ことになる。"
            + Environment.NewLine
            + "E2E の StopOutcomeTests は num-buffers で意図的に EOS を起こしており、"
            + Environment.NewLine
            + "門が外れるとソースが蘇って「供給が止まっている」という前提が壊れる。");

        // **先に「在ること」を見る。** 予約そのものが消えると IndexOfCode は -1 を返し、
        // 順序の表明だけでは「門が予約より後にある」という**嘘の診断**で落ちる。
        int scheduled = SourceMethodBody.IndexOfCode(branch, "ScheduleRestart(elementName)");
        Assert.True(scheduled >= 0,
            "sink バスの EOS が復帰を予約しなくなっている（TheSinkEos_SchedulesARestart を見ること）。");

        Assert.True(gate < scheduled,
            "種別の検査が予約より後にある。門は予約の手前に置くこと。");
    }
}
