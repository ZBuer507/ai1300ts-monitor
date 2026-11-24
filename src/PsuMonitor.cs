using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle(MonitorProduct.AssemblyTitle)]
[assembly: AssemblyDescription(MonitorProduct.Description)]
#if GPU_LEVEL
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: AssemblyFileVersion("1.1.2.0")]
#else
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: AssemblyFileVersion("1.1.2.0")]
#endif

internal static class MonitorProduct
{
#if GPU_LEVEL
    internal const string Key = "Ai1300TS-GpuMonitor";
    internal const string Title = "电源与显卡监控";
    internal const string AssemblyTitle = "PSU and GPU Level Monitor";
    internal const string Description = "Ai1300TS power supply and ROG Astral RTX 5090 angle monitoring";
#else
    internal const string Key = "Ai1300TS-PsuMonitor";
    internal const string Title = "Ai1300TS 电源监控";
    internal const string AssemblyTitle = "Ai1300TS PSU Monitor";
    internal const string Description = "Standalone USB HID monitor for the MSI MPG Ai1300TS PCIE5";
#endif
}

internal static class Program
{
#if GPU_LEVEL
    private const string InstanceName = @"Local\Ai1300TS.GpuMonitor.Instance";
    private const string OpenWindowEvent = @"Local\Ai1300TS.GpuMonitor.OpenWindow";
#else
    private const string InstanceName = @"Local\Ai1300TS.PsuMonitor.Instance";
    private const string OpenWindowEvent = @"Local\Ai1300TS.PsuMonitor.OpenWindow";
#endif

