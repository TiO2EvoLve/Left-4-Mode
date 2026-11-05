using System;
using System.Collections.Generic;
using System.IO;

namespace Left_4_Mod.Infrastructure;

/// <summary>
/// 应用数据目录。所有持久化内容直接放在项目目录（开发时）或程序目录（发布后），
/// 不依赖子目录，也不写入系统盘的用户配置目录。
/// </summary>
public static class AppPaths
{
    public const string LegacyFolderName = "L4D2ModManager";
    public const string LegacyDataFolderName = "L4D2ModManagerData";

    public static string ProjectRoot { get; } = ResolveProjectRoot();

    public static string AppDataDirectory => ProjectRoot;

    public static string SettingsFile => Path.Combine(ProjectRoot, "settings.json");

    public static string DeploymentsFile => Path.Combine(ProjectRoot, "deployments.json");

    public static string StateDirectory => Path.Combine(ProjectRoot, "state");

    public static string LogsDirectory => Path.Combine(ProjectRoot, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(LogsDirectory);
        MigrateLegacyData();
    }

    private static string ResolveProjectRoot()
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
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        // 发布后没有 csproj，数据放在程序目录旁。
        return baseDirectory;
    }

    /// <summary>
    /// 一次性迁移旧版数据：
    /// 1. 上一版项目内的 L4D2ModManagerData；
    /// 2. 更早版本的 %LOCALAPPDATA%\L4D2ModManager。
    /// 只在新文件不存在时复制，不覆盖当前设置。
    /// </summary>
    private static void MigrateLegacyData()
    {
        var legacyDirectories = new List<string>
        {
            Path.Combine(ProjectRoot, LegacyDataFolderName),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LegacyFolderName),
        };

        foreach (var legacyDirectory in legacyDirectories)
        {
            if (!Directory.Exists(legacyDirectory) ||
                string.Equals(legacyDirectory, ProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                CopyFileIfExists(
                    Path.Combine(legacyDirectory, "settings.json"),
                    SettingsFile);
                CopyFileIfExists(
                    Path.Combine(legacyDirectory, "deployments.json"),
                    DeploymentsFile);

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
            }
            catch (Exception)
            {
                // 迁移失败时保留旧数据，应用仍可在新位置重新保存设置。
            }
        }

        // 旧 LocalAppData 目录在设置已成功迁移后删除，避免继续占用 C 盘。
        if (File.Exists(SettingsFile))
        {
            try
            {
                var legacyLocalAppData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    LegacyFolderName);
                if (Directory.Exists(legacyLocalAppData) &&
                    !string.Equals(legacyLocalAppData, ProjectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(legacyLocalAppData, recursive: true);
                }
            }
            catch (Exception)
            {
                // 旧目录被占用时保留。
            }
        }
    }

    private static void CopyFileIfExists(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination))
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
