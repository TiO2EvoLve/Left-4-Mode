using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Infrastructure;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

/// <summary>
/// 每个模组库一个状态文件，保存在应用数据目录，避免配置混入模组库。
/// </summary>
public sealed class JsonModStateStore : IModStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogService _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonModStateStore(ILogService log)
    {
        _log = log;
    }

    public string StateDirectory { get; set; } = AppPaths.StateDirectory;

    public async Task<ModState> LoadAsync(string libraryRoot, CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(libraryRoot);

        try
        {
            if (!File.Exists(path))
            {
                return new ModState { LibraryPath = libraryRoot };
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<ModState>(json, JsonOptions);

            if (state is null)
            {
                _log.Warn($"模组状态文件为空：{path}");
                return new ModState { LibraryPath = libraryRoot };
            }

            state.Version = ModState.CurrentVersion;
            state.LibraryPath = libraryRoot;
            state.Mods ??= [];
            return state;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"读取模组状态失败：{path}");
            BackupBrokenState(path);
            return new ModState { LibraryPath = libraryRoot };
        }
    }

    public async Task SaveAsync(ModState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (string.IsNullOrWhiteSpace(state.LibraryPath))
        {
            throw new InvalidOperationException("模组状态缺少模组库路径。");
        }

        var path = GetStatePath(state.LibraryPath);
        var temporaryPath = path + ".tmp";

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(StateDirectory);
            state.Version = ModState.CurrentVersion;

            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"保存模组状态失败：{path}");
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
                    _log.Warn($"清理模组状态临时文件失败：{temporaryPath}（{ex.Message}）");
                }
            }

            _writeGate.Release();
        }
    }

    public string GetStatePath(string libraryRoot)
    {
        var normalized = Path.GetFullPath(libraryRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar).ToLowerInvariant();

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(StateDirectory, $"{hash}.json");
    }

    private void BackupBrokenState(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var backup = $"{path}.broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, backup, overwrite: true);
            _log.Warn($"已备份损坏的模组状态文件：{backup}");
        }
        catch (Exception ex)
        {
            _log.Warn($"无法备份损坏的模组状态文件：{ex.Message}");
        }
    }
}
