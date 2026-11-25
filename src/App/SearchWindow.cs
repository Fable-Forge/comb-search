using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Comb.Engine;
using Forms = System.Windows.Forms;

namespace Comb.App
{
    /// <summary>
    /// 浮动搜索条、结果窗口和内容概览是同一个窗口的三种形态，共用顶部的搜索框：
    /// 输入框为空时是屏幕上部居中的圆角搜索条；有输入时向下展开成结果窗口（左侧筛选栏 + 结果列表 + 状态栏）；
    /// 点搜索条右侧的概览按钮（或托盘菜单「内容概览」）展开成概览页，在概览页里打字就切到搜索结果。
    /// </summary>
    sealed class SearchWindow : Window
    {
        enum Mode { Bar, Results, Overview }

        const double BarWidth = 680, BarContentHeight = 58;
        const double BarHeight = BarContentHeight;
        const double MinExpandedWidth = 640, MinExpandedHeight = 360;
        const int DebounceMs = 90;

        static readonly KindFilter[] Kinds = { KindFilter.Folder, KindFilter.File, KindFilter.Document, KindFilter.Image, KindFilter.Video };
        static readonly string[] KindNames = { "文件夹", "文件", "文档", "图片", "视频" };
        static readonly string[] KindGlyphs = { Theme.GlyphFolder, Theme.GlyphPage, Theme.GlyphDocument, Theme.GlyphPhoto, Theme.GlyphVideo };
        static readonly DateFilter[] Dates = { DateFilter.LastDay, DateFilter.LastWeek, DateFilter.LastMonth };
        static readonly string[] DateNames = { "最近 1 天", "最近 7 天", "最近 30 天" };
        static readonly SizeFilter[] Sizes = { SizeFilter.Small, SizeFilter.Medium, SizeFilter.Large };
        static readonly string[] SizeNames = { "小文件 (< 1MB)", "中等文件 (1MB - 100MB)", "超大文件 (> 100MB)" };

        readonly IFileIndex index;
        readonly TextBox input;
        readonly TextBlock placeholder;
        readonly TextBlock barStatus;
        readonly Grid body;
        readonly OverviewPage overviewPage;
        readonly Border modeButton;
        readonly TextBlock modeGlyph;
        readonly ResultList list;
        readonly TextBlock statusCount, statusSelection, statusHint;
        readonly FilterItem[] kindItems = new FilterItem[Kinds.Length];
        readonly FilterItem[] dateItems = new FilterItem[Dates.Length];
        readonly FilterItem[] sizeItems = new FilterItem[Sizes.Length];
        readonly DispatcherTimer debounce;
        // 只建一次；切换形态时只改缩放边框宽度（每次换新的 WindowChrome 会让窗口框架重算、闪一下）
        readonly WindowChrome chrome;
        readonly DispatcherTimer noticeTimer;

        KindFilter kind = KindFilter.None;
        DateFilter date = DateFilter.None;
        SizeFilter sizes = SizeFilter.None;          // 文件大小可以多选
        ResultList.Column? sortColumn;               // null = 默认顺序（名称升序）
        bool sortDescending;
        volatile int generation;
        Mode mode = Mode.Bar;
        bool allowClose;
        bool clearingForOverview;   // 切到概览时清空搜索框，不触发收回搜索条
        bool lastSearchFailed;
        int pendingOpen;          // 0 无；1 结果到了就打开；2 结果到了就打开所在位置
        // 结果和概览共用一份尺寸：两者互相切换时窗口不跳动（用户要求，2026-11-21）
        double expandedWidth = 1180, expandedHeight = 760;
        Rect workArea = SystemParameters.WorkArea;

        public SearchWindow(IFileIndex index)
        {
            this.index = index;

            Title = "Comb";
            // 不设 Icon：WPF 默认用 exe 自带的图标（tools/IconGen.cs 生成的黄色放大镜）
            ShowInTaskbar = false;
            Topmost = true;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = BarWidth;
            Height = BarHeight;
            MinWidth = BarWidth;
            Background = Theme.WindowBg;
            FontFamily = Theme.UiFont;
            UseLayoutRounding = true;
            // 不用 AllowsTransparency：WindowChrome 去掉标题栏，DWM 负责圆角和阴影
            chrome = new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);

