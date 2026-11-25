using ScanBridge.Hosting;
using ScanBridge.Scanning;
using ScanBridge.Settings;
using Serilog;

namespace ScanBridge.App;

/// <summary>
/// The configuration window: scanner selection, scan defaults, the local server
/// (port + allowed browser origins) and the run-at-login toggle.
/// Built in code rather than with the WinForms designer to keep it reviewable.
/// </summary>
public sealed class SettingsForm : Form
{
    private sealed record DeviceItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly string[] DriverLabels = ["WIA", "TWAIN", "ESCL"];
    private static readonly string[] DriverValues = ["wia", "twain", "escl"];
    private static readonly string[] SourceLabels = ["Piano di scansione", "Caricatore documenti", "Caricatore documenti (fronte/retro)"];
    private static readonly string[] SourceValues = ["flatbed", "feeder", "duplex"];
    private static readonly string[] ColorLabels = ["Colore", "Scala di grigi", "Bianco e nero"];
    private static readonly string[] ColorValues = ["color", "grayscale", "blackAndWhite"];
    private static readonly string[] PageSizeLabels = ["A4", "Letter", "Legal"];
    private static readonly string[] PageSizeValues = ["a4", "letter", "legal"];
    private static readonly string[] FormatLabels = ["PDF (tutte le pagine)", "JPG (solo la prima immagine)"];
    private static readonly string[] FormatValues = ["pdf", "jpg"];
    private static readonly int[] DpiChoices = [100, 150, 200, 300, 400, 600];

    private readonly SettingsStore _settingsStore;
    private readonly ScannerService _scannerService;
    private readonly WebHostRunner _webHostRunner;
    private readonly SynchronizationContext _syncContext;

