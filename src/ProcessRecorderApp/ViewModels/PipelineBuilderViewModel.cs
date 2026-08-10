using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using ProcessRecorderApp.GStreamer;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ProcessRecorderApp.ViewModels;

/// <summary>パイプラインビルダーの 1 行が使う編集コントロールの種別。</summary>
public enum FieldEditKind
{
    Bool,
    Choice,
    Text,
}

/// <summary>
/// パイプラインビルダーの 1 プロパティ/caps フィールド行。
/// 有効チェック(<see cref="Enabled"/>)＋値を保持し、変更時に <see cref="Changed"/> を発火する。
/// </summary>
public sealed partial class PipelineFieldRow : ObservableObject
{
    /// <summary>GStreamer プロパティ名 / caps フィールド名(resolution は合成名)。</summary>
    public required string Name { get; init; }
    public string? Description { get; init; }
    public FieldEditKind EditKind { get; init; }
    /// <summary>Choice のときの選択肢。</summary>
    public string[] Choices { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 行の「有効」チェックボックスの AutomationId。値エディタ側は <see cref="Name"/> を
    /// そのまま AutomationId にするため、同じ行の 2 要素が衝突しないよう接尾辞を付ける。
    /// <see cref="Name"/> は GStreamer のプロパティ名／caps フィールド名であり
    /// ローカライズされないので、UI 自動化の識別子として安全に使える。
    /// </summary>
    public string EnabledAutomationId => $"{Name}.Enabled";

    public bool IsBool => EditKind == FieldEditKind.Bool;
    public bool IsChoice => EditKind == FieldEditKind.Choice;
    public bool IsText => EditKind == FieldEditKind.Text;

    /// <summary>行の値変更(有効/値)を通知する。ビルダー本体がプレビュー再生成に使う。</summary>
    public event Action? Changed;

    [ObservableProperty]
    public partial bool Enabled { get; set; }
    partial void OnEnabledChanged(bool value) => Changed?.Invoke();

    [ObservableProperty]
    public partial string Value { get; set; } = "";
    partial void OnValueChanged(string value)
    {
        // ComboBox の SelectedItem が候補に無いと null が書き込まれるため空文字へ丸める
        if (value is null)
        {
            Value = "";
            return;
        }
        OnPropertyChanged(nameof(BoolValue));
        Changed?.Invoke();
    }

    /// <summary>IsBool の行で ToggleSwitch から利用する。</summary>
    public bool BoolValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }
}

/// <summary>
/// パイプラインビルダーダイアログのビューモデル。
/// ソース選択・プロパティ・caps 各行を保持し、既存 SrcPipeline の解析(復元)と
/// プレビュー文字列の再生成を <see cref="SrcPipelineBuilder"/> に委譲する。
/// モニター数やデバイス caps などの選択肢は <see cref="GstIntrospect"/> で動的取得する。
/// </summary>
public sealed partial class PipelineBuilderViewModel : ObservableObject
{
    // 行の変更通知でプレビューを再生成する間、初期化中は再生成を抑止するためのガード
    private bool _suppressUpdate;

    // 動的選択肢の取得結果(遅延・キャッシュ)
    private int? _monitorCount;
    private IReadOnlyList<VideoDeviceInfo>? _videoDevices;

    private int MonitorCount => _monitorCount ??= GstIntrospect.GetMonitorCount();
    private IReadOnlyList<VideoDeviceInfo> VideoDevices => _videoDevices ??= GstIntrospect.GetVideoSourceDevices();

    /// <summary>
    /// 画面キャプチャ対象のモニター（実際に出す大きさを持つ）。
    /// 1 度だけ問い合わせて使い回す ── ダイアログを開くたびにデバイスを列挙し直すと、
    /// プロバイダの起動・停止が UI スレッドで効いてくる。
    /// </summary>
    private IReadOnlyList<VideoDeviceInfo> Monitors => _monitors ??= GstIntrospect.GetScreenCaptureMonitors();
    private IReadOnlyList<VideoDeviceInfo>? _monitors;

    public IReadOnlyList<SrcElementDef> Sources => SrcPipelineBuilder.Sources;

    public ObservableCollection<PipelineFieldRow> PropertyRows { get; } = [];
    public ObservableCollection<PipelineFieldRow> CapsRows { get; } = [];

