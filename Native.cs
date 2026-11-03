using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceDictate
{
    /// <summary>Функции Windows: горячая клавиша, активное окно, вставка текста.</summary>
    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int SW_RESTORE = 9;

        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_SHIFT = 0x10;
        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_MENU = 0x12;
        public const ushort VK_LWIN = 0x5B;
        public const ushort VK_RWIN = 0x5C;
        public const ushort VK_V = 0x56;
        
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        // Объединение должно содержать MOUSEINPUT, иначе размер INPUT будет неверным
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        private static INPUT Key(ushort vk, ushort scan, uint flags)
        {
            var input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.U.ki.wVk = vk;
            input.U.ki.wScan = scan;
            if (vk == VK_LWIN || vk == VK_RWIN) flags |= KEYEVENTF_EXTENDEDKEY;
            input.U.ki.dwFlags = flags;
            return input;
        }

        /// <summary>Сделать окно активным, даже если Windows сопротивляется смене фокуса.</summary>
        public static void ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            if (SetForegroundWindow(hWnd) && GetForegroundWindow() == hWnd) return;

            IntPtr fg = GetForegroundWindow();
            uint pid;
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid);
            uint myThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
            try
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }
        }

        private static void Send(INPUT[] inputs)
        {
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>Нажать сочетание, например Ctrl+V: все вниз, затем вверх в обратном порядке.</summary>
        public static void SendChord(params ushort[] vks)
        {
            var inputs = new INPUT[vks.Length * 2];
            for (int i = 0; i < vks.Length; i++)
                inputs[i] = Key(vks[i], 0, 0);
            for (int i = 0; i < vks.Length; i++)
                inputs[vks.Length + i] = Key(vks[vks.Length - 1 - i], 0, KEYEVENTF_KEYUP);
            Send(inputs);
        }

        /// <summary>Напечатать один символ как Unicode (не зависит от раскладки).</summary>
        public static void TypeChar(char c)
        {
            if (c == '\r') return;
            if (c == '\n')
            {
                SendChord(VK_RETURN);
                return;
            }
            Send(new[]
            {
                Key(0, c, KEYEVENTF_UNICODE),
                Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)
            });
        }

        public static bool IsKeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        public static bool IsOwnWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return pid == (uint)Environment.ProcessId;
        }

        public static string GetTitle(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            string t = sb.ToString();
            return t.Length == 0 ? L.T("(окно без заголовка)", "(untitled window)") : t;
        }
    }
}
