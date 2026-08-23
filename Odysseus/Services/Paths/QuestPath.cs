using System;
using System.Collections.Generic;
using System.Numerics;

namespace Odysseus.Services.Paths;

/// <summary>
/// What a step does. Nineteen kinds were measured across the HW+SB MSQ corpus; four of them
/// (<see cref="Interact"/>, <see cref="AcceptQuest"/>, <see cref="CompleteQuest"/>,
/// <see cref="WalkTo"/>) are 86% of all steps and <see cref="Combat"/> takes it to 90%.
/// <see cref="Unknown"/> is what the importer emits for a kind it has never seen, so a new
/// upstream verb converts to a step that stops and says so rather than a step that vanishes.
/// </summary>
public enum StepKind
{
    Unknown,
    Interact,
    AcceptQuest,
    CompleteQuest,
    WalkTo,
    Combat,
    AttuneAetherCurrent,
    SinglePlayerDuty,
    Duty,
    AttuneAetheryte,
    AttuneAethernetShard,
    UseItem,
    Emote,
    Jump,
    EquipItem,
    None,
    Snipe,
    Dive,
    Say,
    EquipRecommended,
    // ── Seen only outside the MSQ (bundle-wide survey 2026-08-15) — named so Unknown means new ──
    Craft,
    Action,
    Gather,
    WaitForNpcAtPosition,
    PurchaseItem,
    SwitchClass,
    UpdateGearset,
    CreateGearset,
    Instruction,
    Fish,
    UnlockTaxiStand,
    RegisterFreeOrFavoredAetheryte,
    WaitForManualProgress,
    StatusOff,
}

/// <summary>How the enemies for a <see cref="StepKind.Combat"/> step come to exist.</summary>
public enum EnemySpawnType
{
    Unknown,
    /// <summary>They spawn when the player reaches the position; wait there and fight.</summary>
    AutoOnEnterArea,
    /// <summary>They spawn after interacting with <see cref="QuestStep.DataId"/>.</summary>
    AfterInteraction,
    /// <summary>They are ordinary overworld mobs; find and kill <see cref="QuestStep.KillEnemyDataIds"/>.</summary>
    OverworldEnemies,
    /// <summary>They spawn after using <see cref="QuestStep.ItemId"/>.</summary>
    AfterItemUse,
    /// <summary>They spawn after performing <see cref="QuestStep.Emote"/> at <see cref="QuestStep.DataId"/>.</summary>
    AfterEmote,
    /// <summary>They spawn after casting <see cref="QuestStep.ActionName"/> on <see cref="QuestStep.DataId"/>.</summary>
    AfterAction,
    /// <summary>Combat is optional: kill what is already here or on us, and skip cleanly when nothing is.</summary>
    FinishCombatIfAny,
}

/// <summary>A dialogue answer the step needs. Prompts and answers are the game's own text keys, never display text.</summary>
public sealed record DialogueChoice(string Type, string? Prompt, string? Answer, bool? Yes);

/// <summary>
/// One thing a <see cref="StepKind.Gather"/> or <see cref="StepKind.Fish"/> step wants.
///
/// <para>
/// Ids at or above <see cref="EventItemBase"/> are <i>event</i> items — quest-only things like
/// "Pristine Oak Branch" that come from a node the quest spawns. They are not in the Item sheet,
/// never appear in the bags a plain count can see, and no gathering plugin has them on a list. 15
/// of the 297 gather targets in the bundle are like that and none of them are in a class quest,
/// which is why they are named and stopped on rather than handed off.
/// </para>
/// </summary>
public sealed record GatherTarget(uint ItemId, int ItemCount)
{
    public const uint EventItemBase = 2_000_000;

    public bool IsEventItem => ItemId >= EventItemBase;
}

/// <summary>
/// One acceptable value for a quest variable slot: the whole byte, or just its high or low nibble.
/// The data writes <c>32</c>, <c>{"High": 3}</c> or <c>{"Low": 1}</c>.
/// </summary>
public sealed record VariableMatch(byte? Exact, byte? High, byte? Low)
{
    public bool Matches(byte value)
    {
        if (Exact is { } e) return value == e;
        if (High is { } h && (value >> 4) != h) return false;
        if (Low is { } l && (value & 0x0F) != l) return false;
        return High is not null || Low is not null;
    }
}

