using System;
using System.Net.Http;
using DryIoc;
using Left_4_Mod.Services;
using Left_4_Mod.ViewModels;
using Serilog;

namespace Left_4_Mod.Infrastructure;

/// <summary>组合根：所有应用服务都在这里注册到 DryIoc。</summary>
public static class ServiceRegistration
{
    public static void Configure(Container container)
    {
        App.Container = container;

        // 创意工坊 Web API 与下载共用；不设置 API Key，使用公开接口。
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("L4D2ModManager/1.0");

        container.RegisterInstance(Log.Logger);
        container.RegisterInstance(httpClient);
        container.Register<ILogService, SerilogLogService>(Reuse.Singleton);
        container.Register<ISettingsService, JsonSettingsService>(Reuse.Singleton);
        container.Register<IModLibraryService, ModLibraryService>(Reuse.Singleton);
        container.Register<IModStateStore, JsonModStateStore>(Reuse.Singleton);
        container.Register<IGamePathService, GamePathService>(Reuse.Singleton);
        container.Register<IDeploymentService, DeploymentService>(Reuse.Singleton);
        container.Register<IModCatalogService, ModCatalogService>(Reuse.Singleton);
        container.Register<IModThumbnailService, ModThumbnailService>(Reuse.Singleton);
        container.Register<IWorkshopDownloader, DownloaderWorkshopDownloader>(Reuse.Singleton);
        container.Register<IWorkshopService, WorkshopService>(Reuse.Singleton);
        container.Register<MainViewModel>(Reuse.Singleton);
    }
}
