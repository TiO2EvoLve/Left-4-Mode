namespace Left_4_Mod.Models;

/// <summary>地址栏中的一级目录，始终相对于模组库根目录。</summary>
public sealed class BreadcrumbItem(string name, string relativePath, bool isLast, bool isRoot)
{
    public string Name { get; } = name;

    public string RelativePath { get; } = relativePath;

    public bool IsLast { get; } = isLast;

    public bool IsRoot { get; } = isRoot;

    public bool ShowSeparator => !IsLast;
}
