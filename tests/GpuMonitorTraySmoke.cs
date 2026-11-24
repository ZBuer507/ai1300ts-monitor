using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class GpuMonitorTraySmoke
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        string scratch = AppDomain.CurrentDomain.BaseDirectory;
        string reportPath = Path.Combine(scratch, "gpu-monitor-tray-smoke.txt");
        string capturePath = Path.Combine(scratch, "gpu-tray-card.png");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var window = new MonitorWindow())
        using (var context = new ApplicationContext())
        using (var timer = new Timer { Interval = 200 })
        {
            window.FormClosed += (sender, args) => context.ExitThread();
            var hover = typeof(MonitorWindow).GetField("trayHover", Private).GetValue(window);
            var snapshotField = typeof(PsuTrayHover).GetField("snapshot", Private);
            var popupField = typeof(PsuTrayHover).GetField("popup", Private);
            var scaleField = typeof(PsuTrayHover).GetField("scale", Private);
            var mouseMove = typeof(PsuTrayHover).GetMethod("OnMouseMove", Private);
            var dismiss = typeof(PsuTrayHover).GetMethod("Dismiss", Type.EmptyTypes);
            var exit = typeof(MonitorWindow).GetMethod("RequestExit", Private);
            DateTime started = DateTime.UtcNow;
            bool requested = false;
            timer.Tick += (sender, args) =>
            {
                var snapshot = snapshotField == null ? null : snapshotField.GetValue(hover) as MonitorTraySnapshot;
                bool ready = snapshot != null && snapshot.Supply != null && snapshot.Gpu != null && !window.Visible;
                bool timedOut = (DateTime.UtcNow - started).TotalSeconds > 20;
                var popup = popupField == null ? null : popupField.GetValue(hover) as Form;

                if (!ready)
                {
                    if (!timedOut)
                        return;
                    Finish(reportPath, exit, window, "FAIL no live PSU+GPU tray snapshot within 20 s.");
                    return;
                }

                if (!requested)
                {
                    requested = true;
                    var scale = (float)scaleField.GetValue(hover);
                    mouseMove.Invoke(hover, new object[]
                    {
                        null, new MouseEventArgs(MouseButtons.None, 0, 0, 0, 0)
                    });
                    if (scale <= 0)
                    {
                        Finish(reportPath, exit, window, "FAIL tray hover scale is not usable.");
                    }
                    return;
                }

                if (popup == null || !popup.Visible)
                {
                    if (!timedOut)
                        return;
                    Finish(reportPath, exit, window, "FAIL the tray hover card did not appear within 20 s.");
                    return;
                }

                string result;
                try
                {
                    result = Verify(snapshot, popup, (float)scaleField.GetValue(hover), capturePath);
                }
                catch (Exception error)
                {
                    result = "FAIL " + error;
                }

                dismiss.Invoke(hover, null);
                if (popup.Visible)
                    result = "FAIL the tray hover card did not dismiss.";
                Finish(reportPath, exit, window, result);
            };
            window.StartPolling();
            timer.Start();
            Application.Run(context);
        }
    }

    private static void Finish(string reportPath, MethodInfo exit, MonitorWindow window, string result)
    {
        File.WriteAllText(reportPath, result);
        exit.Invoke(window, null);
    }

    private static string Verify(MonitorTraySnapshot snapshot, Form card, float scale, string capturePath)
    {
        if (card.GetType() != typeof(MonitorTrayPopup))
            throw new InvalidOperationException("The hover window is not the tray card: " + card.GetType().Name);
        if (snapshot.Supply.Connector2Currents.Length != 6)
            throw new InvalidOperationException("Connector 2 does not carry six pin readings.");
        if (Double.IsNaN(snapshot.Supply.OutputWatts) || snapshot.Supply.OutputWatts < 0)
            throw new InvalidOperationException("Output power is not a usable reading.");
        if (Double.IsNaN(snapshot.Gpu.PitchDegrees) || Double.IsNaN(snapshot.Gpu.RollDegrees))
            throw new InvalidOperationException("GPU pitch or roll is not a usable reading.");

        int expectedWidth = (int)Math.Floor(432 * scale);
        int expectedHeight = (int)Math.Floor(424 * scale);
        if (Math.Abs(card.Width - expectedWidth) > 2 || Math.Abs(card.Height - expectedHeight) > 2)
            throw new InvalidOperationException("Unexpected tray card size " + card.Size +
                " for hover scale " + scale + ".");

        using (var image = new Bitmap(card.Width, card.Height))
        {
            card.DrawToBitmap(image, new Rectangle(Point.Empty, card.Size));
            if (DistinctColors(image) < 8)
                throw new InvalidOperationException("Tray card rendered blank.");
            image.Save(capturePath);
        }

        string supply = "输出 " + snapshot.Supply.OutputWatts.ToString("0") + " W";
        string gpu = "俯仰 " + snapshot.Gpu.PitchDegrees.ToString("+0.00;-0.00;0.00") +
            "° 横滚 " + snapshot.Gpu.RollDegrees.ToString("+0.00;-0.00;0.00") + "°";
        string pins = String.Join(" / ", Array.ConvertAll(snapshot.Supply.Connector2Currents,
            value => value.ToString("0.00")));
        return "PASS live hover card from the real tray hover path." + Environment.NewLine +
            supply + "  ·  " + gpu + Environment.NewLine +
            "接口 2 电流 " + pins + " A" + Environment.NewLine +
            "card " + card.Width + "x" + card.Height + " at scale " + scale.ToString("0.##") +
            "  ·  " + capturePath;
    }

    private static int DistinctColors(Bitmap image)
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int y = 0; y < image.Height; y += 4)
        {
            for (int x = 0; x < image.Width; x += 4)
                seen.Add(image.GetPixel(x, y).ToArgb());
        }

        return seen.Count;
    }
}
