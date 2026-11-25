using ScanBridge.Hosting;
using ScanBridge.Localization;
using ScanBridge.Scanning;
using ScanBridge.Settings;
using Serilog;

namespace ScanBridge.App;

/// <summary>
/// The configuration window: scanner selection, scan defaults, the local server
/// (port + allowed browser origins), the run-at-login toggle and the UI language.
/// Built in code rather than with the WinForms designer to keep it reviewable.
/// Every text comes from <see cref="Strings"/> when the window opens, so a language
/// change applies the next time the window opens.
/// </summary>
public sealed class SettingsForm : Form
{
    private sealed record DeviceItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record LanguageItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly string[] DriverLabels = ["WIA", "TWAIN", "ESCL"];
    private static readonly string[] DriverValues = ["wia", "twain", "escl"];
    private static readonly string[] SourceValues = ["flatbed", "feeder", "duplex"];
    private static readonly string[] ColorValues = ["color", "grayscale", "blackAndWhite"];
    // Paper size names are the same in every language.
    private static readonly string[] PageSizeLabels = ["Letter", "Legal", "A4"];
    private static readonly string[] PageSizeValues = ["letter", "legal", "a4"];
    private static readonly int[] DpiChoices = [100, 150, 200, 300, 400, 600];

    private readonly SettingsStore _settingsStore;
    private readonly ScannerService _scannerService;
    private readonly WebHostRunner _webHostRunner;
    private readonly SynchronizationContext _syncContext;

