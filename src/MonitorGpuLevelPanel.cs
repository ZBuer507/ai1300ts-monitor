using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed partial class MonitorWindow
{
    private const double GpuDeviationThreshold = 0.30;
    private const int GpuPollMilliseconds = 2000;
    private const int GpuRetryMilliseconds = 5000;
    private const string GpuSettingsKey = @"Software\Ai1300TS-GpuMonitor";
    private const string GpuBaselineValue = "LevelBaseline_8A8C1043";
    private readonly Timer gpuTimer = new Timer();
    private GpuLevelSense gpuReader;
    private GpuLevelReading gpuLatest;
    private GpuLevelReading gpuBaseline;
    private bool gpuPolling;
    private double? gpuTrayDeviation;
    private bool gpuTrayAboveThreshold;
    private Label gpuPitch;
    private Label gpuRoll;
    private Label gpuDeviation;
    private Label gpuState;
    private Button gpuCalibrate;

    private Control CreateGpuPanel()
    {
        gpuBaseline = ReadGpuBaseline();
        var panel = Section(4, 4);
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        AddSectionTitle(panel, "显卡姿态  ·  ROG Astral RTX 5090", 4);
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(18)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, Pixels(25)));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(LabelFor("俯仰角", smallFont, Secondary), 0, 1);
        panel.Controls.Add(LabelFor("横滚角", smallFont, Secondary), 1, 1);
        panel.Controls.Add(LabelFor("相对基准偏移", smallFont, Secondary), 2, 1);
        gpuPitch = LabelFor("—", readingFont, Foreground);
        gpuRoll = LabelFor("—", readingFont, Foreground);
        gpuDeviation = LabelFor("—", readingFont, Foreground);
        panel.Controls.Add(gpuPitch, 0, 2);
        panel.Controls.Add(gpuRoll, 1, 2);
        panel.Controls.Add(gpuDeviation, 2, 2);
        gpuCalibrate = ActionButton("设为基准");
        gpuCalibrate.Enabled = false;
        gpuCalibrate.Click += SetGpuBaseline;
        panel.Controls.Add(gpuCalibrate, 3, 1);
        panel.SetRowSpan(gpuCalibrate, 2);
        gpuState = LabelFor("正在连接姿态传感器…", smallFont, Secondary);
        gpuState.AutoEllipsis = true;
        panel.Controls.Add(gpuState, 0, 3);
        panel.SetColumnSpan(gpuState, 4);
        return panel;
    }

    private void StartGpuPolling()
    {
        gpuTimer.Tick += async (sender, args) => await PollGpuAsync();
        gpuTimer.Interval = 100;
        gpuTimer.Start();
    }

    private async Task PollGpuAsync()
    {
        if (gpuPolling || closing)
            return;
        gpuTimer.Stop();
        gpuPolling = true;
        try
        {
            var sample = await Task.Run((Func<GpuLevelReading>)ReadGpuLevel);
            if (!closing)
            {
                gpuLatest = sample;
                ShowGpuLevel();
                gpuTimer.Interval = GpuPollMilliseconds;
            }
        }
        catch (Exception error)
        {
            if (!closing)
            {
                gpuLatest = null;
                gpuTrayDeviation = null;
                gpuTrayAboveThreshold = false;
                gpuPitch.Text = "—";
                gpuRoll.Text = "—";
                gpuDeviation.Text = "—";
                gpuDeviation.ForeColor = Foreground;
                gpuCalibrate.Enabled = false;
                gpuState.Text = error.Message;
                gpuState.ForeColor = Warning;
                RefreshTraySummary();
                gpuTimer.Interval = GpuRetryMilliseconds;
            }
        }
        finally
        {
            gpuPolling = false;
            if (!closing)
                gpuTimer.Start();
            else
                CloseGpuReader();
        }
    }

    private GpuLevelReading ReadGpuLevel()
    {
        if (gpuReader == null)
            gpuReader = new GpuLevelSense();
        return gpuReader.Read();
    }

    private void DisposeGpuPolling()
    {
        gpuTimer.Dispose();
        if (!gpuPolling)
            CloseGpuReader();
    }

    private void CloseGpuReader()
    {
        if (gpuReader != null)
        {
            gpuReader.Dispose();
            gpuReader = null;
        }
    }

    private void ShowGpuLevel()
    {
        gpuPitch.Text = Reading(gpuLatest.PitchDegrees, "+0.00;-0.00;0.00", "°");
        gpuRoll.Text = Reading(gpuLatest.RollDegrees, "+0.00;-0.00;0.00", "°");
        gpuCalibrate.Enabled = true;
        gpuTrayDeviation = gpuBaseline == null ? (double?)null : gpuLatest.DeviationFrom(gpuBaseline);
        gpuTrayAboveThreshold = gpuTrayDeviation >= GpuDeviationThreshold;
        gpuDeviation.Text = Reading(gpuTrayDeviation, "0.00", "°");
        gpuDeviation.ForeColor = gpuTrayAboveThreshold ? Warning : Foreground;
        gpuState.ForeColor = gpuTrayAboveThreshold ? Warning : Secondary;
        gpuState.Text = !gpuTrayDeviation.HasValue ? "可将当前姿态设为基准，以监测后续偏移。" :
            gpuTrayAboveThreshold ? "已达到 0.30° 偏移提示线" : "偏移低于 0.30°";
        RefreshTraySummary();
    }

    private void SetGpuBaseline(object sender, EventArgs args)
    {
        if (gpuLatest == null)
            return;
        try
        {
            using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(GpuSettingsKey))
            {
                if (settings == null)
                    throw new IOException("无法打开当前用户的姿态基准设置。");
                settings.SetValue(GpuBaselineValue, new[]
                {
                    gpuLatest.RawX.ToString(CultureInfo.InvariantCulture),
                    gpuLatest.RawY.ToString(CultureInfo.InvariantCulture),
                    gpuLatest.RawZ.ToString(CultureInfo.InvariantCulture)
                }, RegistryValueKind.MultiString);
            }
        }
        catch (Exception error)
        {
            if (!IsGpuStorageError(error))
                throw;
            MessageBox.Show(this, "无法保存显卡姿态基准。\n" + error.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        gpuBaseline = gpuLatest;
        ShowGpuLevel();
    }

    private static GpuLevelReading ReadGpuBaseline()
    {
        try
        {
            using (RegistryKey settings = Registry.CurrentUser.OpenSubKey(GpuSettingsKey))
            {
                string[] values = settings == null ? null : settings.GetValue(GpuBaselineValue) as string[];
                if (values == null || values.Length != 3)
                    return null;
                short x, y, z;
                if (!Int16.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ||
                    !Int16.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) ||
                    !Int16.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out z) ||
                    (x == 0 && y == 0 && z == 0) || (x == -1 && y == -1 && z == -1))
                    return null;
                return new GpuLevelReading(x, y, z, "ROG Astral RTX 5090");
            }
        }
        catch (Exception error)
        {
            if (!IsGpuStorageError(error))
                throw;
            return null;
        }
    }

    private static bool IsGpuStorageError(Exception error)
    {
        return error is IOException || error is UnauthorizedAccessException || error is SecurityException;
    }
}
