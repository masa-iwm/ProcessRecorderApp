// AppIcon.ico 生成スクリプト
// デスクトップ/Webカメラ録画アプリ用アイコン: ダーク背景 + モニター + 赤い録画ドット
// 実行方法: dotnet script GenerateAppIcon.csx
// (要 dotnet-script: dotnet tool install -g dotnet-script)
#r "nuget: System.Drawing.Common, 9.0.0"

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// スクリプト自身のフォルダを取得(出力先の基準)
static string GetScriptFolder([CallerFilePath] string path = null) => Path.GetDirectoryName(path);

// 角丸矩形パスを作成
static GraphicsPath RoundedRect(RectangleF r, float rad)
{
    var p = new GraphicsPath();
    float d = rad * 2f;
    p.AddArc(r.Left, r.Top, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

// 指定サイズでアイコンを描画
static Bitmap Draw(int s)
{
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float S = s;

        // 背景: 角丸スクエア(ダークスレートの縦グラデーション)
        var bgRect = new RectangleF(0, 0, S, S);
        using (var path = RoundedRect(bgRect, S * 0.22f))
        using (var brush = new LinearGradientBrush(bgRect,
            Color.FromArgb(255, 0x46, 0x51, 0x6B),
            Color.FromArgb(255, 0x1F, 0x25, 0x34), 90f))
            g.FillPath(brush, path);

        // モニター画面(明るいパネル)
        var screen = new RectangleF(S * 0.16f, S * 0.17f, S * 0.68f, S * 0.47f);
        using (var sp = RoundedRect(screen, S * 0.06f))
        using (var sb = new LinearGradientBrush(screen,
            Color.FromArgb(255, 0xF5, 0xF8, 0xFC),
            Color.FromArgb(255, 0xD5, 0xDE, 0xEA), 90f))
            g.FillPath(sb, sp);

        // モニタースタンド(支柱と台座)
        using (var wb = new SolidBrush(Color.FromArgb(255, 0xC9, 0xD3, 0xE0)))
        {
            g.FillRectangle(wb, S * 0.45f, S * 0.64f, S * 0.10f, S * 0.10f);
            using (var basePath = RoundedRect(new RectangleF(S * 0.30f, S * 0.73f, S * 0.40f, S * 0.075f), S * 0.0375f))
                g.FillPath(wb, basePath);
        }

        // 録画ドット(画面中央の赤い円、左上にハイライト)
        float cx = screen.Left + screen.Width / 2f;
        float cy = screen.Top + screen.Height / 2f;
        float r = S * 0.13f;
        var dotRect = new RectangleF(cx - r, cy - r, r * 2f, r * 2f);
        using (var dotPath = new GraphicsPath())
        {
            dotPath.AddEllipse(dotRect);
            using (var pgb = new PathGradientBrush(dotPath))
            {
                pgb.CenterColor = Color.FromArgb(255, 0xFF, 0x5F, 0x52);
                pgb.SurroundColors = new[] { Color.FromArgb(255, 0xD8, 0x23, 0x1C) };
                pgb.CenterPoint = new PointF(cx - r * 0.3f, cy - r * 0.35f);
                g.FillPath(pgb, dotPath);
            }
        }
    }
    return bmp;
}

// 32bit BMP(XOR + ANDマスク)形式にエンコード(小サイズ用)
static byte[] EncodeBmp(Bitmap bmp)
{
    int s = bmp.Width;
    int maskStride = ((s + 31) / 32) * 4;
    var ms = new MemoryStream();
    var bw = new BinaryWriter(ms);
    bw.Write(40); bw.Write(s); bw.Write(s * 2);
    bw.Write((short)1); bw.Write((short)32);
    bw.Write(0); bw.Write(s * 4 * s + maskStride * s);
    bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
    var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var row = new byte[s * 4];
    for (int y = s - 1; y >= 0; y--)
    {
        Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, s * 4);
        bw.Write(row);
    }
    bmp.UnlockBits(data);
    var maskRow = new byte[maskStride]; // 透過はアルファチャンネルで表現するためマスクは全て0
    for (int y = 0; y < s; y++) bw.Write(maskRow);
    return ms.ToArray();
}

static byte[] EncodePng(Bitmap bmp)
{
    var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

// 複数サイズをまとめて ICO ファイルとして書き出す
static void Build(string path, int[] sizes)
{
    var entries = new List<Tuple<int, byte[]>>();
    foreach (int s in sizes)
    {
        using (var bmp = Draw(s))
            entries.Add(Tuple.Create(s, s >= 128 ? EncodePng(bmp) : EncodeBmp(bmp)));
    }
    using (var fs = File.Create(path))
    using (var bw = new BinaryWriter(fs))
    {
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)entries.Count);
        int offset = 6 + 16 * entries.Count;
        foreach (var e in entries)
        {
            bw.Write((byte)(e.Item1 == 256 ? 0 : e.Item1));
            bw.Write((byte)(e.Item1 == 256 ? 0 : e.Item1));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(e.Item2.Length); bw.Write(offset);
            offset += e.Item2.Length;
        }
        foreach (var e in entries) bw.Write(e.Item2);
    }
}

// 確認用にプレビュー PNG も書き出す
static void SavePreview(string path, int size)
{
    using (var bmp = Draw(size))
        bmp.Save(path, ImageFormat.Png);
}

// ---- エントリーポイント ----

// 出力先: このスクリプトと同じフォルダの AppIcon.ico
var outPath = Path.Combine(GetScriptFolder(), "AppIcon.ico");
Build(outPath, new[] { 16, 24, 32, 48, 64, 128, 256 });

// プレビュー PNG は一時フォルダに出力(リポジトリを汚さない)
var preview = Path.Combine(Path.GetTempPath(), "AppIconPreview");
Directory.CreateDirectory(preview);
foreach (var s in new[] { 16, 32, 256 })
    SavePreview(Path.Combine(preview, $"icon_{s}.png"), s);

// 生成した ICO が正しく読み込めるか検証
using (var icon = new Icon(outPath, 32, 32))
    Console.WriteLine($"ICO loaded OK: {icon.Width}x{icon.Height}");
Console.WriteLine($"Done: {outPath} ({new FileInfo(outPath).Length} bytes)");
Console.WriteLine($"Preview PNGs: {preview}");
