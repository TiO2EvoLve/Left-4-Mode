using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

public sealed class ModLibraryService : IModLibraryService
{
    private const int CopyBufferSize = 1024 * 1024;

    private readonly ILogService _log;
    private string? _rootPath;

    public ModLibraryService(ILogService log)
    {
        _log = log;
    }

    public bool IsConfigured => _rootPath is not null;

    public string? RootPath => _rootPath;

    public string LibraryName
    {
        get
        {
            if (_rootPath is null)
            {
                return string.Empty;
            }

            var name = Path.GetFileName(_rootPath);
            return string.IsNullOrEmpty(name) ? _rootPath : name;
        }
    }

    public void SetLibrary(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new LibraryPathException("模组库路径不能为空。");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rootPath.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            throw new LibraryPathException($"模组库路径无效：{ex.Message}", ex);
        }

        if (!Directory.Exists(fullPath))
        {
            throw new LibraryPathException($"模组库目录不存在：{fullPath}");
        }

        var pathRoot = Path.GetPathRoot(fullPath);
        if (pathRoot is not null &&
            string.Equals(
                TrimTrailingSeparator(fullPath),
                TrimTrailingSeparator(pathRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new LibraryPathException("请选择一个专用的模组库文件夹，而不是磁盘根目录。");
        }

        try
        {
            // 读取权限检查：无法枚举时提前给出明确提示。
            using var enumerator = new DirectoryInfo(fullPath)
                .EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true })
                .GetEnumerator();
            enumerator.MoveNext();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LibraryPathException($"没有访问模组库的权限：{fullPath}", ex);
        }
        catch (IOException ex)
        {
            throw new LibraryPathException($"无法读取模组库：{fullPath}（{ex.Message}）", ex);
        }

