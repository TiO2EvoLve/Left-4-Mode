namespace Left_4_Mod.Models;

/// <summary>移动模组时可选的库内文件夹（空字符串表示模组库根目录）。</summary>
public sealed record FolderChoice(string RelativePath, string DisplayName)
{
    public bool IsRoot => RelativePath.Length == 0;
}

public sealed record FileOperationResult(bool Success, string Message)
{
    public static FileOperationResult Ok(string message) => new(true, message);

    public static FileOperationResult Fail(string message) => new(false, message);
}
