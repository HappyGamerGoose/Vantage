using Microsoft.UI.Xaml;

namespace Vantage;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        LogStartup("App constructor starting");

        // Force PerMonitorV2 DPI awareness before any UI is created.
        // Without this, WinUI 3 can render slightly soft on high-DPI displays.
        try
        {
            var setContext = Win32.GetProcAddress(
                Win32.LoadLibrary("user32.dll"),
                "SetProcessDpiAwarenessContext");
            if (setContext != IntPtr.Zero)
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == -4
                Win32.SetProcessDpiAwarenessContext(new IntPtr(-4));
                LogStartup("DPI awareness: PerMonitorV2");
            }
            else
            {
                var setAwareness = Win32.GetProcAddress(
                    Win32.LoadLibrary("shcore.dll"),
                    "SetProcessDpiAwareness");
                if (setAwareness != IntPtr.Zero)
                {
                    Win32.SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
                    LogStartup("DPI awareness: PerMonitor");
                }
            }
        }
        catch (Exception ex)
        {
            LogStartup($"DPI awareness set skipped: {ex.Message}");
        }

        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        InitializeComponent();
        LogStartup("App constructor completed");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            LogStartup("OnLaunched starting");
            _window = new MainWindow();
            LogStartup("MainWindow created");
            _window.Activate();
            LogStartup("MainWindow activated");
        }
        catch (Exception ex)
        {
            LogStartup($"OnLaunched failed: {DescribeException(ex)}");
            throw;
        }
    }

    public static void LogStartup(string message)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vantage");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "startup.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogStartup($"XAML unhandled exception: {DescribeException(e.Exception)}");
    }

    private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogStartup($"Domain unhandled exception: {e.ExceptionObject}");
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogStartup($"Task unobserved exception: {DescribeException(e.Exception)}");
    }

    private static string DescribeException(Exception ex)
    {
        var details = new List<string>
        {
            $"{ex.GetType().FullName}: {ex.Message}",
            $"HResult: 0x{ex.HResult:X8}"
        };

        foreach (var property in ex.GetType().GetProperties().Where(property => property.GetIndexParameters().Length == 0))
        {
            try
            {
                if (property.Name is "LineNumber" or "LinePosition" or "XmlLineNumber" or "XmlLinePosition")
                {
                    details.Add($"{property.Name}: {property.GetValue(ex)}");
                }
            }
            catch
            {
            }
        }

        if (ex.InnerException is not null)
        {
            details.Add($"Inner: {DescribeException(ex.InnerException)}");
        }

        details.Add(ex.StackTrace ?? string.Empty);
        return string.Join(Environment.NewLine, details);
    }
}

internal static class Win32
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [System.Runtime.InteropServices.DllImport("shcore.dll", SetLastError = true)]
    public static extern int SetProcessDpiAwareness(int awareness);
}
