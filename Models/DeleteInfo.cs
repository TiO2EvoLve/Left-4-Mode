namespace Left_4_Mod.Models;

public sealed class DeleteInfo(int fileCount, int folderCount, long totalBytes)
{
    public int FileCount { get; } = fileCount;

    public int FolderCount { get; } = folderCount;

    public long TotalBytes { get; } = totalBytes;

    public bool ContainsChildren => FileCount > 0 || FolderCount > 0;

    public string Summary =>
        ContainsChildren
            ? $"其中 {FileCount} 个文件、{FolderCount} 个文件夹"
            : "未包含子项";
}
