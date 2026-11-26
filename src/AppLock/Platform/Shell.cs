using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using AppLock.Core;
using static AppLock.Native.Win32;

namespace AppLock.Platform;

/// <summary>「アプリを追加」の一覧に出すアプリ</summary>
internal sealed record Candidate(string Name, string ExeName, string? Path, string? PackageFamilyName, string? AppUserModelId, bool IsRunning)
{
    public string Id => AppIdentity.MakeId(ExeName, PackageFamilyName);

    public LockedApp ToLockedApp() => new()
    {
        Name = Name,
        ExeName = ExeName,
        Path = Path,
        PackageFamilyName = PackageFamilyName,
        AppUserModelId = AppUserModelId,
    };
}

/// <summary>ロックできるアプリを集める(スタート メニュー・ストアのアプリ・動いているアプリ)。STA のスレッドで呼ぶ。</summary>
internal static class AppCatalog
{
    public static readonly StringComparer NameOrder =
        StringComparer.Create(CultureInfo.GetCultureInfo("ja-JP"), CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType);

    public static List<Candidate> Load(Action<string> log)
    {
        var byId = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        void Add(Candidate candidate)
        {
            if (!SystemApps.CanLock(candidate.ExeName)) return;
            // 同じアプリは、短い名前(スタート メニューの名前)を残す
            if (!byId.TryGetValue(candidate.Id, out var existing) || candidate.Name.Length < existing.Name.Length) byId[candidate.Id] = candidate;
        }

        try
        {
            foreach (var candidate in StartMenuApps()) Add(candidate);
        }
        catch (Exception e)
        {
            log($"スタート メニューを読めない: {e.Message}");
        }
        try
        {
            foreach (var candidate in PackagedApps()) Add(candidate);
        }
        catch (Exception e)
        {
            log($"ストアのアプリを読めない: {e.Message}");
        }
        foreach (var running in RunningApps())
        {
            if (byId.TryGetValue(running.Id, out var existing)) byId[running.Id] = existing with { IsRunning = true };
            else if (SystemApps.CanLock(running.ExeName)) byId[running.Id] = running;
        }
        return byId.Values.OrderByDescending(c => c.IsRunning).ThenBy(c => c.Name, NameOrder).ToList();
    }

    /// <summary>実行ファイルを選んだとき</summary>
    public static Candidate FromExecutable(string path)
    {
        string? name = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            name = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
        }
        catch
        {
            // 名前が読めなければファイル名にする
        }
        if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);
        return new Candidate(name.Trim(), System.IO.Path.GetFileName(path), path, null, null, false);
    }

    private static IEnumerable<Candidate> StartMenuApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var link in SafeEnumerate(root, "*.lnk"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(link);
                if (SystemApps.LooksLikeUninstaller(name)) continue;
                var target = ShellLinks.Target(link);
                if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                if (SystemApps.LooksLikeUninstaller(System.IO.Path.GetFileName(target))) continue;
                yield return new Candidate(name, System.IO.Path.GetFileName(target), target, null, null, false);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        try
        {
            return Directory.EnumerateFiles(root, pattern, options).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>ストアなどのパッケージのアプリ(shell:AppsFolder の、ID に「!」があるもの)</summary>
    private static IEnumerable<Candidate> PackagedApps()
    {
        var result = new List<Candidate>();
        foreach (var (name, aumid) in ShellItems.AppsFolder())
        {
            var bang = aumid.IndexOf('!');
            if (bang <= 0 || aumid.Contains('\\')) continue;
            var family = aumid[..bang];
            var appId = aumid[(bang + 1)..];
            var executable = Packages.Executable(family, appId);
            if (executable is null) continue;
            result.Add(new Candidate(name, System.IO.Path.GetFileName(executable), executable, family, aumid, false));
        }
        return result;
    }

    /// <summary>画面に窓を出して動いているアプリ</summary>
    private static List<Candidate> RunningApps()
    {
        var pids = new HashSet<uint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero &&
                ((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0 &&
                DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked == 0)
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                pids.Add(pid);
            }
            return true;
        }, IntPtr.Zero);

        var list = new List<Candidate>();
        foreach (var pid in pids)
        {
            if (pid == Environment.ProcessId) continue;
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                var path = ImagePath(handle);
                if (path is null) continue;
                var family = PackageFamilyName(handle);
                var candidate = FromExecutable(path) with { PackageFamilyName = family, IsRunning = true };
                list.Add(candidate);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        return list;
    }
}

