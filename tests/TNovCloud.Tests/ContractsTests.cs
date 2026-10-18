using System.Text.Json;
using System.Text.Json.Serialization;
using TNovCloud.Shared;

namespace TNovCloud.Tests;

public sealed class ContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Metadata_round_trips_revision_and_checkout_state()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var value = new FsMetadataDto(
            false, id, Guid.NewGuid(), "Model.rvt", "Проекты/Model.rvt", 7,
            FileDocStatus.Draft, userId, "Проектировщик", 1234,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            42, false, null, null);

        var json = JsonSerializer.Serialize(value, JsonOptions);
        var restored = JsonSerializer.Deserialize<FsMetadataDto>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(id, restored.Id);
        Assert.Equal(42, restored.Revision);
        Assert.Equal(FileDocStatus.Draft, restored.Status);
        Assert.Equal(userId, restored.CheckedOutById);
    }

    [Fact]
    public void Mutation_contracts_require_expected_revision()
    {
        const long revision = 17;

        Assert.Equal(revision, new RenameEntryRequest("a.txt", "b.txt", revision).ExpectedRevision);
        Assert.Equal(revision, new MoveEntryRequest("a.txt", "Archive", revision).ExpectedRevision);
        Assert.Equal(revision, new DeleteEntryRequest("a.txt", revision).ExpectedRevision);
        Assert.Equal(revision, new RestoreDeletedEntryRequest("a.txt", revision).ExpectedRevision);
    }
}