    [STAThread]
    private static void Main(string[] args)
    {
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        bool replacing = args.Contains("--replace-instance", StringComparer.OrdinalIgnoreCase);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            using (var instance = new Mutex(false, InstanceName))
            using (var openWindow = new EventWaitHandle(false, EventResetMode.AutoReset, OpenWindowEvent))
            {
                bool ownsInstance;
                try
                {
                    ownsInstance = instance.WaitOne(replacing ? 12000 : 0);
                }
                catch (AbandonedMutexException)
                {
                    ownsInstance = true;
                }
                if (!ownsInstance)
                {
                    if (!background)
                        openWindow.Set();
                    return;
                }

                try
                {
                    RunMonitor(openWindow, background);
                }
                finally
                {
                    instance.ReleaseMutex();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (!background)
                MessageBox.Show("电源监控已由其他权限级别的进程打开。请从托盘图标打开已有窗口。",
                    MonitorProduct.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static void RunMonitor(EventWaitHandle openWindow, bool background)
    {
        using (var window = new MonitorWindow())
        using (var context = new ApplicationContext())
        {
            // WinForms automatically shows ApplicationContext.MainForm when its loop starts.
            window.FormClosed += (sender, args) => context.ExitThread();
            if (!background)
                window.OpenWindow();
            window.StartPolling();
            RegisteredWaitHandle openRequest = ThreadPool.RegisterWaitForSingleObject(openWindow,
                (state, timedOut) => window.QueueOpenWindow(), null, Timeout.Infinite, false);
            try
            {
                Application.Run(context);
            }
            finally
            {
                openRequest.Unregister(null);
            }
        }
    }
}

internal sealed partial class MonitorWindow : Form
{
    private static readonly Color Background = Color.FromArgb(15, 20, 29);
    private static readonly Color Surface = Color.FromArgb(24, 32, 45);
    private static readonly Color Foreground = Color.FromArgb(238, 244, 248);
    private static readonly Color Secondary = Color.FromArgb(163, 179, 196);
    private static readonly Color Accent = Color.FromArgb(101, 231, 194);
    private static readonly Color Warning = Color.FromArgb(255, 201, 117);
    private static readonly Color Alert = Color.FromArgb(255, 149, 142);
    private readonly Font uiFont = new Font("Microsoft YaHei UI", 9F);
    private readonly Font smallFont = new Font("Microsoft YaHei UI", 8.5F);
    private readonly Font sectionFont = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
    private readonly Font titleFont = new Font("Segoe UI", 22F, FontStyle.Bold);
    private readonly Font metricFont = new Font("Segoe UI", 18F, FontStyle.Bold);
    private readonly Font readingFont = new Font("Segoe UI", 11F, FontStyle.Bold);
    private readonly float scale = GetDpiForSystem() / 96F;
    private readonly Timer timer = new Timer();
    private readonly Label connection;
    private readonly Label timestamp;
    private readonly Label detail;
    private readonly Button retry;
    private readonly Button elevate;
    private readonly CheckBox startup;
    private readonly NotifyIcon tray;
    private readonly Icon trayIcon;
    private readonly ContextMenuStrip trayMenu;
    private readonly ToolStripMenuItem trayStartup;
    private readonly PsuTrayHover trayHover;
    private readonly Label[] metrics = new Label[5];
    private readonly Label[,] rails = new Label[3, 2];
    private readonly Label[,] pins = new Label[2, 6];
    private readonly Label[] connectorStatus = new Label[2];
#if GPU_LEVEL
    private PsuTelemetry supplyLatest;
    private DateTime? supplyUpdatedAt;
#else
    private string psuTraySummary = "正在连接电源…";
#endif
    private PsuHidTransport transport;
    private bool polling;
    private bool closing;
    private bool exitRequested;
    private bool awaitingElevation;

    public MonitorWindow()
    {
        Text = MonitorProduct.Title;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
#if GPU_LEVEL
        ClientSize = new Size(Pixels(790), Pixels(690));
        MinimumSize = new Size(Pixels(740), Pixels(710));
        const int footerRow = 5;
#else
        ClientSize = new Size(Pixels(790), Pixels(560));
        MinimumSize = new Size(Pixels(740), Pixels(580));
        const int footerRow = 4;
#endif
        BackColor = Background;
        ForeColor = Foreground;
        Font = uiFont;

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Pixels(22), Pixels(15), Pixels(22), Pixels(13)),
            ColumnCount = 1,
            RowCount = footerRow + 1
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(84)));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(91)));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(140)));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(158)));
#if GPU_LEVEL
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(130)));
#endif
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(body);

        var header = Grid(2, 2, false);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(45)));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(25)));
        header.Controls.Add(LabelFor("MPG Ai1300TS PCIE5", titleFont, Foreground), 0, 0);
        header.Controls.Add(LabelFor("USB HID  ·  电源实时监控", uiFont, Secondary), 0, 1);
        connection = LabelFor("正在连接", sectionFont, Warning, ContentAlignment.MiddleRight);
        timestamp = LabelFor("等待首个样本", smallFont, Secondary, ContentAlignment.MiddleRight);
        header.Controls.Add(connection, 1, 0);
        header.Controls.Add(timestamp, 1, 1);
        body.Controls.Add(header, 0, 0);

        var cards = Grid(5, 1, false);
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        string[] metricNames = { "输出功率", "估算输入功率", "转换效率", "电源温度", "风扇转速" };
        for (int index = 0; index < metrics.Length; index++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            var card = Grid(1, 2, true);
            card.Margin = new Padding(0, 0, index == metrics.Length - 1 ? 0 : Pixels(8), Pixels(12));
            card.Padding = new Padding(Pixels(12), Pixels(5), Pixels(10), Pixels(5));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(24)));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(LabelFor(metricNames[index], smallFont, Secondary), 0, 0);
            metrics[index] = LabelFor("—", metricFont, index == 0 ? Accent : Foreground);
            card.Controls.Add(metrics[index], 0, 1);
            cards.Controls.Add(card, index, 0);
        }
        body.Controls.Add(cards, 0, 1);

        var railTable = Section(3, 5);
        railTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        railTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
        railTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
        AddSectionTitle(railTable, "直流输出", 3);
        railTable.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(20)));
        railTable.Controls.Add(LabelFor("电压", smallFont, Secondary), 1, 1);
        railTable.Controls.Add(LabelFor("电流", smallFont, Secondary), 2, 1);
        string[] railNames = { "+12 V", "+5 V", "+3.3 V" };
        for (int row = 0; row < railNames.Length; row++)
        {
            railTable.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(21)));
            railTable.Controls.Add(LabelFor(railNames[row], readingFont, Foreground), 0, row + 2);
            for (int column = 0; column < 2; column++)
            {
                rails[row, column] = LabelFor("—", readingFont, Foreground);
                railTable.Controls.Add(rails[row, column], column + 1, row + 2);
            }
        }
        body.Controls.Add(railTable, 0, 2);

        var pinTable = Section(7, 4);
        pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Pixels(122)));
        for (int number = 1; number <= 6; number++)
        {
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 6F));
            pinTable.Controls.Add(LabelFor("Pin " + number, smallFont, Secondary), number, 1);
        }
        AddSectionTitle(pinTable, "12V-2x6 逐针电流", 7);
        pinTable.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(22)));
        for (int row = 0; row < 2; row++)
        {
            pinTable.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(39)));
            var port = Grid(1, 2, false);
            port.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            port.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(21)));
            port.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            port.Controls.Add(LabelFor("接口 " + (row + 1), uiFont, Foreground), 0, 0);
            connectorStatus[row] = LabelFor("—", smallFont, Secondary);
            port.Controls.Add(connectorStatus[row], 0, 1);
            pinTable.Controls.Add(port, 0, row + 2);
            for (int column = 0; column < 6; column++)
            {
                pins[row, column] = LabelFor("—", readingFont, Foreground);
                pinTable.Controls.Add(pins[row, column], column + 1, row + 2);
            }
        }
        body.Controls.Add(pinTable, 0, 3);