/// <summary>ショートカット(.lnk)の行き先</summary>
internal static class ShellLinks
{
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile(out IntPtr fileName);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    public static string? Target(string linkPath)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(linkPath, 0);
            var buffer = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            var target = buffer.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.ReleaseComObject(link);
        }
    }
}

/// <summary>シェルの項目(shell:AppsFolder の一覧・アイコン)</summary>
internal static class ShellItems
{
    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    private const uint SIIGBF_ICONONLY = 0x4;
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IidEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems copy);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(IntPtr obj, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    public static List<(string Name, string Id)> AppsFolder()
    {
        var result = new List<(string, string)>();
        var iid = IidShellItem;
        if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out var folder) != 0) return result;
        try
        {
            var handler = BhidEnumItems;
            var enumIid = IidEnumShellItems;
            if (folder.BindToHandler(IntPtr.Zero, ref handler, ref enumIid, out var enumPointer) != 0) return result;
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPointer);
            Marshal.Release(enumPointer);
            try
            {
                while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        var name = DisplayName(item, SIGDN_NORMALDISPLAY);
                        var id = DisplayName(item, SIGDN_PARENTRELATIVEPARSING);
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) result.Add((name, id));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
        return result;
    }

    private static string? DisplayName(IShellItem item, uint form)
    {
        if (item.GetDisplayName(form, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    /// <summary>アプリのアイコン(実行ファイルの場所か shell:AppsFolder\ID)。読めなければ null。</summary>
    public static BitmapSource? Icon(string parsingName, int size)
    {
        var iid = IidShellItem;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item) != 0) return null;
        try
        {
            if (item is not IShellItemImageFactory factory) return null;
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out var bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            try
            {
                return ToBitmapSource(bitmap);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static BitmapSource? ToBitmapSource(IntPtr bitmap)
    {
        if (GetObjectW(bitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0) return null;
        int width = info.bmWidth, height = Math.Abs(info.bmHeight);
        if (width <= 0 || height <= 0) return null;
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // 上から下の順で受け取る
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
        // 透明度の無い画像(すべて 0)は、不透明として扱う
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }
}

/// <summary>パッケージのアプリの実行ファイル(AppxManifest.xml の Application の Executable)</summary>
internal static unsafe class Packages
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr* fullNames, ref uint bufferLength, char* buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint length, char* path);

    public static string? Executable(string family, string appId)
    {
        var location = InstallLocation(family);
        if (location is null) return null;
        var manifest = System.IO.Path.Combine(location, "AppxManifest.xml");
        if (!File.Exists(manifest)) return null;
        try
        {
            var document = XDocument.Load(manifest);
            var application = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application" && (string?)e.Attribute("Id") == appId);
            var executable = (string?)application?.Attribute("Executable");
            return string.IsNullOrWhiteSpace(executable) ? null : System.IO.Path.Combine(location, executable);
        }
        catch
        {
            return null;
        }
    }

    private static string? InstallLocation(string family)
    {
        uint count = 0, length = 0;
        GetPackagesByPackageFamily(family, ref count, null, ref length, null);
        if (count == 0 || length == 0) return null;
        var names = stackalloc IntPtr[(int)count];
        var buffer = new char[length];
        fixed (char* chars = buffer)
        {
            if (GetPackagesByPackageFamily(family, ref count, names, ref length, chars) != 0 || count == 0) return null;
            var fullName = new string((char*)names[0]);
            uint pathLength = 0;
            GetPackagePathByFullName(fullName, ref pathLength, null);
            if (pathLength == 0) return null;
            var path = new char[pathLength];
            fixed (char* pathChars = path)
            {
                return GetPackagePathByFullName(fullName, ref pathLength, pathChars) == 0 ? new string(pathChars) : null;
            }
        }
    }
}
