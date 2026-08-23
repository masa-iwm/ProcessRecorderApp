using System.Collections.Generic;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// レコーダー名を一意にする規則。
///
/// <para>
/// CLI のレコーダー指定は「数値ならインデックス、それ以外は名前で<b>完全一致・先勝ち</b>」
/// （<c>RecorderCliRules.ResolveTargetIndex</c>）。同じ名前が2つあると
/// <b>2つ目には CLI から永久に到達できない</b> ── しかも画面上は普通に2件並んで見えるので、
/// 「コマンドが効かない」ではなく「毎回 1 つ目が動く」という気付きにくい形で現れる。
/// </para>
///
/// <para>
/// <b>なぜ純粋関数として切り出すか</b> ── 追加・改名を行うのは
/// <c>GstControllerViewModel</c> / <c>GstEventRecorderViewModel</c>（WinUI アプリプロジェクト）で、
/// L1 テストプロジェクトから参照できない。規則そのものをここへ置けば L1 が守れる
/// （<see cref="RecordingCommandState"/> や <c>SingleInstanceManager.ShouldExitOnClose</c> と同じ手口）。
/// </para>
/// </summary>
public static class RecorderNaming
{
    /// <summary>
    /// <paramref name="desired"/> が既存の名前と衝突する場合に <c> (2)</c>、<c> (3)</c> … を付けて一意にする。
    ///
    /// <para>
    /// 比較は<b>序数（大文字小文字を区別する）</b>。CLI の解決が <c>==</c>（序数比較）なので、
    /// そこに合わせている ── ここだけ大文字小文字を無視すると、CLI では別物として
    /// 解決できる2つの名前を「衝突」と判定して勝手に改名することになる。
    /// </para>
    /// <para>
    /// <paramref name="existingNames"/> には<b>自分自身を含めない</b>。改名時に含めると、
    /// 名前を変えていない再代入でも衝突と判定されて番号が増え続ける。
    /// </para>
    /// </summary>
    /// <param name="desired">付けたい名前。</param>
    /// <param name="existingNames">既に使われている名前（自分自身は除く）。</param>
    public static string MakeUnique(string desired, IEnumerable<string> existingNames)
    {
        var taken = new HashSet<string>(existingNames, StringComparer.Ordinal);
        if (!taken.Contains(desired))
            return desired;

        // 2 から順に空きを探す。候補は必ず有限回で見つかる（taken は有限集合）。
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{desired} ({suffix})";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }
}
