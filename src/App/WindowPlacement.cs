using System;

namespace Comb.App
{
    /// <summary>窗口位置计算用的设备无关矩形；不依赖 WPF，回归程序直接调用同一份逻辑。</summary>
    struct WindowBounds
    {
        public readonly double Left, Top, Width, Height;
        public double Right { get { return Left + Width; } }
        public double Bottom { get { return Top + Height; } }

        public WindowBounds(double left, double top, double width, double height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }
    }

    struct WindowPlacementResult
    {
        public readonly WindowBounds Bounds;
        public readonly double MinWidth, MinHeight;

        public WindowPlacementResult(WindowBounds bounds, double minWidth, double minHeight)
        {
            Bounds = bounds;
            MinWidth = minWidth;
            MinHeight = minHeight;
        }
    }

    /// <summary>选择当前工作区并计算尺寸边界。屏幕查询由调用方提供，不缓存上次呼出时的屏幕。</summary>
    static class WindowPlacement
    {
        public const double BarWidth = 680, BarHeight = 58;
        public const double MinExpandedWidth = 640, MinExpandedHeight = 360;

        public static WindowBounds ChooseWorkArea(bool isVisible, Func<WindowBounds> atCursor, Func<WindowBounds> atWindow)
        {
            // 可见窗口跟随它自己所在的屏幕；鼠标跨屏不能把正在使用的窗口带走。
            return isVisible ? atWindow() : atCursor();
        }

        public static WindowPlacementResult Calculate(WindowBounds workArea, WindowBounds current,
            bool reposition, bool wasExpanded, bool expanded, double expandedWidth, double expandedHeight)
        {
            if (workArea.Width <= 0 || workArea.Height <= 0)
                throw new ArgumentOutOfRangeException("workArea", "工作区必须具有正的宽度和高度。");

            bool keepExpandedBounds = expanded && wasExpanded && !reposition;
            double width, height;
            if (keepExpandedBounds)
            {
                // 结果与概览互切时保留实际尺寸，包括用户拖到工作区边缘的窗口；只有放不下才收缩。
                width = Math.Min(current.Width, workArea.Width);
                height = Math.Min(current.Height, workArea.Height);
            }
            else if (expanded)
            {
                width = Math.Min(Math.Max(expandedWidth, MinExpandedWidth), AvailableLength(workArea.Width, 32));
                height = Math.Min(Math.Max(expandedHeight, MinExpandedHeight), AvailableLength(workArea.Height, 48));
            }
            else
            {
                width = Math.Min(BarWidth, workArea.Width);
                height = Math.Min(BarHeight, workArea.Height);
            }

            double centerX = reposition ? workArea.Left + workArea.Width / 2 : current.Left + current.Width / 2;
            double top = reposition ? workArea.Top + workArea.Height * 0.16 : current.Top;
            if (expanded && !keepExpandedBounds && top + height > workArea.Bottom - 24)
                top = Math.Max(workArea.Top + 24, workArea.Bottom - 24 - height);

            // 可见的大窗口互切时即便因屏幕变小而收缩，也以原左上角为锚，避免无故左右漂移。
            double left = keepExpandedBounds ? current.Left : centerX - width / 2;
            left = Clamp(left, workArea.Left, workArea.Right - width);
            top = Clamp(top, workArea.Top, workArea.Bottom - height);
            var bounds = new WindowBounds(left, top, width, height);

            // 最小尺寸不能超过已按工作区限制的目标尺寸，否则 WPF 会再次撑大窗口、把边缘挤出屏幕。
            return new WindowPlacementResult(bounds,
                expanded ? Math.Min(MinExpandedWidth, width) : width,
                expanded ? Math.Min(MinExpandedHeight, height) : 0);
        }

        static double AvailableLength(double length, double margin)
        {
            return Math.Min(length, Math.Max(1, length - margin));
        }

        static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(value, max));
        }
    }
}
