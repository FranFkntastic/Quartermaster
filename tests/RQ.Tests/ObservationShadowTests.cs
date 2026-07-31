using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Diagnostics;
using Franthropy.Dalamud.Observations;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;
using Microsoft.Data.Sqlite;
using RQ.Domain;
using RQ.Inventory;
using RQ.Observations;

namespace RQ.Tests;

public sealed class ObservationShadowTests
{
    private static readonly ObservationProvenance Provenance =
        new("Quartermaster", "instance", "1.0.0", "2026.07.31.0000.0000");

    [Fact]
    public void Empty_loaded_retainer_listings_map_to_authoritative_empty_payload()
    {
        var retainer = Retainer();

        var observation = QuartermasterObservationMapper.RetainerListings(
            retainer,
            retainer.ObservedAtUtc,
            1,
            Provenance);
        var validation = ObservationValidator.Validate(observation);

        Assert.True(validation.IsAuthoritative);
        Assert.Empty(observation.Payload!.Deserialize<RetainerMarketListingsPayload>(
            ObservationPayloadContracts.RetainerMarketListings,
            1).Listings);
    }

    [Fact]
    public void Listing_without_exact_slot_or_price_maps_as_partial_evidence()
    {
        var retainer = Retainer();
        retainer.Listings.Add(new CachedMarketListing
        {
            ItemId = 100,
            Quantity = 1,
            SlotIndex = 0,
            UnitPrice = null,
        });

        var validation = ObservationValidator.Validate(QuartermasterObservationMapper.RetainerListings(
            retainer,
            retainer.ObservedAtUtc,
            1,
            Provenance));

        Assert.Equal(ObservationValidationStatus.Partial, validation.Status);
    }

    [Fact]
    public void Complete_empty_player_containers_remain_authoritative_empty_not_unavailable()
    {
        var capture = new PlayerStorageCapture(
            [],
            [InventoryType.Inventory1.ToString()],
            [InventoryType.Inventory1.ToString()]);

        var observation = QuartermasterObservationMapper.PlayerInventory(
            Owner(),
            capture,
            1,
            DateTime.SpecifyKind(new DateTime(2026, 7, 31, 12, 0, 0), DateTimeKind.Utc),
            Provenance);

        Assert.True(ObservationValidator.Validate(observation).IsAuthoritative);
        Assert.Empty(observation.Payload!.Deserialize<InventoryObservationPayload>(
            ObservationPayloadContracts.PlayerInventory,
            1).Items);
    }

    [Fact]
    public void Missing_requested_player_container_maps_as_partial_and_cannot_clear_state()
    {
        var capture = new PlayerStorageCapture(
            [],
            [InventoryType.Inventory1.ToString(), InventoryType.Inventory2.ToString()],
            [InventoryType.Inventory1.ToString()]);

        var validation = ObservationValidator.Validate(QuartermasterObservationMapper.PlayerInventory(
            Owner(),
            capture,
            1,
            DateTime.SpecifyKind(new DateTime(2026, 7, 31, 12, 0, 0), DateTimeKind.Utc),
            Provenance));

        Assert.Equal(ObservationValidationStatus.Partial, validation.Status);
    }

    [Fact]
    public async Task Shadow_host_drains_event_driven_listing_write_before_clean_unload()
    {
        var root = Path.Combine(Path.GetTempPath(), "Quartermaster.ObservationShadow.Tests", Guid.NewGuid().ToString("N"));
        var pluginConfig = Path.Combine(root, "XIVLauncher", "pluginConfigs", "Quartermaster");
        Directory.CreateDirectory(pluginConfig);
        var diagnostics = new List<string>();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new QuartermasterObservationShadowHost(
            pluginConfig,
            $"test-{Guid.NewGuid():N}",
            DalamudSharedObservationHost.ApprovedGameBuild,
            (message, _) => diagnostics.Add(message));
        host.CollectorActivated += () => activated.TrySetResult();
        try
        {
            host.Start();
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retainer = Retainer();
            host.ObserveRetainerListings(retainer, retainer.ObservedAtUtc);
            host.Dispose();
            var shadowDiagnostics = host.Diagnostics;

            var paths = SharedObservationPaths.FromPluginConfigDirectory(pluginConfig);
            var open = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = paths.DatabasePath });
            Assert.True(open.IsReady, open.Message);
            var owner = new ObservationOwner(100, 74);
            var read = await open.Store!.ReadCurrentAsync(new ObservationScope(
                owner,
                ObservationSubject.Retainer(200, owner),
                ObservationContainerKind.RetainerMarketListings));

            Assert.Equal(ObservationReadStatus.Found, read.Status);
            Assert.Empty(read.Observation!.Payload.Deserialize<RetainerMarketListingsPayload>(
                ObservationPayloadContracts.RetainerMarketListings,
                1).Listings);
            Assert.Empty(diagnostics);
            Assert.Equal(1, shadowDiagnostics.Enqueued);
            Assert.Equal(1, shadowDiagnostics.AcceptedChanged);
            Assert.Equal(0, shadowDiagnostics.QueueFull);
            Assert.Equal(0, shadowDiagnostics.WriterFaults);
            await open.Store.DisposeAsync();
        }
        finally
        {
            host.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Shadow_host_rejects_an_unapproved_game_build_before_opening_storage()
    {
        var exception = Assert.Throws<GamePatchCompatibilityException>(() =>
            new QuartermasterObservationShadowHost(
                "unused",
                "instance",
                "2099.01.01.0000.0000",
                (_, _) => { }));

        Assert.Equal(DalamudSharedObservationHost.ApprovedGameBuild, exception.Compatibility.ApprovedGameVersion);
    }

    private static OwnerScope Owner() => new()
    {
        LocalContentId = 100,
        HomeWorldId = 74,
        CharacterName = "Test",
        HomeWorldName = "TestWorld",
    };

    private static CachedRetainer Retainer() => new()
    {
        RetainerId = 200,
        RetainerName = "Retainer",
        Owner = Owner(),
        ObservedAtUtc = DateTime.SpecifyKind(new DateTime(2026, 7, 31, 12, 0, 0), DateTimeKind.Utc),
        ListingsObservedAtUtc = DateTime.SpecifyKind(new DateTime(2026, 7, 31, 12, 0, 0), DateTimeKind.Utc),
        RequestedSources = [InventoryType.RetainerMarket.ToString()],
        ObservedSources = [InventoryType.RetainerMarket.ToString()],
    };
}
