using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Left_4_Mod.Infrastructure;
using Left_4_Mod.Models;
using Left_4_Mod.ViewModels;
using SukiUI.Controls;

namespace Left_4_Mod.Views;

public partial class SettingsWindow : SukiWindow
{
    private readonly MainViewModel? _viewModel;

    /// <summary>供 XAML 设计器/运行时加载器使用；实际运行通过带参数的构造函数。</summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;

        LibraryBox.Text = viewModel.LibraryPath;
        GameBox.Text = viewModel.GamePath;
        GameHint.Text = viewModel.GameStatusText;
        AutoUpdateToggle.IsChecked = viewModel.AutoUpdateWorkshop;
    }

    private async void OnBrowseLibraryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFolderAsync("选择模组库文件夹", LibraryBox.Text);
        if (path is not null)
        {
            LibraryBox.Text = path;
        }
    }

    private async void OnBrowseGameClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFolderAsync("选择 L4D2 游戏目录", GameBox.Text);
        if (path is not null)
        {
            GameBox.Text = path;
            GameHint.Text = "保存时会验证目录结构。";
        }
    }

    private async void OnDetectGameClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        DetectButton.IsEnabled = false;
        try
        {
            var (path, error) = await _viewModel.DetectGamePathAsync();
            if (!string.IsNullOrWhiteSpace(path))
            {
                GameBox.Text = path;
                GameHint.Text = "已自动检测到游戏目录，保存后生效。";
                ErrorText.IsVisible = false;
            }
            else
            {
                GameHint.Text = error ?? "未自动检测到游戏目录。";
            }
        }
        finally
        {
            DetectButton.IsEnabled = true;
        }
    }

    private async void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        ErrorText.IsVisible = false;

        var (success, error) = await _viewModel.ApplySettingsAsync(
            LibraryBox.Text ?? string.Empty,
            GameBox.Text ?? string.Empty,
            AutoUpdateToggle.IsChecked == true);

        if (!success)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }

        Close(true);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private void OnOpenLogsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo(AppPaths.LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"无法打开日志目录：{ex.Message}";
            ErrorText.IsVisible = true;
        }
    }

    private async Task<string?> PickFolderAsync(string title, string? currentPath)
    {
        var storage = StorageProvider;
        if (storage is null || !storage.CanPickFolder)
        {
            ErrorText.Text = "当前平台不支持选择文件夹。";
            ErrorText.IsVisible = true;
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        };

        if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
        {
            try
            {
                if (await storage.TryGetFolderFromPathAsync(currentPath) is { } start)
                {
                    options.SuggestedStartLocation = start;
                }
            }
            catch (Exception)
            {
                // 起始位置不可用时仍可正常选择。
            }
        }

        var folders = await storage.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
