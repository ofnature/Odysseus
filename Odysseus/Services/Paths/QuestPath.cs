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
}

/// <summary>A dialogue answer the step needs. Prompts and answers are the game's own text keys, never display text.</summary>
public sealed record DialogueChoice(string Type, string? Prompt, string? Answer, bool? Yes);

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
    public uint? NotInInventory { get; set; }
    /// <summary>True when nothing was specified.</summary>
    public bool IsEmpty
        => InTerritory is null && NotInTerritory is null && QuestsCompleted is null && QuestsAccepted is null
           && Flying is null && CompletionQuestVariablesFlags is null && AetheryteUnlocked is null && NotInInventory is null;
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
    public string? AetheryteShortcut { get; set; }
    /// <summary>[from, to] aethernet shard names within a city.</summary>
    public string[]? AethernetShortcut { get; set; }
    /// <summary>Six-slot bitmask this step's completion sets in the quest variables; null when unknown.</summary>
    public byte?[]? CompletionQuestVariablesFlags { get; set; }
    public List<DialogueChoice>? DialogueChoices { get; set; }
    public SkipConditions? SkipConditions { get; set; }
    public EnemySpawnType? EnemySpawnType { get; set; }
    public List<uint>? KillEnemyDataIds { get; set; }
    public int? MinimumKillCount { get; set; }
    public ushort? PickUpQuestId { get; set; }
    public uint? AetherCurrentId { get; set; }
    public uint? ItemId { get; set; }
    public string? Emote { get; set; }
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
        or StepKind.AttuneAetherCurrent or StepKind.None or StepKind.Say or StepKind.Emote or StepKind.EquipRecommended;

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
    public const int CurrentFormatVersion = 1;

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
