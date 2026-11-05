using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Infrastructure;
using Left_4_Mod.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Left_4_Mod.Services;

public sealed class DeploymentException : Exception
{
    public DeploymentException(string message)
        : base(message)
    {
    }

    public DeploymentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 把启用的模组同步到游戏 addons 目录。
/// 所有写入/删除都以部署清单为依据，绝不触碰用户手动放入且未登记的文件。
/// </summary>
public sealed class DeploymentService : IDeploymentService
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    private readonly IModLibraryService _library;
    private readonly ILogService _log;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);

    public DeploymentService(IModLibraryService library, ILogService log)
    {
        _library = library;
        _log = log;
    }

    public string ManifestFilePath { get; set; } = AppPaths.DeploymentsFile;

    public async Task<DeploymentManifest> LoadManifestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(ManifestFilePath))
            {
                return new DeploymentManifest();
            }

            var json = await File.ReadAllTextAsync(ManifestFilePath, cancellationToken).ConfigureAwait(false);
            var manifest = JsonConvert.DeserializeObject<DeploymentManifest>(json, JsonSettings) ??
                           new DeploymentManifest();

            manifest.Version = DeploymentManifest.CurrentVersion;
            manifest.Targets = manifest.Targets is null
                ? new Dictionary<string, List<DeploymentEntry>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<DeploymentEntry>>(manifest.Targets, StringComparer.OrdinalIgnoreCase);

            foreach (var key in manifest.Targets.Keys.ToArray())
            {
                manifest.Targets[key] ??= [];
            }

            return manifest;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"读取部署清单失败：{ManifestFilePath}");
            BackupBrokenManifest();
            return new DeploymentManifest();
        }
    }

    public ModDeploymentState GetStatus(
        DeploymentManifest manifest,
        string? addonsPath,
        ModRecord record)
    {
        if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
        {
            return ModDeploymentState.NotDeployed;
        }

        var key = NormalizeAddonsPath(addonsPath);
        if (!manifest.Targets.TryGetValue(key, out var entries))
        {
            return ModDeploymentState.NotDeployed;
        }

        var entry = FindEntry(entries, record);
        if (entry is null)
        {
            return ModDeploymentState.NotDeployed;
        }

        var destination = GetSafeDestinationPath(key, entry.DestinationFileName);
        if (destination is null || !File.Exists(destination))
        {
            return ModDeploymentState.MissingDeployment;
        }

        string sourceFullPath;
        try
        {
            sourceFullPath = _library.GetFullPath(record.RelativePath);
        }
        catch (Exception ex)
        {
            _log.Warn($"检查部署状态时无法解析源路径：{record.RelativePath}（{ex.Message}）");
            return ModDeploymentState.MissingDeployment;
        }

        if (!File.Exists(sourceFullPath))
        {
            return ModDeploymentState.MissingDeployment;
        }

        return IsSymbolicLinkValid(destination, sourceFullPath)
            ? ModDeploymentState.Deployed
            : ModDeploymentState.UpdatePending;
    }

    public DeploymentPlan CreatePlan(
        IReadOnlyList<ModRecord> mods,
        DeploymentManifest manifest,
        string addonsPath)
    {
        if (string.IsNullOrWhiteSpace(addonsPath))
        {
            throw new DeploymentException("尚未设置游戏目录。");
        }

        var normalizedAddons = NormalizeAddonsPath(addonsPath);
        if (!Directory.Exists(normalizedAddons))
        {
            throw new DeploymentException($"游戏 addons 目录不存在：{normalizedAddons}");
        }

        var entries = manifest.Targets.TryGetValue(normalizedAddons, out var existingEntries)
            ? existingEntries
            : [];

        var items = new List<DeploymentPlanItem>();
        var matchedEntries = new HashSet<DeploymentEntry>();
        var matchedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in mods.Where(record => record.IsEnabled))
        {
            var existing = FindEntry(entries, record);
            var destinationFileName = existing?.DestinationFileName ?? BuildDestinationFileName(record.Id);
            var destination = GetSafeDestinationPath(normalizedAddons, destinationFileName);

            string sourceFullPath;
            try
            {
                sourceFullPath = _library.GetFullPath(record.RelativePath);
            }
            catch (Exception ex)
            {
                _log.Warn($"生成推送计划时无法解析源路径：{record.RelativePath}（{ex.Message}）");
                continue;
            }

            if (destination is null)
            {
                items.Add(new DeploymentPlanItem(
                    record,
                    existing,
                    DeploymentAction.Update,
                    destinationFileName,
                    isConflict: true,
                    "部署文件名无效。"));
                matchedModIds.Add(record.Id);
                if (existing is not null)
                {
                    matchedEntries.Add(existing);
                }

                continue;
            }

            if (!File.Exists(sourceFullPath))
            {
                items.Add(new DeploymentPlanItem(
                    record,
                    existing,
                    DeploymentAction.Update,
                    destinationFileName,
                    isConflict: true,
                    "源模组文件不存在。"));
                matchedModIds.Add(record.Id);
                if (existing is not null)
                {
                    matchedEntries.Add(existing);
                }

                continue;
            }

            if (existing is null)
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    items.Add(new DeploymentPlanItem(
                        record,
                        null,
                        DeploymentAction.Add,
                        destinationFileName,
                        isConflict: true,
                        "游戏目录中已存在同名文件，且不是本管理器创建的，已跳过。"));
                }
                else
                {
                    items.Add(new DeploymentPlanItem(
                        record,
                        null,
                        DeploymentAction.Add,
                        destinationFileName,
                        isConflict: false,
                        "新增部署。"));
                }

                matchedModIds.Add(record.Id);
                continue;
            }

            matchedEntries.Add(existing);
            matchedModIds.Add(record.Id);

            if (Directory.Exists(destination))
            {
                items.Add(new DeploymentPlanItem(
                    record,
                    existing,
                    DeploymentAction.Update,
                    destinationFileName,
                    isConflict: true,
                    "目标位置是一个目录，无法覆盖。"));
                continue;
            }

            var needsUpdate = !IsSymbolicLinkValid(destination, sourceFullPath);
            items.Add(new DeploymentPlanItem(
                record,
                existing,
                needsUpdate ? DeploymentAction.Update : DeploymentAction.Keep,
                destinationFileName,
                isConflict: false,
                needsUpdate ? "需要创建或更新符号链接。" : "符号链接已指向最新源文件。"));
        }

        // 禁用、已删除或已不再启用的模组：只清理本管理器登记的部署项。
        foreach (var entry in entries)
        {
            if (!EntryBelongsToCurrentLibrary(entry))
            {
                continue;
            }

            if (matchedEntries.Contains(entry) || matchedModIds.Contains(entry.ModId))
            {
                continue;
            }

            items.Add(new DeploymentPlanItem(
                null,
                entry,
                DeploymentAction.Remove,
                entry.DestinationFileName,
                isConflict: false,
                "模组已禁用或已移除，清理部署项。"));
        }

        return new DeploymentPlan(normalizedAddons, manifest, items);
    }

    public async Task<DeploymentResult> ApplyAsync(
        DeploymentPlan plan,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var normalizedAddons = NormalizeAddonsPath(plan.AddonsPath);
        if (!Directory.Exists(normalizedAddons))
        {
            throw new DeploymentException($"游戏 addons 目录不存在：{normalizedAddons}");
        }

        var entries = plan.Manifest.Targets.TryGetValue(normalizedAddons, out var existingEntries)
            ? existingEntries
            : [];

        if (!plan.Manifest.Targets.ContainsKey(normalizedAddons))
        {
            plan.Manifest.Targets[normalizedAddons] = entries;
        }

        var added = 0;
        var updated = 0;
        var removed = 0;
        var skipped = 0;
        var completed = 0;
        var failures = new List<DeploymentFailure>();
        var canceled = false;

        foreach (var item in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            if (item.Action == DeploymentAction.Keep)
            {
                completed++;
                progress?.Report(new DeploymentProgress(completed, plan.Items.Count, item.DisplayName));
                continue;
            }

            if (item.IsConflict)
            {
                skipped++;
                failures.Add(new DeploymentFailure(item.DisplayName, item.Message));
                completed++;
                progress?.Report(new DeploymentProgress(completed, plan.Items.Count, item.DisplayName));
                continue;
            }

            try
            {
                if (item.Action == DeploymentAction.Remove)
                {
                    if (item.Existing is not null)
                    {
                        var destination = GetSafeDestinationPath(normalizedAddons, item.Existing.DestinationFileName);
                        if (destination is null)
                        {
                            throw new DeploymentException("部署文件名无效，拒绝删除。");
                        }

                        if (Directory.Exists(destination))
                        {
                            throw new DeploymentException("目标位置是目录，拒绝删除。");
                        }

                        if (File.Exists(destination))
                        {
                            File.Delete(destination);
                        }

                        entries.Remove(item.Existing);
                        await SaveManifestAsync(plan.Manifest, cancellationToken).ConfigureAwait(false);
                        removed++;
                    }
                }
                else
                {
                    if (item.Mod is null)
                    {
                        throw new DeploymentException("推送项缺少模组信息。");
                    }

                    var sourceFullPath = _library.GetFullPath(item.Mod.RelativePath);
                    if (!File.Exists(sourceFullPath))
                    {
                        throw new FileNotFoundException("源模组文件不存在。", sourceFullPath);
                    }

                    var destination = GetSafeDestinationPath(normalizedAddons, item.DestinationFileName)
                                      ?? throw new DeploymentException("部署文件名无效。");

                    if (item.Existing is null && (File.Exists(destination) || Directory.Exists(destination)))
                    {
                        throw new DeploymentException("目标文件已存在且不属于本管理器，拒绝覆盖。");
                    }

                    if (Directory.Exists(destination))
                    {
                        throw new DeploymentException("目标位置是目录，拒绝覆盖。");
                    }

                    var temporaryPath = Path.Combine(
                        normalizedAddons,
                        $".l4m-deploy-{Guid.NewGuid():N}.tmp");

                    try
                    {
                        CreateSymbolicLink(sourceFullPath, temporaryPath);

                        if (item.Action == DeploymentAction.Update)
                        {
                            File.Move(temporaryPath, destination, overwrite: true);
                        }
                        else
                        {
                            File.Move(temporaryPath, destination, overwrite: false);
                        }
                    }
                    finally
                    {
                        TryDeleteFile(temporaryPath);
                    }

                    var sourceInfo = new FileInfo(sourceFullPath);
                    var entry = item.Existing;
                    if (entry is null)
                    {
                        entry = new DeploymentEntry();
                        entries.Add(entry);
                    }

                    entry.ModId = item.Mod.Id;
                    entry.SourceRelativePath = item.Mod.RelativePath;
                    entry.SourceLibraryPath = _library.RootPath;
                    entry.DestinationFileName = item.DestinationFileName;
                    entry.SourceSize = sourceInfo.Length;
                    entry.SourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
                    entry.Mode = SymbolicLinkMode;
                    entry.DeployedAtUtc = DateTime.UtcNow;

                    await SaveManifestAsync(plan.Manifest, cancellationToken).ConfigureAwait(false);

                    if (item.Action == DeploymentAction.Add)
                    {
                        added++;
                    }
                    else
                    {
                        updated++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                break;
            }
            catch (Exception ex)
            {
                failures.Add(new DeploymentFailure(item.DisplayName, ex.Message));
                _log.Error(ex, $"推送失败：{item.DisplayName}");
            }

            completed++;
            progress?.Report(new DeploymentProgress(completed, plan.Items.Count, item.DisplayName));
        }

        try
        {
            await SaveManifestAsync(plan.Manifest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "保存部署清单失败");
        }

        _log.Info(
            $"推送结束：新增 {added}，更新 {updated}，移除 {removed}，跳过 {skipped}，失败 {failures.Count}，取消={canceled}");

        return new DeploymentResult(added, updated, removed, skipped, failures, canceled);
    }

    public async Task RemoveModsAsync(
        IReadOnlyCollection<string> modIds,
        CancellationToken cancellationToken = default)
    {
        if (modIds.Count == 0)
        {
            return;
        }

        var ids = new HashSet<string>(modIds, StringComparer.OrdinalIgnoreCase);
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;

        foreach (var (addonsPath, entries) in manifest.Targets.ToArray())
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = entries[index];
                if (!ids.Contains(entry.ModId) || !EntryBelongsToCurrentLibrary(entry))
                {
                    continue;
                }

                var destination = GetSafeDestinationPath(addonsPath, entry.DestinationFileName);
                if (destination is not null && Directory.Exists(destination))
                {
                    _log.Warn($"跳过删除部署项，因为目标是一个目录：{destination}");
                    continue;
                }

                if (destination is not null && File.Exists(destination))
                {
                    try
                    {
                        File.Delete(destination);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"删除部署项失败：{destination}（{ex.Message}）");
                        continue;
                    }
                }

                entries.RemoveAt(index);
                changed = true;
            }
        }

        if (changed)
        {
            await SaveManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        }

        _log.Info($"已清理 {ids.Count} 个模组的部署项。");
    }

    public async Task UpdateModSourcesAsync(
        IReadOnlyCollection<ModRecord> records,
        CancellationToken cancellationToken = default)
    {
        if (records.Count == 0 || _library.RootPath is null)
        {
            return;
        }

        var enabledRecords = records
            .Where(record => record.IsEnabled)
            .GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (enabledRecords.Count == 0)
        {
            return;
        }

        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;

        foreach (var (addonsPath, entries) in manifest.Targets.ToArray())
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!enabledRecords.TryGetValue(entry.ModId, out var record) ||
                    !EntryBelongsToCurrentLibrary(entry))
                {
                    continue;
                }

                var destination = GetSafeDestinationPath(addonsPath, entry.DestinationFileName);
                if (destination is null || Directory.Exists(destination))
                {
                    continue;
                }

                string sourceFullPath;
                try
                {
                    sourceFullPath = _library.GetFullPath(record.RelativePath);
                }
                catch (Exception ex)
                {
                    _log.Warn($"更新部署项时无法解析源路径：{record.RelativePath}（{ex.Message}）");
                    continue;
                }

                if (!File.Exists(sourceFullPath))
                {
                    _log.Warn($"更新部署项时源文件不存在：{sourceFullPath}");
                    continue;
                }

                try
                {
                    if (!IsSymbolicLinkValid(destination, sourceFullPath))
                    {
                        var temporaryPath = Path.Combine(
                            addonsPath,
                            $".l4m-deploy-{Guid.NewGuid():N}.tmp");

                        try
                        {
                            CreateSymbolicLink(sourceFullPath, temporaryPath);
                            File.Move(temporaryPath, destination, overwrite: true);
                        }
                        finally
                        {
                            TryDeleteFile(temporaryPath);
                        }
                    }

                    var sourceInfo = new FileInfo(sourceFullPath);
                    entry.SourceRelativePath = record.RelativePath;
                    entry.SourceLibraryPath = _library.RootPath;
                    entry.SourceSize = sourceInfo.Length;
                    entry.SourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
                    entry.Mode = SymbolicLinkMode;
                    entry.DeployedAtUtc = DateTime.UtcNow;
                    changed = true;
                }
                catch (Exception ex)
                {
                    _log.Warn($"更新部署项符号链接失败：{entry.DestinationFileName}（{ex.Message}）");
                }
            }
        }

        if (changed)
        {
            await SaveManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        }

        _log.Info($"已更新 {enabledRecords.Count} 个模组的部署源路径。");
    }

    private const string SymbolicLinkMode = "SymbolicLink";

    private void CreateSymbolicLink(string sourcePath, string temporaryPath)
    {
        try
        {
            File.CreateSymbolicLink(temporaryPath, sourcePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw new DeploymentException(
                "创建符号链接失败。推送只使用符号链接而不复制源文件；Windows 需要开发者模式或管理员权限，且文件系统需支持符号链接。",
                ex);
        }
    }

    private static bool IsSymbolicLinkValid(string destinationPath, string sourcePath)
    {
        try
        {
            if (!File.Exists(destinationPath))
            {
                return false;
            }

            var attributes = File.GetAttributes(destinationPath);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            var target = File.ResolveLinkTarget(destinationPath, returnFinalTarget: true);
            return target is not null &&
                   FileSystemService.PathsEqual(target.FullName, sourcePath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task SaveManifestAsync(DeploymentManifest manifest, CancellationToken cancellationToken)
    {
        await _manifestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = ManifestFilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(ManifestFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            manifest.Version = DeploymentManifest.CurrentVersion;
            var json = JsonConvert.SerializeObject(manifest, JsonSettings);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, ManifestFilePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"保存部署清单失败：{ManifestFilePath}");
            throw;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
            _manifestGate.Release();
        }
    }

    private DeploymentEntry? FindEntry(IEnumerable<DeploymentEntry> entries, ModRecord record)
    {
        return entries.FirstOrDefault(entry =>
                   EntryBelongsToCurrentLibrary(entry) &&
                   string.Equals(entry.ModId, record.Id, StringComparison.OrdinalIgnoreCase))
               ?? entries.FirstOrDefault(entry =>
                   EntryBelongsToCurrentLibrary(entry) &&
                   string.Equals(entry.SourceRelativePath, record.RelativePath, StringComparison.OrdinalIgnoreCase));
    }

    private bool EntryBelongsToCurrentLibrary(DeploymentEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceLibraryPath) || _library.RootPath is null)
        {
            return true;
        }

        return FileSystemService.PathsEqual(entry.SourceLibraryPath, _library.RootPath);
    }

    private static string BuildDestinationFileName(string modId)
    {
        var safeId = string.IsNullOrWhiteSpace(modId)
            ? Guid.NewGuid().ToString("N")
            : modId;

        return $"l4m_{safeId}.vpk";
    }

    private static string? GetSafeDestinationPath(string addonsPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.GetFileName(fileName) != fileName ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var destination = Path.GetFullPath(Path.Combine(addonsPath, fileName));
        var root = TrimTrailingSeparator(addonsPath) + Path.DirectorySeparatorChar;

        return destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? destination
            : null;
    }

    private static string NormalizeAddonsPath(string path) =>
        TrimTrailingSeparator(Path.GetFullPath(path));

    private static string TrimTrailingSeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is not null && path.Length <= root.Length)
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void BackupBrokenManifest()
    {
        try
        {
            if (!File.Exists(ManifestFilePath))
            {
                return;
            }

            var backup = $"{ManifestFilePath}.broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(ManifestFilePath, backup, overwrite: true);
            _log.Warn($"已备份损坏的部署清单：{backup}");
        }
        catch (Exception ex)
        {
            _log.Warn($"备份部署清单失败：{ex.Message}");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"清理临时部署文件失败：{path}（{ex.Message}）");
        }
    }
}
