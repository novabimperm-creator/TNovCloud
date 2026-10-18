using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TNovCloud.Client.Services;

public static class DriveMounter
{
    private const string MountRegistryPath = @"Software\TNovCloud\Mount";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        uint fileSystemNameSize);

    public static char Mount(string driveLetter, string _)
    {
        var existing = FindMountedVolume();
        if (existing is { } mounted)
        {
            WriteMountRegistration(mounted);
            return mounted;
        }

        var preferred = NormalizeLetter(driveLetter);
        var selected = IsFree(preferred) ? preferred : FirstFreeLetter();
        var session = SessionStore.Load();
        session.DriveLetter = selected.ToString();
        SessionStore.Save(session);

        RunHost("start", selected, waitForExit: true);
        if (!Directory.Exists($"{selected}:\\"))
            throw new InvalidOperationException(
                $"WinFsp-host завершился, но том {selected}: не появился. Проверьте лог " +
                Path.Combine(SessionStore.Root, "FileSystemHost", "host.log"));

        WriteMountRegistration(selected);
        return selected;
    }

    public static void Unmount(char letter)
    {
        try
        {
            RunHost("stop", letter, waitForExit: true);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(MountRegistryPath, false);
        }
    }

    public static void Sync(char letter) => RunHost("sync", letter, waitForExit: true);

    public static void Discard(string relativePath)
        => SendHostControl($"discard\t{NormalizeControlPath(relativePath)}");

    public static void MarkCommitted(
        string relativePath,
        int version,
        long revision,
        bool keepCheckout)
        => SendHostControl(
            $"committed\t{NormalizeControlPath(relativePath)}\t{version}\t{revision}\t{keepCheckout}");

    public static void Open(char letter)
        => Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"{letter}:\\",
            UseShellExecute = true
        });

    private static void RunHost(string command, char letter, bool waitForExit)
    {
        var (fileName, prefix) = FindHost();
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fileName)!
        };
        foreach (var item in prefix)
            start.ArgumentList.Add(item);
        start.ArgumentList.Add(command);
        if (command is "start")
        {
            start.ArgumentList.Add("--drive");
            start.ArgumentList.Add(letter.ToString());
        }

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Не удалось запустить WinFsp-host.");
        if (!waitForExit)
            return;
        if (!process.WaitForExit(20_000))
        {
            process.Kill(true);
            throw new TimeoutException("WinFsp-host не ответил за 20 секунд.");
        }
        if (process.ExitCode != 0 && command is not "stop")
            throw new InvalidOperationException(
                $"WinFsp-host завершился с кодом {process.ExitCode}. См. host.log.");
    }

    private static (string FileName, string[] Prefix) FindHost()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "TNovCloud.FileSystemHost.exe");
        if (File.Exists(installed))
            return (installed, []);

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "TNovCloud.slnx")))
                continue;
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                foreach (var platform in new[] { "", "x64" })
                {
                    var dll = Path.Combine(
                        directory.FullName,
                        "src",
                        "TNovCloud.FileSystemHost",
                        "bin",
                        platform,
                        configuration,
                        "net10.0-windows",
                        "win-x64",
                        "TNovCloud.FileSystemHost.dll");
                    if (File.Exists(dll))
                        return ("dotnet", [dll]);
                }
            }
        }

        throw new FileNotFoundException(
            "TNovCloud.FileSystemHost не найден. Выполните publish/установку Windows-клиента.");
    }

    private static char? FindMountedVolume()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady &&
                    string.Equals(drive.VolumeLabel, "TNovCloud", StringComparison.OrdinalIgnoreCase))
                    return char.ToUpperInvariant(drive.Name[0]);
            }
            catch (IOException)
            {
                // A removable or disconnected volume can disappear while enumerating.
            }
        }
        return null;
    }

    private static void WriteMountRegistration(char letter)
    {
        var root = $"{letter}:\\";
        var volumeName = new StringBuilder(260);
        var fileSystemName = new StringBuilder(260);
        uint serial = 0;
        var registered = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (GetVolumeInformation(
                    root,
                    volumeName,
                    (uint)volumeName.Capacity,
                    out serial,
                    out _,
                    out _,
                    fileSystemName,
                    (uint)fileSystemName.Capacity))
            {
                registered = true;
                break;
            }

            Thread.Sleep(100);
        }

        if (!registered)
            throw new InvalidOperationException(
                $"Не удалось прочитать параметры тома {root} (Win32 {Marshal.GetLastWin32Error()}).");

        using var key = Registry.CurrentUser.CreateSubKey(MountRegistryPath);
        key.SetValue("DriveLetter", letter.ToString(), RegistryValueKind.String);
        key.SetValue("RootPath", root, RegistryValueKind.String);
        key.SetValue("VolumeSerial", unchecked((int)serial), RegistryValueKind.DWord);
        key.SetValue("ClientPath", Environment.ProcessPath ?? "", RegistryValueKind.String);
    }

    private static char NormalizeLetter(string preferred)
    {
        var value = char.ToUpperInvariant(string.IsNullOrWhiteSpace(preferred) ? 'T' : preferred[0]);
        return value is >= 'A' and <= 'Z' ? value : 'T';
    }

    private static char FirstFreeLetter()
    {
        foreach (var letter in "TUVWXYZNOPQRSHIJKLM")
        {
            if (IsFree(letter))
                return letter;
        }
        throw new InvalidOperationException("Нет свободной буквы диска для TNovCloud.");
    }

    private static bool IsFree(char letter) => !Directory.Exists($"{letter}:\\");

    private static void SendHostControl(string command)
    {
        var user = string.Concat(
            Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        using var pipe = new NamedPipeClientStream(
            ".",
            $"TNovCloud.FileSystemHost.{user}",
            PipeDirection.InOut);
        pipe.Connect(2_000);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        writer.WriteLine(command);
        var response = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(response) || response.Contains("\"error\"", StringComparison.Ordinal))
            throw new IOException($"WinFsp-host отклонил команду: {response ?? "нет ответа"}");
    }

    private static string NormalizeControlPath(string value)
    {
        var path = value.Replace('\\', '/').Trim('/');
        if (path.Contains('\t') || path.Contains('\r') || path.Contains('\n'))
            throw new ArgumentException("Некорректный путь.", nameof(value));
        return path;
    }
}
