namespace Left_4_Mod.Models;

/// <summary>地址栏中的一级路径。</summary>
public sealed class BreadcrumbItem
{
    public BreadcrumbItem(string name, string path, bool isLast)
    {
        Name = name;
        Path = path;
        IsLast = isLast;
    }

    public string Name { get; }

    public string Path { get; }

    public bool IsLast { get; }

    public bool ShowSeparator => !IsLast;
}
