using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Atlas.Desktop;

public enum MineralRole { Default, Surface, Soft, Brand, BrandText, BrandMuted, BrandAccent, Muted, Accent, Success, Warning, Error, Info, SuccessBadge, WarningBadge, ErrorBadge, InfoBadge, PrimaryButton, SageSuccess, SageSuccessBadge }

public sealed record MineralPalette(Color Canvas, Color Surface, Color Soft, Color Raised,
    Color Ink, Color Muted, Color Line, Color Accent, Color AccentHover, Color OnAccent,
    Color Brand, Color BrandInk, Color BrandMuted, Color BrandAccent,
    Color Success, Color SuccessSoft, Color Warning, Color WarningSoft, Color Error, Color ErrorSoft,
    Color Info, Color InfoSoft)
{
    public static MineralPalette Light { get; } = MineralTokenPalettes.Light;
    public static MineralPalette Dark { get; } = MineralTokenPalettes.Dark;
    public static MineralPalette Contrast => new(SystemColors.Window, SystemColors.Window, SystemColors.Window, SystemColors.Control,
        SystemColors.WindowText, SystemColors.WindowText, SystemColors.WindowText, SystemColors.Highlight, SystemColors.Highlight, SystemColors.HighlightText,
        SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText, SystemColors.WindowText,
        SystemColors.WindowText, SystemColors.Window, SystemColors.WindowText, SystemColors.Window, SystemColors.WindowText, SystemColors.Window,
        SystemColors.WindowText, SystemColors.Window);
}

public interface IMineralControl { void ApplyPalette(MineralPalette palette); }

public static class MineralTheme
{
    private sealed class Style { public MineralRole Role; }
    private static readonly ConditionalWeakTable<Control, Style> Styles = new();
    private static string preferenceFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ATLAS-Universidad", "Appearance", "theme.txt");
    private static bool dark = ReadPreference(preferenceFile) ?? true;
    public static bool IsDark => dark;
    public static MineralPalette Palette => SystemInformation.HighContrast ? MineralPalette.Contrast : dark ? MineralPalette.Dark : MineralPalette.Light;
    public static event EventHandler? Changed;

