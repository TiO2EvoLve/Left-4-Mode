
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Infrastructure;
using Left_4_Mod.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Left_4_Mod.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    private readonly ILogService _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonSettingsService(ILogService log)
    {
        _log = log;
    }

    public string SettingsFilePath { get; set; } = AppPaths.SettingsFile;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonConvert.DeserializeObject<AppSettings>(json, JsonSettings);
            if (settings is null)
            {
                _log.Warn($"设置文件内容为空：{SettingsFilePath}");
                return new AppSettings();
            }

            if (settings.Version <= 0)
            {
                settings.Version = AppSettings.CurrentVersion;
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Error(ex, $"读取设置失败：{SettingsFilePath}");
            BackupBrokenSettings();
            return new AppSettings();
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"读取设置时发生未预期的错误：{SettingsFilePath}");
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var temporaryPath = SettingsFilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            settings.Version = AppSettings.CurrentVersion;
            var json = JsonConvert.SerializeObject(settings, JsonSettings);

            // 先写临时文件再替换，避免退出或异常时产生半写入文件。
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, $"保存设置失败：{SettingsFilePath}");
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    _log.Warn($"清理临时设置文件失败：{temporaryPath}，{ex.Message}");
                }
            }

            _writeGate.Release();
        }
    }

    private void BackupBrokenSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return;
            }

            var backup = $"{SettingsFilePath}.broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(SettingsFilePath, backup, overwrite: true);
            _log.Warn($"已备份损坏的设置文件：{backup}");
        }
        catch (Exception ex)
        {
            _log.Warn($"无法备份损坏的设置文件：{ex.Message}");
        }
    }
}
