using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SukiUI.Controls;

namespace Left_4_Mod.Views;

public partial class NameWindow : SukiWindow
{
    /// <summary>供 XAML 设计器使用。</summary>
    public NameWindow()
    {
        InitializeComponent();
    }

    public NameWindow(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        NameBox.Text = initialValue;

        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Confirm();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
        }
    }

    private void Confirm()
    {
        var value = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            ErrorText.Text = "名称不能为空。";
            ErrorText.IsVisible = true;
            NameBox.Focus();
            return;
        }

        Close(value);
    }
}
