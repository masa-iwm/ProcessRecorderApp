using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
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
    /// <see cref="Save"/> が書き込みに使う一時ファイルの拡張子。
    /// <b>本体と同じディレクトリに置く</b> ── 別ボリュームへ置くと差し替えが原子的でなくなる。
    /// </summary>
    public const string TemporaryFileSuffix = ".tmp";

    /// <summary>中身が壊れていて読めなかった設定ファイルの退避先の拡張子。</summary>
    public const string UnreadableFileSuffix = ".bad";

    /// <summary>
    /// 設定ファイルの隣に置く JSON Schema の拡張子（<c>settings.json</c> なら
    /// <c>settings.schema.json</c>）。<b>本体の拡張子を置き換える</b>形にしてあるのは、
    /// エディタが <c>$schema</c> の相対参照で引く先だからである。
    /// </summary>
    public const string SchemaFileExtension = ".schema.json";

    /// <summary>設定ファイルのパスから、隣に置く JSON Schema のパスを求める。</summary>
    public static string GetSchemaFilePath(string filePath)
        => Path.ChangeExtension(filePath, null) + SchemaFileExtension;

    /// <summary>
    /// <see cref="BuildSchema"/> が出力に宣言する JSON Schema の方言
    /// （<c>JsonSchemaExporter</c> が生成するのはこの版に沿った形）。
    /// </summary>
    public const string SchemaDialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// 指定したファイルからJSONを読み込む。ファイルが存在しない場合や読み込みに失敗した場合は
    /// <paramref name="createDefault"/> が返す既定値を使用する。
    /// </summary>
    /// <param name="onLoadFailure">
    /// 読み込みに失敗したときに理由を受け取るコールバック。
    ///
    /// <para>
    /// <b>ここから直接 <c>ActivityLog</c> へ書かない。</b> <c>Components</c> の型は L1 から
    /// 直接呼ばれるので、書くと<b>テストを回しただけで実ユーザーの activity.log が汚れる</b>
    /// （<c>RecordingCleanup</c> が「ログは呼び出し側が書く」としているのと同じ理由）。
    /// アプリ側はここへ記録を渡すこと ── <b>渡さないと、利用者の設定が丸ごと既定値へ
    /// 戻ったことがどこにも残らない。</b>
    /// </para>
    /// </param>
    protected static TSelf LoadOrCreate(
        string filePath,
        JsonTypeInfo<TSelf> jsonTypeInfo,
        Func<TSelf> createDefault,
        Action<string>? onLoadFailure = null)
    {
        TSelf? settings = null;
        try
        {
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                settings = JsonSerializer.Deserialize(json, jsonTypeInfo);
                if (settings is null)
                {
                    // 中身が JSON リテラルの null（正当な JSON なので JsonException にならない）。
                    // 切り詰められた空ファイル等と同じ「中身が壊れている」扱いで記録・退避する
                    // ── この形だけ無記録で既定値へ倒すと、下の「黙って倒さない」が一貫しない。
                    onLoadFailure?.Invoke($"file='{filePath}' the file contains JSON null");
                    QuarantineUnreadableFile(filePath, onLoadFailure);
                }
            }
        }
        catch (Exception ex)
        {
            // **黙って既定値へ倒さない。** ここを通ると、レコーダー定義・トリガ定義・
            // 永続化したテンプレート変数・保存先が丸ごと消えるのに、記録が無ければ
            // 利用者には「なぜか初期状態に戻っていた」としか見えない。
            onLoadFailure?.Invoke($"file='{filePath}' {ex.GetType().Name}: {ex.Message}");

            // **退避するのは「中身が壊れている」場合だけ。** 読み取り自体の失敗
            // （一時的なロック・権限）で退避すると、**正しいファイルを退けてしまう**
            // ── 次回の Save がその場に新しい既定値を書き、復旧が難しくなる。
            if (ex is JsonException)
                QuarantineUnreadableFile(filePath, onLoadFailure);
        }

        settings ??= createDefault();
        settings.OnLoaded();
        return settings;
    }

    /// <summary>
    /// 読めなかった設定ファイルを <see cref="UnreadableFileSuffix"/> へ退避する。
    /// 次回の <see cref="Save"/> がその場を上書きしてしまう前に、原因の調査と手での復旧の
    /// 余地を残すため。失敗しても読み込み自体は続行する。
    /// </summary>
    private static void QuarantineUnreadableFile(string filePath, Action<string>? onLoadFailure)
    {
        try
        {
            File.Move(filePath, filePath + UnreadableFileSuffix, overwrite: true);
        }
        catch (Exception ex)
        {
            onLoadFailure?.Invoke($"could not quarantine '{filePath}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 現在の内容を指定したファイルへJSONとして書き出す。
    ///
    /// <para>
    /// <b>本体へ直接書かない。</b> 一時ファイルへ書いてから差し替える。
    /// このアプリは設定変更を約1秒のデバウンスで自動保存し、かつ
    /// <b>強制終了されうる前提</b>で作ってある（トレイ常駐中の <c>Stop-Process -Force</c>）。
    /// 直接書くと、その瞬間に切れた JSON が残り、次回起動で
    /// <see cref="LoadOrCreate"/> が既定値へ倒れて<b>利用者の設定が丸ごと消える</b>。
    /// 差し替えなら、途中で落ちても本体は前回の内容のまま残る。
    /// </para>
    /// </summary>
    /// <param name="onSaveFailure">
    /// 書き込み・差し替えに失敗したときの通知先（<see cref="LoadOrCreate"/> の
    /// <c>onLoadFailure</c> と対称）。<b>ここで例外を投げない</b>のは、呼び出し元に
    /// 無防備な経路があるため ── 終了経路（<c>AppWindow.Destroying</c>）で投げると
    /// 後続の録画エンジンの破棄（moov の確定）まで巻き添えになり、デバウンス保存
    /// （<c>DispatcherQueue</c> のコールバック）で投げると捕捉される保証が無い。
    /// ディスク満杯・ウイルス対策ソフトによる一時ロック・保存先の権限は実際に起こる。
    /// </param>
    public void Save(string filePath, JsonTypeInfo<TSelf> jsonTypeInfo, Action<string>? onSaveFailure = null)
    {
        IsFirstRun = false;

        string json = JsonSerializer.Serialize((TSelf)this, jsonTypeInfo);

        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 同じディレクトリの一時ファイルへ書いてから差し替える（同一ボリュームなので
            // MoveFileEx の置換は原子的になる）。
            string temporary = filePath + TemporaryFileSuffix;
            File.WriteAllText(temporary, json);
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onSaveFailure?.Invoke($"could not save '{filePath}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// この型に対応する JSON Schema を組み立てて文字列で返す。
    ///
    /// <para>
    /// <b><see cref="JsonTypeInfo"/> を受ける多重定義だけを使う。</b>
    /// <c>JsonSerializerOptions.GetJsonSchemaAsNode(Type)</c> の側は実行時にリフレクションで
    /// 契約を作るため Native AOT で警告になり、しかも<b>本体の直列化とは別の解決器</b>を
    /// 通るので、ソース生成側と食い違ったスキーマを出しうる。ここへ渡す型情報は
    /// <see cref="Save"/> が使うものと同一にすること ── そうして初めて
    /// 「書いた JSON を、隣に置いたスキーマが必ず受理する」が成り立つ。
    /// </para>
    /// <para>
    /// 出力は <see cref="Utf8JsonWriter"/> で直接書く（<c>JsonNode.ToJsonString</c> は使わない）。
    /// 設定本体と同じくインデント付き・生の UTF-8 にして、人が読めて差分も取れる形にする。
    /// </para>
    /// </summary>
    public static string BuildSchema(JsonTypeInfo<TSelf> jsonTypeInfo)
    {
        JsonNode schema = jsonTypeInfo.GetJsonSchemaAsNode(new JsonSchemaExporterOptions
        {
            // 明示的な null 許容注釈が無い型（null 忘却）を「null 不可」として扱う。
            // 設定ファイルに null を書いてよい場所は無いので、既定（null 可）のままだと
            // スキーマがほぼすべてのプロパティで null を受理し、検査の意味が薄くなる。
            TreatNullObliviousAsNonNullable = true,
        });

        // エクスポータの出力には方言の宣言も題も入らない。**リポジトリへ登録して人と道具の
        // 双方が読むファイル**なので、何の規格で書かれた何のスキーマかをファイル自身に持たせる。
        if (schema is JsonObject root)
        {
            root.Insert(0, "$schema", SchemaDialect);
            root.Insert(1, "title", jsonTypeInfo.Type.Name);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            // 設定本体と同じ理由 ── 書き出し先は HTML ではないので < > & ' を逃がさない。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            schema.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// 設定ファイルの隣へ JSON Schema を書き出す（<see cref="GetSchemaFilePath"/> の場所）。
    ///
    /// <para>
    /// <b>内容が同じなら書かない。</b> スキーマはビルドごとの定数なのに、呼び出し元の
    /// 保存は約1秒のデバウンスで何度も走る ── 毎回書くと、利用者が編集中の
    /// 設定ディレクトリでファイルの更新時刻だけが動き続ける。
    /// </para>
    /// <para>
    /// <b>失敗しても投げない。</b> スキーマは設定本体の付随物であり、これが書けないことで
    /// 設定の保存や終了処理を巻き添えにしてはならない（<see cref="Save"/> の
    /// <c>onSaveFailure</c> と同じ判断）。
    /// </para>
    /// </summary>
    public static void SaveSchema(
        string filePath,
        JsonTypeInfo<TSelf> jsonTypeInfo,
        Action<string>? onSaveFailure = null)
    {
        string schemaPath = GetSchemaFilePath(filePath);

        // 組み立て自体は try の外に置く（Save が直列化を外に置いているのと同じ判断）。
        // ここが投げるのは型の側の作り間違いで、環境によらず必ず再現する ── 下の catch へ
        // 巻き込むと「保存はできているのにスキーマだけ黙って出ない」形になり、気付けない。
        string json = BuildSchema(jsonTypeInfo);

        try
        {
            if (File.Exists(schemaPath) && File.ReadAllText(schemaPath) == json)
                return;

            string? dir = Path.GetDirectoryName(schemaPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 本体と同じく一時ファイル経由で差し替える（読み手はエディタなので、
            // 書きかけの JSON を掴ませない）。
            string temporary = schemaPath + TemporaryFileSuffix;
            File.WriteAllText(temporary, json);
            File.Move(temporary, schemaPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onSaveFailure?.Invoke($"could not save '{schemaPath}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected virtual void OnLoaded() { }

    public virtual IEnumerable<PropertyInfo> GetProperties() => typeof(TSelf).GetProperties(BindingFlags.Instance | BindingFlags.Public);
}
