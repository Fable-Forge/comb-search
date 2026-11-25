using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Comb.Engine;

namespace Comb.App
{
    /// <summary>颜色、字体、图标字形和格式化。中性灰白底 + Windows 蓝强调色；表达文件夹的部分（树图方块、概览列表图标）和应用图标（放大镜，tools/IconGen.cs 画）用 Windows 11 文件夹黄。</summary>
    static class Theme
    {
        public static readonly FontFamily UiFont = new FontFamily("Segoe UI, Microsoft YaHei UI");
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly Typeface UiFace = new Typeface(UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        public static readonly Typeface UiFaceBold = new Typeface(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        public static readonly Typeface IconFace = new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public static readonly Brush WindowBg = Solid("#FBFBFB");
        public static readonly Brush PanelBg = Solid("#F3F3F3");      // 侧栏、状态栏
        public static readonly Brush Divider = Solid("#E5E5E5");
        public static readonly Brush Text = Solid("#1A1A1A");
        public static readonly Brush SubText = Solid("#5F5F5F");
        public static readonly Brush FaintText = Solid("#8A8A8A");
        public static readonly Brush Accent = Solid("#0067C0");       // Windows 蓝：高亮文字、选中项
        public static readonly Brush SelectedBg = Solid("#DDEBF9");
        public static readonly Brush HoverBg = Solid("#EDEDED");
        public static readonly Brush FolderIcon = Solid("#EBAD22");   // 线条图标：Windows 11 文件夹后片的黄色（取样）
        public static readonly Brush FileIcon = Solid("#7A8699");
        public static readonly Brush WarnBg = Solid("#FFF4CE");
        public static readonly Brush WarnText = Solid("#6B4E00");
        public static readonly Brush ErrorText = Solid("#C42B1C");

        // 概览页：灰底上的白色卡片；树图方块用 Windows 11 文件夹的黄色（色值从系统文件夹图标取样），
        // 按大小在浅黄和文件夹黄之间取色，浅黄 = 文件夹黄 30% 叠白，
        // 琥珀描边，选中加深色粗边；列表图标用文件夹黄线条，比例条仍用 Windows 蓝；「其余」用浅灰
        public static readonly Brush CardBg = Solid("#FFFFFF");
        public static readonly Brush CardBorder = Solid("#E3E3E3");
        public static readonly Brush BarTrack = Solid("#E1E1E1");
        public static readonly Brush BarFill = Solid("#0067C0");
        public static readonly Color TileLight = Color.FromRgb(0xFF, 0xF2, 0xCF);
        public static readonly Color TileDeep = Color.FromRgb(0xFF, 0xD4, 0x5E);   // 文件夹前片中下部的黄色（取样；最下沿 #FFC938 铺成大块太艳）
        public static readonly Brush TileStroke = Solid("#D09520");
        public static readonly Brush TileHoverStroke = Solid("#9A6A08");
        public static readonly Brush TileSelectedStroke = Solid("#1A1A1A");
        public static readonly Brush RowSelectedBg = Solid("#E8EEF5");
        public static readonly Brush RowSelectedBorder = Solid("#6F9BD1");
        public static readonly Brush OtherTileFill = Solid("#F2F2F2");
        public static readonly Brush OtherTileStroke = Solid("#C8C8C8");

        /// <summary>两种颜色之间按 t（0–1）插值，返回冻结的画刷。</summary>
        public static Brush Lerp(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            var c = Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        // 图标字形（Segoe Fluent Icons 与 Segoe MDL2 Assets 码位相同）
        public const string GlyphSearch = "";
        public const string GlyphFolder = "";
        public const string GlyphPage = "";
        public const string GlyphDocument = "";
        public const string GlyphPhoto = "";
        public const string GlyphVideo = "";
        public const string GlyphClock = "";
        public const string GlyphDot = "";   // 实心圆点（文件大小筛选项）
        public const string GlyphDrive = "";
        public const string GlyphTiles = "";
        public const string GlyphBack = "";
        public const string GlyphUp = "";
        public const string GlyphRefresh = "";
        public const string GlyphChevron = "";

        public static Brush Solid(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        /// <summary>字节数转成 12.3 MB 这种形式；未知（小于 0）返回空串。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return bytes + " B";
            string[] units = { "KB", "MB", "GB", "TB", "PB" };
            double v = bytes;
            int u = -1;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString(v < 10 ? "0.##" : v < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + units[u];
        }

        /// <summary>「类型」列：文件夹、本地磁盘，文件按扩展名显示成「MD 文件」（和按扩展名排序一致）。</summary>
        public static string FormatType(FileEntry e)
        {
            if (e.IsVolume) return "本地磁盘";
            if (e.IsFolder) return "文件夹";
            string ext = System.IO.Path.GetExtension(e.Name);
            return ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() + " 文件" : "文件";
        }

        public static string FormatDate(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture) : "";
        }

        public static string FormatCount(long n)
        {
            return n.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
