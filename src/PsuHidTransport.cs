using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public sealed class PsuHidTransport : IDisposable
{
    private const ushort VendorId = 0x0DB0;
    private const ushort ProductId = 0xAA6F;
    private const ushort UsagePage = 0xFF00;
    private const int ReportSize = 65;
    private const string MsiMutexName = @"Global\MSI_PSU_Mutex";

    private const uint Synchronize = 0x00100000;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint WaitObject = 0;
    private const uint WaitAbandoned = 0x80;
    private const uint WaitTimeout = 0x102;
    private const int ErrorFileNotFound = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorIoPending = 997;
    private const int ErrorAccessDenied = 5;
    private const int ResponseTimeoutMs = 500;
    private const int MaxMismatchedReplies = 16;

    private static readonly object CancellationGate = new object();
    private static HidOperation pendingCancellation;
    private readonly object gate = new object();
    private SafeWaitHandle msiMutex;
    private SafeFileHandle device;
    private bool disposed;

    public byte[] ReadRegister(byte register)
    {
        if (register != 0xE0 && register != 0xC1)
            throw new ArgumentOutOfRangeException("register", "仅支持读取 E0 遥测和 C1 告警状态。");

        lock (gate)
        {
            if (disposed)
                throw new ObjectDisposedException("PsuHidTransport");

            if (msiMutex == null)
                msiMutex = OpenOrCreateMsiMutex();

            uint wait = Native.WaitForSingleObject(msiMutex, 2000);
            if (wait == WaitTimeout)
                throw new TimeoutException("等待电源访问锁超时。其他监控程序可能正在读取。");
            if (wait != WaitObject && wait != WaitAbandoned)
                throw Win32Failure("等待电源访问锁失败", Marshal.GetLastWin32Error());

            try
            {
                if (!TryFinishPendingCancellation())
                    throw new IOException("Windows 尚未确认上一次 HID 操作已取消，暂缓重新连接。");

                if (device == null)
                {
                    device = OpenDevice(FindDevice(), GenericReadWrite, FileFlagOverlapped);
                    CheckDevice(device);
                    Exchange(0xFA, 0x51);
                }
                return Exchange(0x51, register);
            }
            catch
            {
                CloseDevice();
                throw;
            }
            finally
            {
                if (!Native.ReleaseMutex(msiMutex))
                {
                    int error = Marshal.GetLastWin32Error();
                    msiMutex.Dispose();
                    msiMutex = null;
                    CloseDevice();
                    throw Win32Failure("释放电源访问锁失败", error);
                }
            }
        }
    }

    public static string ReadProductName()
    {
        return ReadDeviceString(true);
    }

    public static string ReadManufacturerName()
    {
        return ReadDeviceString(false);
    }

    public static string ReadModelName()
    {
        string product = ReadProductName();
        if (product == null)
            return null;
        string manufacturer = ReadManufacturerName();
        return String.IsNullOrEmpty(manufacturer) ? product : manufacturer + " " + product;
    }

    private static string ReadDeviceString(bool product)
    {
        try
        {
            using (SafeFileHandle handle = OpenDevice(FindDevice(), 0, 0))
            {
                var buffer = new StringBuilder(256);
                bool read = product
                    ? Native.HidD_GetProductString(handle, buffer, (uint)(buffer.Capacity * 2))
                    : Native.HidD_GetManufacturerString(handle, buffer, (uint)(buffer.Capacity * 2));
                if (!read)
                    return null;

                string value = buffer.ToString().Trim();
                return value.Length == 0 ? null : value;
            }
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is Win32Exception ||
                  error is UnauthorizedAccessException || error is InvalidOperationException ||
                  error is ArgumentException))
                throw;
            return null;
        }
    }

    public static string DescribeDevice()
    {
        string path;
        try
        {
            path = FindDevice();
        }
        catch (Exception error)
        {
            return "USB HID 枚举失败：" + error.Message;
        }

        string description = "已枚举 MSI MPG Ai1300TS 的 USB HID 接口（0DB0:AA6F）。";
        try
        {
            using (SafeFileHandle handle = OpenDevice(path, 0, 0))
            {
                CheckDevice(handle);
                description += Environment.NewLine + "HID 描述符验证通过（Usage Page FF00，输入和输出各 65 字节）。";
            }
        }
        catch (Exception error)
        {
            description += Environment.NewLine + "读取 HID 元数据失败：" + error.Message;
        }

        using (SafeWaitHandle handle = Native.OpenMutexW(Synchronize, false, MsiMutexName))
        {
            int error = Marshal.GetLastWin32Error();
            if (!handle.IsInvalid)
                return description + Environment.NewLine + "可打开与 MSI 软件共用的电源访问锁。";
            if (error == ErrorFileNotFound)
                return description + Environment.NewLine + "当前未创建 MSI 电源访问锁。";
            return description + Environment.NewLine + Win32Failure("打开 MSI 电源访问锁失败", error).Message;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            CloseDevice();
            if (msiMutex != null)
                msiMutex.Dispose();
        }
    }

    private void CloseDevice()
    {
        if (device == null)
            return;

        device.Dispose();
        device = null;
    }

    private static SafeWaitHandle OpenOrCreateMsiMutex()
    {
        SafeWaitHandle handle = Native.OpenMutexW(Synchronize, false, MsiMutexName);
        if (!handle.IsInvalid)
            return handle;

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == ErrorFileNotFound)
        {
            handle = Native.CreateMutexExW(IntPtr.Zero, MsiMutexName, 0, Synchronize);
            if (!handle.IsInvalid)
                return handle;

            error = Marshal.GetLastWin32Error();
            handle.Dispose();
        }

        throw Win32Failure("打开 MSI 电源访问锁失败", error);
    }

    private byte[] Exchange(byte command, byte register)
    {
        if (!Native.HidD_FlushQueue(device))
            throw Win32Failure("清理 HID 输入队列失败", Marshal.GetLastWin32Error());

        var request = new byte[ReportSize];
        request[1] = command;
        request[2] = register;
        string requestName = command == 0xFA ? "握手" : "读取寄存器 0x" + register.ToString("X2");

        if (Transfer(request, true, ResponseTimeoutMs) != ReportSize)
            throw new TimeoutException(requestName + "：HID 请求发送超时或未完整发送。");

        var elapsed = Stopwatch.StartNew();
        var response = new byte[ReportSize];
        int mismatchedReplies = 0;
        while (elapsed.ElapsedMilliseconds < ResponseTimeoutMs)
        {
            int remaining = Math.Max(1, ResponseTimeoutMs - (int)elapsed.ElapsedMilliseconds);
            int count = Transfer(response, false, remaining);
            if (count < 0)
                break;
            if (count != ReportSize || response[0] != 0 || response[1] != command || response[2] != register)
            {
                if (++mismatchedReplies == MaxMismatchedReplies)
                    throw new IOException(requestName + "：连续收到 " + MaxMismatchedReplies + " 个不匹配的 HID 回包。");
                continue;
            }
            if (response[3] == 0xFE && (command == 0xFA || Array.FindIndex(response, 4, value => value != 0) < 0))
                throw new IOException(requestName + "：电源暂时忙或拒绝了请求（0xFE）。");

            return response;
        }

        throw new TimeoutException(requestName + "：500 毫秒内未收到匹配的 HID 回包。");
    }

    private int Transfer(byte[] buffer, bool output, int timeoutMs)
    {
        var operation = new HidOperation(buffer);
        bool deferCleanup = false;
        try
        {
            bool completed = output
                ? Native.WriteFile(device, operation.Buffer, (uint)buffer.Length, IntPtr.Zero, operation.Overlapped)
                : Native.ReadFile(device, operation.Buffer, (uint)buffer.Length, IntPtr.Zero, operation.Overlapped);
            int error = Marshal.GetLastWin32Error();
            string action = output ? "发送 HID 查询失败" : "读取 HID 回包失败";

            if (!completed)
            {
                if (error != ErrorIoPending)
                    throw Win32Failure(action, error);

                uint wait = Native.WaitForSingleObject(operation.Event, (uint)timeoutMs);
                if (wait != WaitObject)
                {
                    int waitError = Marshal.GetLastWin32Error();
                    Native.CancelIoEx(device, operation.Overlapped);

                    // Cancellation is asynchronous. Keep native I/O memory alive
                    // until the kernel signals completion.
                    if (Native.WaitForSingleObject(operation.Event, 500) != WaitObject)
                    {
                        // The shared PSU mutex serializes the pending-operation check
                        // and registration. No further requests run until this event signals.
                        lock (CancellationGate)
                            pendingCancellation = operation;
                        deferCleanup = true;
                        throw new IOException(action + "：Windows 未及时确认取消，已关闭设备并等待重新连接。");
                    }

                    if (wait == WaitTimeout)
                        return -1;

                    throw Win32Failure(action, waitError);
                }
            }

            uint count;
            if (!Native.GetOverlappedResult(device, operation.Overlapped, out count, false))
                throw Win32Failure(action, Marshal.GetLastWin32Error());

            return (int)count;
        }
        finally
        {
            if (!deferCleanup)
                operation.Dispose();
        }
    }

    private static bool TryFinishPendingCancellation()
    {
        lock (CancellationGate)
        {
            if (pendingCancellation == null)
                return true;
            if (Native.WaitForSingleObject(pendingCancellation.Event, 0) != WaitObject)
                return false;

            pendingCancellation.Dispose();
            pendingCancellation = null;
            return true;
        }
    }

    private static SafeFileHandle OpenDevice(string path, uint access, uint flags)
    {
        const uint shareReadWrite = 3;
        const uint openExisting = 3;
        SafeFileHandle handle = Native.CreateFileW(path, access, shareReadWrite, IntPtr.Zero, openExisting, flags, IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw Win32Failure("打开 USB HID 电源设备失败", error);
    }

    private static void CheckDevice(SafeFileHandle handle)
    {
        var attributes = new Native.HidAttributes { Size = Marshal.SizeOf(typeof(Native.HidAttributes)) };
        if (!Native.HidD_GetAttributes(handle, ref attributes))
            throw Win32Failure("读取 HID 设备属性失败", Marshal.GetLastWin32Error());
        if (attributes.VendorId != VendorId || attributes.ProductId != ProductId)
            throw new IOException("当前 HID 设备标识与 MSI MPG Ai1300TS 不匹配。");

        IntPtr preparsed;
        if (!Native.HidD_GetPreparsedData(handle, out preparsed))
            throw Win32Failure("读取 HID 描述符失败", Marshal.GetLastWin32Error());
        try
        {
            Native.HidCaps caps;
            int status = Native.HidP_GetCaps(preparsed, out caps);
            if (status != 0x00110000)
                throw new IOException("读取 HID 报告长度失败（HID 状态 0x" + status.ToString("X8") + "）。");
            if (caps.UsagePage != UsagePage || caps.InputReportByteLength != ReportSize || caps.OutputReportByteLength != ReportSize)
                throw new IOException("HID 描述符与 MSI MPG Ai1300TS 的报告格式不匹配。");
        }
        finally
        {
            Native.HidD_FreePreparsedData(preparsed);
        }
    }

    private static string FindDevice()
    {
        Guid hid;
        Native.HidD_GetHidGuid(out hid);
        const uint presentDeviceInterfaces = 0x12;
        IntPtr devices = Native.SetupDiGetClassDevsW(ref hid, null, IntPtr.Zero, presentDeviceInterfaces);
        if (devices == new IntPtr(-1))
            throw Win32Failure("无法列出 Windows HID 设备", Marshal.GetLastWin32Error());

        var matches = new List<string>();
        string hardwareId = string.Format("#vid_{0:x4}&pid_{1:x4}#", VendorId, ProductId);
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new Native.DeviceInterfaceData { Size = Marshal.SizeOf(typeof(Native.DeviceInterfaceData)) };
                if (!Native.SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref hid, index, ref data))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                        break;
                    throw Win32Failure("无法枚举 Windows HID 接口", error);
                }

                string path = ReadDevicePath(devices, ref data);
                if (path.IndexOf(hardwareId, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add(path);
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(devices);
        }

        if (matches.Count == 0)
            throw new IOException("未找到 MSI MPG Ai1300TS（USB HID 0DB0:AA6F）。请检查电源的 USB 数据线。");
        if (matches.Count > 1)
            throw new IOException("找到多个 MSI MPG Ai1300TS HID 接口，无法确定要读取的电源。");

        return matches[0];
    }

    private static string ReadDevicePath(IntPtr devices, ref Native.DeviceInterfaceData data)
    {
        uint size;
        Native.SetupDiGetDeviceInterfaceDetailW(devices, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
        int error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer || size < 6)
            throw Win32Failure("无法获取 HID 设备路径长度", error);

        IntPtr detail = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            // SetupAPI's Unicode detail structure has platform-specific cbSize.
            // DevicePath starts after the DWORD cbSize on both platforms.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetailW(devices, ref data, detail, size, out size, IntPtr.Zero))
                throw Win32Failure("无法读取 HID 设备路径", Marshal.GetLastWin32Error());
            return Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static Exception Win32Failure(string action, int code)
    {
        string message = action + "（Win32 " + code + "：" + new Win32Exception(code).Message.TrimEnd('.', '。') + "）。";
        if (code == ErrorAccessDenied && action.IndexOf("访问锁", StringComparison.Ordinal) >= 0)
            message += "可在 Windows 桌面用管理员身份打开监控器后重试。";
        return new Win32Exception(code, message);
    }

    private sealed class HidOperation : IDisposable
    {
        private GCHandle pin;
        public SafeWaitHandle Event { get; private set; }
        public IntPtr Overlapped { get; private set; }
        public IntPtr Buffer { get { return pin.AddrOfPinnedObject(); } }

        public HidOperation(byte[] bytes)
        {
            try
            {
                Event = Native.CreateEventW(IntPtr.Zero, true, false, null);
                if (Event.IsInvalid)
                    throw Win32Failure("创建 HID 操作事件失败", Marshal.GetLastWin32Error());
                pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                Overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Overlapped)));
                var state = new Native.Overlapped { Event = Event.DangerousGetHandle() };
                Marshal.StructureToPtr(state, Overlapped, false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Overlapped != IntPtr.Zero)
                Marshal.FreeHGlobal(Overlapped);
            if (pin.IsAllocated)
                pin.Free();
            if (Event != null)
                Event.Dispose();
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInterfaceData
        {
            internal int Size;
            internal Guid InterfaceClassGuid;
            internal uint Flags;
            internal UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidAttributes
        {
            internal int Size;
            internal ushort VendorId;
            internal ushort ProductId;
            internal ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidCaps
        {
            internal ushort Usage;
            internal ushort UsagePage;
            internal ushort InputReportByteLength;
            internal ushort OutputReportByteLength;
            internal ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            internal ushort[] Reserved;
            internal ushort NumberLinkCollectionNodes;
            internal ushort NumberInputButtonCaps;
            internal ushort NumberInputValueCaps;
            internal ushort NumberInputDataIndices;
            internal ushort NumberOutputButtonCaps;
            internal ushort NumberOutputValueCaps;
            internal ushort NumberOutputDataIndices;
            internal ushort NumberFeatureButtonCaps;
            internal ushort NumberFeatureValueCaps;
            internal ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Overlapped
        {
            internal UIntPtr Internal;
            internal UIntPtr InternalHigh;
            internal uint Offset;
            internal uint OffsetHigh;
            internal IntPtr Event;
        }

        [DllImport("hid.dll", ExactSpelling = true)]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

        [DllImport("hid.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll", ExactSpelling = true)]
        internal static extern int HidP_GetCaps(IntPtr preparsed, out HidCaps caps);

        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FlushQueue(SafeFileHandle device);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetProductString(SafeFileHandle device, StringBuilder buffer, uint bufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetManufacturerString(SafeFileHandle device, StringBuilder buffer, uint bufferLength);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(ref Guid hid, string enumerator, IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid hid, uint index, ref DeviceInterfaceData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devices, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);

        [DllImport("setupapi.dll", ExactSpelling = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeWaitHandle OpenMutexW(uint access, bool inherit, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeWaitHandle CreateMutexExW(IntPtr attributes, string name, uint flags, uint access);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool ReleaseMutex(SafeWaitHandle handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeWaitHandle CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool WriteFile(SafeFileHandle device, IntPtr buffer, uint size, IntPtr count, IntPtr overlapped);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle device, IntPtr buffer, uint size, IntPtr count, IntPtr overlapped);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool GetOverlappedResult(SafeFileHandle device, IntPtr overlapped, out uint count, bool wait);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool CancelIoEx(SafeFileHandle device, IntPtr overlapped);
    }
}
