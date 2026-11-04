using System;
using System.IO;
using Left_4_Mod.Services;
using Material.Icons;
using ReactiveUI;

namespace Left_4_Mod.Models;

/// <summary>
/// 模组库文件列表中的一行（文件或文件夹）。
/// </summary>
public sealed class FileSystemEntry : ReactiveObject
{
    private bool _isRenaming;
    private string _editName = string.Empty;
    private bool _isCut;

    public FileSystemEntry(
        string fullPath,
        string relativePath,
        bool isFolder,
        long size,
        DateTime modified,
        bool isHidden)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        IsFolder = isFolder;
        Size = size;
        Modified = modified;
        IsHidden = isHidden;
        IsMod = !isFolder &&
                string.Equals(Path.GetExtension(fullPath), ".vpk", StringComparison.OrdinalIgnoreCase);

        Name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(Name))
        {
            Name = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        TypeText = FileSystemService.GetTypeDisplayName(fullPath, isFolder);
        IconKind = FileSystemService.GetIconKind(fullPath, isFolder);
        SizeText = isFolder ? string.Empty : FileSystemService.FormatSize(size);
        ModifiedText = modified == DateTime.MinValue
            ? string.Empty
            : modified.ToString("yyyy/MM/dd HH:mm");
    }

    public string Name { get; }

    public string FullPath { get; }

    /// <summary>相对于模组库根目录的路径（使用 Windows 分隔符）。</summary>
    public string RelativePath { get; }

    public bool IsFolder { get; }

    public bool IsMod { get; }

    public long Size { get; }

    public DateTime Modified { get; }

    public bool IsHidden { get; }

    public string TypeText { get; }

    public string SizeText { get; }

    public string ModifiedText { get; }

    public MaterialIconKind IconKind { get; }

    public bool IsCut
    {
        get => _isCut;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCut, value);
            this.RaisePropertyChanged(nameof(RowOpacity));
        }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => this.RaiseAndSetIfChanged(ref _isRenaming, value);
    }

    public string EditName
    {
        get => _editName;
        set => this.RaiseAndSetIfChanged(ref _editName, value);
    }

    public double RowOpacity => IsCut ? 0.45 : IsHidden ? 0.7 : 1.0;

    public void BeginRename(string? initialName = null)
    {
        EditName = initialName ?? Name;
        IsRenaming = true;
    }

    public void CancelRename()
    {
        IsRenaming = false;
        EditName = Name;
    }

    public override string ToString() => RelativePath;
}