#if GPU_LEVEL
        body.Controls.Add(CreateGpuPanel(), 0, 4);
#endif

        var footer = Grid(4, 1, false);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Pixels(157)));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Pixels(123)));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Pixels(76)));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detail = LabelFor("正在打开电源的 USB HID 接口…", smallFont, Secondary);
        detail.AutoEllipsis = true;
        footer.Controls.Add(detail, 0, 0);
        startup = new CheckBox
        {
            Anchor = AnchorStyles.Left,
            AutoCheck = false,
            AutoSize = true,
            ForeColor = Foreground,
            Font = smallFont,
            Text = "登录 Windows 时启动"
        };
        startup.Click += (sender, args) => ToggleStartup();
        footer.Controls.Add(startup, 1, 0);
        elevate = ActionButton("以管理员身份重开");
        elevate.Visible = false;
        elevate.Click += ElevateClick;
        footer.Controls.Add(elevate, 2, 0);
        retry = ActionButton("重连");
        retry.Click += RetryClick;
        footer.Controls.Add(retry, 3, 0);
        body.Controls.Add(footer, 0, footerRow);

        trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Icon = trayIcon;
        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("打开监控窗口", null, (sender, args) => OpenWindow());
        trayStartup = new ToolStripMenuItem("登录 Windows 时启动");
        trayStartup.Click += (sender, args) => ToggleStartup();
        trayMenu.Items.Add(trayStartup);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出", null, (sender, args) => RequestExit());
        trayMenu.Opening += (sender, args) => RefreshStartup();
        tray = new NotifyIcon
        {
            Icon = trayIcon ?? SystemIcons.Application,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayHover = new PsuTrayHover(tray);
        RefreshTraySummary();
        tray.MouseClick += (sender, args) =>
        {
            if (args.Button == MouseButtons.Left)
                OpenWindow();
        };
        timer.Tick += async (sender, args) => await PollAsync();
        RefreshStartup();
    }

    public void StartPolling()
    {
        // A background launch also needs a window handle for the single-instance signal.
        if (!IsHandleCreated)
            CreateHandle();
        timer.Interval = 1;
        timer.Start();
#if GPU_LEVEL
        StartGpuPolling();
#endif
    }

    public void QueueOpenWindow()
    {
        try
        {
            BeginInvoke((Action)OpenWindow);
        }
        catch (InvalidOperationException)
        {
            // The application can be exiting as a second launch arrives.
        }
    }

    public void OpenWindow()
    {
        if (exitRequested || closing)
            return;
        trayHover.Dismiss();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        if (!Visible)
            Show();
        RefreshStartup();
        Activate();
    }

    private void RequestExit()
    {
        exitRequested = true;
        tray.Visible = false;
        trayHover.Dismiss();
        Close();
    }

    private void RefreshStartup()
    {
        bool enabled = PsuStartup.IsEnabled();
        bool blocked = !enabled && PsuStartup.HasRegistration() && PsuStartup.IsBlockedByWindows();
        startup.Checked = enabled;
        trayStartup.Checked = enabled;
        startup.Text = blocked ? "登录启动（系统已禁用）" : "登录 Windows 时启动";
        trayStartup.Text = startup.Text;
    }

    private void ToggleStartup()
    {
        trayHover.Dismiss();
        try
        {
            bool enable = !PsuStartup.IsEnabled();
            PsuStartup.SetEnabled(enable);
            if (enable && !PsuStartup.IsEnabled())
                MessageBox.Show("请在 Windows「设置 → 应用 → 启动」中启用 " + MonitorProduct.AssemblyTitle + "。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            if (!(error is UnauthorizedAccessException || error is IOException ||
                  error is System.Security.SecurityException || error is InvalidOperationException))
                throw;
            MessageBox.Show("无法更新登录启动设置。\n" + error.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            RefreshStartup();
        }
    }

    private static TableLayoutPanel Grid(int columns, int rows, bool surface)
    {
        return new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ColumnCount = columns,
            RowCount = rows,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = surface ? Surface : Color.Transparent
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private int Pixels(int logicalPixels)
    {
        return (int)Math.Round(logicalPixels * scale);
    }

    private TableLayoutPanel Section(int columns, int rows)
    {
        var section = Grid(columns, rows, true);
        section.Margin = new Padding(0, 0, 0, Pixels(12));
        section.Padding = new Padding(Pixels(13), Pixels(6), Pixels(12), Pixels(5));
        return section;
    }

    private void AddSectionTitle(TableLayoutPanel section, string title, int span)
    {
        var label = LabelFor(title, sectionFont, Foreground);
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(28)));
        section.Controls.Add(label, 0, 0);
        section.SetColumnSpan(label, span);
    }

    private static Label LabelFor(string text, Font font, Color color,
        ContentAlignment alignment = ContentAlignment.MiddleLeft)
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Text = text,
            Font = font,
            ForeColor = color,
            TextAlign = alignment,
            UseMnemonic = false
        };
    }

    private Button ActionButton(string text)
    {
        var button = new Button
        {
            Anchor = AnchorStyles.Right,
            Width = Pixels(text.Length > 3 ? 115 : 69),
            Height = Pixels(31),
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Text = text,
            Font = smallFont,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Foreground,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(66, 82, 98);
        return button;
    }

    private async void RetryClick(object sender, EventArgs args)
    {
        if (polling || closing)
            return;
        ResetTransport();
        connection.Text = "正在重连";
        connection.ForeColor = Warning;
        await PollAsync();
    }

    private void ElevateClick(object sender, EventArgs args)
    {
        awaitingElevation = true;
        timer.Stop();
        try
        {
            var command = new ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--replace-instance",
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            using (Process.Start(command))
            {
                RequestExit();
            }
        }
        catch (Win32Exception error)
        {
            if (error.NativeErrorCode != 1223)
                detail.Text = "无法以管理员身份启动。Windows 错误 " + error.NativeErrorCode + "。";
        }
        finally
        {
            awaitingElevation = false;
            if (!closing)
                timer.Start();
        }
    }

    private async Task PollAsync()
    {
        if (polling || closing || awaitingElevation)
            return;
        timer.Stop();
        polling = true;
        retry.Enabled = false;
        try
        {
            var sample = await Task.Run((Func<PsuTelemetry>)ReadSample);
            if (!closing)
                ShowSample(sample);
            timer.Interval = 1000;
        }
        catch (Exception error)
        {
            ResetTransport();
            if (!closing)
                ShowProblem(error);
            timer.Interval = 2500;
        }
        finally
        {
            polling = false;
            if (closing)
            {
                Close();
            }
            else
            {
                retry.Enabled = true;
                if (!awaitingElevation)
                    timer.Start();
            }
        }
    }

    private PsuTelemetry ReadSample()
    {
        if (transport == null)
            transport = new PsuHidTransport();
        var liveReply = transport.ReadRegister(PsuTelemetry.TelemetryRegister);
        byte[] statusReply = null;
        try
        {
            statusReply = transport.ReadRegister(PsuTelemetry.ConnectorStatusRegister);
        }
        catch (Exception error)
        {
            if (!(error is TimeoutException || error is IOException ||
                  error is Win32Exception || error is UnauthorizedAccessException))
                throw;
            // The validated telemetry remains usable when the separate status read fails.
        }
        var sample = PsuTelemetry.Decode(liveReply, statusReply);
        if (sample == null)
            throw new InvalidOperationException("电源返回了无效的实时数据。");
        return sample;
    }

    private void ResetTransport()
    {
        if (transport != null)
        {
            transport.Dispose();
            transport = null;
        }
    }

    private static string Reading(double? value, string format, string unit)
    {
        if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value))
            return "—";
        return value.Value.ToString(format, CultureInfo.InvariantCulture) + " " + unit;
    }

    private void ShowSample(PsuTelemetry sample)
    {
        metrics[0].Text = Reading(sample.OutputWatts, "0", "W");
        metrics[1].Text = Reading(sample.InputWatts, "0", "W");
        metrics[2].Text = Reading(sample.EfficiencyPercent, "0.0", "%");
        metrics[3].Text = Reading(sample.TemperatureCelsius, "0.0", "°C");
        metrics[4].Text = Reading(sample.FanRpm, "0", "RPM");
        double[] voltage = { sample.Voltage12V, sample.Voltage5V, sample.Voltage3V3 };
        double[] current = { sample.Current12V, sample.Current5V, sample.Current3V3 };
        for (int row = 0; row < 3; row++)
        {
            rails[row, 0].Text = Reading(voltage[row], "0.00", "V");
            rails[row, 1].Text = Reading(current[row], "0.00", "A");
        }
        ShowConnector(0, sample.Connector1Currents, sample.Connector1Status);
        ShowConnector(1, sample.Connector2Currents, sample.Connector2Status);
        connection.Text = "已连接";
        connection.ForeColor = Accent;
        DateTime sampleTime = DateTime.Now;
        timestamp.Text = "更新于 " + sampleTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        detail.Text = "关闭窗口后继续在托盘监控";
        elevate.Visible = false;
#if GPU_LEVEL
        supplyLatest = sample;
        supplyUpdatedAt = sampleTime;
#else
        psuTraySummary = TraySummary(sample);
#endif
        RefreshTraySummary();
    }

    private void RefreshTraySummary()
    {
#if GPU_LEVEL
        trayHover.Update(new MonitorTraySnapshot
        {
            Supply = supplyLatest,
            SupplyUpdatedAt = supplyUpdatedAt,
            SupplyState = connection.Text,
            SupplyDetail = supplyLatest == null ? detail.Text : null,
            Gpu = gpuLatest,
            GpuDeviation = gpuTrayDeviation,
            GpuAboveThreshold = gpuTrayAboveThreshold,
            GpuDetail = gpuState.Text
        });
#else
        trayHover.Update(MonitorProduct.Title + "\n" + psuTraySummary);
#endif
    }

