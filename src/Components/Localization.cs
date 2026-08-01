using Microsoft.Windows.ApplicationModel.Resources;

namespace ProcessRecorderApp.Components;

/// <summary>
/// resources.pri からの文字列解決をアプリ全体で共有するための薄いラッパー。
/// Application/Window の生成有無に依存しない <see cref="ResourceManager"/> 経由のため、
/// UI 未生成のランチャー/CLI 経路からも呼び出せる。
///
/// Windows App SDK のランタイムがブートストラップされていないプロセス（単体テストのホスト等）では
/// <see cref="ResourceManager"/> の生成自体が失敗する。その場合はリソースマップを持たない
/// 「フォールバックモード」となり、キーの末尾セグメントをそのまま文字列として返す。
/// これは、リソースキーを静的に保持しているだけの型（例: <c>SrcPipelineBuilder.Sources</c>）を
/// リソース基盤なしでテストできるようにするための措置であり、アプリ本体の動作には影響しない
/// （アプリ本体ではマップが必ず取得できるため、従来どおりキー欠落は例外になる）。
/// </summary>
public static class Localization
{
    private static readonly Lazy<ResourceMap?> _map = new(() =>
    {
        try
        {
            return new ResourceManager().MainResourceMap;
        }
        catch
        {
            // MRT Core が利用できない環境（WinAppSDK 未ブートストラップ）。
            return null;
        }
    });

    /// <summary>リソースマップが利用可能か（false の場合はフォールバックモード）。</summary>
    public static bool IsResourceMapAvailable => _map.Value is not null;

    /// <summary>キーの末尾セグメント（"Resources/Foo_Bar" → "Foo_Bar"）。</summary>
    private static string LastSegment(string resourcePath)
    {
        int index = resourcePath.LastIndexOf('/');
        return index >= 0 ? resourcePath[(index + 1)..] : resourcePath;
    }

    /// <summary>
    /// 完全修飾キー（例: "Resources/MainPage_Clear.Content"）からローカライズ文字列を取得する。
    /// リソースマップが利用可能な場合、キーが存在しなければ例外となる（キー名の打ち間違いを早期検出するため）。
    /// リソースマップが利用できない環境ではキーの末尾セグメントを返す。
    /// </summary>
    public static string GetString(string resourcePath)
        => _map.Value is { } map ? map.GetValue(resourcePath).ValueAsString : LastSegment(resourcePath);

    /// <summary>{0}等のプレースホルダーを含むリソースへ引数を埋め込んで取得する。</summary>
    public static string GetString(string resourcePath, params object?[] args) => string.Format(GetString(resourcePath), args);

    /// <summary>
    /// キーが解決できない場合は fallback をそのまま返す。
    /// PropertyGrid の Category/Description のように、属性値がリソースキーと平文のどちらもあり得る箇所で使う。
    /// </summary>
    public static string GetStringOrFallback(string resourcePath, string fallback)
        => _map.Value?.TryGetValue(resourcePath)?.ValueAsString ?? fallback;
}
