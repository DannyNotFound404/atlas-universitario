using Atlas.Desktop;
using Miguel.Contracts;

namespace Atlas.Publisher;

internal sealed class PublisherForm : Form
{
    private static Color Navy => MineralTheme.Palette.Ink;
    private static Color NavySoft => MineralTheme.Palette.Muted;
    private static Color Teal => MineralTheme.Palette.Accent;
    private static Color TealDark => MineralTheme.Palette.Accent;
    private static Color Canvas => MineralTheme.Palette.Canvas;
    private static Color Border => MineralTheme.Palette.Line;
    private static Color Muted => MineralTheme.Palette.Muted;
    private static Color Success => MineralTheme.Palette.Success;
    private static Color Warning => MineralTheme.Palette.Warning;

    private readonly TextBox privateKey = CreateTextBox();
    private readonly TextBox password = CreateTextBox();
    private readonly TextBox distribution = CreateTextBox();
    private readonly TextBox releaseVersion = CreateTextBox();
    private readonly TextBox previousReleaseVersion = CreateTextBox();
    private readonly TextBox releaseSummary = CreateTextBox();
    private readonly TextBox releaseFeatures = CreateTextBox();
    private readonly TextBox releaseChanges = CreateTextBox();
    private readonly TextBox restoreOutcome = CreateTextBox();
    private readonly RichTextBox activity = new();
    private readonly ComboBox releaseProfile = new();
    private readonly Button browseKeyButton = CreateSecondaryButton("Examinar");
    private readonly Button revealPasswordButton = CreateSecondaryButton("Mostrar");
    private readonly Button browseDistributionButton = CreateSecondaryButton("Examinar");
    private readonly Button inspectButton = CreateSecondaryButton("Verificar");
    private readonly Button createKeyButton = CreateSecondaryButton("Crear un par de claves");
    private readonly Button buildButton = CreatePrimaryButton("Crear release firmado");
    private readonly Button cancelButton = CreateSecondaryButton("Cancelar");
    private readonly Button closeButton = CreateSecondaryButton("Cerrar");
    private readonly MineralProgressBar progress = new();
    private readonly Label phaseDescription = CreateBodyLabel(string.Empty);
    private readonly Label distributionStatus = CreateBodyLabel(
        "Seleccione una distribución para verificar sus componentes.");
    private readonly Label releaseReadiness = CreateBodyLabel(
        "Pendiente: seleccione y verifique una distribución.");
    private readonly Label compatibilityStatus = CreateBodyLabel(
        "La compatibilidad se calculará al verificar la distribución.");
    private readonly FlowLayoutPanel componentList = new();
    private readonly Label operationStatus = CreateStatusLabel("Listo", Success);
    private readonly ToolTip hints = new();
    private CancellationTokenSource? operationCancellation;
    private DistributionInspection? inspectedDistribution;
    private bool operationRunning;
    private string lastSuggestedVersion = string.Empty;

