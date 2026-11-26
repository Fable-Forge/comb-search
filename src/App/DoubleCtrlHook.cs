using System;
using System.ComponentModel;
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
        const uint WM_RETRY_HOOK = 0x8001;
        const int MaxAttempts = 3;   // 首次尝试 + 最多两次延迟重试；不在钩子线程 Sleep
        const int ERROR_INVALID_HOOK_HANDLE = 1404;

        public sealed class Status
        {
            public bool Available { get; private set; }
            public bool WakeMonitoringAvailable { get; private set; }
            public bool Retrying { get; private set; }
            public bool CanRetry { get; private set; }
            public int ErrorCode { get; private set; }
            public int WakeErrorCode { get; private set; }
            public string Message { get; private set; }

            public Status(bool available, bool wakeMonitoring, bool retrying, bool canRetry,
                          int errorCode, int wakeErrorCode, string message)
            {
                Available = available;
                WakeMonitoringAvailable = wakeMonitoring;
                Retrying = retrying;
                CanRetry = canRetry;
                ErrorCode = errorCode;
                WakeErrorCode = wakeErrorCode;
                Message = message;
            }
        }

        readonly Action onTrigger;
        readonly Action<Status> onStatus;
        readonly object lifecycle = new object();
        Native.HookProc hookProc;     // 保持引用，防止被 GC 回收
        Native.WndProc windowProc;
        IntPtr hook;
        IntPtr powerWindow;
        string windowClass;
        volatile uint threadId;
        volatile bool stopping;
        bool started;
        UIntPtr retryTimer;
        int attempts;
        int wakeError;
        bool available;
        bool needsInstall;

        // 状态机（只在钩子线程上访问）
        bool ctrlDown;        // Ctrl 正按着
        bool ctrlClean;       // 这次按住期间没有按别的键
        uint ctrlDownTime;
        int taps;             // 已完成的干净单击次数（0 或 1）
        uint lastTapUpTime;

        public DoubleCtrlHook(Action onTrigger, Action<Status> onStatus = null)
        {
            this.onTrigger = onTrigger;
            this.onStatus = onStatus;
        }

        public void Start()
        {
            lock (lifecycle)
            {
                if (started || stopping) return;
                started = true;
                var t = new Thread(Run) { IsBackground = true, Name = "DoubleCtrlHook" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }
        }

        public void Stop()
        {
            uint id;
            lock (lifecycle)
            {
                // 与安装串行：Stop 返回后，即使队列里还有旧定时器消息，也不会再挂钩。
                stopping = true;
                id = threadId;
            }
            if (id != 0) Native.PostThreadMessage(id, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        public bool RequestRetry()
        {
            lock (lifecycle)
            {
                return !stopping && threadId != 0 &&
                    Native.PostThreadMessage(threadId, WM_RETRY_HOOK, IntPtr.Zero, IntPtr.Zero);
            }
        }

        void Run()
        {
            hookProc = HookCallback;
            windowProc = WindowProc;
            try
            {
                Native.EnsureMessageQueue();
                lock (lifecycle)
                {
                    if (stopping) return;
                    threadId = Native.GetCurrentThreadId();
                }
                windowClass = "Comb.PowerWatcher." + System.Diagnostics.Process.GetCurrentProcess().Id + "." + threadId;
                BeginInstallCycle();
                if (stopping) return;
                int error = Native.RunMessageLoop(HandleThreadMessage);
                CancelRetry();
                if (!stopping) Publish(false, error, "热键监听已停止，请退出后重新启动 Comb。", false);
            }
            catch (Exception ex)
            {
                // 后台线程异常不会进入 WPF 的 DispatcherUnhandledException。
                // 仅停用热键，搜索和托盘仍由 UI 线程提供。
                var win32 = ex as Win32Exception;
                CancelRetry();
                if (!stopping) Publish(false, win32 == null ? 0 : win32.NativeErrorCode,
                    "热键监听已停止：" + ex.Message + "。请退出后重新启动 Comb。", false);
            }
            finally
            {
                lock (lifecycle) threadId = 0;
                CancelRetry();
                if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
                if (powerWindow != IntPtr.Zero) Native.DestroyHiddenWindow(powerWindow, windowClass);
                powerWindow = IntPtr.Zero;
            }
        }

        void BeginInstallCycle()
        {
            if (stopping) return;
            CancelRetry();
            attempts = 0;
            needsInstall = true;
            AttemptInstall();
        }

        void AttemptInstall()
        {
            int error = 0;
            string message = "";
            lock (lifecycle)
            {
                if (stopping) return;
                attempts++;
                if (powerWindow == IntPtr.Zero)
                {
                    try
                    {
                        powerWindow = Native.CreateHiddenWindow(windowClass, windowProc);
                        wakeError = 0;
                    }
                    catch (Win32Exception ex)
                    {
                        wakeError = ex.NativeErrorCode;
                    }
                }

                if (needsInstall)
                {
                    bool canInstall = true;
                    if (hook != IntPtr.Zero)
                    {
                        bool removed = Native.UnhookWindowsHookEx(hook);
                        int removeError = removed ? 0 : Native.LastError();
                        // 系统可能已移除旧钩子；其它卸载失败不能盲目再挂一个，避免重复回调。
                        if (removed || removeError == ERROR_INVALID_HOOK_HANDLE) hook = IntPtr.Zero;
                        else
                        {
                            canInstall = false;
                            error = removeError;
                            message = "重新启用前释放旧热键失败";
                        }
                    }
                    available = false;
                    if (canInstall)
                    {
                        IntPtr installed = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc,
                            Native.GetModuleHandle(null), 0);
                        error = installed == IntPtr.Zero ? Native.LastError() : 0;
                        hook = installed;
                        available = installed != IntPtr.Zero;
                        needsInstall = !available;
                        ctrlDown = false;
                        ctrlClean = false;
                        taps = 0;
                        if (!available) message = "双击 Ctrl 热键安装失败";
                    }
                }
                if (available && powerWindow == IntPtr.Zero)
                {
                    error = wakeError;
                    message = "双击 Ctrl 可用，但唤醒监视窗口创建失败；睡眠唤醒后不能自动恢复";
                }
                if ((!available || powerWindow == IntPtr.Zero) && attempts < MaxAttempts)
                {
                    retryTimer = Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, attempts == 1 ? 500u : 1500u, IntPtr.Zero);
                    if (retryTimer == UIntPtr.Zero)
                    {
                        int timerError = Native.LastError();
                        message += "；无法安排自动重试（错误 " + timerError + "）";
                    }
                }
            }
            Publish(available, error, message, true);
        }

        void CancelRetry()
        {
            if (retryTimer == UIntPtr.Zero) return;
            Native.KillTimer(IntPtr.Zero, retryTimer);
            retryTimer = UIntPtr.Zero;
        }

        void HandleThreadMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (stopping) return;
            if (msg == WM_RETRY_HOOK) BeginInstallCycle();
            else if (msg == Native.WM_TIMER && retryTimer != UIntPtr.Zero &&
                     (IntPtr.Size == 4 ? unchecked((uint)wParam.ToInt32()) : unchecked((ulong)wParam.ToInt64())) == retryTimer.ToUInt64())
            {
                CancelRetry();   // Win32 定时器默认重复；每次消费后撤销，再决定是否安排下一次。
                AttemptInstall();
            }
        }

        void Publish(bool enabled, int error, string message, bool canRetry)
        {
            if (stopping || onStatus == null) return;
            onStatus(new Status(enabled, powerWindow != IntPtr.Zero, retryTimer != UIntPtr.Zero,
                canRetry, error, wakeError, message));
        }

        IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_POWERBROADCAST)
            {
                int ev = wParam.ToInt32();
                if ((ev == Native.PBT_APMRESUMEAUTOMATIC || ev == Native.PBT_APMRESUMESUSPEND) &&
                    !stopping && retryTimer == UIntPtr.Zero) BeginInstallCycle();
                return (IntPtr)1;
            }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !stopping)
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
