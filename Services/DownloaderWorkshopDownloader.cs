using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Downloader;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

/// <summary>使用 Downloader 包执行工坊文件下载。</summary>
public sealed class DownloaderWorkshopDownloader : IWorkshopDownloader
{
    private readonly ILogService _log;

    public DownloaderWorkshopDownloader(ILogService log)
    {
        _log = log;
    }

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<WorkshopDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("工坊项目没有可用的下载地址。");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        var configuration = new DownloadConfiguration
        {
            ChunkCount = 4,
            ParallelDownload = true,
            ClearPackageOnCompletionWithFailure = true,
            CheckDiskSizeBeforeDownload = true,
            HttpClientTimeout = 60_000,
        };

        var downloader = new DownloadService(configuration);
        void OnProgressChanged(object? sender, DownloadProgressChangedEventArgs e)
        {
            progress?.Report(new WorkshopDownloadProgress(
                e.ReceivedBytesSize,
                e.TotalBytesToReceive,
                e.BytesPerSecondSpeed,
                e.ProgressPercentage));
        }

        downloader.DownloadProgressChanged += OnProgressChanged;
        try
        {
            await downloader.DownloadFileTaskAsync(url, destinationPath, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length <= 0)
            {
                throw new IOException("下载完成后文件为空。");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"工坊文件下载失败：{url}");
            throw;
        }
        finally
        {
            downloader.DownloadProgressChanged -= OnProgressChanged;
            downloader.Dispose();
        }
    }
}
