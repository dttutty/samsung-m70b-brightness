using System.Net.WebSockets;
using System.Net;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace M70BPopup;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        _singleInstance = new Mutex(
            true,
            @"Local\M70BBrightness.SingleInstance",
            out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "M70B 亮度调节已经在系统托盘中运行。",
                "M70B 亮度调节",
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

            string? token = LocalState.TryLoadToken();
            if (token is null)
            {
                if (MessageBox.Show(
                        "首次使用需要与显示器配对。\n\n点击“确定”后，请在 Samsung M70B 屏幕上选择“允许”。",
                        "M70B 亮度调节",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Information) != DialogResult.OK)
                    return;

                token = SamsungBrightnessSession.PairAsync(host).GetAwaiter().GetResult();
                LocalState.SaveToken(token);
            }

            Application.Run(new TrayContext(host, token));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"无法启动 M70B 亮度调节：\n{ex.Message}",
                "M70B 亮度调节",
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
            MessageBox.Show("请输入有效的 IP 地址或主机名。", "M70B 亮度调节", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return null;
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly BrightnessPopup _popup;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _appIcon;
    private bool _exiting;

    public TrayContext(string host, string token)
    {
        var session = new SamsungBrightnessSession(host, token, LocalState.LoadBrightness());
        _popup = new BrightnessPopup(session);
        _appIcon = (Icon)(Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application).Clone();

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开亮度调节", null, (_, _) => _popup.OpenNearCursor());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());

        _trayIcon = new NotifyIcon
        {
            Text = "Samsung M70B 亮度调节",
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _popup.OpenNearCursor();
        };

        _trayIcon.BalloonTipTitle = "M70B 亮度调节已启动";
        _trayIcon.BalloonTipText = "左键单击此图标即可打开亮度滑块。";
        _trayIcon.ShowBalloonTip(3000);
    }

    private async Task ExitAsync()
    {
        if (_exiting)
            return;

        _exiting = true;
        await _popup.ShutdownAsync();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
        _popup.Dispose();
        ExitThread();
    }
}

internal sealed class BrightnessPopup : Form
{
    private readonly SamsungBrightnessSession _session;
    private readonly ModernBrightnessSlider _slider = new();
    private readonly Label _valueLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _commandLabel = new();
    private readonly ModernTileButton _minimumButton = new();
    private readonly ModernTileButton _maximumButton = new();
    private readonly ModernCloseButton _closeButton = new();
    private readonly ProgressBar _progressBar = new();
    private readonly System.Windows.Forms.Timer _sliderTimer = new();
    private bool _opening;
    private bool _closing;
    private bool _applying;
    private bool _ignoreSlider;

    public BrightnessPopup(SamsungBrightnessSession session)
    {
        _session = session;

        Text = "Samsung M70B 亮度";
        ClientSize = new Size(400, 260);
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

        var subtitle = new Label
        {
            Text = "Samsung M70B  ·  局域网控制",
            ForeColor = Color.FromArgb(174, 174, 174),
            BackColor = Color.Transparent,
            Font = new Font(Font.FontFamily, 8.5F),
            AutoSize = true,
            Location = new Point(21, 45)
        };

        _valueLabel.Text = $"{_session.CurrentBrightness} / 50";
        _valueLabel.ForeColor = Color.White;
        _valueLabel.BackColor = Color.Transparent;
        _valueLabel.Font = new Font("Segoe UI Variable Display", 15F, FontStyle.Bold);
        _valueLabel.TextAlign = ContentAlignment.MiddleRight;
        _valueLabel.Location = new Point(270, 10);
        _valueLabel.Size = new Size(76, 38);

        _closeButton.Location = new Point(358, 12);
        _closeButton.Size = new Size(28, 28);
        _closeButton.Click += async (_, _) => await HideAndCloseSessionAsync();

        _slider.Minimum = 0;
        _slider.Maximum = 50;
        _slider.Value = _session.CurrentBrightness;
        _slider.Location = new Point(18, 137);
        _slider.Size = new Size(362, 46);
        _slider.Scroll += (_, _) =>
        {
            if (_ignoreSlider)
                return;
            _valueLabel.Text = $"{_slider.Value} / 50";
            _sliderTimer.Stop();
            _sliderTimer.Start();
        };
        _slider.MouseUp += async (_, _) =>
        {
            _sliderTimer.Stop();
            await ApplyPendingSliderAsync();
        };

        _minimumButton.Text = "☾   重置最小亮度";
        _minimumButton.Location = new Point(20, 76);
        _minimumButton.Size = new Size(174, 48);
        _minimumButton.Click += async (_, _) => await ResetAsync(minimum: true);

        _maximumButton.Text = "☀   重置最大亮度";
        _maximumButton.Location = new Point(206, 76);
        _maximumButton.Size = new Size(174, 48);
        _maximumButton.Click += async (_, _) => await ResetAsync(minimum: false);

        _statusLabel.Text = "点击右上角 × 关闭显示器设置";
        _statusLabel.ForeColor = Color.FromArgb(228, 228, 228);
        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.Location = new Point(20, 190);
        _statusLabel.Size = new Size(360, 22);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        _commandLabel.Text = "命令：等待操作";
        _commandLabel.ForeColor = Color.FromArgb(155, 155, 155);
        _commandLabel.BackColor = Color.Transparent;
        _commandLabel.Font = new Font("Cascadia Mono", 8.25F);
        _commandLabel.Location = new Point(20, 214);
        _commandLabel.Size = new Size(360, 21);

        _progressBar.Location = new Point(20, 244);
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

        _session.ProgressChanged += Session_ProgressChanged;

        Controls.AddRange([
            title,
            subtitle,
            _closeButton,
            _valueLabel,
            _slider,
            _minimumButton,
            _maximumButton,
            _statusLabel,
            _commandLabel,
            _progressBar
        ]);
    }

    public async void OpenNearCursor()
    {
        if (Visible)
        {
            Activate();
            return;
        }

        Screen screen = Screen.FromPoint(Cursor.Position);
        Rectangle area = screen.WorkingArea;
        Location = new Point(area.Right - Width - 4, area.Bottom - Height - 4);
        SyncSliderToSession();
        SetTransitionLayout(true);
        SetControlsEnabled(false);
        SetBusyDisplay(true);
        _statusLabel.Text = "启动中，正在给电视发送命令…";
        _commandLabel.Text = "命令：CONNECT — 建立局域网连接";
        Show();
        Activate();

        if (_opening)
            return;

        _opening = true;
        try
        {
            await _session.OpenAsync();
            if (Visible)
            {
                SetTransitionLayout(false);
                SetControlsEnabled(true);
                SetBusyDisplay(false);
                _statusLabel.Text = "拖动滑块即可调节；点击右上角 × 关闭";
                _commandLabel.Text = "命令：READY — 亮度界面已就绪";
            }
        }
        catch (Exception ex)
        {
            Hide();
            MessageBox.Show(
                $"无法打开显示器亮度界面：\n{ex.Message}",
                "M70B 亮度调节",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _opening = false;
        }
    }

    private async Task ApplyPendingSliderAsync()
    {
        if (_applying || !_session.IsOpen)
            return;

        _applying = true;
        SetControlsEnabled(false);
        SetBusyDisplay(true);
        try
        {
            while (Visible && _slider.Value != _session.CurrentBrightness)
            {
                int target = _slider.Value;
                _statusLabel.Text = $"正在调节到 {target}…";
                await _session.MoveToAsync(target);
            }
            if (Visible)
                _statusLabel.Text = "拖动滑块即可调节；点击右上角 × 关闭";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"调节失败：{ex.Message}";
            SyncSliderToSession();
        }
        finally
        {
            _applying = false;
            if (Visible)
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
                ? "命令：KEY_LEFT × 50 — 当前已记为 0"
                : "命令：KEY_RIGHT × 50 — 当前已记为 50";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"重置失败：{ex.Message}";
        }
        finally
        {
            if (Visible)
            {
                SetControlsEnabled(true);
                SetBusyDisplay(false);
            }
        }
    }

    private async Task HideAndCloseSessionAsync()
    {
        if (_closing)
            return;

        _closing = true;
        _closeButton.Enabled = false;
        _sliderTimer.Stop();
        SetTransitionLayout(true);
        SetControlsEnabled(false);
        SetBusyDisplay(true);
        _statusLabel.Text = "退出中，正在给电视发送命令…";
        _commandLabel.Text = "命令：WAIT — 等待亮度界面完成响应";
        try
        {
            await _session.CloseAsync();
        }
        finally
        {
            Hide();
            SetBusyDisplay(false);
            _closeButton.Enabled = true;
            _closing = false;
        }
    }

    public async Task ShutdownAsync()
    {
        _sliderTimer.Stop();
        Hide();
        await _session.CloseAsync();
        await _session.DisposeAsync();
    }

    private void SyncSliderToSession()
    {
        _ignoreSlider = true;
        _slider.Value = _session.CurrentBrightness;
        _valueLabel.Text = $"{_session.CurrentBrightness} / 50";
        _ignoreSlider = false;
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
        _valueLabel.Visible = !transition;
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
            _statusLabel.Location = new Point(20, 190);
            _statusLabel.Size = new Size(360, 22);
            _commandLabel.Location = new Point(20, 214);
            _commandLabel.Size = new Size(360, 21);
            _progressBar.Location = new Point(20, 244);
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

        const int iconWidth = 30;
        const int rightPadding = 10;
        int railLeft = iconWidth;
        int railRight = Width - rightPadding;
        int railWidth = Math.Max(1, railRight - railLeft);
        int centerY = Height / 2;
        float ratio = (float)(_value - _minimum) / (_maximum - _minimum);
        int thumbX = railLeft + (int)Math.Round(railWidth * ratio);

        using var iconFont = new Font("Segoe UI Symbol", 12F);
        using var iconBrush = new SolidBrush(Enabled
            ? Color.FromArgb(224, 224, 224)
            : Color.FromArgb(110, 110, 110));
        g.DrawString("☀", iconFont, iconBrush, new PointF(2, centerY - 10));

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
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left)
            return;
        _dragging = true;
        Capture = true;
        UpdateFromMouse(e.X);
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
        }
        base.OnMouseUp(e);
    }

    private void UpdateFromMouse(int x)
    {
        const int railLeft = 30;
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

internal sealed class ModernTileButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public ModernTileButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.White;
        BackColor = Color.Transparent;
        Font = new Font("Microsoft YaHei UI", 9F);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = !Enabled
            ? Color.FromArgb(49, 49, 49)
            : _pressed
                ? Color.FromArgb(75, 75, 75)
                : _hovered
                    ? Color.FromArgb(67, 67, 67)
                    : Color.FromArgb(57, 57, 57);

        using GraphicsPath path = ModernDrawing.RoundedRectangle(
            new Rectangle(0, 0, Width - 1, Height - 1),
            6);
        using var fillBrush = new SolidBrush(fill);
        using var borderPen = new Pen(Color.FromArgb(78, 78, 78));
        e.Graphics.FillPath(fillBrush, path);
        e.Graphics.DrawPath(borderPen, path);

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            Enabled ? ForeColor : Color.FromArgb(130, 130, 130),
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine);
    }
}

