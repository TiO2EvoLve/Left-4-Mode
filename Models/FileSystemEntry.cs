using System;
using System.IO;
using Material.Icons;
using Left_4_Mod.Services;
using ReactiveUI;

namespace Left_4_Mod.Models;

/// <summary>
/// 文件列表中的一行（文件或文件夹）。
/// </summary>
public sealed class FileSystemEntry : ReactiveObject
{
    private bool _isRenaming;
    private string _editName = string.Empty;
    private bool _isCut;

    public FileSystemEntry(string fullPath, bool isFolder, long size, DateTime modified, bool isHidden)
    {
        FullPath = fullPath;
        IsFolder = isFolder;
        Size = size;
        Modified = modified;
        IsHidden = isHidden;

        Name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(Name))
        {
            // 驱动器根目录（例如 "C:\"）没有文件名，退化为盘符。
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

    public bool IsFolder { get; }

    public long Size { get; }

    public DateTime Modified { get; }

    public bool IsHidden { get; }

    public string TypeText { get; }

    public string SizeText { get; }

    public string ModifiedText { get; }

    public MaterialIconKind IconKind { get; }

    /// <summary>是否为“剪切”状态（粘贴后消失）。</summary>
    public bool IsCut
    {
        get => _isCut;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCut, value);
            this.RaisePropertyChanged(nameof(RowOpacity));
        }
    }

    /// <summary>是否正在行内重命名。</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        private set => this.RaiseAndSetIfChanged(ref _isRenaming, value);
    }

    /// <summary>重命名输入框中的文本。</summary>
    public string EditName
    {
        get => _editName;
        set => this.RaiseAndSetIfChanged(ref _editName, value);
    }

    /// <summary>行透明度：剪切项变淡，隐藏项稍淡。</summary>
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

    public override string ToString() => FullPath;
}
