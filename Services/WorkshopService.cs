using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

public sealed class WorkshopException : Exception
{
    public WorkshopException(string message)
        : base(message)
    {
    }

    public WorkshopException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Steam 创意工坊：通过 GetPublishedFileDetails 获取下载地址和时间戳，
/// 通过 Downloader 包下载/更新 VPK。存放路径固定为 Workshop\&lt;id&gt;.vpk，
/// 自动更新时原地替换，已有的符号链接部署会自动指向新内容。
/// </summary>
public sealed class WorkshopService : IWorkshopService
{
    private const string DetailsApi =
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    private const int L4D2AppId = 550;

    private readonly HttpClient _httpClient;
    private readonly IWorkshopDownloader _downloader;
    private readonly IModLibraryService _library;
    private readonly IModStateStore _stateStore;
    private readonly ILogService _log;

    public WorkshopService(
        HttpClient httpClient,
        IWorkshopDownloader downloader,
        IModLibraryService library,
        IModStateStore stateStore,
        ILogService log)
    {
        _httpClient = httpClient;
        _downloader = downloader;
        _library = library;
        _stateStore = stateStore;
        _log = log;
    }

    public async Task<WorkshopItemInfo> GetDetailsAsync(
        ulong publishedFileId,
        CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["itemcount"] = "1",
            ["publishedfileids[0]"] = publishedFileId.ToString(),
        });

        using var response = await _httpClient
            .PostAsync(DetailsApi, content, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new WorkshopException($"Steam 创意工坊接口返回 HTTP {(int)response.StatusCode}。");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("response", out var responseElement) ||
            !responseElement.TryGetProperty("publishedfiledetails", out var detailsArray) ||
            detailsArray.ValueKind != JsonValueKind.Array ||
            detailsArray.GetArrayLength() == 0)
        {
            throw new WorkshopException("创意工坊没有返回项目信息。");
        }

        var details = detailsArray[0];

        var result = GetInt32(details, "result");
        if (result != 1)
        {
            throw new WorkshopException(result == 9
                ? "创意工坊项目不存在或不可访问。"
                : $"创意工坊项目不可用（结果码 {result}）。");
        }

        var appId = GetInt32(details, "consumer_app_id");
        if (appId != L4D2AppId)
        {
            throw new WorkshopException(
                $"该项目不是《求生之路 2》创意工坊项目（App ID：{appId}）。");
        }

        var fileUrl = GetString(details, "file_url");
        if (string.IsNullOrWhiteSpace(fileUrl))
        {
            throw new WorkshopException("该项目没有公开下载地址（可能是合集、隐藏项目或需要订阅）。");
        }

