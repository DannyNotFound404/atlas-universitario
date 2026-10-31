using Atlas.Desktop;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Atlas.Setup;

internal sealed class SetupForm : Form
{
    private static Color Navy => MineralTheme.Palette.Ink;
    private static Color Teal => MineralTheme.Palette.Accent;
    private static Color Pale => MineralTheme.Palette.Canvas;
    private readonly SetupEnvironment setupEnvironment;
    private readonly TextBox nasAddress = new();
    private readonly TextBox nasShare = new();
    private readonly TextBox folders = new();
    private readonly TextBox targetUser = new();
    private readonly CheckBox installBackupNas = new();
    private readonly Label backupDescription = new();
    private readonly TextBox activity = new();
    private readonly MineralProgressBar progress = new();
    private readonly Button installButton = new MineralButton();
    private readonly Button closeButton = new MineralButton();
    private readonly Label stateLabel = new();
    private readonly string? enrollmentToken;
    private readonly InteractiveUserIdentity interactiveUser;
    private bool installationRunning;

    public SetupForm(SetupEnvironment setupEnvironment)
    {
        this.setupEnvironment = setupEnvironment;
        enrollmentToken = ReadEnrollmentToken();
        interactiveUser = InteractiveUserResolver.Resolve();

        Text = $"ATLAS — Instalación segura · {setupEnvironment.Name}";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowIcon = false;
        ClientSize = new Size(760, 730);
        MinimumSize = new Size(640, 610);
        BackColor = Pale;
        ForeColor = Navy;
        Font = new Font("Segoe UI", 10F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        installBackupNas.Text = "Sí, instalar Backup ATLAS en este equipo";
        installBackupNas.Checked = true;
        installBackupNas.AutoSize = true;
        installBackupNas.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        installBackupNas.CheckedChanged += (_, _) =>
            UpdateBackupConfigurationState();

        Panel contentHost = new() { Dock = DockStyle.Fill, AutoScroll = true };
        Control content = BuildContent();
        content.Dock = DockStyle.Top;
        content.MinimumSize = new Size(0, 600);
        contentHost.Controls.Add(content);
        void FitContent()
        {
            content.Width = Math.Max(280, contentHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            content.Height = Math.Max(content.MinimumSize.Height, contentHost.ClientSize.Height);
            int textWidth = Math.Max(240, content.ClientSize.Width - content.Padding.Horizontal);
            stateLabel.MaximumSize = new Size(textWidth, 0);
            backupDescription.MaximumSize = new Size(textWidth, 0);
        }
        contentHost.Resize += (_, _) => FitContent();
        Controls.Add(contentHost);
        Controls.Add(BuildHeader());
        FitContent();

        nasAddress.PlaceholderText = "Ejemplo: 192.0.2.10";
        nasShare.PlaceholderText = "Ejemplo: Colaborador de ejemplo";
        folders.Multiline = true;
        folders.ScrollBars = ScrollBars.Vertical;
        folders.Text = "Documents" + Environment.NewLine + "Desktop";
        targetUser.Text = interactiveUser.AccountName;
        targetUser.ReadOnly = true;
        targetUser.TabStop = false;
        bool loadedPreviousConfiguration =
            TryLoadPreviousBackupConfiguration();
        UpdateBackupConfigurationState();

        activity.Multiline = true;
        activity.ReadOnly = true;
        activity.ScrollBars = ScrollBars.Vertical;
        activity.BackColor = Color.White;
        activity.BorderStyle = BorderStyle.FixedSingle;

        installButton.Text = "Instalar ATLAS";
        installButton.BackColor = Teal;
        installButton.ForeColor = Color.White;
        installButton.FlatStyle = FlatStyle.Flat;
        installButton.FlatAppearance.BorderSize = 0;
        installButton.Height = 44;
        installButton.Click += InstallButton_Click;

        closeButton.Text = "Cerrar";
        closeButton.BackColor = Color.White;
        closeButton.ForeColor = Navy;
        closeButton.FlatStyle = FlatStyle.Flat;
        closeButton.Height = 44;
        closeButton.Click += (_, _) => Close();

        progress.Style = ProgressBarStyle.Blocks;
        progress.MarqueeAnimationSpeed = 28;
        progress.Height = 8;

        if (enrollmentToken is null)
        {
            installButton.Enabled = false;
            stateLabel.Text =
                "Este archivo no contiene una credencial ATLAS válida. " +
                "Descárguelo nuevamente desde el portal.";
            MineralTheme.Role(stateLabel, MineralRole.Error);
        }
        else if (loadedPreviousConfiguration)
        {
            stateLabel.Text =
                "Se detectó una instalación anterior. Revise la configuración " +
                "recuperada; al instalar, ATLAS validará primero su identidad " +
                "con el servidor y migrará únicamente si fue retirada.";
            MineralTheme.Role(stateLabel, MineralRole.Warning);
        }
        else
        {
            stateLabel.Text =
                "Instalador único reconocido. La credencial se consumirá " +
                "al vincular este equipo.";
        }

        stateLabel.Text =
            $"Ambiente {setupEnvironment.Name} · " +
            $"Servidor {setupEnvironment.ServerUrl.Host}" +
            Environment.NewLine +
            stateLabel.Text;
        MineralTheme.Role(installButton, MineralRole.PrimaryButton);
        MineralTheme.Role(backupDescription, MineralRole.Muted);
        MineralTheme.Attach(this);
    }

    private Control BuildHeader()
    {
        Panel header = new MineralHeader()
        {
            Dock = DockStyle.Top,
            Height = 116,
            BackColor = Navy,
            Padding = new Padding(30, 22, 30, 18)
        };
        Label title = new()
        {
            AutoSize = true,
            Text = "ATLAS",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 24F, FontStyle.Bold),
            Location = new Point(70, 18)
        };
        Label mark = new()
        {
            AutoSize = true,
            Text = "◇",
            ForeColor = Teal,
            Font = new Font("Segoe UI Symbol", 30F, FontStyle.Bold),
            Location = new Point(26, 17)
        };
        Label subtitle = new()
        {
            AutoSize = true,
            Text = "Departamento de Ciberseguridad · Instalación del endpoint",
            ForeColor = Color.FromArgb(142, 220, 228),
            Font = new Font("Segoe UI", 10F),
            Location = new Point(72, 70)
        };
        header.Controls.Add(mark);
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        MineralTheme.Role(title, MineralRole.BrandText);
        MineralTheme.Role(mark, MineralRole.BrandAccent);
        MineralTheme.Role(subtitle, MineralRole.BrandMuted);
        MineralThemeButton appearance = new() { Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(598, 22) };
        header.Controls.Add(appearance);
        header.Resize += (_, _) => appearance.Location = new Point(Math.Max(400, header.Width - appearance.Width - 28), 22);
        return header;
    }

    private Control BuildContent()
    {
        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(30, 24, 30, 24),
            ColumnCount = 2,
            RowCount = 11,
            BackColor = Color.White
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label heading = new()
        {
            AutoSize = true,
            Text = "Configuración inicial",
            Font = new Font("Segoe UI", 17F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
        content.Controls.Add(heading, 0, 0);
        content.SetColumnSpan(heading, 2);

        stateLabel.AutoSize = true;
        stateLabel.MaximumSize = new Size(680, 0);
        MineralTheme.Role(stateLabel, MineralRole.Muted);
        stateLabel.Margin = new Padding(0, 0, 0, 18);
        content.Controls.Add(stateLabel, 0, 1);
        content.SetColumnSpan(stateLabel, 2);

        content.Controls.Add(installBackupNas, 0, 2);
        content.SetColumnSpan(installBackupNas, 2);

        AddField(content, "Servidor NAS", nasAddress, 0, 3);
        AddField(content, "Recurso compartido exacto", nasShare, 1, 3);
        AddField(content, "Cuenta Windows que ejecutará la copia", targetUser, 0, 4);
        content.SetColumnSpan(targetUser.Parent!, 2);

        Label folderLabel = new()
        {
            AutoSize = true,
            Text = "Carpetas del perfil — una por línea",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 5)
        };
        content.Controls.Add(folderLabel, 0, 5);
        content.SetColumnSpan(folderLabel, 2);
        folders.Dock = DockStyle.Fill;
        folders.Margin = new Padding(0, 0, 0, 12);
        content.Controls.Add(folders, 0, 6);
        content.SetColumnSpan(folders, 2);

        backupDescription.AutoSize = true;
        backupDescription.Text =
            "ATLAS no solicita ni guarda la contraseña del NAS. " +
            "Indique el nombre que aparece al abrir \\\\servidor y " +
            "BackupNAS validará la credencial al iniciar cada copia.";
        backupDescription.ForeColor = Color.FromArgb(47, 102, 119);
        backupDescription.MaximumSize = new Size(680, 0);
        backupDescription.Margin = new Padding(0, 0, 0, 10);
        content.Controls.Add(backupDescription, 0, 7);
        content.SetColumnSpan(backupDescription, 2);

        activity.Dock = DockStyle.Fill;
        content.Controls.Add(activity, 0, 8);
        content.SetColumnSpan(activity, 2);

        progress.Dock = DockStyle.Fill;
        content.Controls.Add(progress, 0, 9);
        content.SetColumnSpan(progress, 2);

        TableLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0)
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        installButton.Dock = DockStyle.Fill;
        closeButton.Dock = DockStyle.Fill;
        installButton.Margin = new Padding(0, 0, 6, 0);
        closeButton.Margin = new Padding(6, 0, 0, 0);
        buttons.Controls.Add(installButton, 0, 0);
        buttons.Controls.Add(closeButton, 1, 0);
        content.Controls.Add(buttons, 0, 10);
        content.SetColumnSpan(buttons, 2);

        return MineralTheme.Role(content, MineralRole.Surface);
    }

    private static void AddField(
        TableLayoutPanel content,
        string labelText,
        TextBox input,
        int column,
        int row)
    {
        TableLayoutPanel field = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            RowCount = 2,
            Margin = column == 0
                ? new Padding(0, 0, 8, 10)
                : new Padding(8, 0, 0, 10)
        };
        field.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        field.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label label = new()
        {
            AutoSize = true,
            Text = labelText,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 5)
        };
        input.Dock = DockStyle.Fill;
        field.Controls.Add(label, 0, 0);
        field.Controls.Add(input, 0, 1);
        content.Controls.Add(field, column, row);
    }

    private async void InstallButton_Click(object? sender, EventArgs e)
    {
        if (enrollmentToken is null)
        {
            return;
        }

        if (!TryReadConfiguration(out SetupConfiguration configuration))
        {
            return;
        }

        SetInstalling(true);
        activity.Clear();
        AppendActivity("Preparando la instalación segura de ATLAS...");

        try
        {
            await RunInstallerAsync(enrollmentToken, configuration);
            progress.Style = ProgressBarStyle.Blocks;
            progress.Value = 100;
            stateLabel.Text =
                "ATLAS quedó instalado y vinculado correctamente.";
            MineralTheme.Role(stateLabel, MineralRole.Success);
            installButton.Text = "Instalación completada";
            installButton.Enabled = false;
            closeButton.Text = "Finalizar";
            closeButton.Enabled = true;
            installationRunning = false;
            MessageBox.Show(
                this,
                configuration.InstallBackupNas
                    ? "El agente, BackupNAS y el actualizador quedaron instalados."
                    : "El agente y el actualizador quedaron instalados. " +
                      "Este equipo usará Acronis u otro respaldo externo.",
                "ATLAS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Close();
        }
        catch (Exception exception)
        {
            progress.Style = ProgressBarStyle.Blocks;
            progress.Value = 0;
            AppendActivity($"ERROR: {exception.Message}");
            stateLabel.Text =
                "La instalación no terminó. Puede ejecutar nuevamente este " +
                "mismo archivo para reanudarla.";
            MineralTheme.Role(stateLabel, MineralRole.Error);
            SetInstalling(false);
            MessageBox.Show(
                this,
                exception.Message,
                "ATLAS — instalación incompleta",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool TryReadConfiguration(
        out SetupConfiguration configuration)
    {
        configuration = null!;
        if (!installBackupNas.Checked)
        {
            configuration = new SetupConfiguration(
                InstallBackupNas: false,
                NasAddress: null,
                NasShare: null,
                Folders: [],
                TargetUser: null,
                TargetUserSid: null);
            return true;
        }

        string server = nasAddress.Text.Trim();
        string share = nasShare.Text.Trim();
        string account = targetUser.Text.Trim();

        if (string.IsNullOrWhiteSpace(server) ||
            server.Contains('\\') ||
            server.Contains('/') ||
            server.Any(char.IsControl))
        {
            ShowValidation("Ingrese una IPv4 o un nombre válido para el NAS.");
            nasAddress.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(share) ||
            share.Contains('\\') ||
            share.Contains('/') ||
            share.Any(char.IsControl))
        {
            ShowValidation(
                "Ingrese solamente el nombre del recurso compartido del NAS.");
            nasShare.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(account))
        {
            ShowValidation("Indique la cuenta Windows que ejecutará BackupNAS.");
            targetUser.Focus();
            return false;
        }
        if (!string.Equals(
                account,
                interactiveUser.AccountName,
                StringComparison.OrdinalIgnoreCase))
        {
            ShowValidation(
                "La cuenta de BackupNAS ya no coincide con el usuario " +
                "interactivo detectado. Cierre y abra nuevamente el instalador.");
            return false;
        }

        string[] selectedFolders = folders.Text
            .Split(
                ['\r', '\n', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedFolders.Length == 0 ||
            selectedFolders.Length > 32 ||
            selectedFolders.Any(folder => !IsSafeRelativeFolder(folder)))
        {
            ShowValidation(
                "Configure entre 1 y 32 carpetas relativas al perfil, " +
                "por ejemplo Documents o Desktop.");
            folders.Focus();
            return false;
        }

        configuration = new SetupConfiguration(
            InstallBackupNas: true,
            NasAddress: server,
            NasShare: share,
            Folders: selectedFolders,
            TargetUser: account,
            TargetUserSid: interactiveUser.Sid);
        return true;
    }

    private static bool IsSafeRelativeFolder(string folder)
    {
        if (Path.IsPathRooted(folder) ||
            folder.Contains(':') ||
            folder.Any(char.IsControl))
        {
            return false;
        }

        string[] segments = folder.Split(
            ['\\', '/'],
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        return segments.Length > 0 &&
               segments.All(segment =>
                   segment is not "." and not ".." &&
                   segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    private void ShowValidation(string message)
    {
        MessageBox.Show(
            this,
            message,
            "ATLAS — revise la configuración",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private async Task RunInstallerAsync(
        string token,
        SetupConfiguration configuration)
    {
        string staging = Path.Combine(
            Path.GetTempPath(),
            $"ATLAS-Setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            string scriptPath = Path.Combine(staging, "Install-ATLAS.ps1");
            string certificatePath = Path.Combine(
                staging,
                "atlas-root-ca.crt");
            string releasePublicKeyPath = Path.Combine(
                staging,
                "atlas-release-signing.public.pem");
            string bootstrapPath = Path.Combine(
                staging,
                "atlas-bootstrap.json");
            string configurationPath = Path.Combine(
                staging,
                "atlas-setup-configuration.json");

            await ExtractUtf8TextResourceAsync(
                "Atlas.Setup.Install-ATLAS.ps1",
                scriptPath);
            await ExtractResourceAsync(
                "Atlas.Setup.atlas-root-ca.crt",
                certificatePath);
            await ExtractResourceAsync(
                "Atlas.Setup.atlas-release-signing.public.pem",
                releasePublicKeyPath);

            Guid installerId = CreateInstallerId(token);
            object bootstrap = new
            {
                schemaVersion = 1,
                product = "ATLAS",
                installerId,
                serverUrl = setupEnvironment.ServerUrl.AbsoluteUri,
                agencyCode = "",
                enrollmentToken = token,
                expiresAtUtc = DateTimeOffset.UtcNow.AddHours(72)
            };
            await File.WriteAllTextAsync(
                bootstrapPath,
                JsonSerializer.Serialize(bootstrap, JsonOptions));
            await File.WriteAllTextAsync(
                configurationPath,
                JsonSerializer.Serialize(configuration, JsonOptions));

            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            ProcessStartInfo startInfo = new()
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = staging
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ConfigurationPath");
            startInfo.ArgumentList.Add(configurationPath);

            StringBuilder installerOutput = new();
            object outputLock = new();
            using Process process = new() { StartInfo = startInfo };
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    lock (outputLock)
                    {
                        installerOutput.AppendLine(eventArgs.Data);
                    }
                }
                AppendActivity(eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    lock (outputLock)
                    {
                        installerOutput.AppendLine(eventArgs.Data);
                    }
                }
                AppendActivity(eventArgs.Data);
            };

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Windows no pudo iniciar el instalador interno.");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            // Espera también a que los manejadores asíncronos entreguen las
            // últimas líneas, incluido ATLAS_ERROR, antes de construir el aviso.
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string capturedOutput;
                lock (outputLock)
                {
                    capturedOutput = installerOutput.ToString();
                }
                throw new InvalidOperationException(
                    GetInstallerFailureMessage(
                        capturedOutput,
                        process.ExitCode));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task ExtractResourceAsync(
        string resourceName,
        string destination)
    {
        await using Stream source =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(
                resourceName) ??
            throw new InvalidOperationException(
                $"El instalador está incompleto: {resourceName}.");
        await using FileStream target = File.Create(destination);
        await source.CopyToAsync(target);
    }

    private static async Task ExtractUtf8TextResourceAsync(
        string resourceName,
        string destination)
    {
        await using Stream source =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(
                resourceName) ??
            throw new InvalidOperationException(
                $"El instalador está incompleto: {resourceName}.");
        using StreamReader reader = new(
            source,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        string content = await reader.ReadToEndAsync();
        await File.WriteAllTextAsync(
            destination,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private bool TryLoadPreviousBackupConfiguration()
    {
        string configurationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ATLAS",
            "BackupNAS",
            "config.txt");
        if (!File.Exists(configurationPath))
        {
            return false;
        }

        try
        {
            Dictionary<string, string> values = File.ReadLines(configurationPath)
                .Select(line => line.Trim().TrimStart('\uFEFF'))
                .Where(line =>
                    line.Length > 0 &&
                    !line.StartsWith('#') &&
                    line.Contains('='))
                .Select(line => line.Split('=', 2))
                .GroupBy(
                    parts => parts[0].Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last()[1].Trim(),
                    StringComparer.OrdinalIgnoreCase);

            if (values.TryGetValue("NAS_IP", out string? address) &&
                !string.IsNullOrWhiteSpace(address))
            {
                nasAddress.Text = address;
            }
            if (values.TryGetValue("NAS_SHARE", out string? share) &&
                !string.IsNullOrWhiteSpace(share))
            {
                nasShare.Text = share;
            }

            string[] previousFolders = values
                .Where(pair =>
                    pair.Key.StartsWith(
                        "CARPETA",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                .OrderBy(pair => ReadFolderNumber(pair.Key))
                .Select(pair => pair.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (previousFolders.Length > 0)
            {
                folders.Text = string.Join(
                    Environment.NewLine,
                    previousFolders);
            }

            return !string.IsNullOrWhiteSpace(nasAddress.Text) ||
                   !string.IsNullOrWhiteSpace(nasShare.Text) ||
                   previousFolders.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int ReadFolderNumber(string key)
    {
        return int.TryParse(key.AsSpan("CARPETA".Length), out int number)
            ? number
            : int.MaxValue;
    }

    private static string GetInstallerFailureMessage(
        string output,
        int exitCode)
    {
        const string marker = "ATLAS_ERROR:";
        string? installerMessage = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line =>
                line.StartsWith(marker, StringComparison.Ordinal));
        if (installerMessage is not null)
        {
            return installerMessage[marker.Length..].Trim();
        }

        return
            $"La instalación no pudo completarse (código {exitCode}). " +
            "Revise el detalle de actividad o contacte a Ciberseguridad.";
    }

    private void AppendActivity(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendActivity(line));
            return;
        }
        activity.AppendText(line.TrimEnd() + Environment.NewLine);
        activity.SelectionStart = activity.TextLength;
        activity.ScrollToCaret();
    }

    private void SetInstalling(bool installing)
    {
        installationRunning = installing;
        installBackupNas.Enabled = !installing;
        UpdateBackupConfigurationState();
        installButton.Enabled = !installing;
        closeButton.Enabled = !installing;
        progress.Style = installing
            ? ProgressBarStyle.Marquee
            : ProgressBarStyle.Blocks;
        if (installing)
        {
            stateLabel.Text =
                "Instalando y verificando componentes. El movimiento de la " +
                "barra confirma que el proceso continúa activo.";
            MineralTheme.Role(stateLabel, MineralRole.Muted);
        }
    }

    private void UpdateBackupConfigurationState()
    {
        bool enabled = !installationRunning && installBackupNas.Checked;
        nasAddress.Enabled = enabled;
        nasShare.Enabled = enabled;
        folders.Enabled = enabled;
        targetUser.Enabled = enabled;
        backupDescription.Text = installBackupNas.Checked
            ? "ATLAS no solicita ni guarda la contraseña del NAS. " +
              "El recurso debe coincidir exactamente con el nombre " +
              "publicado por el NAS; BackupNAS validará la credencial."
            : "Se instalarán el agente de inventario y el actualizador. " +
              "BackupNAS y su tarea programada no se instalarán.";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (installationRunning)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                "ATLAS todavía está instalando componentes. Espere a que el " +
                "proceso termine antes de cerrar esta ventana.",
                "ATLAS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        base.OnFormClosing(e);
    }

    private static string? ReadEnrollmentToken()
    {
        string executable = Environment.ProcessPath ??
                            Application.ExecutablePath;
        string name = Path.GetFileNameWithoutExtension(executable);
        const string prefix = "ATLAS-Setup_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string token = name[prefix.Length..];
        int duplicateSuffix = token.LastIndexOf(
            " (",
            StringComparison.Ordinal);
        if (duplicateSuffix > 0 &&
            token.EndsWith(')') &&
            int.TryParse(
                token.AsSpan(duplicateSuffix + 2, token.Length - duplicateSuffix - 3),
                out int duplicateNumber) &&
            duplicateNumber > 0)
        {
            token = token[..duplicateSuffix];
        }
        return token.Length is >= 32 and <= 128 &&
               token.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '-' or '_')
            ? token
            : null;
    }

    private static Guid CreateInstallerId(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private sealed record SetupConfiguration(
        bool InstallBackupNas,
        string? NasAddress,
        string? NasShare,
        IReadOnlyList<string> Folders,
        string? TargetUser,
        string? TargetUserSid);
}
