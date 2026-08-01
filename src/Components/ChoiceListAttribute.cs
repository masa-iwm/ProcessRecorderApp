using System;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 文字列プロパティに対して「ホストが供給する選択肢一覧」を関連付ける属性。
///
/// PropertyGridView は、この属性が付いた文字列プロパティを自由入力のテキストではなく
/// <b>コンボボックス</b>で表示する。選択肢はホスト側が
/// <see cref="ProcessRecorderApp.Controls.PropertyGridView.ChoiceProvider"/> に設定した
/// デリゲートへ <see cref="Key"/> と<b>現在値</b>を渡して取得する。
///
/// <para>
/// <b>プロパティの型は <c>string</c> のまま</b>にしてある。列挙型にすると設定 JSON の
/// 形が変わり、移行が必要になる（この repo は未リリースのあいだ <c>DataVersion</c> を
/// 1 に固定する方針）。表示名と保存値を分けたいだけなので、型を変える必要はない。
/// </para>
/// <para>
/// <b>現在値をプロバイダーへ渡すのが要点。</b> 一覧に無い値（古い名前・打ち間違い・
/// 手で編集した設定ファイル）を持つ利用者が画面を開いたとき、
/// 一覧に無いままだとコンボボックスは<b>空を表示し、他の行に触れた瞬間にその値を失う</b>
/// ── しかも編集不可なので打ち直すこともできない。
/// プロバイダーが現在値を受け取れれば、それを「不明」印つきの選択肢として
/// 一覧へ差し込んで<b>値を保持できる</b>。
/// </para>
/// <para>
/// この属性自体は選択肢を持たず、どの一覧を使うかを識別する <see cref="Key"/> のみを保持する
/// （<see cref="ValueBuilderAttribute"/> と同じ考え方）。これにより共有コントロール側は
/// 特定ドメイン（GStreamer 等）に依存しない。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ChoiceListAttribute(string key) : Attribute
{
    /// <summary>
    /// 使用する選択肢一覧の識別キー。ホスト側の <c>ChoiceProvider</c> がこのキーで処理を振り分ける。
    ///
    /// <para>
    /// <b><c>PropCat_</c> / <c>PropDesc_</c> で始める名前を使ってはいけない。</b>
    /// L4 の <c>SourceReferences.PropertyGridKeyLiteralRegex</c> がその形の文字列リテラルを
    /// <b>リソースキーの参照として拾う</b>ので、実在しないキーとして検査が赤になる。
    /// </para>
    /// </summary>
    public string Key { get; } = key;
}