    [ObservableProperty]
    public partial SrcElementDef? SelectedSource { get; set; }
    partial void OnSelectedSourceChanged(SrcElementDef? value)
    {
        // ユーザー操作によるソース変更では、その要素の既定で UI を組み直す
        if (_suppressUpdate || value is null)
            return;
        RebuildForSource(value, parsed: null);
        NotRecognized = false;
        UpdatePreview();
    }

    [ObservableProperty]
    public partial bool CapsEnabled { get; set; } = true;
    partial void OnCapsEnabledChanged(bool value) => UpdatePreview();

    /// <summary>生成されるパイプライン文字列(手動微修正も可能。OK 時はこの値を採用する)。</summary>
    [ObservableProperty]
    public partial string Preview { get; set; } = "";

    /// <summary>既存文字列を想定形として解析できなかった場合に true(警告表示に使う)。</summary>
    [ObservableProperty]
    public partial bool NotRecognized { get; set; }

    public Visibility NotRecognizedVisibility => NotRecognized ? Visibility.Visible : Visibility.Collapsed;
    partial void OnNotRecognizedChanged(bool value) => OnPropertyChanged(nameof(NotRecognizedVisibility));

    public PipelineBuilderViewModel(string? currentPipeline)
    {
        var parsed = SrcPipelineBuilder.Parse(currentPipeline);
        var source = SrcPipelineBuilder.FindSource(parsed.SourceElement) ?? Sources.FirstOrDefault();

        // 想定形(既知ソース＋ソース後の余分な要素なし)として解析できたか
        bool recognized = source is not null
            && SrcPipelineBuilder.FindSource(parsed.SourceElement) is not null
            && parsed.IntermediateElements.Count == 0;

        _suppressUpdate = true;
        try
        {
            SelectedSource = source;
            if (source is not null)
                RebuildForSource(source, recognized ? parsed : null);
        }
        finally
        {
            _suppressUpdate = false;
        }

        NotRecognized = !recognized;

        // 想定形として解析できた場合は各行から再生成する。
        // 解析できなかったが元文字列がある場合は、内容を失わないようそのまま保持する。
        if (recognized || string.IsNullOrWhiteSpace(currentPipeline))
            UpdatePreview();
        else
            Preview = currentPipeline;
    }

    /// <summary>選択ソースの定義(と任意で解析結果)から各行を組み立て直す。</summary>
    private void RebuildForSource(SrcElementDef source, ParsedSrcPipeline? parsed)
    {
        bool prevSuppress = _suppressUpdate;
        _suppressUpdate = true;
        try
        {
            DetachRows(PropertyRows);
            DetachRows(CapsRows);
            PropertyRows.Clear();
            CapsRows.Clear();

            // ---- プロパティ行 ----
            foreach (var def in source.Properties)
            {
                string? pv = null;
                bool hasParsed = parsed?.Properties.TryGetValue(def.Name, out pv) == true;
                string[] choices = GetDynamicChoices(def.DynamicKey) ?? def.EnumChoices ?? Array.Empty<string>();
                FieldEditKind kind = def.Kind == SrcPropertyKind.Bool ? FieldEditKind.Bool
                    : choices.Length > 0 ? FieldEditKind.Choice
                    : FieldEditKind.Text;

                var (value, finalChoices) = ResolveInitial(kind, choices, hasParsed, pv, def.DefaultValue);
                AddRow(PropertyRows, new PipelineFieldRow
                {
                    Name = def.Name,
                    Description = def.Description,
                    EditKind = kind,
                    Choices = finalChoices,
                    Enabled = hasParsed, // プロパティは明示的に有効化した場合のみ出力する
                    Value = value,
                });
            }

            // 解析結果にあってカタログに無いプロパティは、テキスト行として保持し失われないようにする
            if (parsed is not null)
            {
                foreach (var kv in parsed.Properties)
                {
                    if (source.Properties.Any(p => p.Name == kv.Key))
                        continue;
                    AddRow(PropertyRows, new PipelineFieldRow
                    {
                        Name = kv.Key,
                        EditKind = FieldEditKind.Text,
                        Enabled = true,
                        Value = kv.Value,
                    });
                }
            }

            // ---- caps 行 ----
            CapsEnabled = parsed is null || parsed.HasCaps;
            foreach (var def in source.CapsFields)
            {
                string[] choices = GetDynamicChoices(def.DynamicKey) ?? def.Choices ?? Array.Empty<string>();
                FieldEditKind kind = choices.Length > 0 ? FieldEditKind.Choice : FieldEditKind.Text;

                string? parsedVal = null;
                bool hasParsed;
                if (def.IsResolution)
                {
                    string? w = null, h = null;
                    parsed?.CapsFields.TryGetValue("width", out w);
                    parsed?.CapsFields.TryGetValue("height", out h);
                    parsedVal = SrcPipelineBuilder.JoinResolution(w, h);
                    hasParsed = parsedVal is not null;
                }
                else
                {
                    hasParsed = parsed?.CapsFields.TryGetValue(def.Name, out parsedVal) == true;
                }

                var (value, finalChoices) = ResolveInitial(kind, choices, hasParsed, parsedVal, def.DefaultValue);
                AddRow(CapsRows, new PipelineFieldRow
                {
                    Name = def.Name,
                    EditKind = kind,
                    Choices = finalChoices,
                    Enabled = parsed is null || hasParsed, // 新規ソース時は既定で有効
                    Value = value,
                });
            }
        }
        finally
        {
            _suppressUpdate = prevSuppress;
        }
    }

