using System.Windows;

namespace WowProxy.App.Views;

public partial class ConfirmWindow : Window
{
    public bool IsConfirmed { get; private set; }

    public ConfirmWindow(string title, string message, string confirmButtonText = "确定", bool isDangerous = true)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmButtonText;
        
        if (isDangerous)
        {
            ConfirmButton.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
        }
        else
        {
            ConfirmButton.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3B82F6"));
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
    }

    private void TopBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            this.DragMove();
        }
    }
}
