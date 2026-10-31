using Atlas.Desktop;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BackupNAS;

internal static class ModernDrawing
{
	public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
	{
		float diameter = Math.Max(1f, radius * 2f);
		GraphicsPath path = new GraphicsPath();
		path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
		path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
		path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
		path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
		path.CloseFigure();
		return path;
	}
}

internal sealed class RoundedPanel : Panel, IMineralControl
{
	private int cornerRadius = 14;

	private Color borderColor = Color.FromArgb(226, 232, 240);

	[DefaultValue(14)]
	public int CornerRadius
	{
		get => cornerRadius;
		set
		{
			cornerRadius = Math.Max(1, value);
			UpdateRegion();
			Invalidate();
		}
	}

	public Color BorderColor
	{
		get => borderColor;
		set
		{
			borderColor = value;
			Invalidate();
		}
	}

	public void ApplyPalette(MineralPalette palette) { BorderColor = palette.Line; }

	public RoundedPanel()
	{
		DoubleBuffered = true;
		Resize += delegate
		{
			UpdateRegion();
		};
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
		using GraphicsPath path = ModernDrawing.RoundedRectangle(bounds, cornerRadius);
		using Pen borderPen = new Pen(borderColor, 1f);
		e.Graphics.DrawPath(borderPen, path);
		base.OnPaint(e);
	}

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? MineralTheme.Palette.Canvas);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = ModernDrawing.RoundedRectangle(new RectangleF(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), cornerRadius);
        using SolidBrush brush = new(BackColor);
        e.Graphics.FillPath(brush, path);
    }

	private void UpdateRegion()
	{
		if (Width <= 0 || Height <= 0)
		{
			return;
		}
		using GraphicsPath path = ModernDrawing.RoundedRectangle(new RectangleF(0f, 0f, Width, Height), cornerRadius);
		Region?.Dispose();
		Region = new Region(path);
	}
}

internal sealed class ModernProgressBar : Control, IMineralControl
{
	private readonly System.Windows.Forms.Timer animationTimer;

	private double maximum = 100.0;

	private double targetValue;

	private double displayedValue;

	private bool isActive;

	private double shimmerPosition;

	public Color TrackColor { get; set; } = Color.FromArgb(226, 238, 240);

	public Color StartColor { get; set; } = Color.FromArgb(0, 168, 190);

	public Color EndColor { get; set; } = Color.FromArgb(0, 145, 176);

	public double Minimum { get; set; }

	public double Maximum
	{
		get => maximum;
		set
		{
			maximum = Math.Max(1.0, value);
			Value = targetValue;
		}
	}

	public double Value
	{
		get => targetValue;
		set
		{
			targetValue = Math.Clamp(value, Minimum, Maximum);
			if (MineralTheme.MotionAllowed) animationTimer.Start(); else displayedValue = targetValue;
			Invalidate();
		}
	}

	public bool IsActive
	{
		get => isActive;
		set
		{
			isActive = value;
			if (value)
			{
				if (MineralTheme.MotionAllowed) animationTimer.Start(); else displayedValue = targetValue;
			}
			Invalidate();
		}
	}

    public void ApplyPalette(MineralPalette palette)
    {
        TrackColor = palette.Raised; StartColor = palette.Accent; EndColor = palette.AccentHover;
        if (!MineralTheme.MotionAllowed) { displayedValue = targetValue; animationTimer.Stop(); }
        Invalidate();
    }

    public ModernProgressBar()
	{
		SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
		Height = 18;
		animationTimer = new System.Windows.Forms.Timer
		{
			Interval = 30
		};
		animationTimer.Tick += delegate
		{
			double difference = targetValue - displayedValue;
			if (Math.Abs(difference) < 0.04)
			{
				displayedValue = targetValue;
			}
			else
			{
				displayedValue += difference * 0.18;
			}
			if (isActive && MineralTheme.MotionAllowed)
			{
				shimmerPosition += 0.018;
				if (shimmerPosition > 1.0)
				{
					shimmerPosition = 0.0;
				}
			}
			Invalidate();
			if (!isActive && displayedValue == targetValue)
			{
				animationTimer.Stop();
			}
		};
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		RectangleF bounds = new RectangleF(0f, 0f, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
		float radius = Math.Max(2f, bounds.Height / 2f);
		using GraphicsPath trackPath = ModernDrawing.RoundedRectangle(bounds, radius);
		using SolidBrush trackBrush = new SolidBrush(TrackColor);
		e.Graphics.FillPath(trackBrush, trackPath);

		double ratio = Maximum <= Minimum ? 0.0 : (displayedValue - Minimum) / (Maximum - Minimum);
		float fillWidth = (float)Math.Clamp(ratio * bounds.Width, 0.0, bounds.Width);
		if (fillWidth > 1f)
		{
			RectangleF fillBounds = new RectangleF(bounds.Left, bounds.Top, Math.Max(bounds.Height, fillWidth), bounds.Height);
			using GraphicsPath fillPath = ModernDrawing.RoundedRectangle(fillBounds, radius);
			using LinearGradientBrush fillBrush = new LinearGradientBrush(fillBounds, StartColor, EndColor, LinearGradientMode.Horizontal);
			GraphicsState state = e.Graphics.Save();
			e.Graphics.SetClip(trackPath);
			e.Graphics.FillPath(fillBrush, fillPath);
			e.Graphics.Restore(state);
		}

		if (isActive && MineralTheme.MotionAllowed)
		{
			float pulseWidth = Math.Max(22f, bounds.Width * 0.12f);
			float pulseX = (float)(-pulseWidth + shimmerPosition * (bounds.Width + pulseWidth * 2f));
			RectangleF pulseBounds = new RectangleF(pulseX, bounds.Top, pulseWidth, bounds.Height);
			using LinearGradientBrush pulseBrush = new LinearGradientBrush(
				pulseBounds,
				Color.FromArgb(0, Color.White),
				Color.FromArgb(120, Color.White),
				LinearGradientMode.Horizontal);
			pulseBrush.SetBlendTriangularShape(0.5f, 1f);
			GraphicsState state = e.Graphics.Save();
			e.Graphics.SetClip(trackPath);
			e.Graphics.FillRectangle(pulseBrush, pulseBounds);
			e.Graphics.Restore(state);
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			animationTimer.Dispose();
		}
		base.Dispose(disposing);
	}
}

internal sealed class ModernButton : MineralButton { }

internal sealed class PasswordRevealButton : Control
{
	private bool hovered;