    public PublisherForm()
    {
        Text = "ATLAS Universidad - Publisher 0.3.1 U2";
        StartPosition = FormStartPosition.CenterScreen;
        Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(
            Math.Min(1020, Math.Max(740, workingArea.Width - 48)),
            Math.Min(920, Math.Max(640, workingArea.Height - 72)));
        MinimumSize = new Size(
            Math.Min(780, ClientSize.Width),
            Math.Min(680, ClientSize.Height));
        BackColor = Canvas;
        ForeColor = Navy;
        Font = new Font("Segoe UI", 10F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        Panel contentHost = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Canvas
        };
        Control content = BuildContent();
        contentHost.Controls.Add(content);
        void FitContentToViewport()
        {
            int availableWidth = Math.Max(
                680,
                contentHost.ClientSize.Width -
                SystemInformation.VerticalScrollBarWidth);
            content.MaximumSize = new Size(availableWidth, 0);
            content.Width = availableWidth;
        }
        contentHost.Resize += (_, _) => FitContentToViewport();
        Controls.Add(contentHost);
        Controls.Add(BuildHeader());
        FitContentToViewport();

        password.UseSystemPasswordChar = true;
        privateKey.PlaceholderText = "Seleccione la clave privada cifrada (.pem)";
        distribution.PlaceholderText = "Seleccione atlas-distribution-*";
        releaseVersion.PlaceholderText = "Ejemplo: 2026.09.05.1-rc";
        previousReleaseVersion.PlaceholderText =
            "Ejemplo: 2026.08.06.1-golden (opcional para el primer release)";
        releaseSummary.PlaceholderText =
            "Explique en una frase el propósito de esta versión.";
        releaseFeatures.PlaceholderText =
            "Una funcionalidad por línea.";
        releaseChanges.PlaceholderText =
            "Una mejora frente a la versión anterior por línea.";
        restoreOutcome.PlaceholderText =
            "Indique qué componentes y capacidades se obtendrían al recuperar esta versión.";
        ConfigureNarrativeBox(releaseSummary, 58);
        ConfigureNarrativeBox(releaseFeatures, 82);
        ConfigureNarrativeBox(releaseChanges, 82);
        ConfigureNarrativeBox(restoreOutcome, 68);

        releaseProfile.DropDownStyle = ComboBoxStyle.DropDownList;
        releaseProfile.FlatStyle = FlatStyle.Flat;
        releaseProfile.DrawMode = DrawMode.OwnerDrawFixed;
        releaseProfile.ItemHeight = 30;
        releaseProfile.BackColor = Color.White;
        releaseProfile.ForeColor = Navy;
        releaseProfile.Font = new Font("Segoe UI", 10F);
        releaseProfile.Items.AddRange(
        [
            new ReleaseProfileItem(
                ReleaseBuildProfile.Standard,
                "Release estándar · recomendado"),
            new ReleaseProfileItem(
                ReleaseBuildProfile.UpdaterBridge,
                "Compatibilidad excepcional · updater antiguo")
        ]);
        releaseProfile.SelectedIndex = 0;
        releaseProfile.DrawItem += ReleaseProfile_DrawItem;

        activity.ReadOnly = true;
        activity.BorderStyle = BorderStyle.None;
        activity.BackColor = Color.White;
        activity.ForeColor = NavySoft;
        activity.Font = new Font("Cascadia Mono", 9F);
        activity.DetectUrls = false;
        activity.TabStop = false;
        activity.Text =
            "ATLAS Publisher está listo. La clave privada nunca se carga " +
            "al servidor." + Environment.NewLine;

        componentList.AutoSize = true;
        componentList.Dock = DockStyle.Fill;
        componentList.WrapContents = true;
        componentList.FlowDirection = FlowDirection.LeftToRight;
        componentList.Margin = new Padding(0, 8, 0, 0);

        progress.Dock = DockStyle.Fill;
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 24;
        progress.Visible = false;

        cancelButton.Visible = false;

        hints.SetToolTip(
            revealPasswordButton,
            "Mantenga presionado para mostrar la contraseña.");
        hints.SetToolTip(
            createKeyButton,
            "Crea una clave privada cifrada y su clave pública correspondiente.");
        hints.SetToolTip(
            releaseProfile,
            "Use el modo excepcional solo cuando cambie el protocolo que entiende el updater instalado.");
        hints.SetToolTip(
            previousReleaseVersion,
            "Versión utilizada como referencia para explicar las mejoras de esta publicación.");

        browseKeyButton.Click += BrowseKey;
        browseDistributionButton.Click += BrowseDistribution;
        inspectButton.Click += InspectButton_Click;
        createKeyButton.Click += CreateKeyButton_Click;
        buildButton.Click += BuildButton_Click;
        cancelButton.Click += (_, _) => operationCancellation?.Cancel();
        closeButton.Click += (_, _) => Close();
        releaseProfile.SelectedIndexChanged += ReleaseProfile_SelectedIndexChanged;
        distribution.TextChanged += (_, _) =>
        {
            InvalidateInspection();
            UpdateReleaseReadiness();
        };
        foreach (TextBox input in new[]
                 {
                     privateKey,
                     password,
                     releaseVersion,
                     releaseSummary,
                     releaseFeatures,
                     releaseChanges,
                     restoreOutcome
                 })
        {
            input.TextChanged += (_, _) => UpdateReleaseReadiness();
        }
        revealPasswordButton.MouseDown += (_, _) => password.UseSystemPasswordChar = false;
        revealPasswordButton.MouseUp += (_, _) => password.UseSystemPasswordChar = true;
        revealPasswordButton.MouseLeave += (_, _) => password.UseSystemPasswordChar = true;
        KeyDown += PublisherForm_KeyDown;

        UpdateReleaseProfile(forceVersion: true);
        UpdateReleaseReadiness();
        MineralTheme.Role(compatibilityStatus, MineralRole.InfoBadge);
        MineralTheme.Attach(this);
    }

    private Control BuildHeader()
    {
        Panel header = new MineralHeader()
        {
            Dock = DockStyle.Top,
            Height = 116,
            BackColor = Navy,
            Padding = new Padding(28, 18, 28, 16)
        };

        AtlasLogo logo = new()
        {
            Location = new Point(28, 24),
            Size = new Size(54, 54),
            Anchor = AnchorStyles.Left | AnchorStyles.Top
        };
        header.Controls.Add(logo);

        Label title = new()
        {
            AutoSize = true,
            Text = "ATLAS PUBLISHER",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 22F, FontStyle.Bold),
            Location = new Point(94, 18)
        };
        header.Controls.Add(title);

        Label subtitle = new()
        {
            AutoSize = true,
            Text = "Firma y publicación controlada de actualizaciones",
            ForeColor = Color.FromArgb(148, 224, 232),
            Font = new Font("Segoe UI", 10F),
            Location = new Point(97, 65)
        };
        header.Controls.Add(subtitle);

        Label localOnly = new()
        {
            AutoSize = true,
            Text = "FIRMA LOCAL · ECDSA P-256",
            ForeColor = Color.FromArgb(167, 229, 235),
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            Anchor = AnchorStyles.Top
        };
        header.Controls.Add(localOnly);
        MineralTheme.Role(title, MineralRole.BrandText);
        MineralTheme.Role(subtitle, MineralRole.BrandMuted);
        MineralTheme.Role(localOnly, MineralRole.BrandAccent);
        MineralThemeButton appearance = new() { Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(852, 22) };
        header.Controls.Add(appearance);
        header.Resize += (_, _) =>
        {
            localOnly.Location = new Point(
                Math.Max(500, header.ClientSize.Width - localOnly.Width - 30),
                78);
            localOnly.Visible = header.ClientSize.Width >= 950;
            appearance.Location = new Point(Math.Max(490, header.Width - appearance.Width - 28), 22);
        };

