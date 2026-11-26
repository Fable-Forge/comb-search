using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

// 单独与 src/App/DoubleCtrlHook.cs 编译；不链接生产 Native.cs，不调用任何系统钩子或 SendInput。
// 测试使用真实的生产线程/状态机，替身只控制 Win32 成功、失败及消息到达顺序。
namespace Comb.App
{
    static class HotkeyRegressionTests
    {
        static int Main()
        {
            var tests = new Action[]
            {
                InstallFailureRecovers,
                HighTimerIdRecovers,
                AutomaticRetriesAreBounded,
                RepeatedResumeKeepsOneHook,
                ResumeFailureRecovers,
                WakeWatcherCanRecoverAfterExhaustion,
                UnhookFailureNeverAddsAnotherHook,
                AlreadyRemovedHookCanRecover,
                TimerFailurePreservesInstallError,
                StopBeforeQueueExists,
                StopRejectsQueuedRetry,
                MessageLoopFailureIsReported
            };
            int failures = 0;
            foreach (var test in tests)
            {
                try
                {
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + ex.Message);
                }
            }
            Console.WriteLine((tests.Length - failures) + "/" + tests.Length + " hotkey regression tests passed.");
            return failures == 0 ? 0 : 1;
        }

        static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        sealed class Fixture : IDisposable
        {
            public readonly DoubleCtrlHook Hook;
            public readonly List<DoubleCtrlHook.Status> States = new List<DoubleCtrlHook.Status>();
            public int Triggers;
            public DoubleCtrlHook.Status Last { get { return States[States.Count - 1]; } }

            public Fixture(int[] install = null, int[] window = null, int[] remove = null, int[] timer = null, bool highTimer = false)
            {
                Native.Reset(install, window, remove, timer);
                if (highTimer) Native.UseHighTimerIds();
                Hook = new DoubleCtrlHook(() => Triggers++, s => States.Add(s));
                Hook.Start();
                Assert(Native.LoopEntered.WaitOne(3000), "hook thread did not reach its message loop");
                Native.Sync();
            }

            public void Dispose()
            {
                Hook.Stop();
                Native.AllowQueueCreation.Set();
                Native.JoinWorker();
                Assert(Native.ActiveHooks == 0, "stopping leaked an active hook");
                Assert(Native.ActiveTimers == 0, "stopping leaked a retry timer");
                Assert(Native.WrongThreadCalls == 0, "native hook operation ran on another thread");
            }
        }

        static void InstallFailureRecovers()
        {
            using (var f = new Fixture(install: new[] { 5, 0 }))
            {
                Assert(!f.Last.Available && f.Last.Retrying && f.Last.ErrorCode == 5,
                    "initial failure must expose the original error and scheduled recovery");
                Assert(Native.ActiveHooks == 0 && Native.ActiveTimers == 1, "failed installation created a hook");
                Native.FireTimer();
                Native.Sync();
                Assert(f.Last.Available && !f.Last.Retrying && f.Last.ErrorCode == 0, "recovery did not clear failed state");
                Assert(Native.InstallCalls == 2 && Native.ActiveHooks == 1, "recovery must install exactly one hook");
                Native.DoubleCtrl();
                Assert(f.Triggers == 1, "recovered hook did not trigger exactly once");
            }
        }

        static void AutomaticRetriesAreBounded()
        {
            using (var f = new Fixture(install: new[] { 5, 5, 5, 0 }))
            {
                ulong staleTimer = Native.OnlyTimer;
                Assert(Native.OnlyTimerDelay == 500, "first retry must be delayed rather than spinning");
                Native.FireTimer();
                Native.Sync();
                Assert(Native.OnlyTimerDelay == 1500, "second retry must use the bounded backoff");
                Native.FireTimer();
                Native.Sync();
                Assert(Native.InstallCalls == 3 && Native.ActiveTimers == 0, "automatic retry budget was not bounded");
                Assert(!f.Last.Available && !f.Last.Retrying && f.Last.CanRetry, "manual recovery must remain available");
                Native.PostTimer(staleTimer);
                Native.Sync();
                Assert(Native.InstallCalls == 3, "stale timer restarted an exhausted retry cycle");
                Assert(f.Hook.RequestRetry(), "manual retry was rejected");
                Native.Sync();
                Assert(f.Last.Available && Native.InstallCalls == 4, "manual retry did not recover after exhaustion");
            }
        }

        static void HighTimerIdRecovers()
        {
            using (var f = new Fixture(install: new[] { 5, 0 }, highTimer: true))
            {
                Assert(Native.OnlyTimer == 0xF0000000UL, "test did not exercise a high-bit timer ID");
                Native.FireTimer(); Native.Sync();
                Assert(f.Last.Available && Native.InstallCalls == 2 && Native.ActiveTimers == 0,
                    "high-bit timer ID was not matched on this process architecture");
            }
        }

