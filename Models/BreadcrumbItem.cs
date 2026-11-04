namespace Left_4_Mod.Models;

/// <summary>地址栏中的一级目录，始终相对于模组库根目录。</summary>
public sealed class BreadcrumbItem
{
    public BreadcrumbItem(string name, string relativePath, bool isLast, bool isRoot)
    {
        Name = name;
        RelativePath = relativePath;
        IsLast = isLast;
        IsRoot = isRoot;
    }

    public string Name { get; }

    public string RelativePath { get; }

    public bool IsLast { get; }

    public bool IsRoot { get; }

    public bool ShowSeparator => !IsLast;
}
