using System.Net.WebSockets;
using System.Net;
using System.Net.Sockets;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Management;
using System.Text;
using System.Text.Json;

namespace M70BPopup;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        _singleInstance = new Mutex(
            true,
            @"Local\M70BBrightness.SingleInstance",
            out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Samsung 显示器控制已经在系统托盘中运行。",
                "Samsung 显示器控制",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            string? host = LocalState.TryLoadHost();
            if (host is null)
            {
                host = HostPrompt.Show();
                if (host is null)
                    return;
                LocalState.SaveHost(host);
            }

            string token = LocalState.TryLoadToken() ?? string.Empty;
            bool openAtStartup = args.Any(arg =>
                string.Equals(arg, "--open", StringComparison.OrdinalIgnoreCase));
            Application.Run(new TrayContext(host, token, openAtStartup));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"无法启动 Samsung 显示器控制：\n{ex.Message}",
                "Samsung 显示器控制",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

internal static class HostPrompt
{
    public static string? Show()
    {
        using var dialog = new Form
        {
            Text = "连接 Samsung 显示器",
            ClientSize = new Size(390, 148),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        var label = new Label
        {
            Text = "请输入显示器的局域网 IP 地址：",
            AutoSize = true,
            Location = new Point(18, 18)
        };
        var input = new TextBox
        {
            PlaceholderText = "例如 192.168.1.100",
            Location = new Point(20, 48),
            Size = new Size(350, 27)
        };
        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(206, 96),
            Size = new Size(78, 32)
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(292, 96),
            Size = new Size(78, 32)
        };
        dialog.Controls.AddRange([label, input, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        while (dialog.ShowDialog() == DialogResult.OK)
        {
            string host = input.Text.Trim();
            if (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) != UriHostNameType.Unknown)
                return host;
            MessageBox.Show("请输入有效的 IP 地址或主机名。", "Samsung 显示器控制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return null;
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly MonitorControlPopup _popup;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _appIcon;
    private readonly Icon _offlineIcon;
    private readonly MonitorConnectivity _connectivity;
    private readonly HdmiMonitorPresence _hdmiPresence;
    private readonly BrightnessBridgeServer _bridge;
    private bool _exiting;
    private bool _bridgeConnected;
    private bool _hdmiConnected;

    public TrayContext(string host, string token, bool openAtStartup = false)
    {
        _bridge = new BrightnessBridgeServer(host, 8765);
        var session = new SamsungBrightnessSession(host, token, LocalState.LoadBrightness(), _bridge);
        _connectivity = new MonitorConnectivity(_bridge);
        _hdmiPresence = new HdmiMonitorPresence();
        _popup = new MonitorControlPopup(session, _connectivity, host);
        _appIcon = (Icon)(Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application).Clone();
        _offlineIcon = IconVisuals.CreateGrayscale(_appIcon);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开显示器控制", null, (_, _) => _popup.OpenNearCursor());
        menu.Items.Add("恢复 HDMI 画面", null, async (_, _) => await RecoverDisplayAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());

        _trayIcon = new NotifyIcon
        {
            Text = "Samsung 显示器控制",
            Icon = _offlineIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _popup.OpenNearCursor();
        };

        _trayIcon.BalloonTipTitle = "Samsung 显示器控制已启动";
        _trayIcon.BalloonTipText = "左键单击电视图标即可打开控制面板。";
        _trayIcon.ShowBalloonTip(3000);
        _ = _popup.Handle;
        _connectivity.StatusChanged += TrayConnectivity_StatusChanged;
        _hdmiPresence.StatusChanged += HdmiPresence_StatusChanged;
        _bridge.Start();
        _connectivity.Start();
        _hdmiPresence.Start();
        if (openAtStartup)
        {
            EventHandler? openWhenReady = null;
            openWhenReady = (_, _) =>
            {
                Application.Idle -= openWhenReady;
                _popup.OpenNearCursor();
            };
            Application.Idle += openWhenReady;
        }
    }

    private void TrayConnectivity_StatusChanged(bool connected)
    {
        _bridgeConnected = connected;
        UpdateTrayConnectionState();
    }

    private void HdmiPresence_StatusChanged(bool connected)
    {
        _hdmiConnected = connected;
        UpdateTrayConnectionState();
    }

    private void UpdateTrayConnectionState()
    {
        void Update()
        {
            if (_exiting)
                return;

            bool connected = _bridgeConnected && _hdmiConnected;
            _trayIcon.Icon = connected ? _appIcon : _offlineIcon;
            _trayIcon.Text = connected
                ? "Samsung 显示器控制 · 已连接"
                : !_hdmiConnected
                    ? "Samsung 显示器控制 · HDMI 未连接"
                    : "Samsung 显示器控制 · 桥接器已断开";
        }

        if (_popup.IsHandleCreated && _popup.InvokeRequired)
        {
            try { _popup.BeginInvoke(Update); } catch (InvalidOperationException) { }
        }
        else
        {
            Update();
        }
    }

    private async Task RecoverDisplayAsync()
    {
        try
        {
            await _bridge.RecoverDisplayAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"无法恢复 HDMI 画面：\n{error.Message}",
                "Samsung 显示器控制",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting)
            return;

        _exiting = true;
        _connectivity.StatusChanged -= TrayConnectivity_StatusChanged;
        _hdmiPresence.StatusChanged -= HdmiPresence_StatusChanged;
        _hdmiPresence.Dispose();
        await _popup.ShutdownAsync();
        await _bridge.RequestExitAsync();
        await _connectivity.DisposeAsync();
        await _bridge.DisposeAsync();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _offlineIcon.Dispose();
        _appIcon.Dispose();
        _popup.Dispose();
        ExitThread();
    }
}

internal sealed class BrightnessPopup : Form
{
    private readonly SamsungBrightnessSession _session;
    private readonly MonitorConnectivity _connectivity;
    private readonly string _host;
    private readonly ModernBrightnessSlider _slider = new();
    private readonly Label _subtitleLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _commandLabel = new();
    private readonly ModernSunButton _minimumButton = new() { GlyphSize = 11F };
    private readonly ModernSunButton _maximumButton = new() { GlyphSize = 17F };
    private readonly ProgressBar _progressBar = new();
    private readonly System.Windows.Forms.Timer _sliderTimer = new();
    private readonly System.Windows.Forms.Timer _closeTimer = new();
    private readonly System.Windows.Forms.Timer _outsideClickTimer = new();
    private bool _opening;
    private bool _waking;
    private bool _closing;
    private bool _applying;
    private bool _ignoreSlider;
    private bool? _connected;
    private bool _mouseButtonsWereDown;
    private DateTime _outsideClickArmedAt;

    public BrightnessPopup(
        SamsungBrightnessSession session,
        MonitorConnectivity connectivity,
        string host)
    {
        _session = session;
        _connectivity = connectivity;
        _host = host;

        Text = "Samsung M70B 亮度";
        ClientSize = new Size(400, 216);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(44, 44, 44);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9F);
        DoubleBuffered = true;

        var title = new Label
        {
            Text = "显示器亮度",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font(Font.FontFamily, 12.5F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 16)
        };

        _subtitleLabel.Text = "Samsung Tizen  ·  正在检测显示器…";
        _subtitleLabel.ForeColor = Color.FromArgb(174, 174, 174);
        _subtitleLabel.BackColor = Color.Transparent;
        _subtitleLabel.Font = new Font(Font.FontFamily, 8.5F);
        _subtitleLabel.AutoSize = true;
        _subtitleLabel.Location = new Point(21, 45);

        _slider.Minimum = 0;
        _slider.Maximum = 50;
        _slider.Value = _session.CurrentBrightness;
        _slider.Location = new Point(58, 70);
        _slider.Size = new Size(284, 66);
        _slider.Scroll += (_, _) =>
        {
            if (_ignoreSlider)
                return;
            _sliderTimer.Stop();
            _sliderTimer.Start();
        };
        _slider.MouseUp += async (_, _) =>
        {
            _sliderTimer.Stop();
            await ApplyPendingSliderAsync();
        };

        _minimumButton.AccessibleName = "最小亮度";
        _minimumButton.Location = new Point(16, 98);
        _minimumButton.Size = new Size(38, 38);
        _minimumButton.Click += async (_, _) => await ResetAsync(minimum: true);

        _maximumButton.AccessibleName = "最大亮度";
        _maximumButton.Location = new Point(346, 98);
        _maximumButton.Size = new Size(38, 38);
        _maximumButton.Click += async (_, _) => await ResetAsync(minimum: false);

        _statusLabel.Text = string.Empty;
        _statusLabel.ForeColor = Color.FromArgb(228, 228, 228);
        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.Location = new Point(20, 148);
        _statusLabel.Size = new Size(360, 22);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        _commandLabel.Text = "命令：等待操作";
        _commandLabel.ForeColor = Color.FromArgb(155, 155, 155);
        _commandLabel.BackColor = Color.Transparent;
        _commandLabel.Font = new Font("Cascadia Mono", 8.25F);
        _commandLabel.Location = new Point(20, 172);
        _commandLabel.Size = new Size(360, 21);

        _progressBar.Location = new Point(20, 204);
        _progressBar.Size = new Size(360, 4);
        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressBar.MarqueeAnimationSpeed = 28;
        _progressBar.Visible = false;

        _sliderTimer.Interval = 140;
        _sliderTimer.Tick += async (_, _) =>
        {
            _sliderTimer.Stop();
            await ApplyPendingSliderAsync();
        };

        _closeTimer.Interval = 320;
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            _outsideClickTimer.Stop();
            Hide();
            SetBusyDisplay(false);
            _closing = false;
        };

        _outsideClickTimer.Interval = 30;
        _outsideClickTimer.Tick += (_, _) =>
        {
            bool mouseButtonsDown = Control.MouseButtons != MouseButtons.None;
            bool newClick = mouseButtonsDown && !_mouseButtonsWereDown;
            _mouseButtonsWereDown = mouseButtonsDown;
            if (newClick &&
                DateTime.UtcNow >= _outsideClickArmedAt &&
                !Bounds.Contains(Cursor.Position))
                BeginHideAndCloseSession();
        };

        _session.ProgressChanged += Session_ProgressChanged;

        Controls.AddRange([
            title,
            _subtitleLabel,
            _slider,
            _minimumButton,
            _maximumButton,
            _statusLabel,
            _commandLabel,
            _progressBar
        ]);

        _connectivity.StatusChanged += Connectivity_StatusChanged;
    }

    public async void OpenNearCursor()
    {
        if (Visible)
        {
            Activate();
            if (_connected is not true)
                await WakeBridgeAsync();
            return;
        }

        Screen screen = Screen.FromPoint(Cursor.Position);
        Rectangle area = screen.WorkingArea;
        Location = new Point(area.Right - Width - 4, area.Bottom - Height - 4);
        SyncSliderToSession();
        Show();
        Activate();
        _mouseButtonsWereDown = Control.MouseButtons != MouseButtons.None;
        _outsideClickArmedAt = DateTime.UtcNow.AddMilliseconds(250);
        _outsideClickTimer.Start();

        if (_connected is true)
        {
            if (_session.IsOpen)
                ShowReadyState();
            else
                await BeginOpenSessionAsync();
        }
        else
            await WakeBridgeAsync();
    }

    private async Task WakeBridgeAsync()
    {
        if (_waking || _closing || !Visible || _connected is true)
            return;

        _waking = true;
        SetTransitionLayout(true);
        SetControlsEnabled(false);
        SetBusyDisplay(true);
        _subtitleLabel.Text = "Samsung Tizen  ·  正在启动桥接器";
        _subtitleLabel.ForeColor = Color.FromArgb(174, 174, 174);
        _statusLabel.Text = "正在让显示器打开 HDMI 亮度桥接器…";
        _commandLabel.Text = "命令：LAUNCH_APP — q8YFGkFK1p.M70BProbe";
        try
        {
            await _session.WakeBridgeAsync();
            if (!_session.IsBridgeConnected)
                throw new TimeoutException("电视端应用已启动，但没有连回电脑。请确认电视和电脑在同一局域网。");
            _connected = true;
            await BeginOpenSessionAsync();
        }
        catch (Exception ex)
        {
            if (Visible && !_closing && _connected is not true)
            {
                SetTransitionLayout(true);
                SetControlsEnabled(false);
                SetBusyDisplay(false);
                _subtitleLabel.Text = "Samsung Tizen  ·  自动启动失败";
                _subtitleLabel.ForeColor = Color.FromArgb(255, 128, 128);
                _statusLabel.Text = "无法自动启动电视端桥接器";
                _commandLabel.Text = $"错误：{ex.Message}";
            }
        }
        finally
        {
            _waking = false;
        }
    }

    private async Task BeginOpenSessionAsync()
    {
        if (_opening || _closing || !Visible || _connected is not true || _session.IsOpen)
            return;

        SetTransitionLayout(true);
        SetControlsEnabled(false);
        SetBusyDisplay(true);
        _statusLabel.Text = "启动中，正在给显示器发送命令…";
        _commandLabel.Text = "命令：CONNECT — 建立局域网连接";
        _opening = true;
        try
        {
            await _session.OpenAsync();
            if (Visible && _connected is true && _session.IsOpen)
                ShowReadyState();
        }
        catch (Exception ex)
        {
            if (Visible && !_closing)
            {
                SetTransitionLayout(true);
                SetControlsEnabled(false);
                SetBusyDisplay(false);
                _statusLabel.Text = "无法连接或打开显示器亮度界面";
                _commandLabel.Text = $"错误：{ex.Message}";
            }
        }
        finally
        {
            _opening = false;
        }
    }

    private void Connectivity_StatusChanged(bool connected)
    {
        void Update()
        {
            if (IsDisposed)
                return;

            _connected = connected;
            _subtitleLabel.Text = connected
                ? "Samsung Tizen  ·  绝对背光已连接"
                : "Samsung Tizen  ·  桥接器已断开";
            _subtitleLabel.ForeColor = connected
                ? Color.FromArgb(113, 210, 143)
                : Color.FromArgb(255, 128, 128);

            if (_closing)
                return;

            if (!connected)
                _ = HandleDisconnectedAsync();
            else if (Visible)
            {
                if (_session.IsOpen)
                    ShowReadyState();
                else
                    _ = BeginOpenSessionAsync();
            }
        }

        if (IsHandleCreated && InvokeRequired)
            BeginInvoke(Update);
        else
            Update();
    }

    private async Task HandleDisconnectedAsync()
    {
        if (Visible)
        {
            SetTransitionLayout(true);
            SetControlsEnabled(false);
            SetBusyDisplay(false);
            _statusLabel.Text = "显示器已断开，亮度控制不可用";
            _commandLabel.Text = $"OFFLINE — 等待 {_host} 连接本机 :8765";
        }
        await _session.AbortAsync();
    }

    private void ShowConnectivityState()
    {
        SetTransitionLayout(true);
        SetControlsEnabled(false);
        if (_connected is null)
        {
            SetBusyDisplay(true);
            _statusLabel.Text = "正在检测显示器连接状态…";
            _commandLabel.Text = "命令：LISTEN — 等待电视桥接器连接 :8765";
        }
        else
        {
            SetBusyDisplay(false);
            _statusLabel.Text = "显示器已断开，亮度控制不可用";
            _commandLabel.Text = $"OFFLINE — 等待 {_host} 连接本机 :8765";
        }
    }

    private async Task ApplyPendingSliderAsync()
    {
        if (_applying || !_session.IsOpen)
            return;

        _applying = true;
        // Keep the slider interactive while a command is in flight.  Disabling it
        // during a drag releases mouse capture and makes the popup appear frozen.
        _slider.Enabled = true;
        _minimumButton.Enabled = false;
        _maximumButton.Enabled = false;
        UseWaitCursor = false;
        SetBusyDisplay(true);
        try
        {
            int lastTarget = -1;
            while (Visible && _slider.Value != _session.CurrentBrightness)
            {
                int target = _slider.Value;
                // A picture mode or Eco setting can clamp a requested value.  Do
                // not spin forever when the value read back from the TV differs.
                if (target == lastTarget)
                    break;
                lastTarget = target;
                _statusLabel.Text = $"正在调节到 {target}…";
                await _session.MoveToAsync(target);
            }
            if (Visible)
                _statusLabel.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"调节失败：{ex.Message}";
            SyncSliderToSession();
        }
        finally
        {
            _applying = false;
            if (Visible && _connected is true && _session.IsOpen)
            {
                SetControlsEnabled(true);
                SetBusyDisplay(false);
            }
        }
    }

    private async Task ResetAsync(bool minimum)
    {
        if (!_session.IsOpen)
            return;

        SetControlsEnabled(false);
        SetBusyDisplay(true);
        try
        {
            _statusLabel.Text = minimum ? "正在重置为最小亮度…" : "正在重置为最大亮度…";
            if (minimum)
                await _session.ResetMinimumAsync();
            else
                await _session.ResetMaximumAsync();
            SyncSliderToSession();
            _statusLabel.Text = "已同步当前亮度；拖动滑块即可调节";
            _commandLabel.Text = minimum
                ? "命令：SET_BACKLIGHT 0 — 绝对最小亮度"
                : "命令：SET_BACKLIGHT 50 — 绝对最大亮度";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"重置失败：{ex.Message}";
        }
        finally
        {
            if (Visible && _connected is true && _session.IsOpen)
            {
                SetControlsEnabled(true);
                SetBusyDisplay(false);
            }
        }
    }

    private void BeginHideAndCloseSession()
    {
        if (_closing)
            return;

        _closing = true;
        _outsideClickTimer.Stop();
        _sliderTimer.Stop();
        SetTransitionLayout(true);
        SetControlsEnabled(false);
        SetBusyDisplay(true);
        _statusLabel.Text = "正在收起亮度窗口…";
        _commandLabel.Text = "命令：HIDE — 保持绝对背光通道在线";
        _closeTimer.Start();
    }

    public async Task ShutdownAsync()
    {
        _sliderTimer.Stop();
        _closeTimer.Stop();
        _outsideClickTimer.Stop();
        Hide();
        await _session.CloseAsync();
        await _session.DisposeAsync();
    }

    private void SyncSliderToSession()
    {
        _ignoreSlider = true;
        _slider.Value = _session.CurrentBrightness;
        _ignoreSlider = false;
    }

    private void ShowReadyState()
    {
        SyncSliderToSession();
        SetTransitionLayout(false);
        SetControlsEnabled(true);
        SetBusyDisplay(false);
        _subtitleLabel.Text = "Samsung Tizen  ·  绝对背光已连接";
        _subtitleLabel.ForeColor = Color.FromArgb(113, 210, 143);
        _statusLabel.Text = string.Empty;
        _commandLabel.Text = "命令：READY — 绝对背光通道已就绪";
    }

    private void SetControlsEnabled(bool enabled)
    {
        _slider.Enabled = enabled;
        _minimumButton.Enabled = enabled;
        _maximumButton.Enabled = enabled;
        UseWaitCursor = !enabled;
    }

    private void SetBusyDisplay(bool busy)
    {
        _progressBar.Visible = busy;
    }

    private void SetTransitionLayout(bool transition)
    {
        _slider.Visible = !transition;
        _minimumButton.Visible = !transition;
        _maximumButton.Visible = !transition;

        if (transition)
        {
            _statusLabel.Location = new Point(20, 94);
            _statusLabel.Size = new Size(360, 25);
            _commandLabel.Location = new Point(20, 126);
            _commandLabel.Size = new Size(360, 23);
            _progressBar.Location = new Point(20, 168);
            _progressBar.Size = new Size(360, 4);
        }
        else
        {
            _statusLabel.Location = new Point(20, 148);
            _statusLabel.Size = new Size(360, 22);
            _commandLabel.Location = new Point(20, 172);
            _commandLabel.Size = new Size(360, 21);
            _progressBar.Location = new Point(20, 204);
            _progressBar.Size = new Size(360, 4);
        }
    }

    private void Session_ProgressChanged(string status, string command)
    {
        void Update()
        {
            if (_closing && !status.StartsWith("退出"))
                return;
            _statusLabel.Text = status;
            _commandLabel.Text = $"命令：{command}";
        }

        if (InvokeRequired)
            BeginInvoke(Update);
        else
            Update();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && !_closing && DateTime.UtcNow >= _outsideClickArmedAt)
            BeginHideAndCloseSession();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int enabled = 1;
            int rounded = 2;
            int transientBackdrop = 3;
            DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
            DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
            DwmSetWindowAttribute(Handle, 38, ref transientBackdrop, sizeof(int));
        }
        catch
        {
            // The custom dark surface remains usable when a DWM attribute is absent.
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Color.FromArgb(74, 74, 74));
        using GraphicsPath path = ModernDrawing.RoundedRectangle(
            new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1),
            11);
        e.Graphics.DrawPath(border, path);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);
}

