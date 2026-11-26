using System;
using Comb.App;

namespace Comb.Tools
{
    /// <summary>只测试生产代码的选屏策略和几何边界，不创建窗口，也不把它当作真实多屏验收。</summary>
    static class WindowPlacementRegressionTests
    {
        static int assertions;

        static int Main()
        {
            Action[] tests = {
                HiddenWindowUsesCursorScreen,
                VisibleWindowIgnoresCursorScreen,
                MovedWindowUsesFreshWorkArea,
                ExpandedModesKeepBoundsAtWorkAreaEdges,
                ExpandedModesKeepNegativeCoordinates,
                HiddenWindowCentersOnNegativeCoordinateScreen,
                SmallLandscapeFitsWithCompatibleMinimums,
                NarrowPortraitFitsBothModes,
                CollapsingWindowClampsAboveScreen,
                RemovedDisplayReturnsWindowToAvailableScreen,
                PlacementMatrixStaysInsideWorkArea
            };
            try
            {
                foreach (Action test in tests)
                {
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                Console.WriteLine("WindowPlacement: " + tests.Length + " tests, " + assertions + " assertions passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex.Message);
                return 1;
            }
        }

        static void HiddenWindowUsesCursorScreen()
        {
            var cursorScreen = new WindowBounds(1920, 0, 1920, 1040);
            WindowBounds area = WindowPlacement.ChooseWorkArea(false, () => cursorScreen,
                () => { throw new Exception("隐藏窗口不能按旧窗口句柄选屏。"); });
            var result = WindowPlacement.Calculate(area, new WindowBounds(300, 100, 680, 58), true, false, false, 1180, 760);
            Equal(2540, result.Bounds.Left, "隐藏呼出应在鼠标所在右屏居中");
            Equal(166.4, result.Bounds.Top, "隐藏呼出保持上部定位");
            Contained(area, result);
        }

        static void VisibleWindowIgnoresCursorScreen()
        {
            var windowScreen = new WindowBounds(1920, 0, 1920, 1040);
            WindowBounds area = WindowPlacement.ChooseWorkArea(true,
                () => { throw new Exception("可见窗口切换形态不能追随鼠标选屏。"); }, () => windowScreen);
            var result = WindowPlacement.Calculate(area, new WindowBounds(2540, 180, 680, 58), false, false, true, 1180, 760);
            Equal(2290, result.Bounds.Left, "展开后仍在窗口所在右屏");
            Equal(180, result.Bounds.Top, "下方放得下时不移动顶部");
            Contained(area, result);
        }

        static void MovedWindowUsesFreshWorkArea()
        {
            var currentScreen = new WindowBounds(0, 0, 1920, 1040);
            int screenReads = 0;
            Func<WindowBounds> readWindowScreen = () => { screenReads++; return currentScreen; };
            Func<WindowBounds> readCursorScreen = () => new WindowBounds(0, 0, 1920, 1040);
            WindowBounds first = WindowPlacement.ChooseWorkArea(true, readCursorScreen, readWindowScreen);
            Equal(0, first.Left, "初次位于左屏");
            currentScreen = new WindowBounds(1920, 0, 1920, 1040);
            WindowBounds afterMove = WindowPlacement.ChooseWorkArea(true, readCursorScreen, readWindowScreen);
            var result = WindowPlacement.Calculate(afterMove, new WindowBounds(2540, 180, 680, 58), false, false, true, 1180, 760);
            Equal(2, screenReads, "每次切换都重新读取当前窗口所在屏幕");
            Equal(2290, result.Bounds.Left, "跨屏后不能被旧工作区夹回横坐标 740");
            Contained(currentScreen, result);
        }

        static void ExpandedModesKeepBoundsAtWorkAreaEdges()
        {
            var area = new WindowBounds(0, 0, 1920, 1040);
            var fillsArea = new WindowBounds(0, 0, 1920, 1040);
            var result = WindowPlacement.Calculate(area, fillsArea, false, true, true, 1180, 760);
            SameBounds(fillsArea, result.Bounds, "结果与概览互切不能强加 32/48 的尺寸留白");
            var nearBottom = new WindowBounds(400, 280, 1400, 760);
            result = WindowPlacement.Calculate(area, nearBottom, false, true, true, 1180, 760);
            SameBounds(nearBottom, result.Bounds, "靠近底部时互切不能平白向上跳 24");
            Contained(area, result);
        }

        static void ExpandedModesKeepNegativeCoordinates()
        {
            var area = new WindowBounds(-1920, -1080, 1920, 1040);
            var current = new WindowBounds(-1700, -900, 1000, 720);
            var result = WindowPlacement.Calculate(area, current, false, true, true, 1180, 760);
            SameBounds(current, result.Bounds, "左上方副屏的负坐标必须保持");
            Contained(area, result);
        }

        static void HiddenWindowCentersOnNegativeCoordinateScreen()
        {
            var area = new WindowBounds(-1920, -1080, 1920, 1040);
            var result = WindowPlacement.Calculate(area, new WindowBounds(300, 100, 1180, 760), true, true, false, 1180, 760);
            Equal(-1300, result.Bounds.Left, "负横坐标屏幕的中心");
            Equal(-913.6, result.Bounds.Top, "负纵坐标屏幕的上部");
            Contained(area, result);
        }

        static void SmallLandscapeFitsWithCompatibleMinimums()
        {
            var area = new WindowBounds(0, 0, 640, 340);
            var result = WindowPlacement.Calculate(area, new WindowBounds(0, 0, 680, 58), true, false, true, 1180, 760);
            SameBounds(new WindowBounds(16, 24, 608, 292), result.Bounds, "高缩放小工作区应先服从可用空间");
            Equal(608, result.MinWidth, "不能再被旧 680 或 640 最小宽度撑大");
            Equal(292, result.MinHeight, "不能再被 360 最小高度撑大");
            Contained(area, result);
        }

        static void NarrowPortraitFitsBothModes()
        {
            var area = new WindowBounds(-480, 0, 480, 1100);
            var current = new WindowBounds(0, 0, 680, 58);
            var bar = WindowPlacement.Calculate(area, current, true, false, false, 1180, 760);
            Equal(480, bar.Bounds.Width, "窄竖屏搜索条不能固定 680");
            Equal(480, bar.MinWidth, "搜索条最小宽度应匹配适配后的宽度");
            var expanded = WindowPlacement.Calculate(area, bar.Bounds, false, false, true, 1180, 760);
            Equal(448, expanded.Bounds.Width, "窄竖屏展开留边后仍需放得下");
            Contained(area, bar);
            Contained(area, expanded);
        }

        static void CollapsingWindowClampsAboveScreen()
        {
            var area = new WindowBounds(0, 0, 1920, 1040);
            var result = WindowPlacement.Calculate(area, new WindowBounds(-800, -100, 1180, 760), false, true, false, 1180, 760);
            Equal(0, result.Bounds.Left, "窗口不能停留在工作区左边之外");
            Equal(0, result.Bounds.Top, "窗口不能停留在工作区上边之外");
            Contained(area, result);
        }

        static void RemovedDisplayReturnsWindowToAvailableScreen()
        {
            var area = new WindowBounds(0, 0, 1920, 1040);
            var result = WindowPlacement.Calculate(area, new WindowBounds(2400, 100, 1180, 760), false, true, true, 1180, 760);
            SameBounds(new WindowBounds(740, 100, 1180, 760), result.Bounds, "原屏幕消失后应夹回当前可用工作区");
            Contained(area, result);
        }

        static void PlacementMatrixStaysInsideWorkArea()
        {
            WindowBounds[] areas = {
                new WindowBounds(0, 0, 1920, 1040),
                new WindowBounds(-1536, 0, 1536, 832),
                new WindowBounds(0, -900, 1440, 860),
                new WindowBounds(1920, 0, 480, 1100),
                new WindowBounds(0, 0, 640, 340),
                new WindowBounds(-853.333333, -480, 853.333333, 453.333333)
            };
            foreach (WindowBounds area in areas)
                foreach (bool reposition in new[] { false, true })
                    foreach (bool wasExpanded in new[] { false, true })
                        foreach (bool expanded in new[] { false, true })
                        {
                            var current = new WindowBounds(area.Right - 200, area.Top - 60, 1500, 900);
                            var result = WindowPlacement.Calculate(area, current, reposition, wasExpanded, expanded, 2200, 1400);
                            Contained(area, result);
                        }
        }

        static void SameBounds(WindowBounds expected, WindowBounds actual, string message)
        {
            Equal(expected.Left, actual.Left, message + " / Left");
            Equal(expected.Top, actual.Top, message + " / Top");
            Equal(expected.Width, actual.Width, message + " / Width");
            Equal(expected.Height, actual.Height, message + " / Height");
        }

        static void Contained(WindowBounds area, WindowPlacementResult result)
        {
            WindowBounds bounds = result.Bounds;
            Check(bounds.Width > 0 && bounds.Height > 0, "目标宽高必须为正");
            Check(bounds.Left >= area.Left - 0.000001 && bounds.Top >= area.Top - 0.000001, "左上角必须在当前工作区内");
            Check(bounds.Right <= area.Right + 0.000001 && bounds.Bottom <= area.Bottom + 0.000001, "右下角必须在当前工作区内");
            Check(result.MinWidth <= bounds.Width && result.MinHeight <= bounds.Height, "最小尺寸不能超过目标尺寸");
        }

        static void Equal(double expected, double actual, string message)
        {
            Check(!double.IsNaN(actual) && Math.Abs(expected - actual) < 0.000001,
                message + ": expected " + expected + ", actual " + actual);
        }

        static void Check(bool passed, string message)
        {
            assertions++;
            if (!passed) throw new InvalidOperationException(message);
        }
    }
}
