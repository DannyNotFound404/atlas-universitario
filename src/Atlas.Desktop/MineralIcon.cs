using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Atlas.Desktop;

public static class MineralIcon
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
    public static Icon Create()
    {
        using Bitmap bitmap = new(64, 64, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
        using SolidBrush background = new(MineralPalette.Dark.Brand); g.FillEllipse(background, 2, 2, 60, 60);
        PointF[] shield = [new(32,11),new(48,17),new(46,38),new(32,52),new(18,38),new(16,17),new(32,11)];
        using Pen border = new(MineralPalette.Dark.Accent, 4) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(border, shield);
        using Pen detail = new(MineralPalette.Dark.Ink, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(detail,25,25,39,25);g.DrawLine(detail,25,32,39,32);g.DrawLine(detail,28,39,36,39);
        IntPtr handle = bitmap.GetHicon();
        try { using Icon borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    public static void Attach(Form form)
    {
        Icon icon = Create(); form.Icon = icon; form.ShowIcon = true;
        form.Disposed += (_, _) => icon.Dispose();
    }
}
