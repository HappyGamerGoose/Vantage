using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Vantage.Services.Agent;

namespace Vantage.Services;

internal static class WindowsShellIdentity
{
    private const string AppUserModelId = "velopack.HappyGamerGoose.Vantage";
    private static readonly Guid PropertyStoreInterfaceId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid AppUserModelFormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static IntPtr _largeWindowIcon;
    private static IntPtr _smallWindowIcon;

    public static void Apply(Window window)
    {
        try
        {
            var executablePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Vantage.exe");
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            var iconResource = $"{iconPath},0";

            ApplyNativeWindowIcons(window, iconPath);
            ApplyWindowProperties(window, executablePath, iconResource);
            RepairStartMenuShortcut(executablePath, iconResource);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("shell-identity-apply-failed", ex.Message);
        }
    }

    private static void ApplyNativeWindowIcons(Window window, string iconPath)
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _largeWindowIcon = LoadImage(
            IntPtr.Zero,
            iconPath,
            1,
            GetSystemMetrics(11),
            GetSystemMetrics(12),
            0x10);
        _smallWindowIcon = LoadImage(
            IntPtr.Zero,
            iconPath,
            1,
            GetSystemMetrics(49),
            GetSystemMetrics(50),
            0x10);

        if (_largeWindowIcon == IntPtr.Zero || _smallWindowIcon == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        SendMessage(windowHandle, 0x80, new IntPtr(1), _largeWindowIcon);
        SendMessage(windowHandle, 0x80, IntPtr.Zero, _smallWindowIcon);

        var appliedIcon = SendMessage(windowHandle, 0x7F, new IntPtr(1), IntPtr.Zero);
        if (appliedIcon == IntPtr.Zero)
        {
            throw new InvalidOperationException("The window rejected its large icon handle.");
        }

        App.LogStartup($"Shell window icon applied: HWND=0x{windowHandle.ToInt64():X}, HICON=0x{appliedIcon.ToInt64():X}");
    }

    private static void ApplyWindowProperties(Window window, string executablePath, string iconResource)
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var interfaceId = PropertyStoreInterfaceId;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(
            windowHandle,
            ref interfaceId,
            out var store));

        try
        {
            SetString(store, 2, $"\"{executablePath}\"");
            SetString(store, 3, iconResource);
            SetString(store, 5, AppUserModelId);
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally
        {
            Marshal.FinalReleaseComObject(store);
        }
    }

    private static void RepairStartMenuShortcut(string executablePath, string iconResource)
    {
        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs",
            "Vantage.lnk");
        if (!File.Exists(shortcutPath)) return;

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;

        dynamic? shell = null;
        dynamic? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shell?.CreateShortcut(shortcutPath);
            if (shortcut is null) return;

            shortcut.TargetPath = executablePath;
            shortcut.Arguments = string.Empty;
            shortcut.WorkingDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            shortcut.IconLocation = iconResource;
            shortcut.Description = "Vantage";
            shortcut.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        var interfaceId = PropertyStoreInterfaceId;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(
            shortcutPath,
            IntPtr.Zero,
            0x2,
            ref interfaceId,
            out var store));
        try
        {
            SetString(store, 3, iconResource);
            SetString(store, 5, AppUserModelId);
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally
        {
            Marshal.FinalReleaseComObject(store);
        }
    }

    private static void SetString(IPropertyStore store, uint propertyId, string value)
    {
        var key = new PropertyKey(AppUserModelFormatId, propertyId);
        var variant = PropVariant.FromString(value);
        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant));
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;

        public static PropVariant FromString(string value) => new()
        {
            VariantType = 31,
            PointerValue = Marshal.StringToCoTaskMemUni(value),
        };
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propertyCount);
        [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr windowHandle,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path,
        IntPtr bindContext,
        uint flags,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr parameter,
        IntPtr value);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
