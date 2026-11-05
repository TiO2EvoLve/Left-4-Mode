using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

/// <summary>
/// 扫描模组库并把文件、持久化启用状态、部署状态合并成 UI 使用的模组目录。
/// </summary>
public sealed class ModCatalogService : IModCatalogService
{
    private readonly IModLibraryService _library;
    private readonly IModStateStore _stateStore;
    private readonly IDeploymentService _deployment;
    private readonly ILogService _log;

    public ModCatalogService(
        IModLibraryService library,
        IModStateStore stateStore,
        IDeploymentService deployment,
        ILogService log)
    {
        _library = library;
        _stateStore = stateStore;
        _deployment = deployment;
        _log = log;
    }

    public async Task<ModCatalog> LoadAsync(
        string? addonsPath,
        CancellationToken cancellationToken = default)
    {
        if (!_library.IsConfigured || _library.RootPath is null)
        {
            throw new LibraryPathException("尚未选择模组库。");
        }

        var root = _library.RootPath;
        var files = await _library.EnumerateModFilesAsync(cancellationToken).ConfigureAwait(false);
        var state = await _stateStore.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        var manifest = await _deployment.LoadManifestAsync(cancellationToken).ConfigureAwait(false);

        var existingRecords = new Dictionary<string, ModRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in state.Mods)
        {
            if (!string.IsNullOrWhiteSpace(record.RelativePath))
            {
                existingRecords[record.RelativePath] = record;
            }
        }

        var records = new List<ModRecord>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = state.Mods.Count != files.Count;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seen.Add(file.RelativePath))
            {
                continue;
            }

            if (!existingRecords.TryGetValue(file.RelativePath, out var record))
            {
                record = new ModRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RelativePath = file.RelativePath,
                    DisplayName = Path.GetFileName(file.RelativePath),
                };
                record.WorkshopId = TryGetWorkshopId(file.RelativePath);
                changed = true;
            }

            if (record.WorkshopId is null &&
                TryGetWorkshopId(file.RelativePath) is { } inferredWorkshopId)
            {
                record.WorkshopId = inferredWorkshopId;
                changed = true;
            }

            var normalizedGroup = file.GroupPath ?? string.Empty;
            if (!string.Equals(record.RelativePath, file.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(record.GroupPath, normalizedGroup, StringComparison.Ordinal) ||
                record.Size != file.Size ||
                record.LastModifiedUtc != file.LastModifiedUtc ||
                string.IsNullOrWhiteSpace(record.DisplayName))
            {
                changed = true;
            }

            record.RelativePath = file.RelativePath;
            record.GroupPath = normalizedGroup;
            record.Size = file.Size;
            record.LastModifiedUtc = file.LastModifiedUtc;
            record.DisplayName = string.IsNullOrWhiteSpace(record.DisplayName)
                ? Path.GetFileName(file.RelativePath)
                : record.DisplayName;

            records.Add(record);
        }

        state.LibraryPath = root;
        state.Mods = records;

        if (changed)
        {
            try
            {
                await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error(ex, "保存对账后的模组状态失败");
            }
        }

        var items = new List<ModItem>(records.Count);
        foreach (var record in records)
        {
            var fullPath = _library.GetFullPath(record.RelativePath);
            var status = _deployment.GetStatus(manifest, addonsPath, record);
            items.Add(new ModItem(record, fullPath, status));
        }

        var groups = items
            .Select(item => item.GroupPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Length == 0 ? 0 : 1)
            .ThenBy(group => group, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _log.Info($"模组目录已加载：{items.Count} 个模组，{groups.Length} 个分组");

        return new ModCatalog(state, items, groups, manifest);
    }

    private static ulong? TryGetWorkshopId(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        if (!string.Equals(directory, "Workshop", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        return ulong.TryParse(fileName, out var workshopId) && workshopId > 0
            ? workshopId
            : null;
    }

    public Task SaveStateAsync(ModState state, CancellationToken cancellationToken = default) =>
        _stateStore.SaveAsync(state, cancellationToken);

    public async Task RefreshDeploymentStatesAsync(
        ModCatalog catalog,
        string? addonsPath,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _deployment.LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        catalog.Manifest = manifest;

        foreach (var item in catalog.Mods)
        {
            item.DeploymentState = _deployment.GetStatus(manifest, addonsPath, item.Record);
        }
    }
}
