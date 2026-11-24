using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

internal sealed class MonitorTraySnapshot
{
    public PsuTelemetry Supply;
    public DateTime? SupplyUpdatedAt;
    public string SupplyState;
    public string SupplyDetail;
    public GpuLevelReading Gpu;
    public double? GpuDeviation;
    public bool GpuAboveThreshold;
    public string GpuDetail;
}

internal sealed class MonitorTrayPopup : Form
{
    private const int CardWidth = 432;
    private const int SectionWidth = 408;
    private static readonly Color Background = Color.FromArgb(14, 21, 31);
    private static readonly Color Surface = Color.FromArgb(23, 33, 46);
    private static readonly Color Line = Color.FromArgb(47, 63, 81);
    private static readonly Color Foreground = Color.FromArgb(239, 245, 251);
    private static readonly Color Muted = Color.FromArgb(169, 184, 201);
    private static readonly Color Faint = Color.FromArgb(127, 148, 171);
    private static readonly Color Mint = Color.FromArgb(115, 235, 192);
    private static readonly Color Lavender = Color.FromArgb(185, 189, 255);
    private static readonly Color Amber = Color.FromArgb(255, 193, 119);
    private static readonly Color HeroStart = Color.FromArgb(23, 49, 51);
    private static readonly Color HeroEnd = Color.FromArgb(25, 36, 51);
    private static readonly Color GlyphBackground = Color.FromArgb(30, 64, 61);
    private static readonly Color BadgeLive = Color.FromArgb(26, 55, 50);
    private static readonly Color BadgeWaiting = Color.FromArgb(49, 44, 43);
    private static readonly PointF[] Bolt =
    {
        new PointF(32, 19), new PointF(24, 30), new PointF(29, 30),
        new PointF(27, 38), new PointF(36, 26), new PointF(31, 26)
    };