        var tags = new List<string>();
        if (details.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var tagElement in tagsElement.EnumerateArray())
            {
                if (tagElement.TryGetProperty("tag", out var tagValue) &&
                    tagValue.ValueKind == JsonValueKind.String)
                {
                    var tag = tagValue.GetString();
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        tags.Add(tag);
                    }
                }
            }
        }

        return new WorkshopItemInfo(
            publishedFileId,
            GetString(details, "title") is { Length: > 0 } title
                ? title
                : publishedFileId.ToString(),
            GetString(details, "description"),
            fileUrl,
            GetString(details, "preview_url"),
            GetUInt64(details, "time_created"),
            GetUInt64(details, "time_updated"),
            GetInt64(details, "file_size"),
            tags);
    }

    public async Task<WorkshopAddResult> AddAsync(
        ulong publishedFileId,
        IProgress<WorkshopProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!_library.IsConfigured || _library.RootPath is null)
        {
            return WorkshopAddResult.Fail("尚未选择模组库。");
        }

        string? temporaryPath = null;
        try
        {
            var details = await GetDetailsAsync(publishedFileId, cancellationToken).ConfigureAwait(false);

            var relativePath = GetWorkshopRelativePath(publishedFileId);
            var fullPath = _library.GetFullPath(relativePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = await _stateStore.LoadAsync(_library.RootPath, cancellationToken).ConfigureAwait(false);
            var record = state.Mods.FirstOrDefault(item =>
                             item.WorkshopId == publishedFileId ||
                             string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                         ?? state.Mods.FirstOrDefault(item =>
                             string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

            if (record is not null && !record.IsWorkshop)
            {
                return WorkshopAddResult.Fail(
                    $"目标文件已被本地模组占用：{relativePath}。请先处理该文件。");
            }

            if (record is null && File.Exists(fullPath))
            {
                return WorkshopAddResult.Fail(
                    $"目标文件已存在且未登记为创意工坊模组：{relativePath}。");
            }

            temporaryPath = fullPath + ".downloading";
            TryDeleteFile(temporaryPath);

            var downloadProgress = new Progress<WorkshopDownloadProgress>(update =>
            {
                progress?.Report(new WorkshopProgress(
                    WorkshopProgressStage.Downloading,
                    0,
                    1,
                    update.Percentage,
                    $"正在下载 {details.Title}（{update.Percentage:0.#}%）"));
            });

            await _downloader
                .DownloadAsync(details.FileUrl, temporaryPath, downloadProgress, cancellationToken)
                .ConfigureAwait(false);

            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = null;

            var created = record is null;
            if (record is null)
            {
                record = new ModRecord();
                state.Mods.Add(record);
            }

            ApplyDetailsToRecord(record, details, relativePath, fullPath);
            if (created)
            {
                record.IsEnabled = false;
            }

            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);

            _log.Info($"已添加工坊模组 {publishedFileId}：{relativePath}");
            return new WorkshopAddResult(true, null, details, relativePath, !created);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"添加工坊模组失败：{publishedFileId}");
            return WorkshopAddResult.Fail(ex.Message);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteFile(temporaryPath);
            }
        }
    }

    public async Task<WorkshopUpdateResult> CheckForUpdatesAsync(
        ModCatalog catalog,
        IProgress<WorkshopProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var records = catalog.State.Mods
            .Where(record => record.WorkshopId.HasValue && record.WorkshopAutoUpdate)
            .ToArray();

        var updated = new List<WorkshopUpdateItem>();
        var failures = new List<WorkshopFailure>();
        var checkedCount = 0;
        var canceled = false;
        var stateChanged = false;

        for (var index = 0; index < records.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            var record = records[index];
            var workshopId = record.WorkshopId!.Value;
            var title = string.IsNullOrWhiteSpace(record.WorkshopTitle)
                ? record.DisplayName ?? workshopId.ToString()
                : record.WorkshopTitle!;

            progress?.Report(new WorkshopProgress(
                WorkshopProgressStage.Checking,
                index,
                records.Length,
                0,
                $"正在检查 {title}"));

            try
            {
                var details = await GetDetailsAsync(workshopId, cancellationToken).ConfigureAwait(false);
                var relativePath = GetWorkshopRelativePath(workshopId);
                var fullPath = _library.GetFullPath(relativePath);
                var oldTime = record.WorkshopTimeUpdated;
                var needsDownload = !File.Exists(fullPath) || details.TimeUpdated > oldTime;

                if (needsDownload)
                {
                    var directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var temporaryPath = fullPath + ".downloading";
                    TryDeleteFile(temporaryPath);

                    var downloadProgress = new Progress<WorkshopDownloadProgress>(update =>
                    {
                        progress?.Report(new WorkshopProgress(
                            WorkshopProgressStage.Downloading,
                            index,
                            records.Length,
                            update.Percentage,
                            $"正在更新 {details.Title}（{update.Percentage:0.#}%）"));
                    });

                    try
                    {
                        await _downloader
                            .DownloadAsync(details.FileUrl, temporaryPath, downloadProgress, cancellationToken)
                            .ConfigureAwait(false);

                        File.Move(temporaryPath, fullPath, overwrite: true);
                    }
                    finally
                    {
                        TryDeleteFile(temporaryPath);
                    }

                    updated.Add(new WorkshopUpdateItem(
                        workshopId,
                        details.Title,
                        relativePath,
                        oldTime,
                        details.TimeUpdated));
                }

                ApplyDetailsToRecord(record, details, relativePath, fullPath);
                stateChanged = true;
                checkedCount++;

                if (needsDownload)
                {
                    await _stateStore.SaveAsync(catalog.State, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                break;
            }
            catch (Exception ex)
            {
                failures.Add(new WorkshopFailure(workshopId, title, ex.Message));
                _log.Error(ex, $"检查创意工坊更新失败：{workshopId}");
            }
        }

        if (stateChanged)
        {
            try
            {
                await _stateStore.SaveAsync(catalog.State, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "保存工坊更新状态失败");
            }
        }

        return new WorkshopUpdateResult(checkedCount, updated, failures, canceled);
    }

    private static void ApplyDetailsToRecord(
        ModRecord record,
        WorkshopItemInfo details,
        string relativePath,
        string fullPath)
    {
        record.RelativePath = relativePath;
        record.GroupPath = "Workshop";
        record.DisplayName = details.Title;
        record.WorkshopId = details.PublishedFileId;
        record.WorkshopTimeUpdated = details.TimeUpdated;
        record.WorkshopTitle = details.Title;
        record.WorkshopPreviewUrl = details.PreviewUrl;
        record.WorkshopAutoUpdate = true;
        record.Description = details.Description;
        record.Tags = details.Tags.ToList();

        try
        {
            var info = new FileInfo(fullPath);
            record.Size = info.Length;
            record.LastModifiedUtc = info.LastWriteTimeUtc;
        }
        catch (Exception)
        {
            // 文件信息读取失败不影响工坊元数据。
        }
    }

    private static string GetWorkshopRelativePath(ulong publishedFileId) =>
        Path.Combine("Workshop", $"{publishedFileId}.vpk");

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"清理工坊临时文件失败：{path}（{ex.Message}）");
        }
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
        {
            return result;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), out result)
            ? result
            : 0;
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result))
        {
            return result;
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), out result)
            ? result
            : 0;
    }

    private static ulong GetUInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var result))
        {
            return result;
        }

        return value.ValueKind == JsonValueKind.String &&
               ulong.TryParse(value.GetString(), out result)
            ? result
            : 0;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
