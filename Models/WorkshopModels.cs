using System;
using System.Collections.Generic;

namespace Left_4_Mod.Models;

public sealed record WorkshopItemInfo(
    ulong PublishedFileId,
    string Title,
    string Description,
    string FileUrl,
    string PreviewUrl,
    ulong TimeCreated,
    ulong TimeUpdated,
    long FileSize,
    IReadOnlyList<string> Tags);

public sealed class WorkshopAddResult
{
    public WorkshopAddResult(
        bool success,
        string? error,
        WorkshopItemInfo? details,
        string? relativePath,
        bool wasUpdate)
    {
        Success = success;
        Error = error;
        Details = details;
        RelativePath = relativePath;
        WasUpdate = wasUpdate;
    }

    public bool Success { get; }

    public string? Error { get; }

    public WorkshopItemInfo? Details { get; }

    public string? RelativePath { get; }

    public bool WasUpdate { get; }

    public static WorkshopAddResult Fail(string error) => new(false, error, null, null, false);
}

public sealed class WorkshopUpdateResult
{
    public WorkshopUpdateResult(
        int checkedCount,
        IReadOnlyList<WorkshopUpdateItem> updated,
        IReadOnlyList<WorkshopFailure> failures,
        bool canceled)
    {
        CheckedCount = checkedCount;
        Updated = updated;
        Failures = failures;
        Canceled = canceled;
    }

    public int CheckedCount { get; }

    public IReadOnlyList<WorkshopUpdateItem> Updated { get; }

    public IReadOnlyList<WorkshopFailure> Failures { get; }

    public bool Canceled { get; }
}

public sealed record WorkshopUpdateItem(
    ulong PublishedFileId,
    string Title,
    string RelativePath,
    ulong OldTimeUpdated,
    ulong NewTimeUpdated);

public sealed record WorkshopFailure(ulong PublishedFileId, string Title, string Message);

public enum WorkshopProgressStage
{
    Checking,
    Downloading,
    Saving,
}

public sealed record WorkshopProgress(
    WorkshopProgressStage Stage,
    int CurrentIndex,
    int Total,
    double FilePercentage,
    string Message)
{
    /// <summary>0-100 的整体进度。</summary>
    public double OverallPercentage
    {
        get
        {
            if (Total <= 0)
            {
                return 0;
            }

            var current = Math.Clamp(CurrentIndex, 0, Total - 1);
            var filePart = Math.Clamp(FilePercentage, 0, 100) / 100d;
            return Math.Clamp((current + filePart) * 100d / Total, 0d, 100d);
        }
    }
}

public sealed record WorkshopDownloadProgress(
    long ReceivedBytes,
    long TotalBytes,
    double BytesPerSecond,
    double Percentage);