    private readonly float systemScale;
    private readonly Font titleFont = new Font("Microsoft YaHei UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font sectionFont = new Font("Microsoft YaHei UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font labelFont = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font tinyFont = new Font("Microsoft YaHei UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font readingFont = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font metricFont = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font heroFont = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly StringFormat left = Alignment(StringAlignment.Near);
    private readonly StringFormat right = Alignment(StringAlignment.Far);
    private readonly StringFormat center = Alignment(StringAlignment.Center);
    private MonitorTraySnapshot snapshot;
    private float renderScale;

    internal MonitorTrayPopup(float systemScale)
    {
        this.systemScale = systemScale;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Background;
        TopMost = true;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            const int toolWindow = 0x80;
            const int noActivate = 0x08000000;
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= toolWindow | noActivate;
            return parameters;
        }
    }

    internal void Present(MonitorTraySnapshot next, Point anchor)
    {
        SetContent(next, anchor, Screen.FromPoint(anchor).WorkingArea);
        if (!Visible)
            Show();
    }

    internal void SetContent(MonitorTraySnapshot next, Point anchor, Rectangle area)
    {
        snapshot = next;
        int height = CardHeight;
        int gap = Math.Max(2, (int)Math.Round(6 * systemScale));
        renderScale = Math.Max(0.05F, Math.Min(systemScale, Math.Min(
            (area.Width - 2F * gap) / CardWidth, (area.Height - 2F * gap) / height)));
        var size = new Size((int)Math.Floor(CardWidth * renderScale), (int)Math.Floor(height * renderScale));
        bool sizeChanged = Size != size;
        Bounds = Place(anchor, area, size, gap);
        if (sizeChanged || Region == null)
        {
            using (var border = Rounded(new RectangleF(0, 0, size.Width, size.Height), 14 * renderScale))
                Region = new Region(border);
        }
        Invalidate();
    }

    private int CardHeight { get { return snapshot.Supply == null ? 237 : 424; } }

    internal static Rectangle Place(Point anchor, Rectangle area, Size size, int gap)
    {
        int x = Math.Max(area.Left + gap, Math.Min(anchor.X - size.Width + 26, area.Right - size.Width - gap));
        int y = Math.Max(area.Top + gap, Math.Min(anchor.Y - size.Height / 2, area.Bottom - size.Height - gap));
        if (anchor.Y - gap - size.Height >= area.Top + gap)
            return new Rectangle(x, Math.Min(anchor.Y - gap - size.Height, area.Bottom - size.Height - gap), size.Width, size.Height);
        if (anchor.Y + gap + size.Height <= area.Bottom - gap)
            return new Rectangle(x, Math.Max(anchor.Y + gap, area.Top + gap), size.Width, size.Height);
        if (anchor.X - gap - size.Width >= area.Left + gap)
            return new Rectangle(Math.Min(anchor.X - gap - size.Width, area.Right - size.Width - gap), y, size.Width, size.Height);
        if (anchor.X + gap + size.Width <= area.Right - gap)
            return new Rectangle(Math.Max(anchor.X + gap, area.Left + gap), y, size.Width, size.Height);

        return new Rectangle(x, y, size.Width, size.Height);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        if (snapshot == null)
            return;

        Graphics g = args.Graphics;
        GraphicsState saved = g.Save();
        g.ScaleTransform(renderScale, renderScale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var outline = Rounded(new RectangleF(0.6F, 0.6F, CardWidth - 1.2F, CardHeight - 1.2F), 13.5F))
        using (var pen = new Pen(Line))
            g.DrawPath(pen, outline);

        DrawHeader(g);
        if (snapshot.Supply != null)
        {
            DrawPower(g, snapshot.Supply);
            DrawRails(g, snapshot.Supply);
            DrawPins(g, snapshot.Supply);
            DrawGpu(g, 336);
        }
        else
        {
            DrawUnavailable(g);
            DrawGpu(g, 149);
        }
        string time = snapshot.SupplyUpdatedAt.HasValue
            ? "电源采样  " + snapshot.SupplyUpdatedAt.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "电源采样  —";
        DrawText(g, time, tinyFont, Faint, new RectangleF(22, CardHeight - 23, 388, 17), right);
        g.Restore(saved);
    }

    private void DrawHeader(Graphics g)
    {
        FillCard(g, new RectangleF(15, 14, 31, 31), 9, GlyphBackground);
        using (var brush = new SolidBrush(Mint))
            g.FillPolygon(brush, Bolt);
        DrawText(g, "电源与显卡监控", titleFont, Foreground, new RectangleF(55, 12, 254, 25), left);
        DrawText(g, "Ai1300TS PCIE5  ·  Astral RTX 5090 P32G Edition 20", labelFont, Muted, new RectangleF(55, 35, 258, 14), left);

        bool fullyConnected = snapshot.Supply != null && snapshot.Gpu != null;
        bool partlyConnected = snapshot.Supply != null || snapshot.Gpu != null;
        string status = fullyConnected ? "实时" : partlyConnected ? "部分连接" : "等待连接";
        Color statusColor = fullyConnected ? Mint : Amber;
        FillCard(g, new RectangleF(330, 20, 87, 23), 11.5F, fullyConnected ? BadgeLive : BadgeWaiting);
        Dot(g, statusColor, 341, 28.5F, 6);
        DrawText(g, status, labelFont, statusColor, new RectangleF(352, 21, 56, 21), left);
    }

    private void DrawPower(Graphics g, PsuTelemetry s)
    {
        var card = new RectangleF(12, 55, SectionWidth, 76);
        using (var path = Rounded(card, 10))
        using (var fill = new LinearGradientBrush(card, HeroStart, HeroEnd, LinearGradientMode.Horizontal))
            g.FillPath(fill, path);

        string watts = Number(s.OutputWatts, "0");
        DrawText(g, "输出功率", labelFont, Mint, new RectangleF(26, 64, 125, 14), left);
        DrawText(g, watts, heroFont, Foreground, new RectangleF(24, 76, 111, 47), left);
        float wattsWidth = g.MeasureString(watts, heroFont, PointF.Empty, StringFormat.GenericTypographic).Width;
        DrawText(g, "W", metricFont, Muted, new RectangleF(Math.Min(125, 28 + wattsWidth), 91, 25, 30), left);
        Divider(g, 163, 65, 163, 121);
        Metric(g, 181, 62, "估算输入", Value(s.InputWatts, "0", " W"));
        Metric(g, 305, 62, "转换效率", Value(s.EfficiencyPercent, "0.0", "%"));
        Metric(g, 181, 96, "电源温度", Value(s.TemperatureCelsius, "0.0", " °C"));
        Metric(g, 305, 96, "风扇转速", Value(s.FanRpm, "0", " RPM"));
    }

    private void Metric(Graphics g, float x, float y, string label, string value)
    {
        DrawText(g, label, tinyFont, Muted, new RectangleF(x, y, 103, 13), left);
        DrawText(g, value, readingFont, Foreground, new RectangleF(x, y + 12, 110, 19), left);
    }

    private void DrawRails(Graphics g, PsuTelemetry s)
    {
        FillCard(g, new RectangleF(12, 139, SectionWidth, 85), 10, Surface);
        DrawText(g, "直流输出", sectionFont, Foreground, new RectangleF(25, 144, 130, 20), left);
        DrawText(g, "电压", tinyFont, Muted, new RectangleF(195, 145, 96, 19), right);
        DrawText(g, "电流", tinyFont, Muted, new RectangleF(308, 145, 95, 19), right);
        Rail(g, 164, "+12 V", s.Voltage12V, s.Current12V);
        Rail(g, 182, "+5 V", s.Voltage5V, s.Current5V);
        Rail(g, 200, "+3.3 V", s.Voltage3V3, s.Current3V3);
    }

    private void Rail(Graphics g, float y, string name, double voltage, double current)
    {
        Divider(g, 25, y, 403, y);
        DrawText(g, name, readingFont, Muted, new RectangleF(25, y + 1, 105, 16), left);
        DrawText(g, Value(voltage, "0.00", " V"), readingFont, Foreground, new RectangleF(195, y + 1, 96, 16), right);
        DrawText(g, Value(current, "0.00", " A"), readingFont, Foreground, new RectangleF(308, y + 1, 95, 16), right);
    }

    private void DrawPins(Graphics g, PsuTelemetry s)
    {
        FillCard(g, new RectangleF(12, 232, SectionWidth, 96), 10, Surface);
        DrawText(g, "12V-2x6 逐针电流", sectionFont, Foreground, new RectangleF(25, 237, 230, 19), left);
        DrawText(g, "单位 A", tinyFont, Muted, new RectangleF(310, 237, 93, 19), right);
        for (int pin = 0; pin < 6; pin++)
            DrawText(g, "P" + (pin + 1), tinyFont, Faint, new RectangleF(100 + pin * 51, 258, 46, 14), center);
        Pins(g, 273, "接口 1", s.Connector1Currents, s.Connector1Status);
        Pins(g, 297, "接口 2", s.Connector2Currents, s.Connector2Status);
    }

    private void Pins(Graphics g, float y, string port, double[] values, byte? status)
    {
        DrawText(g, port, labelFont, Foreground, new RectangleF(25, y, 65, 13), left);
        Color statusColor = !status.HasValue ? Faint : status.Value == 0 ? Mint : Amber;
        string state = !status.HasValue ? "状态未知" : status.Value == 0 ? "无告警" :
            "告警 " + status.Value.ToString("X2", CultureInfo.InvariantCulture);
        Dot(g, statusColor, 26, y + 16, 4);
        DrawText(g, state, tinyFont, statusColor, new RectangleF(35, y + 11, 61, 14), left);
        for (int pin = 0; pin < 6; pin++)
        {
            var cell = new RectangleF(100 + pin * 51, y + 1, 46, 20);
            FillCard(g, cell, 5, Background);
            double value = values[pin];
            DrawText(g, Number(value, "0.00"), readingFont, value == 0 ? Muted : Foreground, cell, center);
        }
    }

    private void DrawGpu(Graphics g, float y)
    {
        FillCard(g, new RectangleF(12, y, SectionWidth, 65), 10, Surface);
        DrawText(g, "显卡姿态", sectionFont, Lavender, new RectangleF(25, y + 5, 170, 18), left);
        if (snapshot.Gpu == null)
        {
            DrawText(g, snapshot.GpuDetail ?? "正在读取显卡姿态…", labelFont, Muted,
                new RectangleF(25, y + 29, 376, 23), left);
            return;
        }
        GpuMetric(g, 25, y, "俯仰角", Value(snapshot.Gpu.PitchDegrees, "+0.00;-0.00;0.00", "°"), Foreground);
        GpuMetric(g, 156, y, "横滚角", Value(snapshot.Gpu.RollDegrees, "+0.00;-0.00;0.00", "°"), Foreground);
        GpuMetric(g, 282, y, "相对基准偏移",
            snapshot.GpuDeviation.HasValue ? Value(snapshot.GpuDeviation, "0.00", "°") : "未设基准",
            snapshot.GpuAboveThreshold ? Amber : snapshot.GpuDeviation.HasValue ? Lavender : Faint);
    }

    private void GpuMetric(Graphics g, float x, float y, string label, string value, Color tone)
    {
        DrawText(g, label, tinyFont, Muted, new RectangleF(x, y + 25, 115, 13), left);
        DrawText(g, value, metricFont, tone, new RectangleF(x, y + 37, 116, 22), left);
    }

    private void DrawUnavailable(Graphics g)
    {
        FillCard(g, new RectangleF(12, 55, SectionWidth, 86), 10, Surface);
        DrawText(g, "电源", sectionFont, Mint, new RectangleF(25, 63, 145, 20), left);
        DrawText(g, snapshot.SupplyState ?? "正在连接", sectionFont, Foreground, new RectangleF(25, 86, 375, 19), left);
        DrawText(g, snapshot.SupplyDetail ?? "正在读取电源传感器…", labelFont, Muted,
            new RectangleF(25, 108, 375, 20), left);
    }

    private static string Number(double value, string format)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? "—" :
            value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string Value(double? value, string format, string units)
    {
        return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? Number(value.Value, format) + units : "—";
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF layout, StringFormat alignment)
    {
        using (var brush = new SolidBrush(color))
            g.DrawString(text, font, brush, layout, alignment);
    }

    private static StringFormat Alignment(StringAlignment alignment)
    {
        return new StringFormat
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
    }

    private static GraphicsPath Rounded(RectangleF area, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(area.Left, area.Top, diameter, diameter, 180, 90);
        path.AddArc(area.Right - diameter, area.Top, diameter, diameter, 270, 90);
        path.AddArc(area.Right - diameter, area.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(area.Left, area.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillCard(Graphics g, RectangleF area, float radius, Color color)
    {
        using (var outline = Rounded(area, radius))
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, outline);
    }

    private static void Dot(Graphics g, Color color, float x, float y, float diameter)
    {
        using (var brush = new SolidBrush(color))
            g.FillEllipse(brush, x, y, diameter, diameter);
    }

    private static void Divider(Graphics g, float x1, float y1, float x2, float y2)
    {
        using (var pen = new Pen(Line, 0.65F))
            g.DrawLine(pen, x1, y1, x2, y2);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            titleFont.Dispose();
            sectionFont.Dispose();
            labelFont.Dispose();
            tinyFont.Dispose();
            readingFont.Dispose();
            metricFont.Dispose();
            heroFont.Dispose();
            left.Dispose();
            right.Dispose();
            center.Dispose();
        }
    }
}
