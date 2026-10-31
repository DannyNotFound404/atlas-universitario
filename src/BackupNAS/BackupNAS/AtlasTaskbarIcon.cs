using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BackupNAS;

internal static class AtlasTaskbarIcon
{
	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyIcon(IntPtr handle);

	public static Icon Create()
	{
		using Bitmap bitmap = new Bitmap(
			64,
			64,
			PixelFormat.Format32bppArgb);
		using Graphics graphics = Graphics.FromImage(bitmap);
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		graphics.Clear(Color.Transparent);

		using SolidBrush background = new SolidBrush(
			Color.FromArgb(7, 42, 70));
		graphics.FillEllipse(background, 2, 2, 60, 60);

		PointF[] shield =
		{
			new PointF(32f, 11f),
			new PointF(48f, 17f),
			new PointF(46f, 38f),
			new PointF(32f, 52f),
			new PointF(18f, 38f),
			new PointF(16f, 17f),
			new PointF(32f, 11f)
		};
		using Pen shieldPen = new Pen(
			Color.FromArgb(0, 190, 210),
			4f)
		{
			LineJoin = LineJoin.Round,
			StartCap = LineCap.Round,
			EndCap = LineCap.Round
		};
		graphics.DrawLines(shieldPen, shield);

		using Pen detailPen = new Pen(Color.White, 3f)
		{
			StartCap = LineCap.Round,
			EndCap = LineCap.Round
		};
		graphics.DrawLine(detailPen, 25f, 25f, 39f, 25f);
		graphics.DrawLine(detailPen, 25f, 32f, 39f, 32f);
		graphics.DrawLine(detailPen, 28f, 39f, 36f, 39f);

		IntPtr handle = bitmap.GetHicon();
		try
		{
			using Icon borrowed = Icon.FromHandle(handle);
			return (Icon)borrowed.Clone();
		}
		finally
		{
			DestroyIcon(handle);
		}
	}
}
