using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

public sealed class GamePathService : IGamePathService
{
    private static readonly Regex PathRegex = new(
        "\"path\"\\s+\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogService _log;

    public GamePathService(ILogService log)
    {
        _log = log;
    }

    public GamePathInfo? TryResolve(string? path, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "尚未设置游戏目录。";
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            error = $"游戏路径无效：{ex.Message}";
            return null;
        }

        if (!Directory.Exists(fullPath))
        {
            error = $"游戏路径不存在：{fullPath}";
            return null;
        }

        var addons = FindAddonsDirectory(fullPath);
        if (addons is null)
        {
            error = "该目录下没有找到 left4dead2\\addons，请选择 L4D2 游戏根目录、left4dead2 或 addons 目录。";
            return null;
        }

        var left4Dead2 = Directory.GetParent(addons)?.FullName;
        var gameRoot = left4Dead2 is null ? null : Directory.GetParent(left4Dead2)?.FullName;

        if (left4Dead2 is null || gameRoot is null)
        {
            error = "无法从 addons 目录推导游戏根目录。";
            return null;
        }

        _log.Info($"游戏路径解析成功：{gameRoot}");
        return new GamePathInfo(gameRoot, left4Dead2, addons);
    }

    public GamePathInfo? AutoDetect(out string error)
    {
        error = string.Empty;
        var searched = new List<string>();

        foreach (var steamRoot in GetSteamRoots())
        {
            searched.Add(steamRoot);

            foreach (var libraryRoot in GetLibraryRoots(steamRoot))
            {
                var gameRoot = Path.Combine(libraryRoot, "steamapps", "common", "Left 4 Dead 2");
                if (!Directory.Exists(gameRoot))
                {
                    continue;
                }

                var info = TryResolve(gameRoot, out _);
                if (info is not null)
                {
                    _log.Info($"自动检测到 L4D2：{info.GameRoot}");
                    return info;
                }
            }
        }

        error = "未自动找到 Left 4 Dead 2，请手动选择游戏目录。";
        _log.Warn($"自动检测游戏路径失败，已搜索 {searched.Count} 个 Steam 位置。");
        return null;
    }

    public bool IsGameRunning()
    {
        try
        {
            return Process.GetProcessesByName("left4dead2").Length > 0;
        }
        catch (Exception ex)
        {
            _log.Warn($"检查游戏进程失败：{ex.Message}");
            return false;
        }
    }

    private static string? FindAddonsDirectory(string path)
    {
        // 直接选中 addons
        if (string.Equals(Path.GetFileName(path), "addons", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(path))
        {
            return path;
        }

        // 选中 left4dead2
        if (string.Equals(Path.GetFileName(path), "left4dead2", StringComparison.OrdinalIgnoreCase))
        {
            var addons = Path.Combine(path, "addons");
            return Directory.Exists(addons) ? addons : null;
        }

        // 选中游戏根目录
        var gameAddons = Path.Combine(path, "left4dead2", "addons");
        return Directory.Exists(gameAddons) ? gameAddons : null;
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };

        foreach (var drive in GetReadyDrives())
        {
            roots.Add(Path.Combine(drive, "Steam"));
            roots.Add(Path.Combine(drive, "SteamLibrary"));
            roots.Add(Path.Combine(drive, "Games", "Steam"));
            roots.Add(Path.Combine(drive, "Program Files (x86)", "Steam"));
        }

        return roots.Where(Directory.Exists);
    }

    private static IEnumerable<string> GetReadyDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            yield return drive.RootDirectory.FullName;
        }
    }

    private IEnumerable<string> GetLibraryRoots(string steamRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };

        var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFile))
        {
            return roots;
        }

        try
        {
            var text = File.ReadAllText(libraryFile);
            foreach (Match match in PathRegex.Matches(text))
            {
                var path = match.Groups[1].Value.Replace("\\\\", "\\");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    roots.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"读取 Steam libraryfolders.vdf 失败：{libraryFile}（{ex.Message}）");
        }

        return roots.Where(Directory.Exists);
    }
}