    private readonly ComboBox _driverCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _deviceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Button _refreshButton = new() { Text = "Aggiorna", AutoSize = true };
    private readonly Label _scannerStatusLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Text = string.Empty };

    private readonly ComboBox _sourceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _dpiCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly ComboBox _colorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _pageSizeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly CheckBox _blankPagesCheck = new() { Text = "Rimuovi le pagine bianche", AutoSize = true };
    private readonly CheckBox _deskewCheck = new() { Text = "Raddrizza le pagine (deskew)", AutoSize = true };
    private readonly NumericUpDown _jpegQualityInput = new() { Minimum = 1, Maximum = 100, Width = 80 };
    private readonly ComboBox _formatCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };

    private readonly NumericUpDown _portInput = new() { Minimum = 1024, Maximum = 65535, Width = 100 };
    private readonly ListBox _originsList = new() { Width = 420, Height = 90 };
    private readonly TextBox _originInput = new() { Width = 280, PlaceholderText = "https://apps.example.gov" };
    private readonly Button _addOriginButton = new() { Text = "Aggiungi", AutoSize = true };
    private readonly Button _removeOriginButton = new() { Text = "Rimuovi selezionata", AutoSize = true };
    private readonly Label _listenerStatusLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Text = string.Empty };

    private readonly CheckBox _runAtLoginCheck = new() { Text = "Avvia ScanBridge all'accesso a Windows", AutoSize = true };

    public SettingsForm(SettingsStore settingsStore, ScannerService scannerService, WebHostRunner webHostRunner)
    {
        _settingsStore = settingsStore;
        _scannerService = scannerService;
        _webHostRunner = webHostRunner;
        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Text = $"ScanBridge v{AppInfo.Version} - Impostazioni";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(12);

        BuildLayout();
        LoadFromSettings(_settingsStore.Current);

        _refreshButton.Click += async (_, _) => await RefreshDevicesAsync(selectConfigured: false);
        _driverCombo.SelectedIndexChanged += async (_, _) => await RefreshDevicesAsync(selectConfigured: false);
        _addOriginButton.Click += (_, _) => AddOrigin();
        _originInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                AddOrigin();
            }
        };
        _removeOriginButton.Click += (_, _) =>
        {
            if (_originsList.SelectedItem is not null)
            {
                _originsList.Items.Remove(_originsList.SelectedItem);
            }
        };

        _webHostRunner.StatusChanged += OnListenerStatusChanged;
        FormClosed += (_, _) => _webHostRunner.StatusChanged -= OnListenerStatusChanged;
        UpdateListenerStatusLabel();

        Shown += async (_, _) => await RefreshDevicesAsync(selectConfigured: true);
    }

    private void BuildLayout()
    {
        var scannerGroup = new GroupBox { Text = "Scanner", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
        var scannerLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        scannerLayout.Controls.Add(new Label { Text = "Driver:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        scannerLayout.Controls.Add(_driverCombo, 1, 0);
        scannerLayout.Controls.Add(_refreshButton, 2, 0);
        scannerLayout.Controls.Add(new Label { Text = "Scanner:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        scannerLayout.Controls.Add(_deviceCombo, 1, 1);
        scannerLayout.SetColumnSpan(_deviceCombo, 2);
        scannerLayout.Controls.Add(_scannerStatusLabel, 1, 2);
        scannerLayout.SetColumnSpan(_scannerStatusLabel, 2);
        scannerGroup.Controls.Add(scannerLayout);

        var defaultsGroup = new GroupBox { Text = "Impostazioni di scansione predefinite", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
        var defaultsLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Fill };
        defaultsLayout.Controls.Add(new Label { Text = "Origine:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        defaultsLayout.Controls.Add(_sourceCombo, 1, 0);
        defaultsLayout.Controls.Add(new Label { Text = "Risoluzione:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        defaultsLayout.Controls.Add(_dpiCombo, 3, 0);
        defaultsLayout.Controls.Add(new Label { Text = "Colore:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        defaultsLayout.Controls.Add(_colorCombo, 1, 1);
        defaultsLayout.Controls.Add(new Label { Text = "Formato pagina:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
        defaultsLayout.Controls.Add(_pageSizeCombo, 3, 1);
        defaultsLayout.Controls.Add(_blankPagesCheck, 1, 2);
        defaultsLayout.Controls.Add(_deskewCheck, 1, 3);
        defaultsLayout.Controls.Add(new Label { Text = "Formato di output:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        defaultsLayout.Controls.Add(_formatCombo, 1, 4);
        defaultsLayout.SetColumnSpan(_formatCombo, 3);
        var formatHelp = new Label
        {
            Text = "Usato quando il sito non indica un formato. Il JPG contiene solo la prima immagine " +
                   "scansionata (per foto e documenti su un solo lato); il PDF contiene tutte le pagine. " +
                   "Il sito può sostituirlo con \"format\": \"pdf\" oppure \"jpg\".",
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            ForeColor = SystemColors.GrayText,
        };
        defaultsLayout.Controls.Add(formatHelp, 1, 5);
        defaultsLayout.SetColumnSpan(formatHelp, 3);
        defaultsLayout.Controls.Add(new Label { Text = "Qualità JPG (1-100):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        defaultsLayout.Controls.Add(_jpegQualityInput, 1, 6);
        defaultsLayout.SetColumnSpan(_blankPagesCheck, 3);
        defaultsLayout.SetColumnSpan(_deskewCheck, 3);
        defaultsGroup.Controls.Add(defaultsLayout);

        var serverGroup = new GroupBox { Text = "Server locale", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
        var serverLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        serverLayout.Controls.Add(new Label { Text = "Porta:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        serverLayout.Controls.Add(_portInput, 1, 0);
        serverLayout.Controls.Add(_listenerStatusLabel, 2, 0);
        var originsLabel = new Label
        {
            Text = "Siti autorizzati (solo questi siti possono usare lo scanner dal browser):",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        serverLayout.Controls.Add(originsLabel, 0, 1);
        serverLayout.SetColumnSpan(originsLabel, 3);
        serverLayout.Controls.Add(_originsList, 0, 2);
        serverLayout.SetColumnSpan(_originsList, 3);
        var originAddPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        originAddPanel.Controls.Add(_originInput);
        originAddPanel.Controls.Add(_addOriginButton);
        originAddPanel.Controls.Add(_removeOriginButton);
        serverLayout.Controls.Add(originAddPanel, 0, 3);
        serverLayout.SetColumnSpan(originAddPanel, 3);
        serverGroup.Controls.Add(serverLayout);

        var startupPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4) };
        startupPanel.Controls.Add(_runAtLoginCheck);
        var openLogsButton = new Button { Text = "Apri cartella dei log", AutoSize = true };
        openLogsButton.Click += (_, _) => LogFolder.Open();
        startupPanel.Controls.Add(openLogsButton);
        var copyDiagnosticsButton = new Button { Text = "Copia diagnostica", AutoSize = true };
        copyDiagnosticsButton.Click += (_, _) => CopyDiagnostics();
        startupPanel.Controls.Add(copyDiagnosticsButton);

        var saveButton = new Button { Text = "Salva", AutoSize = true, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "Annulla", AutoSize = true, DialogResult = DialogResult.Cancel };
        saveButton.Click += (_, _) => SaveAndClose();
        cancelButton.Click += (_, _) => Close();
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(4),
        };
        buttonPanel.Controls.Add(saveButton);
        buttonPanel.Controls.Add(cancelButton);

        // Docked top-to-bottom; add in reverse so the scanner group ends up on top.
        Controls.Add(buttonPanel);
        Controls.Add(startupPanel);
        Controls.Add(serverGroup);
        Controls.Add(defaultsGroup);
        Controls.Add(scannerGroup);

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(650, 0);
    }

    private void LoadFromSettings(AppSettings settings)
    {
        _driverCombo.Items.Clear();
        _driverCombo.Items.AddRange([.. DriverLabels]);
        _driverCombo.SelectedIndex = Math.Max(0, Array.IndexOf(DriverValues, settings.ScannerDriver ?? "wia"));

        if (!string.IsNullOrEmpty(settings.ScannerDeviceId))
        {
            _deviceCombo.Items.Add(new DeviceItem(settings.ScannerDeviceId, settings.ScannerDeviceName ?? settings.ScannerDeviceId));
            _deviceCombo.SelectedIndex = 0;
        }

        _sourceCombo.Items.AddRange([.. SourceLabels]);
        _sourceCombo.SelectedIndex = Math.Max(0, Array.IndexOf(SourceValues, settings.Defaults.PaperSource));
        _dpiCombo.Items.AddRange([.. DpiChoices.Select(d => (object)d)]);
        _dpiCombo.SelectedItem = DpiChoices.Contains(settings.Defaults.Dpi) ? settings.Defaults.Dpi : 300;
        _colorCombo.Items.AddRange([.. ColorLabels]);
        _colorCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ColorValues, settings.Defaults.ColorMode));
        _pageSizeCombo.Items.AddRange([.. PageSizeLabels]);
        _pageSizeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(PageSizeValues, settings.Defaults.PageSize));
        _blankPagesCheck.Checked = settings.Defaults.ExcludeBlankPages;
        _deskewCheck.Checked = settings.Defaults.AutoDeskew;
        _formatCombo.Items.AddRange([.. FormatLabels]);
        _formatCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FormatValues, settings.Defaults.Format));
        _jpegQualityInput.Value = Math.Clamp(settings.Defaults.JpegQuality, 1, 100);

        _portInput.Value = Math.Clamp(settings.Port, (int)_portInput.Minimum, (int)_portInput.Maximum);
        foreach (var origin in settings.AllowedOrigins)
        {
            _originsList.Items.Add(origin);
        }

        _runAtLoginCheck.Checked = settings.RunAtLogin;
    }

    private async Task RefreshDevicesAsync(bool selectConfigured)
    {
        var driver = DriverValues[Math.Max(0, _driverCombo.SelectedIndex)];
        _refreshButton.Enabled = false;
        _scannerStatusLabel.Text = "Ricerca degli scanner in corso…";
        try
        {
            var scanners = await _scannerService.ListScannersAsync(driver, refresh: true, CancellationToken.None);
            var previousSelection = selectConfigured
                ? _settingsStore.Current.ScannerDeviceId
                : (_deviceCombo.SelectedItem as DeviceItem)?.Id;

            _deviceCombo.Items.Clear();
            foreach (var scanner in scanners)
            {
                _deviceCombo.Items.Add(new DeviceItem(scanner.Id, scanner.Name));
            }

            var toSelect = _deviceCombo.Items.Cast<DeviceItem>().FirstOrDefault(d => d.Id == previousSelection);
            if (toSelect is not null)
            {
                _deviceCombo.SelectedItem = toSelect;
            }
            else if (_deviceCombo.Items.Count > 0)
            {
                _deviceCombo.SelectedIndex = 0;
            }

            _scannerStatusLabel.Text = scanners.Count == 0
                ? "Nessuno scanner trovato con questo driver."
                : $"{scanners.Count} scanner(s) found.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Scanner enumeration failed");
            _scannerStatusLabel.Text = $"Impossibile elencare gli scanner: {ex.Message}";
        }
        finally
        {
            _refreshButton.Enabled = true;
        }
    }

    private void AddOrigin()
    {
        if (!AppSettings.TryNormalizeOrigin(_originInput.Text, out var normalized))
        {
            MessageBox.Show(this,
                "Inserisci un'origine completa, ad esempio https://apps.example.gov: solo protocollo e dominio, senza percorso.",
                "Origine non valida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_originsList.Items.Contains(normalized))
        {
            _originsList.Items.Add(normalized);
        }

        _originInput.Clear();
    }

    private void SaveAndClose()
    {
        var settings = _settingsStore.Current.Clone();
        settings.ScannerDriver = DriverValues[Math.Max(0, _driverCombo.SelectedIndex)];
        if (_deviceCombo.SelectedItem is DeviceItem device)
        {
            settings.ScannerDeviceId = device.Id;
            settings.ScannerDeviceName = device.Name;
        }

        settings.Defaults.PaperSource = SourceValues[Math.Max(0, _sourceCombo.SelectedIndex)];
        settings.Defaults.Dpi = _dpiCombo.SelectedItem is int dpi ? dpi : 300;
        settings.Defaults.ColorMode = ColorValues[Math.Max(0, _colorCombo.SelectedIndex)];
        settings.Defaults.PageSize = PageSizeValues[Math.Max(0, _pageSizeCombo.SelectedIndex)];
        settings.Defaults.ExcludeBlankPages = _blankPagesCheck.Checked;
        settings.Defaults.AutoDeskew = _deskewCheck.Checked;
        settings.Defaults.Format = FormatValues[Math.Max(0, _formatCombo.SelectedIndex)];
        settings.Defaults.JpegQuality = (int)_jpegQualityInput.Value;

        settings.Port = (int)_portInput.Value;
        settings.AllowedOrigins = _originsList.Items.Cast<string>().ToList();
        settings.RunAtLogin = _runAtLoginCheck.Checked;

        _settingsStore.Save(settings);
        Close();
    }

    /// <summary>Copies a short, non-sensitive summary of the setup to the clipboard for support.</summary>
    private void CopyDiagnostics()
    {
        var settings = _settingsStore.Current;
        var text = string.Join(Environment.NewLine,
            $"ScanBridge v{AppInfo.Version}",
            $"Windows: {Environment.OSVersion.VersionString}",
            $".NET: {Environment.Version}",
            $"Server: {(_webHostRunner.IsRunning ? "in ascolto" : "non in ascolto")} sulla porta {settings.Port}",
            $"Ultimo errore: {_webHostRunner.LastError ?? "nessuno"}",
            $"Driver: {settings.ScannerDriver ?? "wia"}",
            $"Scanner: {settings.ScannerDeviceName ?? "non configurato"} ({settings.ScannerDeviceId ?? "-"})",
            $"Formato: {settings.Defaults.Format}, qualità JPG {settings.Defaults.JpegQuality}, {settings.Defaults.Dpi} dpi, {settings.Defaults.PageSize}",
            $"Siti autorizzati: {(settings.AllowedOrigins.Count == 0 ? "nessuno" : string.Join(", ", settings.AllowedOrigins))}",
            $"Cartella dei log: {LogFolder.Path}");

        try
        {
            Clipboard.SetText(text);
            MessageBox.Show(this, "Diagnostica copiata negli appunti.", "ScanBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "Impossibile accedere agli appunti. Riprova.", "ScanBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnListenerStatusChanged()
    {
        _syncContext.Post(_ => UpdateListenerStatusLabel(), null);
    }

    private void UpdateListenerStatusLabel()
    {
        if (_webHostRunner.IsRunning)
        {
            _listenerStatusLabel.Text = $"In ascolto su http://127.0.0.1:{_settingsStore.Current.Port}";
            _listenerStatusLabel.ForeColor = Color.DarkGreen;
        }
        else
        {
            _listenerStatusLabel.Text = _webHostRunner.LastError ?? "Non in ascolto";
            _listenerStatusLabel.ForeColor = Color.Firebrick;
        }
    }
}
