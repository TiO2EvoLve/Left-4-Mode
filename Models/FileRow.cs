using Avalonia.Media.Imaging;
using Material.Icons;
using ReactiveUI;

namespace Left_4_Mod.Models;

/// <summary>文件显示区中的一行：文件夹或 VPK 模组。</summary>
public sealed class FileRow : ReactiveObject
{
    private Bitmap? _thumbnail;
    private bool _ownsThumbnail;

    private FileRow(
        bool isFolder,
        string name,
        string relativePath,
        int modCount,
        int childFolderCount,
        ModItem? mod,
        Bitmap? thumbnail)
    {
        IsFolder = isFolder;
        Name = name;
        RelativePath = relativePath;
        ModCount = modCount;
        ChildFolderCount = childFolderCount;
        Mod = mod;
        _thumbnail = thumbnail;
    }

    public static FileRow Folder(
        string name,
        string relativePath,
        int modCount,
        int childFolderCount,
        Bitmap? thumbnail) =>
        new(true, name, relativePath, modCount, childFolderCount, null, thumbnail);

    public static FileRow ForMod(ModItem mod, Bitmap? thumbnail) =>
        new(false, mod.DisplayName, mod.RelativePath, 0, 0, mod, thumbnail);

    public bool IsFolder { get; }

    public string Name { get; }

    public string RelativePath { get; }

    public int ModCount { get; }

    public int ChildFolderCount { get; }

    public ModItem? Mod { get; }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set => this.RaiseAndSetIfChanged(ref _thumbnail, value);
    }

    public bool ShowCheckBox => !IsFolder;

    public string DisplayName => IsFolder ? Name : Mod?.DisplayName ?? Name;

    public string TypeText => IsFolder ? "文件夹" : "VPK 模组";

    public string SizeText => IsFolder ? string.Empty : Mod?.SizeText ?? string.Empty;

    public string ModifiedText => IsFolder ? string.Empty : Mod?.ModifiedText ?? string.Empty;

    public string StatusText => IsFolder
        ? (ModCount > 0 ? $"{ModCount} 个模组" : "空文件夹")
        : Mod?.StatusText ?? string.Empty;

    public MaterialIconKind StatusIcon => IsFolder
        ? MaterialIconKind.Folder
        : Mod?.StatusIcon ?? MaterialIconKind.FileStar;

    public string ToolTip => RelativePath;

    public void SetThumbnail(Bitmap? thumbnail)
    {
        if (ReferenceEquals(thumbnail, _thumbnail))
        {
            return;
        }

        if (_ownsThumbnail)
        {
            _thumbnail?.Dispose();
        }

        _ownsThumbnail = true;
        Thumbnail = thumbnail;
    }

    public void DisposeThumbnail()
    {
        if (_ownsThumbnail)
        {
            _thumbnail?.Dispose();
            _ownsThumbnail = false;
        }
    }
}