#if !GPU_LEVEL
    private static string TraySummary(PsuTelemetry sample)
    {
        return "输出 " + Reading(sample.OutputWatts, "0", "W") +
            "   ·   输入估算 " + Reading(sample.InputWatts, "0", "W") +
            "   ·   效率 " + Reading(sample.EfficiencyPercent, "0.0", "%") + "\n" +
            "温度 " + Reading(sample.TemperatureCelsius, "0.0", "°C") +
            "   ·   风扇 " + Reading(sample.FanRpm, "0", "RPM") + "\n\n" +
            "+12 V   " + Reading(sample.Voltage12V, "0.00", "V") + "    " +
                Reading(sample.Current12V, "0.00", "A") + "\n" +
            "+5 V     " + Reading(sample.Voltage5V, "0.00", "V") + "    " +
                Reading(sample.Current5V, "0.00", "A") + "\n" +
            "+3.3 V  " + Reading(sample.Voltage3V3, "0.00", "V") + "    " +
                Reading(sample.Current3V3, "0.00", "A") + "\n\n" +
            "12V-2x6 接口 1   " + ConnectorStatusText(sample.Connector1Status) + "\n" +
            PinReadings(sample.Connector1Currents) + "\n" +
            "12V-2x6 接口 2   " + ConnectorStatusText(sample.Connector2Status) + "\n" +
            PinReadings(sample.Connector2Currents) + "\n\n" +
            "更新于 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string PinReadings(double[] values)
    {
        return String.Join(" / ", values.Select(value => value.ToString("0.00", CultureInfo.InvariantCulture))) + " A";
    }
#endif

    private static string ConnectorStatusText(byte? status)
    {
        return status == null ? "状态不可用" :
            status == 0 ? "电源未报告告警" : "告警状态 0x" + status.Value.ToString("X2");
    }

    private void ShowConnector(int index, double[] values, byte? status)
    {
        for (int pin = 0; pin < 6; pin++)
            pins[index, pin].Text = Reading(values[pin], "0.00", "A");
        connectorStatus[index].Text = ConnectorStatusText(status);
        connectorStatus[index].ForeColor = status.GetValueOrDefault() == 0 ? Secondary : Alert;
    }

    private void ShowProblem(Exception error)
    {
        foreach (var label in metrics)
            label.Text = "—";
        foreach (var label in rails)
            label.Text = "—";
        foreach (var label in pins)
            label.Text = "—";
        foreach (var label in connectorStatus)
        {
            label.Text = "—";
            label.ForeColor = Secondary;
        }
        var nativeError = error as Win32Exception;
        bool accessDenied = error is UnauthorizedAccessException ||
            (nativeError != null && nativeError.NativeErrorCode == 5);
        connection.Text = accessDenied ? "访问被拒绝" : "暂无实时数据";
        connection.ForeColor = accessDenied ? Alert : Warning;
        timestamp.Text = "等待下一次连接";
        detail.Text = accessDenied ? "无法访问 HID 设备或 MSI 共用锁。" : error.Message;
        elevate.Visible = accessDenied;
#if GPU_LEVEL
        supplyLatest = null;
        supplyUpdatedAt = null;
#else
        psuTraySummary = "电源 " + connection.Text + "\n" + detail.Text;
#endif
        RefreshTraySummary();
    }

    protected override void OnResize(EventArgs args)
    {
        base.OnResize(args);
        if (WindowState == FormWindowState.Minimized && !exitRequested)
            Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        if (args.CloseReason == CloseReason.UserClosing && !exitRequested)
        {
            args.Cancel = true;
            Hide();
            base.OnFormClosing(args);
            return;
        }
        timer.Stop();
#if GPU_LEVEL
        gpuTimer.Stop();
#endif
        closing = true;
        if (polling && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            Enabled = false;
        }
        base.OnFormClosing(args);
    }

#if GPU_LEVEL
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Q))
        {
            RequestExit();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (trayHover != null)
                trayHover.Dispose();
            if (tray != null)
                tray.Dispose();
            if (trayMenu != null)
                trayMenu.Dispose();
            timer.Dispose();
#if GPU_LEVEL
            DisposeGpuPolling();
#endif
            if (!polling)
                ResetTransport();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            if (trayIcon != null)
                trayIcon.Dispose();
            uiFont.Dispose();
            smallFont.Dispose();
            sectionFont.Dispose();
            titleFont.Dispose();
            metricFont.Dispose();
            readingFont.Dispose();
        }
    }
}
