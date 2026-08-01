using Microsoft.UI.Xaml.Controls;
using ProcessRecorderApp.ViewModels;

namespace ProcessRecorderApp.Views;

/// <summary>
/// SrcPipeline の編集支援ダイアログ。ソース選択・プロパティ・caps を指定して
/// パイプライン文字列を生成する。OK 押下時は <see cref="ResultPipeline"/> に確定文字列を格納する。
/// </summary>
public sealed partial class PipelineBuilderDialog : ContentDialog
{
    /// <summary>ビルダーの状態(ソース/プロパティ/caps/プレビュー)を保持するビューモデル。</summary>
    public PipelineBuilderViewModel Vm { get; }

    /// <summary>OK 確定時のパイプライン文字列。キャンセル時は null のまま。</summary>
    public string? ResultPipeline { get; private set; }

    public PipelineBuilderDialog(string? currentPipeline)
    {
        // x:Bind が初期化される InitializeComponent より前に Vm を用意しておく
        Vm = new PipelineBuilderViewModel(currentPipeline);
        InitializeComponent();
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 手動修正も反映できるよう、プレビュー(編集可能)の内容を採用する
        ResultPipeline = Vm.Preview;
    }
}
