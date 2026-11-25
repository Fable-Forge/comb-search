using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Comb.Engine;

namespace Comb.App
{
    /// <summary>
    /// 已索引内容概览页：嵌在搜索窗口里（搜索框下方），和搜索结果区互相切换。
    /// 布局：标题 + 说明、面包屑卡片、左边矩形树图卡片、右边「名称 / 大小」列表卡片（带返回上层和比例条）、底部汇总。
    /// 面包屑按「此电脑 → 盘 → 文件夹」逐层钻取。数据全部来自引擎（ListChildren），不扫盘。
    /// </summary>
    sealed class OverviewPage : Grid
    {
        const int MaxChildren = 500;
        const string FooterHint = "单击选中 · 双击文件夹进入下一层、双击文件打开 · 搜索框为空时按 Backspace 返回上层";

        /// <summary>面包屑的一层。Path 为 null 表示「此电脑」。</summary>
        sealed class Level
        {
            public string Path;
            public string Label;
            public long KnownSize = -1;
        }

        /// <summary>当前层的一项（树图和列表共用）。</summary>
        sealed class Item
        {
            public FileEntry Entry;     // 「其余 N 项」为 null
            public string Label;
            public long Size;
            public Level Owner;         // 这一项是在哪一层读出来的；不是当前层的旧条目点了不响应
        }

        readonly IFileIndex index;
        readonly List<Level> levels = new List<Level>();
        readonly StackPanel breadcrumb;
        readonly FlatButton upButton;
        readonly FlatButton listUpRow;
        readonly Border hint;
        readonly TreemapView treemap;
        readonly TextBlock overlay;
        readonly StackPanel listPanel;
        readonly ScrollViewer listScroll;
        readonly TextBlock footerLeft, footerRight;
        int loadGeneration;
        long currentTotal = -1;

        // 选中状态：单击选中（树图和列表同步高亮），双击才进入 / 打开
        Item selected;
        readonly Dictionary<Item, Border> rowOf = new Dictionary<Item, Border>();
        readonly Dictionary<Item, TreemapView.Node> nodeOf = new Dictionary<Item, TreemapView.Node>();

