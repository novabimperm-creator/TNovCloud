using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TNovCloud.Client.Services;

public static class DriveMounter
{
    private const uint DddRemoveDefinition = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string? targetPath);

    public static char Mount(string driveLetter, string targetPath)
    {
        Directory.CreateDirectory(targetPath);
        var letter = ResolveLetter(driveLetter);
        Unmount(letter);
        if (!DefineDosDevice(0, $"{letter}:", targetPath))
            throw new InvalidOperationException($"Не удалось подключить диск {letter}:.");
        SetLabel(letter);
        return letter;
    }

    public static void Unmount(char letter)
        => DefineDosDevice(DddRemoveDefinition, $"{letter}:", null);

    public static void Open(char letter)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"{letter}:\\",
            UseShellExecute = true
        });

    private static char ResolveLetter(string preferred)
    {
        var want = char.ToUpperInvariant(string.IsNullOrWhiteSpace(preferred) ? 'T' : preferred[0]);
        if (want is < 'A' or > 'Z')
            want = 'T';
        if (IsFree(want) || IsOurDrive(want))
            return want;
        foreach (var letter in "TUVWXYZNOPQRSHIJKLM")
        {
            if (IsFree(letter))
                return letter;
        }

        throw new InvalidOperationException("Нет свободной буквы диска для TNovCloud.");
    }

    private static bool IsFree(char letter)
        => !Directory.Exists($"{letter}:\\");

    private static bool IsOurDrive(char letter)
    {
        try
        {
            var path = Path.GetFullPath($"{letter}:\\");
            return path.StartsWith(Path.GetFullPath(SessionStore.DrivePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void SetLabel(char letter)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}\DefaultLabel");
        key.SetValue("", "TNovCloud");
        using var icon = Registry.CurrentUser.CreateSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}\DefaultIcon");
        icon.SetValue("", Environment.ProcessPath ?? "");
    }
}