    // 初期値と、必要に応じて補正した選択肢を決める。
    // - 解析値が選択肢に無い場合は選択肢へ追加して保持する。
    // - 既定値が選択肢に無い場合は先頭候補にフォールバックする。
    private static (string Value, string[] Choices) ResolveInitial(
        FieldEditKind kind, string[] choices, bool hasParsed, string? parsedValue, string? defaultValue)
    {
        string initial = hasParsed ? (parsedValue ?? "") : (defaultValue ?? "");
        if (kind != FieldEditKind.Choice || choices.Length == 0)
            return (initial, choices);

        if (initial.Length > 0 && !choices.Contains(initial))
        {
            if (hasParsed)
                choices = [.. choices, initial]; // 解析値を保持
            else
                initial = choices[0];            // 既定値が候補に無ければ先頭を採用
        }
        return (initial, choices);
    }

    private void AddRow(ObservableCollection<PipelineFieldRow> target, PipelineFieldRow row)
    {
        row.Changed += UpdatePreviewVoid;
        target.Add(row);
    }

    private void DetachRows(IEnumerable<PipelineFieldRow> rows)
    {
        foreach (var row in rows)
            row.Changed -= UpdatePreviewVoid;
    }

    private void UpdatePreviewVoid() => UpdatePreview();

    /// <summary>現在の各行の状態からプレビュー文字列を再生成する。</summary>
    private void UpdatePreview()
    {
        if (_suppressUpdate || SelectedSource is null)
            return;

        var props = PropertyRows.Where(r => r.Enabled).Select(r => (r.Name, r.Value));
        var capsValues = CapsRows
            .Where(r => r.Enabled && !string.IsNullOrEmpty(r.Value))
            .ToDictionary(r => r.Name, r => r.Value);

        Preview = SrcPipelineBuilder.Assemble(SelectedSource, CapsEnabled, props, capsValues);
    }

    /// <summary>動的キーに対応する選択肢を取得する(取得できない場合は null)。</summary>
    private string[]? GetDynamicChoices(string? key)
    {
        try
        {
            switch (key)
            {
                case "monitor-index":
                    return Enumerable.Range(0, Math.Max(1, MonitorCount)).Select(i => i.ToString()).ToArray();
                case "monitor-resolution":
                    // モニターが実際に出す大きさ（DPI 仮想化されていない値）。
                    // 取得できなければ null を返して自由入力へ倒す。
                    return NonEmpty(Monitors.SelectMany(m => m.Resolutions));
                case "mf-device-index":
                    return VideoDevices.Count > 0
                        ? Enumerable.Range(0, VideoDevices.Count).Select(i => i.ToString()).ToArray()
                        : null;
                case "mf-device-name":
                    return NonEmpty(VideoDevices.Select(d => d.Name));
                case "mf-format":
                    return NonEmpty(VideoDevices.SelectMany(d => d.Formats));
                case "mf-resolution":
                    return NonEmpty(VideoDevices.SelectMany(d => d.Resolutions));
                case "mf-framerate":
                    return NonEmpty(VideoDevices.SelectMany(d => d.Framerates));
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }

        static string[]? NonEmpty(IEnumerable<string> values)
        {
            var arr = values.Where(v => !string.IsNullOrEmpty(v)).Distinct().ToArray();
            return arr.Length > 0 ? arr : null;
        }
    }
}
