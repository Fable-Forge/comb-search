using System;
using System.Collections.Generic;

namespace Comb.Engine
{
    /// <summary>
    /// 界面唯一依赖的引擎接口。现在由 EverythingIndex 实现；以后换自研引擎（MFT/USN）时只替换实现类。
    /// 所有查询方法都是同步阻塞的，由调用方放到后台线程执行；引擎不可用时抛 IndexUnavailableException。
    /// </summary>
    public interface IFileIndex
    {
        /// <summary>引擎当前状态（给界面显示提示用）。</summary>
        IndexStatus Status { get; }

        /// <summary>状态变化时触发。可能在任意后台线程上触发，界面要自己切回 UI 线程。</summary>
        event EventHandler StatusChanged;

        /// <summary>启动守护：后台确保引擎可用（必要时拉起），立即返回。</summary>
        void Start();

        /// <summary>按 offset 分页搜索。</summary>
        SearchPage Search(SearchQuery query, int offset, int count, SortOrder sort);

        /// <summary>只取匹配总数（筛选计数用）。</summary>
        long Count(SearchQuery query);

        /// <summary>
        /// 列出某路径的直接子项及大小，按大小降序，最多 max 项。
        /// folderPath 为 null 时列出各盘。
        /// </summary>
        ChildList ListChildren(string folderPath, int max);
    }

    public enum IndexState
    {
        /// <summary>正在连接或拉起引擎。</summary>
        Starting,
        /// <summary>引擎在运行，但数据库还没加载完。</summary>
        Loading,
        /// <summary>可以查询。</summary>
        Ready,
        /// <summary>引擎不可用（没安装、没响应等），Message 里有原因。</summary>
        Unavailable
    }

    /// <summary>不可变的状态快照。</summary>
    public sealed class IndexStatus
    {
        public readonly IndexState State;
        /// <summary>给用户看的中文说明。</summary>
        public readonly string Message;

        public IndexStatus(IndexState state, string message)
        {
            State = state;
            Message = message ?? "";
        }
    }

    /// <summary>类型筛选。具体怎么判定（扩展名列表等）由引擎实现决定。</summary>
    public enum KindFilter { None, Folder, File, Document, Image, Video }

    /// <summary>修改时间筛选。</summary>
    public enum DateFilter { None, LastDay, LastWeek, LastMonth }

    /// <summary>文件大小筛选，可以多选（按位组合，之间是「或」）。只匹配文件，不含文件夹。</summary>
    [Flags]
    public enum SizeFilter
    {
        None = 0,
        /// <summary>小于 1 MB。</summary>
        Small = 1,
        /// <summary>1 MB 到 100 MB。</summary>
        Medium = 2,
        /// <summary>大于 100 MB。</summary>
        Large = 4
    }

    /// <summary>排序。「类型」按扩展名排。</summary>
    public enum SortOrder
    {
        NameAscending, NameDescending,
        PathAscending, PathDescending,
        SizeAscending, SizeDescending,
        DateModifiedAscending, DateModifiedDescending,
        TypeAscending, TypeDescending
    }

    /// <summary>一次搜索的条件：用户输入的文字 + 类型、修改时间、文件大小三组筛选。</summary>
    public sealed class SearchQuery
    {
        public readonly string Text;
        public readonly KindFilter Kind;
        public readonly DateFilter Date;
        public readonly SizeFilter Sizes;

        public SearchQuery(string text, KindFilter kind, DateFilter date, SizeFilter sizes = SizeFilter.None)
        {
            Text = (text ?? "").Trim();
            Kind = kind;
            Date = date;
            Sizes = sizes;
        }
    }

    /// <summary>一个文件、文件夹或盘符。</summary>
    public sealed class FileEntry
    {
        /// <summary>名称；盘符项形如 "C:"。</summary>
        public string Name = "";
        /// <summary>所在文件夹；盘符项为空串。</summary>
        public string Folder = "";
        /// <summary>名称里匹配部分的区间，依次为 起点,长度,起点,长度…；没有高亮时为 null。</summary>
        public int[] Highlights;
        /// <summary>字节数；未知（例如没有索引文件夹大小）时为 -1。</summary>
        public long Size = -1;
        /// <summary>修改时间（本地时间）；未知时为 null。</summary>
        public DateTime? Modified;
        public bool IsFolder;
        public bool IsVolume;

        /// <summary>完整路径；盘符项返回 "C:\"。</summary>
        public string FullPath
        {
            get
            {
                if (IsVolume) return Name.EndsWith("\\") ? Name : Name + "\\";
                if (Folder.Length == 0) return Name;
                return Folder.EndsWith("\\") ? Folder + Name : Folder + "\\" + Name;
            }
        }
    }

    public sealed class SearchPage
    {
        /// <summary>匹配总数（不受分页影响）。</summary>
        public long Total;
        public int Offset;
        public List<FileEntry> Items = new List<FileEntry>();
    }

    public sealed class ChildList
    {
        /// <summary>直接子项总数（可能多于 Items.Count）。</summary>
        public long TotalCount;
        /// <summary>按大小降序的前若干项。</summary>
        public List<FileEntry> Items = new List<FileEntry>();
    }

    /// <summary>引擎暂时不能查询（未运行、加载中、无响应）。Message 可以直接给用户看。</summary>
    public sealed class IndexUnavailableException : Exception
    {
        public IndexUnavailableException(string message) : base(message) { }
    }
}
