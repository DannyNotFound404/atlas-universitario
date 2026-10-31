using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Atlas.Desktop;

public static class MineralDrawing
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        GraphicsPath path = new();
        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}

public class MineralButton : Button
{
    private bool hovered;
    private bool pressed;
    [DefaultValue(false)] public bool Primary { get; set; }
    [DefaultValue(10)] public int CornerRadius { get; set; } = 10;
    public MineralButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        Height = 42;
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        MineralPalette p = MineralTheme.Palette;
        e.Graphics.Clear(Parent?.BackColor ?? p.Canvas);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF bounds = new(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        using GraphicsPath path = MineralDrawing.Round(bounds, CornerRadius * DeviceDpi / 96F);
        Color fill = !Enabled ? p.Raised : Primary ? (hovered || pressed ? p.AccentHover : p.Accent) : hovered || pressed ? p.Soft : p.Surface;
        Color text = !Enabled ? p.Muted : Primary ? p.OnAccent : p.Ink;
        using SolidBrush brush = new(fill); e.Graphics.FillPath(brush, path);
        using Pen border = new(Focused && ShowFocusCues ? p.Accent : Primary && Enabled ? p.Accent : p.Line, Focused && ShowFocusCues ? 2 : 1);
        e.Graphics.DrawPath(border, path);
        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(Rectangle.Round(bounds), -7, -2), text, flags);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), text, fill);
    }
}

public sealed class MineralThemeButton : MineralButton, IMineralControl
{
    public MineralThemeButton()
    {
        Size = new Size(132, 36); TabStop = true;
        Click += (_, _) =>
        {
            bool saved = MineralTheme.SetDark(!MineralTheme.IsDark);
            if (!saved) AccessibleDescription = "Apariencia cambiada para esta sesión. No se pudo guardar la preferencia.";
        };
        ApplyPalette(MineralTheme.Palette);
    }
    public void ApplyPalette(MineralPalette palette)
    {
        Text = MineralTheme.IsDark ? "Modo oscuro" : "Modo claro";
        AccessibleName = Text;
        AccessibleDescription = MineralTheme.IsDark ? "Cambiar a Grafito Acero, modo claro." : "Cambiar a Grafito Acero, modo oscuro.";
        Invalidate();
    }
}

public sealed class MineralHeader : Panel
{
    public MineralHeader()
    {
        DoubleBuffered = true; MineralTheme.Role(this, MineralRole.Brand);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (SystemInformation.HighContrast) return;
        MineralPalette p = MineralTheme.Palette;
        int step = Math.Max(16, 20 * DeviceDpi / 96);
        using Pen pen = new(Color.FromArgb(22, p.BrandAccent));
        int start = Math.Max(0, Width - 280 * DeviceDpi / 96);
        for (int x = start; x < Width; x += step) e.Graphics.DrawLine(pen, x, 0, x, Height);
        for (int y = 0; y < Height; y += step) e.Graphics.DrawLine(pen, start, y, Width, y);
    }
}

public sealed class MineralProgressBar : Control, IMineralControl
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 35 };
    private ProgressBarStyle style;
    private int value;
    private int maximum = 100;
    private int phase;
    [DefaultValue(ProgressBarStyle.Blocks)] public ProgressBarStyle Style { get => style; set { style = value; UpdateAnimation(); Invalidate(); } }
    [DefaultValue(100)] public int Maximum { get => maximum; set { maximum = Math.Max(1, value); Value = this.value; } }
    [DefaultValue(0)] public int Value { get => value; set { this.value = Math.Clamp(value, 0, maximum); Invalidate(); } }
    [DefaultValue(35)] public int MarqueeAnimationSpeed { get => timer.Interval; set => timer.Interval = Math.Max(15, value); }
    public MineralProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 8; TabStop = false; AccessibleRole = AccessibleRole.ProgressBar;
        timer.Tick += (_, _) => { phase = (phase + 2) % 120; Invalidate(); };
    }
    public void ApplyPalette(MineralPalette palette) { UpdateAnimation(); Invalidate(); }
    protected override void OnVisibleChanged(EventArgs e) { UpdateAnimation(); base.OnVisibleChanged(e); }
    protected override void OnHandleCreated(EventArgs e) { UpdateAnimation(); base.OnHandleCreated(e); }
    private void UpdateAnimation() => timer.Enabled = !IsDisposed && IsHandleCreated && Visible && style == ProgressBarStyle.Marquee && MineralTheme.MotionAllowed;
    protected override void OnPaint(PaintEventArgs e)
    {
        MineralPalette p = MineralTheme.Palette;
        e.Graphics.Clear(Parent?.BackColor ?? p.Canvas); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath track = MineralDrawing.Round(new RectangleF(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), Height / 2F);
        using SolidBrush trackBrush = new(p.Raised); e.Graphics.FillPath(trackBrush, track);
        e.Graphics.SetClip(track);
        float width = style == ProgressBarStyle.Marquee ? Width * .3F : Width * ((float)value / maximum);
        float x = style == ProgressBarStyle.Marquee ? (phase / 100F * Width) - width : 0;
        if (style == ProgressBarStyle.Marquee && !timer.Enabled) x = (Width - width) / 2;
        using SolidBrush fill = new(p.Accent); e.Graphics.FillRectangle(fill, x, 0, width, Height);
        e.Graphics.ResetClip();
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
