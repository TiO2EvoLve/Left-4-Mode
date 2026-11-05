using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Left_4_Mod.Models;
using SukiUI.Controls;

namespace Left_4_Mod.Views;

public partial class MoveWindow : SukiWindow
{
    /// <summary>供 XAML 设计器使用。</summary>
    public MoveWindow()
    {
        InitializeComponent();
    }

    public MoveWindow(int count, IReadOnlyList<FolderChoice> choices, string initialPath)
    {
        InitializeComponent();
        MessageText.Text = $"将 {count} 个模组移动到：";
        ChoiceBox.ItemsSource = choices;
        ChoiceBox.SelectedItem = choices.FirstOrDefault(choice =>
                                       string.Equals(choice.RelativePath, initialPath, StringComparison.OrdinalIgnoreCase))
                                   ?? choices.FirstOrDefault();

        Opened += (_, _) => ChoiceBox.Focus();
    }

    private void OnMoveClick(object? sender, RoutedEventArgs e)
    {
        if (ChoiceBox.SelectedItem is FolderChoice choice)
        {
            Close(choice);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
            return;
        }

        base.OnKeyDown(e);
    }
}
