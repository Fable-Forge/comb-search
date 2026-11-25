using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Comb.Engine;

namespace Comb.App
{
    /// <summary>
    /// 一次搜索的结果数据：按 offset 分页向引擎取，只取看得到的页。
    /// 只在 UI 线程上调用；取数放到后台线程，结果回到 UI 线程。
    /// </summary>
    sealed class ResultSource
    {
        public const int PageSize = 100;

        readonly IFileIndex index;
        readonly SearchQuery query;
        readonly SortOrder sort;
        readonly Dispatcher dispatcher;
        readonly Dictionary<int, List<FileEntry>> pages = new Dictionary<int, List<FileEntry>>();
        readonly HashSet<int> loading = new HashSet<int>();
        readonly Dictionary<int, DateTime> retryAfter = new Dictionary<int, DateTime>();
        readonly object visibleGate = new object();
        long visibleFirst, visibleLast;
        volatile bool closed;

        public readonly long Total;

        /// <summary>有新页到达（或取页失败）时触发，用来重绘。</summary>
        public event Action Changed;

        public ResultSource(IFileIndex index, SearchQuery query, SortOrder sort, SearchPage firstPage, Dispatcher dispatcher)
        {
            this.index = index;
            this.query = query;
            this.sort = sort;
            this.dispatcher = dispatcher;
            Total = firstPage.Total;
            pages[firstPage.Offset / PageSize] = firstPage.Items;
        }

        /// <summary>取第 i 条；还没取到时返回 null 并在后台请求所在页。</summary>
        public FileEntry Get(long i)
        {
            if (i < 0 || i >= Total) return null;
            int p = (int)(i / PageSize);
            List<FileEntry> items;
            if (pages.TryGetValue(p, out items))
            {
                int k = (int)(i - (long)p * PageSize);
                return k < items.Count ? items[k] : null;
            }
            Request(p);
            return null;
        }

        /// <summary>列表告诉数据源当前看得到哪些行，排队中的过期页请求会被跳过。</summary>
        public void SetVisible(long first, long last)
        {
            lock (visibleGate) { visibleFirst = first; visibleLast = last; }
        }

        public void Close()
        {
            closed = true;
        }

        void Request(int p)
        {
            if (closed || loading.Contains(p)) return;
            DateTime t;
            if (retryAfter.TryGetValue(p, out t) && DateTime.UtcNow < t) return;
            loading.Add(p);

            Task.Run(() =>
            {
                long pageStart = (long)p * PageSize;
                long first, last;
                lock (visibleGate) { first = visibleFirst; last = visibleLast; }
                // 快速拖动滚动条时，排到这里已经看不到的页就不取了
                if (closed || pageStart + PageSize - 1 < first - PageSize || pageStart > last + PageSize)
                {
                    dispatcher.BeginInvoke(new Action(() => loading.Remove(p)));
                    return;
                }

                SearchPage result = null;
                try { result = index.Search(query, (int)pageStart, PageSize, sort); }
                catch (IndexUnavailableException) { }   // 引擎状态由引擎自己上报，这里只安排稍后重试

                dispatcher.BeginInvoke(new Action(() =>
                {
                    loading.Remove(p);
                    if (closed) return;
                    if (result != null) pages[p] = result.Items;
                    else
                    {
                        retryAfter[p] = DateTime.UtcNow.AddSeconds(2);
                        Task.Delay(2100).ContinueWith(_ => dispatcher.BeginInvoke(new Action(RaiseChanged)));
                    }
                    RaiseChanged();
                }));
            });
        }

        void RaiseChanged()
        {
            if (closed) return;
            Action h = Changed;
            if (h != null) h();
        }
    }

    /// <summary>
    /// 自绘的虚拟滚动结果列表：只画看得到的行。列：图标、名称（匹配部分高亮）、路径、大小、修改时间。
    /// 不拿键盘焦点（焦点一直留在搜索框），键盘操作由窗口转发过来。
    /// </summary>
    sealed class ResultList : FrameworkElement
    {
        public const double RowHeight = 30;
        public const double HeaderHeight = 30;
        const double Pad = 14, IconW = 26, DateW = 126, SizeW = 84, TypeW = 96, Gap = 14;

