using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Left_4_Mod.Models;
using ValveKeyValue;
using ValvePak;

namespace Left_4_Mod.Services;

/// <summary>
/// 从 VPK 内部或创意工坊 API 获取模组缩略图，并缓存到模组库的 photo 目录。
/// 缓存优先，保证后续打开文件夹时不再重复解包/下载。
/// </summary>
public sealed class ModThumbnailService : IModThumbnailService
{
    private const string PhotoFolderName = "photo";
    private const long MaxImageBytes = 20 * 1024 * 1024;

    private static readonly string[] ImageExtensions =
    [
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".bmp",
        ".gif",
    ];

    private static readonly string[] AddonInfoImageKeys =
    [
        "addonimage",
        "addonimage_small",
        "addonimage_large",
        "addonimage_thumb",
        "image",
        "thumbnail",
        "thumbnailimage",
        "preview",
        "previewimage",
        "icon",
        "banner",
        "header",
    ];

    private readonly IModLibraryService _library;
    private readonly IWorkshopService _workshop;
    private readonly HttpClient _httpClient;
    private readonly ILogService _log;
    private readonly Lazy<Bitmap?> _folderImage;
    private readonly Lazy<Bitmap?> _placeholderImage;
    private readonly SemaphoreSlim _loadGate = new(4, 4);

    public ModThumbnailService(
        IModLibraryService library,
        IWorkshopService workshop,
        HttpClient httpClient,
        ILogService log)
    {
        _library = library;
        _workshop = workshop;
        _httpClient = httpClient;
        _log = log;
        _folderImage = new Lazy<Bitmap?>(() => LoadAsset("File.png"));
        _placeholderImage = new Lazy<Bitmap?>(() => LoadAsset("VPK.png"));
    }

    public Bitmap? FolderImage => _folderImage.Value;

    public Bitmap? PlaceholderImage => _placeholderImage.Value;

