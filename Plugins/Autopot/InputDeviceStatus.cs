using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Autopot
{
    internal static class InputDeviceStatus
    {
        private const string ViGEmServiceName = "ViGEmBus";
        private const string ViGEmDownloadUrl = "https://github.com/ViGEm/ViGEmBus/releases";

        public static bool IsViGEmBusInstalled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ViGEmServiceName}");
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsControllerConnected()
        {
            var state = new XINPUT_STATE();
            return XInputGetState(0, ref state) == 0;
        }

        public static string ViGEmDownloadLink => ViGEmDownloadUrl;

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState(int dwUserIndex, ref XINPUT_STATE pState);
    }
}