        /// <summary>可点击排序的列，按显示顺序：名称、路径、修改时间、大小、类型。</summary>
        public enum Column { Name, Path, Modified, Size, Type }

        /// <summary>某一列在当前宽度下的位置。</summary>
        struct Layout
        {
            public double IconX, NameX, NameW, PathX, PathW, DateX, SizeX, TypeX;
        }

        Column? sortColumn;          // null = 默认顺序，不显示箭头
        bool sortDescending;

        readonly ScrollBar scroll;
        ResultSource source;
        string message;
        long top;
        long selected = -1;
        long hover = -1;
        int wheelAccum;

        public event Action SelectionChanged;
        public event Action<FileEntry> OpenRequested;
        /// <summary>点了列头。</summary>
        public event Action<Column> HeaderClicked;
        /// <summary>在某一行上右键（行已被选中）。</summary>
        public event Action<FileEntry> RowRightClicked;

        /// <summary>列头上显示排序箭头（▲ 升序 / ▼ 降序）；column 为 null 表示默认顺序。</summary>
        public void SetSortIndicator(Column? column, bool descending)
        {
            sortColumn = column;
            sortDescending = descending;
            InvalidateVisual();
        }

        Layout ComputeLayout(double w)
        {
            double flex = Math.Max(160, w - Pad * 2 - IconW - DateW - SizeW - TypeW - Gap * 4);
            var l = new Layout();
            l.IconX = Pad;
            l.NameX = Pad + IconW;
            l.NameW = flex * 0.45;
            l.PathX = l.NameX + l.NameW + Gap;
            l.PathW = flex - l.NameW;
            l.DateX = l.PathX + l.PathW + Gap;
            l.SizeX = l.DateX + DateW + Gap;
            l.TypeX = l.SizeX + SizeW + Gap;
            return l;
        }

        /// <summary>列头上某个横坐标属于哪一列（以列间空隙的中线为界）。</summary>
        Column ColumnAt(double x)
        {
            Layout l = ComputeLayout(ActualWidth);
            if (x < l.PathX - Gap / 2) return Column.Name;
            if (x < l.DateX - Gap / 2) return Column.Path;
            if (x < l.SizeX - Gap / 2) return Column.Modified;
            if (x < l.TypeX - Gap / 2) return Column.Size;
            return Column.Type;
        }

        public ResultList(ScrollBar scroll)
        {
            this.scroll = scroll;
            scroll.Orientation = System.Windows.Controls.Orientation.Vertical;
            scroll.Minimum = 0;
            scroll.SmallChange = 1;
            scroll.ValueChanged += (s, e) =>
            {
                long v = (long)Math.Round(e.NewValue);
                if (v != top) { top = v; InvalidateVisual(); }
            };
            ClipToBounds = true;
            Focusable = false;
            SnapsToDevicePixels = true;
        }

        public long Total { get { return source == null ? 0 : source.Total; } }
        public long SelectedIndex { get { return selected; } }
        public FileEntry SelectedEntry { get { return source != null && selected >= 0 ? source.Get(selected) : null; } }

        /// <summary>一页能完整显示的行数（PageUp / PageDown 用）。</summary>
        public int FullRows { get { return Math.Max(1, (int)Math.Floor((ActualHeight - HeaderHeight) / RowHeight)); } }

        int VisibleRows { get { return Math.Max(1, (int)Math.Ceiling((ActualHeight - HeaderHeight) / RowHeight)); } }

        /// <summary>换一批结果；s 为 null 时显示 msg（错误或提示）。</summary>
        public void SetSource(ResultSource s, string msg)
        {
            if (source != null)
            {
                source.Changed -= InvalidateVisual;
                source.Close();
            }
            source = s;
            message = msg;
            top = 0;
            hover = -1;
            selected = s != null && s.Total > 0 ? 0 : -1;
            if (s != null) s.Changed += InvalidateVisual;
            UpdateScroll();
            InvalidateVisual();
            RaiseSelectionChanged();
        }

        public void MoveSelection(long delta)
        {
            if (source == null || source.Total == 0) return;
            long n = selected < 0 ? 0 : selected + delta;
            n = Math.Max(0, Math.Min(source.Total - 1, n));
            Select(n, true);
        }

