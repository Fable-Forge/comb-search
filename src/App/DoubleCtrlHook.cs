using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Comb.App
{
    /// <summary>
    /// 双击 Ctrl 热键：WH_KEYBOARD_LL 低级键盘钩子，跑在独立线程和消息循环上。
    /// Ctrl 单独按下又松开两次、间隔在 Interval 内算一次触发；中间按了别的键就重置。
    /// 睡眠唤醒后系统可能悄悄丢掉低级钩子，所以收到 WM_POWERBROADCAST 唤醒通知时重新挂钩子
    /// （同一线程上建一个隐藏顶层窗口来收这个广播）。
    /// </summary>
    sealed class DoubleCtrlHook
    {
        const uint Interval = 350;    // 毫秒：每次按住的时长上限，以及第一次松开到第二次按下的间隔上限
        const int VK_CONTROL = 0x11, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;

        readonly Action onTrigger;
        Native.HookProc hookProc;     // 保持引用，防止被 GC 回收
        Native.WndProc windowProc;
        IntPtr hook;
        uint threadId;

        // 状态机（只在钩子线程上访问）
        bool ctrlDown;        // Ctrl 正按着
        bool ctrlClean;       // 这次按住期间没有按别的键
        uint ctrlDownTime;
        int taps;             // 已完成的干净单击次数（0 或 1）
        uint lastTapUpTime;

        public DoubleCtrlHook(Action onTrigger)
        {
            this.onTrigger = onTrigger;
        }

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "DoubleCtrlHook" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public void Stop()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        void Run()
        {
            threadId = Native.GetCurrentThreadId();
            hookProc = HookCallback;
            windowProc = WindowProc;
            try
            {
                Native.CreateHiddenWindow("Comb.PowerWatcher." + System.Diagnostics.Process.GetCurrentProcess().Id, windowProc);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 建不了收唤醒广播的窗口：钩子照样挂，只是睡眠唤醒后不会自动重挂（降级运行，不影响平时使用）
            }
            Install();
            Native.RunMessageLoop();
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        }

        void Install()
        {
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(null), 0);
            ctrlDown = false;
            taps = 0;
        }

        IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_POWERBROADCAST)
            {
                int ev = wParam.ToInt32();
                if (ev == Native.PBT_APMRESUMEAUTOMATIC || ev == Native.PBT_APMRESUMESUSPEND) Install();
                return (IntPtr)1;
            }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                // KBDLLHOOKSTRUCT：vkCode(0) scanCode(4) flags(8) time(12) dwExtraInfo(16)
                int vk = Marshal.ReadInt32(lParam, 0);
                uint time = unchecked((uint)Marshal.ReadInt32(lParam, 12));
                int msg = wParam.ToInt32();
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                bool isCtrl = vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL;

                if (isCtrl)
                {
                    if (down)
                    {
                        if (!ctrlDown)   // 按住时的自动重复不算新的一次按下
                        {
                            ctrlDown = true;
                            ctrlClean = true;
                            ctrlDownTime = time;
                            if (taps == 1 && unchecked(time - lastTapUpTime) > Interval) taps = 0;
                        }
                    }
                    else if (up && ctrlDown)
                    {
                        ctrlDown = false;
                        bool shortTap = ctrlClean && unchecked(time - ctrlDownTime) <= Interval;
                        if (!shortTap) taps = 0;
                        else if (taps == 1)
                        {
                            taps = 0;
                            onTrigger();
                        }
                        else
                        {
                            taps = 1;
                            lastTapUpTime = time;
                        }
                    }
                }
                else if (down)
                {
                    // 中间按了别的键：重置
                    taps = 0;
                    ctrlClean = false;
                }
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }
    }
}
