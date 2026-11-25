using System;
using System.Diagnostics;
using System.Text;
using Comb.Engine;

namespace Comb.Tools
{
    /// <summary>
    /// 命令行探针：直接调引擎，打印版本、总数、耗时和前几条，用来冒烟。
    ///   Probe.exe ".html"                        搜索，打印总数和前 10 条
    ///   Probe.exe ".html" --kind doc --date week  带筛选（kind: folder/file/doc/image/video；date: day/week/month）
    ///   Probe.exe ".html" --count                 只取计数
    ///   Probe.exe ".html" --max 20 --sort size    条数和排序（name/name-desc/path/path-desc/size-asc/size/date-asc/date/type/type-desc；size、date 是降序）
    ///   Probe.exe ".md" --size small,large --count 文件大小筛选（small &lt;1MB / medium 1–100MB / large &gt;100MB，可多选）
    ///   Probe.exe --children "D:\"               直接子项及大小；不带路径列出各盘
    /// 退出码：0 成功，1 参数错误，2 引擎没就绪，3 查询失败。
    /// </summary>
    static class Probe
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);

            string text = null, childrenPath = null;
            bool children = false, countOnly = false;
            int max = 10;
            KindFilter kind = KindFilter.None;
            DateFilter date = DateFilter.None;
            SizeFilter sizes = SizeFilter.None;
            SortOrder sort = SortOrder.NameAscending;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--children":
                        children = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) childrenPath = args[++i];
                        break;
                    case "--count": countOnly = true; break;
                    case "--max": max = int.Parse(Next(args, ref i)); break;
                    case "--kind": kind = ParseKind(Next(args, ref i)); break;
                    case "--date": date = ParseDate(Next(args, ref i)); break;
                    case "--sort": sort = ParseSort(Next(args, ref i)); break;
                    case "--size": sizes = ParseSizes(Next(args, ref i)); break;
                    default:
                        if (a.StartsWith("--")) { Console.WriteLine("unknown option: " + a); return 1; }
                        text = a;
                        break;
                }
            }
            if (!children && text == null) { Console.WriteLine("usage: Probe.exe <query> [--kind k] [--date d] [--count] [--max n] [--sort s] [--size list] | --children [path]"); return 1; }

            using (var index = new EverythingIndex())
            {
                index.Start();
                var wait = Stopwatch.StartNew();
                if (!index.WaitReady(30000))
                {
                    Console.WriteLine("NOT READY: " + index.Status.State + " " + index.Status.Message);
                    return 2;
                }
                Console.WriteLine("Everything " + index.Version + " ready after " + wait.ElapsedMilliseconds + " ms");

                try
                {
                    var sw = Stopwatch.StartNew();
                    if (children)
                    {
                        ChildList list = index.ListChildren(childrenPath, Math.Max(max, 1));
                        sw.Stop();
                        Console.WriteLine("children of " + (childrenPath ?? "(volumes)") + ": total=" + list.TotalCount + " returned=" + list.Items.Count + " time=" + sw.ElapsedMilliseconds + "ms");
                        foreach (FileEntry e in list.Items) Print(e);
                    }
                    else
                    {
                        var q = new SearchQuery(text, kind, date, sizes);
                        Console.WriteLine("everything search: " + EverythingIndex.ToEverythingSearch(q));
                        if (countOnly)
                        {
                            long n = index.Count(q);
                            sw.Stop();
                            Console.WriteLine("count=" + n + " time=" + sw.ElapsedMilliseconds + "ms");
                        }
                        else
                        {
                            SearchPage page = index.Search(q, 0, max, sort);
                            sw.Stop();
                            Console.WriteLine("total=" + page.Total + " returned=" + page.Items.Count + " time=" + sw.ElapsedMilliseconds + "ms");
                            foreach (FileEntry e in page.Items) Print(e);
                        }
                    }
                }
                catch (IndexUnavailableException ex)
                {
                    Console.WriteLine("ERROR: " + ex.Message);
                    return 3;
                }
            }
            return 0;
        }

        static void Print(FileEntry e)
        {
            string type = e.IsVolume ? "V" : e.IsFolder ? "D" : "F";
            string hl = "";
            if (e.Highlights != null)
            {
                var sb = new StringBuilder();
                for (int i = 0; i + 1 < e.Highlights.Length; i += 2)
                    sb.Append(i == 0 ? "" : ",").Append(e.Name.Substring(e.Highlights[i], e.Highlights[i + 1]));
                hl = "  hl=[" + sb + "]";
            }
            string modified = e.Modified.HasValue ? e.Modified.Value.ToString("yyyy-MM-dd HH:mm") : "-";
            Console.WriteLine("  " + type + "  " + e.Size.ToString().PadLeft(14) + "  " + modified + "  " + e.FullPath + hl);
        }

        static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("missing value after " + args[i]);
            return args[++i];
        }

        static KindFilter ParseKind(string s)
        {
            switch (s)
            {
                case "folder": return KindFilter.Folder;
                case "file": return KindFilter.File;
                case "doc": return KindFilter.Document;
                case "image": return KindFilter.Image;
                case "video": return KindFilter.Video;
                default: throw new ArgumentException("kind: folder/file/doc/image/video");
            }
        }

        static DateFilter ParseDate(string s)
        {
            switch (s)
            {
                case "day": return DateFilter.LastDay;
                case "week": return DateFilter.LastWeek;
                case "month": return DateFilter.LastMonth;
                default: throw new ArgumentException("date: day/week/month");
            }
        }

        static SizeFilter ParseSizes(string s)
        {
            SizeFilter r = SizeFilter.None;
            foreach (string part in s.Split(','))
            {
                switch (part.Trim())
                {
                    case "small": r |= SizeFilter.Small; break;
                    case "medium": r |= SizeFilter.Medium; break;
                    case "large": r |= SizeFilter.Large; break;
                    default: throw new ArgumentException("size: small/medium/large, comma separated");
                }
            }
            return r;
        }

        static SortOrder ParseSort(string s)
        {
            switch (s)
            {
                case "name": return SortOrder.NameAscending;
                case "name-desc": return SortOrder.NameDescending;
                case "path": return SortOrder.PathAscending;
                case "path-desc": return SortOrder.PathDescending;
                case "size-asc": return SortOrder.SizeAscending;
                case "size": return SortOrder.SizeDescending;
                case "date-asc": return SortOrder.DateModifiedAscending;
                case "date": return SortOrder.DateModifiedDescending;
                case "type": return SortOrder.TypeAscending;
                case "type-desc": return SortOrder.TypeDescending;
                default: throw new ArgumentException("sort: name/name-desc/path/path-desc/size-asc/size/date-asc/date/type/type-desc");
            }
        }
    }
}
