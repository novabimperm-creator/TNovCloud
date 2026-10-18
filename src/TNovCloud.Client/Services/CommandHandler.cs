using System.Windows;
using TNovCloud.Client.Services;
using TNovCloud.Shared;

namespace TNovCloud.Client;

internal static class CommandHandler
{
    public static async Task RunAsync(string[] args)
    {
        var command = args[0];
        var target = args[1];
        try
        {
            if (App.Session.User is null || string.IsNullOrEmpty(App.Session.Token))
            {
                MessageBox.Show("Сначала войдите в TNovCloud.", "TNovCloud");
                return;
            }

            App.Api.Configure(App.Session.ServerUrl, App.Session.Token);
            if (command == "--open")
            {
                DriveMounter.Open(App.DriveLetter);
                return;
            }

            var letter = char.ToUpperInvariant(Path.GetPathRoot(target)?[0] ?? App.DriveLetter);
            var path = PathRules.RelativeFromDrive(target, letter);

            switch (command)
            {
                case "--checkout":
                    await App.Api.CheckoutAsync(path);
                    await Task.Run(() => DriveMounter.Sync(App.DriveLetter));
                    MessageBox.Show($"Файл взят на редактирование:\n{path}", "TNovCloud");
                    break;
                case "--checkin":
                    await CheckinAsync(path, target);
                    break;
                case "--cancel":
                    await App.Api.CancelAsync(path);
                    await Task.Run(() => DriveMounter.Discard(path));
                    MessageBox.Show("Изменения отменены, файл возвращён к серверной версии.", "TNovCloud");
                    break;
                case "--approve":
                    await ApproveAsync(path);
                    break;
                case "--properties":
                    new PropertiesWindow(path).ShowDialog();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TNovCloud", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task CheckinAsync(string path, string localFile)
    {
        var dialog = new CheckinWindow();
        if (dialog.ShowDialog() != true)
            return;
        var before = await App.Api.DetailsAsync(path);
        var expectedVersion = before.File.Version
                              ?? throw new InvalidOperationException("У файла отсутствует номер версии.");
        await App.Api.CheckinAsync(
            path,
            localFile,
            dialog.Comment,
            dialog.KeepCheckout,
            expectedVersion);
        var details = await App.Api.DetailsAsync(path);
        if (details.File.Version is { } version)
            await Task.Run(() => DriveMounter.MarkCommitted(
                path,
                version,
                details.File.Revision,
                dialog.KeepCheckout));
        MessageBox.Show($"Сохранена версия {details.File.Version}.", "TNovCloud");
    }

    private static async Task ApproveAsync(string path)
    {
        var users = await App.Api.UsersAsync();
        var dialog = new ApproveWindow(users);
        if (dialog.ShowDialog() != true)
            return;
        await App.Api.StartProcessAsync(new CreateProcessRequest(path, dialog.ProcessTitle, dialog.AssigneeIds, dialog.DueAt));
        MessageBox.Show("Процесс согласования запущен.", "TNovCloud");
    }
}