internal sealed class ModernBrightnessSlider : Control
{
    private int _minimum;
    private int _maximum = 50;
    private int _value;
    private bool _dragging;
    private bool _showValueBubble;
    private readonly System.Windows.Forms.Timer _bubbleTimer = new();

    public ModernBrightnessSlider()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        _bubbleTimer.Interval = 750;
        _bubbleTimer.Tick += (_, _) =>
        {
            _bubbleTimer.Stop();
            _showValueBubble = false;
            Invalidate();
        };
    }

    [DefaultValue(0)]
    public int Minimum
    {
        get => _minimum;
        set { _minimum = value; Value = _value; Invalidate(); }
    }

    [DefaultValue(50)]
    public int Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(value, _minimum + 1); Value = _value; Invalidate(); }
    }

    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, _minimum, _maximum);
            if (_value == clamped)
                return;
            _value = clamped;
            Invalidate();
        }
    }

    public event EventHandler? Scroll;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        const int railPadding = 10;
        int railLeft = railPadding;
        int railRight = Width - railPadding;
        int railWidth = Math.Max(1, railRight - railLeft);
        int centerY = Height - 20;
        float ratio = (float)(_value - _minimum) / (_maximum - _minimum);
        int thumbX = railLeft + (int)Math.Round(railWidth * ratio);

        using var inactivePen = new Pen(Color.FromArgb(105, 105, 105), 4)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var activePen = new Pen(
            Enabled ? Color.FromArgb(96, 205, 255) : Color.FromArgb(72, 112, 130),
            4)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLine(inactivePen, railLeft, centerY, railRight, centerY);
        g.DrawLine(activePen, railLeft, centerY, thumbX, centerY);

        using var outerBrush = new SolidBrush(Color.FromArgb(79, 79, 79));
        using var thumbBrush = new SolidBrush(
            Enabled ? Color.FromArgb(96, 205, 255) : Color.FromArgb(110, 145, 158));
        g.FillEllipse(outerBrush, thumbX - 10, centerY - 10, 20, 20);
        g.FillEllipse(thumbBrush, thumbX - 6, centerY - 6, 12, 12);

        if (_showValueBubble)
        {
            string valueText = _value.ToString();
            using var bubbleFont = new Font("Segoe UI Variable Text", 9F, FontStyle.Bold);
            Size textSize = TextRenderer.MeasureText(valueText, bubbleFont);
            int bubbleWidth = Math.Max(34, textSize.Width + 14);
            int bubbleX = Math.Clamp(thumbX - bubbleWidth / 2, 0, Width - bubbleWidth);
            Rectangle bubble = new(bubbleX, 1, bubbleWidth, 27);
            using GraphicsPath bubblePath = ModernDrawing.RoundedRectangle(bubble, 6);
            using var bubbleBrush = new SolidBrush(Color.FromArgb(72, 72, 72));
            using var bubbleBorder = new Pen(Color.FromArgb(92, 92, 92));
            g.FillPath(bubbleBrush, bubblePath);
            g.DrawPath(bubbleBorder, bubblePath);
            TextRenderer.DrawText(
                g,
                valueText,
                bubbleFont,
                bubble,
                Color.White,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left)
            return;
        _bubbleTimer.Stop();
        _showValueBubble = true;
        _dragging = true;
        Capture = true;
        UpdateFromMouse(e.X);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
            UpdateFromMouse(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            UpdateFromMouse(e.X);
            _dragging = false;
            Capture = false;
            _bubbleTimer.Stop();
            _bubbleTimer.Start();
        }
        base.OnMouseUp(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bubbleTimer.Dispose();
        base.Dispose(disposing);
    }

    private void UpdateFromMouse(int x)
    {
        const int railLeft = 10;
        int railRight = Width - 10;
        float ratio = Math.Clamp((float)(x - railLeft) / Math.Max(1, railRight - railLeft), 0F, 1F);
        int newValue = _minimum + (int)Math.Round((_maximum - _minimum) * ratio);
        if (newValue == _value)
            return;
        _value = newValue;
        Invalidate();
        Scroll?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class ModernSunButton : Button
{
    private bool _hovered;
    private bool _pressed;
    private float _glyphSize = 14F;

    public ModernSunButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.White;
        BackColor = Color.Transparent;
        Font = new Font("Segoe UI Symbol", 14F);
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
    }

    [DefaultValue(14F)]
    public float GlyphSize
    {
        get => _glyphSize;
        set { _glyphSize = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hovered || _pressed)
        {
            Color fill = _pressed
                ? Color.FromArgb(78, 78, 78)
                : Color.FromArgb(64, 64, 64);
            using GraphicsPath hoverPath = ModernDrawing.RoundedRectangle(
                new Rectangle(1, 1, Width - 3, Height - 3),
                6);
            using var hoverBrush = new SolidBrush(fill);
            e.Graphics.FillPath(hoverBrush, hoverPath);
        }

        Color glyphColor = Enabled
            ? Color.FromArgb(224, 224, 224)
            : Color.FromArgb(112, 112, 112);
        float cx = Width / 2F;
        float cy = Height / 2F;
        float coreRadius = GlyphSize * 0.18F;
        float rayStart = GlyphSize * 0.30F;
        float rayEnd = GlyphSize * 0.48F;
        using var glyphPen = new Pen(glyphColor, 1.45F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        e.Graphics.DrawEllipse(
            glyphPen,
            cx - coreRadius,
            cy - coreRadius,
            coreRadius * 2F,
            coreRadius * 2F);
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4D;
            e.Graphics.DrawLine(
                glyphPen,
                cx + (float)Math.Cos(angle) * rayStart,
                cy + (float)Math.Sin(angle) * rayStart,
                cx + (float)Math.Cos(angle) * rayEnd,
                cy + (float)Math.Sin(angle) * rayEnd);
        }
    }
}

internal static class IconVisuals
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    public static Icon CreateGrayscale(Icon source)
    {
        using Bitmap bitmap = source.ToBitmap();
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                int gray = (int)Math.Round(
                    pixel.R * 0.2126D +
                    pixel.G * 0.7152D +
                    pixel.B * 0.0722D);
                bitmap.SetPixel(x, y, Color.FromArgb(pixel.A, gray, gray, gray));
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}

internal static class ModernDrawing
{
    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed record MonitorSettingCapability(
    string Key,
    string Kind,
    int? Minimum,
    int? Maximum,
    IReadOnlyList<string> Options,
    bool Writable,
    bool Experimental = false,
    bool RequiresConfirmation = false);

internal sealed class MonitorSettingsSnapshot
{
    public MonitorSettingsSnapshot(
        IReadOnlyDictionary<string, MonitorSettingCapability> capabilities,
        IReadOnlyDictionary<string, object?> values)
    {
        Capabilities = capabilities;
        Values = values;
    }

    public IReadOnlyDictionary<string, MonitorSettingCapability> Capabilities { get; }
    public IReadOnlyDictionary<string, object?> Values { get; }
}

internal sealed class SamsungBrightnessSession : IAsyncDisposable
{
    private const string AppName = "Codex M70B Control";
    private const string BridgeAppId = "q8YFGkFK1p.M70BProbe";
    private readonly string _host;
    private string _token;
    private readonly BrightnessBridgeServer _bridge;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientWebSocket? _socket;
    private int _initialized;

    public SamsungBrightnessSession(
        string host,
        string token,
        int currentBrightness,
        BrightnessBridgeServer bridge)
    {
        _host = host;
        _token = token;
        _bridge = bridge;
        CurrentBrightness = Math.Clamp(currentBrightness, 0, 50);
    }

    public int CurrentBrightness { get; private set; }
    public bool IsBridgeConnected => _bridge.IsConnected;
    public bool IsOpen => _bridge.IsConnected && Volatile.Read(ref _initialized) == 1;
    public event Action<string, string>? ProgressChanged;

    public async Task WakeBridgeAsync()
    {
        if (_bridge.IsConnected)
            return;

        Report(
            "正在让显示器打开 HDMI 亮度桥接器…",
            $"MS_APPLICATION_START — {BridgeAppId}");

        try
        {
            await LaunchBridgeAppViaControlAsync();
            if (await WaitForBridgeAsync(TimeSpan.FromSeconds(10)))
                return;
        }
        catch
        {
            // Some older firmware does not expose the application-control channel.
            // Fall through to the paired Eden remote channel below.
        }

        if (string.IsNullOrWhiteSpace(_token))
        {
            Report(
                "请在显示器上允许电脑遥控，然后稍候…",
                "PAIR_REMOTE — 等待电视授权");
            _token = await PairAsync(_host);
            LocalState.SaveToken(_token);
        }

        Report(
            "正在尝试兼容启动方式…",
            $"LAUNCH_APP {BridgeAppId} — NATIVE_LAUNCH");

        try
        {
            await LaunchBridgeAppAsync("NATIVE_LAUNCH");
        }
        catch (UnauthorizedAccessException)
        {
            Report(
                "请在显示器上允许电脑遥控，然后稍候…",
                "PAIR_REMOTE — 等待电视授权");
            _token = await PairAsync(_host);
            LocalState.SaveToken(_token);
            await LaunchBridgeAppAsync("NATIVE_LAUNCH");
        }

        if (await WaitForBridgeAsync(TimeSpan.FromSeconds(8)))
            return;

        // Developer-installed Web applications are reported with different app
        // types on different firmware revisions.  Try Eden's other launch mode
        // before declaring the display unavailable.
        Report(
            "第一次启动未响应，正在尝试兼容模式…",
            $"LAUNCH_APP {BridgeAppId} — DEEP_LINK");
        await LaunchBridgeAppAsync("DEEP_LINK");

        if (!await WaitForBridgeAsync(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("显示器没有启动亮度桥接器。");
    }

    public static async Task<string> PairAsync(string host)
    {
        using var socket = CreateSocket();
        var uri = CreateUri(host, token: null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await socket.ConnectAsync(uri, timeout.Token);

        while (true)
        {
            string text = await ReceiveTextAsync(socket, timeout.Token);
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            string? eventName = root.TryGetProperty("event", out JsonElement eventElement)
                ? eventElement.GetString()
                : null;

            if (eventName == "ms.channel.unauthorized")
                throw new UnauthorizedAccessException("显示器未授权，请重新启动并在显示器上选择“允许”。");

            if (eventName == "ms.channel.connect" &&
                root.TryGetProperty("data", out JsonElement data) &&
                data.TryGetProperty("token", out JsonElement tokenElement) &&
                tokenElement.GetString() is string token && token.Length > 0)
                return token;
        }
    }

    private async Task LaunchBridgeAppAsync(string actionType)
    {
        using var socket = CreateSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(CreateUri(_host, _token), timeout.Token);
        await RequireConnectedAsync(socket, timeout.Token);

        string json = JsonSerializer.Serialize(new
        {
            method = "ms.channel.emit",
            @params = new
            {
                @event = "ed.apps.launch",
                to = "host",
                data = new
                {
                    appId = BridgeAppId,
                    action_type = actionType,
                    metaTag = string.Empty
                }
            }
        });
        byte[] data = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(data, WebSocketMessageType.Text, true, timeout.Token);
        await Task.Delay(250, timeout.Token);
    }

    private async Task LaunchBridgeAppViaControlAsync()
    {
        using var socket = CreateSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(CreateControlUri(_host), timeout.Token);
        await RequireConnectedAsync(socket, timeout.Token);

        string json = JsonSerializer.Serialize(new
        {
            id = BridgeAppId,
            method = "ms.application.start",
            @params = new { id = BridgeAppId }
        });
        byte[] data = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(data, WebSocketMessageType.Text, true, timeout.Token);
        await Task.Delay(250, timeout.Token);
    }

    private async Task<bool> WaitForBridgeAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_bridge.IsConnected)
                return true;
            await Task.Delay(200);
        }
        return _bridge.IsConnected;
    }

    public async Task OpenAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_bridge.IsConnected)
                throw new InvalidOperationException("电视亮度桥接器尚未连接。请先在电视上启动桥接应用。");

            Report("启动中，正在读取显示器背光…", "GET_BACKLIGHT — 读取绝对值");
            CurrentBrightness = await _bridge.GetBrightnessAsync();
            LocalState.SaveBrightness(CurrentBrightness);
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MoveToAsync(int target)
    {
        target = Math.Clamp(target, 0, 50);
        await _gate.WaitAsync();
        try
        {
            if (!IsOpen)
                throw new InvalidOperationException("电视亮度桥接器已断开。");
            Report("调节中，正在给电视发送命令…", $"SET_BACKLIGHT {target} — 绝对值");
            CurrentBrightness = await _bridge.SetBrightnessAsync(target);
            LocalState.SaveBrightness(CurrentBrightness);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MonitorSettingsSnapshot> GetSettingsSnapshotAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsOpen)
                throw new InvalidOperationException("电视亮度桥接器已断开。");

            Report("正在读取显示器可调项…", "GET_CAPABILITIES + GET_SETTINGS");
            try
            {
                JsonElement capabilityResponse = await _bridge.GetCapabilitiesAsync();
                JsonElement settingsResponse = await _bridge.GetSettingsAsync();
                Dictionary<string, MonitorSettingCapability> capabilities =
                    ParseCapabilities(capabilityResponse);
                Dictionary<string, object?> values = ParseSettings(settingsResponse);

                if (values.TryGetValue("backlight", out object? backlightValue) &&
                    TryConvertInt(backlightValue, out int backlight))
                {
                    CurrentBrightness = Math.Clamp(backlight, 0, 50);
                    LocalState.SaveBrightness(CurrentBrightness);
                }
                return new MonitorSettingsSnapshot(capabilities, values);
            }
            catch (InvalidOperationException error)
                when (error.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
            {
                CurrentBrightness = await _bridge.GetBrightnessAsync();
                LocalState.SaveBrightness(CurrentBrightness);
                var capability = new MonitorSettingCapability(
                    "backlight", "range", 0, 50, Array.Empty<string>(), true);
                return new MonitorSettingsSnapshot(
                    new Dictionary<string, MonitorSettingCapability>
                    {
                        ["backlight"] = capability
                    },
                    new Dictionary<string, object?>
                    {
                        ["backlight"] = CurrentBrightness
                    });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object?> SetMonitorSettingAsync(
        string key,
        object value,
        bool confirmed = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsOpen)
                throw new InvalidOperationException("电视亮度桥接器已断开。");

            Report("调节中，正在给电视发送命令…", $"SET_SETTING {key} = {value}");
            if (key == "backlight" && TryConvertInt(value, out int legacyBacklight))
            {
                CurrentBrightness = await _bridge.SetBrightnessAsync(legacyBacklight);
                LocalState.SaveBrightness(CurrentBrightness);
                return CurrentBrightness;
            }

            JsonElement response = await _bridge.SetSettingAsync(key, value, confirmed);
            if (!response.TryGetProperty("value", out JsonElement responseValue))
                throw new InvalidDataException("电视返回的设置响应无效。");
            object? actual = ConvertJsonValue(responseValue);
            if (key == "backlight" && TryConvertInt(actual, out int backlight))
            {
                CurrentBrightness = Math.Clamp(backlight, 0, 50);
                LocalState.SaveBrightness(CurrentBrightness);
            }
            return actual;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetMinimumAsync()
    {
        await ResetAsync(0);
    }

    public async Task ResetMaximumAsync()
    {
        await ResetAsync(50);
    }

    private async Task ResetAsync(int resultingBrightness)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsOpen)
                throw new InvalidOperationException("电视亮度桥接器已断开。");
            Report("重置中，正在给电视发送命令…", $"SET_BACKLIGHT {resultingBrightness} — 绝对值");
            CurrentBrightness = await _bridge.SetBrightnessAsync(resultingBrightness);
            LocalState.SaveBrightness(CurrentBrightness);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        Report("亮度窗口已收起", "HIDE — 绝对背光通道保持在线");
        await Task.CompletedTask;
    }

    public async Task AbortAsync()
    {
        Volatile.Write(ref _initialized, 0);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        AbortSocket();
        await Task.CompletedTask;
        _gate.Dispose();
    }

    private ClientWebSocket RequireOpenSocket()
    {
        if (!IsOpen || _socket is null)
            throw new InvalidOperationException("显示器亮度界面尚未打开。");
        return _socket;
    }

    private void Report(string status, string command)
        => ProgressChanged?.Invoke(status, command);

    private static Dictionary<string, MonitorSettingCapability> ParseCapabilities(
        JsonElement response)
    {
        if (!response.TryGetProperty("settings", out JsonElement settings) ||
            settings.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("电视未返回有效的可调项列表。");

        var result = new Dictionary<string, MonitorSettingCapability>(StringComparer.Ordinal);
        foreach (JsonElement item in settings.EnumerateArray())
        {
            string? key = item.TryGetProperty("id", out JsonElement idElement)
                ? idElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(key))
                continue;

            string type = item.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? "integer"
                : "integer";
            string kind = type switch
            {
                "boolean" => "toggle",
                "dynamic" or "enum" => "choice",
                _ => "range"
            };
            int? minimum = item.TryGetProperty("min", out JsonElement minimumElement) &&
                           minimumElement.TryGetInt32(out int min)
                ? min
                : null;
            int? maximum = item.TryGetProperty("max", out JsonElement maximumElement) &&
                           maximumElement.TryGetInt32(out int max)
                ? max
                : null;
            bool writable = item.TryGetProperty("writable", out JsonElement writableElement) &&
                            writableElement.ValueKind == JsonValueKind.True;
            bool experimental = item.TryGetProperty("experimental", out JsonElement experimentalElement) &&
                                experimentalElement.ValueKind == JsonValueKind.True;
            bool requiresConfirmation = item.TryGetProperty("confirmation", out JsonElement confirmationElement) &&
                                        confirmationElement.ValueKind == JsonValueKind.True;
            string[] options = item.TryGetProperty("values", out JsonElement valuesElement) &&
                               valuesElement.ValueKind == JsonValueKind.Array
                ? valuesElement.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray()
                : Array.Empty<string>();
            result[key] = new MonitorSettingCapability(
                key, kind, minimum, maximum, options, writable, experimental,
                requiresConfirmation);
        }
        return result;
    }

    private static Dictionary<string, object?> ParseSettings(JsonElement response)
    {
        if (!response.TryGetProperty("values", out JsonElement values) ||
            values.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("电视未返回有效的设置值。");

        return values.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ConvertJsonValue(property.Value),
            StringComparer.Ordinal);
    }

    private static object? ConvertJsonValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.Clone()
        };

    private static bool TryConvertInt(object? value, out int number)
    {
        switch (value)
        {
            case int integer:
                number = integer;
                return true;
            case long longInteger when longInteger is >= int.MinValue and <= int.MaxValue:
                number = (int)longInteger;
                return true;
            case string text when int.TryParse(text, out int parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private void AbortSocket()
    {
        _socket?.Abort();
        _socket?.Dispose();
        _socket = null;
    }

    private static ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return socket;
    }

    private static Uri CreateUri(string host, string? token)
    {
        string encodedName = Uri.EscapeDataString(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(AppName)));
        string uri = $"wss://{host}:8002/api/v2/channels/samsung.remote.control?name={encodedName}";
        if (!string.IsNullOrEmpty(token))
            uri += $"&token={Uri.EscapeDataString(token)}";
        return new Uri(uri);
    }

    private static Uri CreateControlUri(string host)
    {
        string encodedName = Uri.EscapeDataString(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(AppName)));
        return new Uri($"wss://{host}:8002/api/v2?name={encodedName}");
    }

    private static async Task RequireConnectedAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string text = await ReceiveTextAsync(socket, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(text);
            string? eventName = document.RootElement.TryGetProperty("event", out JsonElement eventElement)
                ? eventElement.GetString()
                : null;
            if (eventName == "ms.channel.connect")
                return;
            if (eventName == "ms.channel.unauthorized")
                throw new UnauthorizedAccessException("显示器没有授权这台电脑进行遥控。");
        }
    }

    private static async Task<string> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static async Task ClickAsync(
        ClientWebSocket socket,
        string key,
        int delayMs,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(new
        {
            method = "ms.remote.control",
            @params = new
            {
                Cmd = "Click",
                DataOfCmd = key,
                Option = "false",
                TypeOfRemote = "SendRemoteKey"
            }
        });
        byte[] data = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(data, WebSocketMessageType.Text, true, cancellationToken);
        await Task.Delay(delayMs, cancellationToken);
    }
}