internal sealed class ModernCloseButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public ModernCloseButton()
    {
        Text = "×";
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.FromArgb(225, 225, 225);
        BackColor = Color.Transparent;
        Font = new Font("Segoe UI", 13F, FontStyle.Regular);
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hovered || _pressed)
        {
            Color fill = _pressed
                ? Color.FromArgb(82, 82, 82)
                : Color.FromArgb(66, 66, 66);
            using GraphicsPath path = ModernDrawing.RoundedRectangle(
                new Rectangle(0, 0, Width - 1, Height - 1),
                5);
            using var brush = new SolidBrush(fill);
            e.Graphics.FillPath(brush, path);
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            Enabled ? ForeColor : Color.FromArgb(115, 115, 115),
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine);
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

internal sealed class SamsungBrightnessSession : IAsyncDisposable
{
    private const string AppName = "Codex M70B Control";
    private readonly string _host;
    private readonly string _token;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientWebSocket? _socket;

    public SamsungBrightnessSession(string host, string token, int currentBrightness)
    {
        _host = host;
        _token = token;
        CurrentBrightness = Math.Clamp(currentBrightness, 0, 50);
    }

    public int CurrentBrightness { get; private set; }
    public bool IsOpen => _socket?.State == WebSocketState.Open;
    public event Action<string, string>? ProgressChanged;

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