/// <summary>
/// A predicate over game state. Every field is optional; a null field is "don't care" and an
/// empty condition is always true. Evaluated by <c>Run.StepConditions</c>.
/// </summary>
public sealed class StepCondition
{
    public List<uint>? InTerritory { get; set; }
    public List<uint>? NotInTerritory { get; set; }
    public List<ushort>? QuestsCompleted { get; set; }
    public List<ushort>? QuestsAccepted { get; set; }
    /// <summary>"Unlocked" or "Locked" — whether flying is available in the current zone.</summary>
    public string? Flying { get; set; }
    /// <summary>Six-slot bitmask over the quest's variables; null slot = don't care.</summary>
    public byte?[]? CompletionQuestVariablesFlags { get; set; }
    public bool? AetheryteUnlocked { get; set; }
    /// <summary>Asks about the step's own item — see <see cref="ItemCondition"/>.</summary>
    public ItemCondition? Item { get; set; }
    /// <summary>True when nothing was specified.</summary>
    public bool IsEmpty
        => InTerritory is null && NotInTerritory is null && QuestsCompleted is null && QuestsAccepted is null
           && Flying is null && CompletionQuestVariablesFlags is null && AetheryteUnlocked is null && Item is null;
}

/// <summary>
/// A condition about the step's own <see cref="QuestStep.ItemId"/> and <see cref="QuestStep.ItemCount"/>.
///
/// <para>
/// This is what stops a Craft step remaking something already in the bag, and a PurchaseItem step
/// re-buying materials after a restart. The bundle carries it on 257 of its 402 Craft steps; we
/// parsed the wrong shape and so never evaluated it, which had exactly the effect the data was
/// written to prevent.
/// </para>
/// </summary>
public sealed class ItemCondition
{
    /// <summary>
    /// <c>false</c> — the common case — means "skip when the item <i>is</i> held". <c>true</c>
    /// inverts it: skip when it is not.
    /// </summary>
    public bool NotInInventory { get; set; }
}

/// <summary>Skip rules on a step: skip the whole step, or just its teleport, when the condition holds.</summary>
public sealed class SkipConditions
{
    public StepCondition? StepIf { get; set; }
    public StepCondition? AetheryteShortcutIf { get; set; }
    public StepCondition? AethernetShortcutIf { get; set; }
}

/// <summary>One thing to do. The unit the executor works in and the unit the editor patches.</summary>
public sealed class QuestStep
{
    public StepKind Kind { get; set; }
    /// <summary>The upstream verb name, kept verbatim so an <see cref="StepKind.Unknown"/> step can say what it was.</summary>
    public string? KindName { get; set; }
    public uint? DataId { get; set; }
    public Vector3? Position { get; set; }
    public uint TerritoryId { get; set; }
    public uint? TargetTerritoryId { get; set; }
    public float? StopDistance { get; set; }
    public bool Fly { get; set; }
    public bool? Mount { get; set; }
    public bool DisableNavmesh { get; set; }

    /// <summary>
    /// Get off the mount before this step, and do not get back on for it. For the places where
    /// being mounted is the problem — a walk that has to thread somewhere a chocobo will not fit,
    /// or an interaction the game refuses from the saddle.
    /// </summary>
    public bool Dismount { get; set; }
    public string? AetheryteShortcut { get; set; }
    /// <summary>[from, to] aethernet shard names within a city.</summary>
    public string[]? AethernetShortcut { get; set; }
    /// <summary>Six-slot bitmask this step's completion sets in the quest variables; null when unknown.</summary>
    public byte?[]? CompletionQuestVariablesFlags { get; set; }
    /// <summary>
    /// Six slots; a non-null slot lists the values that variable must have for this step to apply.
    /// This is how "use the item on three of these five objects" paths pick the three: each object's
    /// step is gated on the variables the game set when the quest was taken.
    /// </summary>
    public List<VariableMatch>?[]? RequiredQuestVariables { get; set; }
    /// <summary>For <see cref="StepKind.Action"/>: the action's name as the game spells it (resolved to an id at run time).</summary>
    public string? ActionName { get; set; }
    /// <summary>The action is placed on the ground at <see cref="Position"/> rather than cast on <see cref="DataId"/>.</summary>
    public bool GroundTarget { get; set; }

    /// <summary>Land before acting — the step is reached flying and must be done from the ground.</summary>
    public bool Land { get; set; }

