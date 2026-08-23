using System;
using System.Globalization;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Odysseus.Services.Run;

namespace Odysseus.Services.Gathering;

/// <summary>
/// The real <see cref="IGatherWorld"/>: the gathering windows, the bag, and the actions.
///
/// <para>
/// The collectable window (<c>GatheringMasterpiece</c>) names most of what is needed — integrity,
/// GP, the action buttons — so those come from the struct rather than being scraped. Current
/// collectability has no named field, and is read from the text node that displays it (id 47, under
/// the "Collectability" label): what the window shows is what the rotation reads, which is a better
/// guarantee than any offset.
/// </para>
/// </summary>
public sealed unsafe class GameGatherWorld : IGatherWorld
{
    /// <summary>The window that appears on a collectable node.</summary>
    public const string MasterpieceAddon = "GatheringMasterpiece";

    /// <summary>The ordinary node window, where the item slot is chosen.</summary>
    public const string GatheringAddon = "Gathering";

    /// <summary>
    /// The text node showing current collectability, under the "Collectability" label in the middle
    /// of the window (read off a live node, 2026-08-22). A node id rather than an AtkValue index
    /// because it is what the window actually displays: if it reads right on screen, it reads right
    /// here.
    /// </summary>
    private const uint CollectabilityNodeId = 47;

    /// <summary>
    /// The numbers printed under the primary action buttons — what a Scour and a Meticulous are
    /// each worth on this character, at this node, with these buffs. Read rather than modelled: the
    /// window has already done the arithmetic that depends on gathering rating and node bonuses.
    /// Node ids from a live window, 2026-08-22.
    /// </summary>
    private const uint ScourYieldNodeId = 84;
    private const uint MeticulousYieldNodeId = 108;

    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly GameStepWorld _steps;
    private readonly Action<string> _log;

    public GameGatherWorld(
        IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui,
        GameStepWorld steps, Action<string> log)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
        _gameGui = gameGui;
        _steps = steps;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;

    public uint TerritoryId => _clientState.TerritoryType;

    public Vector3 PlayerPosition => _objectTable.LocalPlayer?.Position ?? Vector3.Zero;

    public bool IsOccupied => _steps.IsOccupied;

    public uint CurrentClassJob => _steps.CurrentClassJob;

    public bool IsMounted => _steps.IsMounted;

    public void Dismount() => _steps.Dismount();

    /// <summary>
    /// Equip a gearset for the class. Matched on the gearset's own class or its parent, so a job
    /// saved over its base class still counts; the highest-level one wins where there are several.
    /// </summary>
    public bool EquipGearsetFor(uint classJobId, out string reason)
    {
        try
        {
            GearsetInfo? best = null;
            foreach (var set in _steps.Gearsets())
                if ((set.ClassJobId == classJobId || set.ParentClassJobId == classJobId)
                    && (best is null || set.Level > best.Level))
                    best = set;

            if (best is null)
            {
                reason = $"no gearset for class {classJobId} — save one for it, then start again";
                return false;
            }

            reason = string.Empty;
            if (_steps.EquipGearset(best.Id))
                return true;

            reason = $"gearset {best.Id} for class {classJobId} was refused";
            return false;
        }
        catch (Exception ex)
        {
            reason = $"switching to class {classJobId} failed: {ex.Message}";
            return false;
        }
    }

    // Facing is the executor's; finding and opening a node is not.
    public void FaceDataId(uint dataId) => _steps.FaceDataId(dataId);

