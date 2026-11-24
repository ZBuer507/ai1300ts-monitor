using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class GpuLevelReading
{
    private const double DegreesPerRadian = 180.0 / Math.PI;

    internal GpuLevelReading(int x, int y, int z, string model)
    {
        RawX = x;
        RawY = y;
        RawZ = z;
        Model = model;
    }

    internal int RawX { get; private set; }
    internal int RawY { get; private set; }
    internal int RawZ { get; private set; }
    internal string Model { get; private set; }

    internal double PitchDegrees { get { return Math.Atan2(RawX, -RawZ) * DegreesPerRadian; } }
    internal double RollDegrees { get { return Math.Atan2(RawY, -RawZ) * DegreesPerRadian; } }

    internal double DeviationFrom(GpuLevelReading baseline)
    {
        if (baseline == null)
            throw new ArgumentNullException("baseline");

        // Compare gravity vectors so vertical mounting and angle wrapping work.
        double ax = RawX, ay = RawY, az = RawZ;
        double bx = baseline.RawX, by = baseline.RawY, bz = baseline.RawZ;
        double cx = ay * bz - az * by;
        double cy = az * bx - ax * bz;
        double cz = ax * by - ay * bx;
        double cross = Math.Sqrt(cx * cx + cy * cy + cz * cz);
        double dot = ax * bx + ay * by + az * bz;
        return Math.Atan2(cross, dot) * DegreesPerRadian;
    }
}

internal sealed class GpuLevelSense : IDisposable
{
    private const uint ExpectedPciId = 0x2B8510DE;
    private const uint ExpectedSubsystemId = 0x8A8C1043;
    private const int MaxPhysicalGpus = 64;
    private const int NvSuccess = 0;
    private const int NvInvalidHandle = -8;
    private const int NvHandleInvalidated = -10;
    private const byte SensorAddress = 0x2B << 1;
    private const byte SensorPort = 1;
    private const byte ChipIdRegister = 0x21;
    private const byte ChipId = 0x89;
    private const byte FirstAxisRegister = 0x70;
    private const int AxisByteCount = 6;
    private const uint ReadInfoVersion = 0x00030040;

    // NVAPI is shared by reader instances within this process.
    private static readonly object DriverLock = new object();

    private NvStatusCall unload;
    private NvEnumerate enumerate;
    private NvPciIds pciIds;
    private NvName getName;
    private NvI2cRead readI2c;
    private bool initialized;
    private bool disposed;
    private IntPtr adapter;
    private string model;

    internal GpuLevelReading Read()
    {
        lock (DriverLock)
        {
            if (disposed)
                throw new ObjectDisposedException("GpuLevelSense");

            EnsureInitialized();
            IntPtr registers = Marshal.AllocHGlobal(AxisByteCount + 1);
            try
            {
                if (adapter == IntPtr.Zero)
                    IdentifySensor(registers);

                byte[] axes = ReadRegisters(adapter, FirstAxisRegister, AxisByteCount, registers);
                return Decode(axes, model);
            }
            finally
            {
                Marshal.FreeHGlobal(registers);
            }
        }
    }

    internal static GpuLevelReading Decode(byte[] axes, string model)
    {
        if (axes == null || axes.Length != AxisByteCount)
            throw new ArgumentException("显卡姿态数据应包含六个字节。", "axes");

        int x = ReadSignedWord(axes, 0);
        int y = ReadSignedWord(axes, 2);
        int z = ReadSignedWord(axes, 4);
        if ((x == 0 && y == 0 && z == 0) || (x == -1 && y == -1 && z == -1))
            throw new InvalidOperationException("显卡姿态传感器尚未返回有效的重力向量。");

        return new GpuLevelReading(x, y, z, model);
    }

    public void Dispose()
    {
        lock (DriverLock)
        {
            if (disposed)
                return;

            disposed = true;
            adapter = IntPtr.Zero;
            if (initialized)
            {
                unload();
                initialized = false;
            }
        }
    }

    internal static bool HasExpectedReadLayout()
    {
        Type type = typeof(I2cReadInfo);
        return Marshal.SizeOf(type) == 64 &&
            Marshal.OffsetOf(type, "Address").ToInt32() == 9 &&
            Marshal.OffsetOf(type, "RegisterPointer").ToInt32() == 16 &&
            Marshal.OffsetOf(type, "RegisterSize").ToInt32() == 24 &&
            Marshal.OffsetOf(type, "DataPointer").ToInt32() == 32 &&
            Marshal.OffsetOf(type, "Size").ToInt32() == 40 &&
            Marshal.OffsetOf(type, "SpeedKhz").ToInt32() == 48 &&
            Marshal.OffsetOf(type, "Port").ToInt32() == 52 &&
            Marshal.OffsetOf(type, "UsePort").ToInt32() == 56;
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        if (!Environment.Is64BitProcess || !HasExpectedReadLayout())
            throw new InvalidOperationException("显卡姿态读取需要 64 位 NVIDIA 驱动接口。");

        try
        {
            var initialize = Bind<NvStatusCall>(0x0150E828, "NvAPI_Initialize");
            unload = Bind<NvStatusCall>(0xD22BDD7E, "NvAPI_Unload");
            enumerate = Bind<NvEnumerate>(0xE5AC921F, "NvAPI_EnumPhysicalGPUs");
            pciIds = Bind<NvPciIds>(0x2DDFB66E, "NvAPI_GPU_GetPCIIdentifiers");
            getName = Bind<NvName>(0xCEEE8E9F, "NvAPI_GPU_GetFullName");
            readI2c = Bind<NvI2cRead>(0x4D7B0709, "NvAPI_I2CReadEx");
            CheckStatus(initialize(), "初始化 NVIDIA 驱动接口");
            initialized = true;
        }
        catch (DllNotFoundException error)
        {
            throw new InvalidOperationException("找不到 64 位 NVIDIA 驱动接口。", error);
        }
        catch (EntryPointNotFoundException error)
        {
            throw new InvalidOperationException("当前 NVIDIA 驱动未提供 NVAPI 入口。", error);
        }
        catch (BadImageFormatException error)
        {
            throw new InvalidOperationException("NVIDIA 驱动接口的位数与程序不一致。", error);
        }
    }

