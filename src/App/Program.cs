using System;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Comb.Engine;
using Forms = System.Windows.Forms;

namespace Comb.App
{
    /// <summary>
    /// 入口与单实例。再次运行 exe 就通知已有实例呼出窗口：
    ///   Comb.exe              呼出搜索条（首次运行则启动并显示）
    ///   Comb.exe --overview   呼出内容概览
    ///   Comb.exe --background 静默启动，只进托盘（开机自启用）
    /// </summary>
    static class Program
    {
        const string MutexName = @"Local\Comb.SingleInstance";
        public const string ShowEventName = @"Local\Comb.Show";
        public const string OverviewEventName = @"Local\Comb.Overview";

        [STAThread]
        static int Main(string[] args)
        {
            bool background = args.Contains("--background");
            bool overview = args.Contains("--overview");

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (!background) SignalExisting(overview ? OverviewEventName : ShowEventName);
                    return 0;
                }
                var app = new CombApp(background, overview);
                return app.Run();
            }
        }

        static void SignalExisting(string name)
        {
            // 我们是用户刚启动的前台进程，把切前台的权利让给已有实例
            Native.AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            // 已有实例可能刚启动、还没建好事件，最多等 3 秒
            for (int i = 0; i < 30; i++)
            {
                EventWaitHandle h;
                if (EventWaitHandle.TryOpenExisting(name, out h))
                {
                    using (h) h.Set();
                    return;
                }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>WPF 应用：托盘常驻，持有引擎、搜索窗口、概览窗口和热键钩子。</summary>
    sealed class CombApp : Application
    {
        readonly bool startInBackground;
        readonly bool startWithOverview;
        IFileIndex index;
        SearchWindow searchWindow;
        Forms.NotifyIcon tray;
        Forms.ToolStripMenuItem autoStartItem;
        Forms.ToolStripMenuItem hotkeyStatusItem;
        Forms.ToolStripMenuItem retryHotkeyItem;
        DoubleCtrlHook hook;
        System.Drawing.Icon appIcon;
        volatile bool exiting;
        bool hotkeyNoticeShown;

        public CombApp(bool background, bool overview)
        {
            startInBackground = background;
            startWithOverview = overview;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += OnUnhandledException;

            // 整个界面里唯一提到具体引擎的地方；以后换自研引擎只改这一行
            index = new EverythingIndex();
            index.Start();

            appIcon = Native.LoadOwnSmallIcon();
            searchWindow = new SearchWindow(index);
            CreateTray();

            hook = new DoubleCtrlHook(() => PostToUi(searchWindow.Toggle),
                status => PostToUi(() => UpdateHotkeyStatus(status)));
            hook.Start();

            ListenSignal(Program.ShowEventName, () => searchWindow.ShowBar());
            ListenSignal(Program.OverviewEventName, ShowOverview);

            if (startWithOverview) ShowOverview();
            else if (!startInBackground) searchWindow.ShowBar();
        }

        void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip { ShowItemToolTips = true };
            var show = new Forms.ToolStripMenuItem("显示搜索", null, (s, a) => searchWindow.ShowBar());
            show.Font = new System.Drawing.Font(show.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(show);
            menu.Items.Add(new Forms.ToolStripMenuItem("内容概览", null, (s, a) => ShowOverview()));
            menu.Items.Add(new Forms.ToolStripSeparator());
            hotkeyStatusItem = new Forms.ToolStripMenuItem("双 Ctrl：正在启用") { Enabled = false };
            retryHotkeyItem = new Forms.ToolStripMenuItem("重新启用双 Ctrl", null, (s, a) => RetryHotkey()) { Enabled = false };
            menu.Items.Add(hotkeyStatusItem);
            menu.Items.Add(retryHotkeyItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            autoStartItem = new Forms.ToolStripMenuItem("开机自启", null, (s, a) => ToggleAutoStart());
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (s, a) => ExitApp()));
            menu.Opening += (s, a) => autoStartItem.Checked = AutoStart.IsEnabled();

            tray = new Forms.NotifyIcon
            {
                Icon = appIcon,
                Text = "Comb（正在启用双 Ctrl）",
                ContextMenuStrip = menu,
                Visible = true
            };
            tray.MouseClick += (s, a) =>
            {
                if (a.Button == Forms.MouseButtons.Left) searchWindow.ShowBar();
            };
        }

        void PostToUi(Action action)
        {
            if (exiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 退出前已经排队的回调，也不能再访问已释放的托盘或窗口。
                    if (!exiting) action();
                }));
            }
            catch (InvalidOperationException)
            {
                // 检查后 Dispatcher 仍可能进入关闭阶段；后台钩子不应因此抛出未处理异常。
            }
        }

        void UpdateHotkeyStatus(DoubleCtrlHook.Status status)
        {
            if (exiting || tray == null) return;
            bool healthy = status.Available && status.WakeMonitoringAvailable;
            string detail = status.Message;
            if (status.ErrorCode != 0) detail += "（错误码 " + status.ErrorCode + "）";
            if (!status.WakeMonitoringAvailable && status.WakeErrorCode != 0 && status.WakeErrorCode != status.ErrorCode)
                detail += "；唤醒监视错误码 " + status.WakeErrorCode;

            hotkeyStatusItem.Text = !status.CanRetry ? "双 Ctrl：监听已停止，请重启 Comb" :
                healthy ? "双 Ctrl：已启用" :
                status.Available ? "双 Ctrl：可用，唤醒恢复暂不可用" :
                status.Retrying ? "双 Ctrl：暂不可用，正在重试" : "双 Ctrl：暂不可用";
            if (status.ErrorCode != 0) hotkeyStatusItem.Text += "（错误 " + status.ErrorCode + "）";
            hotkeyStatusItem.ToolTipText = detail;
            retryHotkeyItem.Enabled = status.CanRetry && !status.Retrying;
            tray.Text = healthy ? "Comb（双击 Ctrl 呼出）" : status.Available ?
                "Comb（双 Ctrl 可用；唤醒恢复受限）" : "Comb（热键暂不可用；可从托盘打开）";

            if (!healthy && !hotkeyNoticeShown)
            {
                hotkeyNoticeShown = true;  // 一次运行只提示一次；后续状态仍可在托盘菜单查看。
                tray.ShowBalloonTip(5000, "Comb 快捷键提示", detail + "。仍可点击托盘打开搜索。" +
                    (status.Retrying ? "正在自动重试。" : status.CanRetry ? "可从托盘选择“重新启用双 Ctrl”。" : "请退出后重新启动 Comb。"),
                    Forms.ToolTipIcon.Warning);
            }
        }

        void RetryHotkey()
        {
            if (exiting || hook == null) return;
            if (hook.RequestRetry())
            {
                retryHotkeyItem.Enabled = false;
                hotkeyStatusItem.Text = "双 Ctrl：正在重新启用";
            }
            else
            {
                hotkeyStatusItem.Text = "双 Ctrl：无法安排重试，请退出后重启 Comb";
                retryHotkeyItem.Enabled = false;
            }
        }

        void ShowOverview()
        {
            // 概览是搜索窗口里的一个页面，不再单独开窗口
            searchWindow.ShowOverview();
        }

        void ToggleAutoStart()
        {
            bool target = !AutoStart.IsEnabled();
            try
            {
                AutoStart.Set(target);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException)
            {
                MessageBox.Show("修改开机自启失败：" + ex.Message, "Comb", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>等另一个实例发来的信号（命名事件），收到后在 UI 线程上执行 action。</summary>
        void ListenSignal(string name, Action action)
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            var t = new Thread(() =>
            {
                while (true)
                {
                    handle.WaitOne();
                    PostToUi(action);
                }
            });
            t.IsBackground = true;
            t.Name = "Signal:" + name;
            t.Start();
        }

        void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            hook.Stop();
            tray.Visible = false;
            tray.Dispose();
            tray = null;
            searchWindow.AllowClose();
            var disposable = index as IDisposable;
            if (disposable != null) disposable.Dispose();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            exiting = true;
            if (hook != null) hook.Stop();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            base.OnExit(e);
        }

        void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // 最后一道防线：把错误明确告诉用户，程序继续留在托盘，不静默吞掉
            MessageBox.Show("Comb 出错：\n\n" + e.Exception, "Comb", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }

    /// <summary>开机自启：只写 HKCU\...\Run 下的 Comb 值。</summary>
    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Comb";

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(ValueName) != null;
        }

        public static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    key.SetValue(ValueName, "\"" + exe + "\" --background");
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
