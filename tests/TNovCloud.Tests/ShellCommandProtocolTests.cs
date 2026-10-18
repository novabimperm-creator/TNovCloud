using System.Text;
using TNovCloud.Client.Services;

namespace TNovCloud.Tests;

public sealed class ShellCommandProtocolTests
{
    [Fact]
    public void Native_extension_payload_maps_to_client_argument()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"command":"checkin","paths":["T:\\Проекты\\Model.rvt"]}""");

        var request = ShellCommandProtocol.Decode(payload);

        Assert.Equal(@"T:\Проекты\Model.rvt", request.Paths.Single());
        Assert.Equal("--checkin", ShellCommandProtocol.ToArgument(request.Command));
    }

    [Fact]
    public void Unknown_command_is_rejected()
        => Assert.Throws<InvalidDataException>(
            () => ShellCommandProtocol.ToArgument("delete-everything"));

    [Fact]
    public void Multiple_selected_paths_are_preserved()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"command":"checkout","paths":["T:\\a.rvt","T:\\b.rvt"]}""");

        var request = ShellCommandProtocol.Decode(payload);

        Assert.Equal(2, request.Paths.Count);
        Assert.Equal(@"T:\a.rvt", request.Paths[0]);
        Assert.Equal(@"T:\b.rvt", request.Paths[1]);
    }
}