    private void IdentifySensor(IntPtr registers)
    {
        var handles = new IntPtr[MaxPhysicalGpus];
        uint count;
        CheckStatus(enumerate(handles, out count), "枚举 NVIDIA 显卡");
        if (count > handles.Length)
            throw new InvalidOperationException("NVIDIA 驱动返回的显卡数量无效。");

        IntPtr match = IntPtr.Zero;
        for (int index = 0; index < count; index++)
        {
            uint device, subsystem, revision, external;
            IntPtr handle = handles[index];
            if (handle == IntPtr.Zero ||
                pciIds(handle, out device, out subsystem, out revision, out external) != NvSuccess ||
                device != ExpectedPciId || subsystem != ExpectedSubsystemId)
                continue;

            if (match != IntPtr.Zero)
                throw new InvalidOperationException("检测到多张同型号显卡，无法唯一识别姿态传感器。");
            match = handle;
        }

        if (match == IntPtr.Zero)
            throw new InvalidOperationException("未检测到本机支持的 ASUS RTX 5090 显卡（SUBSYS 8A8C1043）。");
        if (ReadRegisters(match, ChipIdRegister, 1, registers)[0] != ChipId)
            throw new InvalidOperationException("显卡板载控制器的标识与预期不符。");

        var name = new StringBuilder(64);
        model = getName(match, name) == NvSuccess && name.Length != 0
            ? name.ToString()
            : "ASUS RTX 5090";
        adapter = match;
    }

    private byte[] ReadRegisters(IntPtr handle, byte register, int length, IntPtr registers)
    {
        IntPtr response = IntPtr.Add(registers, 1);
        Marshal.WriteByte(registers, register);
        for (int index = 0; index < length; index++)
            Marshal.WriteByte(response, index, 0xFF);
        var info = new I2cReadInfo
        {
            Version = ReadInfoVersion,
            Address = SensorAddress,
            RegisterPointer = registers,
            RegisterSize = 1,
            DataPointer = response,
            Size = (uint)length,
            Speed = 0xFFFF,
            SpeedKhz = 4, // NVAPI_I2C_SPEED_100KHZ
            Port = SensorPort,
            UsePort = 1
        };
        uint extra = 0;
        int status = readI2c(handle, ref info, ref extra);
        if (status == NvInvalidHandle || status == NvHandleInvalidated)
            adapter = IntPtr.Zero;
        if (status != NvSuccess)
            CheckStatus(status, "读取显卡姿态寄存器 0x" + register.ToString("X2", CultureInfo.InvariantCulture));
        var values = new byte[length];
        Marshal.Copy(response, values, 0, length);
        return values;
    }

    private static short ReadSignedWord(byte[] data, int index)
    {
        return unchecked((short)((data[index] << 8) | data[index + 1]));
    }

    private static T Bind<T>(uint interfaceId, string name) where T : class
    {
        IntPtr address = QueryInterface(interfaceId);
        if (address == IntPtr.Zero)
            throw new InvalidOperationException("当前 NVIDIA 驱动未提供 " + name + "。");
        return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
    }

    private static void CheckStatus(int status, string operation)
    {
        if (status != NvSuccess)
            throw new InvalidOperationException(operation + "失败（NVAPI " +
                status.ToString(CultureInfo.InvariantCulture) + "）。");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct I2cReadInfo
    {
        internal uint Version;
        internal uint DisplayMask;
        internal byte IsDdcPort;
        internal byte Address;
        internal IntPtr RegisterPointer;
        internal uint RegisterSize;
        internal IntPtr DataPointer;
        internal uint Size;
        internal uint Speed;
        internal uint SpeedKhz;
        internal byte Port;
        internal uint UsePort;
    }

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr QueryInterface(uint interfaceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvStatusCall();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvEnumerate([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = MaxPhysicalGpus)]
        IntPtr[] handles, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvPciIds(IntPtr gpu, out uint device, out uint subsystem, out uint revision, out uint external);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int NvName(IntPtr gpu, StringBuilder name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvI2cRead(IntPtr gpu, ref I2cReadInfo info, ref uint extra);
}
