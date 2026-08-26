using System.Windows;

namespace TNovCloud.Client;

public partial class CheckinWindow : Window
{
    public string? Comment => string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text.Trim();
    public bool KeepCheckout => KeepBox.IsChecked == true;

    public CheckinWindow()
    {
        InitializeComponent();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
