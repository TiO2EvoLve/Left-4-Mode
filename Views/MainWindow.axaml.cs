using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Left_4_Mod.Models;
using Left_4_Mod.ViewModels;
using SukiUI.Controls;
using SukiUI.MessageBox;

namespace Left_4_Mod.Views;

public partial class MainWindow : SukiWindow
{
    /// <summary>应用内部拖放使用的格式（仅在本进程内传递路径，避免依赖系统剪贴板格式）。</summary>
    private static readonly DataFormat<string[]> InternalPathsFormat =
        DataFormat.CreateInProcessFormat<string[]>("l4mod-internal-paths");

    private MainViewModel? _viewModel;
    private Point _dragOrigin;
    private PointerPressedEventArgs? _dragPressedArgs;
    private FileSystemEntry? _dragSourceEntry;
    private bool _dragArmed;
    private bool _dialogOpen;

    public MainWindow()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(FileList, true);
        FileList.AddHandler(DragDrop.DragOverEvent, OnFileListDragOver);
        FileList.AddHandler(DragDrop.DropEvent, OnFileListDrop);

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += OnWindowOpened;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ErrorRaised -= OnViewModelError;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.ErrorRaised += OnViewModelError;
        }
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        await _viewModel.InitializeAsync();

        // 若启动时通过命令行传入了文件夹，则直接打开；否则弹出选择文件夹对话框。
        if (_viewModel.StartupFolder is { } startupFolder &&
            await _viewModel.OpenFolderAsync(startupFolder))
        {
            FileList.Focus();
            return;
        }

        await PickAndOpenFolderAsync();
    }

    // =====================================================================
    // 选择文件夹
    // =====================================================================

    private async Task PickAndOpenFolderAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        var folder = await PickFolderAsync("选择要浏览的文件夹");
        if (folder is null)
        {
            return;
        }

        if (await _viewModel.OpenFolderAsync(folder))
        {
            FileList.Focus();
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var storage = StorageProvider;
        if (storage is null || !storage.CanPickFolder)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        };

        try
        {
            if (_viewModel?.CurrentFolder is { } currentFolder &&
                await storage.TryGetFolderFromPathAsync(currentFolder) is { } startFolder)
            {
                options.SuggestedStartLocation = startFolder;
            }
        }
        catch (Exception)
        {
            // 起始位置无效时忽略，不影响选择。
        }

        var folders = await storage.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    // =====================================================================
    // 工具栏 / 导航
    // =====================================================================

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        await PickAndOpenFolderAsync();
    }

    private async void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.NavigateBackAsync();
        }
    }

    private async void OnForwardClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.NavigateForwardAsync();
        }
    }

    private async void OnUpClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.NavigateUpAsync();
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.RefreshAsync();
        }
    }

    private async void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { Tag: string path })
        {
            await _viewModel.OpenFolderAsync(path);
        }
    }

    private async void OnQuickPlaceClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { Tag: string path })
        {
            await _viewModel.OpenFolderAsync(path);
        }
    }

    private async void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _viewModel.OpenFolderAsync(box.Text ?? string.Empty);
            box.Text = _viewModel.CurrentFolder ?? string.Empty;
            FileList.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            box.Text = _viewModel.CurrentFolder ?? string.Empty;
            FileList.Focus();
        }
    }

    // =====================================================================
    // 搜索 / 排序
    // =====================================================================

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SearchText = SearchBox.Text ?? string.Empty;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SearchBox.Text = string.Empty;
            FileList.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FileList.Focus();
        }
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        _viewModel?.ClearSearch();
        FileList.Focus();
    }

    private void OnSortNameClick(object? sender, RoutedEventArgs e) => _viewModel?.SetSort("Name");

    private void OnSortModifiedClick(object? sender, RoutedEventArgs e) => _viewModel?.SetSort("Modified");

    private void OnSortTypeClick(object? sender, RoutedEventArgs e) => _viewModel?.SetSort("Type");

    private void OnSortSizeClick(object? sender, RoutedEventArgs e) => _viewModel?.SetSort("Size");

    // =====================================================================
    // 列表：选择、打开、拖放
    // =====================================================================

    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _viewModel?.SetSelection(FileList.SelectedItems?.OfType<FileSystemEntry>() ?? []);
    }

    private async void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null || _dialogOpen)
        {
            return;
        }

        var entry = GetEntryFromSource(e.Source);
        if (entry is null || entry.IsRenaming)
        {
            return;
        }

        await _viewModel.OpenEntryAsync(entry);
    }

    private void OnFileListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragArmed = false;
        _dragSourceEntry = null;
        _dragPressedArgs = null;

        if (_dialogOpen)
        {
            return;
        }

        var point = e.GetCurrentPoint(FileList);

        if (point.Properties.IsRightButtonPressed)
        {
            // 与资源管理器一致：右键未选中的项目时，先选中它。
            var rightClicked = GetEntryAt(e.GetPosition(FileList));
            if (rightClicked is not null &&
                _viewModel is not null &&
                !_viewModel.SelectedEntries.Contains(rightClicked))
            {
                SelectEntry(rightClicked);
            }

            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual source && source.FindAncestorOfType<TextBox>() is not null)
        {
            return;
        }

        var position = e.GetPosition(FileList);
        var entry = GetEntryAt(position);
        if (entry is null)
        {
            return;
        }

        _dragOrigin = position;
        _dragSourceEntry = entry;
        _dragPressedArgs = e;
        _dragArmed = true;
    }

    private async void OnFileListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || _dragPressedArgs is null || _dragSourceEntry is null || _viewModel is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
        {
            _dragArmed = false;
            return;
        }

        var position = e.GetPosition(FileList);
        if (Math.Abs(position.X - _dragOrigin.X) < 6 && Math.Abs(position.Y - _dragOrigin.Y) < 6)
        {
            return;
        }

        _dragArmed = false;

        var selection = _viewModel.SelectedEntries;
        var paths = selection.Count > 0 && selection.Contains(_dragSourceEntry)
            ? selection.Select(item => item.FullPath).ToArray()
            : [_dragSourceEntry.FullPath];

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(InternalPathsFormat, paths));

        try
        {
            await DragDrop.DoDragDropAsync(_dragPressedArgs, transfer, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // 拖放被取消时忽略。
        }
    }

    private void OnFileListDragOver(object? sender, DragEventArgs e)
    {
        var hasInternal = e.DataTransfer.Contains(InternalPathsFormat);
        var hasExternalFiles = e.DataTransfer.TryGetFiles() is { Length: > 0 };

        e.DragEffects = hasInternal
            ? DragDropEffects.Move
            : hasExternalFiles
                ? DragDropEffects.Copy
                : DragDropEffects.None;
    }

    private async void OnFileListDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null || _dialogOpen)
        {
            return;
        }

        var targetEntry = GetEntryAt(e.GetPosition(FileList));
        var destination = targetEntry is { IsFolder: true }
            ? targetEntry.FullPath
            : _viewModel.CurrentFolder;

        if (destination is null)
        {
            return;
        }

        if (e.DataTransfer.Contains(InternalPathsFormat))
        {
            var paths = e.DataTransfer.TryGetValue(InternalPathsFormat);
            if (paths is { Length: > 0 })
            {
                await _viewModel.MoveIntoAsync(destination, paths);
            }

            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is { Length: > 0 })
        {
            var paths = files
                .Select(item => item.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();

            if (paths.Length > 0)
            {
                await _viewModel.CopyIntoAsync(destination, paths);
            }
        }
    }

    // =====================================================================
    // 右键菜单 / 文件操作
    // =====================================================================

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (_viewModel is null || sender is not ContextMenu menu || _dialogOpen)
        {
            if (_dialogOpen)
            {
                e.Cancel = true;
            }

            return;
        }

        var hasSelection = _viewModel.HasSelection;

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = item.Tag switch
            {
                "open" or "reveal" or "rename" or "delete" or "copy" or "cut" or "moveTo"
                    => hasSelection,
                "paste" => _viewModel.ClipboardStatus.Length > 0 && _viewModel.HasFolder,
                "newFolder" or "newFile" or "refresh" => _viewModel.HasFolder,
                _ => true,
            };
        }
    }

    private async void OnOpenEntryClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var entry = _viewModel.SelectedEntries.FirstOrDefault();
        if (entry is not null)
        {
            await _viewModel.OpenEntryAsync(entry);
        }
    }

    private void OnRevealInExplorerClick(object? sender, RoutedEventArgs e)
    {
        var entry = _viewModel?.SelectedEntries.FirstOrDefault();
        if (entry is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.FullPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _viewModel?.ReportStatus($"无法打开资源管理器：{ex.Message}");
        }
    }

    private async void OnNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.HasFolder)
        {
            return;
        }

        var entry = await _viewModel.CreateFolderAsync();
        if (entry is not null)
        {
            SelectEntry(entry);
            BeginRename(entry);
        }
    }

    private async void OnNewTextFileClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.HasFolder)
        {
            return;
        }

        var entry = await _viewModel.CreateTextFileAsync();
        if (entry is not null)
        {
            SelectEntry(entry);
            BeginRename(entry);
        }
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => _viewModel?.CopySelection();

    private void OnCutClick(object? sender, RoutedEventArgs e) => _viewModel?.CutSelection();

    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.PasteAsync();
        }
    }

    private async void OnMoveToClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.HasSelection)
        {
            return;
        }

        var destination = await PickFolderAsync("选择要移动到的文件夹");
        if (destination is null)
        {
            return;
        }

        await _viewModel.MoveSelectedToAsync(destination);
    }

    private void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        var entry = _viewModel?.SelectedEntries.FirstOrDefault();
        if (entry is not null)
        {
            BeginRename(entry);
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.HasSelection)
        {
            return;
        }

        var selection = _viewModel.SelectedEntries.ToArray();
        var count = selection.Length;
        var preview = string.Join(
            Environment.NewLine,
            selection.Take(6).Select(item => "  • " + item.Name));

        if (count > 6)
        {
            preview += Environment.NewLine + $"  … 以及另外 {count - 6} 项";
        }

        var message = $"确定要把以下 {count} 个项目移到回收站吗？{Environment.NewLine}{Environment.NewLine}" +
                      $"{preview}{Environment.NewLine}{Environment.NewLine}之后可以从回收站还原。";

        if (!await ConfirmAsync(message, "删除", "确认删除"))
        {
            return;
        }

        await _viewModel.DeleteSelectedAsync();
    }

    // =====================================================================
    // 行内重命名
    // =====================================================================

    private void BeginRename(FileSystemEntry entry)
    {
        if (_viewModel is null)
        {
            return;
        }

        foreach (var item in _viewModel.Entries)
        {
            if (!ReferenceEquals(item, entry) && item.IsRenaming)
            {
                item.CancelRename();
            }
        }

        entry.BeginRename();
        FileList.ScrollIntoView(entry);

        // 等布局完成后再聚焦输入框（新建的行可能还没生成容器）。
        Dispatcher.UIThread.Post(() => FocusRenameBox(entry), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => FocusRenameBox(entry), DispatcherPriority.Background);
    }

    private void FocusRenameBox(FileSystemEntry entry)
    {
        if (FileList.ContainerFromItem(entry) is not Control container)
        {
            return;
        }

        var textBox = container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (textBox is null || !textBox.IsVisible)
        {
            return;
        }

        textBox.Focus();
        textBox.SelectAll();
    }

    private void OnRenameBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileSystemEntry { IsRenaming: true } entry } textBox)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (textBox.IsVisible)
            {
                textBox.Focus();
                textBox.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    private async void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileSystemEntry entry } textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync(entry, textBox.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            entry.CancelRename();
            FileList.Focus();
        }
    }

    private async void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileSystemEntry entry } textBox || !entry.IsRenaming)
        {
            return;
        }

        await CommitRenameAsync(entry, textBox.Text);
    }

    private async Task CommitRenameAsync(FileSystemEntry entry, string? text)
    {
        if (_viewModel is null || !entry.IsRenaming)
        {
            return;
        }

        var newName = (text ?? string.Empty).Trim();
        if (newName.Length == 0)
        {
            entry.CancelRename();
            return;
        }

        var renamed = await _viewModel.RenameAsync(entry, newName);
        if (renamed is null)
        {
            entry.CancelRename();
            return;
        }

        SelectEntry(renamed);
    }

    private void SelectEntry(FileSystemEntry entry)
    {
        if (FileList.SelectedItems is { } selectedItems)
        {
            selectedItems.Clear();
            selectedItems.Add(entry);
        }

        _viewModel?.SetSelection([entry]);
        FileList.ScrollIntoView(entry);
    }

    // =====================================================================
    // 快捷键
    // =====================================================================

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || _dialogOpen)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var focusedIsTextInput = FocusManager?.GetFocusedElement() is TextBox;

        if (control && e.Key == Key.O)
        {
            e.Handled = true;
            await PickAndOpenFolderAsync();
            return;
        }

        if (control && shift && e.Key == Key.N)
        {
            e.Handled = true;
            OnNewFolderClick(this, new RoutedEventArgs());
            return;
        }

        if (control && !focusedIsTextInput)
        {
            switch (e.Key)
            {
                case Key.C:
                    e.Handled = true;
                    _viewModel.CopySelection();
                    return;
                case Key.X:
                    e.Handled = true;
                    _viewModel.CutSelection();
                    return;
                case Key.V:
                    e.Handled = true;
                    await _viewModel.PasteAsync();
                    return;
                case Key.A:
                    e.Handled = true;
                    FileList.SelectAll();
                    return;
            }
        }

        if (focusedIsTextInput)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F2:
                e.Handled = true;
                OnRenameClick(this, new RoutedEventArgs());
                return;
            case Key.Delete:
                e.Handled = true;
                OnDeleteClick(this, new RoutedEventArgs());
                return;
            case Key.F5:
                e.Handled = true;
                await _viewModel.RefreshAsync();
                return;
            case Key.Enter:
                e.Handled = true;
                await OpenFirstSelectedAsync();
                return;
            case Key.Back:
                e.Handled = true;
                await _viewModel.NavigateUpAsync();
                return;
            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    e.Handled = true;
                    SearchBox.Text = string.Empty;
                    _viewModel.SearchText = string.Empty;
                }
                else if (_viewModel.HasSelection)
                {
                    e.Handled = true;
                    FileList.SelectedItems?.Clear();
                    _viewModel.SetSelection([]);
                }

                return;
        }

        if (alt && e.Key == Key.Left)
        {
            e.Handled = true;
            await _viewModel.NavigateBackAsync();
        }
        else if (alt && e.Key == Key.Right)
        {
            e.Handled = true;
            await _viewModel.NavigateForwardAsync();
        }
    }

    private async Task OpenFirstSelectedAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        var entry = _viewModel.SelectedEntries.FirstOrDefault();
        if (entry is not null)
        {
            await _viewModel.OpenEntryAsync(entry);
        }
    }

    // =====================================================================
    // 辅助方法
    // =====================================================================

    private FileSystemEntry? GetEntryFromSource(object? source)
    {
        return source is Visual visual
            ? visual.FindAncestorOfType<ListBoxItem>()?.DataContext as FileSystemEntry
            : null;
    }

    private FileSystemEntry? GetEntryAt(Point position)
    {
        return FileList.InputHitTest(position) is Visual visual
            ? visual.FindAncestorOfType<ListBoxItem>()?.DataContext as FileSystemEntry
            : null;
    }

    private async Task<bool> ConfirmAsync(string message, string title, string header)
    {
        if (_dialogOpen || !IsLoaded)
        {
            return false;
        }

        _dialogOpen = true;
        try
        {
            var result = await SukiMessageBox.ShowDialogResult(
                this,
                message,
                SukiMessageBoxButtons.YesNo,
                title,
                header,
                SukiMessageBoxIcons.Warning,
                null!);

            return result == SukiMessageBoxResult.Yes;
        }
        catch (Exception ex)
        {
            _viewModel?.ReportStatus($"无法显示确认对话框：{ex.Message}");
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async void OnViewModelError(string message)
    {
        if (_dialogOpen || !IsLoaded)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            await SukiMessageBox.ShowDialogResult(
                this,
                message,
                SukiMessageBoxButtons.OK,
                "操作失败",
                "无法完成操作",
                SukiMessageBoxIcons.Error,
                null!);
        }
        catch (Exception)
        {
            // 弹窗失败时错误信息已经写入状态栏。
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
