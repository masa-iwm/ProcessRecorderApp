using System;
using System.Diagnostics.CodeAnalysis;

namespace ProcessRecorderApp.Components;

/// <summary>
/// コレクション型プロパティに対して、要素の具象型を宣言する属性。
///
/// PropertyGridView がコレクション項目の Add ボタンで新規要素を生成する際、
/// この属性の <see cref="ItemType"/> を Activator.CreateInstance に渡す。
/// [DynamicallyAccessedMembers] 注釈により、指定した型の public 無引数コンストラクタが
/// トリミングで除去されないことが保証されるため、Native AOT でも安全に動作する。
/// この属性が付いていないコレクションプロパティでは新規要素の追加は提供されない。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CollectionItemTypeAttribute(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type itemType)
    : Attribute
{
    /// <summary>コレクション要素の具象型。public 無引数コンストラクタを持つこと。</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type ItemType { get; } = itemType;
}