        void Select(long i, bool ensureVisible)
        {
            selected = i;
            if (ensureVisible) EnsureVisible(i);
            InvalidateVisual();
            RaiseSelectionChanged();
        }

        void EnsureVisible(long i)
        {
            int full = FullRows;
            if (i < top) SetTop(i);
            else if (i >= top + full) SetTop(i - full + 1);
        }

        void SetTop(long t)
        {
            long max = Math.Max(0, Total - FullRows);
            t = Math.Max(0, Math.Min(max, t));
            if (t == top) return;
            top = t;
            scroll.Value = t;
            InvalidateVisual();
        }

        void UpdateScroll()
        {
            int full = FullRows;
            long total = Total;
            scroll.Maximum = Math.Max(0, total - full);
            scroll.ViewportSize = full;
            scroll.LargeChange = full;
            scroll.Value = top;
            scroll.Visibility = total > full ? Visibility.Visible : Visibility.Collapsed;
        }

        void RaiseSelectionChanged()
        {
            Action h = SelectionChanged;
            if (h != null) h();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateScroll();
            SetTop(top);
        }

        // ---------- 鼠标 ----------

        long RowAt(Point p)
        {
            if (source == null || p.Y < HeaderHeight) return -1;
            long i = top + (long)((p.Y - HeaderHeight) / RowHeight);
            return i < source.Total ? i : -1;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // 每 120 滚 3 行；精确触控板的小增量累积起来
            wheelAccum += e.Delta;
            int rows = wheelAccum / 40;
            wheelAccum -= rows * 40;
            if (rows != 0) SetTop(top - rows);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point p = e.GetPosition(this);
            Cursor = p.Y < HeaderHeight ? Cursors.Hand : null;   // 列头可点击排序
            long h = RowAt(p);
            if (h != hover) { hover = h; InvalidateVisual(); }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (hover != -1) { hover = -1; InvalidateVisual(); }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (p.Y < HeaderHeight)
            {
                Action<Column> hc = HeaderClicked;
                if (hc != null) hc(ColumnAt(p.X));
                e.Handled = true;
                return;
            }
            long i = RowAt(p);
            if (i < 0) return;
            Select(i, true);
            if (e.ClickCount == 2)
            {
                FileEntry entry = source.Get(i);
                Action<FileEntry> h = OpenRequested;
                if (entry != null && h != null) h(entry);
            }
            e.Handled = true;
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            long i = RowAt(e.GetPosition(this));
            if (i >= 0) Select(i, false);
        }

        /// <summary>右键松开时请求弹出菜单（空白处不弹）。</summary>
        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            long i = RowAt(e.GetPosition(this));
            if (i < 0) return;
            FileEntry entry = source.Get(i);
            Action<FileEntry> h = RowRightClicked;
            if (entry != null && h != null) { h(entry); e.Handled = true; }
        }

