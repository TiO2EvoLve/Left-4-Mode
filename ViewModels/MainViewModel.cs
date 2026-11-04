using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Left_4_Mod.Models;
using Left_4_Mod.Services;
using ReactiveUI;

namespace Left_4_Mod.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly List<FileSystemEntry> _allEntries = [];
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly List<string> _clipboardPaths = [];

    private string? _currentFolder;
    private string _addressText = string.Empty;
    private string _searchText = string.Empty;
    private bool _isLoading;
    private string _sortField = "Name";
    private bool _sortDescending;
    private string _message = string.Empty;
    private bool _clipboardIsCut;

    public MainViewModel()
    {
        QuickPlaces.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasQuickPlaces));
    }

    public ObservableCollection<FileSystemEntry> Entries { get; } = [];

    public ObservableCollection<FileSystemEntry> SelectedEntries { get; } = [];

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public ObservableCollection<QuickPlace> QuickPlaces { get; } = [];

    /// <summary>发生需要弹窗提示的错误时触发。</summary>
    public event Action<string>? ErrorRaised;

    public string? CurrentFolder => _currentFolder;

    /// <summary>启动参数中指定的文件夹（例如通过“打开方式”传入）。</summary>
    public string? StartupFolder { get; set; }

    public bool HasFolder => _currentFolder is not null;

    public bool HasQuickPlaces => QuickPlaces.Count > 0;

    public string AddressText
    {
        get => _addressText;
        private set => this.RaiseAndSetIfChanged(ref _addressText, value);
    }

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
            ApplyView();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            this.RaisePropertyChanged();
            UpdateStates();
        }
    }

    public bool CanGoBack => _backStack.Count > 0;

    public bool CanGoForward => _forwardStack.Count > 0;

    public bool CanGoUp => _currentFolder is not null && Directory.GetParent(_currentFolder) is not null;

    public bool HasSelection => SelectedEntries.Count > 0;

    public bool ShowWelcome => _currentFolder is null;

    public bool IsEmptyFolder =>
        _currentFolder is not null &&
        !IsLoading &&
        Entries.Count == 0 &&
        string.IsNullOrWhiteSpace(SearchText);

    public bool HasNoResults =>
        _currentFolder is not null &&
        !IsLoading &&
        Entries.Count == 0 &&
        !string.IsNullOrWhiteSpace(SearchText);

    public string WindowTitle =>
        _currentFolder is null
            ? "L4D2 模组管理器"
            : $"{GetFolderDisplayName(_currentFolder)} — L4D2 模组管理器";

    public string StatusText
    {
        get
        {
            if (_currentFolder is null)
            {
                return "未选择文件夹";
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                return $"{Entries.Count} 个匹配项（共 {_allEntries.Count} 项）";
            }

            return $"{Entries.Count} 个项目";
        }
    }

    public string SelectionStatus
    {
        get
        {
            if (SelectedEntries.Count == 0)
            {
                return string.Empty;
            }

            var files = SelectedEntries.Where(e => !e.IsFolder).ToArray();
            if (files.Length == 0)
            {
                return $"已选择 {SelectedEntries.Count} 项";
            }

            var total = files.Sum(e => e.Size);
            return $"已选择 {SelectedEntries.Count} 项 · {FileSystemService.FormatSize(total)}";
        }
    }

    public string Message
    {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public string ClipboardStatus
    {
        get
        {
            if (_clipboardPaths.Count == 0)
            {
                return string.Empty;
            }

            return _clipboardIsCut
                ? $"剪贴板：已剪切 {_clipboardPaths.Count} 项"
                : $"剪贴板：已复制 {_clipboardPaths.Count} 项";
        }
    }

    public string SortField => _sortField;

    public bool SortDescending => _sortDescending;

    public string NameHeader => BuildHeader("名称", "Name");

    public string ModifiedHeader => BuildHeader("修改日期", "Modified");

    public string TypeHeader => BuildHeader("类型", "Type");

    public string SizeHeader => BuildHeader("大小", "Size");

    public async Task InitializeAsync()
    {
        if (QuickPlaces.Count > 0)
        {
            return;
        }

        var places = await Task.Run(FileSystemService.BuildQuickPlaces).ConfigureAwait(true);
        foreach (var place in places)
        {
            QuickPlaces.Add(place);
        }
    }

    public async Task<bool> OpenFolderAsync(string path, bool addHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            RaiseError($"路径无效：{ex.Message}");
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            RaiseError($"找不到文件夹：{fullPath}");
            return false;
        }

        if (_currentFolder is not null && FileSystemService.PathsEqual(_currentFolder, fullPath))
        {
            await RefreshAsync();
            return true;
        }

        IsLoading = true;
        try
        {
            var entries = await Task.Run(() => FileSystemService.ReadEntries(fullPath)).ConfigureAwait(true);

            if (addHistory && _currentFolder is not null)
            {
                _backStack.Push(_currentFolder);
                _forwardStack.Clear();
            }

            _allEntries.Clear();
            _allEntries.AddRange(entries);

            SetCurrentFolder(fullPath);
            ApplyView();
            Message = $"已打开“{GetFolderDisplayName(fullPath)}”";
            return true;
        }
        catch (Exception ex)
        {
            RaiseError($"无法打开文件夹：{ex.Message}");
            return false;
        }
        finally
        {
            IsLoading = false;
            UpdateStates();
        }
    }

    public async Task RefreshAsync()
    {
        if (_currentFolder is null)
        {
            return;
        }

        var path = _currentFolder;
        IsLoading = true;
        try
        {
            var entries = await Task.Run(() => FileSystemService.ReadEntries(path)).ConfigureAwait(true);
            _allEntries.Clear();
            _allEntries.AddRange(entries);
            ApplyView();
            Message = "已刷新";
        }
        catch (Exception ex)
        {
            RaiseError($"刷新失败：{ex.Message}");
        }
        finally
        {
            IsLoading = false;
            UpdateStates();
        }
    }

    public async Task<bool> NavigateBackAsync()
    {
        if (_backStack.Count == 0)
        {
            return false;
        }

        var target = _backStack.Peek();
        var previous = _currentFolder;

        if (!await OpenFolderAsync(target, addHistory: false).ConfigureAwait(true))
        {
            return false;
        }

        _backStack.Pop();
        if (previous is not null)
        {
            _forwardStack.Push(previous);
        }

        UpdateStates();
        return true;
    }

    public async Task<bool> NavigateForwardAsync()
    {
        if (_forwardStack.Count == 0)
        {
            return false;
        }

        var target = _forwardStack.Peek();
        var previous = _currentFolder;

        if (!await OpenFolderAsync(target, addHistory: false).ConfigureAwait(true))
        {
            return false;
        }

        _forwardStack.Pop();
        if (previous is not null)
        {
            _backStack.Push(previous);
        }

        UpdateStates();
        return true;
    }

    public async Task<bool> NavigateUpAsync()
    {
        if (_currentFolder is null)
        {
            return false;
        }

        var parent = Directory.GetParent(_currentFolder);
        if (parent is null)
        {
            Message = "已经到达根目录。";
            return false;
        }

        return await OpenFolderAsync(parent.FullName).ConfigureAwait(true);
    }

    public void NavigateTo(string path)
    {
        _ = OpenFolderAsync(path);
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

        this.RaisePropertyChanged(nameof(SortField));
        this.RaisePropertyChanged(nameof(SortDescending));
        this.RaisePropertyChanged(nameof(NameHeader));
        this.RaisePropertyChanged(nameof(ModifiedHeader));
        this.RaisePropertyChanged(nameof(TypeHeader));
        this.RaisePropertyChanged(nameof(SizeHeader));

        ApplyView();
    }

    public void SetSelection(IEnumerable<FileSystemEntry> entries)
    {
        SelectedEntries.Clear();
        foreach (var entry in entries)
        {
            SelectedEntries.Add(entry);
        }

        this.RaisePropertyChanged(nameof(HasSelection));
        this.RaisePropertyChanged(nameof(SelectionStatus));
    }

    public void SelectAllVisible()
    {
        SetSelection(Entries);
    }

    public async Task<FileSystemEntry?> CreateFolderAsync()
    {
        if (_currentFolder is null)
        {
            return null;
        }

        try
        {
            var path = FileSystemService.GetUniquePath(_currentFolder, "新建文件夹");
            Directory.CreateDirectory(path);
            await RefreshEntriesAsync();
            Message = "已创建文件夹，输入名称后按回车确认。";
            return Entries.FirstOrDefault(entry => FileSystemService.PathsEqual(entry.FullPath, path));
        }
        catch (Exception ex)
        {
            RaiseError($"新建文件夹失败：{ex.Message}");
            return null;
        }
    }

    public async Task<FileSystemEntry?> CreateTextFileAsync()
    {
        if (_currentFolder is null)
        {
            return null;
        }

        try
        {
            var path = FileSystemService.GetUniquePath(_currentFolder, "新建文本文档.txt");
            await File.WriteAllTextAsync(path, string.Empty).ConfigureAwait(true);
            await RefreshEntriesAsync();
            Message = "已创建文本文档，输入名称后按回车确认。";
            return Entries.FirstOrDefault(entry => FileSystemService.PathsEqual(entry.FullPath, path));
        }
        catch (Exception ex)
        {
            RaiseError($"新建文件失败：{ex.Message}");
            return null;
        }
    }

    public async Task<FileSystemEntry?> RenameAsync(FileSystemEntry entry, string newName)
    {
        newName = newName.Trim();

        if (!FileSystemService.IsValidEntryName(newName, out var error))
        {
            RaiseError(error);
            return null;
        }

        var directory = Path.GetDirectoryName(entry.FullPath);
        if (directory is null)
        {
            RaiseError("无法重命名该位置。");
            return null;
        }

        var destination = Path.Combine(directory, newName);

        if (string.Equals(newName, entry.Name, StringComparison.Ordinal))
        {
            entry.CancelRename();
            return entry;
        }

        if (FileSystemService.PathsEqual(destination, entry.FullPath))
        {
            // 仅修改大小写：Windows 需要借助临时名称完成。
            try
            {
                var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                if (entry.IsFolder)
                {
                    Directory.Move(entry.FullPath, temporary);
                    Directory.Move(temporary, destination);
                }
                else
                {
                    File.Move(entry.FullPath, temporary);
                    File.Move(temporary, destination);
                }

                entry.CancelRename();
                await RefreshEntriesAsync();
                Message = $"已重命名为“{newName}”";
                return Entries.FirstOrDefault(item => FileSystemService.PathsEqual(item.FullPath, destination));
            }
            catch (Exception ex)
            {
                RaiseError($"重命名失败：{ex.Message}");
                return null;
            }
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            RaiseError($"当前文件夹中已存在名为“{newName}”的项目。");
            return null;
        }

        try
        {
            if (entry.IsFolder)
            {
                Directory.Move(entry.FullPath, destination);
            }
            else
            {
                File.Move(entry.FullPath, destination);
            }

            entry.CancelRename();
            await RefreshEntriesAsync();
            Message = $"已重命名为“{newName}”";
            return Entries.FirstOrDefault(item => FileSystemService.PathsEqual(item.FullPath, destination));
        }
        catch (Exception ex)
        {
            RaiseError($"重命名失败：{ex.Message}");
            return null;
        }
    }

    public async Task DeleteSelectedAsync()
    {
        var paths = SelectedEntries.Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0 || _currentFolder is null)
        {
            return;
        }

        try
        {
            var deletedFolder = _currentFolder;
            await FileSystemService.DeleteToRecycleBinAsync(paths).ConfigureAwait(true);

            Message = $"已将 {paths.Length} 个项目移到回收站（可从回收站还原）。";

            if (!Directory.Exists(deletedFolder))
            {
                // 当前显示的文件夹本身被删除时，回到其父目录。
                var parent = Directory.GetParent(deletedFolder)?.FullName
                             ?? Path.GetPathRoot(deletedFolder);
                if (parent is not null)
                {
                    await OpenFolderAsync(parent);
                    return;
                }
            }

            await RefreshEntriesAsync();
        }
        catch (Exception ex)
        {
            RaiseError($"删除失败：{ex.Message}");
        }
    }

    public void CopySelection()
    {
        var paths = SelectedEntries.Select(entry => entry.FullPath).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        _clipboardPaths.Clear();
        _clipboardPaths.AddRange(paths);
        _clipboardIsCut = false;

        foreach (var entry in _allEntries)
        {
            entry.IsCut = false;
        }

        Message = $"已复制 {paths.Count} 个项目。";
        UpdateStates();
    }

    public void CutSelection()
    {
        var paths = SelectedEntries.Select(entry => entry.FullPath).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        _clipboardPaths.Clear();
        _clipboardPaths.AddRange(paths);
        _clipboardIsCut = true;

        foreach (var entry in _allEntries)
        {
            entry.IsCut = paths.Contains(entry.FullPath, StringComparer.OrdinalIgnoreCase);
        }

        Message = $"已剪切 {paths.Count} 个项目，打开目标文件夹后粘贴即可移动。";
        UpdateStates();
    }

    public async Task PasteAsync()
    {
        if (_currentFolder is null || _clipboardPaths.Count == 0)
        {
            Message = "剪贴板为空。";
            return;
        }

        var destination = _currentFolder;
        var sources = _clipboardPaths.ToArray();
        var isCut = _clipboardIsCut;
        var completed = 0;
        var skipped = 0;

        try
        {
            await Task.Run(() =>
            {
                foreach (var source in sources)
                {
                    if (!File.Exists(source) && !Directory.Exists(source))
                    {
                        skipped++;
                        continue;
                    }

                    var sourceDirectory = Path.GetDirectoryName(source);

                    if (isCut && sourceDirectory is not null &&
                        FileSystemService.PathsEqual(sourceDirectory, destination))
                    {
                        continue;
                    }

                    if (FileSystemService.IsSameOrDescendant(destination, source))
                    {
                        // 不能把文件夹复制/移动到它自己或自己的子文件夹里。
                        skipped++;
                        continue;
                    }

                    var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));

                    if (isCut)
                    {
                        var target = Path.Combine(destination, name);
                        if (File.Exists(target) || Directory.Exists(target))
                        {
                            target = FileSystemService.GetUniquePath(destination, name);
                        }

                        FileSystemService.Move(source, target);
                    }
                    else
                    {
                        var target = FileSystemService.GetCopyPath(destination, name);
                        FileSystemService.Copy(source, target);
                    }

                    completed++;
                }
            }).ConfigureAwait(true);

            if (isCut)
            {
                _clipboardPaths.Clear();
                _clipboardIsCut = false;
                foreach (var entry in _allEntries)
                {
                    entry.IsCut = false;
                }
            }

            await RefreshEntriesAsync();
            Message = skipped > 0
                ? $"已粘贴 {completed} 个项目，跳过 {skipped} 个（源文件夹与目标位置冲突）。"
                : $"已粘贴 {completed} 个项目。";
        }
        catch (Exception ex)
        {
            RaiseError($"粘贴失败：{ex.Message}");
        }
        finally
        {
            UpdateStates();
        }
    }

    public async Task MoveSelectedToAsync(string destinationFolder)
    {
        var sources = SelectedEntries.Select(entry => entry.FullPath).ToArray();
        await MoveIntoAsync(destinationFolder, sources).ConfigureAwait(true);
    }

    /// <summary>把指定项目移动到目标文件夹（拖放、剪切粘贴、移动到…共用）。</summary>
    public async Task MoveIntoAsync(string destinationFolder, IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0 || !Directory.Exists(destinationFolder))
        {
            return;
        }

        var moved = 0;
        var skipped = 0;

        try
        {
            await Task.Run(() =>
            {
                foreach (var source in sourcePaths)
                {
                    if (!File.Exists(source) && !Directory.Exists(source))
                    {
                        skipped++;
                        continue;
                    }

                    var sourceDirectory = Path.GetDirectoryName(source);
                    if (sourceDirectory is not null &&
                        FileSystemService.PathsEqual(sourceDirectory, destinationFolder))
                    {
                        continue;
                    }

                    if (FileSystemService.IsSameOrDescendant(destinationFolder, source))
                    {
                        skipped++;
                        continue;
                    }

                    var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
                    var target = Path.Combine(destinationFolder, name);
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        target = FileSystemService.GetUniquePath(destinationFolder, name);
                    }

                    FileSystemService.Move(source, target);
                    moved++;
                }
            }).ConfigureAwait(true);

            await RefreshEntriesAsync();
            Message = skipped > 0
                ? $"已移动 {moved} 个项目，跳过 {skipped} 个（不能移动到自身内部）。"
                : $"已移动 {moved} 个项目。";
        }
        catch (Exception ex)
        {
            RaiseError($"移动失败：{ex.Message}");
        }
    }

    /// <summary>把外部拖入的文件/文件夹复制到当前文件夹。</summary>
    public async Task CopyIntoAsync(string destinationFolder, IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0 || !Directory.Exists(destinationFolder))
        {
            return;
        }

        var copied = 0;

        try
        {
            await Task.Run(() =>
            {
                foreach (var source in sourcePaths)
                {
                    if (!File.Exists(source) && !Directory.Exists(source))
                    {
                        continue;
                    }

                    if (FileSystemService.PathsEqual(Path.GetDirectoryName(source), destinationFolder))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
                    var target = FileSystemService.GetCopyPath(destinationFolder, name);
                    FileSystemService.Copy(source, target);
                    copied++;
                }
            }).ConfigureAwait(true);

            await RefreshEntriesAsync();
            Message = $"已复制 {copied} 个项目到当前文件夹。";
        }
        catch (Exception ex)
        {
            RaiseError($"复制失败：{ex.Message}");
        }
    }

    public async Task OpenEntryAsync(FileSystemEntry entry)
    {
        if (entry.IsFolder)
        {
            await OpenFolderAsync(entry.FullPath).ConfigureAwait(true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RaiseError($"无法打开“{entry.Name}”：{ex.Message}");
        }
    }

    public void ClearSearch()
    {
        SearchText = string.Empty;
    }

    public void ReportStatus(string message) => Message = message;

    private async Task RefreshEntriesAsync()
    {
        if (_currentFolder is null)
        {
            return;
        }

        var entries = await Task.Run(() => FileSystemService.ReadEntries(_currentFolder)).ConfigureAwait(true);
        _allEntries.Clear();
        _allEntries.AddRange(entries);
        ApplyView();
        UpdateStates();
    }

    private void SetCurrentFolder(string fullPath)
    {
        _currentFolder = fullPath;
        AddressText = fullPath;

        Breadcrumbs.Clear();
        foreach (var breadcrumb in FileSystemService.BuildBreadcrumbs(fullPath))
        {
            Breadcrumbs.Add(breadcrumb);
        }

        this.RaisePropertyChanged(nameof(CurrentFolder));
        this.RaisePropertyChanged(nameof(HasFolder));
        this.RaisePropertyChanged(nameof(WindowTitle));
        this.RaisePropertyChanged(nameof(CanGoUp));
        UpdateStates();
    }

    private void ApplyView()
    {
        IEnumerable<FileSystemEntry> query = _allEntries;

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(entry =>
                entry.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        var ordered = query
            .OrderByDescending(entry => entry.IsFolder)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase);

        var sorted = _sortField switch
        {
            "Modified" => _sortDescending
                ? ordered.ThenByDescending(entry => entry.Modified)
                : ordered.ThenBy(entry => entry.Modified),
            "Type" => _sortDescending
                ? ordered.ThenByDescending(entry => entry.TypeText, StringComparer.CurrentCultureIgnoreCase)
                : ordered.ThenBy(entry => entry.TypeText, StringComparer.CurrentCultureIgnoreCase),
            "Size" => _sortDescending
                ? ordered.ThenByDescending(entry => entry.Size)
                : ordered.ThenBy(entry => entry.Size),
            _ => _sortDescending
                ? ordered.ThenByDescending(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                : ordered.ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        Entries.Clear();
        foreach (var entry in sorted)
        {
            Entries.Add(entry);
        }

        SelectedEntries.Clear();
        this.RaisePropertyChanged(nameof(HasSelection));
        this.RaisePropertyChanged(nameof(SelectionStatus));
        UpdateStates();
    }

    private void UpdateStates()
    {
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(SelectionStatus));
        this.RaisePropertyChanged(nameof(HasSelection));
        this.RaisePropertyChanged(nameof(ShowWelcome));
        this.RaisePropertyChanged(nameof(IsEmptyFolder));
        this.RaisePropertyChanged(nameof(HasNoResults));
        this.RaisePropertyChanged(nameof(CanGoBack));
        this.RaisePropertyChanged(nameof(CanGoForward));
        this.RaisePropertyChanged(nameof(CanGoUp));
        this.RaisePropertyChanged(nameof(WindowTitle));
        this.RaisePropertyChanged(nameof(ClipboardStatus));
    }

    private string BuildHeader(string label, string field)
    {
        if (!string.Equals(_sortField, field, StringComparison.Ordinal))
        {
            return label;
        }

        return _sortDescending ? $"{label} ↓" : $"{label} ↑";
    }

    private void RaiseError(string message)
    {
        Message = message;
        ErrorRaised?.Invoke(message);
    }

    private static string GetFolderDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
