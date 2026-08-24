using System;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ProcessRecorderApp.Components;

/// <summary>
/// リモート操作 API の<b>純粋な規則</b>（終了コード→HTTP・トークン照合・パス解決・
/// 設定キーの許可/拒否）。
///
/// <para>
/// 規則の利用側は ASP.NET Core と WinUI アプリの両方にあるが、どちらも L1 テスト
/// プロジェクトから参照できない。認証とパス解決は間違えると<b>LAN からファイルが読める</b>
/// 種類の欠陥になるので、<c>RecorderCliRules</c> と同じ形でここへ置いて L1 で固定する。
/// </para>
/// </summary>
public static class RemoteApiRules
{
    /// <summary>
    /// CLI の終了コードを HTTP ステータスへ写す。
    ///
    /// <para>
    /// <b>数値のリテラルで書いてある。</b> 終了コードの定数は WinUI アプリ側
    /// （<c>ActivationCommands</c>）と <c>SingleInstance</c> にあり、このプロジェクトからは
    /// 参照できない。行末に定数名を書いてあるのは読み手のためであり、
    /// 対応の維持は L1 の <c>RemoteApiRulesDriftTests</c> が
    /// ソーステキストの双方向照合で担っている。
    /// </para>
    /// <para>
    /// <b>この型に終了コードの定数を作らないこと。</b> <c>DocumentationDriftTests</c> は
    /// <c>src/</c> 配下すべてから終了コード定数を集め、<c>src/README.md</c> の
    /// 「終了コードの一覧」に行があることを要求する ── ここに写像用の別名を置くと、
    /// 実在しない終了コードが文書に要求される。
    /// </para>
    /// </summary>
    public static int HttpStatusFor(int exitCode) => exitCode switch
    {
        0 => 200,   // 成功（名前付きの定数は無い）
        4 => 400,   // ExitCode_InvalidArguments
        10 => 500,  // DefaultFailureExitCode
        11 => 404,  // ExitCode_VariableNotDefined
        12 => 503,  // ExitCode_RecorderNotAvailable
        13 => 404,  // ExitCode_RecorderNotFound
        14 => 409,  // ExitCode_RecordingNotExecutable
        15 => 200,  // ExitCode_RecorderUnhealthy（処理は成功。健康状態は本文で表現する）
        16 => 422,  // ExitCode_RecordingProducedNothing
        17 => 422,  // ExitCode_RecordingNotFinalized
        99 => 500,  // UnexpectedErrorExitCode
        _ => 500,
    };

    /// <summary>
    /// 録画エンジンがまだ使えないとき（終了コード 12）に返す <c>Retry-After</c> の秒数。
    /// </summary>
    public const int RetryAfterSecondsWhenNotReady = 5;

    /// <summary>
    /// アクセストークンの照合。<b>一致した文字数で時間が変わらない比較</b>を使う。
    /// どちらかが null か空なら false（トークン未設定のときに空文字で通ってしまわないため）。
    /// 長さ違いは <see cref="CryptographicOperations.FixedTimeEquals"/> の仕様どおり false。
    /// </summary>
    public static bool TokenEquals(string? expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }

