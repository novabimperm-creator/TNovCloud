using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Fsp;
using WinFspHost = global::Fsp.FileSystemHost;

namespace TNovCloud.FileSystemHost;

internal static class HostApplication
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        var command = args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? "run";
        HostPaths.Configure(Option(args, "--data-root"));
        var sessionPath = Option(args, "--session") ?? HostPaths.SessionPath;
        var driveOverride = Option(args, "--drive");

        try
        {
            return command.ToLowerInvariant() switch
            {
                "run" or "mount" => await RunAsync(sessionPath, driveOverride),
                "start" => await StartAsync(sessionPath, driveOverride),
                "stop" => await SendCommandAsync("stop"),
                "status" => await SendCommandAsync("status"),
                "sync" => await SendCommandAsync("sync"),
                "version" => PrintVersion(),
                "help" or "--help" or "-h" => PrintUsage(),
                _ => throw new ArgumentException($"Unknown command: {command}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string sessionPath, string? driveOverride)
    {
        Directory.CreateDirectory(HostPaths.HostRoot);
        using var singleInstance = new Mutex(true, HostPaths.MutexName, out var acquired);
        if (!acquired)
            throw new InvalidOperationException("TNovCloud filesystem host is already running.");

        var session = HostSession.Load(sessionPath);
        var userId = session.User!.Id;
        var drive = NormalizeDrive(driveOverride ?? session.DriveLetter);
        using var api = new CloudApi(session);
        var store = new ManifestStore(HostPaths.CacheRoot, HostPaths.DatabasePath);
        Log("Synchronizing manifest and full-file cache.");
        try
        {
            await store.SynchronizeAsync(api, userId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Log($"Server unavailable; mounting the existing offline cache: {exception.Message}");
        }

        var version = WinFspHost.Version();
        if (version.Major != 2 || version.Minor < 2)
            throw new InvalidOperationException(
                $"WinFsp 2.2.26215 Beta4 or later is required; installed runtime reports {version}.");

        using var host = new WinFspHost(
            new TNovCloudFileSystem(store, api, userId, Log))
        {
            FileSystemName = "TNovCloud"
        };

        var mountPoint = $"{drive}:";
        var status = host.Mount(
            mountPoint,
            null!,
            true, // Synchronized=true for the first implementation stage.
            0);
        if (status < 0)
            throw new IOException(
                $"WinFsp could not mount {mountPoint} (NTSTATUS 0x{status:X8}).");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        Log($"Mounted {host.MountPoint()} using WinFsp {version}.");
        Console.WriteLine($"TNovCloud mounted at {host.MountPoint()}. Press Ctrl+C to stop.");
        try
        {
            await ServePipeAsync(host, store, api, userId, cancellation);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            host.Unmount();
            Log($"Unmounted {mountPoint}.");
        }
        return 0;
    }

    private static async Task ServePipeAsync(
        WinFspHost host,
        ManifestStore store,
        CloudApi api,
        Guid currentUserId,
        CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    HostPaths.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var request = (await reader.ReadLineAsync(stop.Token) ?? "").Trim();
                var parts = request.Split('\t');
                var command = parts[0].ToLowerInvariant();
                switch (command)
                {
                    case "stop":
                        await writer.WriteLineAsync("{\"stopping\":true}");
                        stop.Cancel();
                        break;
                    case "status":
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new
                        {
                            running = true,
                            mountPoint = host.MountPoint(),
                            winFsp = WinFspHost.Version().ToString(),
                            cacheRoot = HostPaths.CacheRoot,
                            database = HostPaths.DatabasePath
                        }, JsonDefaults.Options));
                        break;
                    case "sync":
                        await store.SynchronizeAsync(api, currentUserId, stop.Token);
                        await writer.WriteLineAsync("{\"synchronized\":true}");
                        break;
                    case "discard" when parts.Length == 2:
                        store.DiscardLocalChanges(parts[1]);
                        await store.SynchronizeAsync(api, currentUserId, stop.Token);
                        await writer.WriteLineAsync("{\"discarded\":true}");
                        break;
                    case "committed" when parts.Length == 5 &&
                                                  int.TryParse(parts[2], out var version) &&
                                                  long.TryParse(parts[3], out var revision) &&
                                                  bool.TryParse(parts[4], out var keepCheckout):
                        store.MarkCommitted(
                            parts[1],
                            version,
                            revision,
                            keepCheckout ? currentUserId : null);
                        await writer.WriteLineAsync("{\"committed\":true}");
                        break;
                    default:
                        await writer.WriteLineAsync("{\"error\":\"unknown command\"}");
                        break;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log($"Command pipe failure: {exception}");
            }
        }
    }

    private static async Task<int> StartAsync(string sessionPath, string? driveOverride)
    {
        if (await TrySendCommandAsync("status", 250) is not null)
        {
            Console.WriteLine("TNovCloud filesystem host is already running.");
            return 0;
        }

        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Cannot determine host executable path.");
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--session");
        start.ArgumentList.Add(sessionPath);
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(HostPaths.AppRoot);
        if (!string.IsNullOrWhiteSpace(driveOverride))
        {
            start.ArgumentList.Add("--drive");
            start.ArgumentList.Add(driveOverride);
        }

        _ = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start TNovCloud filesystem host.");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(250);
            var response = await TrySendCommandAsync("status", 250);
            if (response is not null)
            {
                Console.WriteLine(response);
                return 0;
            }
        }

        throw new TimeoutException($"Host did not start. See {HostPaths.LogPath}.");
    }

    private static async Task<int> SendCommandAsync(string command)
    {
        var response = await TrySendCommandAsync(command, 2000);
        if (response is null)
        {
            Console.Error.WriteLine("TNovCloud filesystem host is not running.");
            return 2;
        }
        Console.WriteLine(response);
        return 0;
    }

    private static async Task<string?> TrySendCommandAsync(string command, int timeoutMilliseconds)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                HostPaths.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            await pipe.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(command);
            return await reader.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static char NormalizeDrive(string value)
    {
        var letter = char.ToUpperInvariant(string.IsNullOrWhiteSpace(value) ? 'T' : value[0]);
        if (letter is < 'A' or > 'Z')
            throw new ArgumentException($"Invalid drive letter: {value}");
        return letter;
    }

    private static string? Option(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private static int PrintUsage()
    {
        Console.WriteLine(
            """
            TNovCloud.FileSystemHost [run|start|stop|status|sync|version] [--session PATH] [--drive T] [--data-root PATH]

              run      synchronize, mount, and stay in the foreground (default)
              start    launch a background host
              stop     unmount and stop the running host
              status   print running host status
              sync     refresh the remote tree and full-file cache
              version  print the installed WinFsp runtime version
            """);
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"WinFsp {WinFspHost.Version()}");
        return 0;
    }

    private static void Log(string message)
    {
        Directory.CreateDirectory(HostPaths.HostRoot);
        File.AppendAllText(
            HostPaths.LogPath,
            $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }
}
