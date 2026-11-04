namespace Left_4_Mod.Models;

/// <summary>
/// 持久化设置。包含版本字段，后续字段变更时可以迁移。
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>上次使用的模组库根目录。</summary>
    public string? LibraryPath { get; set; }

    /// <summary>上次浏览的目录（相对于模组库根目录）。</summary>
    public string? LastFolder { get; set; }

    /// <summary>预留：L4D2 游戏目录。</summary>
    public string? GamePath { get; set; }

    /// <summary>预留：部署策略。</summary>
    public string? DeploymentStrategy { get; set; }
}
