using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Left_4_Mod.Models;
using Left_4_Mod.ViewModels;
using SukiUI.Controls;
using SukiUI.MessageBox;

namespace Left_4_Mod.Views;

public partial class MainWindow : SukiWindow
{
    private MainViewModel? _viewModel;
    private bool _dialogOpen;
    private DispatcherTimer? _workshopTimer;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
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
        StartWorkshopTimer();

        if (_viewModel.HasLibrary)
        {
            await _viewModel.CheckWorkshopUpdatesAsync(automatic: true);
            ModList.Focus();
            return;
        }

        if (_viewModel.AutoPromptLibrary)
        {
            await PickLibraryAsync();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _workshopTimer?.Stop();
        _ = _viewModel?.SaveSettingsAsync();
    }

    private void StartWorkshopTimer()
    {
        _workshopTimer?.Stop();
        _workshopTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(30),
        };
        _workshopTimer.Tick += async (_, _) =>
        {
            if (_viewModel is { HasLibrary: true, IsBusy: false } && !_dialogOpen)
            {
                await _viewModel.CheckWorkshopUpdatesAsync(automatic: true);
            }
        };
        _workshopTimer.Start();
    }

    // =====================================================================
    // 模组库
    // =====================================================================

    private async Task PickLibraryAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        var folder = await PickFolderAsync("选择模组库文件夹", _viewModel.LibraryPath);
        if (folder is null)
        {
            return;
        }

        await _viewModel.SetLibraryAsync(folder);
        if (_viewModel.HasLibrary)
        {
            await _viewModel.CheckWorkshopUpdatesAsync(automatic: true);
            ModList.Focus();
        }
    }

    private async void OnSelectLibraryClick(object? sender, RoutedEventArgs e) => await PickLibraryAsync();

    private async Task<string?> PickFolderAsync(string title, string? currentPath)
    {
        var storage = StorageProvider;
        if (storage is null || !storage.CanPickFolder)
        {
            _viewModel?.ReportStatus("当前平台不支持选择文件夹。");
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(currentPath) &&
                await storage.TryGetFolderFromPathAsync(currentPath) is { } start)
            {
                options.SuggestedStartLocation = start;
            }
        }
        catch (Exception ex)
        {
            _viewModel?.ReportViewWarning($"无法设置选择框起始位置：{ex.Message}", ex);
        }

        try
        {
            var folders = await storage.OpenFolderPickerAsync(options);
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            _viewModel?.ReportViewError($"无法打开文件夹选择框：{ex.Message}", ex);
            return null;
        }
    }

    // =====================================================================
    // 设置
    // =====================================================================

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (await OpenSettingsAsync() &&
            _viewModel is { HasLibrary: true })
        {
            await _viewModel.CheckWorkshopUpdatesAsync(automatic: true);
        }
    }

    private async Task<bool> OpenSettingsAsync()
    {
        if (_viewModel is null)
        {
            return false;
        }

        var window = new SettingsWindow(_viewModel);
        return await window.ShowDialog<bool>(this);
    }

    // =====================================================================
    // 搜索、筛选、排序
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
            _viewModel?.ClearFilters();
            ModList.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ModList.Focus();
        }
    }

    private void OnClearFiltersClick(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        _viewModel?.ClearFilters();
        ModList.Focus();
    }

    private void OnSortNameClick(object? sender, RoutedEventArgs e) => _viewModel?.SetSort("Name");

    private void OnSortGroupClick(object? sender, RoutedEventArgs e) => _viewModel?.SetSort("Group");

    private void OnSortSizeClick(object? sender, RoutedEventArgs e) => _viewModel?.SetSort("Size");

    private void OnSortModifiedClick(object? sender, RoutedEventArgs e) => _viewModel?.SetSort("Modified");

    // =====================================================================
    // 列表操作
    // =====================================================================

    private void OnModSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _viewModel?.SetSelection(ModList.SelectedItems?.OfType<ModItem>() ?? []);
    }

    private async void OnEnableSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.SetSelectedEnabledAsync(true);
        }
    }

    private async void OnDisableSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.SetSelectedEnabledAsync(false);
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.RefreshAsync();
        }
    }

    private void OnRevealInExplorerClick(object? sender, RoutedEventArgs e)
    {
        var item = _viewModel?.SelectedMods.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _viewModel?.ReportViewError($"无法打开资源管理器：{ex.Message}", ex);
        }
    }

    private void OnCancelOperationClick(object? sender, RoutedEventArgs e) => _viewModel?.CancelCurrentOperation();

    // =====================================================================
    // 推送
    // =====================================================================

    private async void OnPushClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.CanPush)
        {
            return;
        }

        if (!_viewModel.IsGameReady)
        {
            await OpenSettingsAsync();
            if (!_viewModel.IsGameReady)
            {
                _viewModel.ReportStatus("请先在设置中配置 L4D2 游戏目录。");
                return;
            }
        }

        var plan = await _viewModel.CreateDeploymentPlanAsync();
        if (plan is null)
        {
            return;
        }

        if (!plan.HasChanges)
        {
            if (plan.ConflictCount > 0)
            {
                await ShowInfoAsync(BuildConflictSummary(plan), "无法推送");
            }
            else
            {
                await ShowInfoAsync("当前启用状态已经同步，没有需要推送的变更。", "推送到游戏");
            }

            return;
        }

        var summary = BuildPushSummary(plan);
        if (_viewModel.IsGameRunning())
        {
            summary += Environment.NewLine + Environment.NewLine +
                       "检测到 L4D2 正在运行。推送后可能需要在游戏内刷新模组或重启游戏。";
        }

        if (!await ConfirmAsync(summary, "推送到游戏", "确认推送"))
        {
            return;
        }

        var result = await _viewModel.PushAsync(plan);
        if (result is null)
        {
            return;
        }

        if (result.Failures.Count > 0)
        {
            var details = string.Join(
                Environment.NewLine,
                result.Failures.Take(10).Select(failure => $"• {failure.FileName}：{failure.Message}"));

            if (result.Failures.Count > 10)
            {
                details += Environment.NewLine + $"… 以及另外 {result.Failures.Count - 10} 项失败";
            }

            await ShowInfoAsync(
                $"推送完成：新增 {result.Added}，更新 {result.Updated}，移除 {result.Removed}，失败 {result.Failures.Count}。" +
                $"{Environment.NewLine}{Environment.NewLine}{details}",
                "推送结果");
        }
    }

    private string BuildPushSummary(DeploymentPlan plan)
    {
        var details = new List<string>
        {
            $"游戏 addons 目录：{plan.AddonsPath}",
            "推送方式：符号链接（不复制源文件）",
            string.Empty,
            $"新增：{plan.AddCount}    更新：{plan.UpdateCount}    移除：{plan.RemoveCount}    保持不变：{plan.KeepCount}",
        };

        if (plan.ConflictCount > 0)
        {
            details.Add(string.Empty);
            details.Add($"冲突：{plan.ConflictCount} 项（这些项目不会被覆盖，将跳过）");
            details.AddRange(BuildConflictSummary(plan)
                .Split(Environment.NewLine)
                .Where(line => line.StartsWith("•", StringComparison.Ordinal)));
        }

        details.Add(string.Empty);
        details.Add("只会操作本管理器登记的 l4m_*.vpk 文件，不会删除你手动放入 addons 的文件。");

        return string.Join(Environment.NewLine, details);
    }

    private static string BuildConflictSummary(DeploymentPlan plan)
    {
        var conflicts = plan.Items
            .Where(item => item.IsConflict)
            .Take(8)
            .Select(item => $"• {item.DisplayName}：{item.Message}")
            .ToArray();

        return conflicts.Length == 0
            ? "没有冲突。"
            : "以下项目存在冲突，已跳过：" + Environment.NewLine + string.Join(Environment.NewLine, conflicts);
    }

    // =====================================================================
    // 创意工坊
    // =====================================================================

    private async void OnWorkshopClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.CanImport)
        {
            return;
        }

        var window = new WorkshopWindow(_viewModel);
        await window.ShowDialog<bool>(this);
    }

    private async void OnCheckWorkshopUpdatesClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.CanRefresh)
        {
            return;
        }

        var result = await _viewModel.CheckWorkshopUpdatesAsync(automatic: false);
        if (result is null)
        {
            return;
        }

        if (result.Failures.Count > 0)
        {
            var details = string.Join(
                Environment.NewLine,
                result.Failures.Take(10).Select(failure =>
                    $"• {failure.Title}：{failure.Message}"));

            if (result.Failures.Count > 10)
            {
                details += Environment.NewLine + $"… 以及另外 {result.Failures.Count - 10} 项失败";
            }

            await ShowInfoAsync(
                $"已检查 {result.CheckedCount} 个工坊模组，更新 {result.Updated.Count} 个，失败 {result.Failures.Count} 个。" +
                $"{Environment.NewLine}{Environment.NewLine}{details}",
                "创意工坊更新检查");
        }
    }

    // =====================================================================
    // 导入
    // =====================================================================

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.CanImport)
        {
            return;
        }

        var sourceFiles = await PickVpkFilesAsync();
        if (sourceFiles.Count == 0)
        {
            return;
        }

        var plan = _viewModel.CreateImportPlan(sourceFiles);
        if (plan is null || plan.Items.Count == 0)
        {
            _viewModel.ReportStatus("没有可导入的 .vpk 文件（已在本目录中的文件会被跳过）。");
            return;
        }

        if (plan.RenamedCount > 0)
        {
            var message = $"有 {plan.RenamedCount} 个同名文件，将自动重命名后导入。是否继续？";
            if (!await ConfirmAsync(message, "导入模组", "发现同名文件"))
            {
                return;
            }
        }

        var outcome = await _viewModel.ImportAsync(plan);
        if (outcome is null)
        {
            return;
        }

        if (outcome.Failures.Count > 0)
        {
            var details = string.Join(
                Environment.NewLine,
                outcome.Failures.Take(10).Select(failure => $"• {failure.FileName}：{failure.Message}"));

            if (outcome.Failures.Count > 10)
            {
                details += Environment.NewLine + $"… 以及另外 {outcome.Failures.Count - 10} 个失败项";
            }

            await ShowInfoAsync(
                $"成功导入 {outcome.ImportedRelativePaths.Count} 个文件，失败 {outcome.Failures.Count} 个。" +
                $"{Environment.NewLine}{Environment.NewLine}{details}",
                "导入结果");
        }
    }

    private async Task<IReadOnlyList<string>> PickVpkFilesAsync()
    {
        var storage = StorageProvider;
        if (storage is null || !storage.CanOpen)
        {
            _viewModel?.ReportStatus("当前平台不支持选择文件。");
            return [];
        }

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入 VPK 模组",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("VPK 模组") { Patterns = ["*.vpk"] },
                ],
            });

            return files
                .Select(file => file.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();
        }
        catch (Exception ex)
        {
            _viewModel?.ReportViewError($"无法打开文件选择框：{ex.Message}", ex);
            return [];
        }
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
        var focusedIsTextInput = FocusManager?.GetFocusedElement() is TextBox;

        if (control && e.Key == Key.F)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }

        if (control && e.Key == Key.P)
        {
            e.Handled = true;
            OnPushClick(this, new RoutedEventArgs());
            return;
        }

        if (control && e.Key == Key.O)
        {
            e.Handled = true;
            await OpenSettingsAsync();
            return;
        }

        if (focusedIsTextInput)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F5:
                e.Handled = true;
                await _viewModel.RefreshAsync();
                return;
            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    e.Handled = true;
                    SearchBox.Text = string.Empty;
                    _viewModel.ClearFilters();
                }

                return;
        }
    }

    // =====================================================================
    // 右键菜单
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

        var hasSelection = _viewModel.HasSelection && !_viewModel.IsBusy;

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = item.Tag switch
            {
                "enable" or "disable" or "reveal" => hasSelection,
                "refresh" => _viewModel.CanRefresh,
                _ => true,
            };
        }
    }

    // =====================================================================
    // 对话框
    // =====================================================================

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
            _viewModel?.ReportViewWarning($"无法显示确认对话框：{ex.Message}", ex);
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task ShowInfoAsync(string message, string title)
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
                title,
                title,
                SukiMessageBoxIcons.Information,
                null!);
        }
        catch (Exception ex)
        {
            _viewModel?.ReportViewWarning($"无法显示提示对话框：{ex.Message}", ex);
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
        catch (Exception ex)
        {
            _viewModel?.ReportViewWarning($"无法显示错误对话框：{ex.Message}", ex);
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