	private bool revealing;

	public event EventHandler? RevealChanged;

	public bool IsRevealing => revealing;

	public int CornerRadius { get; set; } = 8;

	public PasswordRevealButton()
	{
		Cursor = Cursors.Hand;
		TabStop = true;
		AccessibleName = "Mantener presionado para mostrar la contraseña";
		AccessibleDescription = "La contraseña se oculta al soltar el botón.";
		AccessibleRole = AccessibleRole.PushButton;
		SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.UserPaint, true);
	}

	protected override void OnMouseEnter(EventArgs e)
	{
		hovered = true;
		Invalidate();
		base.OnMouseEnter(e);
	}

	protected override void OnMouseLeave(EventArgs e)
	{
		hovered = false;
		Capture = false;
		SetRevealing(false);
		Invalidate();
		base.OnMouseLeave(e);
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		if (e.Button == MouseButtons.Left && Enabled)
		{
			Focus();
			Capture = true;
			SetRevealing(true);
		}
		base.OnMouseDown(e);
	}

	protected override void OnMouseUp(MouseEventArgs e)
	{
		if (e.Button == MouseButtons.Left)
		{
			Capture = false;
			SetRevealing(false);
		}
		base.OnMouseUp(e);
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && Enabled)
		{
			SetRevealing(true);
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
		base.OnKeyDown(e);
	}

	protected override void OnKeyUp(KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
		{
			SetRevealing(false);
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
		base.OnKeyUp(e);
	}

	protected override void OnLostFocus(EventArgs e)
	{
		SetRevealing(false);
		base.OnLostFocus(e);
	}

	protected override void OnEnabledChanged(EventArgs e)
	{
		if (!Enabled)
		{
			SetRevealing(false);
		}
		Invalidate();
		base.OnEnabledChanged(e);
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		RectangleF bounds = new RectangleF(0.5f, 0.5f, Math.Max(1f, Width - 1f), Math.Max(1f, Height - 1f));
		if (hovered || revealing || Focused)
		{
			using GraphicsPath backgroundPath = ModernDrawing.RoundedRectangle(bounds, CornerRadius);
			using SolidBrush backgroundBrush = new SolidBrush(revealing ? MineralTheme.Palette.Raised : MineralTheme.Palette.Soft);
			e.Graphics.FillPath(backgroundBrush, backgroundPath);
		}

		Color iconColor = Enabled ? MineralTheme.Palette.Accent : MineralTheme.Palette.Muted;
		float eyeWidth = Math.Min(18f, Math.Max(12f, Width - 10f));
		float eyeHeight = Math.Min(10f, Math.Max(7f, Height - 14f));
		float left = (Width - eyeWidth) / 2f;
		float top = (Height - eyeHeight) / 2f;
		float right = left + eyeWidth;
		float middleY = top + eyeHeight / 2f;
		using GraphicsPath eyePath = new GraphicsPath();
		eyePath.AddBezier(left, middleY, left + eyeWidth * 0.22f, top, right - eyeWidth * 0.22f, top, right, middleY);
		eyePath.AddBezier(right, middleY, right - eyeWidth * 0.22f, top + eyeHeight, left + eyeWidth * 0.22f, top + eyeHeight, left, middleY);
		using Pen eyePen = new Pen(iconColor, 1.6f);
		e.Graphics.DrawPath(eyePen, eyePath);
		float pupilSize = revealing ? 5f : 4f;
		using SolidBrush pupilBrush = new SolidBrush(iconColor);
		e.Graphics.FillEllipse(pupilBrush, (Width - pupilSize) / 2f, (Height - pupilSize) / 2f, pupilSize, pupilSize);
	}

	private void SetRevealing(bool value)
	{
		if (revealing == value)
		{
			return;
		}
		revealing = value;
		Invalidate();
		RevealChanged?.Invoke(this, EventArgs.Empty);
	}
}