        public OverviewPage(IFileIndex index)
        {
            this.index = index;
            Background = Theme.PanelBg;
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            Children.Add(new Border { Background = Theme.Divider });

            var page = new Grid { Margin = new Thickness(24, 16, 24, 14) };
            for (int i = 0; i < 4; i++) page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(page, 1);
            Children.Add(page);

            // ---------- 标题 ----------
            var title = new TextBlock { Text = "已索引内容概览", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text };
            page.Children.Add(title);
            var subtitle = new TextBlock
            {
                Text = "仅统计 Everything 索引中已有的文件和文件夹，不会扫描磁盘。",
                FontSize = 13, Foreground = Theme.SubText, Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(subtitle, 1);
            page.Children.Add(subtitle);

            // ---------- 面包屑卡片：返回上层 + 路径 + 刷新 ----------
            var crumbBar = new DockPanel { LastChildFill = true };
            upButton = new FlatButton(Theme.GlyphUp, null);
            upButton.ToolTip = "返回上层";
            upButton.Click += GoUp;
            DockPanel.SetDock(upButton, Dock.Left);
            crumbBar.Children.Add(upButton);
            var refresh = new FlatButton(Theme.GlyphRefresh, "刷新");
            refresh.Click += Load;
            DockPanel.SetDock(refresh, Dock.Right);
            crumbBar.Children.Add(refresh);
            breadcrumb = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            crumbBar.Children.Add(breadcrumb);
            var crumbCard = Card(crumbBar, new Thickness(6, 4, 6, 4));
            crumbCard.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(crumbCard, 2);
            page.Children.Add(crumbCard);

            // ---------- 提示：Everything 没有索引文件夹大小 ----------
            hint = new Border
            {
                Background = Theme.WarnBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed,
                Child = new TextBlock
                {
                    Foreground = Theme.WarnText, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                    Text = "Everything 没有索引文件夹大小，文件夹的占用无法显示。请在 Everything 里打开「工具 → 选项 → 索引 → 索引文件夹大小」，等索引更新完成后点「刷新」。"
                }
            };
            Grid.SetRow(hint, 3);
            page.Children.Add(hint);

            // ---------- 树图卡片 + 列表卡片 ----------
            var content = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            Grid.SetRow(content, 4);
            page.Children.Add(content);

            var mapHost = new Grid();
            treemap = new TreemapView();
            treemap.NodeClicked += n => Select(n.Tag as Item);
            treemap.NodeDoubleClicked += n => Activate(n.Tag as Item);
            treemap.HoverChanged += n => ShowHover(n == null ? null : n.Tag as Item);
            mapHost.Children.Add(treemap);
            overlay = new TextBlock
            {
                FontSize = 14, Foreground = Theme.SubText, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 420, IsHitTestVisible = false
            };
            mapHost.Children.Add(overlay);
            content.Children.Add(Card(mapHost, new Thickness(14)));

            var listGrid = new Grid();
            listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = new Grid { Margin = new Thickness(10, 2, 16, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var hName = new TextBlock { Text = "名称", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.FaintText };
            var hSize = new TextBlock { Text = "大小", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.FaintText };
            Grid.SetColumn(hSize, 1);
            header.Children.Add(hName);
            header.Children.Add(hSize);
            listGrid.Children.Add(header);
            listUpRow = new FlatButton(Theme.GlyphUp, "返回上层") { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 2) };
            listUpRow.Click += GoUp;
            Grid.SetRow(listUpRow, 1);
            listGrid.Children.Add(listUpRow);
            listPanel = new StackPanel();
            listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel };
            Grid.SetRow(listScroll, 2);
            listGrid.Children.Add(listScroll);
            var listCard = Card(listGrid, new Thickness(8, 12, 4, 8));
            Grid.SetColumn(listCard, 2);
            content.Children.Add(listCard);

            // ---------- 底部：项目数和总大小 ----------
            var footer = new DockPanel { LastChildFill = false };
            footerLeft = new TextBlock { FontSize = 12, Foreground = Theme.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 14, 0) };
            footerRight = new TextBlock { FontSize = 12, Foreground = Theme.FaintText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 24, 0), TextTrimming = TextTrimming.CharacterEllipsis, Text = FooterHint };
            DockPanel.SetDock(footerLeft, Dock.Left);
            DockPanel.SetDock(footerRight, Dock.Right);
            footer.Children.Add(footerLeft);
            footer.Children.Add(footerRight);
            var footerBorder = new Border { Background = Theme.WindowBg, BorderBrush = Theme.Divider, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
            Grid.SetRow(footerBorder, 2);
            Children.Add(footerBorder);

            levels.Add(new Level { Path = null, Label = "此电脑" });
        }

        /// <summary>页面切到前台时调用：重新读取当前层（索引可能变了）。</summary>
        public void Activate()
        {
            Load();
        }

        // ================= 导航 =================

        public void GoUp()
        {
            if (levels.Count <= 1) return;
            levels.RemoveAt(levels.Count - 1);
            Load();
        }

        void GoTo(int levelIndex)
        {
            if (levelIndex >= levels.Count - 1) return;
            levels.RemoveRange(levelIndex + 1, levels.Count - levelIndex - 1);
            Load();
        }

        void DrillInto(Item item)
        {
            if (item == null || item.Entry == null || !item.Entry.IsFolder) return;
            // 新一层还在后台读取时旧列表仍显示着；点到旧条目会把同一个文件夹重复加进面包屑
            // （出现过「A > A」这种不存在的路径）
            if (item.Owner != levels[levels.Count - 1]) return;
            levels.Add(new Level { Path = item.Entry.FullPath, Label = item.Label, KnownSize = item.Entry.Size });
            Load();
        }

