namespace Left_4_Mod.Models;

public sealed class DeleteInfo
{
    public DeleteInfo(int fileCount, int folderCount, long totalBytes)
    {
        FileCount = fileCount;
        FolderCount = folderCount;
        TotalBytes = totalBytes;
    }

    public int FileCount { get; }

    public int FolderCount { get; }

    public long TotalBytes { get; }

    public bool ContainsChildren => FileCount > 0 || FolderCount > 0;

    public string Summary =>
        ContainsChildren
            ? $"其中 {FileCount} 个文件、{FolderCount} 个文件夹"
            : "未包含子项";
}
