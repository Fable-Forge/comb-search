using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Comb.Tools
{
    /// <summary>
    /// 构建时生成应用图标：用 Windows 11 文件夹的黄色（和概览树图同一套取样色）画一个矢量放大镜，透明底，
    /// 写出多尺寸 .ico。build.ps1 用 /win32icon 把它嵌进 Comb.exe；托盘和窗口图标运行时从 exe 自身取。
    /// 用法：IconGen.exe &lt;输出.ico&gt;
    /// </summary>
    static class IconGen
    {
        // 256 存 PNG，其余存 32 位 DIB（各版本 Windows 和 csc 都认）
        static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

        static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("usage: IconGen.exe <output.ico>");
                return 2;
            }
            var images = new List<byte[]>();
            foreach (int size in Sizes)
                using (Bitmap bmp = Render(size))
                    images.Add(size >= 256 ? EncodePng(bmp) : EncodeDib(bmp));

            using (var stream = new FileStream(args[0], FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(stream))
            {
                // ICONDIR：保留 0、类型 1（图标）、张数
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)Sizes.Length);
                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    // ICONDIRENTRY：宽高（256 记作 0）、调色板 0、保留 0、平面 1、位深 32、数据长度、数据偏移
                    byte side = (byte)(Sizes[i] >= 256 ? 0 : Sizes[i]);
                    w.Write(side);
                    w.Write(side);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
            }
            Console.WriteLine("IconGen: " + args[0] + " (" + Sizes.Length + " sizes)");
            return 0;
        }

        // 设计坐标是 256×256：镜片圆心 (98, 98)，外半径 78；手柄沿 45° 伸到 (216, 216)。
        // 小尺寸（< 32）镜圈和手柄加粗、不画描边和镜片，只留清楚的轮廓。
        static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 256f, size / 256f);
                bool detailed = size >= 32;

                const float cx = 98, cy = 98, outer = 78;
                float inner = detailed ? 52 : 44;
                float handleWidth = detailed ? 42 : 52;

                // 手柄：从镜圈里面起笔（起点被镜圈盖住），末端圆头；颜色比镜圈深一档
                using (var handle = new GraphicsPath())
                {
                    handle.AddLine(cx + 50, cy + 50, 216, 216);
                    using (var outline = new Pen(Rgb(0xA86F08), handleWidth + 8) { StartCap = LineCap.Flat, EndCap = LineCap.Round })
                    using (LinearGradientBrush shade = Gradient(new PointF(150, 150), new PointF(236, 236), 0xEBAD22, 0xC4850E))
                    using (var body = new Pen(shade, handleWidth) { StartCap = LineCap.Flat, EndCap = LineCap.Round })
                    {
                        if (detailed) g.DrawPath(outline, handle);
                        g.DrawPath(body, handle);
                    }
                }

                // 镜片：浅色玻璃 + 左上一道高光（小尺寸透明，免得糊）
                if (detailed)
                {
                    using (LinearGradientBrush glass = Gradient(new PointF(cx - inner, cy - inner), new PointF(cx + inner, cy + inner), 0xFFFCF2, 0xFFEFC2))
                        g.FillEllipse(glass, cx - inner, cy - inner, inner * 2, inner * 2);
                    float r = inner - 15;
                    using (var shine = new Pen(Color.White, 11) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(shine, cx - r, cy - r, r * 2, r * 2, 195, 65);
                }

                // 镜圈：圆环，左上亮、右下深（取自文件夹前片的渐变）；大尺寸内外各描一道琥珀边
                using (var ring = new GraphicsPath(FillMode.Alternate))
                {
                    ring.AddEllipse(cx - outer, cy - outer, outer * 2, outer * 2);
                    ring.AddEllipse(cx - inner, cy - inner, inner * 2, inner * 2);
                    using (LinearGradientBrush fill = detailed
                        ? Gradient(new PointF(cx - outer, cy - outer), new PointF(cx + outer, cy + outer), 0xFFE69E, 0xF5B623)
                        : Gradient(new PointF(cx - outer, cy - outer), new PointF(cx + outer, cy + outer), 0xFFD452, 0xEBA81E))
                        g.FillPath(fill, ring);
                    if (detailed)
                        using (var edge = new Pen(Rgb(0xC98C14), 5))
                            g.DrawPath(edge, ring);
                }
            }
            return bmp;
        }

        static LinearGradientBrush Gradient(PointF from, PointF to, int fromRgb, int toRgb)
        {
            // 抗锯齿边缘会落到渐变线两端之外，用镜像平铺，避免另一端的颜色漏到边上
            return new LinearGradientBrush(from, to, Rgb(fromRgb), Rgb(toRgb)) { WrapMode = WrapMode.TileFlipXY };
        }

        static Color Rgb(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        static byte[] EncodePng(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // 图标里的 DIB：BITMAPINFOHEADER（高度记两倍）+ 自下而上的 BGRA 像素 + 1 位 AND 掩码（全透明像素置 1）
        static byte[] EncodeDib(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int maskStride = (w + 31) / 32 * 4;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(40);
                bw.Write(w);
                bw.Write(h * 2);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(0);
                bw.Write(w * h * 4 + maskStride * h);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);

                var mask = new byte[maskStride * h];
                var row = new byte[w * 4];
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = h - 1; y >= 0; y--)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        bw.Write(row);
                        int maskRow = (h - 1 - y) * maskStride;
                        for (int x = 0; x < w; x++)
                            if (row[x * 4 + 3] == 0) mask[maskRow + x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                bw.Write(mask);
                bw.Flush();
                return ms.ToArray();
            }
        }
    }
}
