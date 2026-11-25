using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Comb.Engine
{
    /// <summary>
    /// 用本机 Everything 1.4 当引擎（IPC 查询，不装服务、不要管理员）。
    /// 整个项目里只有这个文件知道 Everything：窗口类、IPC 常量、搜索语法、扩展名列表、拉起逻辑。
    /// 线程模型：回复窗口是 message-only 窗口，建在独立线程上并有自己的消息循环；
    /// 查询在调用方线程上用 SendMessageTimeout 发出，再等事件拿回复；同一时间只跑一个查询。
    /// </summary>
    public sealed class EverythingIndex : IFileIndex, IDisposable
    {
        // ---- Everything IPC 常量（everything_ipc.h；2026-11-21 本机实测） ----
        const string IpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
        const int WM_USER = 0x0400;
        const int WM_COPYDATA = 0x004A;
        const int IPC_GET_MAJOR_VERSION = 0;
        const int IPC_GET_MINOR_VERSION = 1;
        const int IPC_GET_REVISION = 2;
        const int IPC_GET_BUILD_NUMBER = 3;
        const int IPC_IS_DB_LOADED = 401;
        const int COPYDATA_QUERY2W = 18;

        const uint REQUEST_NAME = 0x1;
        const uint REQUEST_PATH = 0x2;
        const uint REQUEST_SIZE = 0x10;
        const uint REQUEST_DATE_MODIFIED = 0x40;
        const uint REQUEST_HIGHLIGHTED_NAME = 0x2000;

        const uint SORT_NAME_ASCENDING = 1;
        const uint SORT_NAME_DESCENDING = 2;
        const uint SORT_PATH_ASCENDING = 3;
        const uint SORT_PATH_DESCENDING = 4;
        const uint SORT_SIZE_ASCENDING = 5;
        const uint SORT_SIZE_DESCENDING = 6;
        const uint SORT_EXTENSION_ASCENDING = 7;
        const uint SORT_EXTENSION_DESCENDING = 8;
        const uint SORT_DATE_MODIFIED_ASCENDING = 13;
        const uint SORT_DATE_MODIFIED_DESCENDING = 14;

        const uint ITEM_FOLDER = 0x1;
        const uint ITEM_DRIVE = 0x2;

        // ---- 类型筛选用的扩展名（Everything 的 ext: 语法，分号分隔） ----
        const string DocumentExt = "doc;docx;docm;xls;xlsx;xlsm;csv;ppt;pptx;pptm;pdf;txt;md;rtf;odt;ods;odp;wps;et;dps;epub";
        const string ImageExt = "jpg;jpeg;png;gif;bmp;webp;tif;tiff;svg;ico;heic;heif;avif;psd;raw;cr2;nef;dng";
        const string VideoExt = "mp4;mkv;avi;mov;wmv;flv;webm;m4v;mpg;mpeg;3gp;rm;rmvb;m2ts;mts;vob";

        const int QueryTimeoutMs = 5000;
        const int SimpleTimeoutMs = 1000;
        const int LaunchRetryMs = 20000;

        readonly object ipcLock = new object();
        readonly ReplyWindow reply = new ReplyWindow();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        uint nextReplyId;
        volatile IndexStatus status = new IndexStatus(IndexState.Starting, "正在连接 Everything…");
        Thread guardThread;
        volatile bool disposed;
        DateTime lastLaunchUtc = DateTime.MinValue;

        public event EventHandler StatusChanged;

        public IndexStatus Status { get { return status; } }

        public void Start()
        {
            if (guardThread != null) return;
            string error = reply.Start();
            if (error != null)
            {
                SetStatus(IndexState.Unavailable, "无法创建接收 Everything 回复的窗口：" + error);
                return;
            }
            guardThread = new Thread(GuardLoop) { IsBackground = true, Name = "EverythingGuard" };
            guardThread.Start();
        }

        public void Dispose()
        {
            disposed = true;
            wake.Set();
            reply.Stop();
        }

        /// <summary>给 Probe 用：等到可查询或超时。</summary>
        public bool WaitReady(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (status.State == IndexState.Ready) return true;
                Thread.Sleep(100);
            }
            return status.State == IndexState.Ready;
        }

        /// <summary>Everything 版本号，例如 1.4.1.1032；取不到时返回 null。</summary>
        public string Version
        {
            get
            {
                IntPtr hwnd = FindWindow(IpcWindowClass, null);
                if (hwnd == IntPtr.Zero) return null;
                var parts = new string[4];
                int[] codes = { IPC_GET_MAJOR_VERSION, IPC_GET_MINOR_VERSION, IPC_GET_REVISION, IPC_GET_BUILD_NUMBER };
                for (int i = 0; i < 4; i++)
                {
                    IntPtr v;
                    if (!SendSimple(hwnd, codes[i], out v)) return null;
                    parts[i] = v.ToInt64().ToString();
                }
                return string.Join(".", parts);
            }
        }

        // ================= 查询 =================

        public SearchPage Search(SearchQuery query, int offset, int count, SortOrder sort)
        {
            uint request = REQUEST_NAME | REQUEST_PATH | REQUEST_SIZE | REQUEST_DATE_MODIFIED | REQUEST_HIGHLIGHTED_NAME;
            byte[] data = Query(ToEverythingSearch(query), offset, count, request, MapSort(sort));
            long total;
            var items = Parse(data, out total);
            return new SearchPage { Total = total, Offset = offset, Items = items };
        }

        public long Count(SearchQuery query)
        {
            // max_results = 0：只返回总数
            byte[] data = Query(ToEverythingSearch(query), 0, 0, REQUEST_NAME, SORT_NAME_ASCENDING);
            if (data.Length < 20) throw Fail(IndexState.Unavailable, "Everything 返回的数据不完整。");
            return BitConverter.ToUInt32(data, 0);
        }

        public ChildList ListChildren(string folderPath, int max)
        {
            const uint request = REQUEST_NAME | REQUEST_PATH | REQUEST_SIZE;
            long total;
            if (folderPath == null)
            {
                // root: 列出没有上级的项（各盘）
                var volumes = Parse(Query("root:", 0, max, request, SORT_SIZE_DESCENDING), out total);
                return new ChildList { TotalCount = total, Items = volumes };
            }

            // Everything 按大小降序时，文件整组排在文件夹前面（两组各自降序），直接取前 max 项会漏掉大文件夹
            // （2026-11-21 实测：C:\Windows\System32 前几项全是文件，7.8 GB 的 DriverStore 排不进来）。
            // 所以文件夹、文件各取前 max 项，合并后再按大小取前 max。
            string parent = "parent:\"" + folderPath + "\"";
            long folderTotal, fileTotal;
            var items = Parse(Query(parent + " folder:", 0, max, request, SORT_SIZE_DESCENDING), out folderTotal);
            items.AddRange(Parse(Query(parent + " file:", 0, max, request, SORT_SIZE_DESCENDING), out fileTotal));
            items.Sort((a, b) =>
            {
                int c = b.Size.CompareTo(a.Size);   // 大小未知（-1）排在最后
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            if (items.Count > max) items.RemoveRange(max, items.Count - max);
            return new ChildList { TotalCount = folderTotal + fileTotal, Items = items };
        }

        /// <summary>把引擎无关的查询条件翻译成 Everything 搜索语法（公开给 Probe 打印诊断）。</summary>
        public static string ToEverythingSearch(SearchQuery q)
        {
            var sb = new StringBuilder(q.Text);
            // 用户输入里引号没配对时补上，免得后面追加的筛选被吞进引号里
            int quotes = 0;
            foreach (char c in q.Text) if (c == '"') quotes++;
            if (quotes % 2 == 1) sb.Append('"');

            switch (q.Kind)
            {
                case KindFilter.Folder: sb.Append(" folder:"); break;
                case KindFilter.File: sb.Append(" file:"); break;
                case KindFilter.Document: sb.Append(" file: ext:").Append(DocumentExt); break;
                case KindFilter.Image: sb.Append(" file: ext:").Append(ImageExt); break;
                case KindFilter.Video: sb.Append(" file: ext:").Append(VideoExt); break;
            }
            switch (q.Date)
            {
                case DateFilter.LastDay: sb.Append(" dm:last24hours"); break;
                case DateFilter.LastWeek: sb.Append(" dm:last7days"); break;
                case DateFilter.LastMonth: sb.Append(" dm:last30days"); break;
            }
            if (q.Sizes != SizeFilter.None)
            {
                // 大小区间只算文件；多选之间是「或」。Everything 里 | 比空格（与）优先级高，不用额外加括号
                var ranges = new List<string>();
                if ((q.Sizes & SizeFilter.Small) != 0) ranges.Add("size:<1mb");
                if ((q.Sizes & SizeFilter.Medium) != 0) ranges.Add("size:1mb..100mb");
                if ((q.Sizes & SizeFilter.Large) != 0) ranges.Add("size:>100mb");
                sb.Append(" file: ").Append(string.Join("|", ranges));
            }
            return sb.ToString().Trim();
        }

        static uint MapSort(SortOrder sort)
        {
            switch (sort)
            {
                case SortOrder.NameDescending: return SORT_NAME_DESCENDING;
                case SortOrder.PathAscending: return SORT_PATH_ASCENDING;
                case SortOrder.PathDescending: return SORT_PATH_DESCENDING;
                case SortOrder.SizeAscending: return SORT_SIZE_ASCENDING;
                case SortOrder.SizeDescending: return SORT_SIZE_DESCENDING;
                case SortOrder.DateModifiedAscending: return SORT_DATE_MODIFIED_ASCENDING;
                case SortOrder.DateModifiedDescending: return SORT_DATE_MODIFIED_DESCENDING;
                // 「类型」按扩展名排，和界面上「MD 文件」这种按扩展名显示的类型一致
                case SortOrder.TypeAscending: return SORT_EXTENSION_ASCENDING;
                case SortOrder.TypeDescending: return SORT_EXTENSION_DESCENDING;
                default: return SORT_NAME_ASCENDING;
            }
        }

        /// <summary>发一个 QUERY2W 请求，返回回复的原始字节。</summary>
        byte[] Query(string search, int offset, int maxResults, uint request, uint sort)
        {
            IndexStatus st = status;
            if (st.State != IndexState.Ready)
            {
                wake.Set();
                throw new IndexUnavailableException(st.Message);
            }

            lock (ipcLock)
            {
                IntPtr hwnd = FindWindow(IpcWindowClass, null);
                if (hwnd == IntPtr.Zero) throw Fail(IndexState.Starting, "Everything 没有运行，正在重新启动…");

                uint id = unchecked(++nextReplyId);
                if (id == 0) id = ++nextReplyId;

                // 7 个 DWORD：reply_hwnd, reply_copydata_message, search_flags, offset, max_results, request_flags, sort_type
                // 后面紧跟以 0 结尾的 UTF-16 搜索串
                byte[] text = Encoding.Unicode.GetBytes(search + "\0");
                int size = 28 + text.Length;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buffer, 0, unchecked((int)reply.Handle.ToInt64()));
                    Marshal.WriteInt32(buffer, 4, unchecked((int)id));
                    Marshal.WriteInt32(buffer, 8, 0);
                    Marshal.WriteInt32(buffer, 12, offset);
                    Marshal.WriteInt32(buffer, 16, maxResults);
                    Marshal.WriteInt32(buffer, 20, unchecked((int)request));
                    Marshal.WriteInt32(buffer, 24, unchecked((int)sort));
                    Marshal.Copy(text, 0, buffer + 28, text.Length);

                    var cds = new COPYDATASTRUCT { dwData = (IntPtr)COPYDATA_QUERY2W, cbData = size, lpData = buffer };
                    reply.Expect(id);
                    IntPtr accepted;
                    IntPtr sent = SendMessageTimeout(hwnd, WM_COPYDATA, reply.Handle, ref cds,
                                                     SMTO_ABORTIFHUNG, QueryTimeoutMs, out accepted);
                    if (sent == IntPtr.Zero)
                        throw Fail(IndexState.Unavailable, "Everything 没有响应，稍后自动重试。");
                    if (accepted == IntPtr.Zero)
                        throw Fail(IndexState.Unavailable, "Everything 拒绝了查询，稍后自动重试。");

                    byte[] data = reply.Wait(QueryTimeoutMs);
                    if (data == null) throw Fail(IndexState.Unavailable, "Everything 查询超时，稍后自动重试。");
                    return data;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        /// <summary>
        /// 解析 EVERYTHING_IPC_LIST2：头部 totitems, numitems, offset, request_flags, sort_type；
        /// 然后 numitems 个 {flags, data_offset}；各字段按 request 位从低到高依次排在 data_offset 处。
        /// </summary>
        static List<FileEntry> Parse(byte[] b, out long total)
        {
            try
            {
                if (b.Length < 20) throw new ArgumentException("header");
                total = BitConverter.ToUInt32(b, 0);
                int count = (int)BitConverter.ToUInt32(b, 4);
                uint request = BitConverter.ToUInt32(b, 12);
                var list = new List<FileEntry>(count);
                for (int i = 0; i < count; i++)
                {
                    int itemPos = 20 + i * 8;
                    uint flags = BitConverter.ToUInt32(b, itemPos);
                    int p = (int)BitConverter.ToUInt32(b, itemPos + 4);
                    var e = new FileEntry();
                    e.IsVolume = (flags & ITEM_DRIVE) != 0;
                    e.IsFolder = e.IsVolume || (flags & ITEM_FOLDER) != 0;
                    string highlighted = null;

                    for (int bit = 0; bit < 16; bit++)
                    {
                        uint f = 1u << bit;
                        if ((request & f) == 0) continue;
                        switch (FieldKindOf(f))
                        {
                            case FieldKind.Text:
                                string s = ReadText(b, ref p);
                                if (f == REQUEST_NAME) e.Name = s;
                                else if (f == REQUEST_PATH) e.Folder = s;
                                else if (f == REQUEST_HIGHLIGHTED_NAME) highlighted = s;
                                break;
                            case FieldKind.Int64:
                                long v = BitConverter.ToInt64(b, p);
                                p += 8;
                                if (f == REQUEST_SIZE) e.Size = v < 0 ? -1 : v;
                                else if (f == REQUEST_DATE_MODIFIED) e.Modified = FromFileTime(v);
                                break;
                            default:
                                p += 4;
                                break;
                        }
                    }
                    if (highlighted != null) e.Highlights = ParseHighlights(highlighted);
                    list.Add(e);
                }
                return list;
            }
            catch (ArgumentException)
            {
                throw new IndexUnavailableException("Everything 返回的数据格式不对。");
            }
            catch (IndexOutOfRangeException)
            {
                throw new IndexUnavailableException("Everything 返回的数据格式不对。");
            }
        }

        enum FieldKind { Text, Int64, Dword }

        static FieldKind FieldKindOf(uint flag)
        {
            switch (flag)
            {
                case 0x10: case 0x20: case 0x40: case 0x80: case 0x800: case 0x1000:
                    return FieldKind.Int64;   // 大小、各种日期
                case 0x100: case 0x400:
                    return FieldKind.Dword;   // 属性、运行次数
                default:
                    return FieldKind.Text;    // 名称、路径、扩展名、高亮名等
            }
        }

        /// <summary>字符串：DWORD 长度（字符数）+ 字符 + 一个 0。</summary>
        static string ReadText(byte[] b, ref int p)
        {
            int len = (int)BitConverter.ToUInt32(b, p);
            p += 4;
            string s = Encoding.Unicode.GetString(b, p, len * 2);
            p += (len + 1) * 2;
            return s;
        }

        /// <summary>FILETIME；全 1 表示未知。</summary>
        static DateTime? FromFileTime(long v)
        {
            if (v <= 0) return null;
            try { return DateTime.FromFileTime(v); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        /// <summary>高亮名里 * 切换高亮，** 表示字面的星号。返回 起点,长度 对。</summary>
        static int[] ParseHighlights(string s)
        {
            var ranges = new List<int>();
            int plain = 0, start = -1;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '*')
                {
                    if (i + 1 < s.Length && s[i + 1] == '*') { plain++; i++; continue; }
                    if (start < 0) start = plain;
                    else
                    {
                        if (plain > start) { ranges.Add(start); ranges.Add(plain - start); }
                        start = -1;
                    }
                    continue;
                }
                plain++;
            }
            if (start >= 0 && plain > start) { ranges.Add(start); ranges.Add(plain - start); }
            return ranges.Count == 0 ? null : ranges.ToArray();
        }

        // ================= 守护：找窗口、拉起、等数据库 =================

        void GuardLoop()
        {
            while (!disposed)
            {
                CheckOnce();
                wake.WaitOne(status.State == IndexState.Ready ? 3000 : 700);
            }
        }

        void CheckOnce()
        {
            IntPtr hwnd = FindWindow(IpcWindowClass, null);
            if (hwnd == IntPtr.Zero)
            {
                string exe = FindEverythingExe();
                if (exe == null)
                {
                    SetStatus(IndexState.Unavailable, "没有找到 Everything。请先安装 Everything 1.4（voidtools.com）。");
                    return;
                }
                if ((DateTime.UtcNow - lastLaunchUtc).TotalMilliseconds >= LaunchRetryMs)
                {
                    lastLaunchUtc = DateTime.UtcNow;
                    try
                    {
                        // -startup：只在托盘启动，不弹主窗口
                        Process.Start(new ProcessStartInfo(exe, "-startup") { UseShellExecute = true });
                        SetStatus(IndexState.Starting, "正在启动 Everything…");
                    }
                    catch (Win32Exception ex)
                    {
                        SetStatus(IndexState.Unavailable, "启动 Everything 失败：" + ex.Message);
                    }
                }
                else if (status.State == IndexState.Ready || status.State == IndexState.Loading)
                {
                    SetStatus(IndexState.Starting, "正在启动 Everything…");
                }
                return;
            }

            IntPtr loaded;
            if (!SendSimple(hwnd, IPC_IS_DB_LOADED, out loaded))
                SetStatus(IndexState.Unavailable, "Everything 没有响应，稍后自动重试。");
            else if (loaded == IntPtr.Zero)
                SetStatus(IndexState.Loading, "Everything 正在加载数据库…");
            else
                SetStatus(IndexState.Ready, "");
        }

        static string FindEverythingExe()
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432")
            };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string exe = Path.Combine(root, "Everything", "Everything.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        IndexUnavailableException Fail(IndexState state, string message)
        {
            SetStatus(state, message);
            wake.Set();
            return new IndexUnavailableException(message);
        }

        void SetStatus(IndexState state, string message)
        {
            IndexStatus old = status;
            if (old.State == state && old.Message == (message ?? "")) return;
            status = new IndexStatus(state, message);
            EventHandler h = StatusChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        static bool SendSimple(IntPtr hwnd, int command, out IntPtr result)
        {
            IntPtr sent = SendMessageTimeout(hwnd, WM_USER, (IntPtr)command, IntPtr.Zero,
                                             SMTO_ABORTIFHUNG, SimpleTimeoutMs, out result);
            return sent != IntPtr.Zero;
        }

        // ================= 回复窗口 =================

        /// <summary>接收 Everything 回复的 message-only 窗口，跑在自己的线程和消息循环里。</summary>
        sealed class ReplyWindow
        {
            readonly object gate = new object();
            readonly ManualResetEvent created = new ManualResetEvent(false);
            readonly AutoResetEvent replied = new AutoResetEvent(false);
            WndProc proc;          // 保持引用，防止被 GC 回收
            uint threadId;
            uint expected;
            byte[] data;
            string createError;

            public IntPtr Handle { get; private set; }

            /// <summary>建窗口；成功返回 null，失败返回错误说明。</summary>
            public string Start()
            {
                var t = new Thread(Run) { IsBackground = true, Name = "EverythingReply" };
                t.Start();
                created.WaitOne();
                return Handle == IntPtr.Zero ? createError : null;
            }

            public void Stop()
            {
                if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            void Run()
            {
                threadId = GetCurrentThreadId();
                proc = WindowProc;
                string cls = "Comb.EverythingReply." + Process.GetCurrentProcess().Id;
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
                    lpfnWndProc = proc,
                    hInstance = GetModuleHandle(null),
                    lpszClassName = cls
                };
                if (RegisterClassEx(ref wc) == 0)
                {
                    createError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    created.Set();
                    return;
                }
                Handle = CreateWindowEx(0, cls, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (Handle == IntPtr.Zero) createError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                created.Set();
                if (Handle == IntPtr.Zero) return;

                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }

            public void Expect(uint id)
            {
                lock (gate) { expected = id; data = null; }
                replied.Reset();
            }

            /// <summary>等回复；超时返回 null。</summary>
            public byte[] Wait(int timeoutMs)
            {
                bool got = replied.WaitOne(timeoutMs);
                lock (gate)
                {
                    byte[] d = got ? data : null;
                    expected = 0;
                    data = null;
                    return d;
                }
            }

            IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                if (msg == WM_COPYDATA)
                {
                    var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
                    lock (gate)
                    {
                        // dwData 就是请求时填的 reply_copydata_message；对不上的是过期回复，丢掉
                        if (expected != 0 && unchecked((uint)cds.dwData.ToInt64()) == expected && cds.cbData >= 0)
                        {
                            var buf = new byte[cds.cbData];
                            if (cds.cbData > 0) Marshal.Copy(cds.lpData, buf, 0, cds.cbData);
                            data = buf;
                            replied.Set();
                        }
                    }
                    return (IntPtr)1;
                }
                return DefWindowProc(hwnd, msg, wParam, lParam);
            }
        }

        // ================= Win32 =================

        const int SMTO_ABORTIFHUNG = 0x0002;
        const uint WM_QUIT = 0x0012;
        static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
                                                int flags, int timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
                                                int flags, int timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
                                            int x, int y, int width, int height, IntPtr parent, IntPtr menu,
                                            IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string moduleName);
    }
}