    private readonly ComboBox _driverCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _deviceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Button _refreshButton = new() { Text = Strings.SettingsRefresh, AutoSize = true };
    private readonly Label _scannerStatusLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Text = string.Empty };

    private readonly ComboBox _sourceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _dpiCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly ComboBox _colorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _pageSizeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly CheckBox _blankPagesCheck = new() { Text = Strings.SettingsRemoveBlankPages, AutoSize = true };
    private readonly CheckBox _deskewCheck = new() { Text = Strings.SettingsDeskew, AutoSize = true };
    private readonly NumericUpDown _jpegQualityInput = new() { Minimum = 1, Maximum = 100, Width = 80 };

    private readonly NumericUpDown _portInput = new() { Minimum = 1024, Maximum = 65535, Width = 100 };
    private readonly ListBox _originsList = new() { Width = 420, Height = 90 };
    private readonly TextBox _originInput = new() { Width = 280, PlaceholderText = "https://apps.example.gov" };
    private readonly Button _addOriginButton = new() { Text = Strings.SettingsAddOrigin, AutoSize = true };
    private readonly Button _removeOriginButton = new() { Text = Strings.SettingsRemoveOrigin, AutoSize = true };
    private readonly Label _listenerStatusLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Text = string.Empty };

    private readonly CheckBox _runAtLoginCheck = new() { Text = Strings.SettingsRunAtLogin, AutoSize = true };
    private readonly ComboBox _languageCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };

    public SettingsForm(SettingsStore settingsStore, ScannerService scannerService, WebHostRunner webHostRunner)
    {
        _settingsStore = settingsStore;
        _scannerService = scannerService;
        _webHostRunner = webHostRunner;
        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Text = Strings.SettingsTitle(AppInfo.Version);
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
        var scannerGroup = new GroupBox { Text = Strings.SettingsScannerGroup, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
        var scannerLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        scannerLayout.Controls.Add(new Label { Text = Strings.SettingsDriverLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        scannerLayout.Controls.Add(_driverCombo, 1, 0);
        scannerLayout.Controls.Add(_refreshButton, 2, 0);
        scannerLayout.Controls.Add(new Label { Text = Strings.SettingsScannerLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        scannerLayout.Controls.Add(_deviceCombo, 1, 1);
        scannerLayout.SetColumnSpan(_deviceCombo, 2);
        scannerLayout.Controls.Add(_scannerStatusLabel, 1, 2);
        scannerLayout.SetColumnSpan(_scannerStatusLabel, 2);
        scannerGroup.Controls.Add(scannerLayout);

        var defaultsGroup = new GroupBox { Text = Strings.SettingsDefaultsGroup, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
        var defaultsLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Fill };
        defaultsLayout.Controls.Add(new Label { Text = Strings.SettingsSourceLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        defaultsLayout.Controls.Add(_sourceCombo, 1, 0);
        defaultsLayout.Controls.Add(new Label { Text = Strings.SettingsResolutionLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        defaultsLayout.Controls.Add(_dpiCombo, 3, 0);
        defaultsLayout.Controls.Add(new Label { Text = Strings.SettingsColorLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        defaultsLayout.Controls.Add(_colorCombo, 1, 1);
        defaultsLayout.Controls.Add(new Label { Text = Strings.SettingsPageSizeLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
        defaultsLayout.Controls.Add(_pageSizeCombo, 3, 1);
        defaultsLayout.Controls.Add(_blankPagesCheck, 1, 2);
        defaultsLayout.Controls.Add(_deskewCheck, 1, 3);
        defaultsLayout.Controls.Add(new Label { Text = Strings.SettingsJpegQualityLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        defaultsLayout.Controls.Add(_jpegQualityInput, 1, 4);
        var jpegHelp = new Label
        {
            Text = Strings.SettingsJpegQualityHelp,
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            ForeColor = SystemColors.GrayText,
        };
        defaultsLayout.Controls.Add(jpegHelp, 1, 5);
        defaultsLayout.SetColumnSpan(jpegHelp, 3);
        defaultsLayout.SetColumnSpan(_blankPagesCheck, 3);
        defaultsLayout.SetColumnSpan(_deskewCheck, 3);
        defaultsGroup.Controls.Add(defaultsLayout);

        var serverGroup = new GroupBox { Text = Strings.SettingsServerGroup, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
        var serverLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        serverLayout.Controls.Add(new Label { Text = Strings.SettingsPortLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        serverLayout.Controls.Add(_portInput, 1, 0);
        serverLayout.Controls.Add(_listenerStatusLabel, 2, 0);
        var originsLabel = new Label
        {
            Text = Strings.SettingsOriginsLabel,
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
        startupPanel.SetFlowBreak(_runAtLoginCheck, true);
        startupPanel.Controls.Add(new Label { Text = Strings.SettingsLanguageLabel, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 6, 0, 0) });
        startupPanel.Controls.Add(_languageCombo);
        startupPanel.SetFlowBreak(_languageCombo, true);
        var openLogsButton = new Button { Text = Strings.OpenLogFolder, AutoSize = true };
        openLogsButton.Click += (_, _) => LogFolder.Open();
        startupPanel.Controls.Add(openLogsButton);
        var copyDiagnosticsButton = new Button { Text = Strings.SettingsCopyDiagnostics, AutoSize = true };
        copyDiagnosticsButton.Click += (_, _) => CopyDiagnostics();
        startupPanel.Controls.Add(copyDiagnosticsButton);

        var saveButton = new Button { Text = Strings.SettingsSave, AutoSize = true, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = Strings.SettingsCancel, AutoSize = true, DialogResult = DialogResult.Cancel };
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

        _sourceCombo.Items.AddRange([Strings.SourceFlatbed, Strings.SourceFeeder, Strings.SourceDuplex]);
        _sourceCombo.SelectedIndex = Math.Max(0, Array.IndexOf(SourceValues, settings.Defaults.PaperSource));
        _dpiCombo.Items.AddRange([.. DpiChoices.Select(d => (object)d)]);
        _dpiCombo.SelectedItem = DpiChoices.Contains(settings.Defaults.Dpi) ? settings.Defaults.Dpi : 300;
        _colorCombo.Items.AddRange([Strings.ColorColor, Strings.ColorGrayscale, Strings.ColorBlackAndWhite]);
        _colorCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ColorValues, settings.Defaults.ColorMode));
        _pageSizeCombo.Items.AddRange([.. PageSizeLabels]);
        _pageSizeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(PageSizeValues, settings.Defaults.PageSize));
        _blankPagesCheck.Checked = settings.Defaults.ExcludeBlankPages;
        _deskewCheck.Checked = settings.Defaults.AutoDeskew;
        _jpegQualityInput.Value = Math.Clamp(settings.Defaults.JpegQuality, 1, 100);

        _portInput.Value = Math.Clamp(settings.Port, (int)_portInput.Minimum, (int)_portInput.Maximum);
        foreach (var origin in settings.AllowedOrigins)
        {
            _originsList.Items.Add(origin);
        }

        _runAtLoginCheck.Checked = settings.RunAtLogin;

        _languageCombo.Items.Add(new LanguageItem(UiLanguage.Automatic, Strings.SettingsLanguageAutomatic));
        foreach (var language in UiLanguage.Supported)
        {
            _languageCombo.Items.Add(new LanguageItem(language.Name, language.NativeName));
        }

        var configured = UiLanguage.FindSupported(settings.Language ?? string.Empty)?.Name ?? UiLanguage.Automatic;
        _languageCombo.SelectedItem = _languageCombo.Items.Cast<LanguageItem>().First(l => l.Value == configured);
    }

    private async Task RefreshDevicesAsync(bool selectConfigured)
    {
        var driver = DriverValues[Math.Max(0, _driverCombo.SelectedIndex)];
        _refreshButton.Enabled = false;
        _scannerStatusLabel.Text = Strings.SettingsLookingForScanners;
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
                ? Strings.SettingsNoScannersFound
                : Strings.SettingsScannersFound(scanners.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Scanner enumeration failed");
            _scannerStatusLabel.Text = Strings.SettingsScannerListFailed(ex.Message);
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
                Strings.SettingsInvalidOriginMessage,
                Strings.SettingsInvalidOriginTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        settings.Defaults.JpegQuality = (int)_jpegQualityInput.Value;

        settings.Port = (int)_portInput.Value;
        settings.AllowedOrigins = _originsList.Items.Cast<string>().ToList();
        settings.RunAtLogin = _runAtLoginCheck.Checked;
        settings.Language = (_languageCombo.SelectedItem as LanguageItem)?.Value ?? UiLanguage.Automatic;

        _settingsStore.Save(settings);
        Close();
    }

    /// <summary>
    /// Copies a short, non-sensitive summary of the setup to the clipboard for support. It
    /// stays in English, like the log files, whatever the UI language.
    /// </summary>
    private void CopyDiagnostics()
    {
        var settings = _settingsStore.Current;
        var text = string.Join(Environment.NewLine,
            $"ScanBridge v{AppInfo.Version}",
            $"Windows: {Environment.OSVersion.VersionString}",
            $".NET: {Environment.Version}",
            $"Server: {(_webHostRunner.IsRunning ? "listening" : "not listening")} on port {settings.Port}",
            $"Last error: {_webHostRunner.LastError?.ToLogString() ?? "none"}",
            $"Driver: {settings.ScannerDriver ?? "wia"}",
            $"Scanner: {settings.ScannerDeviceName ?? "not configured"} ({settings.ScannerDeviceId ?? "-"})",
            $"Defaults: {settings.Defaults.Dpi} dpi, {settings.Defaults.PageSize}, JPG quality {settings.Defaults.JpegQuality}",
            $"Language: {settings.Language} (UI: {System.Globalization.CultureInfo.CurrentUICulture.Name}, Windows: {System.Globalization.CultureInfo.InstalledUICulture.Name})",
            $"Allowed origins: {(settings.AllowedOrigins.Count == 0 ? "none" : string.Join(", ", settings.AllowedOrigins))}",
            $"Log folder: {LogFolder.Path}");

        try
        {
            Clipboard.SetText(text);
            MessageBox.Show(this, Strings.SettingsDiagnosticsCopied, "ScanBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, Strings.SettingsClipboardUnavailable, "ScanBridge",
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
            _listenerStatusLabel.Text = Strings.SettingsListening(_settingsStore.Current.Port);
            _listenerStatusLabel.ForeColor = Color.DarkGreen;
        }
        else
        {
            _listenerStatusLabel.Text = _webHostRunner.LastError?.ToString() ?? Strings.NotListening;
            _listenerStatusLabel.ForeColor = Color.Firebrick;
        }
    }
}
