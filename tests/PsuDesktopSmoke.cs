using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class PsuDesktopSmoke
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        string scratch = AppDomain.CurrentDomain.BaseDirectory;
        string reportPath = Path.Combine(scratch, "psu-desktop-smoke.txt");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            string command;
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                command = run == null ? null : run.GetValue(PsuStartup.ValueName) as string;
            // The merged PSU+GPU monitor owns the logon entry now. A missing PSU entry is
            // acceptable; a present one must point at a PSU monitor executable.
            if (command != null && command.IndexOf("Ai1300TS-PsuMonitor.exe", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Windows logon command points elsewhere: " + command);
            if (PsuStartup.IsBlockedByWindows())
                throw new InvalidOperationException("Windows logon entry is disabled by the user.");

            using (var window = new MonitorWindow())
            using (var context = new ApplicationContext())
            using (var check = new Timer { Interval = 200 })
            {
                var connection = (Label)typeof(MonitorWindow).GetField("connection", Private).GetValue(window);
                var hover = (PsuTrayHover)typeof(MonitorWindow).GetField("trayHover", Private).GetValue(window);
                var content = typeof(PsuTrayHover).GetField("content", Private);
                var popup = typeof(PsuTrayHover).GetField("popup", Private);
                var hoverTime = (System.Diagnostics.Stopwatch)typeof(PsuTrayHover).GetField("hoverTime", Private).GetValue(hover);
                var mouseMove = typeof(PsuTrayHover).GetMethod("OnMouseMove", Private);
                var start = DateTime.UtcNow;
                bool requestedHover = false;
                check.Tick += (sender, args) =>
                {
                    if (!hoverTime.IsRunning && connection.Text == "已连接" && !window.Visible)
                    {
                        requestedHover = true;
                        mouseMove.Invoke(hover, new object[] {null, new MouseEventArgs(MouseButtons.None, 0, 0, 0, 0)});
                    }
                    var tool = popup.GetValue(hover) as Form;
                    bool finished = requestedHover && tool != null && tool.Visible;
                    if (!finished && (DateTime.UtcNow - start).TotalSeconds < 8)
                        return;

                    check.Stop();
                    if (!finished || window.Visible)
                    {
                        File.WriteAllText(reportPath, "FAIL hidden-window live sampling or simulated hover. State " +
                            connection.Text + ", window visible: " + window.Visible + ", pointer pressed: " +
                            Control.MouseButtons + ", attempted mouse move: " + requestedHover +
                            ", hover timer: " + hoverTime.IsRunning + ", popup constructed: " + (tool != null));
                    }
                    else
                    {
                        string data = (string)content.GetValue(hover);
                        if (!data.Contains("接口 2") || !data.Contains("更新于") || tool.Width <= 0 || tool.Height <= 0)
                            throw new InvalidOperationException("The live tooltip has incomplete contents or bounds.");
                        using (var capture = new Bitmap(tool.Width, tool.Height))
                        {
                            tool.DrawToBitmap(capture, new Rectangle(Point.Empty, tool.Size));
                            capture.Save(Path.Combine(scratch, "psu-hover-smoke.png"));
                        }
                        hover.Dismiss();
                        if (tool.Visible)
                            throw new InvalidOperationException("Hover window did not dismiss.");
                        File.WriteAllText(reportPath,
                            "PASS hidden-window real HID readings, simulated hover and dismiss." +
                            Environment.NewLine + "logon entry: " +
                            (command == null ? "absent (owned by Ai1300TS-GpuMonitor)" : command) +
                            Environment.NewLine + data);
                    }
                    typeof(MonitorWindow).GetMethod("RequestExit", Private).Invoke(window, null);
                };
                check.Start();
                window.FormClosed += (sender, args) => context.ExitThread();
                window.StartPolling();
                Application.Run(context);
            }
        }
        catch (Exception error)
        {
            File.WriteAllText(reportPath, error.ToString());
        }
    }
}
