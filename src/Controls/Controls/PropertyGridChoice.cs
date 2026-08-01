namespace ProcessRecorderApp.Controls;

/// <summary>
/// <see cref="PropertyEditKind.Choice"/> の選択肢1件。<b>表示名と保存値を分ける</b>ための器。
///
/// <para>
/// 例: 保存値 <c>nvautogpuh264enc</c> に対して表示は
/// <c>nvautogpuh264enc (この PC には見つかりません)</c>。
/// </para>
/// <para>
/// <b>コンボボックスの表示は <c>DisplayMemberPath</c> ではなく <c>ItemTemplate</c> の
/// <c>x:Bind</c> で行う。</b> <c>DisplayMemberPath</c> と <c>SelectedValuePath</c> は
/// どちらも<b>文字列のパスを実行時にリフレクションで解決する</b>ので、
/// Native AOT では消えうる（PropertyGrid は元からリフレクションを使う AOT の危険域なので、
/// ここで更に増やす理由が無い）。<c>x:Bind</c> はコンパイル時に生成される。
/// </para>
/// </summary>
public sealed class PropertyGridChoice
{
    /// <summary>設定へ保存される値。空文字も正当な値（「自動」を意味する等）。</summary>
    public required string Value { get; init; }

    /// <summary>画面に出す文言。<see cref="Value"/> と同じでもよい。</summary>
    public required string Display { get; init; }

    /// <summary>ToolTip 用。表示名を短くしても保存値が確認できるようにする。</summary>
    public override string ToString() => Display;
}