    public async Task OpenAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsOpen)
                return;

            _socket?.Dispose();
            _socket = CreateSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Report("启动中，正在给电视发送命令…", "CONNECT — 建立局域网连接");
            await _socket.ConnectAsync(CreateUri(_host, _token), timeout.Token);
            await RequireConnectedAsync(_socket, timeout.Token);

            Report("启动中，正在给电视发送命令…", "KEY_MENU — 打开设置");
            await ClickAsync(_socket, "KEY_MENU", 650, timeout.Token);
            Report("启动中，正在给电视发送命令…", "KEY_ENTER — 进入 Picture");
            await ClickAsync(_socket, "KEY_ENTER", 450, timeout.Token);
            for (int i = 0; i < 3; i++)
            {
                Report("启动中，正在给电视发送命令…", $"KEY_DOWN {i + 1}/3 — 定位 Expert Settings");
                await ClickAsync(_socket, "KEY_DOWN", 90, timeout.Token);
            }
            Report("启动中，正在给电视发送命令…", "KEY_ENTER — 进入 Expert Settings");
            await ClickAsync(_socket, "KEY_ENTER", 650, timeout.Token);
            Report("启动中，正在给电视发送命令…", "KEY_ENTER — 进入 Brightness 调节");
            await ClickAsync(_socket, "KEY_ENTER", 500, timeout.Token);
        }
        catch
        {
            AbortSocket();
            throw;
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
            ClientWebSocket socket = RequireOpenSocket();
            string key = target > CurrentBrightness ? "KEY_RIGHT" : "KEY_LEFT";
            int total = Math.Abs(target - CurrentBrightness);
            int sent = 0;
            while (CurrentBrightness != target)
            {
                sent++;
                Report("调节中，正在给电视发送命令…", $"{key} {sent}/{total} — 目标 {target}");
                await ClickAsync(socket, key, 70, CancellationToken.None);
                CurrentBrightness += key == "KEY_RIGHT" ? 1 : -1;
            }
            LocalState.SaveBrightness(CurrentBrightness);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetMinimumAsync()
    {
        await ResetAsync("KEY_LEFT", 0);
    }

    public async Task ResetMaximumAsync()
    {
        await ResetAsync("KEY_RIGHT", 50);
    }

    private async Task ResetAsync(string key, int resultingBrightness)
    {
        await _gate.WaitAsync();
        try
        {
            ClientWebSocket socket = RequireOpenSocket();
            for (int i = 0; i < 50; i++)
            {
                Report(
                    "重置中，正在给电视发送命令…",
                    $"{key} {i + 1}/50 — 重置为 {resultingBrightness}");
                await ClickAsync(socket, key, 45, CancellationToken.None);
            }
            CurrentBrightness = resultingBrightness;
            LocalState.SaveBrightness(CurrentBrightness);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsOpen || _socket is null)
            {
                AbortSocket();
                return;
            }

            Report("退出中，正在给电视发送命令…", "WAIT — 等待亮度界面完成响应");
            await Task.Delay(1500);
            for (int i = 0; i < 4; i++)
            {
                Report("退出中，正在给电视发送命令…", $"KEY_RETURN {i + 1}/4 — 逐层关闭设置");
                await ClickAsync(_socket, "KEY_RETURN", 1000, CancellationToken.None);
            }

            try
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "done",
                    CancellationToken.None);
            }
            catch
            {
                _socket.Abort();
            }
            _socket.Dispose();
            _socket = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
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

    private static async Task RequireConnectedAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        string text = await ReceiveTextAsync(socket, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(text);
        string? eventName = document.RootElement.TryGetProperty("event", out JsonElement eventElement)
            ? eventElement.GetString()
            : null;
        if (eventName != "ms.channel.connect")
            throw new InvalidOperationException($"显示器拒绝连接：{eventName ?? "未知响应"}");
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
