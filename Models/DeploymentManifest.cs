using System;
using System.Collections.Generic;

namespace Left_4_Mod.Models;

/// <summary>
/// 部署清单。按游戏 addons 目录分组，确保只清理本管理器登记的文件。
/// </summary>
public sealed class DeploymentManifest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>键是规范化后的 addons 目录，值是该目录下的部署项。</summary>
    public Dictionary<string, List<DeploymentEntry>> Targets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DeploymentEntry
{
    public string ModId { get; set; } = string.Empty;

    public string SourceRelativePath { get; set; } = string.Empty;

    /// <summary>部署来源的模组库根目录，用于避免多个模组库互相覆盖。</summary>
    public string? SourceLibraryPath { get; set; }

    public string DestinationFileName { get; set; } = string.Empty;

    public long SourceSize { get; set; }

    public DateTime SourceLastWriteUtc { get; set; }

    public string Mode { get; set; } = "SymbolicLink";

    public DateTime DeployedAtUtc { get; set; }
}

public enum DeploymentAction
{
    Add,
    Update,
    Remove,
    Keep,
}

public sealed class DeploymentPlanItem
{
    public DeploymentPlanItem(
        ModRecord? mod,
        DeploymentEntry? existing,
        DeploymentAction action,
        string destinationFileName,
        bool isConflict,
        string message)
    {
        Mod = mod;
        Existing = existing;
        Action = action;
        DestinationFileName = destinationFileName;
        IsConflict = isConflict;
        Message = message;
    }

    public ModRecord? Mod { get; }

    public DeploymentEntry? Existing { get; }

    public DeploymentAction Action { get; }

    public string DestinationFileName { get; }

    public bool IsConflict { get; }

    public string Message { get; }

    public string DisplayName =>
        Mod is not null ? Mod.FileName : Existing?.DestinationFileName ?? DestinationFileName;
}

public sealed class DeploymentPlan
{
    public DeploymentPlan(string addonsPath, DeploymentManifest manifest, IReadOnlyList<DeploymentPlanItem> items)
    {
        AddonsPath = addonsPath;
        Manifest = manifest;
        Items = items;
    }

    public string AddonsPath { get; }

    public DeploymentManifest Manifest { get; }

    public IReadOnlyList<DeploymentPlanItem> Items { get; }

    public int AddCount => Count(DeploymentAction.Add);
    public int UpdateCount => Count(DeploymentAction.Update);
    public int RemoveCount => Count(DeploymentAction.Remove);
    public int KeepCount => Count(DeploymentAction.Keep);
    public int ConflictCount => CountConflicts();

    public bool HasChanges => AddCount > 0 || UpdateCount > 0 || RemoveCount > 0;

    private int Count(DeploymentAction action)
    {
        var count = 0;
        foreach (var item in Items)
        {
            if (item.Action == action && !item.IsConflict)
            {
                count++;
            }
        }

        return count;
    }

    private int CountConflicts()
    {
        var count = 0;
        foreach (var item in Items)
        {
            if (item.IsConflict)
            {
                count++;
            }
        }

        return count;
    }
}

public sealed class DeploymentProgress
{
    public DeploymentProgress(int completed, int total, string currentFile)
    {
        Completed = completed;
        Total = total;
        CurrentFile = currentFile;
    }

    public int Completed { get; }

    public int Total { get; }

    public string CurrentFile { get; }

    public double Percentage => Total > 0 ? Math.Clamp(Completed * 100d / Total, 0d, 100d) : 0d;
}

public sealed record DeploymentFailure(string FileName, string Message);

public sealed class DeploymentResult
{
    public DeploymentResult(
        int added,
        int updated,
        int removed,
        int skipped,
        IReadOnlyList<DeploymentFailure> failures,
        bool canceled)
    {
        Added = added;
        Updated = updated;
        Removed = removed;
        Skipped = skipped;
        Failures = failures;
        Canceled = canceled;
    }

    public int Added { get; }

    public int Updated { get; }

    public int Removed { get; }

    public int Skipped { get; }

    public IReadOnlyList<DeploymentFailure> Failures { get; }

    public bool Canceled { get; }
}