    public async Task<Bitmap?> LoadAsync(
        ModRecord record,
        string vpkFullPath,
        CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(record, vpkFullPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<Bitmap?> LoadCoreAsync(
        ModRecord record,
        string vpkFullPath,
        CancellationToken cancellationToken)
    {
        var cached = FindCachedPhoto(record);
        if (cached is not null)
        {
            var cachedBitmap = TryLoadBitmap(cached);
            if (cachedBitmap is not null)
            {
                return cachedBitmap;
            }

            TryDeleteFile(cached);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var fromVpk = TryExtractFromVpk(vpkFullPath);
        if (fromVpk is not null)
        {
            var saved = SaveToPhotoFolder(record, fromVpk.Value.Bytes, fromVpk.Value.Extension);
            var bitmap = saved is null ? TryLoadBitmap(fromVpk.Value.Bytes) : TryLoadBitmap(saved);
            if (bitmap is not null)
            {
                return bitmap;
            }

            if (saved is not null)
            {
                TryDeleteFile(saved);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var previewUrl = record.WorkshopPreviewUrl;
        if (string.IsNullOrWhiteSpace(previewUrl) &&
            record.WorkshopId is { } workshopId &&
            workshopId != 0)
        {
            try
            {
                var details = await _workshop
                    .GetDetailsAsync(workshopId, cancellationToken)
                    .ConfigureAwait(false);
                previewUrl = details.PreviewUrl;
                if (!string.IsNullOrWhiteSpace(previewUrl))
                {
                    record.WorkshopPreviewUrl = previewUrl;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"获取创意工坊预览图地址失败：{workshopId}（{ex.Message}）");
            }
        }

        if (!string.IsNullOrWhiteSpace(previewUrl))
        {
            var downloaded = await TryDownloadPreviewAsync(record, previewUrl, cancellationToken)
                .ConfigureAwait(false);
            if (downloaded is not null)
            {
                return downloaded;
            }
        }

        return null;
    }

    private async Task<Bitmap?> TryDownloadPreviewAsync(
        ModRecord record,
        string previewUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient
                .GetAsync(previewUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"创意工坊预览图下载失败：HTTP {(int)response.StatusCode}");
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxImageBytes)
            {
                _log.Warn("创意工坊预览图过大，已跳过。");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            {
                return null;
            }

            var extension = GetExtensionFromUrl(previewUrl);
            var saved = SaveToPhotoFolder(record, bytes, extension);
            var bitmap = saved is null ? TryLoadBitmap(bytes) : TryLoadBitmap(saved);
            if (bitmap is null && saved is not null)
            {
                TryDeleteFile(saved);
            }

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"下载创意工坊预览图失败：{ex.Message}");
            return null;
        }
    }

    private (byte[] Bytes, string Extension)? TryExtractFromVpk(string vpkPath)
    {
        if (string.IsNullOrWhiteSpace(vpkPath) || !File.Exists(vpkPath))
        {
            return null;
        }

        try
        {
            using var package = Package.Open(vpkPath, StringComparison.OrdinalIgnoreCase);
            if (package is null)
            {
                return null;
            }

            if (package.Entries is not { } entryMap)
            {
                return null;
            }

            var entries = entryMap.Values
                .SelectMany(list => list)
                .ToArray();

            var configuredEntry = FindEntryFromAddonInfo(package, entries);
            var entry = configuredEntry is not null && IsImageEntry(configuredEntry)
                ? configuredEntry
                : FindImageEntry(entries);
            if (entry is null ||
                entry.TotalLength == 0 ||
                entry.TotalLength > MaxImageBytes)
            {
                return null;
            }

            package.ReadEntry(entry, out var bytes, validateCrc: false);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            {
                return null;
            }

            return (bytes, NormalizeExtension(entry.TypeName));
        }
        catch (Exception ex)
        {
            _log.Warn($"读取 VPK 缩略图失败：{vpkPath}（{ex.Message}）");
            return null;
        }
    }

    private PackageEntry? FindEntryFromAddonInfo(Package package, IReadOnlyList<PackageEntry> entries)
    {
        var infoEntry = entries.FirstOrDefault(entry =>
            entry.FileName.Equals("addoninfo", StringComparison.OrdinalIgnoreCase) &&
            entry.TypeName.Equals("txt", StringComparison.OrdinalIgnoreCase));

        if (infoEntry is null)
        {
            return null;
        }

        try
        {
            using var stream = package.OpenEntryStream(infoEntry);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;

            var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
            var document = serializer.Deserialize(memory);
            var imagePath = FindValue(document.Root, AddonInfoImageKeys);
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            var candidate = imagePath
                .Trim()
                .Trim('"', '\'')
                .TrimStart('/', '\\')
                .Replace('/', '\\');
            var direct = TryFindEntry(package, candidate, entries);
            if (direct is not null)
            {
                return direct;
            }

            if (Path.GetExtension(candidate).Length == 0)
            {
                foreach (var extension in ImageExtensions)
                {
                    direct = TryFindEntry(package, candidate + extension, entries);
                    if (direct is not null)
                    {
                        return direct;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"解析 addoninfo.txt 失败：{ex.Message}");
        }

        return null;
    }

    private static PackageEntry? TryFindEntry(
        Package package,
        string path,
        IReadOnlyList<PackageEntry> entries)
    {
        try
        {
            var entry = package.FindEntry(path);
            if (entry is not null)
            {
                return entry;
            }
        }
        catch (Exception)
        {
            // 继续用完整路径扫描做兼容。
        }

        var normalized = path.Replace('\\', '/').Trim('/');
        return entries.FirstOrDefault(entry =>
        {
            var fullPath = $"{entry.DirectoryName}/{entry.FileName}.{entry.TypeName}"
                .Replace('\\', '/')
                .Trim()
                .Trim('/');
            return string.Equals(fullPath, normalized, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static PackageEntry? FindImageEntry(IReadOnlyList<PackageEntry> entries)
    {
        var imageEntries = entries
            .Where(IsImageEntry)
            .ToArray();

        return imageEntries.FirstOrDefault(entry =>
                   entry.DirectoryName.Length == 0 &&
                   entry.FileName.Equals("addonimage", StringComparison.OrdinalIgnoreCase))
               ?? imageEntries.FirstOrDefault(entry =>
                   entry.FileName.Equals("addonimage", StringComparison.OrdinalIgnoreCase))
               ?? imageEntries.FirstOrDefault(entry =>
                   entry.FileName.StartsWith("addonimage", StringComparison.OrdinalIgnoreCase))
               ?? imageEntries.FirstOrDefault(entry =>
                   entry.FileName.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) ||
                   entry.FileName.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
                   entry.FileName.Contains("thumb", StringComparison.OrdinalIgnoreCase))
               ?? imageEntries.FirstOrDefault(entry =>
                   string.IsNullOrWhiteSpace(entry.DirectoryName));
    }

    private static bool IsImageEntry(PackageEntry entry) =>
        ImageExtensions.Contains(
            NormalizeExtension(entry.TypeName),
            StringComparer.OrdinalIgnoreCase);

    private static string? FindValue(KVObject node, IReadOnlyList<string> keys)
    {
        foreach (var child in node.Children)
        {
            if (keys.Any(key => string.Equals(key, child.Key, StringComparison.OrdinalIgnoreCase)))
            {
                var value = child.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (child.Value.IsCollection)
            {
                var nested = FindValue(child.Value, keys);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private string? FindCachedPhoto(ModRecord record)
    {
        var directory = GetPhotoDirectory();
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            foreach (var stem in GetCacheStems(record))
            {
                foreach (var extension in ImageExtensions)
                {
                    var path = Path.Combine(directory, stem + extension);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }

                var fallback = Directory
                    .EnumerateFiles(directory, stem + ".*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (fallback is not null)
                {
                    return fallback;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"读取缩略图缓存失败：{ex.Message}");
        }

        return null;
    }

    private string? SaveToPhotoFolder(ModRecord record, byte[] bytes, string extension)
    {
        var directory = GetPhotoDirectory();
        if (directory is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, GetStableFileStem(record) + NormalizeExtension(extension));
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            _log.Warn($"保存缩略图失败：{ex.Message}");
            return null;
        }
    }

    private string? GetPhotoDirectory()
    {
        if (!_library.IsConfigured || _library.RootPath is null)
        {
            return null;
        }

        return Path.Combine(_library.RootPath, PhotoFolderName);
    }

    /// <summary>
    /// 优先读取旧版以记录 Id 命名的缓存，再使用与状态文件无关的稳定键。
    /// 这样即使状态文件丢失、记录 Id 重新生成，也能复用同一张缩略图。
    /// </summary>
    private static IEnumerable<string> GetCacheStems(ModRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Id))
        {
            yield return GetSafeFileStem(record.Id);
        }

        if (record.WorkshopId is { } workshopId && workshopId != 0)
        {
            yield return "workshop_" + workshopId;
        }

        var relativePath = NormalizeCachePath(record.RelativePath);
        if (relativePath.Length > 0)
        {
            yield return "path_" + HashForFileName(relativePath);
        }
    }

    private static string GetStableFileStem(ModRecord record)
    {
        if (record.WorkshopId is { } workshopId && workshopId != 0)
        {
            return "workshop_" + workshopId;
        }

        var relativePath = NormalizeCachePath(record.RelativePath);
        if (relativePath.Length > 0)
        {
            return "path_" + HashForFileName(relativePath);
        }

        return GetSafeFileStem(record.Id);
    }

    private static string NormalizeCachePath(string? relativePath) =>
        (relativePath ?? string.Empty)
        .Replace('\\', '/')
        .Trim('/')
        .ToLowerInvariant();

    private static string HashForFileName(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static string GetSafeFileStem(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "mod" : value;
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(source
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(stem) ? "mod" : stem;
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".jpg";
        }

        var normalized = extension.StartsWith('.')
            ? extension
            : "." + extension;

        normalized = normalized.ToLowerInvariant();
        return ImageExtensions.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : ".jpg";
    }

    private static string GetExtensionFromUrl(string url)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            return ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                ? extension
                : ".jpg";
        }
        catch (Exception)
        {
            return ".jpg";
        }
    }

    private static Bitmap? TryLoadBitmap(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Bitmap? TryLoadBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Bitmap? LoadAsset(string fileName)
    {
        try
        {
            var assemblyName = typeof(ModThumbnailService).Assembly.GetName().Name;
            var uri = new Uri($"avares://{assemblyName}/Assets/{fileName}");
            using var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warn($"清理缩略图失败：{path}（{ex.Message}）");
        }
    }
}