    // Allows an isolated host (including UI verification) to keep preferences in its own directory.
    public static void UsePreferenceFile(string path)
    {
        preferenceFile = Path.GetFullPath(path);
        SetDark(ReadPreference(preferenceFile) ?? true, persist: false);
    }
    public static bool SetDark(bool value, bool persist = true)
    {
        bool changed = dark != value;
        dark = value;
        bool saved = !persist || SavePreference();
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
        return saved;
    }
    public static void RefreshPreference()
    {
        if (ReadPreference(preferenceFile) is bool value) SetDark(value, persist: false);
    }
    private static bool? ReadPreference(string path)
    {
        try { return File.ReadAllText(path).Trim() switch { "dark" => true, "light" => false, _ => null }; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    private static bool SavePreference()
    {
        string temporary = preferenceFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferenceFile)!);
            File.WriteAllText(temporary, dark ? "dark\n" : "light\n");
            File.Move(temporary, preferenceFile, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public static T Role<T>(T control, MineralRole role) where T : Control
    {
        Styles.GetOrCreateValue(control).Role = role;
        Apply(control);
        return control;
    }
    public static void Apply(Control control)
    {
        if (control.IsDisposed) return;
        MineralPalette p = Palette;
        MineralRole role = Styles.TryGetValue(control, out Style? style) ? style.Role : MineralRole.Default;
        Color background = control.Parent?.BackColor ?? p.Canvas;
        Color foreground = p.Ink;
        Color sage = SystemInformation.HighContrast ? p.Ink : (dark ? MineralTokenPalettes.SageDark : MineralTokenPalettes.SageLight);
        Color sageSoft = SystemInformation.HighContrast ? p.Surface : (dark ? MineralTokenPalettes.SageSoftDark : MineralTokenPalettes.SageSoftLight);
        if (control is TextBoxBase box) background = box.ReadOnly ? p.Soft : p.Surface;
        else if (control is ComboBox) background = p.Surface;
        else if (control is Button) background = p.Surface;
        else if (control is Form) background = p.Canvas;
        (background, foreground) = role switch
        {
            MineralRole.Surface => (p.Surface, p.Ink), MineralRole.Soft => (p.Soft, p.Ink),
            MineralRole.Brand => (p.Brand, p.BrandInk), MineralRole.BrandText => (background, p.BrandInk),
            MineralRole.BrandMuted => (background, p.BrandMuted), MineralRole.BrandAccent => (background, p.BrandAccent),
            MineralRole.Muted => (background, p.Muted), MineralRole.Accent => (background, p.Accent),
            MineralRole.Success => (background, p.Success), MineralRole.Warning => (background, p.Warning),
            MineralRole.Error => (background, p.Error), MineralRole.Info => (background, p.Info),
            MineralRole.SuccessBadge => (p.SuccessSoft, p.Success), MineralRole.WarningBadge => (p.WarningSoft, p.Warning),
            MineralRole.ErrorBadge => (p.ErrorSoft, p.Error), MineralRole.InfoBadge => (p.InfoSoft, p.Info),
            MineralRole.SageSuccess => (background, sage), MineralRole.SageSuccessBadge => (sageSoft, sage),
            MineralRole.PrimaryButton => (p.Accent, p.OnAccent), _ => (background, foreground)
        };
        control.BackColor = background;
        control.ForeColor = foreground;
        if (control is MineralButton button) button.Primary = role == MineralRole.PrimaryButton;
        else if (control is Button native)
        {
            native.UseVisualStyleBackColor = false; native.FlatStyle = FlatStyle.Flat;
            native.FlatAppearance.BorderColor = p.Line;
            native.FlatAppearance.MouseOverBackColor = p.Soft;
        }
        if (control is IMineralControl custom) custom.ApplyPalette(p);
        control.Invalidate();
    }
    public static void ApplyTree(Control root)
    {
        Apply(root);
        foreach (Control child in root.Controls) ApplyTree(child);
    }
    public static IDisposable Attach(Form form) => new ThemeScope(form);
    public static bool MotionAllowed
    {
        get
        {
            if (SystemInformation.HighContrast) return false;
            return !SystemParametersInfo(0x1042, 0, out bool enabled, 0) || enabled;
        }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private sealed class ThemeScope : IDisposable
    {
        private readonly Form form;
        private readonly HashSet<Control> controls = new();
        private bool disposed;
        public ThemeScope(Form form)
        {
            this.form = form; MineralIcon.Attach(form); Watch(form); Changed += OnChanged;
            form.Disposed += OnDisposed; form.Activated += OnActivated; form.HandleCreated += OnChanged;
            OnChanged(null, EventArgs.Empty);
        }
        private void Watch(Control control)
        {
            if (!controls.Add(control)) return;
            control.ControlAdded += OnAdded;
            control.Disposed += OnControlDisposed;
            foreach (Control child in control.Controls) Watch(child);
        }
        private void OnControlDisposed(object? sender, EventArgs e)
        {
            if (sender is Control c) { c.ControlAdded -= OnAdded; c.Disposed -= OnControlDisposed; controls.Remove(c); }
        }
        private void OnAdded(object? sender, ControlEventArgs e)
        {
            if (e.Control is not null) { Watch(e.Control); ApplyTree(e.Control); }
        }
        private void OnActivated(object? sender, EventArgs e) { RefreshPreference(); OnChanged(null, EventArgs.Empty); }
        private void OnChanged(object? sender, EventArgs e)
        {
            if (disposed || form.IsDisposed || form.Disposing) return;
            if (form.InvokeRequired) { form.BeginInvoke(() => OnChanged(null, EventArgs.Empty)); return; }
            ApplyTree(form);
            if (form.IsHandleCreated)
            {
                int value = dark && !SystemInformation.HighContrast ? 1 : 0;
                _ = DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int));
            }
        }
        private void OnDisposed(object? sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Changed -= OnChanged;
            form.Disposed -= OnDisposed; form.Activated -= OnActivated; form.HandleCreated -= OnChanged;
            foreach (Control c in controls) { c.ControlAdded -= OnAdded; c.Disposed -= OnControlDisposed; }
            controls.Clear();
        }
    }
}
