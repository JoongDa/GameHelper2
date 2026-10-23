namespace CraftingHelper
{
    using System;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using GameHelper;
    using PickupHelper;

    /// <summary>Local safety checks around the source-linked PickupHelper input helpers.</summary>
    internal static class InputAdapter
    {
        internal static bool ShiftHeld => Native.IsKeyDown(0x10);
        internal static bool LeftButtonHeld => Native.IsKeyDown(0x01);
        internal static bool RightButtonHeld => Native.IsKeyDown(0x02);
        internal static bool ControlHeld => Native.IsKeyDown(0x11);
        internal static bool AltHeld => Native.IsKeyDown(0x12);
        internal static bool EscapeHeld => Native.IsKeyDown(0x1B);

        internal static bool IsGameForeground
        {
            get
            {
                var gamePid = Core.Process.Pid;
                if (gamePid == 0) return false;
                var window = GetForegroundWindow();
                return window != IntPtr.Zero &&
                    GetWindowThreadProcessId(window, out var foregroundPid) != 0 && foregroundPid == gamePid;
            }
        }

        internal static bool HasConflictingInput =>
            ControlHeld || AltHeld || EscapeHeld || LeftButtonHeld || RightButtonHeld;

        internal static bool TryGetCursorPosition(out Vector2 position)
        {
            if (GetCursorPos(out var point))
            {
                position = new Vector2(point.X, point.Y);
                return true;
            }

            position = default;
            return false;
        }

        internal static bool TryGetClientCursorPosition(out Vector2 position)
        {
            position = default;
            var gamePid = Core.Process.Pid;
            var window = GetForegroundWindow();
            if (gamePid == 0 || window == IntPtr.Zero ||
                GetWindowThreadProcessId(window, out var foregroundPid) == 0 || foregroundPid != gamePid ||
                !GetCursorPos(out var point) || !ScreenToClient(window, ref point)) return false;

            position = new Vector2(point.X, point.Y);
            return true;
        }

        internal static bool TryLeftClick()
        {
            // Check again immediately before native input; Core.Process.Foreground is cached.
            if (!ShiftHeld || !IsGameForeground || HasConflictingInput || Native.DisableNativeInput) return false;
            Native.LeftClick();
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorPoint
        {
            internal int X;
            internal int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out CursorPoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ScreenToClient(IntPtr window, ref CursorPoint point);
    }
}