        void RebuildBreadcrumb()
        {
            breadcrumb.Children.Clear();
            for (int i = 0; i < levels.Count; i++)
            {
                if (i > 0)
                {
                    breadcrumb.Children.Add(new TextBlock
                    {
                        Text = Theme.GlyphChevron, FontFamily = Theme.IconFont, FontSize = 10, Foreground = Theme.FaintText,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0)
                    });
                }
                int k = i;
                var crumb = new FlatButton(null, levels[i].Label);
                if (i == levels.Count - 1) crumb.SetCurrent();
                else crumb.Click += () => GoTo(k);
                breadcrumb.Children.Add(crumb);
            }
            upButton.Enabled = levels.Count > 1;
            listUpRow.Visibility = levels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================= 读取 =================

        void Load()
        {
            int gen = ++loadGeneration;
            Level level = levels[levels.Count - 1];
            RebuildBreadcrumb();
            overlay.Text = "正在读取…";
            overlay.Visibility = Visibility.Visible;
            footerRight.Text = FooterHint;

            Task.Run(() =>
            {
                ChildList result = null;
                string error = null;
                try
                {
                    result = index.ListChildren(level.Path, MaxChildren);
                    if (level.Path == null) FillVolumeSizes(result);
                }
                catch (IndexUnavailableException ex)
                {
                    error = ex.Message;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != loadGeneration) return;
                    if (result != null) Apply(level, result);
                    else ShowError(error);
                }));
            });
        }

        /// <summary>盘符本身没有大小时，用它的直接子项之和补上（子项里有文件夹大小未知就保持未知）。在后台线程调用。</summary>
        void FillVolumeSizes(ChildList volumes)
        {
            foreach (FileEntry v in volumes.Items)
            {
                if (v.Size >= 0) continue;
                ChildList kids = index.ListChildren(v.FullPath, 5000);
                if (kids.TotalCount > kids.Items.Count) continue;
                long sum = 0;
                bool unknown = false;
                foreach (FileEntry k in kids.Items)
                {
                    if (k.Size >= 0) sum += k.Size;
                    else if (k.IsFolder) { unknown = true; break; }
                }
                if (!unknown) v.Size = sum;
            }
            volumes.Items.Sort((a, b) =>
            {
                int c = b.Size.CompareTo(a.Size);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
        }

        void Apply(Level level, ChildList result)
        {
            bool folderSizeMissing = false;
            long sum = 0, maxSize = 0;
            foreach (FileEntry e in result.Items)
            {
                if (e.Size >= 0) sum += e.Size;
                else if (e.IsFolder) folderSizeMissing = true;
                if (e.Size > maxSize) maxSize = e.Size;
            }
            long total = level.KnownSize >= 0 ? level.KnownSize : folderSizeMissing ? -1 : sum;
            // 总大小未知时不算比例（只按已知部分算出的百分比会误导）
            currentTotal = total;

            var items = new List<Item>();
            foreach (FileEntry e in result.Items)
                items.Add(new Item { Entry = e, Label = e.IsVolume ? e.FullPath : e.Name, Size = e.Size, Owner = level });
            long rest = result.TotalCount - result.Items.Count;
            if (rest > 0)
            {
                long restSize = level.KnownSize >= 0 && level.KnownSize > sum ? level.KnownSize - sum : -1;
                items.Add(new Item { Entry = null, Label = "其余 " + Theme.FormatCount(rest) + " 项", Size = restSize, Owner = level });
            }

            // 树图只画有大小的项，按大小降序。文件和文件夹都按大小取浅黄到文件夹黄，琥珀描边；「其余」用灰色
            selected = null;
            nodeOf.Clear();
            var nodes = new List<TreemapView.Node>();
            foreach (Item it in items)
            {
                if (it.Size <= 0) continue;
                Brush fill, stroke;
                if (it.Entry != null)
                {
                    double t = maxSize > 0 ? Math.Sqrt((double)it.Size / maxSize) : 0;
                    fill = Theme.Lerp(Theme.TileLight, Theme.TileDeep, t);
                    stroke = Theme.TileStroke;
                }
                else { fill = Theme.OtherTileFill; stroke = Theme.OtherTileStroke; }
                var node = new TreemapView.Node { Label = it.Label, Size = it.Size, Fill = fill, Stroke = stroke, Clickable = it.Entry != null, Tag = it };
                nodeOf[it] = node;
                nodes.Add(node);
            }
            nodes.Sort((a, b) => b.Size.CompareTo(a.Size));
            treemap.SetNodes(nodes);

            hint.Visibility = folderSizeMissing ? Visibility.Visible : Visibility.Collapsed;
            if (nodes.Count == 0)
            {
                overlay.Text = result.TotalCount == 0 ? "这里是空的。"
                             : folderSizeMissing ? "文件夹大小未知，暂时画不出矩形树图。右侧列表仍可逐层浏览。"
                             : "这里的项目都没有大小。";
                overlay.Visibility = Visibility.Visible;
            }
            else
            {
                overlay.Visibility = Visibility.Collapsed;
            }

            BuildList(items);

            string totalText = total >= 0 ? Theme.FormatSize(total) : "总大小未知（文件夹大小没有索引）";
            footerLeft.Text = Theme.FormatCount(result.TotalCount) + " 个项目 · " + totalText
                            + (rest > 0 ? " · 列出最大的 " + result.Items.Count + " 项" : "");
        }

        void ShowError(string message)
        {
            treemap.SetNodes(null);
            listPanel.Children.Clear();
            hint.Visibility = Visibility.Collapsed;
            overlay.Text = message;
            overlay.Visibility = Visibility.Visible;
            footerLeft.Text = "";
        }

        void BuildList(List<Item> items)
        {
            listPanel.Children.Clear();
            rowOf.Clear();
            foreach (Item it in items) listPanel.Children.Add(BuildRow(it));
            listScroll.ScrollToTop();
        }

        FrameworkElement BuildRow(Item it)
        {
            double pct = currentTotal > 0 && it.Size > 0 ? Math.Min(1, (double)it.Size / currentTotal) : 0;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string glyph = it.Entry == null ? Theme.GlyphPage
                         : it.Entry.IsVolume ? Theme.GlyphDrive
                         : it.Entry.IsFolder ? Theme.GlyphFolder : Theme.GlyphPage;
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = Theme.IconFont, FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = it.Entry != null ? Theme.FolderIcon : Theme.FileIcon   // 文件、文件夹、磁盘图标都用文件夹黄线条（用户 2026-11-25 要求）
            };
            var name = new TextBlock { Text = it.Label, FontSize = 14, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var size = new TextBlock
            {
                Text = it.Size >= 0 ? Theme.FormatSize(it.Size) : "—", FontSize = 14, Foreground = Theme.SubText,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(size, 2);
            top.Children.Add(icon);
            top.Children.Add(name);
            top.Children.Add(size);
            grid.Children.Add(top);

            // 比例条：两列按比例分宽，不用手算像素
            var bar = new Grid { Height = 3, Margin = new Thickness(26, 7, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pct, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - pct, GridUnitType.Star) });
            var track = new Border { Background = Theme.BarTrack, CornerRadius = new CornerRadius(2) };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            if (pct > 0) bar.Children.Add(new Border { Background = Theme.BarFill, CornerRadius = new CornerRadius(2) });
            Grid.SetRow(bar, 1);
            grid.Children.Add(bar);

            var row = new Border
            {
                Child = grid, Padding = new Thickness(10, 8, 12, 8), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 1, 6, 1)
            };
            rowOf[it] = row;
            bool hovering = false;
            StyleRow(row, false, false);
            row.MouseEnter += (s, e) => { hovering = true; StyleRow(row, it == selected, true); ShowHover(it); };
            row.MouseLeave += (s, e) => { hovering = false; StyleRow(row, it == selected, false); ShowHover(null); };
            row.Tag = (Func<bool>)(() => hovering);
            // 单击选中；双击：文件夹进入下一层，文件打开（和资源管理器一致）
            if (it.Entry != null)
            {
                row.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 1) Select(it);
                    else if (e.ClickCount == 2) Activate(it);
                    e.Handled = true;
                };
                row.MouseRightButtonUp += (s, e) => { Select(it); ShowItemMenu(it); e.Handled = true; };
            }
            return row;
        }

        // ================= 选中 / 双击 =================

        /// <summary>选中一项：列表行和树图方块同步高亮，列表滚到这一行。</summary>
        void Select(Item it)
        {
            if (it == null || it.Owner != levels[levels.Count - 1]) return;
            Item old = selected;
            selected = it;
            Border oldRow;
            if (old != null && rowOf.TryGetValue(old, out oldRow)) StyleRow(oldRow, false, IsHovering(oldRow));
            Border row;
            if (rowOf.TryGetValue(it, out row))
            {
                StyleRow(row, true, IsHovering(row));
                row.BringIntoView();
            }
            TreemapView.Node node;
            treemap.Selected = nodeOf.TryGetValue(it, out node) ? node : null;
            ShowHover(null);
        }

        /// <summary>双击：文件夹进入下一层；文件用默认程序打开，没有默认程序就打开所在文件夹并选中它（用户要求）。</summary>
        void Activate(Item it)
        {
            if (it == null || it.Entry == null) return;
            if (it.Entry.IsFolder) { DrillInto(it); return; }
            OpenFileOrLocation(it.Entry);
        }

        /// <summary>不隐藏窗口，方便接着浏览。</summary>
        void OpenFileOrLocation(FileEntry entry)
        {
            if (!Native.HasDefaultOpenAction(entry.FullPath)) { OpenLocation(entry); return; }
            try
            {
                Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
            }
            catch (Win32Exception ex)
            {
                // 例如文件已被删除、索引还没更新
                footerRight.Text = "无法打开：" + ex.Message;
            }
        }

        static void StyleRow(Border row, bool isSelected, bool hover)
        {
            row.Background = isSelected ? Theme.RowSelectedBg : hover ? Theme.HoverBg : Brushes.Transparent;
            row.BorderBrush = isSelected ? Theme.RowSelectedBorder : Brushes.Transparent;
        }

        static bool IsHovering(Border row)
        {
            var f = row.Tag as Func<bool>;
            return f != null && f();
        }

        // ================= 右键菜单 =================

        DispatcherTimer reloadTimer;

        void ShowItemMenu(Item it)
        {
            if (it == null || it.Entry == null) return;
            var owner = PresentationSource.FromVisual(this) as HwndSource;
            if (owner == null) return;
            FileEntry entry = it.Entry;
            int x, y;
            ShellMenu.CursorPosition(out x, out y);
            ShellMenu.Result r = ShellMenu.Show(owner, entry.FullPath, entry.IsVolume, ShellMenu.CanRunAsAdmin(entry.Name, entry.IsFolder), x, y);
            switch (r.Builtin)
            {
                case ShellMenu.Command.Open: OpenWithShell(entry, false); break;
                case ShellMenu.Command.OpenAsAdmin: OpenWithShell(entry, true); break;
                case ShellMenu.Command.OpenLocation: OpenLocation(entry); break;
                case ShellMenu.Command.CopyName: CopyText(owner.Handle, entry.Name); break;
                case ShellMenu.Command.CopyPath: CopyText(owner.Handle, entry.FullPath); break;
            }
            if (r.Error != null) footerRight.Text = r.Error;
            // 删除、剪切等外壳操作后，等 Everything 更新索引再刷新当前层
            if (r.ShellInvoked) ScheduleReload();
        }


        /// <summary>菜单里点「打开」：交给系统处理（没有关联程序时系统会让用户选，这是用户主动点的）。</summary>
        void OpenWithShell(FileEntry entry, bool admin)
        {
            try
            {
                var psi = new ProcessStartInfo(entry.FullPath) { UseShellExecute = true };
                if (admin) psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return;   // ERROR_CANCELLED：用户在 UAC 里点了取消
                footerRight.Text = "无法打开：" + ex.Message;
            }
        }

        void OpenLocation(FileEntry entry)
        {
            try
            {
                string args = entry.IsVolume ? "\"" + entry.FullPath + "\"" : "/select,\"" + entry.FullPath + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            }
            catch (Win32Exception ex)
            {
                footerRight.Text = "无法打开所在文件夹：" + ex.Message;
            }
        }

        void CopyText(IntPtr owner, string text)
        {
            try
            {
                Native.SetClipboardText(owner, text);
                footerRight.Text = "已复制：" + text;
            }
            catch (Win32Exception ex)
            {
                footerRight.Text = "复制失败：" + ex.Message;
            }
        }

        void ScheduleReload()
        {
            if (reloadTimer == null)
            {
                reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); Load(); };
            }
            reloadTimer.Stop();
            reloadTimer.Start();
        }

        void ShowHover(Item it)
        {
            if (it == null) it = selected;   // 鼠标移开时显示选中项的信息
            if (it == null) { footerRight.Text = FooterHint; return; }
            string where = it.Entry != null ? it.Entry.FullPath : it.Label;
            string text = where + " · " + (it.Size >= 0 ? Theme.FormatSize(it.Size) : "大小未知");
            if (currentTotal > 0 && it.Size > 0) text += " · " + ((double)it.Size / currentTotal * 100).ToString("0.0") + "%";
            footerRight.Text = text;
        }

        /// <summary>白底圆角描边卡片。</summary>
        static Border Card(UIElement child, Thickness padding)
        {
            return new Border
            {
                Background = Theme.CardBg, BorderBrush = Theme.CardBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = padding, Child = child
            };
        }
    }

    /// <summary>扁平按钮：图标 + 文字，悬停变色。</summary>
    sealed class FlatButton : Border
    {
        readonly TextBlock label;
        bool enabled = true;
        bool current;
        bool pressed;   // 鼠标是在这个按钮上按下的

        public event Action Click;

        public FlatButton(string glyph, string text)
        {
            Padding = new Thickness(9, 6, 9, 6);
            CornerRadius = new CornerRadius(6);
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph != null)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = glyph, FontFamily = Theme.IconFont, FontSize = 14, Foreground = Theme.SubText,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, text != null ? 8 : 0, 0)
                });
            }
            label = new TextBlock { Text = text ?? "", FontSize = 14, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
            if (text != null) panel.Children.Add(label);
            Child = panel;

            MouseEnter += (s, e) => { if (enabled && !current) Background = Theme.HoverBg; };
            MouseLeave += (s, e) => { Background = Brushes.Transparent; pressed = false; };
            // 按下和松开都在按钮上才算一次点击（和系统按钮一样）。只认松开的话：从根层双击第一行进入下一层时，
            // 「返回上层」刚好出现在第一行的位置，第二下的松开落在它上面，刚进去就又退回来（2026-11-25 截图时发现）
            MouseLeftButtonDown += (s, e) => { if (enabled && !current) pressed = true; };
            MouseLeftButtonUp += (s, e) =>
            {
                if (!pressed) return;
                pressed = false;
                Action h = Click;
                if (enabled && !current && h != null) h();
            };
        }

        public bool Enabled
        {
            set
            {
                enabled = value;
                Opacity = value ? 1 : 0.4;
                Cursor = value ? Cursors.Hand : null;
            }
        }

        /// <summary>面包屑的当前层：加粗、不可点。</summary>
        public void SetCurrent()
        {
            current = true;
            label.FontWeight = FontWeights.SemiBold;
            Cursor = null;
        }
    }
}