        _rootPath = TrimTrailingSeparator(fullPath);
        _log.Info($"已设置模组库：{_rootPath}");
    }

    public string GetFullPath(string relativePath)
    {
        EnsureConfigured();
        var normalized = NormalizeRelativePath(relativePath);

        var fullPath = normalized.Length == 0
            ? _rootPath!
            : Path.GetFullPath(Path.Combine(_rootPath!, normalized));

        if (!IsInsideLibrary(fullPath))
        {
            throw new LibraryPathException($"不允许访问模组库以外的路径：{relativePath}");
        }

        return fullPath;
    }

    public string GetRelativePath(string fullPath)
    {
        EnsureConfigured();

        var full = Path.GetFullPath(fullPath);
        if (!IsInsideLibrary(full))
        {
            throw new LibraryPathException($"路径不在模组库内：{fullPath}");
        }

        var relative = Path.GetRelativePath(_rootPath!, full);
        return relative == "." ? string.Empty : relative;
    }

    public bool IsInsideLibrary(string fullPath)
    {
        if (_rootPath is null)
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(fullPath);
        }
        catch (Exception)
        {
            return false;
        }

        if (string.Equals(normalized, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        return normalized.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<FileSystemEntry>> ScanAsync(
        string relativeFolder,
        CancellationToken cancellationToken)
    {
        var normalizedFolder = NormalizeRelativePath(relativeFolder);
        var fullPath = GetFullPath(normalizedFolder);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"文件夹不存在：{relativeFolder}");
        }

        return Task.Run<IReadOnlyList<FileSystemEntry>>(
            () => FileSystemService.ReadEntries(fullPath, normalizedFolder, _log, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<ModFileInfo>> EnumerateModFilesAsync(CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var root = _rootPath!;

        return Task.Run<IReadOnlyList<ModFileInfo>>(
            () =>
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    RecurseSubdirectories = true,
                    ReturnSpecialDirectories = false,
                };

                var result = new List<ModFileInfo>();

                foreach (var filePath in Directory.EnumerateFiles(root, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fileName = Path.GetFileName(filePath);
                    if (!string.Equals(Path.GetExtension(filePath), ".vpk", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith(".l4m-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var file = new FileInfo(filePath);
                        var relativePath = Path.GetRelativePath(root, filePath);
                        var groupPath = Path.GetDirectoryName(relativePath) ?? string.Empty;

                        result.Add(new ModFileInfo(
                            file.FullName,
                            relativePath,
                            groupPath,
                            file.Length,
                            file.LastWriteTimeUtc));
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"枚举模组文件时跳过：{filePath}（{ex.Message}）");
                    }
                }

                _log.Info($"扫描到 {result.Count} 个 VPK 模组：{root}");
                return result;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> EnumerateFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var root = _rootPath!;

        return Task.Run<IReadOnlyList<string>>(
            () =>
            {
                var result = new List<string>();
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };

                foreach (var directory in Directory.EnumerateDirectories(root, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = Path.GetRelativePath(root, directory);
                    if (relativePath.StartsWith(".l4m-", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(relativePath, "photo", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(relativePath);
                }

                result.Sort(StringComparer.CurrentCultureIgnoreCase);
                return result;
            },
            cancellationToken);
    }

    public Task<FileSystemEntry> CreateFolderAsync(
        string parentRelativePath,
        string name,
        CancellationToken cancellationToken)    {
        cancellationToken.ThrowIfCancellationRequested();

        var trimmedName = name.Trim();
        if (!FileSystemService.IsValidEntryName(trimmedName, out var error))
        {
            throw new LibraryPathException(error);
        }

        var parentRelative = NormalizeRelativePath(parentRelativePath);
        var parentFullPath = GetFullPath(parentRelative);
        if (!Directory.Exists(parentFullPath))
        {
            throw new DirectoryNotFoundException($"父文件夹不存在：{parentRelativePath}");
        }

        var target = FileSystemService.GetUniquePath(parentFullPath, trimmedName);
        Directory.CreateDirectory(target);

        var createdName = Path.GetFileName(target);
        var relativePath = FileSystemService.CombineRelative(parentRelative, createdName);
        _log.Info($"已创建文件夹：{relativePath}");

        return Task.FromResult(FileSystemService.ReadEntry(target, relativePath));
    }

    public Task<string> RenameAsync(
        string relativePath,
        string newName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!FileSystemService.IsValidEntryName(newName.Trim(), out var error))
        {
            throw new LibraryPathException(error);
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Length == 0)
        {
            throw new LibraryPathException("不能重命名模组库根目录。");
        }

        var sourceFullPath = GetFullPath(normalized);
        if (!File.Exists(sourceFullPath) && !Directory.Exists(sourceFullPath))
        {
            throw new FileNotFoundException("要重命名的项目不存在。", sourceFullPath);
        }

        var parentRelative = FileSystemService.GetParentRelative(normalized) ?? string.Empty;
        var parentFullPath = GetFullPath(parentRelative);
        var destination = Path.Combine(parentFullPath, newName.Trim());

        if (string.Equals(newName.Trim(), Path.GetFileName(sourceFullPath), StringComparison.Ordinal))
        {
            return Task.FromResult(normalized);
        }

        var newRelativePath = FileSystemService.CombineRelative(parentRelative, newName.Trim());

        if (FileSystemService.PathsEqual(destination, sourceFullPath))
        {
            // 仅修改大小写：Windows 需要借助临时名称。
            var temporary = Path.Combine(parentFullPath, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(sourceFullPath))
            {
                Directory.Move(sourceFullPath, temporary);
                Directory.Move(temporary, destination);
            }
            else
            {
                File.Move(sourceFullPath, temporary);
                File.Move(temporary, destination);
            }

            _log.Info($"已重命名：{normalized} -> {newRelativePath}");
            return Task.FromResult(newRelativePath);
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new LibraryPathException($"当前文件夹中已存在名为“{newName.Trim()}”的项目。");
        }

        if (Directory.Exists(sourceFullPath))
        {
            Directory.Move(sourceFullPath, destination);
        }
        else
        {
            File.Move(sourceFullPath, destination);
        }

        _log.Info($"已重命名：{normalized} -> {newRelativePath}");
        return Task.FromResult(newRelativePath);
    }

    public Task<DeleteInfo> GetDeleteInfoAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        var normalizedPaths = NormalizeAndValidateTargets(relativePaths);

        return Task.Run(
            () =>
            {
                var fileCount = 0;
                var folderCount = 0;
                var totalBytes = 0L;

                foreach (var relativePath in normalizedPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fullPath = GetFullPath(relativePath);
                    if (Directory.Exists(fullPath))
                    {
                        CountDirectory(fullPath, cancellationToken, ref fileCount, ref folderCount, ref totalBytes);
                    }
                    else if (File.Exists(fullPath))
                    {
                        fileCount++;
                        totalBytes += new FileInfo(fullPath).Length;
                    }
                }

                return new DeleteInfo(fileCount, folderCount, totalBytes);
            },
            cancellationToken);
    }

    public async Task DeleteAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        var normalizedPaths = NormalizeAndValidateTargets(relativePaths);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPaths = normalizedPaths.Select(GetFullPath).ToArray();
        await FileSystemService.DeleteToRecycleBinAsync(fullPaths).ConfigureAwait(false);

        _log.Info($"已删除 {fullPaths.Length} 个项目到回收站：{string.Join("; ", normalizedPaths)}");
    }

    public ImportPlan CreateImportPlan(string destinationRelativePath, IReadOnlyList<string> sourceFiles)
    {
        var destinationRelative = NormalizeRelativePath(destinationRelativePath);
        var destinationFullPath = GetFullPath(destinationRelative);

        if (!Directory.Exists(destinationFullPath))
        {
            throw new DirectoryNotFoundException($"目标文件夹不存在：{destinationRelativePath}");
        }

        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ImportPlanItem>();

        foreach (var sourceFile in sourceFiles)
        {
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                continue;
            }

            var sourceFullPath = Path.GetFullPath(sourceFile);
            if (!File.Exists(sourceFullPath))
            {
                throw new FileNotFoundException($"要导入的文件不存在：{sourceFile}", sourceFullPath);
            }

            if (!string.Equals(Path.GetExtension(sourceFullPath), ".vpk", StringComparison.OrdinalIgnoreCase))
            {
                throw new LibraryPathException($"只能导入 .vpk 模组文件：{Path.GetFileName(sourceFullPath)}");
            }

            if (FileSystemService.PathsEqual(Path.GetDirectoryName(sourceFullPath), destinationFullPath))
            {
                // 已经位于当前目录，无需再复制一份。
                continue;
            }

            var desiredName = Path.GetFileName(sourceFullPath);
            var finalName = GenerateUniqueImportName(destinationFullPath, desiredName, reservedNames);
            var renamed = !string.Equals(finalName, desiredName, StringComparison.OrdinalIgnoreCase);
            var itemRelativePath = FileSystemService.CombineRelative(destinationRelative, finalName);

            items.Add(new ImportPlanItem(
                sourceFullPath,
                itemRelativePath,
                finalName,
                renamed,
                new FileInfo(sourceFullPath).Length));
        }

        _log.Info(
            $"导入计划：目标“{destinationRelative}”，{items.Count} 个文件，重命名 {items.Count(i => i.Renamed)} 个");

        return new ImportPlan(destinationRelative, items);
    }

    public async Task<ImportOutcome> ImportAsync(
        ImportPlan plan,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destinationRelative = NormalizeRelativePath(plan.DestinationRelativePath);
        var destinationFullPath = GetFullPath(destinationRelative);

        if (!Directory.Exists(destinationFullPath))
        {
            throw new DirectoryNotFoundException($"目标文件夹不存在：{plan.DestinationRelativePath}");
        }

        var imported = new List<string>();
        var failures = new List<ImportFailure>();
        var completed = 0;
        var copiedBytes = 0L;
        var canceled = false;

        foreach (var item in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            var temporaryPath = Path.Combine(
                destinationFullPath,
                $".l4m-import-{Guid.NewGuid():N}.tmp");

            try
            {
                if (!File.Exists(item.SourcePath))
                {
                    throw new FileNotFoundException("源文件已被移动或删除。", item.SourcePath);
                }

                var finalName = item.FileName;
                var finalPath = Path.Combine(destinationFullPath, finalName);
                if (File.Exists(finalPath) || Directory.Exists(finalPath))
                {
                    finalPath = FileSystemService.GetUniquePath(destinationFullPath, finalName);
                    finalName = Path.GetFileName(finalPath);
                }

                if (!IsInsideLibrary(finalPath))
                {
                    throw new LibraryPathException("导入目标越出模组库范围。");
                }

                await CopyFileAsync(
                    item.SourcePath,
                    temporaryPath,
                    completed,
                    plan.Items.Count,
                    copiedBytes,
                    plan.TotalBytes,
                    item.FileName,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                File.Move(temporaryPath, finalPath, overwrite: false);

                copiedBytes += item.SourceSize;
                completed++;
                imported.Add(FileSystemService.CombineRelative(destinationRelative, finalName));

                progress?.Report(new ImportProgress(
                    completed,
                    plan.Items.Count,
                    copiedBytes,
                    plan.TotalBytes,
                    item.FileName));
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                TryDeleteTemporaryFile(temporaryPath);
                break;
            }
            catch (Exception ex)
            {
                TryDeleteTemporaryFile(temporaryPath);
                failures.Add(new ImportFailure(item.FileName, ex.Message));
                _log.Error(ex, $"导入失败：{item.SourcePath}");
            }
        }

        _log.Info(
            $"导入完成：成功 {imported.Count} 个，失败 {failures.Count} 个，取消={canceled}，目标“{destinationRelative}”");

        return new ImportOutcome(imported, failures, plan.RenamedCount, canceled);
    }

    public Task<IReadOnlyList<string>> CopyAsync(
        IReadOnlyList<string> sourceRelativePaths,
        string destinationRelativePath,
        CancellationToken cancellationToken)
    {
        return TransferAsync(
            sourceRelativePaths,
            destinationRelativePath,
            move: false,
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> MoveAsync(
        IReadOnlyList<string> sourceRelativePaths,
        string destinationRelativePath,
        CancellationToken cancellationToken)
    {
        return TransferAsync(
            sourceRelativePaths,
            destinationRelativePath,
            move: true,
            cancellationToken);
    }

    private Task<IReadOnlyList<string>> TransferAsync(
        IReadOnlyList<string> sourceRelativePaths,
        string destinationRelativePath,
        bool move,
        CancellationToken cancellationToken)
    {
        var destinationRelative = NormalizeRelativePath(destinationRelativePath);
        var destinationFullPath = GetFullPath(destinationRelative);

        if (!Directory.Exists(destinationFullPath))
        {
            throw new DirectoryNotFoundException($"目标文件夹不存在：{destinationRelativePath}");
        }

        var normalizedSources = NormalizeAndValidateTargets(sourceRelativePaths);

        return Task.Run<IReadOnlyList<string>>(
            () =>
            {
                var results = new List<string>();

                foreach (var sourceRelative in normalizedSources)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var sourceFullPath = GetFullPath(sourceRelative);
                    if (!File.Exists(sourceFullPath) && !Directory.Exists(sourceFullPath))
                    {
                        throw new FileNotFoundException("要操作的项目不存在。", sourceFullPath);
                    }

                    var sourceParent = Path.GetDirectoryName(sourceFullPath);
                    if (move && sourceParent is not null &&
                        FileSystemService.PathsEqual(sourceParent, destinationFullPath))
                    {
                        // 剪切到同一目录没有意义；复制到同一目录则生成“副本”。
                        continue;
                    }

                    if (Directory.Exists(sourceFullPath) &&
                        FileSystemService.IsSameOrDescendant(destinationFullPath, sourceFullPath))
                    {
                        throw new LibraryPathException("不能把文件夹复制或移动到它自己或其子目录中。");
                    }

                    var name = Path.GetFileName(sourceFullPath.TrimEnd(Path.DirectorySeparatorChar));
                    var target = move
                        ? FileSystemService.GetUniquePath(destinationFullPath, name)
                        : FileSystemService.GetCopyPath(destinationFullPath, name);

                    if (!IsInsideLibrary(target))
                    {
                        throw new LibraryPathException("目标路径越出模组库范围。");
                    }

                    if (move)
                    {
                        FileSystemService.Move(sourceFullPath, target);
                    }
                    else
                    {
                        FileSystemService.Copy(sourceFullPath, target);
                    }

                    var resultRelative = FileSystemService.CombineRelative(
                        destinationRelative,
                        Path.GetFileName(target));
                    results.Add(resultRelative);
                    _log.Info($"{(move ? "已移动" : "已复制")}：{sourceRelative} -> {resultRelative}");
                }

                return results;
            },
            cancellationToken);
    }

    private async Task CopyFileAsync(
        string sourcePath,
        string temporaryPath,
        int completedFiles,
        int totalFiles,
        long copiedBytesSoFar,
        long totalBytes,
        string currentFileName,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            await using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous);

            var copied = copiedBytesSoFar;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;

                progress?.Report(new ImportProgress(
                    completedFiles,
                    totalFiles,
                    copied,
                    totalBytes,
                    currentFileName));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string GenerateUniqueImportName(
        string directory,
        string desiredName,
        ISet<string> reservedNames)
    {
        if (!reservedNames.Contains(desiredName) &&
            !File.Exists(Path.Combine(directory, desiredName)) &&
            !Directory.Exists(Path.Combine(directory, desiredName)))
        {
            reservedNames.Add(desiredName);
            return desiredName;
        }

        var stem = Path.GetFileNameWithoutExtension(desiredName);
        var extension = Path.GetExtension(desiredName);

        for (var index = 2; index < 10_000; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (reservedNames.Contains(candidate))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(directory, candidate)) &&
                !Directory.Exists(Path.Combine(directory, candidate)))
            {
                reservedNames.Add(candidate);
                return candidate;
            }
        }

        throw new IOException($"无法为“{desiredName}”生成可用的导入名称。");
    }

    private void CountDirectory(
        string directory,
        CancellationToken cancellationToken,
        ref int fileCount,
        ref int folderCount,
        ref long totalBytes)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
        };

        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (info is DirectoryInfo childDirectory)
                {
                    folderCount++;
                    CountDirectory(childDirectory.FullName, cancellationToken, ref fileCount, ref folderCount, ref totalBytes);
                }
                else if (info is FileInfo file)
                {
                    fileCount++;
                    totalBytes += file.Length;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"统计项目时跳过：{info.FullName}（{ex.Message}）");
            }
        }
    }

    private List<string> NormalizeAndValidateTargets(IReadOnlyList<string> relativePaths)
    {
        var normalized = new List<string>();

        foreach (var relativePath in relativePaths)
        {
            var item = NormalizeRelativePath(relativePath);
            if (item.Length == 0)
            {
                throw new LibraryPathException("不能对模组库根目录执行该操作。");
            }

            var fullPath = GetFullPath(item);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                throw new FileNotFoundException($"项目不存在：{relativePath}", fullPath);
            }

            normalized.Add(item);
        }

        return normalized;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Replace('/', '\\').Trim('\\');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(normalized))
        {
            throw new LibraryPathException("只能使用相对于模组库的路径。");
        }

        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new LibraryPathException("路径不能包含“.”或“..”。");
        }

        return string.Join('\\', segments);
    }

    private void EnsureConfigured()
    {
        if (_rootPath is null)
        {
            throw new LibraryPathException("尚未选择模组库。");
        }
    }

    private static string TrimTrailingSeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is not null && path.Length <= root.Length)
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void TryDeleteTemporaryFile(string path)
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
            _log.Warn($"清理导入临时文件失败：{path}（{ex.Message}）");
        }
    }
}
