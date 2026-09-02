using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Vantage;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            VelopackApp.Build()
                .SetAppUserModelId("velopack.HappyGamerGoose.Vantage")
                .Run();
        }
        catch (Exception ex)
        {
            App.LogStartup($"VeloPack startup hook skipped: {ex.Message}");
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
