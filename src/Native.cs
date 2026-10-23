using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ChargeLimit
{
    struct BatteryState
    {
        public bool Online, Charging, Discharging;
        public int Percent, RateMw;
    }

    // Talks to two drivers:
    //  - the Windows battery class (battc.sys) for read-only status, and
    //  - the Qualcomm PMIC GLink driver (qcpmicglink*.sys), whose \\.\QCOMBATTMGR device turns
    //    IOCTL 0x80092044 into the battery-manager firmware request BATTMGR_CHG_CTRL_LIMIT_EN
    //    (opcode 0x48, payload {enable, target_soc, delta_soc}). It is the same request Linux's
    //    qcom_battmgr driver sends for charge_control_end_threshold.
    static class Native
    {
        public const string BattMgrDevice = @"\\.\QCOMBATTMGR";
        const uint IOCTL_QCOM_BATTMGR_CHG_CTRL_LIMIT = 0x80092044;

        const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
        const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
        const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
        const int BATTERY_POWER_ON_LINE = 0x1, BATTERY_DISCHARGING = 0x2, BATTERY_CHARGING = 0x4;
        const int BatteryInformationLevel = 0, FullChargedCapacityOffset = 16;

        const uint GENERIC_READ_WRITE = 0xC0000000, FILE_SHARE_READ_WRITE = 3, OPEN_EXISTING = 3;
        const uint DIGCF_PRESENT_INTERFACE = 0x12;
        static readonly IntPtr InvalidHandle = new IntPtr(-1);
        static Guid GUID_DEVICE_BATTERY = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("setupapi", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi", SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int requiredSize, IntPtr info);

        [DllImport("setupapi")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static string batteryPath;

        // Throws Win32Exception with ERROR_FILE_NOT_FOUND (2) when the driver is absent
        // and ERROR_ACCESS_DENIED (5) when not running elevated.
        public static void CheckChargeControlAvailable()
        {
            CloseHandle(Open(BattMgrDevice));
        }

        public static void SetChargeLimit(int targetSoc, int deltaSoc)
        {
            byte[] request = new byte[12];
            BitConverter.GetBytes(1).CopyTo(request, 0);
            BitConverter.GetBytes(targetSoc).CopyTo(request, 4);
            BitConverter.GetBytes(deltaSoc).CopyTo(request, 8);
            IntPtr device = Open(BattMgrDevice);
            try { Ioctl(device, IOCTL_QCOM_BATTMGR_CHG_CTRL_LIMIT, request, 0); }
            finally { CloseHandle(device); }
        }

        public static BatteryState ReadBattery()
        {
            if (batteryPath == null)
                batteryPath = FindBattery();
            IntPtr device = Open(batteryPath);
            try
            {
                uint tag = BitConverter.ToUInt32(Ioctl(device, IOCTL_BATTERY_QUERY_TAG, new byte[4], 4), 0);

                byte[] query = new byte[12];
                BitConverter.GetBytes(tag).CopyTo(query, 0);
                BitConverter.GetBytes(BatteryInformationLevel).CopyTo(query, 4);
                byte[] info = Ioctl(device, IOCTL_BATTERY_QUERY_INFORMATION, query, 36);
                uint fullCapacity = BitConverter.ToUInt32(info, FullChargedCapacityOffset);

                byte[] wait = new byte[20];
                BitConverter.GetBytes(tag).CopyTo(wait, 0);
                byte[] status = Ioctl(device, IOCTL_BATTERY_QUERY_STATUS, wait, 16);
                int power = BitConverter.ToInt32(status, 0);
                uint capacity = BitConverter.ToUInt32(status, 4);

                BatteryState state = new BatteryState();
                state.Online = (power & BATTERY_POWER_ON_LINE) != 0;
                state.Discharging = (power & BATTERY_DISCHARGING) != 0;
                state.Charging = (power & BATTERY_CHARGING) != 0;
                state.RateMw = BitConverter.ToInt32(status, 12);
                state.Percent = fullCapacity > 0 ? (int)Math.Round(capacity * 100.0 / fullCapacity) : -1;
                return state;
            }
            finally { CloseHandle(device); }
        }

        static IntPtr Open(string path)
        {
            IntPtr handle = CreateFileW(path, GENERIC_READ_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == InvalidHandle)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        }

        static byte[] Ioctl(IntPtr device, uint code, byte[] input, int outputSize)
        {
            byte[] output = new byte[outputSize];
            int returned;
            if (!DeviceIoControl(device, code, input, input == null ? 0 : input.Length, output, outputSize, out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return output;
        }

        static string FindBattery()
        {
            IntPtr set = SetupDiGetClassDevsW(ref GUID_DEVICE_BATTERY, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT_INTERFACE);
            try
            {
                SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(data);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GUID_DEVICE_BATTERY, 0, ref data))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                int size;
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, 8); // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) on 64-bit
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out size, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    return Marshal.PtrToStringUni(detail + 4);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }
}