    /// <summary>The variables allow this step (true when the step has no requirement).</summary>
    public bool RequiredVariablesMet(ReadOnlySpan<byte> variables)
    {
        if (RequiredQuestVariables is null) return true;
        for (var i = 0; i < RequiredQuestVariables.Length && i < variables.Length; i++)
        {
            var allowed = RequiredQuestVariables[i];
            if (allowed is null || allowed.Count == 0) continue;
            var ok = false;
            foreach (var m in allowed) if (m.Matches(variables[i])) { ok = true; break; }
            if (!ok) return false;
        }
        return true;
    }
    public List<DialogueChoice>? DialogueChoices { get; set; }
    public SkipConditions? SkipConditions { get; set; }
    public EnemySpawnType? EnemySpawnType { get; set; }
    public List<uint>? KillEnemyDataIds { get; set; }
    public int? MinimumKillCount { get; set; }
    public ushort? PickUpQuestId { get; set; }
    public uint? AetherCurrentId { get; set; }
    public uint? ItemId { get; set; }
    /// <summary>How many the step wants — Craft and PurchaseItem both carry it.</summary>
    public int? ItemCount { get; set; }
    /// <summary>
    /// For <see cref="StepKind.PurchaseItem"/>: the shop to pick out of the vendor's options, or
    /// null when the NPC has only one and interacting opens it.
    /// </summary>
    public uint? PurchaseShopId { get; set; }
    /// <summary>
    /// Which sheet <see cref="PurchaseShopId"/> is a row of. Every one measured is <c>GilShop</c>;
    /// the name is kept so a step naming some other kind of shop fails saying which rather than
    /// buying out of the wrong window.
    /// </summary>
    public string? PurchaseShopSheet { get; set; }
    /// <summary>
    /// For <see cref="StepKind.Gather"/> and <see cref="StepKind.Fish"/>: what to come back with.
    /// A step can want several things at once, which is why this is a list and not
    /// <see cref="ItemId"/>.
    /// </summary>
    public List<GatherTarget>? GatherItems { get; set; }
    /// <summary>
    /// For <see cref="StepKind.SwitchClass"/>: the class as the data names it ("Fisher",
    /// "Blue Mage"), or one of the three symbolic values <c>ConfiguredCombatJob</c>,
    /// <c>ConfiguredCraftingJob</c>, <c>QuestStartJob</c>.
    /// </summary>
    public string? TargetClass { get; set; }
    public string? Emote { get; set; }
    /// <summary>Text key for a <see cref="StepKind.Say"/> step, resolved against the quest's dialogue sheet.</summary>
    public string? ChatMessageKey { get; set; }
    public uint? ContentFinderConditionId { get; set; }
    /// <summary>For duty kinds: upstream considered the automated handoff usable.</summary>
    public bool? DutyEnabled { get; set; }
    public float? DelaySecondsAtStart { get; set; }
    public string? Comment { get; set; }

    /// <summary>
    /// The step can be run again after it has already succeeded without doing harm — which is
    /// what makes untagged sequences resumable by replay. Interacting with an NPC that has
    /// already advanced the sequence is a no-op; walking somewhere twice is a no-op; using an
    /// item twice may not be.
    /// </summary>
    public bool IsReplaySafe => Kind is StepKind.Interact or StepKind.AcceptQuest or StepKind.CompleteQuest
        or StepKind.WalkTo or StepKind.Combat or StepKind.AttuneAetheryte or StepKind.AttuneAethernetShard
        or StepKind.AttuneAetherCurrent or StepKind.None or StepKind.Say or StepKind.Emote or StepKind.EquipRecommended
        or StepKind.Action or StepKind.Instruction or StepKind.StatusOff or StepKind.SwitchClass
        // Both work to a target total read off the bag, so running them again makes up a shortfall
        // of zero and does nothing.
        or StepKind.Craft or StepKind.Gather
        // Equipping what is already worn is a no-op, and both gearset steps check before acting —
        // CreateGearset in particular would otherwise leave a duplicate behind on every replay.
        or StepKind.EquipItem or StepKind.CreateGearset or StepKind.UpdateGearset;

    public override string ToString()
        => $"{Kind}{(DataId is { } d ? $" {d}" : "")}{(Position is { } p ? $" @({p.X:F0},{p.Y:F0},{p.Z:F0})" : "")} in {TerritoryId}";
}

/// <summary>One quest sequence: the steps that take the game from sequence N to N+1.</summary>
public sealed class QuestSequence
{
    public byte Sequence { get; set; }
    /// <summary>Empty is legal and common — the game advances this sequence on its own (a duty ends, a cutscene plays).</summary>
    public List<QuestStep> Steps { get; set; } = [];
}

