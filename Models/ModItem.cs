using System;
using System.Collections.Generic;
using System.IO;
using Left_4_Mod.Services;
using Material.Icons;
using ReactiveUI;

namespace Left_4_Mod.Models;

public enum ModDeploymentState
{
    NotDeployed,
    Deployed,
    UpdatePending,
    MissingDeployment,
}

public sealed class ModItem : ReactiveObject
{
    private readonly ModRecord _record;
    private ModDeploymentState _deploymentState;

    public ModItem(ModRecord record, string fullPath, ModDeploymentState deploymentState)
    {
        _record = record;
        FullPath = fullPath;
        _deploymentState = deploymentState;
    }

    public ModRecord Record => _record;

    public string Id => _record.Id;

    public string RelativePath => _record.RelativePath;

    public string FullPath { get; }

    public string FileName => Path.GetFileName(_record.RelativePath);

    public string DisplayName =>
        string.IsNullOrWhiteSpace(_record.DisplayName) ? FileName : _record.DisplayName!;

    public string GroupPath => _record.GroupPath ?? string.Empty;

    public string GroupDisplay =>
        string.IsNullOrEmpty(GroupPath) ? "未分类" : GroupPath.Replace('\\', '/');

    public long Size => _record.Size;

    public string SizeText => FileSystemService.FormatSize(Size);

    public DateTime LastModifiedUtc => _record.LastModifiedUtc;

    public string ModifiedText => LastModifiedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm");

    public MaterialIconKind IconKind => MaterialIconKind.FileStar;

    public bool IsEnabled
    {
        get => _record.IsEnabled;
        set
        {
            if (_record.IsEnabled == value)
            {
                return;
            }

            _record.IsEnabled = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(StatusIcon));
            this.RaisePropertyChanged(nameof(StatusDescription));
        }
    }

    public ModDeploymentState DeploymentState
    {
        get => _deploymentState;
        set
        {
            if (_deploymentState == value)
            {
                return;
            }

            _deploymentState = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(StatusIcon));
            this.RaisePropertyChanged(nameof(StatusDescription));
        }
    }

    public bool HasDeployment =>
        DeploymentState is ModDeploymentState.Deployed or ModDeploymentState.UpdatePending
            or ModDeploymentState.MissingDeployment;

    public bool IsWorkshop => _record.IsWorkshop;

    public string StatusText
    {
        get
        {
            if (!IsEnabled)
            {
                return HasDeployment ? "已禁用 · 已部署" : "未启用";
            }

            return DeploymentState switch
            {
                ModDeploymentState.Deployed => "已推送",
                ModDeploymentState.UpdatePending => "待更新",
                ModDeploymentState.MissingDeployment => "部署缺失",
                _ => "待推送",
            };
        }
    }

    public MaterialIconKind StatusIcon
    {
        get
        {
            if (!IsEnabled)
            {
                return HasDeployment ? MaterialIconKind.AlertCircle : MaterialIconKind.CloseCircle;
            }

            return DeploymentState switch
            {
                ModDeploymentState.Deployed => MaterialIconKind.CheckCircle,
                ModDeploymentState.UpdatePending => MaterialIconKind.ClockOutline,
                ModDeploymentState.MissingDeployment => MaterialIconKind.AlertCircle,
                _ => MaterialIconKind.ClockOutline,
            };
        }
    }

    public string StatusDescription =>
        DeploymentState switch
        {
            ModDeploymentState.Deployed => "游戏目录中的部署与源文件一致。",
            ModDeploymentState.UpdatePending => "源文件已修改，需要重新推送。",
            ModDeploymentState.MissingDeployment => "部署清单中的文件在游戏目录中缺失，需要重新推送。",
            _ => "尚未推送到游戏目录。",
        };

    public override string ToString() => RelativePath;
}

public sealed class ModCatalog
{
    public ModCatalog(
        ModState state,
        IReadOnlyList<ModItem> mods,
        IReadOnlyList<string> groups,
        DeploymentManifest manifest)
    {
        State = state;
        Mods = mods;
        Groups = groups;
        Manifest = manifest;
    }

    public ModState State { get; }

    public IReadOnlyList<ModItem> Mods { get; }

    public IReadOnlyList<string> Groups { get; }

    public DeploymentManifest Manifest { get; set; }
}

public sealed class GroupOption
{
    public GroupOption(string displayName, string? relativePath, int count)
    {
        DisplayName = displayName;
        RelativePath = relativePath;
        Count = count;
    }

    public string DisplayName { get; }

    /// <summary>为 null 表示“全部分组”。</summary>
    public string? RelativePath { get; }

    public int Count { get; }

    public string CountText => Count > 0 ? $" ({Count})" : string.Empty;
}

public sealed class FilterOption<T>
{
    public FilterOption(string displayName, T value)
    {
        DisplayName = displayName;
        Value = value;
    }

    public string DisplayName { get; }

    public T Value { get; }
}

public enum ModEnabledFilter
{
    All,
    Enabled,
    Disabled,
}

public enum ModDeploymentFilter
{
    All,
    Deployed,
    NotDeployed,
}
