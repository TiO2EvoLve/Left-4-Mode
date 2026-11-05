using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

/// <summary>
/// 模组库服务。所有路径参数都相对于模组库根目录，并且会被强制限制在库内。
/// </summary>
public interface IModLibraryService
{
    bool IsConfigured { get; }

    string? RootPath { get; }

    string LibraryName { get; }

    void SetLibrary(string rootPath);

    string GetFullPath(string relativePath);

    string GetRelativePath(string fullPath);

    bool IsInsideLibrary(string fullPath);

    Task<IReadOnlyList<FileSystemEntry>> ScanAsync(string relativeFolder, CancellationToken cancellationToken);

    /// <summary>递归枚举模组库内的全部子文件夹（相对路径，不含根目录）。</summary>
    Task<IReadOnlyList<string>> EnumerateFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>递归枚举模组库内的全部 VPK 模组。</summary>
    Task<IReadOnlyList<ModFileInfo>> EnumerateModFilesAsync(CancellationToken cancellationToken);

    Task<FileSystemEntry> CreateFolderAsync(
        string parentRelativePath,
        string name,
        CancellationToken cancellationToken);

    Task<string> RenameAsync(string relativePath, string newName, CancellationToken cancellationToken);

    Task<DeleteInfo> GetDeleteInfoAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken);

    Task DeleteAsync(IReadOnlyList<string> relativePaths, CancellationToken cancellationToken);

    ImportPlan CreateImportPlan(string destinationRelativePath, IReadOnlyList<string> sourceFiles);

    Task<ImportOutcome> ImportAsync(
        ImportPlan plan,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> CopyAsync(
        IReadOnlyList<string> sourceRelativePaths,
        string destinationRelativePath,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> MoveAsync(
        IReadOnlyList<string> sourceRelativePaths,
        string destinationRelativePath,
        CancellationToken cancellationToken);
}