/// <summary>
/// One quest, in Odysseus's own format.
///
/// <para>
/// Converted once from the user's installed bundle by <see cref="QuestionableImporter"/> and then
/// owned outright: the store never re-reads the bundle, so a change upstream cannot break a path
/// that worked yesterday. <see cref="FormatVersion"/> is ours; <see cref="SourceHash"/> is what
/// lets the importer skip a file it has already converted.
/// </para>
/// </summary>
public sealed class QuestPath
{
    /// <summary>
    /// Bump whenever the converter learns something — a new step kind, a new field — so stored
    /// paths are re-converted instead of kept. <see cref="PathStore.ImportBundle"/> skips a quest
    /// only when its source hash <i>and</i> this version both match, so without a bump a path
    /// converted by an older build silently keeps its old parse.
    ///
    /// <para>
    /// 1 → 2 (2026-08-16): named the 14 non-MSQ verbs (Action, Craft, Gather, Instruction,
    /// StatusOff, …) and added ActionName, GroundTarget, RequiredQuestVariables, ChatMessageKey.
    /// </para>
    ///
    /// <para>
    /// 2 → 3 (2026-08-17): PurchaseItem's <c>PurchaseMenu</c>, SwitchClass's <c>TargetClass</c> and
    /// Gather/Fish's <c>ItemsToGather</c>. All three were dropped on the floor before, so a stored
    /// path for any of those verbs carries no shop, no class and nothing to gather, and must be
    /// re-converted rather than kept.
    /// </para>
    /// </summary>
    public const int CurrentFormatVersion = 3;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public ushort QuestId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Folder path inside the bundle, e.g. "3.x - Heavensward/MSQ/A-3.0" — the only classification the data carries.</summary>
    public string Category { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? LastChecked { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public List<QuestSequence> Sequences { get; set; } = [];

    /// <summary>The block for a live sequence number, or null when the path has none for it.</summary>
    public QuestSequence? Block(byte sequence)
    {
        foreach (var s in Sequences)
            if (s.Sequence == sequence)
                return s;
        return null;
    }

    public bool IsMainScenario => Category.Contains("/MSQ", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The current converter would get more out of this path than the one that wrote it did.
    ///
    /// <para>
    /// Version alone over-reports, and the over-report is not harmless. A path of nothing but
    /// Interact and AcceptQuest steps parses identically under every converter so far, so its
    /// version number is a fact about history rather than about the path. Worse, a quest dropped
    /// upstream is never in a bundle again — "Child Labor" (2813) is one — so it can never be
    /// re-converted, and counting it would be a warning nobody could ever act on.
    /// </para>
    ///
    /// <para>
    /// So the question asked is the useful one: does this path carry a step a later converter
    /// handles better? Either a verb it did not know (kept as <see cref="StepKind.Unknown"/> with
    /// its name) or one of the kinds that has since gained fields.
    /// </para>
    /// </summary>
    /// <summary>
    /// An allied society path — the bundle files them under "Allied Societies" per expansion and
    /// "Allied Society Quests (…)" for the repeatable dailies.
    ///
    /// <para>
    /// Worth knowing because these are run on the ground in base-game zones: the daily circuits
    /// were written to fly, and the zones they fly over were built before flight existed, so a
    /// flight path through them snags on scenery that later zones do not have.
    /// </para>
    /// </summary>
    public bool IsAlliedSociety
        => Category is { } c && c.Contains("Allied Societ", StringComparison.OrdinalIgnoreCase);

    public bool NeedsReconvert
    {
        get
        {
            if (FormatVersion >= CurrentFormatVersion)
                return false;
            foreach (var sequence in Sequences)
            foreach (var step in sequence.Steps)
            {
                if (step.Kind is StepKind.Craft or StepKind.Gather or StepKind.Fish
                    or StepKind.PurchaseItem or StepKind.SwitchClass)
                    return true;
                if (step.Kind == StepKind.Unknown && step.KindName is { Length: > 0 } named
                    && Enum.TryParse<StepKind>(named, ignoreCase: false, out _))
                    return true;
            }
            return false;
        }
    }

    public int StepCount
    {
        get
        {
            var n = 0;
            foreach (var s in Sequences) n += s.Steps.Count;
            return n;
        }
    }
}
