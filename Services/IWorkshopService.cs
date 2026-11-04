using System;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

public interface IWorkshopDownloader
{
    Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<WorkshopDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IWorkshopService
{
    Task<WorkshopItemInfo> GetDetailsAsync(ulong publishedFileId, CancellationToken cancellationToken = default);

    Task<WorkshopAddResult> AddAsync(
        ulong publishedFileId,
        IProgress<WorkshopProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<WorkshopUpdateResult> CheckForUpdatesAsync(
        ModCatalog catalog,
        IProgress<WorkshopProgress>? progress,
        CancellationToken cancellationToken = default);
}

public static class WorkshopUtils
{
    public static bool TryParsePublishedFileId(string input, out ulong id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (ulong.TryParse(trimmed, out id))
        {
            return true;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"(?:sharedfiles|workshop)/filedetails/\?.*?id=(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success && ulong.TryParse(match.Groups[1].Value, out id);
    }
}
