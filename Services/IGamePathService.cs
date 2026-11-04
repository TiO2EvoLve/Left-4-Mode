using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

public interface IGamePathService
{
    /// <summary>把游戏根目录、left4dead2 或 addons 目录统一解析成 addons 路径。</summary>
    GamePathInfo? TryResolve(string? path, out string error);

    /// <summary>从常见 Steam 安装位置和 libraryfolders.vdf 自动检测。</summary>
    GamePathInfo? AutoDetect(out string error);

    bool IsGameRunning();
}
