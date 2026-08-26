using System.Windows;
using TNovCloud.Client.Services;
using TNovCloud.Shared;

namespace TNovCloud.Client;

public partial class PropertiesWindow : Window
{
    private readonly string _path;

    public PropertiesWindow(string path)
    {
        InitializeComponent();
        _path = path;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var details = await App.Api.DetailsAsync(_path);
            HeaderText.Text = details.File.Name;
            MetaText.Text =
                $"Путь: {details.File.Path}\n" +
                $"Текущая версия: {details.File.Version}   Статус: {details.File.Status}\n" +
                $"Извлечён: {details.File.CheckedOutByName ?? "—"}";
            VersionList.ItemsSource = details.Versions;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TNovCloud", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not FileVersionDto version)
        {
            MessageBox.Show("Выберите версию.", "TNovCloud");
            return;
        }

        try
        {
            await App.Api.RestoreAsync(_path, version.Version);
            if (App.Session.User is not null)
                await new SyncService(App.Api).SyncAsync(App.Session.User.Id);
            await LoadAsync();
            MessageBox.Show($"Восстановлено из версии {version.Version}. Создана новая major-версия.", "TNovCloud");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TNovCloud", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