    /// <summary>
    /// アクセストークンを作る。256 ビットの乱数を Base64Url で表した 43 文字
    /// （パディングの <c>=</c> は付かない）。URL のクエリにそのまま載せられる。
    /// </summary>
    public static string GenerateAccessToken()
        => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// 配信 root 配下の録画ファイルの絶対パスを求める。
    ///
    /// <para>
    /// <b>文字列の規則だけで判定し、ファイルシステムには触らない</b>
    /// ── 存在するかどうかで結果が変わると、同じ要求が環境しだいで通ったり通らなかったりする。
    /// リパースポイントの確認は実際に開く側（<see cref="RecordingFiles.TryOpen"/>）が行う。
    /// </para>
    /// <para>拒否するもの: 絶対パス・ドライブ相対・UNC、<c>.</c> と <c>..</c>、空のセグメント、
    /// 末尾がドットか空白のセグメント（Win32 が黙って切り落とす）、
    /// <c>:</c>（NTFS の代替データストリーム）・<c>*</c>・<c>?</c>・<c>"</c>・<c>&lt;</c>・<c>&gt;</c>、
    /// DOS の予約名（<c>CON</c>・<c>PRN</c>・<c>AUX</c>・<c>NUL</c>・<c>COM1</c>〜<c>COM9</c>・
    /// <c>LPT1</c>〜<c>LPT9</c>。拡張子を除いた名前で照合する）、
    /// <see cref="Path.GetInvalidPathChars"/>、<see cref="RecordingCleanup.Extension"/> 以外の拡張子、
    /// 解決結果が root の外に出るもの。</para>
    /// </summary>
    public static bool TryResolveUnderRoot(string root, string relativePath, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;

        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        string normalized = relativePath.Replace('/', '\\');

        if (Path.IsPathRooted(normalized))
            return false;

        if (normalized.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;

        foreach (string segment in normalized.Split('\\'))
        {
            if (segment.Length == 0)
                return false;
            if (segment is "." or "..")
                return false;

            char last = segment[^1];
            if (last == '.' || char.IsWhiteSpace(last))
                return false;

            if (segment.AsSpan().IndexOfAny(":*?\"<>") >= 0)
                return false;

            if (IsDosReservedName(segment))
                return false;
        }

        if (!string.Equals(Path.GetExtension(normalized), RecordingCleanup.Extension, StringComparison.OrdinalIgnoreCase))
            return false;

        string rootFull;
        string candidate;
        try
        {
            rootFull = Path.GetFullPath(root).TrimEnd('\\');
            candidate = Path.GetFullPath(Path.Combine(rootFull, normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                      or PathTooLongException or IOException or SecurityException)
        {
            return false;
        }

        if (!candidate.StartsWith(rootFull + '\\', StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = candidate;
        return true;
    }

    /// <summary>DOS の予約名（デバイス名）。拡張子が付いていても同じデバイスを指す。</summary>
    private static readonly string[] DosReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// そのセグメントが DOS の予約名か（拡張子を除いた名前で <see cref="StringComparison.OrdinalIgnoreCase"/> 照合）。
    /// <c>CON.mp4</c> はファイルではなくコンソールデバイスを開く。
    /// </summary>
    private static bool IsDosReservedName(string segment)
    {
        string name = Path.GetFileNameWithoutExtension(segment);
        foreach (string reserved in DosReservedNames)
        {
            if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 録画ファイルの <c>ETag</c>。長さと更新時刻（tick）を 16 進で連ねた
    /// <c>"&lt;length&gt;-&lt;ticks&gt;"</c>（<b>引用符込みで返す</b> ── HTTP の entity-tag は
    /// 引用符まで含めて 1 つの値であり、付ける側が 2 か所に分かれると片方が忘れる）。
    ///
    /// <para>
    /// <b>弱い（<c>W/</c>）にはしない。</b> 長さか更新時刻のどちらかが動けば別の値になり、
    /// 同じ値なら中身も同じ ── Range 要求の前提条件に使える強さがある。
    /// 録画中のファイルは書かれるたびに両方が動くので、キャッシュは自然に無効になる。
    /// </para>
    /// </summary>
    public static string RecordingETag(long length, DateTime lastWriteUtc)
        => string.Create(CultureInfo.InvariantCulture, $"\"{length:x}-{lastWriteUtc.Ticks:x}\"");

    /// <summary>
    /// Windows の相対パス（区切りは <c>\</c>）を URL の経路（区切りは <c>/</c>）にする。
    /// <b>エスケープはしない</b> ── 各セグメントの符号化は URL を組み立てる側の責務である。
    /// </summary>
    public static string ToUrlPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return relativePath.Replace('\\', '/');
    }

    /// <summary>
    /// URL の経路（区切りは <c>/</c>）を Windows の相対パス（区切りは <c>\</c>）にする。
    /// <see cref="ToUrlPath"/> の逆。<b>妥当性は見ない</b> ── 判定は
    /// <see cref="TryResolveUnderRoot"/> 1 か所に置く。
    /// </summary>
    public static string FromUrlPath(string urlPath)
    {
        ArgumentNullException.ThrowIfNull(urlPath);
        return urlPath.Replace('/', '\\');
    }

    /// <summary>
    /// リモートから書き換えてよい <c>AppSettings</c> のプロパティ名。
    ///
    /// <para>
    /// <b>許可と拒否は明示の 2 本立てで、合わせて全 public プロパティと過不足なく一致する</b>
    /// （L1 の <c>RemoteEditableAppSettingsTests</c> が検査）。
    /// 「拒否リストに無ければ許可」にすると、後から増えたプロパティが黙って書き換え可能になる。
    /// </para>
    /// </summary>
    public static readonly string[] RemoteEditableAppSettings =
    [
        "ConfirmRecorderRemoval",
        "FragmentedOutput",
        "FramingGrid",
        "GstDebug",
        "LogScrollbackLines",
        "PreferredH264Encoder",
        "StopFinalizeTimeoutMs",
        "RecordingRetentionDays",
        "RecordingCleanupIntervalHours",
        "UiaTriggersEnabled",
    ];

    /// <summary>
    /// リモートからは書き換えない <c>AppSettings</c> のプロパティ名。
    ///
    /// <para>
    /// パス系（<c>OutputDirectory</c> は配信 root そのもの、他はログの出力先）、
    /// ホストの画面状態、コレクション（要素の差し替えは録画中のパイプラインを落とす）、
    /// 永続化の内部状態。
    /// </para>
    /// <para>
    /// <b>リモート操作の設定そのもの（<c>RemoteControl*</c>）も拒否側に置く。</b>
    /// リモートから自分の待ち受けとトークンを書き換えられると、
    /// アクセスを与えた相手がそのまま鍵を掛け替えられる。
    /// </para>
    /// </summary>
    public static readonly string[] RemoteDeniedAppSettings =
    [
        "OutputDirectory",
        "RemoteControlEnabled",
        "RemoteControlBindAddress",
        "RemoteControlPort",
        "RemoteControlAccessToken",
        "RemoteControlAllowGuestRead",
        "RemoteUsers",
        "RemoteUserList",
        "GstDebugDumpDotDir",
        "DebugLogFile",
        "WindowWidth",
        "WindowHeight",
        "SettingsWidth",
        "IsPropertyPaneCollapsed",
        "PaneDisplayMode",
        "Recorders",
        "UiaTriggerAssignments",
        "UiaTriggers",
        "ExtensionData",
        "TemplateVariables",
        "DataVersion",
        "SchemaReference",
        "UiaTriggerList",
        "IsFirstRun",
    ];

    /// <summary>そのプロパティ名がリモートから書き換えてよいものか（序数一致）。</summary>
    public static bool IsRemoteEditable(string name)
    {
        foreach (string editable in RemoteEditableAppSettings)
        {
            if (string.Equals(editable, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// リモートの PATCH で書き換えを拒否するレコーダー設定（settings.json のキー名）。
    ///
    /// <para>
    /// 基準は 2 つだけ ── <b>アプリが実行する内容そのもの</b>か、
    /// <b><c>OutputDirectory</c> の外へ書ける</b>か。エンコーダーの指定 2 つが載るのは
    /// 前者に当たるためである: 値は検証されず <c>parse_launch</c> の記述へそのまま
    /// 補間されるので（<c>EventRecorder.BuildEncoderCandidates</c> /
    /// <c>ResolveContinuousEncoder</c> → <c>BuildSinkPipeline</c> の <c>{encoder}</c>）、
    /// <c>SrcPipeline</c> と同じだけの実行能力を持つ（<c>filesink location=…</c> も差せる）。
    /// </para>
    /// </summary>
    public static readonly string[] RemoteDeniedRecorderSettings =
    [
        "SrcPipeline",                 // 任意の GStreamer パイプライン ── 実行内容そのもの
        "EncodingProperties",          // parse_launch へ生で補間される ── 同上
        "ContinuousEncodingProperties",// 同上
        "FilenameTemplate",            // 絶対パスを許すので OutputDirectory の外へ書ける
        "ContinuousFilenameTemplate"   // 同上
    ];

    public static bool IsRemoteEditableRecorderSetting(string name)
        => !Array.Exists(RemoteDeniedRecorderSettings, n => string.Equals(n, name, StringComparison.Ordinal));

    /// <summary>
    /// 部分更新（PATCH）の本文を、現在値の JSON へ重ねる。
    ///
    /// <para>
    /// <b>キーの検査と上書きだけを行う純関数である。</b> 値が型として妥当かどうかは見ない
    /// ── そこは設定オブジェクトへ戻すデシリアライズが答える唯一の正解であり、
    /// ここで先回りして判定すると<b>同じ規則が 2 か所に書かれる</b>。
    /// </para>
    /// <para>
    /// <b>写すのは <see cref="JsonNode.DeepClone"/> した複製。</b> 要求の本文のノードを
    /// そのまま差すと、同じノードが 2 つの親を持って
    /// <see cref="InvalidOperationException"/> になる。
    /// </para>
    /// <para>
    /// <b><see langword="null"/> の値も写す。</b> 「キーを指定しない」と
    /// 「null を指定する」は別の意味で、後者は null 許容のプロパティを空へ戻す唯一の手段である。
    /// </para>
    /// </summary>
    /// <param name="target">現在値の JSON（この場で書き換わる）。</param>
    /// <param name="patch">要求の本文。</param>
    /// <param name="isKnownKey">そのキーを受け付けてよいか。</param>
    /// <param name="rejectedKey">受け付けられなかった最初のキー（成功なら <see langword="null"/>）。</param>
    /// <returns>全キーを写せたら <see langword="true"/>。false のとき
    /// <paramref name="target"/> は<b>部分的に書き換わっていることがある</b>
    /// （呼び出し側は失敗した <paramref name="target"/> を使わない）。</returns>
    public static bool TryMergeIntoNode(
        JsonObject target, JsonObject patch, Func<string, bool> isKnownKey, out string? rejectedKey)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(isKnownKey);

        foreach (var pair in patch)
        {
            if (!isKnownKey(pair.Key))
            {
                rejectedKey = pair.Key;
                return false;
            }

            target[pair.Key] = pair.Value?.DeepClone();
        }

        rejectedKey = null;
        return true;
    }
}
