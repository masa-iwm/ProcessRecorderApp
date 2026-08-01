using System;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 文字列プロパティに対して、外部の値ビルダー(編集支援ダイアログ等)を関連付ける属性。
///
/// PropertyGridView は、この属性が付いた文字列プロパティを通常のテキスト編集に加えて
/// 「…」ビルダー起動ボタンつきで表示する。ボタン押下時には、ホスト側が
/// <see cref="ProcessRecorderApp.Controls.PropertyGridView.ValueBuilder"/> に設定した
/// デリゲートへ <see cref="Key"/> と現在値を渡し、返ってきた文字列を新しい値としてコミットする。
///
/// この属性自体はビルダーの実体を持たず、どのビルダーを使うかを識別する <see cref="Key"/> のみを保持する。
/// これにより共有コントロール側は特定ドメイン(GStreamer 等)に依存しない。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ValueBuilderAttribute(string key) : Attribute
{
    /// <summary>使用するビルダーの識別キー。ホスト側の <c>ValueBuilder</c> がこのキーで処理を振り分ける。</summary>
    public string Key { get; } = key;
}