        return header;
    }

    private Control BuildContent()
    {
        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 22, 24, 26),
            ColumnCount = 1,
            RowCount = 7,
            BackColor = Canvas
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        content.Controls.Add(BuildIntroduction(), 0, 0);
        content.Controls.Add(BuildKeyCard(), 0, 1);
        content.Controls.Add(BuildReleaseCard(), 0, 2);
        content.Controls.Add(BuildActivityCard(), 0, 3);

        progress.Margin = new Padding(0, 12, 0, 0);
        progress.Height = 6;
        content.Controls.Add(progress, 0, 4);

        content.Controls.Add(BuildActions(), 0, 5);

        Label footer = new()
        {
            AutoSize = true,
            Text = "ATLAS · Departamento de Ciberseguridad",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.5F),
            Margin = new Padding(2, 18, 0, 0)
        };
        content.Controls.Add(footer, 0, 6);
        return content;
    }

    private Control BuildIntroduction()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 16)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        TableLayoutPanel copy = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        copy.Controls.Add(CreateEyebrow("PUBLICACIÓN SEGURA"), 0, 0);
        Label description = CreateBodyLabel(
            "Prepare releases firmados, verifique cada componente y " +
            "despliegue por etapas sin exponer la clave privada.");
        description.MaximumSize = new Size(560, 0);
        description.Margin = new Padding(0, 4, 12, 0);
        copy.Controls.Add(description, 0, 1);
        panel.Controls.Add(copy, 0, 0);

        operationStatus.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        operationStatus.Margin = new Padding(12, 10, 0, 0);
        panel.Controls.Add(operationStatus, 1, 0);
        panel.SetRowSpan(operationStatus, 2);
        return panel;
    }

    private Control BuildKeyCard()
    {
        AtlasCard card = CreateCard();
        TableLayoutPanel layout = CreateCardLayout(rowCount: 8);
        card.Controls.Add(layout);

        AddCardHeading(
            layout,
            0,
            "1. Autoridad de firma",
            "La clave privada permanece en este equipo y solo se usa en memoria.");
        AddCaption(layout, 2, "Clave privada cifrada");
        AddInputWithButton(layout, 3, privateKey, browseKeyButton);
        AddCaption(layout, 4, "Contraseña de la clave");
        AddInputWithButton(layout, 5, password, revealPasswordButton);

        FlowLayoutPanel actions = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0)
        };
        createKeyButton.Width = 230;
        actions.Controls.Add(createKeyButton);
        layout.Controls.Add(actions, 0, 6);
        layout.SetColumnSpan(actions, 2);
        return card;
    }

    private Control BuildReleaseCard()
    {
        AtlasCard card = CreateCard();
        TableLayoutPanel layout = CreateCardLayout(rowCount: 25);
        card.Controls.Add(layout);

        AddCardHeading(
            layout,
            0,
            "2. Preparar publicación",
            "Publisher comprobará nombres, versiones, tamaños y SHA-256 antes de firmar.");

        AddCaption(layout, 2, "Etapa del despliegue");
        releaseProfile.Dock = DockStyle.Fill;
        releaseProfile.Height = 38;
        releaseProfile.Margin = new Padding(0, 2, 0, 5);
        layout.Controls.Add(releaseProfile, 0, 3);
        layout.SetColumnSpan(releaseProfile, 2);

        MineralTheme.Role(phaseDescription, MineralRole.Accent);
        phaseDescription.BackColor = Color.FromArgb(229, 247, 250);
        phaseDescription.Padding = new Padding(12, 9, 12, 9);
        phaseDescription.Margin = new Padding(0, 4, 0, 12);
        phaseDescription.Dock = DockStyle.Fill;
        phaseDescription.AutoSize = false;
        phaseDescription.Height = 64;
        phaseDescription.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(phaseDescription, 0, 4);
        layout.SetColumnSpan(phaseDescription, 2);

        AddCaption(layout, 5, "Carpeta de distribución");
        AddInputWithTwoButtons(
            layout,
            6,
            distribution,
            browseDistributionButton,
            inspectButton);

        AddCaption(layout, 7, "Versión del release");
        releaseVersion.Dock = DockStyle.Fill;
        releaseVersion.Margin = new Padding(0, 2, 0, 8);
        layout.Controls.Add(releaseVersion, 0, 8);
        layout.SetColumnSpan(releaseVersion, 2);

        AddCaption(layout, 9, "Versión anterior de referencia");
        previousReleaseVersion.Dock = DockStyle.Fill;
        previousReleaseVersion.Margin = new Padding(0, 2, 0, 8);
        layout.Controls.Add(previousReleaseVersion, 0, 10);
        layout.SetColumnSpan(previousReleaseVersion, 2);

        AddCaption(layout, 11, "Resumen ejecutivo");
        AddNarrativeInput(layout, 12, releaseSummary);
        AddCaption(layout, 13, "Funcionalidades incluidas · una por línea");
        AddNarrativeInput(layout, 14, releaseFeatures);
        AddCaption(layout, 15, "Qué ofrece sobre la anterior · una mejora por línea");
        AddNarrativeInput(layout, 16, releaseChanges);
        AddCaption(layout, 17, "Resultado esperado de una recuperación");
        AddNarrativeInput(layout, 18, restoreOutcome);

        Label narrativeHelp = CreateBodyLabel(
            "Las notas se firman en un anexo del release. No cambian el " +
            "manifiesto que consumen los actualizadores ya instalados.");
        MineralTheme.Role(narrativeHelp, MineralRole.Accent);
        narrativeHelp.Margin = new Padding(0, 3, 0, 9);
        narrativeHelp.Dock = DockStyle.Fill;
        narrativeHelp.AutoSize = false;
        narrativeHelp.Height = 44;
        narrativeHelp.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(narrativeHelp, 0, 19);
        layout.SetColumnSpan(narrativeHelp, 2);

        distributionStatus.ForeColor = Muted;
        distributionStatus.Margin = new Padding(0, 5, 0, 0);
        layout.Controls.Add(distributionStatus, 0, 20);
        layout.SetColumnSpan(distributionStatus, 2);

        layout.Controls.Add(componentList, 0, 21);
        layout.SetColumnSpan(componentList, 2);

        compatibilityStatus.ForeColor = Muted;
        compatibilityStatus.BackColor = Color.FromArgb(229, 247, 250);
        compatibilityStatus.Padding = new Padding(12, 9, 12, 9);
        compatibilityStatus.Margin = new Padding(0, 8, 0, 0);
        compatibilityStatus.Dock = DockStyle.Fill;
        compatibilityStatus.AutoSize = false;
        compatibilityStatus.Height = 86;
        compatibilityStatus.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(compatibilityStatus, 0, 22);
        layout.SetColumnSpan(compatibilityStatus, 2);

        releaseReadiness.ForeColor = Warning;
        releaseReadiness.BackColor = Color.FromArgb(255, 247, 229);
        releaseReadiness.Padding = new Padding(12, 9, 12, 9);
        releaseReadiness.Margin = new Padding(0, 8, 0, 0);
        releaseReadiness.Dock = DockStyle.Fill;
        releaseReadiness.AutoSize = false;
        releaseReadiness.Height = 46;
        releaseReadiness.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(releaseReadiness, 0, 23);
        layout.SetColumnSpan(releaseReadiness, 2);
        return card;
    }

    private Control BuildActivityCard()
    {
        AtlasCard card = CreateCard();
        TableLayoutPanel layout = CreateCardLayout(rowCount: 4);
        card.Controls.Add(layout);
        AddCardHeading(
            layout,
            0,
            "3. Registro de la operación",
            "La actividad mostrada no contiene la contraseña de la clave.");
        activity.Dock = DockStyle.Fill;
        activity.Height = 150;
        activity.Margin = new Padding(0, 10, 0, 0);
        layout.Controls.Add(activity, 0, 2);
        layout.SetColumnSpan(activity, 2);
        return card;
    }

    private Control BuildActions()
    {
        TableLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            Margin = new Padding(0, 14, 0, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));

        cancelButton.Dock = DockStyle.Fill;
        cancelButton.Margin = new Padding(0, 0, 10, 0);
        closeButton.Dock = DockStyle.Fill;
        closeButton.Margin = new Padding(0, 0, 10, 0);
        buildButton.Dock = DockStyle.Fill;
        buildButton.Margin = Padding.Empty;
        actions.Controls.Add(cancelButton, 1, 0);
        actions.Controls.Add(closeButton, 2, 0);
        actions.Controls.Add(buildButton, 3, 0);
        return actions;
    }

    private static AtlasCard CreateCard() => new(Border)
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        Padding = new Padding(22),
        Margin = new Padding(0, 0, 0, 16),
        BackColor = Color.White
    };

    private static TableLayoutPanel CreateCardLayout(int rowCount)
    {
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = rowCount,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        return layout;
    }

    private static void AddCardHeading(
        TableLayoutPanel layout,
        int row,
        string title,
        string subtitle)
    {
        Label heading = new()
        {
            AutoSize = true,
            Text = title,
            ForeColor = Navy,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            Margin = Padding.Empty
        };
        layout.Controls.Add(heading, 0, row);
        layout.SetColumnSpan(heading, 2);

        Label help = CreateBodyLabel(subtitle);
        help.ForeColor = Muted;
        help.Margin = new Padding(0, 3, 0, 14);
        layout.Controls.Add(help, 0, row + 1);
        layout.SetColumnSpan(help, 2);
    }

    private static void AddCaption(
        TableLayoutPanel layout,
        int row,
        string text)
    {
        Label caption = new()
        {
            AutoSize = true,
            Text = text,
            ForeColor = Navy,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 5, 0, 3)
        };
        layout.Controls.Add(caption, 0, row);
        layout.SetColumnSpan(caption, 2);
    }

    private static void AddInputWithButton(
        TableLayoutPanel layout,
        int row,
        TextBox input,
        Button button)
    {
        TableLayoutPanel inputRow = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 2, 10, 8);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 2, 0, 8);
        inputRow.Controls.Add(input, 0, 0);
        inputRow.Controls.Add(button, 1, 0);
        layout.Controls.Add(inputRow, 0, row);
        layout.SetColumnSpan(inputRow, 2);
    }

    private static void AddInputWithTwoButtons(
        TableLayoutPanel layout,
        int row,
        TextBox input,
        Button first,
        Button second)
    {
        TableLayoutPanel inputRow = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 2, 10, 8);
        first.Dock = DockStyle.Fill;
        second.Dock = DockStyle.Fill;
        first.Margin = new Padding(0, 2, 8, 8);
        second.Margin = new Padding(0, 2, 0, 8);
        inputRow.Controls.Add(input, 0, 0);
        inputRow.Controls.Add(first, 1, 0);
        inputRow.Controls.Add(second, 2, 0);
        layout.Controls.Add(inputRow, 0, row);
        layout.SetColumnSpan(inputRow, 2);
    }

    private static void AddNarrativeInput(
        TableLayoutPanel layout,
        int row,
        TextBox input)
    {
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 2, 0, 8);
        layout.Controls.Add(input, 0, row);
        layout.SetColumnSpan(input, 2);
    }

    private static void ConfigureNarrativeBox(TextBox input, int height)
    {
        input.Multiline = true;
        input.AcceptsReturn = true;
        input.ScrollBars = ScrollBars.Vertical;
        input.Height = height;
    }

    private static TextBox CreateTextBox() => new()
    {
        AutoSize = false,
        Height = 38,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        ForeColor = Navy,
        Font = new Font("Segoe UI", 10F)
    };

    private static Button CreatePrimaryButton(string text) => MineralTheme.Role(CreateButton(text), MineralRole.PrimaryButton);

    private static Button CreateSecondaryButton(string text) => CreateButton(text);

    private static Button CreateButton(string text) => new MineralButton()
    {
        Text = text,
        AutoSize = false,
        Height = 42,
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false,
        Cursor = Cursors.Hand,
        Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
    };

    private static Label CreateEyebrow(string text) => MineralTheme.Role(new Label()
    {
        AutoSize = true,
        Text = text,
        ForeColor = TealDark,
        Font = new Font("Segoe UI", 8F, FontStyle.Bold),
        Margin = Padding.Empty
    }, MineralRole.Accent);

    private static Label CreateBodyLabel(string text) => MineralTheme.Role(new Label()
    {
        AutoSize = true,
        Text = text,
        ForeColor = Muted,
        Font = new Font("Segoe UI", 9F),
        MaximumSize = new Size(760, 0)
    }, MineralRole.Muted);

    private static Label CreateStatusLabel(string text, Color color) => MineralTheme.Role(new Label()
    {
        AutoSize = true,
        Text = text,
        ForeColor = color,
        BackColor = Color.FromArgb(232, 247, 241),
        Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
        Padding = new Padding(12, 7, 12, 7)
    }, MineralRole.SuccessBadge);

    private void BrowseKey(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new()
        {
            Filter = "Clave privada PEM (*.pem)|*.pem|Todos los archivos|*.*",
            CheckFileExists = true,
            Title = "Seleccione la clave privada de releases"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            privateKey.Text = dialog.FileName;
        }
    }

    private async void BrowseDistribution(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Seleccione la carpeta atlas-distribution-*",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            distribution.Text = dialog.SelectedPath;
            await InspectDistributionAsync();
        }
    }

    private async void InspectButton_Click(object? sender, EventArgs e) =>
        await InspectDistributionAsync();

    private async Task<bool> InspectDistributionAsync()
    {
        if (!Directory.Exists(distribution.Text.Trim()))
        {
            ShowError("Seleccione una carpeta de distribución válida.");
            distribution.Focus();
            return false;
        }

        SetRunning(true, "Verificando distribución…", cancellable: true);
        try
        {
            inspectedDistribution = await ReleaseBuilder.InspectDistributionAsync(
                distribution.Text,
                Append,
                operationCancellation!.Token);
            RenderInspection(inspectedDistribution);
            Append(
                $"Distribución verificada: " +
                $"{inspectedDistribution.Packages.Count} componentes.");
            return true;
        }
        catch (OperationCanceledException)
        {
            inspectedDistribution = null;
            RenderInspection(null);
            Append("Verificación cancelada.");
            return false;
        }
        catch (Exception exception)
        {
            inspectedDistribution = null;
            RenderInspection(null);
            ShowError(exception.Message);
            return false;
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void RenderInspection(DistributionInspection? inspection)
    {
        componentList.SuspendLayout();
        componentList.Controls.Clear();
        if (inspection is null)
        {
            compatibilityStatus.Text =
                "La compatibilidad se calculará al verificar la distribución.";
            MineralTheme.Role(compatibilityStatus, MineralRole.InfoBadge);
            distributionStatus.Text =
                "La distribución todavía no ha sido verificada.";
            MineralTheme.Role(distributionStatus, MineralRole.Warning);
        }
        else
        {
            ReleaseCompatibility compatibility = ReleaseBuilder.GetCompatibility(inspection.Packages);
            compatibilityStatus.Text =
                $"Actualizador incluido: {compatibility.TargetUpdaterVersion}" + Environment.NewLine +
                $"Los equipos deben tener Updater {compatibility.MinimumUpdaterVersion} o posterior." +
                (compatibility.IncludesCoordinatedUpdater
                    ? Environment.NewLine + "Para esta transición, asigne primero un equipo piloto en ATLAS."
                    : string.Empty);
            MineralTheme.Role(compatibilityStatus, MineralRole.InfoBadge);
            distributionStatus.Text =
                "Integridad correcta · todos los archivos coinciden con el catálogo.";
            MineralTheme.Role(distributionStatus, MineralRole.Success);
            foreach (AtlasReleasePackage package in inspection.Packages)
            {
                componentList.Controls.Add(CreateComponentChip(package));
            }
        }
        componentList.ResumeLayout(performLayout: true);
        UpdateReleaseReadiness();
    }

    private static Control CreateComponentChip(AtlasReleasePackage package)
    {
        string label = package.Component switch
        {
            AtlasReleaseComponents.Agent => "Agente",
            AtlasReleaseComponents.BackupNas => "BackupNAS",
            AtlasReleaseComponents.BackupService => "Servicio VSS",
            AtlasReleaseComponents.Updater => "Updater",
            AtlasReleaseComponents.Setup => "Instalador",
            _ => package.Component
        };
        return MineralTheme.Role(new Label
        {
            AutoSize = true,
            Text = $"✓  {label}  {package.Version}",
            ForeColor = Success,
            BackColor = Color.FromArgb(232, 247, 241),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0, 0, 8, 8)
        }, MineralRole.SuccessBadge);
    }

    private void CreateKeyButton_Click(object? sender, EventArgs e)
    {
        if (password.Text.Length < 14)
        {
            ShowError(
                "Escriba primero una contraseña de al menos 14 caracteres.");
            password.Focus();
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Filter = "Clave privada cifrada (*.pem)|*.pem",
            FileName = "atlas-release-signing.private.pem",
            AddExtension = true,
            OverwritePrompt = false,
            Title = "Guardar la nueva clave privada de releases"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            (string privatePath, string publicPath) =
                ReleaseBuilder.CreateKeyPair(
                    dialog.FileName,
                    password.Text.AsSpan());
            privateKey.Text = privatePath;
            Append($"Clave privada creada: {privatePath}");
            Append($"Clave pública creada: {publicPath}");
            MessageBox.Show(
                this,
                "La ceremonia de clave terminó correctamente. Guarde la " +
                "clave privada y su contraseña en ubicaciones separadas; " +
                "copie al servidor únicamente la clave pública.",
                "ATLAS · Clave creada",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void BuildButton_Click(object? sender, EventArgs e)
    {
        if (!File.Exists(privateKey.Text.Trim()))
        {
            ShowError("Seleccione una clave privada cifrada válida.");
            privateKey.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(releaseVersion.Text) ||
            string.IsNullOrEmpty(password.Text))
        {
            ShowError("Complete la versión y la contraseña de la clave.");
            return;
        }
        if (string.IsNullOrWhiteSpace(releaseSummary.Text) ||
            string.IsNullOrWhiteSpace(releaseFeatures.Text) ||
            string.IsNullOrWhiteSpace(releaseChanges.Text) ||
            string.IsNullOrWhiteSpace(restoreOutcome.Text))
        {
            ShowError(
                "Complete el resumen, las funcionalidades, las mejoras y el resultado de recuperación.");
            return;
        }
        if (inspectedDistribution is null ||
            !PathEquals(inspectedDistribution.Directory, distribution.Text))
        {
            bool valid = await InspectDistributionAsync();
            if (!valid)
            {
                return;
            }
        }

        ReleaseBuildProfile profile = SelectedProfile;
        using SaveFileDialog dialog = new()
        {
            Filter = "Release firmado de ATLAS (*.atlasrelease)|*.atlasrelease",
            FileName = $"ATLAS-{releaseVersion.Text.Trim()}.atlasrelease",
            AddExtension = true,
            OverwritePrompt = true,
            Title = "Guardar release firmado"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!ConfirmPublication(profile, dialog.FileName))
        {
            Append("Publicación cancelada antes de utilizar la clave privada.");
            return;
        }

        SetRunning(true, "Firmando release…", cancellable: true);
        try
        {
            AtlasReleaseManifest manifest = await ReleaseBuilder.BuildAsync(
                distribution.Text,
                privateKey.Text,
                password.Text.AsMemory(),
                releaseVersion.Text,
                new ReleaseNarrative(
                    previousReleaseVersion.Text,
                    releaseSummary.Text,
                    ParseNarrativeLines(releaseFeatures.Text),
                    ParseNarrativeLines(releaseChanges.Text),
                    restoreOutcome.Text),
                dialog.FileName,
                profile,
                Append,
                operationCancellation!.Token,
                ReleaseBuilder.GetCompatibility(inspectedDistribution!.Packages).MinimumUpdaterVersion);
            string archiveSha256 = await ReleaseBuilder.ComputeFileSha256Async(
                dialog.FileName,
                operationCancellation.Token);
            Append($"Release creado: {manifest.ReleaseId}");
            Append($"Archivo: {Path.GetFullPath(dialog.FileName)}");
            Append($"SHA-256: {archiveSha256.ToUpperInvariant()}");
            Append($"Actualizador mínimo firmado: {manifest.MinimumUpdaterVersion}.");
            password.Clear();
            MessageBox.Show(
                this,
                ReleaseBuilder.GetCompatibility(manifest.Packages).IncludesCoordinatedUpdater
                    ? "El paquete quedó firmado y verificado. Cárguelo en ATLAS > " +
                      "Distribución sin activarlo como versión general. Para esta transición, " +
                      "desmarque Requerir actualizador coordinado y asígnelo a un equipo piloto."
                    : profile == ReleaseBuildProfile.UpdaterBridge
                    ? "La publicación de compatibilidad excepcional quedó " +
                      "firmada y verificada. Despliéguela primero al anillo " +
                      "PILOTO y confirme el updater antes de continuar."
                    : "El release quedó firmado, reabierto y verificado. " +
                      "El SHA-256 del archivo aparece en el registro.",
                "ATLAS Publisher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            Append("Firma cancelada; no se publicó ningún archivo incompleto.");
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void ReleaseProfile_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateReleaseProfile(forceVersion: false);
        UpdateReleaseReadiness();
    }

    private bool ConfirmPublication(
        ReleaseBuildProfile profile,
        string outputPath)
    {
        string components = inspectedDistribution is null
            ? "sin verificar"
            : string.Join(
                ", ",
                inspectedDistribution.Packages.Select(
                    package => $"{package.Component} {package.Version}"));
        ReleaseCompatibility? compatibility = inspectedDistribution is null
            ? null : ReleaseBuilder.GetCompatibility(inspectedDistribution.Packages);
        string mode = profile == ReleaseBuildProfile.Standard
            ? "Release estándar (recomendado)"
            : "Compatibilidad excepcional con updater antiguo";
        string warning = compatibility?.IncludesCoordinatedUpdater == true
            ? Environment.NewLine + Environment.NewLine +
              "Para esta transición, cargue el paquete en ATLAS y asígnelo " +
              "primero a un equipo piloto. La firma no activa la distribución."
            : profile == ReleaseBuildProfile.UpdaterBridge
            ? Environment.NewLine + Environment.NewLine +
              "ATENCIÓN: este modo solo corresponde a una transición del " +
              "protocolo de actualización. Debe probarse primero en PILOTO."
            : string.Empty;

        DialogResult result = MessageBox.Show(
            this,
            $"Revise el comprobante antes de firmar:" +
            Environment.NewLine + Environment.NewLine +
            $"Modo: {mode}" + Environment.NewLine +
            $"Versión: {releaseVersion.Text.Trim()}" + Environment.NewLine +
            $"Componentes: {components}" + Environment.NewLine +
            $"Actualizador mínimo del equipo: {compatibility?.MinimumUpdaterVersion}" + Environment.NewLine +
            $"Destino: {Path.GetFullPath(outputPath)}" +
            warning + Environment.NewLine + Environment.NewLine +
            "¿Desea crear y firmar este release?",
            "ATLAS Publisher · Confirmar publicación",
            MessageBoxButtons.YesNo,
            profile == ReleaseBuildProfile.UpdaterBridge
                ? MessageBoxIcon.Warning
                : MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes;
    }

    private static string[] ParseNarrativeLines(string value) =>
        value.Split(
                ["\r\n", "\n", "\r"],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

    private void ReleaseProfile_DrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= releaseProfile.Items.Count)
        {
            return;
        }

        string text = releaseProfile.Items[e.Index]?.ToString() ?? string.Empty;
        Color foreground = (e.State & DrawItemState.Selected) != 0
            ? SystemColors.HighlightText
            : Navy;
        TextRenderer.DrawText(
            e.Graphics,
            text,
            releaseProfile.Font,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height),
            foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private void UpdateReleaseProfile(bool forceVersion)
    {
        ReleaseBuildProfile profile = SelectedProfile;
        string suggestion = profile == ReleaseBuildProfile.UpdaterBridge
            ? DateTimeOffset.Now.ToString("yyyy.MM.dd") + ".1-bridge"
            : DateTimeOffset.Now.ToString("yyyy.MM.dd") + ".1-rc";
        if (forceVersion ||
            string.IsNullOrWhiteSpace(releaseVersion.Text) ||
            string.Equals(
                releaseVersion.Text,
                lastSuggestedVersion,
                StringComparison.Ordinal))
        {
            releaseVersion.Text = suggestion;
        }
        lastSuggestedVersion = suggestion;

        phaseDescription.Text = profile == ReleaseBuildProfile.UpdaterBridge
            ? "Uso excepcional: prepare primero la compatibilidad del updater " +
              "cuando cambie el protocolo de actualización. Requiere validación " +
              "previa en el anillo PILOTO."
            : "Opción recomendada para publicaciones ordinarias. Conserva el " +
              "manifiesto compatible y firma por separado las funcionalidades, " +
              "la comparación y el resultado de recuperación.";
        MineralTheme.Role(phaseDescription, profile == ReleaseBuildProfile.UpdaterBridge
            ? MineralRole.WarningBadge : MineralRole.InfoBadge);
    }

    private ReleaseBuildProfile SelectedProfile =>
        releaseProfile.SelectedItem is ReleaseProfileItem item
            ? item.Profile
            : ReleaseBuildProfile.Standard;

    private void UpdateReleaseReadiness()
    {
        List<string> pending = [];
        if (!File.Exists(privateKey.Text.Trim()))
        {
            pending.Add("clave privada");
        }
        if (password.Text.Length == 0)
        {
            pending.Add("contraseña");
        }
        if (string.IsNullOrWhiteSpace(releaseVersion.Text))
        {
            pending.Add("versión");
        }
        if (string.IsNullOrWhiteSpace(releaseSummary.Text) ||
            string.IsNullOrWhiteSpace(releaseFeatures.Text) ||
            string.IsNullOrWhiteSpace(releaseChanges.Text) ||
            string.IsNullOrWhiteSpace(restoreOutcome.Text))
        {
            pending.Add("notas completas");
        }
        if (inspectedDistribution is null ||
            !PathEquals(inspectedDistribution.Directory, distribution.Text))
        {
            pending.Add("distribución verificada");
        }

        bool ready = pending.Count == 0;
        releaseReadiness.Text = ready
            ? "✓  Todo listo para revisar el comprobante y firmar."
            : "Pendiente: " + string.Join(", ", pending) + ".";
        MineralTheme.Role(releaseReadiness, ready ? MineralRole.SuccessBadge : MineralRole.WarningBadge);
    }

    private void InvalidateInspection()
    {
        if (inspectedDistribution is not null &&
            !PathEquals(inspectedDistribution.Directory, distribution.Text))
        {
            inspectedDistribution = null;
            RenderInspection(null);
        }
    }

    private static bool PathEquals(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void SetRunning(
        bool running,
        string status = "Listo",
        bool cancellable = false)
    {
        operationCancellation?.Dispose();
        operationCancellation = running ? new CancellationTokenSource() : null;
        operationRunning = running;

        createKeyButton.Enabled = !running;
        buildButton.Enabled = !running;
        browseKeyButton.Enabled = !running;
        browseDistributionButton.Enabled = !running;
        inspectButton.Enabled = !running;
        privateKey.Enabled = !running;
        password.Enabled = !running;
        distribution.Enabled = !running;
        releaseVersion.Enabled = !running;
        previousReleaseVersion.Enabled = !running;
        releaseSummary.Enabled = !running;
        releaseFeatures.Enabled = !running;
        releaseChanges.Enabled = !running;
        restoreOutcome.Enabled = !running;
        releaseProfile.Enabled = !running;
        revealPasswordButton.Enabled = !running;
        closeButton.Enabled = !running;
        cancelButton.Visible = running && cancellable;
        cancelButton.Enabled = running && cancellable;
        progress.Visible = running;
        buildButton.Text = running ? "Procesando…" : "Crear release firmado";
        UseWaitCursor = running;

        operationStatus.Text = running ? status : "Listo";
        MineralTheme.Role(operationStatus, running ? MineralRole.InfoBadge : MineralRole.SuccessBadge);
    }

    private void Append(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Append(message));
            return;
        }
        activity.AppendText(
            $"[{DateTimeOffset.Now:HH:mm:ss}] {message}" +
            Environment.NewLine);
        activity.SelectionStart = activity.TextLength;
        activity.ScrollToCaret();
    }

    private void ShowError(string message)
    {
        Append("ERROR: " + message);
        MessageBox.Show(
            this,
            message,
            "ATLAS Publisher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void PublisherForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && operationRunning)
        {
            operationCancellation?.Cancel();
            e.Handled = true;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        int width = Math.Min(Width, Math.Max(740, workingArea.Width - 32));
        int height = Math.Min(Height, Math.Max(640, workingArea.Height - 48));
        Size = new Size(width, height);
        Location = new Point(
            workingArea.Left + Math.Max(0, (workingArea.Width - width) / 2),
            workingArea.Top + Math.Max(0, (workingArea.Height - height) / 2));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (operationRunning)
        {
            e.Cancel = true;
            DialogResult result = MessageBox.Show(
                this,
                "Hay una operación en curso. ¿Desea cancelarla?",
                "ATLAS Publisher",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result == DialogResult.Yes)
            {
                operationCancellation?.Cancel();
            }
            return;
        }
        operationCancellation?.Dispose();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            operationCancellation?.Dispose();
            hints.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record ReleaseProfileItem(
        ReleaseBuildProfile Profile,
        string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}

internal sealed class AtlasCard : Panel, IMineralControl
{

    public AtlasCard(Color borderColor)
    {
        MineralTheme.Role(this, MineralRole.Surface);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
    }

    public void ApplyPalette(MineralPalette palette) { BackColor = palette.Surface; ForeColor = palette.Ink; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using Pen pen = new(MineralTheme.Palette.Line);
        Rectangle rectangle = ClientRectangle;
        rectangle.Width -= 1;
        rectangle.Height -= 1;
        e.Graphics.DrawRectangle(pen, rectangle);
    }
}

internal sealed class AtlasLogo : Control
{
    public AtlasLogo()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        PointF[] diamond =
        [
            new(Width / 2F, 4F),
            new(Width - 5F, Height / 2F),
            new(Width / 2F, Height - 5F),
            new(5F, Height / 2F)
        ];
        using Pen outer = new(MineralTheme.Palette.BrandAccent, 3F);
        using Pen inner = new(MineralTheme.Palette.BrandMuted, 2F);
        e.Graphics.DrawPolygon(outer, diamond);
        e.Graphics.DrawLine(
            inner,
            Width * 0.36F,
            Height * 0.5F,
            Width * 0.64F,
            Height * 0.5F);
    }
}
