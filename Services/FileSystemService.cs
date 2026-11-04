using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Left_4_Mod.Models;
using Material.Icons;

namespace Left_4_Mod.Services;

/// <summary>
/// 与界面无关的文件系统操作：读取目录、复制/移动/删除、重名处理等。
/// </summary>
public static class FileSystemService
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Dictionary<string, string> KnownTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "文本文档",
        [".md"] = "Markdown 文档",
        [".rtf"] = "RTF 文档",
        [".pdf"] = "PDF 文档",
        [".doc"] = "Word 文档",
        [".docx"] = "Word 文档",
        [".xls"] = "Excel 工作表",
        [".xlsx"] = "Excel 工作表",
        [".ppt"] = "PowerPoint 演示文稿",
        [".pptx"] = "PowerPoint 演示文稿",
        [".png"] = "PNG 图片",
        [".jpg"] = "JPEG 图片",
        [".jpeg"] = "JPEG 图片",
        [".gif"] = "GIF 图片",
        [".bmp"] = "位图图像",
        [".webp"] = "WebP 图片",
        [".ico"] = "图标文件",
        [".svg"] = "SVG 图片",
        [".mp4"] = "MP4 视频",
        [".avi"] = "AVI 视频",
        [".mkv"] = "MKV 视频",
        [".mov"] = "QuickTime 视频",
        [".wmv"] = "WMV 视频",
        [".mp3"] = "MP3 音频",
        [".wav"] = "WAV 音频",
        [".ogg"] = "OGG 音频",
        [".flac"] = "FLAC 音频",
        [".m4a"] = "M4A 音频",
        [".zip"] = "ZIP 压缩文件",
        [".rar"] = "RAR 压缩文件",
        [".7z"] = "7-Zip 压缩文件",
        [".tar"] = "TAR 压缩文件",
        [".gz"] = "GZip 压缩文件",
        [".exe"] = "应用程序",
        [".dll"] = "应用程序扩展",
        [".msi"] = "Windows 安装包",
        [".cs"] = "C# 源代码",
        [".xaml"] = "XAML 文件",
        [".json"] = "JSON 文件",
        [".xml"] = "XML 文件",
        [".js"] = "JavaScript 文件",
        [".ts"] = "TypeScript 文件",
        [".html"] = "HTML 文档",
        [".css"] = "样式表",
        [".py"] = "Python 文件",
        [".lua"] = "Lua 脚本",
        [".cfg"] = "配置文件",
        [".ini"] = "配置设置",
        [".log"] = "日志文件",
        [".vpk"] = "VPK 模组包",
        [".vpks"] = "VPK 模组包",
        [".vmf"] = "VMF 地图源文件",
        [".bsp"] = "BSP 地图",
    };

    /// <summary>读取目录下一级内容。</summary>
    public static List<FileSystemEntry> ReadEntries(string directoryPath)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        var entries = new List<FileSystemEntry>();

        foreach (var info in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos("*", options))
        {
            try
            {
                var isFolder = info is DirectoryInfo;
                var size = 0L;
                var modified = info.LastWriteTime;

                if (info is FileInfo fileInfo)
                {
                    size = fileInfo.Length;
                }

                var isHidden = (info.Attributes & FileAttributes.Hidden) != 0;
                entries.Add(new FileSystemEntry(info.FullName, isFolder, size, modified, isHidden));
            }
            catch (Exception)
            {
                // 单个项目读取失败（权限、链接损坏等）时跳过，不影响整个目录。
            }
        }

        return entries;
    }

    public static string GetTypeDisplayName(string path, bool isFolder)
    {
        if (isFolder)
        {
            return "文件夹";
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return "文件";
        }

        if (KnownTypeNames.TryGetValue(extension, out var name))
        {
            return name;
        }

        return $"{extension.TrimStart('.').ToUpperInvariant()} 文件";
    }

    public static MaterialIconKind GetIconKind(string path, bool isFolder)
    {
        if (isFolder)
        {
            return MaterialIconKind.Folder;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico" or ".svg"
                => MaterialIconKind.FileImage,
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm"
                => MaterialIconKind.FileVideo,
            ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a"
                => MaterialIconKind.FileMusic,
            ".pdf" => MaterialIconKind.FilePdfBox,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz"
                => MaterialIconKind.FolderZip,
            ".cs" or ".xaml" or ".json" or ".xml" or ".js" or ".ts" or ".py" or ".lua"
                or ".html" or ".css" or ".cfg" or ".ini" or ".log"
                => MaterialIconKind.FileCode,
            ".exe" or ".msi" or ".dll" or ".bat" or ".cmd"
                => MaterialIconKind.Application,
            ".vpk" or ".vpks" or ".vmf" or ".bsp"
                => MaterialIconKind.FileStar,
            _ => MaterialIconKind.FileDocument,
        };
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return string.Empty;
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unitIndex = -1;

        do
        {
            value /= 1024d;
            unitIndex++;
        }
        while (value >= 1024d && unitIndex < units.Length - 1);

        return $"{value:0.#} {units[unitIndex]}";
    }

    public static bool IsValidEntryName(string name, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "名称不能为空。";
            return false;
        }

        if (name.IndexOfAny(InvalidNameChars) >= 0)
        {
            error = "名称中不能包含下列字符：\\ / : * ? \" < > |";
            return false;
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            error = "名称不能以空格或句点结尾。";
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem))
        {
            error = $"“{stem}”是系统保留名称，请换一个名称。";
            return false;
        }

        return true;
    }

    /// <summary>判断 <paramref name="path"/> 是否就是 <paramref name="ancestor"/> 或位于其内部。</summary>
    public static bool IsSameOrDescendant(string path, string ancestor)
    {
        var normalizedAncestor = NormalizeDirectory(ancestor);
        var normalizedPath = NormalizeDirectory(path);
        return normalizedPath.StartsWith(normalizedAncestor, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               + Path.DirectorySeparatorChar;
    }

    public static bool PathsEqual(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>生成不冲突的路径，例如“新建文件夹 (2)”。</summary>
    public static string GetUniquePath(string directory, string desiredName)
    {
        var candidate = Path.Combine(directory, desiredName);
        if (!EntryExists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(desiredName);
        var extension = Path.GetExtension(desiredName);

        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!EntryExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为“{desiredName}”生成可用的名称。");
    }

    /// <summary>生成复制冲突时使用的路径，例如“文档 - 副本”。</summary>
    public static string GetCopyPath(string directory, string sourceName)
    {
        var candidate = Path.Combine(directory, sourceName);
        if (!EntryExists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(sourceName);
        var extension = Path.GetExtension(sourceName);
        candidate = Path.Combine(directory, $"{stem} - 副本{extension}");

        if (!EntryExists(candidate))
        {
            return candidate;
        }

        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} - 副本 ({index}){extension}");
            if (!EntryExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为“{sourceName}”生成可用的副本名称。");
    }

    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);

    public static void Copy(string sourcePath, string destinationPath)
    {
        if (Directory.Exists(sourcePath))
        {
            CopyDirectory(sourcePath, destinationPath);
            return;
        }

        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 不跟随符号链接/目录联接，避免循环复制。
                continue;
            }

            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        }
    }

    public static void Move(string sourcePath, string destinationPath)
    {
        try
        {
            if (Directory.Exists(sourcePath))
            {
                Directory.Move(sourcePath, destinationPath);
            }
            else
            {
                File.Move(sourcePath, destinationPath);
            }
        }
        catch (IOException)
        {
            // 跨分区移动时系统会抛 IOException，退化为“复制 + 删除”。
            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, destinationPath);
                ClearReadOnlyRecursive(sourcePath);
                Directory.Delete(sourcePath, recursive: true);
            }
            else
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
                ClearReadOnly(sourcePath);
                File.Delete(sourcePath);
            }
        }
    }

    /// <summary>删除到回收站；Shell 操作失败时抛出异常。</summary>
    public static async Task DeleteToRecycleBinAsync(IReadOnlyList<string> paths)
    {
        var result = await ShellFileOperations.MoveToRecycleBinAsync(paths).ConfigureAwait(false);
        if (result != 0)
        {
            throw new IOException($"无法移入回收站（Shell 错误码 0x{result:X}）。");
        }
    }

    /// <summary>永久删除（用于回收站不可用时的兜底）。</summary>
    public static void DeletePermanently(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                ClearReadOnlyRecursive(path);
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                ClearReadOnly(path);
                File.Delete(path);
            }
        }
    }

    private static void ClearReadOnly(string filePath)
    {
        var attributes = File.GetAttributes(filePath);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void ClearReadOnlyRecursive(string directoryPath)
    {
        foreach (var file in Directory.EnumerateFiles(directoryPath))
        {
            ClearReadOnly(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(directoryPath))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            ClearReadOnlyRecursive(directory);
        }
    }

    /// <summary>从完整路径生成面包屑列表。</summary>
    public static List<BreadcrumbItem> BuildBreadcrumbs(string path)
    {
        var breadcrumbs = new List<BreadcrumbItem>();
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? fullPath;

        breadcrumbs.Add(new BreadcrumbItem(root, root, isLast: false));

        var remainder = fullPath[root.Length..];
        var segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            breadcrumbs.Add(new BreadcrumbItem(segment, current, isLast: false));
        }

        var last = breadcrumbs[^1];
        breadcrumbs[^1] = new BreadcrumbItem(last.Name, last.Path, isLast: true);
        return breadcrumbs;
    }

    /// <summary>快捷访问：桌面、下载、文档、图片以及所有就绪的磁盘。</summary>
    public static List<QuickPlace> BuildQuickPlaces()
    {
        var places = new List<QuickPlace>();

        AddSpecialFolder(places, Environment.SpecialFolder.DesktopDirectory, "桌面", MaterialIconKind.DesktopClassic);
        AddPathPlace(
            places,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "下载",
            MaterialIconKind.Download);
        AddSpecialFolder(places, Environment.SpecialFolder.MyDocuments, "文档", MaterialIconKind.FileDocument);
        AddSpecialFolder(places, Environment.SpecialFolder.MyPictures, "图片", MaterialIconKind.FileImage);

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name.TrimEnd(Path.DirectorySeparatorChar)
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd(Path.DirectorySeparatorChar)})";

                places.Add(new QuickPlace(label, drive.RootDirectory.FullName, MaterialIconKind.Harddisk));
            }
        }
        catch (Exception)
        {
            // 无法枚举磁盘时忽略。
        }

        return places;
    }

    private static void AddSpecialFolder(
        List<QuickPlace> places,
        Environment.SpecialFolder folder,
        string name,
        MaterialIconKind icon)
    {
        AddPathPlace(places, Environment.GetFolderPath(folder), name, icon);
    }

    private static void AddPathPlace(List<QuickPlace> places, string path, string name, MaterialIconKind icon)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            places.Add(new QuickPlace(name, path, icon));
        }
    }
}
