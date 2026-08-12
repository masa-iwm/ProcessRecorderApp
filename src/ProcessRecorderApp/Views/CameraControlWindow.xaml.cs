using Microsoft.UI.Xaml;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.ViewModels;
using System.Threading.Tasks;

namespace ProcessRecorderApp.Views;

/// <summary>
/// カメラ（<c>mfvideosrc</c>）のフォーカス・明るさ等の編集画面。
///
/// <para>
/// <b>モーダルのダイアログにしない。</b> この機能の価値は
/// 「<b>プレビューを見ながら</b>合わせられること」なのに、<c>ContentDialog</c> は
/// ウィンドウ中央に被さって<b>プレビューを覆い隠す</b>（実機で指摘された）。
/// 別ウィンドウなら脇へ動かせるし、幅も自由に取れるので値の欄が切れない。
/// トリガ一覧エディタ（<c>TriggerListEditorWindow</c>）と同じ形である。
/// </para>
/// <para>
/// <b>スライダーの操作はその場でカメラへ届く。</b> 取り消してもカメラ側の状態は戻らない
/// （ドライバの状態であってアプリの状態ではない）── 戻したいときはレコーダーを
/// 初期化し直せば、保存済みの設定が当て直される。
/// </para>
/// </summary>
public sealed partial class CameraControlWindow : Window
{
    /// <summary>ウィンドウの大きさ（DIP）。物理ピクセルへは表示倍率を掛けて渡す。</summary>
    private const int WidthInDips = 780;

    /// <inheritdoc cref="WidthInDips"/>
    private const int HeightInDips = 640;

    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>画面の状態（開けなかった理由も持つ）。</summary>
    public CameraControlViewModel Vm { get; }

    private CameraControlWindow(CameraControlViewModel vm)
    {
        // x:Bind が初期化される InitializeComponent より前に Vm を用意しておく。
        Vm = vm;
        InitializeComponent();

        Title = Localize("Resources/CameraDialog_WindowTitle");

        // **AppWindow.Resize は物理ピクセルで受ける。** 定数をそのまま渡すと
        // 高 DPI の機械ほど中身が詰まり、右端の「自動」が切れる ──
        // 表示倍率を掛けて、DIP で見た大きさが一定になるようにする。
        // 倍率が分かるのは XamlRoot が付いてから（＝Loaded）なので、そこで測る。
        // Content をキャストしない（WinRT ランタイムクラスへのキャストはトリミング安全でない
        // ── CsWinRT1034）。x:Name で持っている実体を直接使う。
        RootGrid.Loaded += (_, _) =>
        {
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0)
                scale = 1.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)(WidthInDips * scale), (int)(HeightInDips * scale)));
        };

        // 閉じるボタン（×）は取り消し扱い。**必ず 1 回は完了させる** ──
        // させないと呼び出し側の await が永久に返らず、PropertyGrid の行が固まる。
        Closed += (_, _) =>
        {
            Vm.Dispose();
            _completion.TrySetResult(null);
        };
    }

    /// <summary>
    /// カメラ設定の編集画面を開き、確定した設定文字列（取り消しなら null）を返す。
    /// デバイスの解決も接続も専用スレッドで行うので、<b>UI スレッドは止まらない</b>。
    /// </summary>
    /// <param name="srcPipeline">レコーダーの映像ソースのパイプライン文字列。</param>
    /// <param name="current">現在の設定文字列。</param>
    public static async Task<string?> EditAsync(string? srcPipeline, string? current)
    {
        var vm = await CameraControlViewModel.CreateAsync(srcPipeline, current, Localize);
        var window = new CameraControlWindow(vm);
        window.Activate();
        return await window._completion.Task;
    }

    /// <summary>
    /// 文言を resw から引く。
    /// <b>解決できなければ "Resources/" を落としたキーを返す</b>（<c>PropertyGridView</c> の
    /// Category/Description と同じフォールバック設計）── 項目が増えたときに
    /// 例外で落ちるより、英語のキーが出た方がまだ使える。
    /// </summary>
    public static string Localize(string qualifiedKey)
    {
        int separator = qualifiedKey.IndexOf('/');
        string bare = separator >= 0 ? qualifiedKey[(separator + 1)..] : qualifiedKey;
        return Localization.GetStringOrFallback(qualifiedKey, bare);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // **1 行も出せなかったときは何も確定しない。** 空文字を返すとそれまでの設定を消す
        // ──「開けなかった」と「空にしたい」は別物である。
        if (Vm.Items.Count > 0)
            _completion.TrySetResult(Vm.BuildResult());
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
