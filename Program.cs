using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Left_4_Mod.Infrastructure;
using ReactiveUI.Avalonia.Splat;
using Serilog;
using Serilog.Events;

namespace Left_4_Mod;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureLogging();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "应用异常退出");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureLogging()
    {
        try
        {
            AppPaths.EnsureCreated();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(AppPaths.LogsDirectory, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                .CreateLogger();

            Log.Information("应用启动，版本 {Version}", typeof(Program).Assembly.GetName().Version);
            Log.Information("应用数据目录：{DataDirectory}", AppPaths.AppDataDirectory);
        }
        catch (Exception ex)
        {
            // 日志目录不可写时退化为静默日志，至少让应用还能启动。
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .CreateLogger();
            Debug.WriteLine($"初始化日志失败：{ex}");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUIWithDryIoc(ServiceRegistration.Configure);
}
