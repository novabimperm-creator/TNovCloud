using System.Text.Json;

namespace TNovCloud.Client.Services;

internal static class ShellCommandProtocol
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static ShellCommandRequest Decode(ReadOnlySpan<byte> payload)
    {
        var request = JsonSerializer.Deserialize<ShellCommandRequest>(payload, JsonOptions)
                      ?? throw new InvalidDataException("Пустая shell-команда.");
        if (request.Paths is null ||
            request.Paths.Count is 0 or > 256 ||
            request.Paths.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Shell-команда не содержит корректные пути.");
        return request;
    }

    public static string ToArgument(string command)
        => command.ToLowerInvariant() switch
        {
            "checkout" => "--checkout",
            "checkin" => "--checkin",
            "cancel" => "--cancel",
            "approve" => "--approve",
            "properties" => "--properties",
            _ => throw new InvalidDataException("Неизвестная shell-команда.")
        };
}

internal sealed record ShellCommandRequest(
    string Command,
    IReadOnlyList<string> Paths);
