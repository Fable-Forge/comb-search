using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Comb.App
{
    /// <summary>
    /// 文件 / 文件夹的右键菜单：
    /// 上半是本程序自己的内置项（不显示分组标题）；下半「快捷菜单」直接接 Windows 外壳给这个对象的右键菜单
    /// （打开方式、发送到、压缩软件、属性等各软件注册的扩展都在里面），去掉和内置重复的项。
    /// 用系统原生菜单（TrackPopupMenuEx），弹出期间把菜单消息转给外壳，子菜单才会填充。
    /// </summary>
    static class ShellMenu
    {
        /// <summary>需要调用方自己执行的内置项。剪切 / 复制 / 删除交给外壳，在这里直接执行。</summary>
        public enum Command { None, Open, OpenAsAdmin, OpenLocation, CopyName, CopyPath }

        public sealed class Result
        {
            public Command Builtin = Command.None;
            /// <summary>执行了外壳命令（剪切、删除、发送到……），调用方可能需要刷新。</summary>
            public bool ShellInvoked;
            /// <summary>给用户看的错误说明；没有错误为 null。</summary>
            public string Error;
        }

        const uint IdOpen = 1, IdOpenAdmin = 2, IdLocation = 3, IdCopyName = 4, IdCopyPath = 5;
        const uint IdCut = 6, IdCopy = 7, IdDelete = 8, IdDeleteForever = 9;
        const uint ShellFirst = 100, ShellLast = 0x7FFF;

        // 外壳菜单里和内置项重复的命令（按 verb 去掉）
        static readonly HashSet<string> DuplicateVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "open", "cut", "copy", "delete", "rename"
        };

        /// <summary>「以管理员身份打开」只对可执行的文件显示。</summary>
        public static bool CanRunAsAdmin(string name, bool isFolder)
        {
            if (isFolder) return false;
            string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            return ext == ".exe" || ext == ".msi" || ext == ".bat" || ext == ".cmd";
        }

        /// <summary>在屏幕坐标 (x, y) 弹出菜单，阻塞到用户选择或取消。</summary>
        public static Result Show(HwndSource owner, string path, bool isVolume, bool canRunAsAdmin, int x, int y)
        {
            var result = new Result();
            IntPtr pidl = IntPtr.Zero, menu = IntPtr.Zero;
            IContextMenu cm = null;
            HwndSourceHook hook = null;
            try
            {
                cm = GetContextMenu(owner.Handle, path, out pidl);

                menu = CreatePopupMenu();
                // 顶部不放「内置」分组标题（用户觉得没用，2026-11-25 去掉）
                Append(menu, IdOpen, "打开");
                if (canRunAsAdmin) Append(menu, IdOpenAdmin, "以管理员身份打开");
                Append(menu, IdLocation, "打开所在文件夹");
                Append(menu, IdCopyName, "复制名称");
                Append(menu, IdCopyPath, "复制完整路径");
                if (cm != null && !isVolume)
                {
                    AppendSeparator(menu);
                    Append(menu, IdCut, "剪切");
                    Append(menu, IdCopy, "复制");
                    Append(menu, IdDelete, "删除");
                    Append(menu, IdDeleteForever, "彻底删除");
                }
                if (cm != null)
                {
                    AppendSeparator(menu);
                    AppendHeader(menu, "快捷菜单");
                    int start = GetMenuItemCount(menu);
                    if (cm.QueryContextMenu(menu, (uint)start, ShellFirst, ShellLast, CMF_NORMAL) >= 0)
                    {
                        RemoveDuplicates(menu, cm, start);
                        TidySeparators(menu);
                    }
                }

                // 外壳扩展的子菜单（发送到、打开方式……）靠这些消息填充和绘制
                var cm2 = cm as IContextMenu2;
                var cm3 = cm as IContextMenu3;
                if (cm2 != null)
                {
                    hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    {
                        if (msg == WM_INITMENUPOPUP || msg == WM_DRAWITEM || msg == WM_MEASUREITEM || msg == WM_MENUCHAR)
                        {
                            IntPtr r = IntPtr.Zero;
                            int hr = cm3 != null ? cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out r)
                                                 : cm2.HandleMenuMsg((uint)msg, wParam, lParam);
                            if (hr >= 0) { handled = true; return r; }
                        }
                        return IntPtr.Zero;
                    };
                    owner.AddHook(hook);
                }

                SetForegroundWindow(owner.Handle);
                uint id = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, owner.Handle, IntPtr.Zero);
                switch (id)
                {
                    case 0: break;   // 取消
                    case IdOpen: result.Builtin = Command.Open; break;
                    case IdOpenAdmin: result.Builtin = Command.OpenAsAdmin; break;
                    case IdLocation: result.Builtin = Command.OpenLocation; break;
                    case IdCopyName: result.Builtin = Command.CopyName; break;
                    case IdCopyPath: result.Builtin = Command.CopyPath; break;
                    case IdCut: result.Error = InvokeVerb(cm, owner.Handle, "cut", false, x, y); result.ShellInvoked = true; break;
                    case IdCopy: result.Error = InvokeVerb(cm, owner.Handle, "copy", false, x, y); result.ShellInvoked = true; break;
                    // 删除：外壳自己弹确认框；彻底删除 = 按住 Shift 删除，不进回收站
                    case IdDelete: result.Error = InvokeVerb(cm, owner.Handle, "delete", false, x, y); result.ShellInvoked = true; break;
                    case IdDeleteForever: result.Error = InvokeVerb(cm, owner.Handle, "delete", true, x, y); result.ShellInvoked = true; break;
                    default:
                        if (id >= ShellFirst && id <= ShellLast)
                        {
                            result.Error = InvokeId(cm, owner.Handle, id - ShellFirst, x, y);
                            result.ShellInvoked = true;
                        }
                        break;
                }
            }
            finally
            {
                if (hook != null) owner.RemoveHook(hook);
                if (menu != IntPtr.Zero) DestroyMenu(menu);
                if (cm != null) Marshal.ReleaseComObject(cm);
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
            return result;
        }

        /// <summary>取外壳给这个路径的 IContextMenu；取不到返回 null（菜单只显示内置项）。</summary>
        static IContextMenu GetContextMenu(IntPtr owner, string path, out IntPtr pidl)
        {
            uint attrs;
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs) < 0 || pidl == IntPtr.Zero)
            {
                pidl = IntPtr.Zero;
                return null;
            }
            Guid iidFolder = typeof(IShellFolder).GUID;
            IntPtr folderPtr, child;
            if (SHBindToParent(pidl, ref iidFolder, out folderPtr, out child) < 0 || folderPtr == IntPtr.Zero) return null;
            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            Marshal.Release(folderPtr);
            try
            {
                Guid iidMenu = typeof(IContextMenu).GUID;
                IntPtr menuPtr;
                if (folder.GetUIObjectOf(owner, 1, new[] { child }, ref iidMenu, IntPtr.Zero, out menuPtr) < 0 || menuPtr == IntPtr.Zero) return null;
                var cm = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
                Marshal.Release(menuPtr);
                return cm;
            }
            finally
            {
                Marshal.ReleaseComObject(folder);
            }
        }

        /// <summary>去掉外壳菜单里和内置项重复的命令（打开、剪切、复制、删除、重命名）。</summary>
        static void RemoveDuplicates(IntPtr menu, IContextMenu cm, int start)
        {
            IntPtr buffer = Marshal.AllocHGlobal(512);
            try
            {
                for (int pos = GetMenuItemCount(menu) - 1; pos >= start; pos--)
                {
                    uint id = GetMenuItemID(menu, pos);
                    if (id < ShellFirst || id > ShellLast) continue;   // 子菜单（返回 -1）和分隔符保留
                    Marshal.WriteInt16(buffer, 0);
                    if (cm.GetCommandString((UIntPtr)(id - ShellFirst), GCS_VERBW, IntPtr.Zero, buffer, 256) < 0) continue;
                    string verb = Marshal.PtrToStringUni(buffer);
                    if (!string.IsNullOrEmpty(verb) && DuplicateVerbs.Contains(verb)) DeleteMenu(menu, (uint)pos, MF_BYPOSITION);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>去掉连续的、紧跟标题的和末尾的分隔符（删重复项后会留下空隙）。</summary>
        static void TidySeparators(IntPtr menu)
        {
            bool prevIsBreak = true;   // 开头或标题之后
            for (int pos = 0; pos < GetMenuItemCount(menu); )
            {
                bool separator = IsSeparator(menu, pos);
                bool header = !separator && GetMenuItemID(menu, pos) == 0
                              && (GetMenuState(menu, (uint)pos, MF_BYPOSITION) & MF_GRAYED) != 0;
                if (separator && prevIsBreak) { DeleteMenu(menu, (uint)pos, MF_BYPOSITION); continue; }
                prevIsBreak = separator || header;
                pos++;
            }
            int last = GetMenuItemCount(menu) - 1;
            if (last >= 0 && IsSeparator(menu, last)) DeleteMenu(menu, (uint)last, MF_BYPOSITION);
        }

        /// <summary>
        /// 按菜单项类型判断分隔符。外壳插入的分隔符可能带非零 ID（2026-11-25 实测出现过两条相邻分隔线），
        /// 而 GetMenuState 对子菜单项返回的高字节是子项数、会碰上分隔符位，都不可靠。
        /// </summary>
        static bool IsSeparator(IntPtr menu, int pos)
        {
            var info = new MENUITEMINFO { cbSize = Marshal.SizeOf(typeof(MENUITEMINFO)), fMask = MIIM_FTYPE };
            return GetMenuItemInfo(menu, (uint)pos, true, ref info) && (info.fType & MFT_SEPARATOR) != 0;
        }

        static string InvokeVerb(IContextMenu cm, IntPtr owner, string verb, bool shift, int x, int y)
        {
            IntPtr ansi = Marshal.StringToHGlobalAnsi(verb);
            IntPtr wide = Marshal.StringToHGlobalUni(verb);
            try { return Invoke(cm, owner, ansi, wide, shift, x, y); }
            finally { Marshal.FreeHGlobal(ansi); Marshal.FreeHGlobal(wide); }
        }

        static string InvokeId(IContextMenu cm, IntPtr owner, uint offset, int x, int y)
        {
            // 按序号调用时 lpVerb 是 MAKEINTRESOURCE(偏移)
            return Invoke(cm, owner, (IntPtr)offset, (IntPtr)offset, false, x, y);
        }

        static string Invoke(IContextMenu cm, IntPtr owner, IntPtr verb, IntPtr verbW, bool shift, int x, int y)
        {
            var info = new CMINVOKECOMMANDINFOEX();
            info.cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX));
            info.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE | (shift ? CMIC_MASK_SHIFT_DOWN : 0);
            info.hwnd = owner;
            info.lpVerb = verb;
            info.lpVerbW = verbW;
            info.nShow = 1;   // SW_SHOWNORMAL
            info.ptInvoke = new POINT { X = x, Y = y };
            int hr = cm.InvokeCommand(ref info);
            // 用户在确认框里点了取消不算错误
            if (hr >= 0 || hr == unchecked((int)0x800704C7)) return null;
            return "操作没有完成（0x" + hr.ToString("X8") + "）";
        }

        static void Append(IntPtr menu, uint id, string text) { AppendMenu(menu, MF_STRING, (UIntPtr)id, text); }
        static void AppendSeparator(IntPtr menu) { AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null); }
        static void AppendHeader(IntPtr menu, string text) { AppendMenu(menu, MF_STRING | MF_GRAYED | MF_DISABLED, UIntPtr.Zero, text); }

        // ================= Win32 / COM =================

        const uint CMF_NORMAL = 0;
        const uint GCS_VERBW = 4;
        const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_PTINVOKE = 0x20000000;
        const uint MF_STRING = 0, MF_GRAYED = 1, MF_DISABLED = 2, MF_SEPARATOR = 0x800, MF_BYPOSITION = 0x400;
        const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
        const int WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_INITMENUPOPUP = 0x117, WM_MENUCHAR = 0x120;

        const uint MIIM_FTYPE = 0x100, MFT_SEPARATOR = 0x800;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MENUITEMINFO
        {
            public int cbSize;
            public uint fMask;
            public uint fType;
            public uint fState;
            public uint wID;
            public IntPtr hSubMenu;
            public IntPtr hbmpChecked;
            public IntPtr hbmpUnchecked;
            public IntPtr dwItemData;
            public IntPtr dwTypeData;
            public uint cch;
            public IntPtr hbmpItem;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);

        [StructLayout(LayoutKind.Sequential)]
        struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr lpTitle;
            public IntPtr lpVerbW;
            public IntPtr lpParametersW;
            public IntPtr lpDirectoryW;
            public IntPtr lpTitleW;
            public POINT ptInvoke;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
        interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attributes);
            [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attributes);
            [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr ppv);
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
            [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E4-0000-0000-C000-000000000046")]
        interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, int cchMax);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F4-0000-0000-C000-000000000046")]
        interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, int cchMax);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
        interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, int cchMax);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);

        [DllImport("user32.dll")]
        static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);

        [DllImport("user32.dll")]
        static extern int GetMenuItemCount(IntPtr menu);

        [DllImport("user32.dll")]
        static extern uint GetMenuItemID(IntPtr menu, int pos);

        [DllImport("user32.dll")]
        static extern uint GetMenuState(IntPtr menu, uint id, uint flags);

        [DllImport("user32.dll")]
        static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

        [DllImport("user32.dll")]
        static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT point);

        /// <summary>当前鼠标位置（本进程的坐标系，和 TrackPopupMenuEx 一致）。</summary>
        public static void CursorPosition(out int x, out int y)
        {
            POINT p;
            GetCursorPos(out p);
            x = p.X;
            y = p.Y;
        }
    }
}