    /// <summary>
    /// The node itself, found by kind and id rather than by the executor's NPC lookup.
    ///
    /// <para>
    /// Two differences from talking to a person, and either alone stops a run dead. A node is an
    /// <see cref="ObjectKind.GatheringPoint"/>, and the executor's lookup also insists on
    /// <c>IsTargetable</c> — which is a question about NPCs, not about a rock — so a node that is
    /// perfectly workable can read as absent, and then the run walks to a coordinate it can never
    /// arrive at.
    /// </para>
    /// </summary>
    private IGameObject? Node(uint dataId)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return null;
        var found = _objectTable
            .Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.BaseId == dataId)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();

        if (found is null)
            SayWhatIsThereInstead(dataId, player.Position);
        return found;
    }

    private readonly HashSet<uint> _reportedMissing = [];

    /// <summary>
    /// Once per node id, name the gathering points that <i>are</i> in range. If the atlas and the
    /// object table disagree about what a node's id is, this is the line that says so — otherwise
    /// the only symptom is a run walking in circles round a rock it can plainly see.
    /// </summary>
    private void SayWhatIsThereInstead(uint dataId, Vector3 from)
    {
        if (!_reportedMissing.Add(dataId))
            return;

        var nearby = _objectTable
            .Where(o => o.ObjectKind == ObjectKind.GatheringPoint)
            .Select(o => (o.BaseId, Distance: Vector3.Distance(o.Position, from)))
            .Where(o => o.Distance < 100f)
            .OrderBy(o => o.Distance)
            .Take(6)
            .Select(o => $"{o.BaseId} at {o.Distance:F0}y")
            .ToList();

        _log(nearby.Count == 0
            ? $"Node {dataId} is not here, and no gathering point is within 100y."
            : $"Node {dataId} is not here. Gathering points that are: {string.Join(", ", nearby)}.");
    }

    public bool IsDataIdSpawned(uint dataId) => Node(dataId) is not null;

    /// <summary>
    /// The nearest node that is up, of any that yield what we came for.
    ///
    /// <para>
    /// <c>IsTargetable</c> is the whole point of this: a node that has been worked out stays in the
    /// object table where it was until it moves, and it is not targetable. Without that test a run
    /// walks back to the spent one for ever; with it, "what is up right now" is a single query, and
    /// the recorded coordinates go back to being what they are — a list of places nodes can be,
    /// most of them empty at any moment.
    /// </para>
    /// </summary>
    public (uint NodeId, Vector3 Position, float Distance)? NearestLiveNode(IReadOnlyCollection<uint> nodeIds)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return null;

        // An empty set means "any node", which is what a diagnostic wants: whatever you are stood
        // next to, whether or not it is on the list we came for.
        var best = _objectTable
            .Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable
                        && (nodeIds.Count == 0 || nodeIds.Contains(o.BaseId)))
            .Select(o => (o.BaseId, o.Position, Distance: Vector3.Distance(o.Position, player.Position)))
            .OrderBy(o => o.Distance)
            .FirstOrDefault();

        return best.BaseId == 0 ? null : (best.BaseId, best.Position, best.Distance);
    }

    public float? DistanceToDataId(uint dataId)
    {
        var node = Node(dataId);
        return node is null ? null : Vector3.Distance(node.Position, PlayerPosition);
    }

    public Vector3? PositionOfDataId(uint dataId) => Node(dataId)?.Position;

    /// <summary>
    /// Open the node. <c>OpenObjectInteraction</c>, not the <c>InteractWithObject</c> that talks to
    /// people: the second is what the quest runner uses and works there, and does nothing at all on
    /// a mineral deposit. GatherBuddy uses the first, which is how this was settled.
    /// </summary>
    public bool TryInteractWithDataId(uint dataId)
    {
        try
        {
            if (Node(dataId) is not { } node)
                return false;

            var system = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            if (system == null)
                return false;

            // The object and nothing else. Writing the hard target first was mine, on a hunch, and
            // GatherBuddy does not do it — worth not doing on a call that can wedge the client.
            system->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)node.Address);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Opening node {dataId} failed: {ex.Message}");
            return false;
        }
    }
    public void Log(string message) => _log(message);

    /// <summary>
    /// Collectables stack by collectability, so a bag can hold the same item at several values and
    /// only the ones at or above the threshold are worth anything to the client.
    /// </summary>
    public int CollectableCount(uint itemId, int minimumCollectability)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return 0;

            var total = 0;
            foreach (var type in Inventories)
            {
                var container = manager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded) continue;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != itemId) continue;
                    if (slot->SpiritbondOrCollectability >= minimumCollectability)
                        total += slot->Quantity;
                }
            }
            return total;
        }
        catch (Exception ex)
        {
            _log($"Counting collectable {itemId} failed: {ex.Message}");
            return 0;
        }
    }

    private static readonly InventoryType[] Inventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>
    /// A node is open when <i>either</i> window is up. Interacting with a collectable node gives
    /// the plain <c>Gathering</c> list of its eight items first, and <c>GatheringMasterpiece</c>
    /// only appears once one of them is chosen — so watching for the masterpiece alone means never
    /// noticing the node opened, and interacting with it again, and again.
    /// </summary>
    /// <summary>
    /// Gathering has begun. The condition flag is the game's own answer and arrives before either
    /// window does — GatherBuddy waits on exactly this — so watching for an addon alone means
    /// firing the interaction again while one is already in flight.
    /// </summary>
    public bool NodeOpen
        => _condition[ConditionFlag.Gathering] || Masterpiece() is not null || GatheringList() is not null;

    public bool ItemListOpen => GatheringList() is not null;

    public bool ExecutingAction => _condition[ConditionFlag.ExecutingGatheringAction];

    private AtkUnitBase* GatheringList()
    {
        var addon = _gameGui.GetAddonByName(GatheringAddon);
        return addon.IsNull || !addon.IsVisible ? null : (AtkUnitBase*)addon.Address;
    }

    public CollectableState? Collectable
    {
        get
        {
            try
            {
                var window = Masterpiece();
                if (window == null) return null;

                // GatherBuddy's reader, index for index: collectability at 13, integrity at 62 and
                // 63, Scour's stated gain at 48, Meticulous's at 51. Values rather than text nodes
                // because the text nodes did not read, and theirs is the version proven in the
                // field for years.
                var integrity = Value(&window->AtkUnitBase, 62);
                if (integrity is null)
                {
                    if (!_reportedUnreadable)
                    {
                        _reportedUnreadable = true;
                        _log("The collectable window is open but integrity would not read — dumping it:");
                        DumpMasterpiece(window);
                    }
                    return null;
                }

                // Whether Scrutiny is up is not exposed here; the runner remembers that it used it.
                return new CollectableState(
                    Value(&window->AtkUnitBase, 13) ?? 0, Target: 0, integrity.Value,
                    Number(window->GPLeftover) ?? 0, ScrutinyUsed: false,
                    ScourYield: Value(&window->AtkUnitBase, 48) ?? 0,
                    MeticulousYield: Value(&window->AtkUnitBase, 51) ?? 0);
            }
            catch (Exception ex)
            {
                _log($"Reading the collectable window failed: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>One of a window's values as a number, whichever integer type it arrived as.</summary>
    private static int? Value(AtkUnitBase* window, int index)
    {
        if (index < 0 || index >= window->AtkValuesCount) return null;
        var v = window->AtkValues[index];
        return v.Type switch
        {
            AtkValueType.Int => v.Int,
            AtkValueType.UInt => (int)v.UInt,
            _ => null,
        };
    }

    /// <summary>Fire a window's callback with two int values, update-state set — the shape GatherBuddy uses.</summary>
    private static void FireTwo(AtkUnitBase* window, int first, int second)
    {
        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = first;
        values[1].Type = AtkValueType.Int;
        values[1].Int = second;
        window->FireCallback(2, values, true);
    }

    /// <summary>
    /// Choose the item on an ordinary node, which is what opens the collectable window.
    ///
    /// <para>
    /// The eight slots are tab nodes 17 to 24 in the <c>Gathering</c> window, in the order the list
    /// shows them, and the window's callback takes that position — so picking the third row is
    /// <c>FireCallbackInt(2)</c>. Which row holds the wanted item comes from the window's own
    /// values, where the eight item ids sit; the whole array is logged once so a layout change
    /// shows up as a line in the log rather than as a wrong item gathered.
    /// </para>
    /// </summary>
    public bool SelectSlotFor(uint itemId)
    {
        try
        {
            var window = GatheringList();
            if (window == null)
                return false;

            DumpGatheringValuesOnce(window);

            var slot = SlotOf(window, itemId);
            if (slot is < 0 or >= SlotCount)
            {
                _log($"Could not identify which of this node's {SlotCount} slots holds item {itemId}.");
                return false;
            }

            // Once. A callback fired into this window with the wrong row is the likeliest thing to
            // leave it unpopulated and the character stuck mid-gather, so it gets one attempt and
            // then says so rather than trying another number.
            if (!_slotFired.Add(itemId))
            {
                _log($"Already chose slot {slot + 1} for item {itemId} and the window did not follow — not firing again.");
                return false;
            }

            _log($"Choosing slot {slot + 1} of {SlotCount} for item {itemId}.");

            // Two values — the slot and a trailing zero — with update-state set. GatherBuddy's own
            // slot click, verbatim: Callback.Fire(addon, true, index, 0). One bare int is a
            // different signature and the window ignores it, which read from outside as a row that
            // was chosen and a collectable window that never came.
            FireTwo(window, slot, 0);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Choosing the slot for item {itemId} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Which of the eight rows yields the item, or -1.
    ///
    /// <para>
    /// The window's values are one eleven-value block per row, starting at index 5, with the item
    /// id first in each block — so row <c>n</c>'s item is at <c>6 + 11n</c>. Read off a live window
    /// (2026-08-22) and self-checking: index 39 held 0 for the empty row the window showed as
    /// "NOTHING", and index 50 held 12, the Lightning Crystal in the fifth row.
    /// </para>
    /// </summary>
    private static int SlotOf(AtkUnitBase* window, uint itemId)
    {
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var index = FirstItemValue + slot * ValuesPerSlot;
            if (index >= window->AtkValuesCount)
                break;

            var value = window->AtkValues[index];
            var held = value.Type switch
            {
                AtkValueType.UInt => value.UInt,
                AtkValueType.Int => (uint)Math.Max(0, value.Int),
                _ => 0u,
            };
            if (held == itemId)
                return slot;
        }
        return -1;
    }

    /// <summary>The first row's item id, and the stride between rows.</summary>
    private const int FirstItemValue = 6;
    private const int ValuesPerSlot = 11;

    /// <summary>A node offers eight slots, whether or not each has something in it.</summary>
    private const int SlotCount = 8;

    private bool _dumpedGatheringValues;
    private readonly HashSet<uint> _slotFired = [];

    private void DumpGatheringValuesOnce(AtkUnitBase* window)
    {
        if (_dumpedGatheringValues) return;
        _dumpedGatheringValues = true;

        // The count first: whether rows six to eight are in here at all is the difference between
        // a slot we can find and one we cannot, and a truncated dump looks identical to a short one.
        var text = new System.Text.StringBuilder($"Gathering window values ({window->AtkValuesCount} of them):");
        for (var i = 0; i < window->AtkValuesCount && i < 120; i++)
        {
            var v = window->AtkValues[i];
            var shown = v.Type switch
            {
                AtkValueType.Int => v.Int.ToString(CultureInfo.InvariantCulture),
                AtkValueType.UInt => v.UInt.ToString(CultureInfo.InvariantCulture),
                AtkValueType.Bool => v.Bool ? "true" : "false",
                AtkValueType.String or AtkValueType.WideString or AtkValueType.ConstString => "\"text\"",
                _ => v.Type.ToString(),
            };
            text.Append($" [{i}]={shown}");
        }
        _log(text.ToString());
    }

    public bool UseAction(uint actionId)
    {
        try
        {
            var manager = ActionManager.Instance();
            return manager != null && manager->UseAction(ActionType.Action, actionId);
        }
        catch (Exception ex)
        {
            _log($"Gathering action {actionId} failed: {ex.Message}");
            return false;
        }
    }

    public void StopMoving() => _steps.StopMoving();

    private bool _reportedUnreadable;

    /// <summary>Every value and the named text nodes of the collectable window, for the log.</summary>
    private void DumpMasterpiece(AddonGatheringMasterpiece* window)
    {
        try
        {
            var text = new System.Text.StringBuilder($"Masterpiece values ({window->AtkValuesCount} of them):");
            for (var i = 0; i < window->AtkValuesCount && i < 60; i++)
            {
                var v = window->AtkValues[i];
                var shown = v.Type switch
                {
                    AtkValueType.Int => v.Int.ToString(CultureInfo.InvariantCulture),
                    AtkValueType.UInt => v.UInt.ToString(CultureInfo.InvariantCulture),
                    AtkValueType.Bool => v.Bool ? "true" : "false",
                    AtkValueType.String or AtkValueType.WideString or AtkValueType.ConstString => "\"text\"",
                    _ => v.Type.ToString(),
                };
                text.Append($" [{i}]={shown}");
            }
            _log(text.ToString());
            _log($"Masterpiece text nodes: integrityLeft=\"{window->IntegrityLeftover->NodeText}\" " +
                 $"integrityTotal=\"{window->IntegrityTotal->NodeText}\" gp=\"{window->GPLeftover->NodeText}\" " +
                 $"collect47=\"{window->AtkUnitBase.GetTextNodeById(CollectabilityNodeId)->NodeText}\"");
        }
        catch (Exception ex)
        {
            _log($"Dumping the collectable window failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Everything both windows are carrying, for the log. The item-list values are where the eight
    /// slots' item ids live, and the only way to know which index holds which is to look at a real
    /// one — which is what this is for.
    /// </summary>
    public void DescribeOpenWindow()
    {
        try
        {
            var list = GatheringList();
            if (list != null)
            {
                _dumpedGatheringValues = false;
                DumpGatheringValuesOnce(list);
            }

            var masterpiece = Masterpiece();
            if (masterpiece != null)
                _log($"Collectable window: collectability={Number(masterpiece->AtkUnitBase.GetTextNodeById(CollectabilityNodeId))}, " +
                     $"integrity={Number(masterpiece->IntegrityLeftover)}/{Number(masterpiece->IntegrityTotal)}, " +
                     $"gp={Number(masterpiece->GPLeftover)}, scour={Number(masterpiece->AtkUnitBase.GetTextNodeById(ScourYieldNodeId))}, " +
                     $"meticulous={Number(masterpiece->AtkUnitBase.GetTextNodeById(MeticulousYieldNodeId))}");
        }
        catch (Exception ex)
        {
            _log($"Describing the open window failed: {ex.Message}");
        }
    }

    /// <summary>Every condition the game currently has set, which is what GatherBuddy logs here too.</summary>
    public string Conditions
    {
        get
        {
            try
            {
                var set = System.Enum.GetValues<ConditionFlag>()
                    .Where(f => _condition[f])
                    .Select(f => f.ToString());
                return string.Join(' ', set);
            }
            catch
            {
                return "unreadable";
            }
        }
    }

    /// <summary>A node is done with; the next one gets a fresh attempt at its slot.</summary>
    public void ForgetSlotAttempts() => _slotFired.Clear();

    /// <summary>
    /// Leave a node.
    ///
    /// <para>
    /// <b>Never <c>Close(true)</c>.</b> Forcing the addon shut while the game is mid-action —
    /// <c>Gathering ExecutingGatheringAction</c> was set every time — wedges the client so hard it
    /// has to be killed. That was the whole of the lock-up: interacting on its own was always fine,
    /// and it was this that followed which broke it.
    /// </para>
    ///
    /// <para>
    /// The window's own cancel is what Escape does, so that is what is sent. If it does not take,
    /// the node is left alone: an open window is a nuisance, and a client that needs killing is not.
    /// </para>
    /// </summary>
    public void CloseNode()
    {
        try
        {
            if (_condition[ConditionFlag.ExecutingGatheringAction])
            {
                _log("Not closing the node: an action is still running, and forcing it shut is what locks the client.");
                return;
            }

            var list = GatheringList();
            if (list != null)
            {
                var cancel = stackalloc AtkValue[1];
                cancel->Type = AtkValueType.Int;
                cancel->Int = CancelCallback;
                list->FireCallback(1, cancel, true);
            }
        }
        catch (Exception ex)
        {
            _log($"Leaving the node failed: {ex.Message}");
        }
    }

    /// <summary>What the window's own cancel takes — the same thing Escape sends.</summary>
    private const int CancelCallback = -1;

    private AddonGatheringMasterpiece* Masterpiece()
    {
        var addon = _gameGui.GetAddonByName(MasterpieceAddon);
        return addon.IsNull || !addon.IsVisible ? null : (AddonGatheringMasterpiece*)addon.Address;
    }

    /// <summary>The window's numbers are text; the game formats them with separators in some locales.</summary>
    private static int? Number(AtkTextNode* node)
    {
        if (node == null) return null;
        var text = node->NodeText.ToString();
        if (text.Length == 0) return null;
        Span<char> digits = stackalloc char[text.Length];
        var used = 0;
        foreach (var c in text)
            if (char.IsAsciiDigit(c))
                digits[used++] = c;
        return used == 0 ? null : int.Parse(digits[..used], CultureInfo.InvariantCulture);
    }

}
