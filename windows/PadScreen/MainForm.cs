using System.Diagnostics;
using System.Drawing;
using PadScreen.Core;

namespace PadScreen;

internal sealed class MainForm : Form
{
    private readonly Settings _settings = Settings.Load();
    private readonly AppController _controller;
    private readonly System.Windows.Forms.Timer _autoRefresh = new() { Interval = 10_000 };
    private bool _discovering;
    private bool _closingAfterStop;

    // 1. iPad
    private readonly ListView _lvPads = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Dock = DockStyle.Fill,
        Height = 110,
    };
    private readonly Button _btnRefresh = new() { Text = "Cerca di nuovo", AutoSize = true };
    private readonly TextBox _txtManual = new() { Width = 170, PlaceholderText = "es. 192.168.1.50" };
    private readonly CheckBox _chkAuto = new() { Text = "Collega automaticamente all'ultimo iPad", AutoSize = true };

    // 2. Schermo
    private readonly RadioButton _rbVirtual = new() { Text = "Schermo virtuale esteso (consigliato)", AutoSize = true };
    private readonly Label _lblDriver = new() { AutoSize = true };
    private readonly LinkLabel _lnkDriver = new() { Text = "Scarica il driver Parsec VDD", AutoSize = true };
    private readonly RadioButton _rbMirror = new() { Text = "Duplica un monitor esistente:", AutoSize = true };
    private readonly ComboBox _cmbMonitors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly ComboBox _cmbRes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };

    // 3. Qualità e input
    private readonly ComboBox _cmbFps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly NumericUpDown _numBitrate = new() { Minimum = 2, Maximum = 150, Width = 70 };
    private readonly ComboBox _cmbEncoder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ComboBox _cmbInput = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };

    // FFmpeg
    private readonly Label _lblFfmpeg = new() { AutoSize = true };
    private readonly Button _btnInstallFfmpeg = new() { Text = "Installa FFmpeg (winget)", AutoSize = true };
    private readonly Button _btnBrowseFfmpeg = new() { Text = "Sfoglia…", AutoSize = true };

    // Azioni e stato
    private readonly Button _btnConnect = new() { Text = "Connetti", AutoSize = true, Padding = new Padding(18, 4, 18, 4) };
    private readonly Button _btnDisconnect = new() { Text = "Disconnetti", AutoSize = true, Padding = new Padding(8, 4, 8, 4), Enabled = false };
    private readonly Button _btnLog = new() { Text = "Apri registro", AutoSize = true };
    private readonly Label _lblStatus = new() { AutoSize = true, Text = "Pronto." };
    private readonly Label _lblStats = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly TextBox _txtLog = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f),
        BackColor = SystemColors.Window,
    };

    private GroupBox _grpScreen = null!;
    private GroupBox _grpQuality = null!;

    public MainForm()
    {
        Text = "PadScreen — iPad come secondo schermo";
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 800);
        MinimumSize = new Size(620, 640);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        _controller = new AppController(_settings);
        _controller.LogLine += line => Ui(() => AppendLog(line));
        _controller.StatusChanged += s => Ui(() => _lblStatus.Text = s);
        _controller.StatsChanged += s => Ui(() => _lblStats.Text = s == null ? "" : FormatStats(s.Value));
        _controller.RunningChanged += running => Ui(() => SetRunning(running));

        BuildLayout();
        LoadSettingsIntoControls();
        WireEvents();
    }

    // ---------- Layout ----------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
            AutoSize = false,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var title = new Label { Text = "PadScreen", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
        var subtitle = new Label
        {
            Text = "Usa l'iPad come secondo schermo, collegato con il cavo USB o in Wi-Fi. Sull'iPad apri l'app PadScreen.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 8),
        };

        // 1. iPad
        _lvPads.Columns.Add("iPad", 260);
        _lvPads.Columns.Add("Collegamento", 120);
        _lvPads.Columns.Add("Indirizzo", 170);
        var padRow = Flow(_btnRefresh, new Label { Text = "Indirizzo manuale:", AutoSize = true, Margin = new Padding(14, 7, 3, 0) }, _txtManual);
        var grpPad = Group("1 · iPad", Stack(_lvPads, padRow, _chkAuto));
        _lvPads.Dock = DockStyle.Top;

        // 2. Schermo
        _cmbRes.Items.AddRange(new object[]
        {
            "Nativa dell'iPad — nitida (poi imposta la scala di Windows al 200%)",
            "Metà — più leggera, meno nitida (scala 100%)",
        });
        var driverRow = Flow(_lblDriver, _lnkDriver);
        driverRow.Margin = new Padding(22, 0, 3, 4);
        var resRow = Flow(new Label { Text = "Risoluzione:", AutoSize = true, Margin = new Padding(22, 7, 3, 0) }, _cmbRes);
        _grpScreen = Group("2 · Schermo", Stack(_rbVirtual, driverRow, resRow, Flow(_rbMirror, _cmbMonitors)));

        // 3. Qualità
        _cmbFps.Items.AddRange(new object[] { "30", "60" });
        foreach (var (_, label) in FfmpegSetup.Preferences) _cmbEncoder.Items.Add(label);
        _cmbInput.Items.AddRange(new object[]
        {
            "Touch + Apple Pencil (come uno schermo touch)",
            "Mouse (un dito muove il puntatore)",
        });
        var q1 = Flow(
            Lbl("FPS:"), _cmbFps,
            Lbl("Bitrate (Mbit/s):", 16), _numBitrate,
            Lbl("Encoder:", 16), _cmbEncoder);
        var q2 = Flow(Lbl("Input dall'iPad:"), _cmbInput);
        var q3 = Flow(_lblFfmpeg, _btnInstallFfmpeg, _btnBrowseFfmpeg);
        _lblFfmpeg.Margin = new Padding(3, 8, 8, 0);
        _grpQuality = Group("3 · Qualità e input", Stack(q1, q2, q3));

        // Azioni
        _btnConnect.Font = new Font(Font, FontStyle.Bold);
        var actions = Flow(_btnConnect, _btnDisconnect, _btnLog);
        _lblStatus.Font = new Font(Font, FontStyle.Bold);
        _lblStatus.Margin = new Padding(3, 6, 3, 0);

        foreach (var c in new Control[] { title, subtitle, grpPad, _grpScreen, _grpQuality, actions, _lblStatus, _lblStats })
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c);
        }
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _txtLog.Margin = new Padding(3, 8, 3, 3);
        root.Controls.Add(_txtLog);
        Controls.Add(root);
        AcceptButton = _btnConnect;
    }

    private static Label Lbl(string text, int leftMargin = 3) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(leftMargin, 7, 3, 0) };

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
        f.Controls.AddRange(controls);
        return f;
    }

    private static TableLayoutPanel Stack(params Control[] controls)
    {
        var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var c in controls)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c);
        }
        return t;
    }

    private static GroupBox Group(string title, Control content)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 6, 8, 6), Margin = new Padding(3, 4, 3, 4) };
        content.Dock = DockStyle.Fill;
        g.Controls.Add(content);
        return g;
    }

    // ---------- Impostazioni ----------

    private void LoadSettingsIntoControls()
    {
        _rbVirtual.Checked = _settings.VirtualDisplay;
        _rbMirror.Checked = !_settings.VirtualDisplay;
        _cmbRes.SelectedIndex = _settings.HalfResolution ? 1 : 0;
        _cmbFps.SelectedItem = _settings.Fps >= 60 ? "60" : "30";
        _numBitrate.Value = Math.Clamp(_settings.BitrateMbps, 2, 150);
        int encIdx = Array.FindIndex(FfmpegSetup.Preferences, p => p.Key == _settings.Encoder);
        _cmbEncoder.SelectedIndex = encIdx < 0 ? 0 : encIdx;
        _cmbInput.SelectedIndex = _settings.MouseMode ? 1 : 0;
        _txtManual.Text = _settings.ManualAddress ?? "";
        _chkAuto.Checked = _settings.AutoConnect;
    }

    private void SaveControlsIntoSettings()
    {
        _settings.VirtualDisplay = _rbVirtual.Checked;
        _settings.MirrorDevice = (_cmbMonitors.SelectedItem as MonitorInfo)?.DeviceName;
        _settings.HalfResolution = _cmbRes.SelectedIndex == 1;
        _settings.Fps = _cmbFps.SelectedItem as string == "30" ? 30 : 60;
        _settings.BitrateMbps = (int)_numBitrate.Value;
        _settings.Encoder = FfmpegSetup.Preferences[Math.Max(0, _cmbEncoder.SelectedIndex)].Key;
        _settings.MouseMode = _cmbInput.SelectedIndex == 1;
        _settings.ManualAddress = _txtManual.Text.Trim();
        _settings.AutoConnect = _chkAuto.Checked;
        _settings.Save();
    }

    // ---------- Eventi ----------

    private void WireEvents()
    {
        Load += async (_, _) =>
        {
            AppendLog($"PadScreen {Application.ProductVersion} — registro completo: {AppController.LogFile}");
            RefreshMonitors();
            RefreshDriver();
            await RefreshFfmpegAsync();
            await DiscoverAsync();
            _autoRefresh.Start();
        };
        Activated += (_, _) => { if (!_controller.IsRunning) RefreshDriver(); };
        _autoRefresh.Tick += async (_, _) => { if (!_controller.IsRunning) await DiscoverAsync(); };

        _btnRefresh.Click += async (_, _) => await DiscoverAsync();
        _btnConnect.Click += (_, _) => Connect();
        _btnDisconnect.Click += async (_, _) =>
        {
            _btnDisconnect.Enabled = false;
            await _controller.StopAsync();
        };
        _btnLog.Click += (_, _) => OpenShell(AppController.LogFile);
        _lvPads.DoubleClick += (_, _) => Connect();
        _lvPads.SelectedIndexChanged += (_, _) => { if (_lvPads.SelectedItems.Count > 0) _txtManual.Text = ""; };
        _txtManual.TextChanged += (_, _) => { if (_txtManual.Text.Length > 0) _lvPads.SelectedItems.Clear(); };

        _rbVirtual.CheckedChanged += (_, _) => UpdateScreenControls();
        _cmbInput.SelectedIndexChanged += (_, _) => _settings.MouseMode = _cmbInput.SelectedIndex == 1;

        _lnkDriver.LinkClicked += (_, _) =>
        {
            MessageBox.Show(this,
                "Si aprirà il download del driver «Parsec Virtual Display» (gratuito, firmato).\n\n" +
                "1. Esegui il file scaricato e conferma l'installazione.\n" +
                "2. Torna su PadScreen: lo stato del driver si aggiorna da solo.\n\n" +
                "Se l'installazione desse problemi su una versione vecchia di Windows 10, usa la versione 0.41 (link nel README).",
                "Driver schermo virtuale", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenShell(ParsecVdd.DownloadUrl);
        };

        _btnInstallFfmpeg.Click += async (_, _) =>
        {
            _btnInstallFfmpeg.Enabled = false;
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c winget install -e --id Gyan.FFmpeg --accept-package-agreements --accept-source-agreements & echo. & pause")
                { UseShellExecute = true };
                using var p = Process.Start(psi);
                if (p != null) await p.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Impossibile avviare winget: " + ex.Message + "\n\nScarica FFmpeg da https://www.gyan.dev/ffmpeg/builds/ e indica ffmpeg.exe con «Sfoglia…».",
                    "FFmpeg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _btnInstallFfmpeg.Enabled = true;
            }
            await RefreshFfmpegAsync();
        };

        _btnBrowseFfmpeg.Click += async (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|Programmi (*.exe)|*.exe", Title = "Dov'è ffmpeg.exe?" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _settings.FfmpegPath = dlg.FileName;
            _settings.Save();
            await RefreshFfmpegAsync();
        };

        FormClosing += async (_, e) =>
        {
            SaveControlsIntoSettings();
            if (_closingAfterStop || !_controller.IsRunning) return;
            e.Cancel = true;
            Enabled = false;
            _lblStatus.Text = "Chiusura…";
            await _controller.StopAsync();
            _closingAfterStop = true;
            Close();
        };
        FormClosed += (_, _) =>
        {
            _autoRefresh.Dispose();
            _controller.Dispose();
        };
    }

    private void Connect()
    {
        if (_controller.IsRunning) return;
        SaveControlsIntoSettings();

        PadTarget? target = null;
        string manual = _txtManual.Text.Trim();
        if (manual.Length > 0)
        {
            string host = manual;
            int port = Proto.DefaultPort;
            int colon = manual.LastIndexOf(':');
            if (colon > 0 && int.TryParse(manual[(colon + 1)..], out int p))
            {
                host = manual[..colon];
                port = p;
            }
            target = new PadTarget(false, host, host, port, 0, null);
        }
        else if (_lvPads.SelectedItems.Count > 0)
        {
            target = _lvPads.SelectedItems[0].Tag as PadTarget;
        }
        else if (_lvPads.Items.Count == 1)
        {
            target = _lvPads.Items[0].Tag as PadTarget;
        }

        if (target == null)
        {
            MessageBox.Show(this,
                "Scegli un iPad dall'elenco oppure scrivi il suo indirizzo IP (lo trovi nell'app PadScreen sull'iPad).",
                "PadScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_settings.VirtualDisplay && !ParsecVdd.Describe().Ready)
        {
            var r = MessageBox.Show(this,
                "Per usare l'iPad come schermo esteso serve il driver Parsec VDD, che non risulta installato.\n\n" +
                "Vuoi invece duplicare il monitor principale?",
                "Driver mancante", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            _rbMirror.Checked = true;
            SaveControlsIntoSettings();
        }
        if (_controller.FfmpegPath == null)
        {
            MessageBox.Show(this, "FFmpeg non è installato: premi «Installa FFmpeg (winget)» oppure indica ffmpeg.exe con «Sfoglia…».",
                "PadScreen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.LastTarget = target.Key;
        _settings.Save();
        _lblStats.Text = "";
        _controller.Start(target);
    }

    private async Task DiscoverAsync()
    {
        if (_discovering) return;
        _discovering = true;
        _btnRefresh.Enabled = false;
        _btnRefresh.Text = "Ricerca…";
        try
        {
            var found = await _controller.DiscoverAsync();
            string? selectedKey = (_lvPads.SelectedItems.Count > 0 ? _lvPads.SelectedItems[0].Tag as PadTarget : null)?.Key
                                  ?? _settings.LastTarget;
            _lvPads.BeginUpdate();
            _lvPads.Items.Clear();
            foreach (var t in found)
            {
                var item = new ListViewItem(new[] { t.Name, t.Connection, t.Usb ? "cavo" : $"{t.Address}:{t.Port}" }) { Tag = t };
                _lvPads.Items.Add(item);
                if (t.Key == selectedKey && _txtManual.Text.Length == 0) item.Selected = true;
            }
            if (found.Count == 0)
                _lvPads.Items.Add(new ListViewItem(new[] { "Nessun iPad trovato: apri PadScreen sull'iPad", "", "" }) { ForeColor = SystemColors.GrayText });
            _lvPads.EndUpdate();

            if (_chkAuto.Checked && !_controller.IsRunning && _settings.LastTarget != null &&
                found.Any(f => f.Key == _settings.LastTarget) && _txtManual.Text.Length == 0)
            {
                Connect();
            }
        }
        finally
        {
            _btnRefresh.Text = "Cerca di nuovo";
            _btnRefresh.Enabled = true;
            _discovering = false;
        }
    }

    private void RefreshMonitors()
    {
        _cmbMonitors.Items.Clear();
        foreach (var m in Displays.GetAttached().Where(m => !m.AdapterName.Contains(ParsecVdd.AdapterName, StringComparison.OrdinalIgnoreCase)))
        {
            _cmbMonitors.Items.Add(m);
            if (m.DeviceName == _settings.MirrorDevice || (_settings.MirrorDevice == null && m.Primary))
                _cmbMonitors.SelectedItem = m;
        }
        if (_cmbMonitors.SelectedIndex < 0 && _cmbMonitors.Items.Count > 0) _cmbMonitors.SelectedIndex = 0;
        UpdateScreenControls();
    }

    private void RefreshDriver()
    {
        var (ready, text) = ParsecVdd.Describe();
        _lblDriver.Text = text;
        _lblDriver.ForeColor = ready ? Color.ForestGreen : Color.Firebrick;
        _lnkDriver.Visible = !ready;
    }

    private async Task RefreshFfmpegAsync()
    {
        _lblFfmpeg.Text = "FFmpeg: verifico…";
        bool ok = await _controller.RefreshFfmpegAsync();
        if (ok)
        {
            string v = _controller.FfmpegVersion;
            _lblFfmpeg.Text = "FFmpeg: " + (v.Length > 40 ? v[..40] + "…" : v);
            _lblFfmpeg.ForeColor = Color.ForestGreen;
            _btnInstallFfmpeg.Visible = false;
        }
        else
        {
            _lblFfmpeg.Text = "FFmpeg: non trovato";
            _lblFfmpeg.ForeColor = Color.Firebrick;
            _btnInstallFfmpeg.Visible = true;
        }
    }

    private void UpdateScreenControls()
    {
        bool v = _rbVirtual.Checked;
        _cmbRes.Enabled = v;
        _cmbMonitors.Enabled = !v;
    }

    private void SetRunning(bool running)
    {
        _btnConnect.Enabled = !running;
        _btnDisconnect.Enabled = running;
        _grpScreen.Enabled = !running;
        _grpQuality.Enabled = !running;
        _lvPads.Enabled = !running;
        _btnRefresh.Enabled = !running;
        _txtManual.Enabled = !running;
        if (!running)
        {
            _lblStats.Text = "";
            RefreshMonitors();
        }
    }

    private static string FormatStats(SessionStats s) =>
        $"{s.Fps:0} fps · {s.Mbps:0.0} Mbit/s · RTT {s.RttMs:0} ms · {s.Width}×{s.Height} · {s.Encoder} · limite {s.BitrateKbps / 1000.0:0.#} Mbit/s";

    private void AppendLog(string line)
    {
        if (_txtLog.TextLength > 200_000) _txtLog.Text = _txtLog.Text[^100_000..];
        _txtLog.AppendText(line + Environment.NewLine);
    }

    private void Ui(Action a)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }
}