        // ---------- 绘制 ----------

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Theme.WindowBg, null, new Rect(0, 0, w, h));
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // 列位置
            Layout l = ComputeLayout(w);
            double iconX = l.IconX, nameX = l.NameX, nameW = l.NameW, pathX = l.PathX, pathW = l.PathW;
            double dateX = l.DateX, sizeX = l.SizeX, typeX = l.TypeX;

            // 表头（排序中的列带 ▲ / ▼，并用强调色）
            DrawHeader(dc, "名称", Column.Name, nameX, nameW, false, dip);
            DrawHeader(dc, "路径", Column.Path, pathX, pathW, false, dip);
            DrawHeader(dc, "修改时间", Column.Modified, dateX, DateW, false, dip);
            DrawHeader(dc, "大小", Column.Size, sizeX, SizeW, true, dip);
            DrawHeader(dc, "类型", Column.Type, typeX, TypeW, false, dip);
            dc.DrawRectangle(Theme.Divider, null, new Rect(0, HeaderHeight - 1, w, 1));

            if (source == null || source.Total == 0)
            {
                string text = message ?? (source != null ? "没有找到匹配的项目" : "");
                if (text.Length > 0)
                {
                    var ft = MakeText(text, 13, Theme.SubText, Theme.UiFace, Math.Max(1, w - 40), dip);
                    ft.TextAlignment = TextAlignment.Center;
                    ft.MaxLineCount = 3;
                    dc.DrawText(ft, new Point(20, HeaderHeight + 40));
                }
                return;
            }

            long last = Math.Min(source.Total - 1, top + VisibleRows - 1);
            source.SetVisible(top, last);
            for (long i = top; i <= last; i++)
            {
                double y = HeaderHeight + (i - top) * RowHeight;
                var rowRect = new Rect(6, y + 1, Math.Max(0, w - 12), RowHeight - 2);
                if (i == selected) dc.DrawRoundedRectangle(Theme.SelectedBg, null, rowRect, 4, 4);
                else if (i == hover) dc.DrawRoundedRectangle(Theme.HoverBg, null, rowRect, 4, 4);

                FileEntry e = source.Get(i);
                if (e == null)
                {
                    DrawCell(dc, "…", nameX, y, nameW, RowHeight, 13, Theme.FaintText, Theme.UiFace, dip, false);
                    continue;
                }

                string glyph = e.IsVolume ? Theme.GlyphDrive : e.IsFolder ? Theme.GlyphFolder : Theme.GlyphPage;
                Brush iconBrush = e.IsFolder && !e.IsVolume ? Theme.FolderIcon : Theme.FileIcon;
                DrawCell(dc, glyph, iconX, y, IconW, RowHeight, 15, iconBrush, Theme.IconFace, dip, false);

                // 名称：匹配部分加粗并用强调色
                var name = MakeText(e.Name, 13, Theme.Text, Theme.UiFace, nameW, dip);
                if (e.Highlights != null)
                {
                    for (int k = 0; k + 1 < e.Highlights.Length; k += 2)
                    {
                        int start = e.Highlights[k], len = e.Highlights[k + 1];
                        if (start < 0 || len <= 0 || start + len > e.Name.Length) continue;
                        name.SetFontWeight(FontWeights.SemiBold, start, len);
                        name.SetForegroundBrush(Theme.Accent, start, len);
                    }
                }
                dc.DrawText(name, new Point(nameX, y + (RowHeight - name.Height) / 2));

                DrawCell(dc, e.Folder, pathX, y, pathW, RowHeight, 12, Theme.SubText, Theme.UiFace, dip, false);
                DrawCell(dc, Theme.FormatDate(e.Modified), dateX, y, DateW, RowHeight, 12, Theme.SubText, Theme.UiFace, dip, false);
                DrawCell(dc, Theme.FormatSize(e.Size), sizeX, y, SizeW, RowHeight, 12, Theme.SubText, Theme.UiFace, dip, true);
                DrawCell(dc, Theme.FormatType(e), typeX, y, TypeW, RowHeight, 12, Theme.SubText, Theme.UiFace, dip, false);
            }
        }

        void DrawHeader(DrawingContext dc, string title, Column column, double x, double width, bool alignRight, double dip)
        {
            bool sorted = sortColumn.HasValue && sortColumn.Value == column;
            string text = sorted ? title + (sortDescending ? " ▼" : " ▲") : title;
            DrawCell(dc, text, x, 0, width, HeaderHeight, 12, sorted ? Theme.Accent : Theme.FaintText,
                     sorted ? Theme.UiFaceBold : Theme.UiFace, dip, alignRight);
        }

        static void DrawCell(DrawingContext dc, string text, double x, double y, double width, double height,
                             double size, Brush brush, Typeface face, double dip, bool alignRight)
        {
            if (string.IsNullOrEmpty(text) || width < 4) return;
            var ft = MakeText(text, size, brush, face, width, dip);
            if (alignRight) ft.TextAlignment = TextAlignment.Right;
            dc.DrawText(ft, new Point(x, y + (height - ft.Height) / 2));
        }

        static FormattedText MakeText(string text, double size, Brush brush, Typeface face, double maxWidth, double dip)
        {
            var ft = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dip);
            ft.MaxTextWidth = Math.Max(1, maxWidth);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            return ft;
        }
    }
}
