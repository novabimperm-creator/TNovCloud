using System.Drawing;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using TNovCloud.Client.Services;

namespace TNovCloud.Client;

public partial class App : Application
{
    public static CloudApiClient Api { get; } = new();
    public static AppSession Session { get; private set; } = new();
    public static char DriveLetter { get; internal set; } = 'T';

    private const string MutexName = @"Local\TNovCloud.Client";
    private const string PipeName = "TNovCloud.Client.Activate";

    private Mutex? _mutex;
    private CancellationTokenSource? _activateCts;
    private ShellCommandServer? _shellCommands;
    private TaskbarIcon? _tray;
    private MainWindow? _window;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isCommand = e.Args.Length >= 2 && e.Args[0].StartsWith("--");
        if (!isCommand && !TryTakeSingleInstance())
        {
            SignalActivate();
            Shutdown();
            return;
        }

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

        _shellCommands = new ShellCommandServer();
        if (Session.User is not null && !string.IsNullOrWhiteSpace(Session.Token))
        {
            try
            {
                DriveLetter = await Task.Run(
                    () => DriveMounter.Mount(Session.DriveLetter, SessionStore.DrivePath));
                Session.DriveLetter = DriveLetter.ToString();
                SessionStore.Save(Session);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "TNovCloud", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        _window = new MainWindow();
        InitTray();
        _window.Show();
        ListenForActivate();
    }

    private bool TryTakeSingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out var created);
        return created;
    }

    private static void SignalActivate()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            client.WriteByte(1);
        }
        catch
        {
            // первый экземпляр мог ещё не открыть канал
        }
    }

    private void ListenForActivate()
    {
        _activateCts = new CancellationTokenSource();
        var token = _activateCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    await server.WaitForConnectionAsync(token);
                    Dispatcher.Invoke(ShowMain);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(300, token);
                }
            }
        }, token);
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
        _shellCommands?.Dispose();
        _activateCts?.Cancel();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Api.Dispose();
        _tray?.Dispose();
        _shellCommands?.Dispose();
        _activateCts?.Cancel();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
