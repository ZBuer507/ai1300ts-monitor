using System;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class PsuStartup
{
    internal const string ValueName = MonitorProduct.Key;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const int MaximumRunCommandLength = 260;

    internal static bool HasRegistration()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            string command = key == null ? null : key.GetValue(
                ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return String.Equals(command, StartupCommand, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static bool IsEnabled()
    {
        return HasRegistration() && !IsBlockedByWindows();
    }

    internal static bool IsBlockedByWindows()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ApprovalKey))
        {
            byte[] approval = key == null ? null : key.GetValue(ValueName) as byte[];
            // Windows Settings and Task Manager own this state.
            return approval != null && approval.Length >= 4 &&
                (approval[0] == 0x03 || approval[0] == 0x07);
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string command = StartupCommand;
            if (command.Length > MaximumRunCommandLength)
                throw new InvalidOperationException("程序路径太长，无法注册为 Windows 启动应用。");

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null)
                    throw new InvalidOperationException("无法打开当前用户的 Windows 启动设置。");
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
        }
        else
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key != null)
                    key.DeleteValue(ValueName, false);
            }
        }
    }

    private static string StartupCommand
    {
        get { return "\"" + Application.ExecutablePath + "\" --background"; }
    }
}