internal sealed class BrightnessBridgeServer : IAsyncDisposable
{
    private readonly string _allowedHost;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _socketLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private Task? _acceptLoop;
    private WebSocket? _socket;
    private int _connected;
    private int _currentBrightness;

    public BrightnessBridgeServer(string allowedHost, int port)
    {
        _allowedHost = allowedHost;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public bool IsConnected => Volatile.Read(ref _connected) == 1;
    public int CurrentBrightness => Volatile.Read(ref _currentBrightness);
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        if (_acceptLoop is not null)
            return;
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public async Task<int> GetBrightnessAsync()
    {
        JsonElement response = await SendRequestAsync("get", null, TimeSpan.FromSeconds(5));
        return ReadIntegerValue(response, 0, 50);
    }

    public async Task<int> SetBrightnessAsync(int value)
    {
        JsonElement response = await SendRequestAsync(
            "set",
            new Dictionary<string, object?> { ["value"] = Math.Clamp(value, 0, 50) },
            TimeSpan.FromSeconds(5));
        return ReadIntegerValue(response, 0, 50);
    }

    public Task<JsonElement> GetCapabilitiesAsync()
        => SendRequestAsync("capabilities", null, TimeSpan.FromSeconds(8));

    public Task<JsonElement> GetSettingsAsync()
        => SendRequestAsync("get_settings", null, TimeSpan.FromSeconds(8));

    public Task<JsonElement> RecoverDisplayAsync()
        => SendRequestAsync("recover_display", null, TimeSpan.FromSeconds(12));

    public Task<JsonElement> SetSettingAsync(
        string key,
        object value,
        bool confirmed = false)
        => SendRequestAsync(
            "set_setting",
            new Dictionary<string, object?>
            {
                ["setting"] = key,
                ["value"] = value,
                ["confirmed"] = confirmed
            },
            TimeSpan.FromSeconds(12));

    public async Task RequestExitAsync()
    {
        if (!IsConnected)
            return;
        try
        {
            await SendRequestAsync("exit", null, TimeSpan.FromSeconds(3));
        }
        catch
        {
            // The TV may close the application before its acknowledgement arrives.
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _ = HandleClientAsync(client);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
                !string.Equals(remote.Address.ToString(), _allowedHost, StringComparison.OrdinalIgnoreCase))
                return;

            NetworkStream stream = client.GetStream();
            WebSocket? websocket = null;
            try
            {
                string headers = await ReadHttpHeadersAsync(stream, _shutdown.Token);
                string[] lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0 || !lines[0].StartsWith("GET /m70b ", StringComparison.Ordinal))
                    return;

                Dictionary<string, string> values = lines.Skip(1)
                    .Select(line => line.Split(':', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(
                        parts => parts[0].Trim(),
                        parts => parts[1].Trim(),
                        StringComparer.OrdinalIgnoreCase);
                if (!values.TryGetValue("Sec-WebSocket-Key", out string? key))
                    return;

                string accept = Convert.ToBase64String(SHA1.HashData(
                    Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                string response =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _shutdown.Token);

                websocket = WebSocket.CreateFromStream(
                    stream,
                    isServer: true,
                    subProtocol: null,
                    keepAliveInterval: TimeSpan.FromSeconds(20));

                WebSocket? previous;
                lock (_socketLock)
                {
                    previous = _socket;
                    _socket = websocket;
                }
                if (previous is not null && previous != websocket)
                {
                    try { previous.Abort(); } catch { }
                    previous.Dispose();
                }

                await ReceiveLoopAsync(websocket, _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                // A disconnect is reflected through ConnectionChanged below.
            }
            finally
            {
                bool wasActive;
                lock (_socketLock)
                {
                    wasActive = websocket is not null && ReferenceEquals(_socket, websocket);
                    if (wasActive)
                        _socket = null;
                }
                websocket?.Dispose();
                if (wasActive)
                {
                    SetConnected(false);
                    FailPending(new IOException("电视亮度桥接器已断开。"));
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket websocket, CancellationToken cancellationToken)
    {
        while (websocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            string? text = await ReceiveBridgeTextAsync(websocket, cancellationToken);
            if (text is null)
                return;

            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            string? operation = root.TryGetProperty("op", out JsonElement op) ? op.GetString() : null;
            string? id = root.TryGetProperty("id", out JsonElement idElement) &&
                         idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;

            if (operation is "state" or "ack")
            {
                if (root.TryGetProperty("value", out JsonElement valueElement) &&
                    valueElement.TryGetInt32(out int value))
                {
                    value = Math.Clamp(value, 0, 50);
                    Volatile.Write(ref _currentBrightness, value);
                    SetConnected(true);
                }
            }

            if (operation == "error" && id is not null &&
                _pending.TryRemove(id, out TaskCompletionSource<JsonElement>? failedCompletion))
            {
                string message = root.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString() ?? "电视返回未知错误。"
                    : "电视返回未知错误。";
                failedCompletion.TrySetException(new InvalidOperationException(message));
            }
            else if (id is not null &&
                     _pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion))
            {
                completion.TrySetResult(root.Clone());
            }
        }
    }

    private async Task<JsonElement> SendRequestAsync(
        string operation,
        IReadOnlyDictionary<string, object?>? payload,
        TimeSpan timeout)
    {
        WebSocket websocket;
        lock (_socketLock)
        {
            websocket = _socket is { State: WebSocketState.Open }
                ? _socket
                : throw new InvalidOperationException("电视亮度桥接器尚未连接。");
        }

        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("无法创建显示器命令。请重试。");

        var message = new Dictionary<string, object?>
        {
            ["op"] = operation,
            ["id"] = id
        };
        if (payload is not null)
        {
            foreach ((string key, object? value) in payload)
                message[key] = value;
        }
        string json = JsonSerializer.Serialize(message);
        byte[] data = Encoding.UTF8.GetBytes(json);

        try
        {
            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            commandTimeout.CancelAfter(timeout);

            await _sendGate.WaitAsync(commandTimeout.Token);
            try
            {
                await websocket.SendAsync(data, WebSocketMessageType.Text, true, commandTimeout.Token);
            }
            finally
            {
                _sendGate.Release();
            }
            return await completion.Task.WaitAsync(commandTimeout.Token);
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            // A stalled send is not a usable connection.  Abort it so the TV's
            // reconnect watchdog can establish a fresh socket automatically.
            lock (_socketLock)
            {
                if (ReferenceEquals(_socket, websocket))
                    websocket.Abort();
            }
            throw new TimeoutException("电视在规定时间内没有响应，正在自动重新连接。");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void SetConnected(bool connected)
    {
        int next = connected ? 1 : 0;
        if (Interlocked.Exchange(ref _connected, next) != next)
            ConnectionChanged?.Invoke(connected);
    }

    private static int ReadIntegerValue(JsonElement response, int minimum, int maximum)
    {
        if (!response.TryGetProperty("value", out JsonElement valueElement) ||
            !valueElement.TryGetInt32(out int value))
            throw new InvalidDataException("电视返回的数值响应无效。");
        return Math.Clamp(value, minimum, maximum);
    }

    private void FailPending(Exception exception)
    {
        foreach ((string id, TaskCompletionSource<JsonElement> completion) in _pending)
        {
            if (_pending.TryRemove(id, out _))
                completion.TrySetException(exception);
        }
    }

    private static async Task<string> ReadHttpHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var buffer = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
                throw new IOException("连接在 WebSocket 握手前关闭。");
            bytes.Add(buffer[0]);
            int n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' &&
                bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("WebSocket 请求头过大。");
    }

    private static async Task<string?> ReceiveBridgeTextAsync(
        WebSocket websocket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await websocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        WebSocket? websocket;
        lock (_socketLock)
        {
            websocket = _socket;
            _socket = null;
        }
        websocket?.Abort();
        websocket?.Dispose();
        FailPending(new OperationCanceledException("亮度桥接服务已关闭。"));
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch (OperationCanceledException) { }
        }
        _sendGate.Dispose();
        _shutdown.Dispose();
    }
}

internal sealed class MonitorConnectivity : IAsyncDisposable
{
    private readonly BrightnessBridgeServer _bridge;
    private bool? _lastResult;

    public MonitorConnectivity(BrightnessBridgeServer bridge)
    {
        _bridge = bridge;
        _bridge.ConnectionChanged += Bridge_ConnectionChanged;
    }

    public event Action<bool>? StatusChanged;

    public void Start()
    {
        PublishIfChanged(_bridge.IsConnected);
    }

    private void Bridge_ConnectionChanged(bool connected) => PublishIfChanged(connected);

    private void PublishIfChanged(bool connected)
    {
        if (_lastResult == connected)
            return;
        _lastResult = connected;
        StatusChanged?.Invoke(connected);
    }

    public async ValueTask DisposeAsync()
    {
        _bridge.ConnectionChanged -= Bridge_ConnectionChanged;
        await Task.CompletedTask;
    }
}

internal sealed class HdmiMonitorPresence : IDisposable
{
    private System.Threading.Timer? _timer;
    private int _checking;
    private bool? _lastResult;

    public event Action<bool>? StatusChanged;

    public void Start()
    {
        _timer ??= new System.Threading.Timer(
            _ => CheckNow(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2));
    }

    private void CheckNow()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0)
            return;

        try
        {
            bool connected = IsSamsungMonitorActive();
            if (_lastResult == connected)
                return;
            _lastResult = connected;
            StatusChanged?.Invoke(connected);
        }
        catch (ManagementException)
        {
            // Keep the previous state if Windows monitor instrumentation is busy.
        }
        catch (COMException)
        {
            // A display topology change can briefly invalidate the WMI query.
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private static bool IsSamsungMonitorActive()
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\wmi",
            "SELECT Active, ManufacturerName FROM WmiMonitorID WHERE Active = TRUE");
        using ManagementObjectCollection monitors = searcher.Get();
        foreach (ManagementObject monitor in monitors)
        {
            using (monitor)
            {
                string manufacturer = DecodeWmiText(monitor["ManufacturerName"] as Array);
                if (manufacturer.Equals("SAM", StringComparison.OrdinalIgnoreCase) ||
                    manufacturer.Contains("SAMSUNG", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string DecodeWmiText(Array? values)
    {
        if (values is null)
            return string.Empty;

        var text = new StringBuilder(values.Length);
        foreach (object? value in values)
        {
            char character = (char)Convert.ToUInt16(value);
            if (character == '\0')
                break;
            text.Append(character);
        }
        return text.ToString();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

internal static class LocalState
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("M70B-Brightness-v1");
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "M70BBrightness");
    private static readonly string TokenPath = Path.Combine(DirectoryPath, "token.dat");
    private static readonly string BrightnessPath = Path.Combine(DirectoryPath, "brightness.txt");
    private static readonly string HostPath = Path.Combine(DirectoryPath, "host.txt");

    public static string? TryLoadHost()
    {
        if (!File.Exists(HostPath))
            return null;
        string host = File.ReadAllText(HostPath).Trim();
        return host.Length > 0 ? host : null;
    }

    public static void SaveHost(string host)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(HostPath, host.Trim());
    }

    public static string? TryLoadToken()
    {
        if (!File.Exists(TokenPath))
            return null;
        try
        {
            byte[] encrypted = Convert.FromBase64String(File.ReadAllText(TokenPath));
            byte[] plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveToken(string token)
    {
        Directory.CreateDirectory(DirectoryPath);
        byte[] plaintext = Encoding.UTF8.GetBytes(token);
        byte[] encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllText(TokenPath, Convert.ToBase64String(encrypted));
    }

    public static int LoadBrightness()
    {
        if (File.Exists(BrightnessPath) &&
            int.TryParse(File.ReadAllText(BrightnessPath), out int value) &&
            value is >= 0 and <= 50)
            return value;

        // The last verified brightness before this version was built.
        return 15;
    }

    public static void SaveBrightness(int value)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(BrightnessPath, Math.Clamp(value, 0, 50).ToString());
    }
}
