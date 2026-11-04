using Material.Icons;

namespace Left_4_Mod.Models;

/// <summary>左侧“快速访问”中的一个位置。</summary>
public sealed class QuickPlace
{
    public QuickPlace(string name, string path, MaterialIconKind iconKind)
    {
        Name = name;
        Path = path;
        IconKind = iconKind;
    }

    public string Name { get; }

    public string Path { get; }

    public MaterialIconKind IconKind { get; }
}
