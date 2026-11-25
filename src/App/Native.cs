using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Comb.App
{
    /// <summary>界面侧用到的 Win32 API（引擎层有自己的一份，互不依赖）。</summary>
    static class Native
    {
        public const uint WM_QUIT = 0x0012;
        public const uint WM_POWERBROADCAST = 0x0218;
        public const int PBT_APMRESUMESUSPEND = 0x0007;
        public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 在当前线程建一个不可见的顶层窗口。顶层窗口收得到 WM_POWERBROADCAST 这类广播，
        /// message-only 窗口收不到。proc 必须由调用方保持引用，防止被 GC 回收。
        /// </summary>
        public static IntPtr CreateHiddenWindow(string className, WndProc proc)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
                lpfnWndProc = proc,
                hInstance = GetModuleHandle(null),
                lpszClassName = className
            };
            if (RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr hwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            return hwnd;
        }

        /// <summary>当前线程的消息循环，收到 WM_QUIT 返回。</summary>
        public static void RunMessageLoop()
        {
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        /// <summary>
        /// 把窗口切到前台。热键由低级钩子触发时我们不是前台进程，SetForegroundWindow 会被系统拦下，
        /// 所以临时挂到当前前台线程的输入状态上再切。
        /// </summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return;
            uint pid;
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid);
            uint self = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(self, fgThread, false);
            }
            if (GetForegroundWindow() == hwnd) return;

            // 前台锁仍然拦着：系统只允许"最后收到输入"的进程切前台。
            // 由本进程发一次 Alt 按下，切前台，再发 Alt 松开（松开落在我们自己的窗口上，不会触发别的程序的菜单）
            SendAltKey(false);
            SetForegroundWindow(hwnd);
            SendAltKey(true);
        }

        static void SendAltKey(bool up)
        {
            var input = new INPUT[1];
            input[0].type = 1; // INPUT_KEYBOARD
            input[0].u.ki.wVk = 0x12; // VK_MENU
            input[0].u.ki.dwFlags = up ? 2u : 0u; // KEYEVENTF_KEYUP
            SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
        }

        // INPUT 里键盘和鼠标共用一个联合体；联合体按鼠标结构的大小占位，保证 cbSize 正确，32/64 位都对齐
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);

        /// <summary>Windows 11 的 DWM 圆角。Windows 10 上不支持，调用无效果，属预期。</summary>
        public static void SetRoundCorners(IntPtr hwnd)
        {
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
        }

        /// <summary>
        /// 把文本放进剪贴板（CF_UNICODETEXT）。不用 WPF 的 Clipboard.SetText：它在 OleFlushClipboard 时
        /// 常撞上剪贴板历史等程序正占着剪贴板，文本其实已写入却抛 CLIPBRD_E_CANT_OPEN（2026-11-21 实测）。
        /// 这里直接用 Win32，打不开就短暂重试；最终失败抛 Win32Exception。
        /// </summary>
        public static void SetClipboardText(IntPtr owner, string text)
        {
            for (int attempt = 0; !OpenClipboard(owner); attempt++)
            {
                if (attempt >= 10) throw new Win32Exception(Marshal.GetLastWin32Error());
                System.Threading.Thread.Sleep(30);
            }
            try
            {
                if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
                int bytes = (text.Length + 1) * 2;
                IntPtr mem = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (UIntPtr)bytes);
                if (mem == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr p = GlobalLock(mem);
                Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                Marshal.WriteInt16(p, text.Length * 2, 0);
                GlobalUnlock(mem);
                if (SetClipboardData(13 /* CF_UNICODETEXT */, mem) == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    GlobalFree(mem);   // 失败时内存还归我们，要自己释放；成功后归系统
                    throw new Win32Exception(error);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>
        /// 这个文件的格式在本机有没有默认打开方式。查注册表关联，不用 HKCR\Unknown 兜底：
        /// 否则没关联的格式也会被当成「有」，打开时弹「你要如何打开这个文件」。
        /// 传统程序关联的是命令行（ASSOCSTR_COMMAND），「照片」这类商店应用只有 DelegateExecute，两种都要查
        /// （2026-11-21 实测：.gif/.jpg/.png 只有 DelegateExecute；.pth/.safetensors/.jsonl 都没有关联）。
        /// </summary>
        public static bool HasDefaultOpenAction(string path)
        {
            string ext = System.IO.Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            return QueryAssoc(ext, 1 /* ASSOCSTR_COMMAND */) || QueryAssoc(ext, 18 /* ASSOCSTR_DELEGATEEXECUTE */);
        }

        static bool QueryAssoc(string ext, int what)
        {
            var buffer = new System.Text.StringBuilder(1024);
            int length = buffer.Capacity;
            int hr = AssocQueryString(0x20 /* ASSOCF_NOTRUNCATE */ | 0x400 /* ASSOCF_INIT_IGNOREUNKNOWN */, what, ext, null, buffer, ref length);
            // E_POINTER 只是缓冲区不够长，说明关联存在
            return hr >= 0 || hr == unchecked((int)0x80004003);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int AssocQueryString(int flags, int str, string assoc, string extra, System.Text.StringBuilder outBuffer, ref int outLength);

        /// <summary>
        /// 取 exe 自带的图标（构建时由 tools/IconGen.cs 生成，build.ps1 用 /win32icon 嵌入）。
        /// 托盘用系统小图标尺寸；系统从图标组里挑最接近的一张，高缩放下也清晰。取不到时退回系统默认应用图标。
        /// </summary>
        public static System.Drawing.Icon LoadOwnSmallIcon()
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
            ExtractIconEx(exe, 0, large, small, 1);
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            // 小图标句柄随进程存活：只取一次，不释放
            return small[0] != IntPtr.Zero ? System.Drawing.Icon.FromHandle(small[0]) : System.Drawing.SystemIcons.Application;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint ExtractIconEx(string file, int index, IntPtr[] largeIcons, IntPtr[] smallIcons, uint count);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetClipboardData(uint format, IntPtr mem);

        [DllImport("user32.dll")]
        static extern bool CloseClipboard();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalLock(IntPtr mem);

        [DllImport("kernel32.dll")]
        static extern bool GlobalUnlock(IntPtr mem);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr mem);

        /// <summary>
        /// DWM 隐身（DWMWA_CLOAK）：窗口照常渲染、只是不显示。用来把切换形态时的中间帧藏起来。
        /// Windows 8 以下不支持，调用无效果，属预期。
        /// </summary>
        public static void SetCloaked(IntPtr hwnd, bool cloaked)
        {
            int value = cloaked ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
        }

        /// <summary>一次调用同时改位置和大小（设备像素）。</summary>
        public static void MoveWindow(IntPtr hwnd, int x, int y, int width, int height)
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x0004 /* SWP_NOZORDER */ | 0x0010 /* SWP_NOACTIVATE */);
        }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
                                            int x, int y, int width, int height, IntPtr parent, IntPtr menu,
                                            IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
