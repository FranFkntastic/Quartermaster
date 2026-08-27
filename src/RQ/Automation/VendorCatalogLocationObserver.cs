using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Vendors;

namespace RQ.Automation;

/// <summary>
/// Holds the current vendor catalog behind an immutable reference so framework
/// ticks can swap in a new instance when observations promote pending vendors.
/// </summary>
public sealed class MutableVendorCatalogSource
{
    private GilVendorCatalog current;

    public MutableVendorCatalogSource(GilVendorCatalog initial)
    {
        current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public GilVendorCatalog Current => Volatile.Read(ref current);

    public void Replace(GilVendorCatalog next)
    {
        if (next is null)
            return;
        Volatile.Write(ref current, next);
    }
}

/// <summary>
/// Watches the object table for vendor NPCs whose offers are pending a location
/// and records their live territory and position. Observed locations outrank
/// static sheet resolution: an NPC with pending offers becomes executable the
/// first time the player shares a zone with it. Dynamic spawn mechanics are
/// irrelevant — only where the NPC actually is.
/// </summary>
public sealed class VendorCatalogLocationObserver : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly MutableVendorCatalogSource catalogSource;
    private readonly DalamudVendorLocationObserver observer;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private DateTimeOffset nextScanAt;

    public VendorCatalogLocationObserver(
        IObjectTable objects,
        IClientState clientState,
        MutableVendorCatalogSource catalogSource,
        DalamudVendorLocationObserver observer,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.objects = objects ?? throw new ArgumentNullException(nameof(objects));
        this.clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        this.catalogSource = catalogSource ?? throw new ArgumentNullException(nameof(catalogSource));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Call once per framework tick. Scans at most every <see cref="ScanInterval"/>;
    /// each scan records every visible event NPC whose id has pending offers, then
    /// promotes any newly located vendors into the catalog.
    /// </summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (now < nextScanAt || clientState.TerritoryType == 0)
            return;
        nextScanAt = now.Add(ScanInterval);

        var catalog = catalogSource.Current;
        var pending = catalog.PendingByNpcId;
        if (pending.Count == 0)
        {
            // Nothing left to learn — skip object-table scans entirely.
            return;
        }

        var promoted = false;
        var territoryId = clientState.TerritoryType;
        foreach (var obj in objects)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc)
                continue;
            var npcId = obj.BaseId;
            if (!pending.ContainsKey(npcId))
                continue;

            observer.Observe(npcId, territoryId, obj.Position);
            promoted |= Promote(catalog, npcId, territoryId, obj.Position);
        }

        if (promoted)
            log.Info("Vendor catalog updated from live observations; a newly seen vendor is now purchasable.");
    }

    private bool Promote(GilVendorCatalog catalog, uint npcId, uint territoryId, System.Numerics.Vector3 position)
    {
        var routes = DalamudGilVendorCatalogBuilder.ResolveTravelRoutesForTerritory(dataManager, territoryId);
        var routeAetheryteIds = routes
            .Select(route => route.AetheryteId)
            .Distinct()
            .ToArray();
        var next = catalog.WithObservedLocation(npcId, territoryId, position, routes, routeAetheryteIds);
        if (ReferenceEquals(next, catalog))
            return false;
        catalogSource.Replace(next);
        return true;
    }

    public void Dispose()
    {
    }
}
