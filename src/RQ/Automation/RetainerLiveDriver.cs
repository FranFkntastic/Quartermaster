using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using Lumina.Excel.Sheets;
using RQ.Operations;

namespace RQ.Automation;

public sealed class RetainerLiveDriver : IRetainerTransferDriver
{
    private const string RetainerList = "RetainerList";
    private const string SelectString = "SelectString";
    private const string InventoryLarge = "InventoryRetainerLarge";
    private const string InventorySmall = "InventoryRetainer";
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly DalamudSummoningBellInteractor bell;
    private readonly DalamudRetainerCrystalTransfer crystals;
    private RetainerRouteCandidate? active;

    public RetainerLiveDriver(
        IFramework framework,
        IGameGui gameGui,
        IDataManager dataManager,
        IPluginLog log,
        IObjectTable objects,
        ITargetManager targets,
        ISigScanner sigScanner)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.log = log;
        bell = new(objects, targets, dataManager);
        crystals = new(sigScanner, gameGui, framework, log);
    }

    public async Task RequireRetainerListAsync(CancellationToken cancellationToken)
    {
        var state = await framework.RunOnTick(() => (List: IsReady(RetainerList), Inventory: IsInventoryReady(), Menu: IsCommandMenuReady()), cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (state.Inventory || state.Menu)
            throw new InvalidOperationException("Close current retainer interaction before starting transfer.");
        if (state.List)
            return;
        SummoningBellInteractionResult? interaction = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            interaction = await framework.RunOnTick(bell.TryInteract, cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (interaction.State == SummoningBellInteractionState.Unavailable)
                throw new InvalidOperationException(interaction.Message);
            if (interaction.Submitted)
                break;
            await framework.DelayTicks(1).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (interaction is not { Submitted: true })
            throw new InvalidOperationException(interaction?.Message ?? "No summoning bell was available.");
        await WaitUntilAsync(() => IsReady(RetainerList), "retainer list", cancellationToken).ConfigureAwait(false);
    }

    public async Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken)
    {
        active = null;
        var result = await framework.RunOnTick(() => SelectRetainer(candidate.RetainerName), cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
        await WaitUntilAsync(IsCommandMenuReady, $"command menu for {candidate.RetainerName}", cancellationToken).ConfigureAwait(false);
        var verified = await framework.RunOnTick(() => VerifyActive(candidate.RetainerId), cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            throw new InvalidOperationException(verified.Message);
        active = candidate;
    }

    public async Task OpenInventoryAsync(CancellationToken cancellationToken)
    {
        var selected = await framework.RunOnTick(() => SelectCommand(2378), cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!selected.Success)
            throw new InvalidOperationException(selected.Message);
        await WaitUntilAsync(IsInventoryReady, "retainer inventory", cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
        framework.RunOnTick(() => DalamudRetainerInventory.ScanLoadedStacks(itemIds), cancellationToken: cancellationToken).WaitAsync(cancellationToken);

    public async Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken)
    {
        var verified = await framework.RunOnTick(() => VerifyActive(active?.RetainerId ?? 0), cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            return new(false, 0, "RetainerIdentityMismatch", verified.Message);
        var pending = await framework.RunOnTick(() => OpenContext(stack, quantity), cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!pending.Success)
            return new(false, 0, "ContextOpenFailed", pending.Message);
        var selection = await PollAsync(() => SelectContextEntry(pending.Label, stack), 30, cancellationToken).ConfigureAwait(false);
        if (!selection.Success)
            return new(false, 0, "ContextSelectionFailed", selection.Message);
        if (pending.NeedsQuantity)
        {
            var submitted = await PollAsync(() => SubmitQuantity(pending.Quantity), 30, cancellationToken).ConfigureAwait(false);
            if (!submitted.Success)
                return new(false, 0, "QuantityFailed", submitted.Message);
        }
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var result = await framework.RunOnTick(() => VerifyRetrieval(stack, pending.Quantity, pending.PlayerBefore), cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Success)
                return result;
            await framework.DelayTicks(1).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(false, 0, "TransferNotObserved", $"Retrieval was not observed for item {stack.ItemId}.");
    }

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
        framework.RunOnTick(() => DalamudInventoryStackScanner.ScanLoadedStacks([InventoryType.Crystals], itemIds), cancellationToken: cancellationToken).WaitAsync(cancellationToken);

    public async Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken)
    {
        var verified = await framework.RunOnTick(() => VerifyActive(active?.RetainerId ?? 0), cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        return verified.Success
            ? await crystals.DepositAsync(stack, quantity, cancellationToken).ConfigureAwait(false)
            : new(false, 0, "RetainerIdentityMismatch", verified.Message);
    }

    public async Task CloseRetainerAsync(CancellationToken cancellationToken)
    {
        await framework.RunOnTick(CloseInventory, cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        await WaitUntilAsync(IsCommandMenuReady, "retainer command menu", cancellationToken).ConfigureAwait(false);
        var quit = await framework.RunOnTick(() => SelectCommand(2383), cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!quit.Success)
            throw new InvalidOperationException(quit.Message);
        await WaitUntilAsync(() => IsReady(RetainerList), "retainer list after close", cancellationToken).ConfigureAwait(false);
        active = null;
    }

    public unsafe void CancelActive()
    {
        CloseInventory();
        foreach (var addonName in new[] { "InputNumeric", "ContextMenu", SelectString })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon is not null && addon->IsReady && addon->IsVisible)
                addon->Close(true);
        }
        active = null;
    }

    private async Task WaitUntilAsync(Func<bool> predicate, string state, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            if (await framework.RunOnTick(predicate, cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false))
                return;
            await framework.DelayTicks(1).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Timed out waiting for {state}.");
    }

    private async Task<DriverAction> PollAsync(Func<DriverAction> action, int attempts, CancellationToken cancellationToken)
    {
        DriverAction result = new(false, "Action did not become ready.");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            result = await framework.RunOnTick(action, cancellationToken: cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Success)
                return result;
            await framework.DelayTicks(1).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private unsafe DriverAction SelectRetainer(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(RetainerList, 1);
        if (addon is null || !addon->IsReady || !addon->IsVisible)
            return new(false, "Retainer list is not ready.");
        const int first = 3;
        const int stride = 10;
        const int activeOffset = 8;
        var entries = new List<RetainerListEntry>();
        for (var index = 0; index < 10; index++)
        {
            var valueIndex = first + index * stride;
            if (valueIndex + activeOffset >= addon->AtkValuesCount)
                break;
            var value = addon->AtkValues + valueIndex;
            var rowName = value->Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString
                ? value->GetValueAsString()
                : string.Empty;
            var active = addon->AtkValues + valueIndex + activeOffset;
            entries.Add(new(rowName, active->Type == AtkValueType.Bool && active->Byte != 0));
        }
        var selectedIndex = RetainerUiAutomationText.FindRetainerListIndex(entries, name);
        if (selectedIndex is not null)
        {
            var values = stackalloc AtkValue[4];
            values[0] = new() { Type = AtkValueType.Int, Int = 2 };
            values[1] = new() { Type = AtkValueType.UInt, UInt = (uint)selectedIndex.Value };
            addon->FireCallback(4, values, true);
            return new(true, $"Selected {name}.");
        }
        return new(false, $"Retainer '{name}' was not visible in retainer list.");
    }

    private unsafe bool IsCommandMenuReady()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectString, 1);
        return addon is not null && addon->AtkUnitBase.IsReady && addon->AtkUnitBase.IsVisible && FindEntry(addon, ResolveAddonText(2378)) >= 0;
    }

    private unsafe DriverAction SelectCommand(uint addonRow)
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectString, 1);
        if (addon is null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return new(false, "Retainer command menu is unavailable.");
        var index = FindEntry(addon, ResolveAddonText(addonRow));
        if (index < 0)
            return new(false, $"Retainer command entry {addonRow} is unavailable.");
        addon->AtkUnitBase.FireCallbackInt(index);
        return new(true, "Retainer command selected.");
    }

    private static unsafe int FindEntry(AddonSelectString* addon, string target)
    {
        var popup = addon->PopupMenu.PopupMenu;
        for (var index = 0; index < popup.EntryCount; index++)
            if (RetainerUiAutomationText.IsSelectStringEntryMatch(popup.EntryNames[index].ToString(), target))
                return index;
        return -1;
    }

    private unsafe PendingRetrieval OpenContext(DalamudInventoryStack stack, int requested)
    {
        if (requested <= 0 || active is null)
            return PendingRetrieval.Fail("Invalid retrieval request.");
        var manager = InventoryManager.Instance();
        if (manager == null)
            return PendingRetrieval.Fail("Inventory manager is unavailable.");
        var container = manager->GetInventoryContainer(stack.Container);
        if (container == null || !container->IsLoaded)
            return PendingRetrieval.Fail("Retainer source container is unavailable.");
        var slot = container->GetInventorySlot(stack.SlotIndex);
        if (slot == null || slot->ItemId != stack.ItemId || slot->Quantity != stack.Quantity)
            return PendingRetrieval.Fail("Exact retainer source slot changed before retrieval.");
        var quantity = Math.Min(requested, slot->Quantity);
        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        var context = AgentInventoryContext.Instance();
        if (retainerAgent is null || context is null)
            return PendingRetrieval.Fail("Retainer inventory context agent is unavailable.");
        context->OpenForItemSlot(stack.Container, stack.SlotIndex, 0, retainerAgent->GetAddonId());
        return new(true, quantity, quantity < slot->Quantity, ResolveAddonText(quantity < slot->Quantity ? 773u : 98u), CountPlayer(stack.ItemId), "Context menu requested.");
    }

    private unsafe DriverAction SelectContextEntry(string label, DalamudInventoryStack stack)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        var agent = AgentInventoryContext.Instance();
        if (addon is null || !addon->IsReady || !addon->IsVisible || agent is null || agent->TargetInventoryId != stack.Container || agent->TargetInventorySlotId != stack.SlotIndex)
            return new(false, "Waiting for exact retainer context menu.");
        var labels = new List<string>();
        foreach (var value in agent->EventParams)
            if (value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString)
                labels.Add(value.GetValueAsString());
        var index = RetainerUiAutomationText.FindContextMenuLabelIndex(labels, label);
        if (index is null)
            return new(false, $"Context entry '{label}' is unavailable.");
        var values = stackalloc AtkValue[5];
        values[0] = new() { Type = AtkValueType.Int, Int = 0 };
        values[1] = new() { Type = AtkValueType.Int, Int = index.Value };
        return addon->FireCallback(5, values, true) ? new(true, "Context action selected.") : new(false, "Context action callback was rejected.");
    }

    private unsafe DriverAction SubmitQuantity(int quantity)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("InputNumeric", 1);
        if (addon is null || !addon->IsReady || !addon->IsVisible)
            return new(false, "Waiting for quantity input.");
        addon->FireCallbackInt(quantity);
        return new(true, "Quantity submitted.");
    }

    private unsafe RetrievalResult VerifyRetrieval(DalamudInventoryStack original, int transferred, int playerBefore)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return new(false, 0, "ContainerUnavailable", "Inventory manager became unavailable.");
        var container = manager->GetInventoryContainer(original.Container);
        if (container == null || !container->IsLoaded)
            return new(false, 0, "ContainerUnavailable", "Retainer source container became unavailable.");
        var slot = container->GetInventorySlot(original.SlotIndex);
        if (slot == null)
            return new(false, 0, "SlotUnavailable", "Retainer source slot became unavailable.");
        var remaining = original.Quantity - transferred;
        var slotMatches = remaining <= 0 ? slot->ItemId != original.ItemId || slot->Quantity == 0 : slot->ItemId == original.ItemId && slot->Quantity == remaining;
        var playerAfter = CountPlayer(original.ItemId);
        if (slotMatches && playerAfter - playerBefore == transferred)
            return new(true, transferred, "TransferVerified", $"Verified {transferred}x item {original.ItemId}: player {playerBefore}->{playerAfter}.");
        return new(false, 0, "TransferPending", "Waiting for matching retainer-slot and player-inventory deltas.");
    }

    private static unsafe DriverAction VerifyActive(ulong expected)
    {
        var manager = RetainerManager.Instance();
        var current = manager == null ? null : manager->GetActiveRetainer();
        return current != null && expected > 0 && current->RetainerId == expected
            ? new(true, "Retainer identity verified.")
            : new(false, "Active retainer identity does not match planned stable ID.");
    }

    private static unsafe int CountPlayer(uint itemId)
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return 0;
        var total = 0;
        foreach (var type in RQ.Inventory.InventoryScanner.PlayerBags.Append(InventoryType.Crystals))
        {
            var container = manager->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded)
                continue;
            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot != null && slot->ItemId == itemId)
                    total += slot->Quantity;
            }
        }
        return total;
    }

    private unsafe bool IsReady(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return addon is not null && addon->IsReady && addon->IsVisible;
    }

    private bool IsInventoryReady() => IsReady(InventoryLarge) || IsReady(InventorySmall);

    private unsafe void CloseInventory()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(InventoryLarge, 1);
        if (addon == null)
            addon = gameGui.GetAddonByName<AtkUnitBase>(InventorySmall, 1);
        if (addon != null && addon->IsReady && addon->IsVisible)
            addon->Close(true);
    }

    private string ResolveAddonText(uint rowId) => dataManager.GetExcelSheet<Addon>().GetRow(rowId).Text.ExtractText();
    private sealed record DriverAction(bool Success, string Message);
    private sealed record PendingRetrieval(bool Success, int Quantity, bool NeedsQuantity, string Label, int PlayerBefore, string Message)
    {
        public static PendingRetrieval Fail(string message) => new(false, 0, false, string.Empty, 0, message);
    }
}