            // ---------- 搜索条 ----------
            var bar = new Grid { Height = BarContentHeight, Background = Brushes.Transparent };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var searchIcon = new TextBlock
            {
                Text = Theme.GlyphSearch, FontFamily = Theme.IconFont, FontSize = 18, Foreground = Theme.FaintText,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 12, 0)
            };
            bar.Children.Add(searchIcon);

            input = new TextBox
            {
                FontSize = 18, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = Theme.Text, CaretBrush = Theme.Accent, VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 0, 16, 0)
            };
            placeholder = new TextBlock
            {
                Text = "搜索文件和文件夹", FontSize = 18, Foreground = Theme.FaintText, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0)
            };
            var inputHost = new Grid();
            inputHost.Children.Add(placeholder);
            inputHost.Children.Add(input);
            Grid.SetColumn(inputHost, 1);
            bar.Children.Add(inputHost);

            barStatus = new TextBlock
            {
                FontSize = 12, Foreground = Theme.WarnText, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0), MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(barStatus, 2);
            bar.Children.Add(barStatus);

            // 右侧按钮：搜索条 / 结果状态下打开概览，概览状态下返回搜索条
            modeGlyph = new TextBlock
            {
                FontFamily = Theme.IconFont, FontSize = 16, Foreground = Theme.SubText,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            modeButton = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent,
                Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = modeGlyph
            };
            modeButton.MouseEnter += (s, e) => modeButton.Background = Theme.HoverBg;
            modeButton.MouseLeave += (s, e) => modeButton.Background = Brushes.Transparent;
            modeButton.MouseLeftButtonUp += (s, e) =>
            {
                if (mode == Mode.Overview) { SetMode(Mode.Bar, false); input.Focus(); }
                else ShowOverview();
            };
            Grid.SetColumn(modeButton, 3);
            bar.Children.Add(modeButton);

            // 没有标题栏，按住搜索条空白处拖动窗口
            bar.MouseLeftButtonDown += (s, e) =>
            {
                if (!input.IsMouseOver && !modeButton.IsMouseOver && e.ButtonState == MouseButtonState.Pressed) DragMove();
            };

            // ---------- 结果区：筛选栏 + 列表 + 状态栏 ----------
            body = new Grid { Visibility = Visibility.Collapsed };
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            body.Children.Add(new Border { Background = Theme.Divider });

            var middle = new Grid();
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(middle, 1);
            body.Children.Add(middle);

            var sidebar = new StackPanel();
            sidebar.Children.Add(GroupHeader("类型"));
            for (int i = 0; i < Kinds.Length; i++)
            {
                int k = i;
                kindItems[i] = new FilterItem(KindNames[i], KindGlyphs[i]);
                kindItems[i].Click += () => OnKindClick(k);
                sidebar.Children.Add(kindItems[i]);
            }
            sidebar.Children.Add(GroupHeader("修改时间"));
            for (int i = 0; i < Dates.Length; i++)
            {
                int d = i;
                dateItems[i] = new FilterItem(DateNames[i], Theme.GlyphClock);
                dateItems[i].Click += () => OnDateClick(d);
                sidebar.Children.Add(dateItems[i]);
            }
            sidebar.Children.Add(GroupHeader("文件大小"));
            for (int i = 0; i < Sizes.Length; i++)
            {
                int z = i;
                sizeItems[i] = new FilterItem(SizeNames[i], Theme.GlyphDot);
                sizeItems[i].Click += () => OnSizeClick(z);
                sidebar.Children.Add(sizeItems[i]);
            }
            middle.Children.Add(new Border { Background = Theme.PanelBg, Child = sidebar });
            var sideDivider = new Border { Background = Theme.Divider };
            Grid.SetColumn(sideDivider, 1);
            middle.Children.Add(sideDivider);

            var scroll = new ScrollBar();
            list = new ResultList(scroll);
            Grid.SetColumn(list, 2);
            Grid.SetColumn(scroll, 3);
            middle.Children.Add(list);
            middle.Children.Add(scroll);
            list.SelectionChanged += UpdateStatusBar;
            list.OpenRequested += Open;
            list.RowRightClicked += ShowItemMenu;
            list.HeaderClicked += OnHeaderClicked;

            var status = new DockPanel { LastChildFill = false };
            statusCount = StatusText();
            statusSelection = StatusText();
            statusHint = StatusText();
            statusHint.Text = "↑↓ 选择    Enter 打开    Ctrl+Enter 打开所在位置    Esc 隐藏";
            statusHint.Foreground = Theme.FaintText;
            DockPanel.SetDock(statusCount, Dock.Left);
            DockPanel.SetDock(statusSelection, Dock.Left);
            DockPanel.SetDock(statusHint, Dock.Right);
            status.Children.Add(statusCount);
            status.Children.Add(statusSelection);
            status.Children.Add(statusHint);
            var statusBorder = new Border
            {
                Background = Theme.PanelBg, BorderBrush = Theme.Divider, BorderThickness = new Thickness(0, 1, 0, 0),
                Child = status
            };
            Grid.SetRow(statusBorder, 2);
            body.Children.Add(statusBorder);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(bar);
            Grid.SetRow(body, 1);
            root.Children.Add(body);
            overviewPage = new OverviewPage(index) { Visibility = Visibility.Collapsed };
            Grid.SetRow(overviewPage, 1);
            root.Children.Add(overviewPage);
            // 内容按 DWM 的窗口圆角（8px）裁剪，免得侧栏、状态栏的直角露在圆角外（用户不要外围黄框，2026-11-25 去掉）
            root.SizeChanged += (s, e) =>
                root.Clip = new RectangleGeometry(new Rect(root.RenderSize), 8, 8);
            root.Background = Theme.WindowBg;
            Content = root;

            // ---------- 行为 ----------
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
            debounce.Tick += (s, e) => RunSearch();
            noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            noticeTimer.Tick += (s, e) => { noticeTimer.Stop(); UpdateStatusBar(); };

            input.TextChanged += OnTextChanged;
            PreviewKeyDown += OnPreviewKeyDown;
            Deactivated += OnDeactivated;
            SourceInitialized += (s, e) =>
            {
                Native.SetRoundCorners(new WindowInteropHelper(this).Handle);
            };
            index.StatusChanged += (s, e) => Dispatcher.BeginInvoke(new Action(OnEngineStatusChanged));
            OnEngineStatusChanged();
            UpdateModeButton();
            // 先建好窗口句柄（不显示）：显示前就能隐身、能一次性摆位置
            new WindowInteropHelper(this).EnsureHandle();
        }

        IntPtr Hwnd { get { return new WindowInteropHelper(this).Handle; } }

        // ================= 显示 / 隐藏 =================

        /// <summary>双击 Ctrl：可见且在前台就隐藏，否则呼出。</summary>
        public void Toggle()
        {
            if (IsVisible && IsActive) HideWindow();
            else ShowBar();
        }

        public void ShowBar()
        {
            bool wasVisible = IsVisible;
            if (!wasVisible)
            {
                workArea = WorkAreaAtCursor();
                bool hasText = input.Text.Trim().Length > 0;
                SetMode(hasText ? Mode.Results : Mode.Bar, true);
            }
            Topmost = true;
            if (!wasVisible) ShowCloaked(mode == Mode.Results ? body : null);
            Activate();
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
            input.Focus();
            Keyboard.Focus(input);
            input.SelectAll();
            if (!wasVisible && input.Text.Trim().Length > 0) RunSearch();   // 重新呼出时刷新结果
        }

        /// <summary>展开成内容概览（托盘菜单、--overview、搜索条右侧按钮）。</summary>
        public void ShowOverview()
        {
            bool wasVisible = IsVisible;
            if (!wasVisible) workArea = WorkAreaAtCursor();
            // 概览页的搜索框是空的；开始打字就切到搜索结果。清空时不要先收回搜索条（会多闪一次）
            if (input.Text.Length > 0)
            {
                clearingForOverview = true;
                input.Text = "";
                clearingForOverview = false;
            }
            SetMode(Mode.Overview, !wasVisible);
            Topmost = true;
            if (!wasVisible) ShowCloaked(overviewPage);
            Activate();
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
            input.Focus();
            Keyboard.Focus(input);
            overviewPage.Activate();
        }

        public void HideWindow()
        {
            debounce.Stop();
            pendingOpen = 0;
            Hide();
        }

        public void AllowClose()
        {
            allowClose = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Alt+F4 只隐藏；退出走托盘菜单
            if (!allowClose)
            {
                e.Cancel = true;
                HideWindow();
            }
            base.OnClosing(e);
        }

        void OnDeactivated(object sender, EventArgs e)
        {
            // 搜索条状态下失焦就隐藏；结果 / 概览状态下只取消置顶，方便切去别的窗口
            if (mode == Mode.Bar) HideWindow();
            else Topmost = false;
        }

        /// <summary>切换搜索条 / 结果窗口 / 概览形态。reposition 为 true 时按工作区重新定位。</summary>
        void SetMode(Mode m, bool reposition)
        {
            if (m == mode && !reposition) return;
            // 离开结果或概览形态时，记住用户拖出来的尺寸（只在内存里；两种形态共用）
            if (m != mode && mode != Mode.Bar && IsVisible && WindowState == WindowState.Normal)
            {
                expandedWidth = ActualWidth;
                expandedHeight = ActualHeight;
            }
            double centerX = reposition ? workArea.Left + workArea.Width / 2 : Left + ActualWidth / 2;
            double topY = reposition ? workArea.Top + workArea.Height * 0.16 : Top;
            bool wasBig = mode != Mode.Bar;
            mode = m;
            bool big = m != Mode.Bar;

            // 目标位置和大小。结果和概览用同一套尺寸和定位规则，互相切换时位置大小都不变
            double w, h;
            if (big)
            {
                w = Math.Min(Math.Max(expandedWidth, MinExpandedWidth), workArea.Width - 32);
                h = Math.Max(MinExpandedHeight, Math.Min(expandedHeight, workArea.Height - 48));
                // 下面放不下就把窗口整体往上挪，而不是压扁
                if (topY + h > workArea.Bottom - 24) topY = Math.Max(workArea.Top + 24, workArea.Bottom - 24 - h);
            }
            else
            {
                w = BarWidth;
                h = BarHeight;
            }
            var target = new Rect(Math.Max(workArea.Left, Math.Min(centerX - w / 2, workArea.Right - w)), topY, w, h);

            // 从搜索条展开时：先让窗口隐身，按最终尺寸排好版、一次摆好位置，等新画面画完再现身，
            // 避免露出没画好的白块和按旧尺寸排版的错位帧（2026-11-25 用户录屏反馈展开闪烁）
            bool reveal = IsVisible && big && !wasBig;
            if (reveal) Native.SetCloaked(Hwnd, true);

            body.Visibility = m == Mode.Results ? Visibility.Visible : Visibility.Collapsed;
            overviewPage.Visibility = m == Mode.Overview ? Visibility.Visible : Visibility.Collapsed;
            UpdateModeButton();
            ResizeMode = big ? ResizeMode.CanResize : ResizeMode.NoResize;
            chrome.ResizeBorderThickness = new Thickness(big ? 6 : 0);
            MinHeight = 0;   // 先放开最小高度，免得它自己先把窗口撑大一次
            ApplyBounds(target);
            if (big) MinHeight = MinExpandedHeight;

            if (reveal) RevealAfterRender(m == Mode.Results ? (UIElement)body : overviewPage);
        }

        /// <summary>
        /// 一次设定位置和大小。WPF 分别改 Left / Top / Width / Height 会让窗口经历几次中间状态
        /// （先变宽、再挪位置……），看起来在跳。
        /// </summary>
        void ApplyBounds(Rect r)
        {
            if (IsVisible)
            {
                // 内容先按目标尺寸排版，窗口一变大，第一帧就是完整的新布局
                var root = (FrameworkElement)Content;
                root.Width = r.Width;
                root.Height = r.Height;
                root.UpdateLayout();
                Matrix t = HwndSource.FromHwnd(Hwnd).CompositionTarget.TransformToDevice;
                Native.MoveWindow(Hwnd, (int)Math.Round(r.X * t.M11), (int)Math.Round(r.Y * t.M22),
                                  (int)Math.Round(r.Width * t.M11), (int)Math.Round(r.Height * t.M22));
                root.Width = double.NaN;
                root.Height = double.NaN;
            }
            // 同步 WPF 自己记的位置大小（窗口已经在这里了，不会再动）
            Left = r.X;
            Top = r.Y;
            Width = r.Width;
            Height = r.Height;
        }

        // ---------- 隐身显示 + 渲染完再现身 ----------

        UIElement fadeTarget;
        int revealFrames;
        bool revealPending;
        DispatcherTimer revealFallback;

        /// <summary>首次 / 重新显示：隐身状态下 Show，画好第一帧再现身，避免先露出一帧白底。</summary>
        void ShowCloaked(UIElement fade)
        {
            Native.SetCloaked(Hwnd, true);
            Show();
            RevealAfterRender(fade);
        }

        /// <summary>等 WPF 把新尺寸、新内容画出来再取消隐身；fade 不为 null 时这块内容随后淡入。</summary>
        void RevealAfterRender(UIElement fade)
        {
            if (fade != null)
            {
                fade.BeginAnimation(OpacityProperty, null);
                fade.Opacity = 0;
            }
            fadeTarget = fade;
            revealFrames = 0;
            if (!revealPending)
            {
                revealPending = true;
                CompositionTarget.Rendering += OnRevealRendering;
            }
            if (revealFallback == null)
            {
                // 万一没有渲染回调（比如窗口被藏起来），最多 200 毫秒也要现身
                revealFallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                revealFallback.Tick += (s, e) => Reveal();
            }
            revealFallback.Stop();
            revealFallback.Start();
        }

        void OnRevealRendering(object sender, EventArgs e)
        {
            // 第二次渲染回调时，上一帧（新尺寸、新内容）已经交给 DWM
            if (++revealFrames >= 2) Reveal();
        }

        void Reveal()
        {
            if (!revealPending) return;
            revealPending = false;
            CompositionTarget.Rendering -= OnRevealRendering;
            revealFallback.Stop();
            Native.SetCloaked(Hwnd, false);
            if (fadeTarget != null)
            {
                fadeTarget.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
                fadeTarget = null;
            }
        }

        void UpdateModeButton()
        {
            modeGlyph.Text = mode == Mode.Overview ? Theme.GlyphBack : Theme.GlyphTiles;
            modeButton.ToolTip = mode == Mode.Overview ? "返回搜索" : "已索引内容概览";
        }

        /// <summary>鼠标所在屏幕的工作区（换算成 WPF 的设备无关单位）。</summary>
        static Rect WorkAreaAtCursor()
        {
            System.Drawing.Rectangle wa = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
            double scale;
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96.0;
            return new Rect(wa.Left / scale, wa.Top / scale, wa.Width / scale, wa.Height / scale);
        }

        // ================= 搜索 =================

        void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            debounce.Stop();
            if (input.Text.Trim().Length == 0)
            {
                generation++;
                pendingOpen = 0;
                list.SetSource(null, null);
                if (mode == Mode.Results && !clearingForOverview) SetMode(Mode.Bar, false);   // 概览页里清空输入不收起
            }
            else
            {
                debounce.Start();
            }
        }

        void RunSearch()
        {
            debounce.Stop();
            string text = input.Text.Trim();
            if (text.Length == 0) return;
            // 不在这里展开：等第一页结果回来再展开（ApplyPage），免得先闪一个空列表
            int gen = ++generation;
            var query = new SearchQuery(text, kind, date, sizes);
            SortOrder order = CurrentSort();
            foreach (FilterItem f in kindItems) f.SetPending();
            foreach (FilterItem f in dateItems) f.SetPending();
            foreach (FilterItem f in sizeItems) f.SetPending();
            Task.Run(() => SearchWorker(gen, query, order));
        }

        /// <summary>
        /// 后台：先取第一页，再取三组筛选计数。每组的计数按另外两组当前的选择算
        /// （类型组按时间 + 大小，时间组按类型 + 大小，大小组按类型 + 时间，每个大小区间单独算）。
        /// </summary>
        void SearchWorker(int gen, SearchQuery q, SortOrder order)
        {
            SearchPage page = null;
            string error = null;
            try { page = index.Search(q, 0, ResultSource.PageSize, order); }
            catch (IndexUnavailableException ex) { error = ex.Message; }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (gen != generation) return;
                if (page != null) ApplyPage(q, page, order);
                else ShowSearchError(error);
            }));
            if (page == null) return;

            var kindCounts = new long?[Kinds.Length];
            var dateCounts = new long?[Dates.Length];
            var sizeCounts = new long?[Sizes.Length];
            try
            {
                for (int i = 0; i < Kinds.Length; i++)
                {
                    if (gen != generation) return;
                    kindCounts[i] = Kinds[i] == q.Kind ? page.Total : index.Count(new SearchQuery(q.Text, Kinds[i], q.Date, q.Sizes));
                }
                for (int i = 0; i < Dates.Length; i++)
                {
                    if (gen != generation) return;
                    dateCounts[i] = Dates[i] == q.Date ? page.Total : index.Count(new SearchQuery(q.Text, q.Kind, Dates[i], q.Sizes));
                }
                for (int i = 0; i < Sizes.Length; i++)
                {
                    if (gen != generation) return;
                    sizeCounts[i] = Sizes[i] == q.Sizes ? page.Total : index.Count(new SearchQuery(q.Text, q.Kind, q.Date, Sizes[i]));
                }
            }
            catch (IndexUnavailableException)
            {
                // 计数失败只让计数留空；引擎状态会通过 StatusChanged 显示出来
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (gen != generation) return;
                for (int i = 0; i < Kinds.Length; i++) kindItems[i].SetCount(kindCounts[i]);
                for (int i = 0; i < Dates.Length; i++) dateItems[i].SetCount(dateCounts[i]);
                for (int i = 0; i < Sizes.Length; i++) sizeItems[i].SetCount(sizeCounts[i]);
            }));
        }

        void ApplyPage(SearchQuery q, SearchPage page, SortOrder order)
        {
            lastSearchFailed = false;
            // 第一页结果到了再从搜索条 / 概览切到结果窗口；按了回车直接打开的就不展开
            if (pendingOpen == 0 && mode != Mode.Results) SetMode(Mode.Results, false);
            list.SetSource(new ResultSource(index, q, order, page, Dispatcher), null);
            UpdateStatusBar();
            int open = pendingOpen;
            pendingOpen = 0;
            if (open == 1) Open(list.SelectedEntry);
            else if (open == 2) OpenLocation(list.SelectedEntry);
        }

        void ShowSearchError(string message)
        {
            lastSearchFailed = true;
            pendingOpen = 0;
            if (mode != Mode.Results) SetMode(Mode.Results, false);   // 展开结果窗口显示错误说明
            list.SetSource(null, message);
            foreach (FilterItem f in kindItems) f.SetCount(null);
            foreach (FilterItem f in dateItems) f.SetCount(null);
            foreach (FilterItem f in sizeItems) f.SetCount(null);
            UpdateStatusBar();
        }

        void OnEngineStatusChanged()
        {
            IndexStatus st = index.Status;
            barStatus.Text = st.State == IndexState.Ready ? "" : st.Message;
            // 之前因为引擎没就绪而失败的搜索，就绪后自动重跑
            if (st.State == IndexState.Ready && lastSearchFailed && IsVisible && input.Text.Trim().Length > 0) RunSearch();
            else if (st.State != IndexState.Ready && lastSearchFailed) list.SetSource(null, st.Message);
        }

        // ================= 筛选 =================

        void OnKindClick(int i)
        {
            kind = kind == Kinds[i] ? KindFilter.None : Kinds[i];    // 单选，再点一次取消
            for (int k = 0; k < Kinds.Length; k++) kindItems[k].Selected = Kinds[k] == kind;
            RunSearch();
            input.Focus();
        }

        void OnDateClick(int i)
        {
            date = date == Dates[i] ? DateFilter.None : Dates[i];
            for (int k = 0; k < Dates.Length; k++) dateItems[k].Selected = Dates[k] == date;
            RunSearch();
            input.Focus();
        }

        void OnSizeClick(int i)
        {
            sizes ^= Sizes[i];                           // 可以多选，再点一次取消
            sizeItems[i].Selected = (sizes & Sizes[i]) != 0;
            RunSearch();
            input.Focus();
        }

        // ================= 排序 =================

        /// <summary>点列头：第一次升序，第二次降序，第三次回到默认顺序（名称升序）。</summary>
        void OnHeaderClicked(ResultList.Column column)
        {
            if (sortColumn != column) { sortColumn = column; sortDescending = false; }
            else if (!sortDescending) sortDescending = true;
            else sortColumn = null;
            list.SetSortIndicator(sortColumn, sortDescending);
            RunSearch();
            input.Focus();
        }

        SortOrder CurrentSort()
        {
            if (!sortColumn.HasValue) return SortOrder.NameAscending;
            bool d = sortDescending;
            switch (sortColumn.Value)
            {
                case ResultList.Column.Path: return d ? SortOrder.PathDescending : SortOrder.PathAscending;
                case ResultList.Column.Modified: return d ? SortOrder.DateModifiedDescending : SortOrder.DateModifiedAscending;
                case ResultList.Column.Size: return d ? SortOrder.SizeDescending : SortOrder.SizeAscending;
                case ResultList.Column.Type: return d ? SortOrder.TypeDescending : SortOrder.TypeAscending;
                default: return d ? SortOrder.NameDescending : SortOrder.NameAscending;
            }
        }

        // ================= 键盘 =================

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (mode == Mode.Overview)
            {
                // 概览页：Esc 隐藏；搜索框为空时 Backspace 返回上层；其它键交给搜索框（打字即切到搜索结果）
                if (e.Key == Key.Escape) { HideWindow(); e.Handled = true; }
                else if (e.Key == Key.Back && input.Text.Length == 0) { overviewPage.GoUp(); e.Handled = true; }
                return;
            }
            switch (e.Key)
            {
                case Key.Escape:
                    HideWindow();
                    e.Handled = true;
                    break;
                case Key.Down:
                    list.MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    list.MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    list.MoveSelection(list.FullRows);
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    list.MoveSelection(-list.FullRows);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (debounce.IsEnabled)
                    {
                        // 输入后还没搜就按了回车：立即搜，结果到了再打开第一条
                        pendingOpen = ctrl ? 2 : 1;
                        RunSearch();
                    }
                    else if (ctrl) OpenLocation(list.SelectedEntry);
                    else Open(list.SelectedEntry);
                    e.Handled = true;
                    break;
            }
        }

        // ================= 打开 / 定位 / 复制 =================

        void Open(FileEntry entry)
        {
            if (entry == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
                HideWindow();
            }
            catch (Win32Exception ex)
            {
                ShowNotice("无法打开：" + ex.Message);
            }
        }

        void OpenLocation(FileEntry entry)
        {
            if (entry == null) return;
            try
            {
                // 盘符直接打开；其它用 explorer /select 打开所在文件夹并选中
                string args = entry.IsVolume ? "\"" + entry.FullPath + "\"" : "/select,\"" + entry.FullPath + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
                HideWindow();
            }
            catch (Win32Exception ex)
            {
                ShowNotice("无法打开所在位置：" + ex.Message);
            }
        }

        void OpenAsAdmin(FileEntry entry)
        {
            try
            {
                Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true, Verb = "runas" });
                HideWindow();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return;   // ERROR_CANCELLED：用户在 UAC 里点了取消
                ShowNotice("无法打开：" + ex.Message);
            }
        }

        void CopyText(string text)
        {
            try
            {
                Native.SetClipboardText(new WindowInteropHelper(this).Handle, text);
                ShowNotice("已复制：" + text);
            }
            catch (Win32Exception ex)
            {
                // 重试后剪贴板仍被别的程序占着
                ShowNotice("复制失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 右键菜单和概览页同一套（ShellMenu：内置项 + Windows 原生右键菜单，用户 2026-11-25 要求）。
        /// 「打开」「打开所在文件夹」和 Enter / Ctrl+Enter 一样执行后收起窗口；剪切、删除等外壳操作后重新搜索刷新列表。
        /// </summary>
        void ShowItemMenu(FileEntry entry)
        {
            var owner = PresentationSource.FromVisual(this) as HwndSource;
            if (entry == null || owner == null) return;
            int x, y;
            ShellMenu.CursorPosition(out x, out y);
            ShellMenu.Result r = ShellMenu.Show(owner, entry.FullPath, entry.IsVolume, ShellMenu.CanRunAsAdmin(entry.Name, entry.IsFolder), x, y);
            switch (r.Builtin)
            {
                case ShellMenu.Command.Open: Open(entry); break;
                case ShellMenu.Command.OpenAsAdmin: OpenAsAdmin(entry); break;
                case ShellMenu.Command.OpenLocation: OpenLocation(entry); break;
                case ShellMenu.Command.CopyName: CopyText(entry.Name); break;
                case ShellMenu.Command.CopyPath: CopyText(entry.FullPath); break;
            }
            if (r.Error != null) ShowNotice(r.Error);
            if (r.ShellInvoked) ScheduleRefresh();
            if (IsVisible) input.Focus();
        }

        DispatcherTimer refreshTimer;

        /// <summary>外壳操作（删除、剪切、重命名……）后等 Everything 更新索引，再重新搜索。</summary>
        void ScheduleRefresh()
        {
            if (refreshTimer == null)
            {
                refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                refreshTimer.Tick += (s, e) =>
                {
                    refreshTimer.Stop();
                    if (IsVisible && mode == Mode.Results && input.Text.Trim().Length > 0) RunSearch();
                };
            }
            refreshTimer.Stop();
            refreshTimer.Start();
        }

        // ================= 状态栏 =================

        void UpdateStatusBar()
        {
            if (noticeTimer.IsEnabled) return;   // 正在显示临时提示
            if (list.Total > 0 || !lastSearchFailed)
            {
                statusCount.Text = Theme.FormatCount(list.Total) + " 个项目";
                statusCount.Foreground = Theme.SubText;
            }
            else
            {
                statusCount.Text = index.Status.State == IndexState.Ready ? "搜索失败" : index.Status.Message;
                statusCount.Foreground = Theme.ErrorText;
            }
            statusSelection.Text = list.SelectedIndex >= 0 ? "已选择 1 个项目" : "";
        }

        void ShowNotice(string text)
        {
            statusSelection.Text = text;
            noticeTimer.Stop();
            noticeTimer.Start();
        }

        static TextBlock StatusText()
        {
            return new TextBlock
            {
                FontSize = 12, Foreground = Theme.SubText, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 14, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        static TextBlock GroupHeader(string text)
        {
            return new TextBlock { Text = text, FontSize = 12, Foreground = Theme.FaintText, Margin = new Thickness(18, 14, 0, 6) };
        }
    }

    /// <summary>筛选栏的一项：图标 + 名称 + 计数；单击切换选中。</summary>
    sealed class FilterItem : Border
    {
        readonly TextBlock icon, label, count;
        bool selected, hovering;

        public event Action Click;

        public FilterItem(string text, string glyph)
        {
            Height = 32;
            CornerRadius = new CornerRadius(6);
            Padding = new Thickness(10, 0, 10, 0);
            Margin = new Thickness(8, 1, 8, 1);
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            icon = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            count = new TextBlock { FontSize = 12, Foreground = Theme.FaintText, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(count, 2);
            g.Children.Add(icon);
            g.Children.Add(label);
            g.Children.Add(count);
            Child = g;

            MouseEnter += (s, e) => { hovering = true; Refresh(); };
            MouseLeave += (s, e) => { hovering = false; Refresh(); };
            MouseLeftButtonUp += (s, e) =>
            {
                Action h = Click;
                if (h != null) h();
            };
            Refresh();
        }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Refresh(); }
        }

        /// <summary>新计数还在路上：旧数字先淡显。</summary>
        public void SetPending()
        {
            count.Opacity = 0.4;
        }

        public void SetCount(long? n)
        {
            count.Text = n.HasValue ? Theme.FormatCount(n.Value) : "";
            count.Opacity = 1;
        }

        void Refresh()
        {
            Background = selected ? Theme.SelectedBg : hovering ? Theme.HoverBg : Brushes.Transparent;
            label.Foreground = selected ? Theme.Accent : Theme.Text;
            label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            icon.Foreground = selected ? Theme.Accent : Theme.SubText;
        }
    }
}