        static void RepeatedResumeKeepsOneHook()
        {
            using (var f = new Fixture())
            {
                for (int i = 0; i < 4; i++)
                {
                    Native.Resume(Native.PBT_APMRESUMEAUTOMATIC);
                    Native.Resume(Native.PBT_APMRESUMESUSPEND);
                }
                Assert(Native.ActiveHooks == 1 && Native.MaximumHooks == 1, "resume accumulated multiple active hooks");
                Assert(Native.ActiveTimers == 0, "successful resume left retries running");
                Native.DoubleCtrl();
                Assert(f.Triggers == 1, "repeated resume duplicated keyboard callbacks");
            }
        }

        static void ResumeFailureRecovers()
        {
            using (var f = new Fixture(install: new[] { 0, 8, 0 }))
            {
                Native.Resume(Native.PBT_APMRESUMEAUTOMATIC);
                Assert(!f.Last.Available && f.Last.ErrorCode == 8 && Native.ActiveHooks == 0,
                    "failed wake reinstallation was not reported");
                Native.Resume(Native.PBT_APMRESUMESUSPEND);
                Assert(Native.InstallCalls == 2 && Native.ActiveTimers == 1, "duplicate resume reset an active retry cycle");
                Native.FireTimer();
                Native.Sync();
                Assert(f.Last.Available && Native.ActiveHooks == 1 && Native.MaximumHooks == 1, "wake recovery failed");
            }
        }

        static void WakeWatcherCanRecoverAfterExhaustion()
        {
            using (var f = new Fixture(window: new[] { 1407, 1407, 1407, 0 }))
            {
                Assert(f.Last.Available && !f.Last.WakeMonitoringAvailable && f.Last.WakeErrorCode == 1407,
                    "wake-monitor failure must be distinguishable from hotkey failure");
                Native.FireTimer(); Native.Sync();
                Native.FireTimer(); Native.Sync();
                Assert(Native.InstallCalls == 1, "watcher-only retries needlessly replaced a healthy hook");
                Assert(!f.Last.Retrying && !f.Last.WakeMonitoringAvailable, "degraded state was hidden after retries");
                Assert(f.Hook.RequestRetry(), "manual retry was unavailable in degraded mode");
                Native.Sync();
                Assert(f.Last.Available && f.Last.WakeMonitoringAvailable && f.Last.WakeErrorCode == 0,
                    "manual retry did not rebuild wake monitoring");
                Assert(Native.WindowAttempts == 4 && Native.ActiveHooks == 1, "unexpected watcher recovery state");
            }
        }

        static void UnhookFailureNeverAddsAnotherHook()
        {
            using (var f = new Fixture(remove: new[] { 5, 0 }))
            {
                Assert(f.Hook.RequestRetry(), "manual retry was rejected"); Native.Sync();
                Assert(Native.InstallCalls == 1 && Native.ActiveHooks == 1 && Native.MaximumHooks == 1,
                    "unhook failure must not stack a replacement onto the old hook");
                Assert(f.Last.ErrorCode == 5 && f.Last.Retrying, "unhook failure was not exposed");
                Native.FireTimer(); Native.Sync();
                Assert(f.Last.Available && Native.InstallCalls == 2 && Native.MaximumHooks == 1, "unhook retry did not recover");
            }
        }

        static void AlreadyRemovedHookCanRecover()
        {
            using (var f = new Fixture(remove: new[] { 1404 }))
            {
                Assert(f.Hook.RequestRetry(), "manual retry was rejected"); Native.Sync();
                Assert(f.Last.Available && Native.InstallCalls == 2 && Native.ActiveHooks == 1,
                    "an already-removed OS hook prevented recovery");
            }
        }

        static void TimerFailurePreservesInstallError()
        {
            using (var f = new Fixture(install: new[] { 5, 0 }, timer: new[] { 8 }))
            {
                Assert(!f.Last.Available && !f.Last.Retrying && f.Last.ErrorCode == 5 && f.Last.Message.Contains("8"),
                    "timer failure lost the original installation error or claimed recovery was scheduled");
                Assert(f.Hook.RequestRetry(), "timer failure disabled manual recovery"); Native.Sync();
                Assert(f.Last.Available, "manual recovery after timer failure failed");
            }
        }

        static void StopBeforeQueueExists()
        {
            Native.Reset();
            Native.AllowQueueCreation.Reset();
            var states = new List<DoubleCtrlHook.Status>();
            var hook = new DoubleCtrlHook(() => { }, s => states.Add(s));
            hook.Start();
            try
            {
                Assert(Native.QueueCreationEntered.WaitOne(3000), "worker did not start");
                hook.Stop();
                Native.AllowQueueCreation.Set();
                Native.JoinWorker();
                Assert(Native.InstallCalls == 0 && Native.ActiveTimers == 0 && states.Count == 0,
                    "early Stop allowed a later install or notification");
                Assert(!hook.RequestRetry(), "a stopped hook accepted retry");
            }
            finally { Native.AllowQueueCreation.Set(); hook.Stop(); Native.JoinWorker(); }
        }

