using System;
using System.Windows;

namespace WowProxy.App.Views;

public partial class PromptWindow : Window
{
    public string InputText { get; private set; } = string.Empty;

    public PromptWindow(string title, string message, string defaultText = "")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        InputTextBox.Text = defaultText;
        
        Loaded += (s, e) =>
        {
            InputTextBox.SelectAll();
            InputTextBox.Focus();
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        InputText = InputTextBox.Text;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void InputTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            OkButton_Click(sender, e);
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            CancelButton_Click(sender, e);
        }
    }

    private void TopBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            this.DragMove();
        }
    }
}
