using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Left_4_Mod.Services;
using Left_4_Mod.ViewModels;
using SukiUI.Controls;

namespace Left_4_Mod.Views;

public partial class WorkshopWindow : SukiWindow
{
    private readonly MainViewModel? _viewModel;

    /// <summary>供 XAML 设计器使用。</summary>
    public WorkshopWindow()
    {
        InitializeComponent();
    }

    public WorkshopWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel is { IsBusy: true })
        {
            _viewModel.CancelCurrentOperation();
        }
    }

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        ErrorText.IsVisible = false;

        if (!WorkshopUtils.TryParsePublishedFileId(IdBox.Text ?? string.Empty, out var id))
        {
            ShowError("请输入有效的创意工坊物品 ID，或包含 ?id= 的创意工坊链接。");
            return;
        }

        AddButton.IsEnabled = false;

        try
        {
            var result = await _viewModel.AddWorkshopModAsync(id);
            if (result is null)
            {
                ShowError("操作已取消。");
                return;
            }

            if (result.Success)
            {
                Close(true);
                return;
            }

            ShowError(result.Error ?? "创意工坊模组下载失败。");
        }
        catch (Exception ex)
        {
            ShowError($"下载失败：{ex.Message}");
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is { IsBusy: true })
        {
            _viewModel.CancelCurrentOperation();
            ErrorText.Text = "正在取消，请稍候…";
            ErrorText.IsVisible = true;
            return;
        }

        Close(false);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
