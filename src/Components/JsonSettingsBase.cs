using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ProcessRecorderApp.Components;

/// <summary>
/// JSONファイルへの永続化を伴う設定クラスの基底実装。
/// ファイルパスや <see cref="JsonTypeInfo{T}"/>（Native AOT対応のソース生成JSONコンテキスト）は
/// アプリ固有のため派生クラス側が保持し、<see cref="LoadOrCreate"/>/<see cref="Save"/> の
/// 呼び出し時に渡す設計とする。
/// </summary>
public abstract partial class JsonSettingsBase<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSelf>
    : ObservableObject, IPropertyAccess
    where TSelf : JsonSettingsBase<TSelf>
{
    [Browsable(false)]
    [ObservableProperty]
    public partial bool IsFirstRun { get; set; } = true;

    /// <summary>
    /// 指定したファイルからJSONを読み込む。ファイルが存在しない場合や読み込みに失敗した場合は
    /// <paramref name="createDefault"/> が返す既定値を使用する。
    /// </summary>
    protected static TSelf LoadOrCreate(string filePath, JsonTypeInfo<TSelf> jsonTypeInfo, Func<TSelf> createDefault)
    {
        TSelf? settings = null;
        try
        {
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                settings = JsonSerializer.Deserialize(json, jsonTypeInfo) ?? createDefault();
            }
        }
        catch
        {
        }

        settings ??= createDefault();
        settings.OnLoaded();
        return settings;
    }

    /// <summary>現在の内容を指定したファイルへJSONとして書き出す。</summary>
    public void Save(string filePath, JsonTypeInfo<TSelf> jsonTypeInfo)
    {
        IsFirstRun = false;

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize((TSelf)this, jsonTypeInfo);
        File.WriteAllText(filePath, json);
    }

    protected virtual void OnLoaded() { }

    public virtual IEnumerable<PropertyInfo> GetProperties() => typeof(TSelf).GetProperties(BindingFlags.Instance | BindingFlags.Public);
}