        static void StopRejectsQueuedRetry()
        {
            using (var f = new Fixture(install: new[] { 5, 0 }))
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                Native.PostWork(() => { entered.Set(); release.WaitOne(3000); });
                Assert(entered.WaitOne(3000), "message pump did not pause");
                try
                {
                    int stateCount = f.States.Count;
                    Native.FireTimer();
                    Assert(f.Hook.RequestRetry(), "could not enqueue the pending user retry");
                    f.Hook.Stop();
                    release.Set();
                    Native.JoinWorker();
                    Assert(Native.InstallCalls == 1 && Native.ActiveHooks == 0 && Native.ActiveTimers == 0,
                        "queued retry installed a hook after Stop");
                    Assert(f.States.Count == stateCount, "stopped monitor delivered a new status notification");
                    Assert(!f.Hook.RequestRetry(), "stopped monitor accepted retry");
                }
                finally { release.Set(); }
            }
        }

        static void MessageLoopFailureIsReported()
        {
            using (var f = new Fixture())
            {
                Native.FailMessageLoop(87);
                Native.JoinWorker();
                Assert(!f.Last.Available && !f.Last.CanRetry && !f.Last.Retrying && f.Last.ErrorCode == 87,
                    "message-loop failure was confused with an available hotkey");
                Assert(Native.ActiveHooks == 0 && !f.Hook.RequestRetry(), "dead worker kept an active hook or accepted work");
            }
        }
    }

    // Win32 替身：定时器由测试投递，不依赖墙上时钟；没有真实键盘输入或桌面交互。
    static class Native
    {
        public const uint WM_QUIT = 0x0012, WM_TIMER = 0x0113, WM_POWERBROADCAST = 0x0218;
        public const int PBT_APMRESUMESUSPEND = 7, PBT_APMRESUMEAUTOMATIC = 18, WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        sealed class Message
        {
            public uint Id;
            public IntPtr WParam, LParam;
            public Action Work;
            public int Error;
        }

        static BlockingCollection<Message> queue;
        static Queue<int> installErrors, windowErrors, removeErrors, timerErrors;
        static Dictionary<long, HookProc> hooks;
        static Dictionary<ulong, uint> timers;
        static WndProc windowProc;
        static long nextHook;
        static ulong nextTimer;
        static bool queueReady;
        [ThreadStatic] static int lastError;
        public static Thread Worker;
        public static ManualResetEvent LoopEntered, QueueCreationEntered, AllowQueueCreation;
        public static int InstallCalls, WindowAttempts, MaximumHooks, WrongThreadCalls;
        public static int ActiveHooks { get { return hooks.Count; } }
        public static int ActiveTimers { get { return timers.Count; } }
        public static ulong OnlyTimer { get { return timers.Keys.Single(); } }
        public static uint OnlyTimerDelay { get { return timers.Values.Single(); } }
        public static void UseHighTimerIds() { nextTimer = 0xEFFFFFFFUL; }

        public static void Reset(int[] install = null, int[] window = null, int[] remove = null, int[] timer = null)
        {
            if (Worker != null && Worker.IsAlive) throw new Exception("previous test left a worker running");
            queue = new BlockingCollection<Message>();
            installErrors = new Queue<int>(install ?? new int[0]);
            windowErrors = new Queue<int>(window ?? new int[0]);
            removeErrors = new Queue<int>(remove ?? new int[0]);
            timerErrors = new Queue<int>(timer ?? new int[0]);
            hooks = new Dictionary<long, HookProc>();
            timers = new Dictionary<ulong, uint>();
            LoopEntered = new ManualResetEvent(false);
            QueueCreationEntered = new ManualResetEvent(false);
            AllowQueueCreation = new ManualResetEvent(true);
            Worker = null; windowProc = null; queueReady = false;
            nextHook = 100; nextTimer = 200;
            InstallCalls = WindowAttempts = MaximumHooks = WrongThreadCalls = 0;
        }

        static int ErrorFrom(Queue<int> errors) { return errors.Count == 0 ? 0 : errors.Dequeue(); }
        static void CheckThread()
        {
            if (Worker != Thread.CurrentThread) WrongThreadCalls++;
        }

        public static void EnsureMessageQueue()
        {
            Worker = Thread.CurrentThread;
            QueueCreationEntered.Set();
            if (!AllowQueueCreation.WaitOne(3000)) throw new Exception("queue-creation test gate timed out");
            queueReady = true;
        }
        public static uint GetCurrentThreadId() { return (uint)Thread.CurrentThread.ManagedThreadId; }
        public static IntPtr GetModuleHandle(string module) { CheckThread(); lastError = 999; return (IntPtr)1; }
        public static int LastError() { return lastError; }

        public static IntPtr CreateHiddenWindow(string name, WndProc proc)
        {
            CheckThread(); WindowAttempts++;
            int error = ErrorFrom(windowErrors);
            if (error != 0) throw new Win32Exception(error);
            windowProc = proc;
            return (IntPtr)10;
        }
        public static void DestroyHiddenWindow(IntPtr hwnd, string name) { CheckThread(); windowProc = null; }
        public static IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l) { return IntPtr.Zero; }

        public static IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread)
        {
            CheckThread(); InstallCalls++;
            lastError = ErrorFrom(installErrors);
            if (lastError != 0) return IntPtr.Zero;
            long id = ++nextHook;
            hooks.Add(id, proc);
            MaximumHooks = Math.Max(MaximumHooks, hooks.Count);
            return (IntPtr)id;
        }
        public static bool UnhookWindowsHookEx(IntPtr handle)
        {
            CheckThread();
            lastError = ErrorFrom(removeErrors);
            if (lastError == 1404) hooks.Remove(handle.ToInt64());
            if (lastError != 0) return false;
            return hooks.Remove(handle.ToInt64());
        }
        public static IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l) { return IntPtr.Zero; }

        public static UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint delay, IntPtr callback)
        {
            CheckThread(); lastError = ErrorFrom(timerErrors);
            if (lastError != 0) return UIntPtr.Zero;
            ulong key = ++nextTimer;
            timers.Add(key, delay);
            // 故意覆盖 last-error，验证调用方已在安装失败后立即保存错误码。
            lastError = 999;
            return new UIntPtr(key);
        }
        public static bool KillTimer(IntPtr hwnd, UIntPtr id)
        {
            CheckThread(); lastError = 999;
            return timers.Remove(id.ToUInt64());
        }

        public static bool PostThreadMessage(uint id, uint msg, IntPtr w, IntPtr l)
        {
            if (!queueReady || Worker == null || id != (uint)Worker.ManagedThreadId) return false;
            queue.Add(new Message { Id = msg, WParam = w, LParam = l });
            return true;
        }
        public static int RunMessageLoop(Action<uint, IntPtr, IntPtr> handler)
        {
            CheckThread(); LoopEntered.Set();
            while (true)
            {
                Message message = queue.Take();
                if (message.Error != 0) return message.Error;
                if (message.Id == WM_QUIT) return 0;
                if (message.Work != null) message.Work();
                else handler(message.Id, message.WParam, message.LParam);
            }
        }
        public static void PostWork(Action work) { queue.Add(new Message { Work = work }); }
        public static void Sync() { Invoke(() => { }); }
        static void Invoke(Action work)
        {
            Exception failure = null;
            using (var completed = new ManualResetEvent(false))
            {
                PostWork(() =>
                {
                    try { work(); } catch (Exception ex) { failure = ex; }
                    finally { completed.Set(); }
                });
                if (!completed.WaitOne(3000)) throw new Exception("test message was not processed");
            }
            if (failure != null) throw failure;
        }
        public static void FireTimer() { PostTimer(OnlyTimer); }
        public static void PostTimer(ulong id)
        {
            IntPtr value = IntPtr.Size == 4 ? new IntPtr(unchecked((int)(uint)id)) : new IntPtr(unchecked((long)id));
            queue.Add(new Message { Id = WM_TIMER, WParam = value });
        }
        public static void Resume(int kind)
        {
            Invoke(() => windowProc((IntPtr)10, WM_POWERBROADCAST, (IntPtr)kind, IntPtr.Zero));
        }
        public static void FailMessageLoop(int error) { queue.Add(new Message { Error = error }); }
        public static void JoinWorker()
        {
            if (Worker != null && !Worker.Join(3000)) throw new Exception("hook worker failed to stop");
        }
        public static void DoubleCtrl()
        {
            Invoke(() =>
            {
                IntPtr data = Marshal.AllocHGlobal(32);
                try
                {
                    Marshal.WriteInt32(data, 0, 0xA2);
                    int[] times = { 10, 40, 100, 130 };
                    for (int i = 0; i < times.Length; i++)
                    {
                        Marshal.WriteInt32(data, 12, times[i]);
                        foreach (HookProc proc in hooks.Values.ToArray())
                            proc(0, (IntPtr)(i % 2 == 0 ? WM_KEYDOWN : WM_KEYUP), data);
                    }
                }
                finally { Marshal.FreeHGlobal(data); }
            });
        }
    }
}
