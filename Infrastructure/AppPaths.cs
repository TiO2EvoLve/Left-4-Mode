using System;
using System.IO;

namespace Left_4_Mod.Infrastructure;

/// <summary>
/// 应用数据目录。配置放在本地应用数据目录而不是模组库中，
/// 这样在用户尚未选择模组库时也能记住上次路径。
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "L4D2ModManager";

    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    public static string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");

    public static string DeploymentsFile => Path.Combine(AppDataDirectory, "deployments.json");

    public static string StateDirectory => Path.Combine(AppDataDirectory, "state");

    public static string LogsDirectory => Path.Combine(AppDataDirectory, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
