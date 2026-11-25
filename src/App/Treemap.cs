using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Comb.App
{
    /// <summary>squarified 矩形树图布局（Bruls, Huizing, van Wijk 2000）。</summary>
    static class Squarify
    {
        /// <summary>values 必须按降序排列且都大于 0；返回与 values 一一对应的矩形。</summary>
        public static Rect[] Layout(double[] values, Rect bounds)
        {
            int n = values.Length;
            var result = new Rect[n];
            double total = 0;
            foreach (double v in values) total += v;
            if (n == 0 || total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

            // 把数值换算成面积
            double scale = bounds.Width * bounds.Height / total;
            var areas = new double[n];
            for (int i = 0; i < n; i++) areas[i] = values[i] * scale;

            Rect free = bounds;
            int start = 0;
            while (start < n)
            {
                double side = Math.Min(free.Width, free.Height);
                if (side <= 0) break;
                // 往当前行里加项，直到最差长宽比开始变差
                int end = start;
                double sum = 0, worst = double.MaxValue;
                while (end < n)
                {
                    double newSum = sum + areas[end];
                    double newWorst = Worst(areas[start], areas[end], newSum, side);
                    if (end > start && newWorst > worst) break;
                    sum = newSum;
                    worst = newWorst;
                    end++;
                }
                free = LayoutRow(areas, start, end, sum, free, result);
                start = end;
            }
            return result;
        }

        /// <summary>一行里最差的长宽比（项按降序，最大项是第一个、最小项是最后一个）。</summary>
        static double Worst(double maxArea, double minArea, double sum, double side)
        {
            double s2 = sum * sum, w2 = side * side;
            return Math.Max(w2 * maxArea / s2, s2 / (w2 * minArea));
        }

        /// <summary>沿短边摆一行，返回剩余区域。</summary>
        static Rect LayoutRow(double[] areas, int start, int end, double sum, Rect free, Rect[] result)
        {
            if (free.Width >= free.Height)
            {
                // 竖着摆在左侧
                double w = sum / free.Height;
                double y = free.Top;
                for (int i = start; i < end; i++)
                {
                    double h = areas[i] / w;
                    result[i] = new Rect(free.Left, y, w, h);
                    y += h;
                }
                return new Rect(free.Left + w, free.Top, Math.Max(0, free.Width - w), free.Height);
            }
            else
            {
                // 横着摆在顶部
                double h = sum / free.Width;
                double x = free.Left;
                for (int i = start; i < end; i++)
                {
                    double w = areas[i] / h;
                    result[i] = new Rect(x, free.Top, w, h);
                    x += w;
                }
                return new Rect(free.Left, free.Top + h, free.Width, Math.Max(0, free.Height - h));
            }
        }
    }

    /// <summary>自绘的矩形树图：悬停高亮，单击回调。</summary>
    sealed class TreemapView : FrameworkElement
    {
        public sealed class Node
        {
            public string Label;
            public long Size;
            public Brush Fill;
            public Brush Stroke;
            public bool Clickable;
            public object Tag;
        }

        List<Node> nodes = new List<Node>();
        Rect[] rects = new Rect[0];
        int hover = -1;
        Node selected;

        /// <summary>选中的方块（加深色粗描边）；null 表示不选中。</summary>
        public Node Selected
        {
            get { return selected; }
            set { selected = value; InvalidateVisual(); }
        }

        public event Action<Node> NodeClicked;
        public event Action<Node> NodeDoubleClicked;
        /// <summary>悬停项变化（离开时参数为 null）。</summary>
        public event Action<Node> HoverChanged;

        public TreemapView()
        {
            ClipToBounds = true;
            SnapsToDevicePixels = true;
        }

        /// <summary>nodes 须按 Size 降序且 Size 都大于 0。</summary>
        public void SetNodes(List<Node> list)
        {
            nodes = list ?? new List<Node>();
            hover = -1;
            selected = null;
            Relayout();
            InvalidateVisual();
        }

        void Relayout()
        {
            var values = new double[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) values[i] = nodes[i].Size;
            rects = Squarify.Layout(values, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Relayout();
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < rects.Length; i++) if (rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.GetPosition(this));
            if (h == hover) return;
            hover = h;
            Cursor = h >= 0 && nodes[h].Clickable ? Cursors.Hand : null;
            InvalidateVisual();
            Action<Node> cb = HoverChanged;
            if (cb != null) cb(h >= 0 ? nodes[h] : null);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (hover < 0) return;
            hover = -1;
            InvalidateVisual();
            Action<Node> cb = HoverChanged;
            if (cb != null) cb(null);
        }

        /// <summary>
        /// 第一下按下算单击，第二下（ClickCount = 2）算双击：
        /// 双击文件夹不会因为第二下又落在新一层的方块上而连进两层。
        /// </summary>
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            int h = HitTest(e.GetPosition(this));
            if (h < 0) return;
            Action<Node> cb = e.ClickCount == 1 ? NodeClicked : e.ClickCount == 2 ? NodeDoubleClicked : null;
            if (cb != null) cb(nodes[h]);
        }

        protected override void OnRender(DrawingContext dc)
        {
            // 透明底：卡片背景透出来，同时保证整块区域能收到鼠标
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            const double gap = 5, strokeWidth = 2, radius = 6;
            for (int i = 0; i < rects.Length && i < nodes.Count; i++)
            {
                Rect r = rects[i];
                // 方块之间留缝：每边各缩 gap，再让出半个描边宽度
                double inset = gap + strokeWidth / 2;
                if (r.Width <= inset * 2 + 1 || r.Height <= inset * 2 + 1) continue;
                var inner = new Rect(r.X + inset, r.Y + inset, r.Width - inset * 2, r.Height - inset * 2);
                double rad = Math.Min(radius, Math.Min(inner.Width, inner.Height) / 2);
                Pen pen = nodes[i].Stroke != null ? new Pen(nodes[i].Stroke, strokeWidth) : null;
                dc.DrawRoundedRectangle(nodes[i].Fill, pen, inner, rad, rad);
                // 选中：外面再描一圈深色粗边；悬停：描边加深
                if (nodes[i] == selected)
                {
                    var outer = new Rect(inner.X - 2, inner.Y - 2, inner.Width + 4, inner.Height + 4);
                    dc.DrawRoundedRectangle(null, new Pen(Theme.TileSelectedStroke, 3), outer, rad + 2, rad + 2);
                }
                else if (i == hover)
                {
                    dc.DrawRoundedRectangle(null, new Pen(Theme.TileHoverStroke, strokeWidth + 1), inner, rad, rad);
                }

                // 大方块：名称 14 + 大小 12；中等方块只写名称；太小不写
                bool big = inner.Width >= 70 && inner.Height >= 44;
                if (!big && (inner.Width < 46 || inner.Height < 24)) continue;
                double pad = big ? 12 : 6;
                var name = new FormattedText(nodes[i].Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                             Theme.UiFaceBold, big ? 15 : 12, Theme.Text, dip);
                name.MaxTextWidth = Math.Max(1, inner.Width - pad * 2);
                name.MaxLineCount = 1;
                name.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(name, new Point(inner.X + pad, inner.Y + (big ? 10 : 4)));
                if (big && inner.Height >= 60)
                {
                    var size = new FormattedText(Theme.FormatSize(nodes[i].Size), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                                 Theme.UiFace, 13, Theme.SubText, dip);
                    size.MaxTextWidth = Math.Max(1, inner.Width - pad * 2);
                    size.MaxLineCount = 1;
                    size.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(size, new Point(inner.X + pad, inner.Y + 10 + name.Height + 2));
                }
            }
        }
    }
}
