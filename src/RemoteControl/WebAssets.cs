using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// 埋め込みの Web UI（<c>wwwroot/</c> の 7 ファイル）。
///
/// <para>
/// <b>提供するのは <see cref="Manifest"/> に載っている名前だけである。</b> ディレクトリを
/// 走査して「在るものを配る」形にすると、将来 <c>wwwroot/</c> へ置いたものが
/// 意図せず LAN から読めるようになる ── 配信の範囲を決めているのはこの表である。
/// </para>
/// <para>
/// <b>埋め込みは型初期化で全部読む。</b> 資産が発行物へ入っていない場合、要求が来て
/// 初めて 404 になるのでは原因に辿り着けない ── <c>RemoteControlHost.StartAsync</c> が
/// 待ち受けの前にこの型へ触れ、起動そのものを <c>remote.error</c> で落とす。
/// </para>
/// </summary>
internal static class WebAssets
{
    /// <summary>
    /// 開発時にディスク上のファイルで上書きするための環境変数
    /// （値は <c>wwwroot</c> 相当のディレクトリ）。
    /// <b>設定しても <see cref="Manifest"/> の名前しか読まない</b>ので、
    /// 配信の範囲は上書きの有無で変わらない。
    /// </summary>
    public const string WebRootVariable = "PROCESSRECORDERAPP_WEBROOT";

    /// <summary>提供するファイルの全列挙（名前 → Content-Type）。ここに無い名前は 404。</summary>
    public static readonly IReadOnlyDictionary<string, string> Manifest = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["app-core.js"] = "text/javascript; charset=utf-8",
        ["app-player.js"] = "text/javascript; charset=utf-8",
        ["app-recordings.js"] = "text/javascript; charset=utf-8",
        ["app-settings.js"] = "text/javascript; charset=utf-8",
        ["app.css"] = "text/css; charset=utf-8",
        ["app.js"] = "text/javascript; charset=utf-8",
        ["index.html"] = "text/html; charset=utf-8",
    };

    /// <summary>埋め込みリソースの論理名の接頭辞（<c>RemoteControl.csproj</c> の <c>LogicalName</c> と対）。</summary>
    private const string ResourcePrefix = "wwwroot/";

    private static readonly Dictionary<string, (byte[] Bytes, string ETag)> Embedded = LoadEmbedded();

    private static Dictionary<string, (byte[], string)> LoadEmbedded()
    {
        var assembly = typeof(WebAssets).Assembly;
        var loaded = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);

        foreach (string name in Manifest.Keys)
        {
            using Stream? stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
                ?? throw new InvalidOperationException(
                    $"the embedded web asset '{ResourcePrefix + name}' is missing from {assembly.GetName().Name}");

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            byte[] bytes = buffer.ToArray();
            loaded[name] = (bytes, ComputeETag(bytes));
        }

        return loaded;
    }

    /// <summary>
    /// 内容から <c>ETag</c> を作る。SHA256 の先頭 16 バイトを 16 進で表し、引用符で括る
    /// （HTTP の entity-tag は引用符まで含めて 1 つの値）。
    /// <b>弱くない</b> ── 同じ値なら中身も同じである。
    /// </summary>
    public static string ComputeETag(byte[] bytes)
        => '"' + Convert.ToHexStringLower(SHA256.HashData(bytes).AsSpan(0, 16)) + '"';

    /// <summary>
    /// 資産を取り出す。<see cref="Manifest"/> に無い名前は <see langword="false"/>。
    ///
    /// <para>
    /// <see cref="WebRootVariable"/> が指すディレクトリに同名のファイルが在れば
    /// <b>要求ごとに</b>そちらを読む（編集して再読込するだけで反映される）。
    /// 読めなければ埋め込みへ戻る ── 上書きは一部のファイルだけでも成立する。
    /// </para>
    /// </summary>
    public static bool TryGet(string name, out byte[] bytes, out string contentType)
    {
        bytes = [];
        contentType = string.Empty;

        if (!Manifest.TryGetValue(name, out string? type))
            return false;

        contentType = type;
        bytes = Embedded[name].Bytes;

        string? webRoot = Environment.GetEnvironmentVariable(WebRootVariable);
        if (!string.IsNullOrEmpty(webRoot))
        {
            try
            {
                bytes = File.ReadAllBytes(Path.Combine(webRoot, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException or System.Security.SecurityException)
            {
                // 上書きが読めなければ埋め込みのまま。開発用の仕掛けで配信を止めない。
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="bytes"/> の <c>ETag</c>。埋め込みそのものなら型初期化で計算済みの値を返し、
    /// ディスクの上書きなら<b>その場で</b>計算する。
    /// </summary>
    public static string ETagFor(string name, byte[] bytes)
        => Embedded.TryGetValue(name, out var asset) && ReferenceEquals(asset.Bytes, bytes)
            ? asset.ETag
            : ComputeETag(bytes);
}
