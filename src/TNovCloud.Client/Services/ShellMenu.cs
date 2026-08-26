using Microsoft.Win32;

namespace TNovCloud.Client.Services;

public static class ShellMenu
{
    public static void Register(char driveLetter)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь к клиенту.");
        var applies = $"System.Volume.DriveLetter:=\"{driveLetter}\"";
        RegisterTree(@"Software\Classes\*\shell\TNovCloud", exe, applies, files: true);
        RegisterTree(@"Software\Classes\Directory\shell\TNovCloud", exe, applies, files: false);
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\TNovCloud", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\TNovCloud", false);
    }

    private static void RegisterTree(string root, string exe, string applies, bool files)
    {
        using var menu = Registry.CurrentUser.CreateSubKey(root);
        menu.SetValue("MUIVerb", "TNovCloud");
        menu.SetValue("SubCommands", "");
        menu.SetValue("AppliesTo", applies);
        menu.SetValue("Icon", exe);

        if (files)
        {
            Verb(root, "01checkout", "Взять на редактирование", exe, "--checkout");
            Verb(root, "02checkin", "Сохранить изменения", exe, "--checkin");
            Verb(root, "03cancel", "Отменить изменения", exe, "--cancel");
            Verb(root, "04approve", "Отправить на согласование", exe, "--approve");
            Verb(root, "05props", "Свойства и версии", exe, "--properties");
        }
        else
        {
            Verb(root, "01open", "Открыть", exe, "--open");
        }
    }

    private static void Verb(string root, string name, string title, string exe, string arg)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{root}\shell\{name}");
        key.SetValue("MUIVerb", title);
        using var cmd = Registry.CurrentUser.CreateSubKey($@"{root}\shell\{name}\command");
        cmd.SetValue("", $"\"{exe}\" {arg} \"%1\"");
    }
}
