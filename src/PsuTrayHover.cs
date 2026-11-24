using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class PsuTrayHover : IDisposable
{
    private const int HoverDelayMs = 350;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo IconId = typeof(NotifyIcon).GetField("id", PrivateInstance);
    private static readonly FieldInfo IconWindow = typeof(NotifyIcon).GetField("window", PrivateInstance);

    private readonly NotifyIcon icon;
    private readonly Timer timer = new Timer { Interval = 50 };
    private readonly Stopwatch hoverTime = new Stopwatch();
    private readonly float scale = GetDpiForSystem() / 96F;
#if GPU_LEVEL
    private MonitorTrayPopup popup;
    private MonitorTraySnapshot snapshot;
#else
    private HoverWindow popup;
    private string content = String.Empty;
#endif
    private Point anchor;
    private Rectangle hoverBounds;
    private bool disposed;

    public PsuTrayHover(NotifyIcon icon)
    {
        if (icon == null)
            throw new ArgumentNullException("icon");

        this.icon = icon;
        this.icon.MouseMove += OnMouseMove;
        this.icon.MouseDown += OnMouseDown;
        timer.Tick += OnTick;
    }

#if GPU_LEVEL
    public void Update(MonitorTraySnapshot next)
    {
        if (disposed)
            return;

        snapshot = next;
        if (snapshot == null)
        {
            Dismiss();
            return;
        }

        if (popup != null && popup.Visible)
        {
            if (IsHovering())
                popup.SetContent(snapshot, anchor, Screen.FromPoint(anchor).WorkingArea);
            else
                Dismiss();
        }
    }
#else
    public void Update(string text)
    {
        if (disposed)
            return;

        string next = (text ?? String.Empty).Trim();
        if (String.Equals(content, next, StringComparison.Ordinal))
            return;

        content = next;
        if (content.Length == 0)
        {
            Dismiss();
        }
        else if (popup != null && popup.Visible)
        {
            if (IsHovering())
                popup.Present(content, anchor);
            else
                Dismiss();
        }
    }
#endif

    public void Dismiss()
    {
        if (disposed)
            return;

        timer.Stop();
        hoverTime.Reset();
        if (popup != null && popup.Visible)
            popup.Hide();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        Dismiss();
        disposed = true;
        icon.MouseMove -= OnMouseMove;
        icon.MouseDown -= OnMouseDown;
        timer.Tick -= OnTick;
        timer.Dispose();
        if (popup != null)
            popup.Dispose();
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        if (!icon.Visible || IsMenuOpen() || Control.MouseButtons != MouseButtons.None)
        {
            Dismiss();
            return;
        }

        Point cursor = Cursor.Position;
        if (hoverTime.IsRunning && hoverBounds.Contains(cursor))
            return;

        Dismiss();
        anchor = cursor;
        hoverBounds = GetHoverBounds(cursor);
        hoverTime.Start();
        timer.Start();
    }

    private void OnMouseDown(object sender, MouseEventArgs args)
    {
        Dismiss();
    }

    private void OnTick(object sender, EventArgs args)
    {
        if (!IsHovering())
        {
            Dismiss();
            return;
        }

        if (hoverTime.ElapsedMilliseconds < HoverDelayMs || !HasContent || (popup != null && popup.Visible))
            return;

        if (popup == null)
            popup = CreatePopup();

#if GPU_LEVEL
        popup.Present(snapshot, anchor);
#else
        popup.Present(content, anchor);
#endif
    }

#if GPU_LEVEL
    private bool HasContent
    {
        get { return snapshot != null; }
    }

    private MonitorTrayPopup CreatePopup()
    {
        return new MonitorTrayPopup(scale);
    }
#else
    private bool HasContent
    {
        get { return content.Length != 0; }
    }

    private HoverWindow CreatePopup()
    {
        return new HoverWindow(scale);
    }
#endif

    private bool IsHovering()
    {
        return icon.Visible && !IsMenuOpen() && Control.MouseButtons == MouseButtons.None
            && hoverBounds.Contains(Cursor.Position);
    }

    private bool IsMenuOpen()
    {
        return icon.ContextMenuStrip != null && icon.ContextMenuStrip.Visible;
    }

    private Rectangle GetHoverBounds(Point cursor)
    {
        Rectangle bounds;
        if (TryGetIconBounds(out bounds))
        {
            bounds.Inflate(1, 1);
            if (bounds.Contains(cursor))
                return bounds;
        }

        int radius = Math.Max(6, (int)Math.Round(10 * scale));
        return new Rectangle(cursor.X - radius, cursor.Y - radius, radius * 2 + 1, radius * 2 + 1);
    }

    private bool TryGetIconBounds(out Rectangle bounds)
    {
        bounds = Rectangle.Empty;

        // NotifyIcon does not expose the HWND/ID pair Explorer needs. This
        // optional lookup targets .NET Framework and has a cursor fallback.
        if (IconId == null || IconWindow == null)
            return false;

        try
        {
            var window = IconWindow.GetValue(icon) as NativeWindow;
            if (window == null || window.Handle == IntPtr.Zero)
                return false;

            var identifier = new NotifyIconIdentifier
            {
                Size = Marshal.SizeOf(typeof(NotifyIconIdentifier)),
                Window = window.Handle,
                Id = (int)IconId.GetValue(icon)
            };
            NativeRect rect;
            if (Shell_NotifyIconGetRect(ref identifier, out rect) != 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                return false;

            bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }
        catch (MemberAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        internal int Size;
        internal IntPtr Window;
        internal int Id;
        internal Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForSystem();

    private sealed class HoverWindow : Form
    {
        private const TextFormatFlags TextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
        private static readonly Color Fill = Color.FromArgb(24, 32, 45);
        private static readonly Color Edge = Color.FromArgb(61, 79, 98);
        private static readonly Color Heading = Color.FromArgb(101, 231, 194);
        private static readonly Color ForegroundText = Color.FromArgb(238, 244, 248);
        private readonly Font titleFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        private readonly Font textFont = new Font("Microsoft YaHei UI", 9F);
        private readonly float scale;
        private string title = String.Empty;
        private string body = String.Empty;
        private int titleHeight;

        public HoverWindow(float scale)
        {
            this.scale = scale;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Fill;
            TopMost = true;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

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

        public void Present(string text, Point anchor)
        {
            int firstBreak = text.IndexOf('\n');
            title = firstBreak < 0 ? text : text.Substring(0, firstBreak).TrimEnd('\r');
            body = firstBreak < 0 ? String.Empty : text.Substring(firstBreak + 1).TrimStart('\r', '\n');

            Rectangle area = Screen.FromPoint(anchor).WorkingArea;
            int padding = Pixels(13);
            int gap = Pixels(6);
            int maxWidth = Math.Max(1, Math.Min(Pixels(510), area.Width - 2 * gap - 2 * padding));
            int minWidth = Math.Min(Pixels(328), maxWidth);
            int width;
            int bodyHeight;
            using (Graphics graphics = CreateGraphics())
            {
                int preferred = TextRenderer.MeasureText(graphics, title, titleFont, Size.Empty,
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                foreach (string line in body.Split('\n'))
                {
                    preferred = Math.Max(preferred, TextRenderer.MeasureText(graphics, line.TrimEnd('\r'),
                        textFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
                }

                width = Math.Min(Math.Max(preferred + Pixels(4), minWidth), maxWidth);
                var bounds = new Size(width, Int32.MaxValue);
                titleHeight = TextRenderer.MeasureText(graphics, title, titleFont, bounds, TextFlags).Height;
                bodyHeight = body.Length == 0 ? 0 : TextRenderer.MeasureText(graphics, body, textFont, bounds, TextFlags).Height;
            }

            var size = new Size(width + 2 * padding,
                Math.Min(padding + titleHeight + (bodyHeight == 0 ? 0 : gap + bodyHeight) + padding,
                    Math.Max(1, area.Height - 2 * gap)));
            Bounds = PlacePopup(anchor, area, size, gap);
            Invalidate();
            if (!Visible)
                Show();
        }

        private static Rectangle PlacePopup(Point anchor, Rectangle area, Size size, int gap)
        {
            int x = anchor.X + gap;
            int y = anchor.Y - size.Height - gap;
            if (anchor.X >= area.Right)
                x = area.Right - size.Width - gap;
            else if (anchor.X < area.Left)
                x = area.Left + gap;
            if (y < area.Top + gap)
                y = anchor.Y + gap;

            x = Math.Max(area.Left + gap, Math.Min(x, area.Right - size.Width - gap));
            y = Math.Max(area.Top + gap, Math.Min(y, area.Bottom - size.Height - gap));
            return new Rectangle(new Point(x, y), size);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            int padding = Pixels(13);
            int gap = Pixels(6);
            int width = Math.Max(1, ClientSize.Width - 2 * padding);
            var titleBounds = new Rectangle(padding, padding, width, titleHeight);
            TextRenderer.DrawText(args.Graphics, title, titleFont, titleBounds, Heading, TextFlags);
            if (body.Length > 0)
            {
                int bodyTop = titleBounds.Bottom + gap;
                var bodyBounds = new Rectangle(padding, bodyTop, width, Math.Max(1, ClientSize.Height - bodyTop - padding));
                TextRenderer.DrawText(args.Graphics, body, textFont, bodyBounds, ForegroundText, TextFlags);
            }

            using (var border = new Pen(Edge))
                args.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void WndProc(ref Message message)
        {
            const int mouseActivate = 0x21;
            const int doNotActivate = 3;
            if (message.Msg == mouseActivate)
            {
                message.Result = new IntPtr(doNotActivate);
                return;
            }

            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                titleFont.Dispose();
                textFont.Dispose();
            }
        }

        private int Pixels(int value)
        {
            return Math.Max(1, (int)Math.Round(value * scale));
        }
    }
}
