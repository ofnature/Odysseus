using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace Odysseus.Config;

/// <summary>
/// Persisted settings. Deliberately flat and small for the framework cut — sections are added as
/// their phases land (see the plan doc's Phases).
/// </summary>
[Serializable]
public sealed class OdysseusConfig : IPluginConfiguration, Services.Run.IRunPolicy
{
    // IRunPolicy — the controller reads these live.
    bool Services.Run.IRunPolicy.HandOffSoloDuties => HandOffSoloDuties;
    bool Services.Run.IRunPolicy.HandOffDuties => HandOffDutiesToTheseus;
    bool Services.Run.IRunPolicy.ContinueToNextQuest => ContinueToNextQuest;
    int Services.Run.IRunPolicy.StopAtLevel => StopAtLevel;
    bool Services.Run.IRunPolicy.ConfirmBeforeResume => EnableResume && ConfirmBeforeResume;

    /// <summary>Bumped when a migration is needed; migrations live in <c>OdysseusPlugin</c>.</summary>
    public int Version { get; set; } = 1;

    // ── Master ──

    /// <summary>
    /// Master switch. Off means Odysseus never drives movement, dialogue or combat — it only
    /// observes. Default OFF: a quest runner that starts walking the moment it is installed is a
    /// bad neighbour.
    /// </summary>
    public bool Enabled { get; set; }

    // ── Run ──

    /// <summary>
    /// Keep going into the next MSQ quest once the current one completes. Off means one quest at
    /// a time and stop, which is what you want while a path is still being proven.
    /// </summary>
    public bool ContinueToNextQuest { get; set; } = true;

    /// <summary>
    /// Stop when the character reaches this level. 0 = no level stop. Useful for keeping an alt
    /// under a duty's sync level, or parking a trial account before its cap.
    /// </summary>
    public int StopAtLevel { get; set; }

    /// <summary>
    /// Which Grand Company to join when the story asks (The Company You Keep). 0 = not chosen —
    /// the run stops at that fork and says so. 1 Maelstrom, 2 Twin Adder, 3 Immortal Flames.
    /// Ignored once the character has joined one.
    /// </summary>
    public byte PreferredGrandCompany { get; set; }

    /// <summary>
    /// Let TextAdvance choose optional quest rewards (its own priority: gil, vendor value, gear
    /// coffers, gear for the current job). Off means the reward window waits for you — the run
    /// pauses there and says so.
    /// </summary>
    public bool PickQuestRewards { get; set; } = true;

    // ── Priority quests ──

    /// <summary>The list, in priority order. Only meaningful while <see cref="PersistPriorityList"/> is on.</summary>
    public List<ushort> PriorityQuests { get; set; } = [];

    /// <summary>Keep the priority list across sessions. Off = it lasts until the client closes.</summary>
    public bool PersistPriorityList { get; set; } = true;

    /// <summary>Drop priority entries automatically once the game says the quest is complete.</summary>
    public bool AutoRemoveCompletedPriority { get; set; } = true;

    // ── The Wake (resume) ──

    /// <summary>
    /// Pick up an interrupted quest where the game says it stopped, rather than restarting the
    /// quest. The whole point of the Wake — on by default, with the prompt below as the valve.
    /// </summary>
    public bool EnableResume { get; set; } = true;

    /// <summary>
    /// Ask before resuming instead of resuming automatically. Off = auto-resume; game state is
    /// authoritative so confidence is not the question it was for dungeons.
    /// </summary>
    public bool ConfirmBeforeResume { get; set; }

    // ── Handoffs ──

    /// <summary>Hand solo instanced duties to BossMod Reborn's AI. Off = stop at the entrance and wait for you.</summary>
    public bool HandOffSoloDuties { get; set; } = true;

    /// <summary>Hand full duties (dungeons, trials) inside a quest to Theseus. Off = stop at the entrance and wait for you.</summary>
    public bool HandOffDutiesToTheseus { get; set; } = true;

    // ── Fleet ──

    /// <summary>
    /// Publish this character's quest position on the Daedalus relay so the fleet window on any
    /// box can show it. Read-only: nothing on the wire changes what this box does.
    /// </summary>
    public bool PublishFleetStatus { get; set; } = true;

    /// <summary>A peer unheard from for this long is drawn as stale in the fleet window.</summary>
    public float PeerStaleSeconds { get; set; } = 10f;

    // ── Look ──

    /// <summary>Day (light blue) or Dusk (slate). Applied to every Odysseus window.</summary>
    public Windows.ThemeMode Theme { get; set; } = Windows.ThemeMode.Day;

    /// <summary>
    /// Main window shows only the essentials — state, quest, step, progress, one control row — and
    /// folds everything else away. For running in the corner of the screen.
    /// </summary>
    public bool CompactMode { get; set; }

    // ── Diagnostics ──

    /// <summary>Show the debug section and verbose step logging.</summary>
    public bool DebugMode { get; set; }
}
