using System.Globalization;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// <b>常時録画の枝から最初のフレームが出てくるのを待つ上限(ms)を決める（純粋関数）。</b>
///
/// <para>
/// 常時録画の <c>appsink</c> には <c>async=false</c> を付けるので、
/// <b>この枝が 1 フレームも出さなくてもパイプラインは <c>PLAYING</c> に到達する</b>
/// ── イベント録画を人質に取らせないための意図的な設計だが、その代償として
/// 「常時録画を on にしたのに何も出来ない」が<b>無音の失敗</b>になる。
/// この予算を過ぎても最初のサンプルが来なければ <c>ContinuousLastError</c> に出す。
/// </para>
/// <para>
/// <b><c>EventRecorder.PlayingStateTimeoutMs</c> をそのまま流用してはいけない。</b>
/// あの定数の doc が名指ししている唯一の誤検出形は「低いフレームレート ×
/// 出力の遅いエンコーダー」で、常時録画（低 fps で回すのが主用途）はまさにそれである。
/// エンコーダーが最初の 1 枚を出すまでに数フレーム溜める分を、
/// <b>設定されたフレームレートから逆算して</b>上乗せする。
/// </para>
/// </summary>
public static class ContinuousFirstSampleBudget
{
    /// <summary>
    /// 下限。イベント側の <c>PLAYING</c> 到達予算と同じ値に固定する
    /// （同じ「エンコーダーが最初の 1 枚を出すまで」を測っているため）。
    /// </summary>
    public const int MinimumMs = EventRecorder.PlayingStateTimeoutMs;

    /// <summary>
    /// 上限。これを超えて待っても、利用者には「無反応」と区別が付かない。
    /// 0.25fps 相当（8 フレームで 32 秒）より遅い設定はここで頭打ちにする。
    /// </summary>
    public const int MaximumMs = 30_000;

    /// <summary>エンコーダーが最初の 1 枚を出すまでに溜めると見込むフレーム数。</summary>
    public const int PrimeFrames = 8;

    /// <summary>
    /// <c>5/1</c>（または <c>5</c>）形式のフレームレートから予算を出す。
    /// 読めなければ <see cref="MinimumMs"/>。
    /// </summary>
    public static int For(string? framerate)
    {
        if (!TryParseFramerate(framerate, out int numerator, out int denominator))
            return MinimumMs;

        // 1 フレームの長さ(ms) = 1000 * 分母 / 分子
        long frameMs = 1000L * denominator / numerator;
        long budget = MinimumMs + PrimeFrames * frameMs;
        return budget < MinimumMs ? MinimumMs
            : MaximumMs < budget ? MaximumMs
            : (int)budget;
    }

    /// <summary><c>30/1</c> / <c>30</c> / <c>30000/1001</c> を読む。</summary>
    public static bool TryParseFramerate(string? framerate, out int numerator, out int denominator)
    {
        numerator = 0;
        denominator = 1;
        if (string.IsNullOrWhiteSpace(framerate))
            return false;

        string[] parts = framerate.Trim().Split('/');
        if (parts.Length is not (1 or 2))
            return false;
        if (!int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out numerator)
            || numerator <= 0)
            return false;
        if (parts.Length == 2
            && (!int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out denominator)
                || denominator <= 0))
        {
            return false;
        }
        return true;
    }
}
