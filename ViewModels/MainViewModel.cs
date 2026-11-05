using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Models;
using Left_4_Mod.Services;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace Left_4_Mod.ViewModels;

/// <summary>
/// 模组管理主视图模型。文件操作通过服务完成，这里负责筛选、启用状态、推送和设置。
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IModLibraryService _library;
    private readonly IModCatalogService _catalogService;
    private readonly IDeploymentService _deployment;
    private readonly IGamePathService _gamePathService;
    private readonly IWorkshopService _workshop;
    private readonly IModThumbnailService _thumbnails;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly AppSettings _appSettings;
    private readonly List<ModItem> _allMods = [];
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();
    private IReadOnlyList<string> _libraryFolders = [];
    private string _currentFolder = string.Empty;
    private FileViewMode _viewMode = FileViewMode.Details;

    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _thumbnailCts;
    private ModCatalog? _catalog;
    private string _searchText = string.Empty;
    private string _sortField = "Name";
    private bool _sortDescending;
    private GroupOption? _selectedGroup;
    private FilterOption<ModEnabledFilter>? _selectedEnabledFilter;
    private FilterOption<ModDeploymentFilter>? _selectedDeploymentFilter;
    private bool _suppressStateSave;
    private bool _suppressFilterApply;
    private string _gameAddonsPath = string.Empty;

    [Reactive] private string _libraryName = string.Empty;
    [Reactive] private string _libraryPath = string.Empty;
    [Reactive] private bool _hasLibrary;
    [Reactive] private bool _libraryUnavailable;
    [Reactive] private bool _isBusy;
    [Reactive] private string _busyText = string.Empty;
    [Reactive] private double _progressValue;
    [Reactive] private bool _isProgressIndeterminate = true;
    [Reactive] private bool _canCancel;
    [Reactive] private string _message = string.Empty;
    [Reactive] private int _modCount;
    [Reactive] private int _enabledCount;
    [Reactive] private int _deployedCount;
    [Reactive] private int _pendingCount;
    [Reactive] private string _gamePath = string.Empty;
    [Reactive] private string _gameStatusText = "未设置游戏目录。";
    [Reactive] private bool _isGameReady;

    public MainViewModel(
        IModLibraryService library,
        IModCatalogService catalogService,
        IDeploymentService deployment,
        IGamePathService gamePath,
        IWorkshopService workshop,
        IModThumbnailService thumbnails,
        ISettingsService settings,
        ILogService log)
    {
        _library = library;
        _catalogService = catalogService;
        _deployment = deployment;
        _gamePathService = gamePath;
        _workshop = workshop;
        _thumbnails = thumbnails;
        _settings = settings;
        _log = log;
        _appSettings = _settings.Load();

        EnabledFilterOptions =
        [
            new FilterOption<ModEnabledFilter>("全部状态", ModEnabledFilter.All),
            new FilterOption<ModEnabledFilter>("已启用", ModEnabledFilter.Enabled),
            new FilterOption<ModEnabledFilter>("未启用", ModEnabledFilter.Disabled),
        ];

        DeploymentFilterOptions =
        [
            new FilterOption<ModDeploymentFilter>("全部部署状态", ModDeploymentFilter.All),
            new FilterOption<ModDeploymentFilter>("已部署", ModDeploymentFilter.Deployed),
            new FilterOption<ModDeploymentFilter>("未部署", ModDeploymentFilter.NotDeployed),
        ];

        _selectedEnabledFilter = EnabledFilterOptions[0];
        _selectedDeploymentFilter = DeploymentFilterOptions[0];
        GroupOptions.Add(new GroupOption("全部分组", null, 0));

        RestoreLibrary();
        RestoreGamePath();
        UpdateStates();
    }

    public ObservableCollection<ModItem> Mods { get; } = [];

    public ObservableCollection<FileRow> Rows { get; } = [];

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public ObservableCollection<ModItem> SelectedMods { get; } = [];

    public ObservableCollection<GroupOption> GroupOptions { get; } = [];

    public IReadOnlyList<FilterOption<ModEnabledFilter>> EnabledFilterOptions { get; }

    public IReadOnlyList<FilterOption<ModDeploymentFilter>> DeploymentFilterOptions { get; }

    public event Action<string>? ErrorRaised;

    public bool AutoPromptLibrary { get; private set; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            this.RaisePropertyChanged();
            ApplyFilter();
        }
    }

    public GroupOption? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (ReferenceEquals(_selectedGroup, value))
            {
                return;
            }

            _selectedGroup = value;
            this.RaisePropertyChanged();
            ApplyFilter();
        }
    }

    public FilterOption<ModEnabledFilter>? SelectedEnabledFilter
    {
        get => _selectedEnabledFilter;
        set
        {
            if (ReferenceEquals(_selectedEnabledFilter, value))
            {
                return;
            }

            _selectedEnabledFilter = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(EnabledFilterIndex));
            ApplyFilter();
        }
    }

    public FilterOption<ModDeploymentFilter>? SelectedDeploymentFilter
    {
        get => _selectedDeploymentFilter;
        set
        {
            if (ReferenceEquals(_selectedDeploymentFilter, value))
            {
                return;
            }

            _selectedDeploymentFilter = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(DeploymentFilterIndex));
            ApplyFilter();
        }
    }

    public int EnabledFilterIndex
    {
        get => (int)(SelectedEnabledFilter?.Value ?? ModEnabledFilter.All);
        set
        {
            if (value < 0 || value >= EnabledFilterOptions.Count)
            {
                return;
            }

            SelectedEnabledFilter = EnabledFilterOptions[value];
        }
    }

    public int DeploymentFilterIndex
    {
        get => (int)(SelectedDeploymentFilter?.Value ?? ModDeploymentFilter.All);
        set
        {
            if (value < 0 || value >= DeploymentFilterOptions.Count)
            {
                return;
            }

            SelectedDeploymentFilter = DeploymentFilterOptions[value];
        }
    }

    public string WindowTitle =>
        HasLibrary
            ? $"{LibraryName} — L4D2 模组管理器"
            : "L4D2 模组管理器";

    public bool ShowWelcome => !HasLibrary;

    public bool HasSelection => SelectedMods.Count > 0;

    public bool CanModify => HasLibrary && !IsBusy;

    public bool CanImport => HasLibrary && !IsBusy;

    public bool CanPush => HasLibrary && !IsBusy;

    public bool CanRefresh => HasLibrary && !IsBusy;

    public bool CanOpenSettings => !IsBusy;

    public bool IsFilterActive =>
        !string.IsNullOrWhiteSpace(SearchText) ||
        SelectedEnabledFilter?.Value != ModEnabledFilter.All ||
        SelectedDeploymentFilter?.Value != ModDeploymentFilter.All;

    public bool IsEmptyLibrary =>
        HasLibrary && !IsBusy && ModCount == 0 && _libraryFolders.Count == 0;

    public bool HasNoResults =>
        HasLibrary && !IsBusy && IsFilterActive && Rows.Count == 0;

    public bool IsCurrentFolderEmpty =>
        HasLibrary && !IsBusy && !IsFilterActive && Rows.Count == 0 && !IsEmptyLibrary;

    public bool CanNavigateBack => _backHistory.Count > 0;

    public bool CanNavigateForward => _forwardHistory.Count > 0;

    public bool CanNavigateUp => _currentFolder.Length > 0;

    public bool IsDetailsView => _viewMode == FileViewMode.Details;

    public bool IsLargeIconsView => _viewMode == FileViewMode.LargeIcons;

    public bool ShowDetailsHeader => HasLibrary && IsDetailsView;

    public string CurrentFolderDisplay =>
        _currentFolder.Length == 0 ? "模组库" : _currentFolder.Replace('\\', '/');

    public string CurrentFolderPath => _currentFolder;

    public string NoResultsTitle => "没有匹配的项目";

    public string NoResultsHint => "换个关键词，或清除筛选。";

    public string NoResultsActionText => "清除筛选";

    public string FilterSummary
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                parts.Add($"搜索“{SearchText.Trim()}”");
            }

            if (SelectedEnabledFilter is not null &&
                SelectedEnabledFilter.Value != ModEnabledFilter.All)
            {
                parts.Add(SelectedEnabledFilter.DisplayName);
            }

            if (SelectedDeploymentFilter is not null &&
                SelectedDeploymentFilter.Value != ModDeploymentFilter.All)
            {
                parts.Add(SelectedDeploymentFilter.DisplayName);
            }

            return parts.Count == 0 ? "全部模组" : string.Join(" · ", parts);
        }
    }

    public string StatusText
    {
        get
        {
            if (!HasLibrary)
            {
                return "未选择模组库";
            }

            return IsFilterActive
                ? $"显示 {Mods.Count} / {ModCount} 个模组"
                : $"{ModCount} 个模组";
        }
    }

    public string SelectionStatus
    {
        get
        {
            if (SelectedMods.Count == 0)
            {
                return string.Empty;
            }

            var enabled = SelectedMods.Count(item => item.IsEnabled);
            return $"已选择 {SelectedMods.Count} 项 · 已启用 {enabled}";
        }
    }

    public string ModSummary =>
        $"模组 {ModCount} · 已启用 {EnabledCount} · 已部署 {DeployedCount} · 待推送 {PendingCount}";

    public string NameHeader => BuildHeader("名称", "Name");

    public string GroupHeader => BuildHeader("分组", "Group");

    public string SizeHeader => BuildHeader("大小", "Size");

    public string ModifiedHeader => BuildHeader("修改日期", "Modified");

    public async Task InitializeAsync()
    {
        if (!HasLibrary)
        {
            AutoPromptLibrary = !LibraryUnavailable;
            return;
        }

        await LoadCatalogAsync();
    }

    public async Task SetLibraryAsync(string path)
    {
        if (IsBusy)
        {
            Message = "正在执行其他操作，请稍候。";
            return;
        }

        try
        {
            _library.SetLibrary(path);
            HasLibrary = true;
            LibraryUnavailable = false;
            LibraryName = _library.LibraryName;
            LibraryPath = _library.RootPath ?? path;
            AutoPromptLibrary = false;

            _appSettings.LibraryPath = _library.RootPath;
            await SaveSettingsAsync();

            ClearCatalog();
            await LoadCatalogAsync();
        }
        catch (Exception ex)
        {
            Fail($"无法使用该模组库：{ex.Message}", ex);
        }
    }

    public async Task<(bool Success, string Error)> ApplySettingsAsync(
        string libraryPath,
        string gamePath)
    {
        if (IsBusy)
        {
            return (false, "正在执行其他操作，请稍候。");
        }

        var libraryChanged = false;

        if (!string.IsNullOrWhiteSpace(libraryPath) && !Directory.Exists(libraryPath))
        {
            return (false, $"模组库目录不存在：{libraryPath}");
        }

        GamePathInfo? gameInfo = null;
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            gameInfo = _gamePathService.TryResolve(gamePath, out var gameError);
            if (gameInfo is null)
            {
                return (false, gameError);
            }
        }

        if (!string.IsNullOrWhiteSpace(libraryPath))
        {
            if (_library.RootPath is null ||
                !FileSystemService.PathsEqual(_library.RootPath, libraryPath))
            {
                try
                {
                    _library.SetLibrary(libraryPath);
                    HasLibrary = true;
                    LibraryUnavailable = false;
                    LibraryName = _library.LibraryName;
                    LibraryPath = _library.RootPath ?? libraryPath;
                    AutoPromptLibrary = false;
                    libraryChanged = true;
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            }
        }

        GamePath = gameInfo?.GameRoot ?? string.Empty;
        _gameAddonsPath = gameInfo?.AddonsPath ?? string.Empty;
        IsGameReady = gameInfo is not null;
        GameStatusText = gameInfo is not null
            ? $"游戏目录：{gameInfo.GameRoot}"
            : "未设置游戏目录。";

        _appSettings.LibraryPath = _library.RootPath;
        _appSettings.GamePath = gameInfo?.GameRoot;
        _appSettings.DeploymentStrategy = "SymbolicLink";

        if (!await SaveSettingsAsync())
        {
            return (false, Message);
        }

        if (libraryChanged)
        {
            ClearCatalog();
            await LoadCatalogAsync();
        }
        else
        {
            await RefreshDeploymentStatesAsync();
        }

        return (true, string.Empty);
    }

    public async Task<(string? Path, string? Error)> DetectGamePathAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                var info = _gamePathService.AutoDetect(out var error);
                return (info?.GameRoot, error);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "自动检测游戏路径失败");
            return (null, $"自动检测失败：{ex.Message}");
        }
    }

    public async Task RefreshAsync()
    {
        if (!HasLibrary || IsBusy)
        {
            return;
        }

        await LoadCatalogAsync();
    }

    public void SetSelection(IEnumerable<ModItem> items)
    {
        SelectedMods.Clear();
        foreach (var item in items)
        {
            if (Mods.Contains(item))
            {
                SelectedMods.Add(item);
            }
        }

        UpdateStates();
    }

    public void SetSort(string field)
    {
        if (string.Equals(_sortField, field, StringComparison.Ordinal))
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortField = field;
            _sortDescending = false;
        }

        this.RaisePropertyChanged(nameof(NameHeader));
        this.RaisePropertyChanged(nameof(GroupHeader));
        this.RaisePropertyChanged(nameof(SizeHeader));
        this.RaisePropertyChanged(nameof(ModifiedHeader));

        ApplyFilter();
    }

    public async Task SetSelectedEnabledAsync(bool enabled)
    {
        if (SelectedMods.Count == 0 || IsBusy)
        {
            return;
        }

        _suppressStateSave = true;
        try
        {
            foreach (var item in SelectedMods)
            {
                item.IsEnabled = enabled;
            }
        }
        finally
        {
            _suppressStateSave = false;
        }

        UpdateCounts();
        UpdateStates();
        Message = enabled
            ? $"已启用 {SelectedMods.Count} 个模组，推送到游戏后生效。"
            : $"已禁用 {SelectedMods.Count} 个模组，再次推送会清理游戏目录中的部署。";

        await PersistStateAsync();
    }

    public void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedGroup = GroupOptions.FirstOrDefault();
        SelectedEnabledFilter = EnabledFilterOptions[0];
        SelectedDeploymentFilter = DeploymentFilterOptions[0];
    }

    public void NavigateTo(string relativePath, bool addHistory = true)
    {
        if (!HasLibrary)
        {
            return;
        }

        var normalized = NormalizeFolderPath(relativePath);
        if (normalized is null)
        {
            Message = $"文件夹不存在：{relativePath}";
            return;
        }

        if (string.Equals(normalized, _currentFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (addHistory)
        {
            _backHistory.Push(_currentFolder);
            _forwardHistory.Clear();
        }

        _currentFolder = normalized;
        ApplyFilter();
        NotifyNavigationChanged();
    }

    public void NavigateBack()
    {
        if (_backHistory.Count == 0)
        {
            return;
        }

        var target = _backHistory.Pop();
        _forwardHistory.Push(_currentFolder);
        _currentFolder = target;
        ApplyFilter();
        NotifyNavigationChanged();
    }

    public void NavigateForward()
    {
        if (_forwardHistory.Count == 0)
        {
            return;
        }

        var target = _forwardHistory.Pop();
        _backHistory.Push(_currentFolder);
        _currentFolder = target;
        ApplyFilter();
        NotifyNavigationChanged();
    }

    public void NavigateUp()
    {
        var parent = GetParentFolderPath(_currentFolder);
        if (parent is not null)
        {
            NavigateTo(parent);
        }
    }

    public void SetViewMode(FileViewMode mode)
    {
        if (_viewMode == mode)
        {
            return;
        }

        _viewMode = mode;
        this.RaisePropertyChanged(nameof(IsDetailsView));
        this.RaisePropertyChanged(nameof(IsLargeIconsView));
        this.RaisePropertyChanged(nameof(ShowDetailsHeader));
    }

    private string? NormalizeFolderPath(string relativePath)
    {
        if (!HasLibrary)
        {
            return null;
        }

        try
        {
            var normalized = string.IsNullOrWhiteSpace(relativePath)
                ? string.Empty
                : _library.GetRelativePath(_library.GetFullPath(relativePath));

            normalized = normalized
                .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (normalized.Length > 0 && !Directory.Exists(_library.GetFullPath(normalized)))
            {
                return null;
            }

            return normalized;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? GetParentFolderPath(string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return null;
        }

        var parent = Path.GetDirectoryName(relativePath);
        return parent ?? string.Empty;
    }

    private void NotifyNavigationChanged()
    {
        this.RaisePropertyChanged(nameof(CanNavigateBack));
        this.RaisePropertyChanged(nameof(CanNavigateForward));
        this.RaisePropertyChanged(nameof(CanNavigateUp));
        this.RaisePropertyChanged(nameof(CurrentFolderDisplay));
        this.RaisePropertyChanged(nameof(CurrentFolderPath));
        BuildBreadcrumbs();
    }

    private void BuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (!HasLibrary)
        {
            return;
        }

        Breadcrumbs.Add(new BreadcrumbItem("模组库", string.Empty, _currentFolder.Length == 0, true));

        if (_currentFolder.Length == 0)
        {
            return;
        }

        var parts = _currentFolder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = string.Empty;
        for (var index = 0; index < parts.Length; index++)
        {
            current = string.IsNullOrEmpty(current)
                ? parts[index]
                : Path.Combine(current, parts[index]);

            Breadcrumbs.Add(new BreadcrumbItem(
                parts[index],
                current,
                index == parts.Length - 1,
                false));
        }
    }

    public ImportPlan? CreateImportPlan(IReadOnlyList<string> sourceFiles)
    {
        if (!HasLibrary)
        {
            return null;
        }

        try
        {
            return _library.CreateImportPlan(GetImportTargetGroup(), sourceFiles);
        }
        catch (Exception ex)
        {
            Fail($"无法导入模组：{ex.Message}", ex);
            return null;
        }
    }

    public async Task<ImportOutcome?> ImportAsync(ImportPlan plan)
    {
        if (!HasLibrary || IsBusy)
        {
            return null;
        }

        var token = BeginOperation("正在导入模组…", indeterminate: false);
        IsProgressIndeterminate = false;
        ProgressValue = 0;

        var progress = new Progress<ImportProgress>(update =>
        {
            if (!IsBusy)
            {
                return;
            }

            ProgressValue = update.Percentage;
            BusyText = update.TotalFiles > 0
                ? $"正在导入 {Math.Min(update.CompletedFiles + 1, update.TotalFiles)}/{update.TotalFiles}：{update.CurrentFileName}"
                : "正在导入模组…";
        });

        ImportOutcome? outcome = null;

        try
        {
            outcome = await _library.ImportAsync(plan, progress, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Message = "导入已取消。";
        }
        catch (Exception ex)
        {
            Fail($"导入失败：{ex.Message}", ex);
        }
        finally
        {
            EndOperation();
        }

        if (outcome is not null)
        {
            await LoadCatalogAsync();
            Message = outcome.Canceled
                ? $"导入已取消，已成功导入 {outcome.ImportedRelativePaths.Count} 个文件。"
                : $"已导入 {outcome.ImportedRelativePaths.Count} 个模组，默认未启用。";

            if (outcome.RenamedCount > 0)
            {
                Message += $" {outcome.RenamedCount} 个同名文件已自动重命名。";
            }
        }

        return outcome;
    }

    public async Task<WorkshopAddResult?> AddWorkshopModAsync(ulong publishedFileId)
    {
        if (!HasLibrary || IsBusy)
        {
            return null;
        }

        var token = BeginOperation("正在查询创意工坊…", indeterminate: false);
        IsProgressIndeterminate = false;
        ProgressValue = 0;

        var progress = new Progress<WorkshopProgress>(update =>
        {
            if (!IsBusy)
            {
                return;
            }

            ProgressValue = update.OverallPercentage;
            BusyText = update.Message;
        });

        WorkshopAddResult? result = null;
        try
        {
            result = await _workshop.AddAsync(publishedFileId, progress, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Message = "创意工坊下载已取消。";
        }
        catch (Exception ex)
        {
            Fail($"创意工坊下载失败：{ex.Message}", ex);
        }
        finally
        {
            EndOperation();
        }

        if (result is not null && result.Success)
        {
            await LoadCatalogAsync();
            Message = result.WasUpdate
                ? $"创意工坊模组已更新：{result.Details?.Title}。"
                : $"已添加工坊模组：{result.Details?.Title}，默认未启用。";
        }
        else if (result is not null)
        {
            Message = result.Error ?? "创意工坊下载失败。";
        }

        return result;
    }

    public async Task<WorkshopUpdateResult?> CheckWorkshopUpdatesAsync(bool automatic)
    {
        if (!HasLibrary || IsBusy || _catalog is null)
        {
            return null;
        }

        var workshopCount = _catalog.State.Mods.Count(record => record.WorkshopId.HasValue);
        if (workshopCount == 0)
        {
            if (!automatic)
            {
                Message = "模组库中没有创意工坊模组。";
            }

            return null;
        }

        var token = BeginOperation("正在检查创意工坊更新…", indeterminate: false);
        IsProgressIndeterminate = false;
        ProgressValue = 0;

        var progress = new Progress<WorkshopProgress>(update =>
        {
            if (!IsBusy)
            {
                return;
            }

            ProgressValue = update.OverallPercentage;
            BusyText = update.Message;
        });

        WorkshopUpdateResult? result = null;
        try
        {
            result = await _workshop.CheckForUpdatesAsync(_catalog, progress, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Message = "已取消创意工坊更新检查。";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "检查创意工坊更新失败");
            Message = $"检查创意工坊更新失败：{ex.Message}";
            if (!automatic)
            {
                ErrorRaised?.Invoke(Message);
            }
        }
        finally
        {
            EndOperation();
        }

        if (result is not null)
        {
            if (result.CheckedCount > 0)
            {
                await LoadCatalogAsync();
            }

            if (result.Updated.Count > 0)
            {
                Message = $"创意工坊更新完成：已更新 {result.Updated.Count} 个模组。";

                if (IsGameRunning())
                {
                    Message += " 检测到游戏正在运行，请在游戏内刷新模组或重启游戏。";
                }
            }
            else if (result.Canceled)
            {
                Message = "已取消创意工坊更新检查。";
            }
            else
            {
                Message = $"已检查 {result.CheckedCount} 个创意工坊模组，没有可用更新。";
            }

            if (result.Failures.Count > 0)
            {
                Message += $" 失败 {result.Failures.Count} 个。";
                foreach (var failure in result.Failures)
                {
                    _log.Warn($"工坊更新失败：{failure.PublishedFileId} {failure.Title}：{failure.Message}");
                }
            }
        }

        return result;
    }

    public IReadOnlyList<FolderChoice> GetFolderChoices()
    {
        var choices = new List<FolderChoice>
        {
            new(string.Empty, "模组库根目录（未分类）"),
        };

        choices.AddRange(_libraryFolders.Select(path =>
            new FolderChoice(path, path.Replace('\\', '/'))));

        return choices;
    }

    public async Task<DeleteInfo> GetDeleteInfoAsync(IReadOnlyList<ModItem> items)
    {
        if (!HasLibrary || items.Count == 0)
        {
            return new DeleteInfo(0, 0, 0);
        }

        try
        {
            return await _library
                .GetDeleteInfoAsync(items.Select(item => item.RelativePath).ToArray(), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warn($"读取删除信息失败：{ex.Message}");
            return new DeleteInfo(items.Count, 0, items.Sum(item => item.Size));
        }
    }

    public async Task<FileOperationResult> CreateFolderAsync(string name, string parentRelativePath)
    {
        if (!HasLibrary || IsBusy)
        {
            return FileOperationResult.Fail("当前无法创建文件夹。");
        }

        string? createdRelativePath = null;
        string? error = null;
        var token = BeginOperation("正在创建文件夹…", indeterminate: true);

        try
        {
            var created = await _library
                .CreateFolderAsync(parentRelativePath, name, token)
                .ConfigureAwait(true);
            createdRelativePath = created.RelativePath;
        }
        catch (OperationCanceledException)
        {
            Message = "已取消创建文件夹。";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Error(ex, "创建文件夹失败");
        }
        finally
        {
            EndOperation();
        }

        if (createdRelativePath is null)
        {
            return FileOperationResult.Fail(error ?? "创建文件夹失败。");
        }

        await LoadCatalogAsync();
        Message = $"已创建文件夹：{createdRelativePath.Replace('\\', '/')}";
        return FileOperationResult.Ok(Message);
    }

    public async Task<FileOperationResult> RenameModAsync(ModItem item, string newName)
    {
        if (!HasLibrary || IsBusy || _catalog is null)
        {
            return FileOperationResult.Fail("当前无法重命名模组。");
        }

        var extension = Path.GetExtension(item.RelativePath);
        var finalName = newName.Trim();
        if (extension.Length > 0 &&
            !finalName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            finalName += extension;
        }

        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(finalName)))
        {
            return FileOperationResult.Fail("文件名不能为空。");
        }

        string? newRelativePath = null;
        string? error = null;
        var token = BeginOperation("正在重命名模组…", indeterminate: true);

        try
        {
            newRelativePath = await _library
                .RenameAsync(item.RelativePath, finalName, token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Message = "已取消重命名。";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Error(ex, "重命名模组失败");
        }
        finally
        {
            EndOperation();
        }

        if (newRelativePath is null)
        {
            return FileOperationResult.Fail(error ?? "重命名失败。");
        }

        var record = _catalog.State.Mods.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, item.Id, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            return FileOperationResult.Fail("找不到该模组的状态记录。");
        }

        record.RelativePath = newRelativePath;
        record.GroupPath = GetGroupPath(newRelativePath);
        if (!record.IsWorkshop)
        {
            record.DisplayName = Path.GetFileName(newRelativePath);
        }

        var syncWarning = await SaveAndSyncMovedRecordsAsync([record], []).ConfigureAwait(true);
        await LoadCatalogAsync();
        Message = $"已重命名为：{Path.GetFileName(newRelativePath)}";
        if (syncWarning is not null)
        {
            Message += $" {syncWarning}";
        }

        return FileOperationResult.Ok(Message);
    }

    public async Task<FileOperationResult> MoveModsAsync(
        IReadOnlyList<ModItem> items,
        string destinationRelativePath)
    {
        if (!HasLibrary || IsBusy || _catalog is null || items.Count == 0)
        {
            return FileOperationResult.Fail("当前无法移动模组。");
        }

        var movedRecords = new List<ModRecord>();
        var errors = new List<string>();
        var skipped = 0;
        var token = BeginOperation("正在移动模组…", indeterminate: true);

        try
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var result = await _library
                        .MoveAsync([item.RelativePath], destinationRelativePath, token)
                        .ConfigureAwait(true);

                    if (result.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    var newRelativePath = result[0];
                    var record = _catalog.State.Mods.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, item.Id, StringComparison.OrdinalIgnoreCase));

                    if (record is null)
                    {
                        errors.Add($"{item.DisplayName}：找不到状态记录");
                        continue;
                    }

                    record.RelativePath = newRelativePath;
                    record.GroupPath = GetGroupPath(newRelativePath);
                    if (!record.IsWorkshop)
                    {
                        record.DisplayName = Path.GetFileName(newRelativePath);
                    }

                    movedRecords.Add(record);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.DisplayName}：{ex.Message}");
                    _log.Error(ex, $"移动模组失败：{item.RelativePath}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Message = "已取消移动。";
        }
        finally
        {
            EndOperation();
        }

        var syncWarning = movedRecords.Count > 0
            ? await SaveAndSyncMovedRecordsAsync(movedRecords, []).ConfigureAwait(true)
            : null;

        if (movedRecords.Count > 0)
        {
            await LoadCatalogAsync();
            Message = $"已移动 {movedRecords.Count} 个模组到：{FormatGroup(destinationRelativePath)}";
        }
        else if (skipped > 0 && errors.Count == 0)
        {
            Message = "模组已经位于目标文件夹。";
        }
        else
        {
            Message = "没有移动任何模组。";
        }

        if (syncWarning is not null)
        {
            Message += $" {syncWarning}";
        }

        if (errors.Count > 0)
        {
            Message += $" 失败 {errors.Count} 个。";
            return FileOperationResult.Fail(Message + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        return FileOperationResult.Ok(Message);
    }

    public async Task<FileOperationResult> DeleteModsAsync(IReadOnlyList<ModItem> items)
    {
        if (!HasLibrary || IsBusy || _catalog is null || items.Count == 0)
        {
            return FileOperationResult.Fail("当前无法删除模组。");
        }

        var paths = items.Select(item => item.RelativePath).ToArray();
        var ids = items.Select(item => item.Id).ToArray();
        var token = BeginOperation("正在删除模组…", indeterminate: true);

        try
        {
            await _library.DeleteAsync(paths, token).ConfigureAwait(true);

            _catalog.State.Mods.RemoveAll(record =>
                paths.Any(path =>
                    string.Equals(path, record.RelativePath, StringComparison.OrdinalIgnoreCase)));

            await _catalogService.SaveStateAsync(_catalog.State, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Message = "已取消删除。";
            return FileOperationResult.Fail(Message);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "删除模组失败");
            Message = $"删除失败：{ex.Message}";
            return FileOperationResult.Fail(Message);
        }
        finally
        {
            EndOperation();
        }

        var syncWarning = await SyncDeploymentsAfterChangeAsync([], ids).ConfigureAwait(true);
        await LoadCatalogAsync();
        Message = $"已将 {items.Count} 个模组移动到回收站。";
        if (syncWarning is not null)
        {
            Message += $" {syncWarning}";
        }

        return FileOperationResult.Ok(Message);
    }

    public async Task<DeleteInfo> GetDeleteInfoForPathAsync(string relativePath)
    {
        if (!HasLibrary || string.IsNullOrWhiteSpace(relativePath))
        {
            return new DeleteInfo(0, 0, 0);
        }

        try
        {
            return await _library
                .GetDeleteInfoAsync([relativePath], CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warn($"读取文件夹删除信息失败：{ex.Message}");
            return new DeleteInfo(0, 1, 0);
        }
    }

    public async Task<FileOperationResult> DeleteFolderAsync(FileRow folder)
    {
        if (!HasLibrary || IsBusy || _catalog is null || !folder.IsFolder)
        {
            return FileOperationResult.Fail("当前无法删除文件夹。");
        }

        var removedIds = new List<string>();
        var token = BeginOperation("正在删除文件夹…", indeterminate: true);

        try
        {
            await _library.DeleteAsync([folder.RelativePath], token).ConfigureAwait(true);

            var prefix = folder.RelativePath + Path.DirectorySeparatorChar;
            var removed = _catalog.State.Mods
                .Where(record =>
                    string.Equals(record.RelativePath, folder.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                    record.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            removedIds.AddRange(removed.Select(record => record.Id));
            var removedIdSet = removedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _catalog.State.Mods.RemoveAll(record => removedIdSet.Contains(record.Id));

            await _catalogService.SaveStateAsync(_catalog.State, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Message = "已取消删除文件夹。";
            return FileOperationResult.Fail(Message);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "删除文件夹失败");
            Message = $"删除文件夹失败：{ex.Message}";
            return FileOperationResult.Fail(Message);
        }
        finally
        {
            EndOperation();
        }

        var prefixPath = folder.RelativePath + Path.DirectorySeparatorChar;
        var deletedFolder = folder.RelativePath;
        var parentPath = GetParentFolderPath(deletedFolder) ?? string.Empty;
        var currentWasDeleted =
            string.Equals(_currentFolder, deletedFolder, StringComparison.OrdinalIgnoreCase) ||
            _currentFolder.StartsWith(prefixPath, StringComparison.OrdinalIgnoreCase);

        if (currentWasDeleted)
        {
            _currentFolder = parentPath;
            _backHistory.Clear();
            _forwardHistory.Clear();
        }

        if (removedIds.Count > 0)
        {
            await SyncDeploymentsAfterChangeAsync([], removedIds).ConfigureAwait(true);
        }

        await LoadCatalogAsync();
        NotifyNavigationChanged();
        Message = $"已将文件夹“{folder.Name}”及其中内容移动到回收站。";
        return FileOperationResult.Ok(Message);
    }

    private async Task<string?> SaveAndSyncMovedRecordsAsync(
        IReadOnlyList<ModRecord> records,
        IReadOnlyList<string> removedIds)
    {
        if (_catalog is null || records.Count == 0)
        {
            return null;
        }

        try
        {
            await _catalogService.SaveStateAsync(_catalog.State).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "保存移动/重命名后的状态失败");
            return "状态保存失败。";
        }

        return await SyncDeploymentsAfterChangeAsync(records, removedIds).ConfigureAwait(true);
    }

    private async Task<string?> SyncDeploymentsAfterChangeAsync(
        IReadOnlyList<ModRecord> changedRecords,
        IReadOnlyList<string> removedIds)
    {
        try
        {
            var enabled = changedRecords.Where(record => record.IsEnabled).ToArray();
            if (enabled.Length > 0)
            {
                await _deployment.UpdateModSourcesAsync(enabled).ConfigureAwait(true);
            }

            var removeIds = changedRecords
                .Where(record => !record.IsEnabled)
                .Select(record => record.Id)
                .Concat(removedIds)
                .ToArray();

            if (removeIds.Length > 0)
            {
                await _deployment.RemoveModsAsync(removeIds).ConfigureAwait(true);
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "同步部署链接失败");
            return "部分部署链接未能同步，请手动推送。";
        }
    }

    private static string GetGroupPath(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        return string.IsNullOrEmpty(directory) ? string.Empty : directory;
    }

    private static string FormatGroup(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? "未分类" : relativePath.Replace('\\', '/');

    public async Task<DeploymentPlan?> CreateDeploymentPlanAsync()
    {
        if (!HasLibrary)
        {
            return null;
        }

        if (!IsGameReady)
        {
            Message = "请先在设置中配置 L4D2 游戏目录。";
            return null;
        }

        var token = BeginOperation("正在生成推送计划…", indeterminate: true);
        try
        {
            var manifest = await _deployment.LoadManifestAsync(token).ConfigureAwait(true);
            var plan = _deployment.CreatePlan(
                _allMods.Select(item => item.Record).ToArray(),
                manifest,
                _gameAddonsPath);

            if (_catalog is not null)
            {
                _catalog.Manifest = manifest;
            }

            return plan;
        }
        catch (OperationCanceledException)
        {
            Message = "已取消生成推送计划。";
            return null;
        }
        catch (Exception ex)
        {
            Fail($"无法生成推送计划：{ex.Message}", ex);
            return null;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<DeploymentResult?> PushAsync(DeploymentPlan plan)
    {
        if (!HasLibrary || IsBusy)
        {
            return null;
        }

        var token = BeginOperation("正在推送到游戏…", indeterminate: false);
        IsProgressIndeterminate = false;
        ProgressValue = 0;

        var progress = new Progress<DeploymentProgress>(update =>
        {
            if (!IsBusy)
            {
                return;
            }

            ProgressValue = update.Percentage;
            BusyText = update.Total > 0
                ? $"正在推送 {Math.Min(update.Completed + 1, update.Total)}/{update.Total}：{update.CurrentFile}"
                : "正在推送到游戏…";
        });

        DeploymentResult? result = null;

        try
        {
            result = await _deployment.ApplyAsync(plan, progress, token).ConfigureAwait(true);
            Message = result.Canceled
                ? $"推送已取消：新增 {result.Added}、更新 {result.Updated}、移除 {result.Removed}，失败 {result.Failures.Count}。"
                : $"推送完成：新增 {result.Added}、更新 {result.Updated}、移除 {result.Removed}，失败 {result.Failures.Count}。";
        }
        catch (OperationCanceledException)
        {
            Message = "推送已取消。";
        }
        catch (Exception ex)
        {
            Fail($"推送失败：{ex.Message}", ex);
        }
        finally
        {
            EndOperation();
        }

        if (result is not null)
        {
            await RefreshDeploymentStatesAsync();
        }

        return result;
    }

    public void CancelCurrentOperation()
    {
        try
        {
            _operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            _log.Warn("取消操作时令牌已经释放。");
        }
    }

    public bool IsGameRunning() => _gamePathService.IsGameRunning();

    public void ReportStatus(string message) => Message = message;

    public void ReportViewError(string message, Exception exception)
    {
        _log.Error(exception, message);
        Message = message;
        ErrorRaised?.Invoke(message);
    }

    public void ReportViewWarning(string message, Exception exception)
    {
        _log.Warn($"{message}（{exception.Message}）");
        Message = message;
    }

    public async Task<bool> SaveSettingsAsync()
    {
        _appSettings.LibraryPath = _library.RootPath;
        _appSettings.GamePath = IsGameReady ? GamePath : null;
        _appSettings.DeploymentStrategy = "SymbolicLink";

        try
        {
            await _settings.SaveAsync(_appSettings).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "保存设置失败");
            Message = $"保存设置失败：{ex.Message}";
            return false;
        }
    }

    private async Task<bool> LoadCatalogAsync()
    {
        if (!HasLibrary)
        {
            return false;
        }

        var token = BeginOperation("正在扫描模组库…", indeterminate: true);
        try
        {
            var catalog = await _catalogService
                .LoadAsync(IsGameReady ? _gameAddonsPath : null, token)
                .ConfigureAwait(true);

            _libraryFolders = await _library
                .EnumerateFoldersAsync(token)
                .ConfigureAwait(true);

            foreach (var item in _allMods)
            {
                item.PropertyChanged -= OnModPropertyChanged;
            }

            _allMods.Clear();
            _catalog = catalog;
            _allMods.AddRange(catalog.Mods);

            foreach (var item in _allMods)
            {
                item.PropertyChanged += OnModPropertyChanged;
            }

            BuildGroupOptions();
            EnsureCurrentFolderValid();
            ApplyFilter();
            NotifyNavigationChanged();
            UpdateCounts();

            Message = _allMods.Count == 0
                ? "模组库中没有找到 .vpk 模组。"
                : $"已加载 {_allMods.Count} 个模组。";

            return true;
        }
        catch (OperationCanceledException)
        {
            Message = "已取消扫描。";
            return false;
        }
        catch (Exception ex)
        {
            Fail($"无法扫描模组库：{ex.Message}", ex);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    private void ClearCatalog()
    {
        foreach (var item in _allMods)
        {
            item.PropertyChanged -= OnModPropertyChanged;
        }

        _allMods.Clear();
        _catalog = null;
        _libraryFolders = [];
        Mods.Clear();
        SelectedMods.Clear();
        var previousRows = Rows.ToArray();
        Rows.Clear();
        foreach (var row in previousRows)
        {
            row.DisposeThumbnail();
        }

        Breadcrumbs.Clear();
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
        _currentFolder = string.Empty;
        _backHistory.Clear();
        _forwardHistory.Clear();

        _suppressFilterApply = true;
        try
        {
            GroupOptions.Clear();
            GroupOptions.Add(new GroupOption("全部分组", null, 0));
            _selectedGroup = GroupOptions[0];
            this.RaisePropertyChanged(nameof(SelectedGroup));
        }
        finally
        {
            _suppressFilterApply = false;
        }

        UpdateCounts();
        UpdateStates();
    }

    private async Task RefreshDeploymentStatesAsync()
    {
        if (_catalog is null)
        {
            return;
        }

        try
        {
            await _catalogService.RefreshDeploymentStatesAsync(
                _catalog,
                IsGameReady ? _gameAddonsPath : null,
                CancellationToken.None).ConfigureAwait(true);

            UpdateCounts();
            UpdateStates();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "刷新部署状态失败");
            Message = $"刷新部署状态失败：{ex.Message}";
        }
    }

    private void OnModPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ModItem item)
        {
            return;
        }

        if (e.PropertyName == nameof(ModItem.IsEnabled))
        {
            UpdateCounts();
            UpdateStates();
            Message = item.IsEnabled
                ? $"已启用“{item.DisplayName}”，推送到游戏后生效。"
                : $"已禁用“{item.DisplayName}”，再次推送会清理游戏目录中的部署。";

            if (!_suppressStateSave)
            {
                _ = PersistStateAsync();
            }
        }
        else if (e.PropertyName == nameof(ModItem.DeploymentState))
        {
            UpdateCounts();
            UpdateStates();
        }
    }

    private async Task PersistStateAsync()
    {
        if (_catalog is null)
        {
            return;
        }

        try
        {
            await _catalogService.SaveStateAsync(_catalog.State).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "保存模组启用状态失败");
            Message = $"保存启用状态失败：{ex.Message}";
        }
    }

    private void RestoreLibrary()
    {
        var savedPath = _appSettings.LibraryPath;
        if (string.IsNullOrWhiteSpace(savedPath))
        {
            return;
        }

        LibraryPath = savedPath;

        if (!Directory.Exists(savedPath))
        {
            LibraryUnavailable = true;
            Message = $"上次使用的模组库已不存在：{savedPath}";
            return;
        }

        try
        {
            _library.SetLibrary(savedPath);
            HasLibrary = true;
            LibraryName = _library.LibraryName;
            LibraryPath = _library.RootPath ?? savedPath;
            _log.Info($"已恢复模组库：{LibraryPath}");
        }
        catch (Exception ex)
        {
            LibraryUnavailable = true;
            Message = $"无法打开上次的模组库：{ex.Message}";
            _log.Error(ex, "恢复模组库失败");
        }
    }

    private void RestoreGamePath()
    {
        if (string.IsNullOrWhiteSpace(_appSettings.GamePath))
        {
            GameStatusText = "未设置游戏目录。";
            return;
        }

        GamePath = _appSettings.GamePath;
        var info = _gamePathService.TryResolve(_appSettings.GamePath, out var error);
        if (info is null)
        {
            IsGameReady = false;
            GameStatusText = $"游戏目录不可用：{error}";
            _log.Warn($"恢复游戏路径失败：{error}");
            return;
        }

        GamePath = info.GameRoot;
        _gameAddonsPath = info.AddonsPath;
        IsGameReady = true;
        GameStatusText = $"游戏目录：{info.GameRoot}";
    }

    private void BuildGroupOptions()
    {
        var previousPath = _selectedGroup?.RelativePath;

        _suppressFilterApply = true;
        try
        {
            GroupOptions.Clear();
            GroupOptions.Add(new GroupOption("全部分组", null, _allMods.Count));

            var counts = _allMods
                .GroupBy(item => item.GroupPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            var paths = new HashSet<string>(_libraryFolders, StringComparer.OrdinalIgnoreCase);
            foreach (var path in counts.Keys)
            {
                paths.Add(path);
            }

            foreach (var path in paths
                         .OrderBy(path => path.Length == 0 ? 0 : 1)
                         .ThenBy(path => path, StringComparer.CurrentCultureIgnoreCase))
            {
                var displayName = string.IsNullOrEmpty(path)
                    ? "未分类"
                    : path.Replace('\\', '/');
                var count = counts.GetValueOrDefault(path);
                GroupOptions.Add(new GroupOption(displayName, path, count));
            }

            _selectedGroup = GroupOptions.FirstOrDefault(option =>
                                 string.Equals(option.RelativePath, previousPath, StringComparison.OrdinalIgnoreCase))
                             ?? GroupOptions[0];
            this.RaisePropertyChanged(nameof(SelectedGroup));
        }
        finally
        {
            _suppressFilterApply = false;
        }
    }

    private void EnsureCurrentFolderValid()
    {
        while (_currentFolder.Length > 0 &&
               !_libraryFolders.Any(folder =>
                   string.Equals(folder, _currentFolder, StringComparison.OrdinalIgnoreCase)))
        {
            _currentFolder = GetParentFolderPath(_currentFolder) ?? string.Empty;
        }
    }

    private void ApplyFilter()
    {
        if (_suppressFilterApply)
        {
            return;
        }

        var search = SearchText.Trim();

        IEnumerable<ModItem> query = _allMods.Where(item =>
            string.Equals(item.GroupPath, _currentFolder, StringComparison.OrdinalIgnoreCase));

        if (SelectedEnabledFilter?.Value == ModEnabledFilter.Enabled)
        {
            query = query.Where(item => item.IsEnabled);
        }
        else if (SelectedEnabledFilter?.Value == ModEnabledFilter.Disabled)
        {
            query = query.Where(item => !item.IsEnabled);
        }

        if (SelectedDeploymentFilter?.Value == ModDeploymentFilter.Deployed)
        {
            query = query.Where(item => item.DeploymentState == ModDeploymentState.Deployed);
        }
        else if (SelectedDeploymentFilter?.Value == ModDeploymentFilter.NotDeployed)
        {
            query = query.Where(item => item.DeploymentState != ModDeploymentState.Deployed);
        }

        if (search.Length > 0)
        {
            query = query.Where(item =>
                item.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                item.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        var sortedMods = SortMods(query).ToArray();

        Mods.Clear();
        foreach (var item in sortedMods)
        {
            Mods.Add(item);
        }

        BuildCurrentFolderRows(search, sortedMods);

        SelectedMods.Clear();
        UpdateCounts();
        UpdateStates();
    }

    private void BuildCurrentFolderRows(string search, IReadOnlyList<ModItem> sortedMods)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        var thumbnailToken = _thumbnailCts.Token;

        var previousRows = Rows.ToArray();
        Rows.Clear();
        foreach (var row in previousRows)
        {
            row.DisposeThumbnail();
        }

        var childFolders = _libraryFolders
            .Where(path => string.Equals(
                GetParentFolderPath(path) ?? string.Empty,
                _currentFolder,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => search.Length == 0 ||
                           Path.GetFileName(path).Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        foreach (var folder in childFolders)
        {
            Rows.Add(FileRow.Folder(
                Path.GetFileName(folder),
                folder,
                CountModsInFolder(folder),
                CountChildFolders(folder),
                _thumbnails.FolderImage));
        }

        foreach (var mod in sortedMods)
        {
            var row = FileRow.ForMod(mod, _thumbnails.PlaceholderImage);
            Rows.Add(row);
            _ = LoadThumbnailAsync(row, mod, thumbnailToken);
        }
    }

    private async Task LoadThumbnailAsync(
        FileRow row,
        ModItem mod,
        CancellationToken cancellationToken)
    {
        try
        {
            var bitmap = await Task.Run(
                    () => _thumbnails.LoadAsync(mod.Record, mod.FullPath, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);

            if (bitmap is null)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested || !Rows.Contains(row))
            {
                bitmap.Dispose();
                return;
            }

            row.SetThumbnail(bitmap);
        }
        catch (OperationCanceledException)
        {
            // 切换目录后取消旧缩略图加载。
        }
        catch (Exception ex)
        {
            _log.Warn($"加载缩略图失败：{mod.RelativePath}（{ex.Message}）");
        }
    }

    private int CountModsInFolder(string relativePath)
    {
        var prefix = relativePath + Path.DirectorySeparatorChar;
        return _allMods.Count(item =>
            string.Equals(item.GroupPath, relativePath, StringComparison.OrdinalIgnoreCase) ||
            item.GroupPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private int CountChildFolders(string relativePath) =>
        _libraryFolders.Count(path => string.Equals(
            GetParentFolderPath(path) ?? string.Empty,
            relativePath,
            StringComparison.OrdinalIgnoreCase));

    private IEnumerable<ModItem> SortMods(IEnumerable<ModItem> mods)
    {
        var baseline = mods
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        return _sortField switch
        {
            "Size" => _sortDescending
                ? baseline.OrderByDescending(item => item.Size)
                : baseline.OrderBy(item => item.Size),
            "Modified" => _sortDescending
                ? baseline.OrderByDescending(item => item.LastModifiedUtc)
                : baseline.OrderBy(item => item.LastModifiedUtc),
            _ => _sortDescending
                ? baseline.OrderByDescending(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                : baseline.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        };
    }

    private void UpdateCounts()
    {
        ModCount = _allMods.Count;
        EnabledCount = _allMods.Count(item => item.IsEnabled);
        DeployedCount = _allMods.Count(item => item.DeploymentState == ModDeploymentState.Deployed);
        PendingCount = _allMods.Count(item =>
            item.IsEnabled && item.DeploymentState != ModDeploymentState.Deployed);
    }

    private void UpdateStates()
    {
        this.RaisePropertyChanged(nameof(WindowTitle));
        this.RaisePropertyChanged(nameof(ShowWelcome));
        this.RaisePropertyChanged(nameof(HasSelection));
        this.RaisePropertyChanged(nameof(CanModify));
        this.RaisePropertyChanged(nameof(CanImport));
        this.RaisePropertyChanged(nameof(CanPush));
        this.RaisePropertyChanged(nameof(CanRefresh));
        this.RaisePropertyChanged(nameof(CanOpenSettings));
        this.RaisePropertyChanged(nameof(IsFilterActive));
        this.RaisePropertyChanged(nameof(FilterSummary));
        this.RaisePropertyChanged(nameof(IsEmptyLibrary));
        this.RaisePropertyChanged(nameof(HasNoResults));
        this.RaisePropertyChanged(nameof(IsCurrentFolderEmpty));
        this.RaisePropertyChanged(nameof(ShowDetailsHeader));
        this.RaisePropertyChanged(nameof(CurrentFolderDisplay));
        this.RaisePropertyChanged(nameof(NoResultsTitle));
        this.RaisePropertyChanged(nameof(NoResultsHint));
        this.RaisePropertyChanged(nameof(NoResultsActionText));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(SelectionStatus));
        this.RaisePropertyChanged(nameof(ModSummary));
    }

    public string GetFullPath(string relativePath) => _library.GetFullPath(relativePath);

    private string GetImportTargetGroup() => _currentFolder;

    private CancellationToken BeginOperation(string text, bool indeterminate, bool canCancel = true)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        IsBusy = true;
        BusyText = text;
        IsProgressIndeterminate = indeterminate;
        ProgressValue = 0;
        CanCancel = canCancel;
        UpdateStates();

        return _operationCts.Token;
    }

    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;

        IsBusy = false;
        BusyText = string.Empty;
        CanCancel = false;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        UpdateStates();
    }

    private string BuildHeader(string label, string field)
    {
        if (!string.Equals(_sortField, field, StringComparison.Ordinal))
        {
            return label;
        }

        return _sortDescending ? $"{label} ↓" : $"{label} ↑";
    }

    private void Fail(string message, Exception? exception = null)
    {
        if (exception is not null)
        {
            _log.Error(exception, message);
        }

        Message = message;
        ErrorRaised?.Invoke(message);
    }
}

