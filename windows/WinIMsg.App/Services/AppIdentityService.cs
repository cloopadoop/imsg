using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace WinIMsg.App.Services;

public static class AppIdentityService
{
    public const string DisplayName = "iMessage for Windows";
    public const string AppUserModelId = "iMessageForWindows.App";
    public const string ProtocolScheme = "winimsg";

    public static void EnsureConfigured()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        TryCreateStartMenuShortcut();
        TryRegisterProtocol();
    }

    public static string BuildProtocolCommand(string executablePath) => $"\"{executablePath}\" \"%1\"";

    private static void TryRegisterProtocol()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return;
        }

        try
        {
            using var protocol = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolScheme}");
            protocol?.SetValue(null, $"URL:{DisplayName} Protocol");
            protocol?.SetValue("URL Protocol", string.Empty);
            using var icon = protocol?.CreateSubKey("DefaultIcon");
            icon?.SetValue(null, $"\"{executablePath}\",0");
            using var command = protocol?.CreateSubKey(@"shell\open\command");
            command?.SetValue(null, BuildProtocolCommand(executablePath));
        }
        catch
        {
            // Protocol launch is a convenience for the cached web companion;
            // normal shortcuts and direct executable launch remain available.
        }
    }

    internal static string StartMenuShortcutPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs",
            $"{DisplayName}.lnk");
    }

    private static void TryCreateStartMenuShortcut()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return;
        }

        try
        {
            var shortcutPath = StartMenuShortcutPath();
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            TryDeleteLegacyStartMenuShortcuts(Path.GetDirectoryName(shortcutPath)!);
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Messages.ico");
            CreateShortcutFile(shortcutPath, executablePath, iconPath);
            TrySetShortcutAppUserModelId(shortcutPath, executablePath, iconPath);
        }
        catch
        {
            // A missing shortcut only affects notification attribution; the app remains usable.
        }
    }

    private static void TryDeleteLegacyStartMenuShortcuts(string programsDirectory)
    {
        foreach (var shortcutName in new[] { "WinIMsg.lnk", "WinIMsg.App.lnk" })
        {
            try
            {
                var path = Path.Combine(programsDirectory, shortcutName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A stale shortcut is not fatal; the current shortcut is still rewritten below.
            }
        }
    }

    private static void CreateShortcutFile(string shortcutPath, string executablePath, string iconPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is not null)
        {
            var shell = Activator.CreateInstance(shellType)!;
            object? shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    [shortcutPath])!;
                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [executablePath]);
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [AppContext.BaseDirectory]);
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, [DisplayName]);
                if (File.Exists(iconPath))
                {
                    shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [iconPath]);
                }

                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return;
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        CreateShellLinkShortcut(shortcutPath, executablePath, iconPath);
    }

    private static void CreateShellLinkShortcut(string shortcutPath, string executablePath, string iconPath)
    {
        var shellLinkObject = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClassId)!)!;
        var shellLink = (IShellLinkW)shellLinkObject;
        try
        {
            shellLink.SetPath(executablePath);
            shellLink.SetWorkingDirectory(AppContext.BaseDirectory);
            shellLink.SetDescription(DisplayName);
            if (File.Exists(iconPath))
            {
                shellLink.SetIconLocation(iconPath, 0);
            }

            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally
        {
            ReleaseComObject(shellLinkObject);
        }
    }

    private static void TrySetShortcutAppUserModelId(string shortcutPath, string executablePath, string iconPath)
    {
        try
        {
            var shellLinkObject = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClassId)!)!;
            try
            {
                var persistFile = (IPersistFile)shellLinkObject;
                persistFile.Load(shortcutPath, 0);

                var propertyStore = (IPropertyStore)shellLinkObject;
                SetStringProperty(propertyStore, PropertyKeys.AppUserModelId, AppUserModelId);
                SetStringProperty(propertyStore, PropertyKeys.RelaunchCommand, executablePath);
                SetStringProperty(propertyStore, PropertyKeys.RelaunchDisplayNameResource, DisplayName);
                SetStringProperty(propertyStore, PropertyKeys.RelaunchIconResource, File.Exists(iconPath) ? iconPath : executablePath);
                propertyStore.Commit();
                persistFile.Save(shortcutPath, true);
            }
            finally
            {
                ReleaseComObject(shellLinkObject);
            }
        }
        catch
        {
            // The shortcut name/icon still improve attribution when the AppUserModelID stamp fails.
        }
    }

    private static void SetStringProperty(IPropertyStore propertyStore, PropertyKey key, string value)
    {
        var propertyValue = PropVariant.FromString(value);
        try
        {
            propertyStore.SetValue(ref key, ref propertyValue);
        }
        finally
        {
            propertyValue.Dispose();
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000138-0000-0000-C000-000000000046")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant propvar);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    private static class PropertyKeys
    {
        public static PropertyKey AppUserModelId => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

        public static PropertyKey RelaunchCommand => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 2);

        public static PropertyKey RelaunchIconResource => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);

        public static PropertyKey RelaunchDisplayNameResource => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 4);
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        private const ushort VtLpwstr = 31;

        [FieldOffset(0)]
        private ushort valueType;

        [FieldOffset(8)]
        private nint pointerValue;

        public static PropVariant FromString(string value)
        {
            return new PropVariant
            {
                valueType = VtLpwstr,
                pointerValue = Marshal.StringToCoTaskMemUni(value)
            };
        }

        public void Dispose()
        {
            PropVariantClear(ref this);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
