using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Interop;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Tests;

public sealed class IpcTests
{

    [Fact]
    public void Submit_SameCanonicalRequestReplaysAndChangedPayloadConflicts()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var queue = new TestWorkQueue();
        var service = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        var request = TestData.Request();
        service.Submit(TestData.Json(request));
        queue.Drain();

        var replay = Parse(service.Submit(TestData.Json(request)));
        var changed = request with { Items = [new ShortageRequestItem { ItemId = 100, ItemName = "Darksteel Ore", TargetQuantity = 60, ShortageQuantity = 40 }] };
        var conflict = Parse(service.Submit(TestData.Json(changed)));

        Assert.Equal(OperationStatuses.Accepted, replay.GetProperty("status").GetString());
        Assert.Equal(1, replay.GetProperty("revision").GetInt64());
        Assert.Equal(OperationStatuses.Rejected, conflict.GetProperty("status").GetString());
        Assert.Equal("request_conflict", conflict.GetProperty("errorCode").GetString());
        Assert.Single(repository.FullSnapshot().Operations);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

}
