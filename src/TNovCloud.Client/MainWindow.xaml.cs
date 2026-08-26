using System.Windows;
using TNovCloud.Client.Services;
using TNovCloud.Shared;

namespace TNovCloud.Client;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ServerBox.Text = App.Session.ServerUrl;
        DriveBox.Text = App.Session.DriveLetter;
        LoginBox.Text = App.Session.User?.Login ?? "admin";
        if (App.Session.User is not null && !string.IsNullOrEmpty(App.Session.Token))
            ShowWork();
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Вход…";
            App.Session.ServerUrl = ServerBox.Text.Trim();
            App.Session.DriveLetter = string.IsNullOrWhiteSpace(DriveBox.Text) ? "T" : DriveBox.Text.Trim().ToUpperInvariant();
            App.Api.Configure(App.Session.ServerUrl, null);
            var result = await App.Api.LoginAsync(LoginBox.Text.Trim(), PasswordBox.Password);
            App.Session.Token = result.Token;
            App.Session.User = result.User;
            App.Api.Configure(App.Session.ServerUrl, result.Token);
            SessionStore.Save(App.Session);
            ShowWork();
            await SyncAndInbox();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TNovCloud", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Ошибка входа";
        }
    }

    private void ShowWork()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        WorkPanel.Visibility = Visibility.Visible;
        UserText.Text = $"{App.Session.User?.DisplayName} ({App.Session.User?.Login})";
        DriveText.Text = $"Диск {App.DriveLetter}:  TNovCloud  →  {App.Session.ServerUrl}";
        StatusText.Text = "Подключено";
    }

    private async Task SyncAndInbox()
    {
        if (App.Session.User is null)
            return;
        var progress = new Progress<string>(x => StatusText.Text = x);
        await new SyncService(App.Api).SyncAsync(App.Session.User.Id, progress);
        await RefreshInbox();
    }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        try
        {
            await SyncAndInbox();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TNovCloud", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenDrive(object sender, RoutedEventArgs e) => DriveMounter.Open(App.DriveLetter);

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        App.Session.Token = null;
        App.Session.User = null;
        SessionStore.Save(App.Session);
        LoginPanel.Visibility = Visibility.Visible;
        WorkPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "Выход выполнен";
        await Task.CompletedTask;
    }

    private async void OnRefreshInbox(object sender, RoutedEventArgs e) => await RefreshInbox();

    private async Task RefreshInbox()
    {
        try
        {
            InboxList.ItemsSource = await App.Api.InboxAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void OnApproveTask(object sender, RoutedEventArgs e) => await Decide("approve");
    private async void OnRejectTask(object sender, RoutedEventArgs e) => await Decide("reject");

    private async Task Decide(string action)
    {
        if (InboxList.SelectedItem is not ProcessTaskDto task)
        {
            MessageBox.Show("Выберите задачу.", "TNovCloud");
            return;
        }

        try
        {
            await App.Api.DecideAsync(task.Id, action, null);
            await SyncAndInbox();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TNovCloud", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
