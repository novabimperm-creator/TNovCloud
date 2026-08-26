using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using TNovCloud.Client.Services;

namespace TNovCloud.Client;

public partial class App : Application
{
    public static CloudApiClient Api { get; } = new();
    public static AppSession Session { get; private set; } = new();
    public static char DriveLetter { get; private set; } = 'T';

    private TaskbarIcon? _tray;
    private MainWindow? _window;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Session = SessionStore.Load();
        Api.Configure(Session.ServerUrl, Session.Token);
        if (!string.IsNullOrWhiteSpace(Session.DriveLetter))
            DriveLetter = char.ToUpperInvariant(Session.DriveLetter[0]);

        if (e.Args.Length >= 2 && e.Args[0].StartsWith("--"))
        {
            await CommandHandler.RunAsync(e.Args);
            Shutdown();
            return;
        }

        try
        {
            DriveLetter = DriveMounter.Mount(Session.DriveLetter, SessionStore.DrivePath);
            Session.DriveLetter = DriveLetter.ToString();
            SessionStore.Save(Session);
            ShellMenu.Register(DriveLetter);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TNovCloud", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _window = new MainWindow();
        InitTray();
        _window.Show();
    }

    private void InitTray()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Открыть диск", (_, _) => DriveMounter.Open(DriveLetter)));
        menu.Items.Add(Item("Окно TNovCloud", (_, _) => ShowMain()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Выход", (_, _) => ExitApp()));

        _tray = new TaskbarIcon
        {
            ToolTipText = "TNovCloud",
            ContextMenu = menu,
            Icon = SystemIcons.Application
        };
        _tray.TrayMouseDoubleClick += (_, _) => DriveMounter.Open(DriveLetter);
    }

    private static MenuItem Item(string header, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        return item;
    }

    public void ShowMain()
    {
        if (_window is null)
            return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApp()
    {
        try
        {
            DriveMounter.Unmount(DriveLetter);
        }
        catch
        {
            // диск мог быть уже отключён
        }

        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Api.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
