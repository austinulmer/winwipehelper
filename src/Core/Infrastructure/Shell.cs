using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace WinWipeHelper
{
    /// <summary>Shell APIs: known folders (OneDrive-redirect aware) and .lnk / .url reading.</summary>
    public static class Shell
    {
        // ---- Known folders -----------------------------------------------------------------
        public static readonly Guid FolderDesktop = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        public static readonly Guid FolderDocuments = new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
        public static readonly Guid FolderPictures = new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB");
        public static readonly Guid FolderVideos = new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
        public static readonly Guid FolderMusic = new Guid("4BD8D571-6D19-48D3-BE97-422220080E43");
        public static readonly Guid FolderDownloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        public static readonly Guid FolderProfile = new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        public static string KnownFolder(Guid id)
        {
            try
            {
                if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) == 0)
                {
                    try { return Marshal.PtrToStringUni(p); }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            catch { }
            return null;
        }

        // ---- .lnk ---------------------------------------------------------------------------
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkCoClass { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        public sealed class LinkInfo
        {
            public string Target;
            public string Arguments;
            public string Url;          // for .url files (and steam:// links)
        }

        /// <summary>Reads a .lnk or .url without resolving (so a missing target stays visible as missing).</summary>
        public static LinkInfo ReadLink(string path)
        {
            try
            {
                if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in File.ReadAllLines(path))
                        if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                            return new LinkInfo { Url = line.Substring(4).Trim() };
                    return new LinkInfo();
                }
                var link = (IShellLinkW)new ShellLinkCoClass();
                try
                {
                    ((IPersistFile)link).Load(path, 0);
                    var sb = new StringBuilder(1024);
                    link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
                    var target = Environment.ExpandEnvironmentVariables(sb.ToString());
                    var args = new StringBuilder(2048);
                    try { link.GetArguments(args, args.Capacity); } catch { }
                    return new LinkInfo { Target = target, Arguments = args.ToString() };
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            catch { return null; }
        }

        public static IEnumerable<string> SafeFiles(string dir, string pattern, bool recurse)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                string[] files = null, subs = null;
                try { files = Directory.GetFiles(d, pattern); } catch { }
                if (files != null) foreach (var f in files) yield return f;
                if (!recurse) continue;
                try { subs = Directory.GetDirectories(d); } catch { }
                if (subs != null)
                    foreach (var s in subs)
                    {
                        try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                        stack.Push(s);
                    }
            }
        }
    }

    /// <summary>Reads scheduled tasks through the Task Scheduler COM API (works without admin for most tasks).</summary>
    public static class ScheduledTasks
    {
        public sealed class TaskInfo
        {
            public string Path;       // \Folder\Name
            public string Name;
            public string Exe;
            public string Arguments;
        }

        public static List<TaskInfo> All()
        {
            var list = new List<TaskInfo>();
            object service = null;
            try
            {
                var t = Type.GetTypeFromProgID("Schedule.Service");
                if (t == null) return list;
                dynamic svc = Activator.CreateInstance(t);
                service = svc;
                svc.Connect();
                Walk(svc.GetFolder("\\"), list, 0);
            }
            catch { }
            finally { if (service != null) try { Marshal.FinalReleaseComObject(service); } catch { } }
            return list;
        }

        static void Walk(dynamic folder, List<TaskInfo> list, int depth)
        {
            if (depth > 12) return;
            try
            {
                foreach (dynamic task in folder.GetTasks(1 /* include hidden */))
                {
                    try
                    {
                        string path = task.Path, name = task.Name;
                        foreach (dynamic action in task.Definition.Actions)
                        {
                            try
                            {
                                if ((int)action.Type != 0) continue; // 0 = exec
                                string exe = action.Path, args = action.Arguments;
                                list.Add(new TaskInfo { Path = path, Name = name, Exe = Environment.ExpandEnvironmentVariables((exe ?? "").Trim('"')), Arguments = args });
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, list, depth + 1);
            }
            catch { }
        }

        /// <summary>Task state (1 disabled, 2 queued, 3 ready, 4 running), or -1 if it can't be read.</summary>
        public static int State(string taskPath)
        {
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                int slash = taskPath.LastIndexOf('\\');
                string folder = slash <= 0 ? "\\" : taskPath.Substring(0, slash);
                return (int)svc.GetFolder(folder).GetTask(taskPath.Substring(slash + 1)).State;
            }
            catch { return -1; }
        }

        public static bool Delete(string taskPath, out string error)
        {
            error = null;
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                int slash = taskPath.LastIndexOf('\\');
                string folder = slash <= 0 ? "\\" : taskPath.Substring(0, slash);
                string name = taskPath.Substring(slash + 1);
                svc.GetFolder(folder).DeleteTask(name, 0);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
