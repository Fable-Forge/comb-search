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
        DoubleCtrlHook hook;
        System.Drawing.Icon appIcon;

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

            hook = new DoubleCtrlHook(() => Dispatcher.BeginInvoke(new Action(searchWindow.Toggle)));
            hook.Start();

            ListenSignal(Program.ShowEventName, () => searchWindow.ShowBar());
            ListenSignal(Program.OverviewEventName, ShowOverview);

            if (startWithOverview) ShowOverview();
            else if (!startInBackground) searchWindow.ShowBar();
        }

        void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip();
            var show = new Forms.ToolStripMenuItem("显示搜索", null, (s, a) => searchWindow.ShowBar());
            show.Font = new System.Drawing.Font(show.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(show);
            menu.Items.Add(new Forms.ToolStripMenuItem("内容概览", null, (s, a) => ShowOverview()));
            menu.Items.Add(new Forms.ToolStripSeparator());
            autoStartItem = new Forms.ToolStripMenuItem("开机自启", null, (s, a) => ToggleAutoStart());
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (s, a) => ExitApp()));
            menu.Opening += (s, a) => autoStartItem.Checked = AutoStart.IsEnabled();

            tray = new Forms.NotifyIcon
            {
                Icon = appIcon,
                Text = "Comb（双击 Ctrl 呼出）",
                ContextMenuStrip = menu,
                Visible = true
            };
            tray.MouseClick += (s, a) =>
            {
                if (a.Button == Forms.MouseButtons.Left) searchWindow.ShowBar();
            };
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
                    Dispatcher.BeginInvoke(action);
                }
            });
            t.IsBackground = true;
            t.Name = "Signal:" + name;
            t.Start();
        }

        void ExitApp()
        {
            hook.Stop();
            tray.Visible = false;
            tray.Dispose();
            searchWindow.AllowClose();
            var disposable = index as IDisposable;
            if (disposable != null) disposable.Dispose();
            Shutdown();
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
