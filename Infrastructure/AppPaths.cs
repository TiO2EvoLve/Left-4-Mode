using System;
using System.IO;

namespace Left_4_Mod.Infrastructure;

/// <summary>
/// 应用数据目录。所有持久化内容都放在项目目录（开发时）或程序目录（发布后），
/// 不写入系统盘的用户配置目录。
/// </summary>
public static class AppPaths
{
    public const string DataFolderName = "L4D2ModManagerData";
    public const string LegacyFolderName = "L4D2ModManager";

    public static string AppDataDirectory { get; } = ResolveDataDirectory();

    public static string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");

    public static string DeploymentsFile => Path.Combine(AppDataDirectory, "deployments.json");

    public static string StateDirectory => Path.Combine(AppDataDirectory, "state");

    public static string LogsDirectory => Path.Combine(AppDataDirectory, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(LogsDirectory);
        MigrateLegacyData();
    }

    private static string ResolveDataDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        // 开发/IDE 运行时从 bin 向上找到项目文件，避免数据写进 bin。
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Left 4 Mod.csproj")))
            {
                return Path.Combine(directory.FullName, DataFolderName);
            }

            directory = directory.Parent;
        }

        // 发布后没有 csproj，数据放在程序目录旁。
        return Path.Combine(baseDirectory, DataFolderName);
    }

    /// <summary>
    /// 一次性迁移之前版本位于 %LOCALAPPDATA% 的设置/状态/清单。
    /// 只读取旧目录；新目录已有设置时不覆盖。
    /// </summary>
    private static void MigrateLegacyData()
    {
        try
        {
            var legacyDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LegacyFolderName);

            if (!Directory.Exists(legacyDirectory) ||
                string.Equals(legacyDirectory, AppDataDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!File.Exists(SettingsFile))
            {
                CopyFileIfExists(
                    Path.Combine(legacyDirectory, "settings.json"),
                    SettingsFile);
            }

            if (!File.Exists(DeploymentsFile))
            {
                CopyFileIfExists(
                    Path.Combine(legacyDirectory, "deployments.json"),
                    DeploymentsFile);
            }

            var legacyState = Path.Combine(legacyDirectory, "state");
            if (Directory.Exists(legacyState))
            {
                Directory.CreateDirectory(StateDirectory);
                foreach (var stateFile in Directory.EnumerateFiles(legacyState, "*.json"))
                {
                    CopyFileIfExists(
                        stateFile,
                        Path.Combine(StateDirectory, Path.GetFileName(stateFile)));
                }
            }

            // 迁移成功后移除旧目录，保证应用数据只保留在项目目录。
            if (File.Exists(SettingsFile))
            {
                try
                {
                    Directory.Delete(legacyDirectory, recursive: true);
                }
                catch (Exception)
                {
                    // 旧目录被占用时保留，应用仍只写入新目录。
                }
            }
        }
        catch (Exception)
        {
            // 迁移失败时保留旧数据；应用仍可在新目录中重新保存设置。
        }
    }

    private static void CopyFileIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        if (File.Exists(destination))
        {
            return;
        }

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(source, destination, overwrite: false);
    }
}
