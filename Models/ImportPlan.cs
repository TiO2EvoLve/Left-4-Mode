using System;
using System.Collections.Generic;

namespace Left_4_Mod.Models;

public sealed class ImportPlan(string destinationRelativePath, IReadOnlyList<ImportPlanItem> items)
{
    public string DestinationRelativePath { get; } = destinationRelativePath;

    public IReadOnlyList<ImportPlanItem> Items { get; } = items;

    public int RenamedCount
    {
        get
        {
            var count = 0;
            foreach (var item in Items)
            {
                if (item.Renamed)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public long TotalBytes
    {
        get
        {
            var total = 0L;
            foreach (var item in Items)
            {
                total += item.SourceSize;
            }

            return total;
        }
    }
}

public sealed class ImportPlanItem
{
    public ImportPlanItem(
        string sourcePath,
        string destinationRelativePath,
        string fileName,
        bool renamed,
        long sourceSize)
    {
        SourcePath = sourcePath;
        DestinationRelativePath = destinationRelativePath;
        FileName = fileName;
        Renamed = renamed;
        SourceSize = sourceSize;
    }

    public string SourcePath { get; }

    public string DestinationRelativePath { get; }

    public string FileName { get; }

    public bool Renamed { get; }

    public long SourceSize { get; }
}

public sealed class ImportProgress
{
    public ImportProgress(
        int completedFiles,
        int totalFiles,
        long copiedBytes,
        long totalBytes,
        string currentFileName)
    {
        CompletedFiles = completedFiles;
        TotalFiles = totalFiles;
        CopiedBytes = copiedBytes;
        TotalBytes = totalBytes;
        CurrentFileName = currentFileName;
    }

    public int CompletedFiles { get; }

    public int TotalFiles { get; }

    public long CopiedBytes { get; }

    public long TotalBytes { get; }

    public string CurrentFileName { get; }

    public double Percentage =>
        TotalBytes > 0
            ? Math.Clamp(CopiedBytes * 100d / TotalBytes, 0d, 100d)
            : TotalFiles > 0
                ? Math.Clamp(CompletedFiles * 100d / TotalFiles, 0d, 100d)
                : 0d;
}

public sealed class ImportOutcome
{
    public ImportOutcome(
        IReadOnlyList<string> importedRelativePaths,
        IReadOnlyList<ImportFailure> failures,
        int renamedCount,
        bool canceled)
    {
        ImportedRelativePaths = importedRelativePaths;
        Failures = failures;
        RenamedCount = renamedCount;
        Canceled = canceled;
    }

    public IReadOnlyList<string> ImportedRelativePaths { get; }

    public IReadOnlyList<ImportFailure> Failures { get; }

    public int RenamedCount { get; }

    public bool Canceled { get; }
}

public sealed record ImportFailure(string FileName, string Message);
