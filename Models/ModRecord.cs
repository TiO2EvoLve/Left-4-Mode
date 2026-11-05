
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Left_4_Mod.Models;

/// <summary>模组库中一个 .vpk 模组的持久化状态。</summary>
public sealed class ModRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string RelativePath { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>相对于模组库的分组目录，根目录为空字符串。</summary>
    public string GroupPath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public string? Description { get; set; }

    public List<string> Tags { get; set; } = [];

    public ulong? WorkshopId { get; set; }

    public ulong WorkshopTimeUpdated { get; set; }

    public string? WorkshopTitle { get; set; }

    public string? WorkshopPreviewUrl { get; set; }

    public bool WorkshopAutoUpdate { get; set; } = true;

    public long Size { get; set; }

    public DateTime LastModifiedUtc { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public string FileName => Path.GetFileName(RelativePath);

    [Newtonsoft.Json.JsonIgnore]
    public bool IsWorkshop => WorkshopId.HasValue;
}

public sealed class ModState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string LibraryPath { get; set; } = string.Empty;

    public List<ModRecord> Mods { get; set; } = [];
}

public sealed record ModFileInfo(
    string FullPath,
    string RelativePath,
    string GroupPath,
    long Size,
    DateTime LastModifiedUtc);
