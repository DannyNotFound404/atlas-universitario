using Atlas.Desktop;
using Miguel.Contracts;

namespace BackupNAS;

internal sealed class BackupResultDialog : Form
{
    private readonly IDisposable theme;

    internal BackupResultDialog(BackupRunReport? report, string summary)
    {
        bool pending = report is not null && BackupTelemetry.IsAcceptableWithPendingFiles(report);
        bool complete = report is not null && BackupTelemetry.IsAcceptable(report);
        MineralRole tone = pending ? MineralRole.SageSuccess : complete ? MineralRole.Success :
            report?.Status == BackupRunStatus.Failed ? MineralRole.Error : MineralRole.Info;
        MineralRole badge = pending ? MineralRole.SageSuccessBadge : complete ? MineralRole.SuccessBadge :
            report?.Status == BackupRunStatus.Failed ? MineralRole.ErrorBadge : MineralRole.InfoBadge;
        string title = pending || complete ? "Copia aceptable" : report?.Status switch
        {
            BackupRunStatus.Cancelled => "Copia cancelada",
            BackupRunStatus.Skipped => "Copia no iniciada",
            BackupRunStatus.Failed => "La copia no pudo completarse",
            BackupRunStatus.Partial => "Carpetas pendientes de confirmar",
            _ => "Resultado pendiente de confirmar"
        };

        Text = "ATLAS Backup · Resultado";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(650, 550);
        MinimumSize = new Size(470, 380);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(layout);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 14) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var icon = MineralTheme.Role(new Label { Text = pending || complete ? "✓" : "•", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 25F, FontStyle.Bold), AccessibleName = title }, tone);
        header.Controls.Add(icon, 0, 0); header.SetRowSpan(icon, 2);
        header.Controls.Add(MineralTheme.Role(new Label { Text = title, Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 17F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, tone), 1, 0);
        long failures = report?.Evidence?.Folders.Sum(f => f.FilesFailed ?? 0) ?? 0;
        var status = MineralTheme.Role(new Label { Text = pending ? failures + (failures == 1 ? " archivo pendiente · ver detalle" : " archivos pendientes · ver detalle") :
            complete ? "Ejecución finalizada" : "Consulta el resultado de esta ejecución", AutoSize = true,
            Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 3, 0, 0) }, badge);
        header.Controls.Add(status, 1, 1);
        layout.Controls.Add(header, 0, 0);

        var details = new TextBox { Text = summary.Replace("\n", Environment.NewLine), Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            Margin = new Padding(0, 4, 0, 8), Font = new Font("Segoe UI", 10.5F), AccessibleName = "Detalle del resultado de la copia" };
        layout.Controls.Add(details, 0, 1);
        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var okay = MineralTheme.Role(new MineralButton { Text = "Entendido", DialogResult = DialogResult.OK,
            Size = new Size(130, 40), Margin = new Padding(0, 12, 0, 0) }, MineralRole.PrimaryButton);
        actions.Controls.Add(okay); layout.Controls.Add(actions, 0, 2);
        AcceptButton = okay; CancelButton = okay;
        theme = MineralTheme.Attach(this);
        Shown += (_, _) => { details.SelectionStart = 0; details.SelectionLength = 0; okay.Focus(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) theme.Dispose();
        base.Dispose(disposing);
    }
}
