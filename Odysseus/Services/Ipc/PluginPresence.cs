using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;

namespace Odysseus.Services.Ipc;

/// <summary>
/// Which of the plugins Odysseus leans on are actually loaded right now.
///
/// <para>
/// Odysseus does quest logic, dialogue choice and the last leg of movement; it deliberately does
/// not do pathfinding, teleporting, cutscene skipping, boss mechanics, dungeons or rotations.
/// Missing dependencies are therefore a first-class UI state, not a crash — the settings window
/// shows a chip per dependency so "why isn't it moving" is answerable at a glance.
/// </para>
///
/// <para>
/// <b>Hard</b> dependencies are needed to walk any quest at all. <b>Soft</b> ones are needed only
/// for the step types that hand off to them; without them those steps stop and wait for you.
/// </para>
/// </summary>
public sealed class PluginPresence
{
    // ── Hard ──

    /// <summary>Pathfinding and movement.</summary>
    public const string VnavmeshInternalName = "vnavmesh";

    /// <summary>Aetheryte teleport + aethernet travel — most cross-zone movement in the path data.</summary>
    public const string LifestreamInternalName = "Lifestream";

    /// <summary>Dialogue advance and cutscene skip. It skips text; Odysseus still makes the choices.</summary>
    public const string TextAdvanceInternalName = "TextAdvance";
    public const string CharonInternalName = "Charon";

    // ── Soft ──

    /// <summary>Rotation engine for quest combat, and the LAN relay the fleet window rides.</summary>
    public const string DaedalusInternalName = "Daedalus";

    /// <summary>Solo instanced duties. Reborn is the fork this fleet runs; upstream is accepted as a fallback.</summary>
    public const string BossModRebornInternalName = "BossModReborn";
    public const string BossModInternalName = "BossMod";

    /// <summary>The other duty-AI this fleet can be pointed at.</summary>
    public const string MinervaInternalName = "Minerva";

    /// <summary>The two names <see cref="DutyAiProvider"/> takes, as the settings combo spells them.</summary>
    public const string BossModRebornProvider = "BossMod Reborn";
    public const string MinervaProvider = "Minerva";

    /// <summary>Full duties (dungeons, trials) inside a quest.</summary>
    public const string TheseusInternalName = "Theseus";

    private readonly IDalamudPluginInterface _pluginInterface;

    public PluginPresence(IDalamudPluginInterface pluginInterface)
        => _pluginInterface = pluginInterface;

    public bool Vnavmesh => IsLoaded(VnavmeshInternalName);

    public const string AriadneInternalName = "Ariadne";
    public bool Ariadne => IsLoaded(AriadneInternalName);

    /// <summary>Mirrors the config: which pathing plugin a run needs present.</summary>
    public string PathingProvider { get; set; } = VnavIpc.VnavmeshProvider;
    private bool UsesAriadne => string.Equals(PathingProvider, VnavIpc.AriadneProvider, StringComparison.OrdinalIgnoreCase);
    public bool Pathing => UsesAriadne ? Ariadne : Vnavmesh;

    public bool Lifestream => IsLoaded(LifestreamInternalName);

    public bool TextAdvance => IsLoaded(TextAdvanceInternalName);

    /// <summary>Charon presses Esc in cutscenes (Odysseus leases it for the run); the one skip Odysseus cannot do itself.</summary>
    public bool Charon => IsLoaded(CharonInternalName);

    public bool Daedalus => IsLoaded(DaedalusInternalName);

    /// <summary>
    /// Which plugin is asked to fight a solo duty. Set from the config, the same way
    /// <see cref="PathingProvider"/> is — the setting is a pointer, not a second feature.
    /// </summary>
    public string DutyAiProvider { get; set; } = BossModRebornProvider;

    public bool UsesMinerva => string.Equals(DutyAiProvider, MinervaProvider, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The chat command that hands a fight to the chosen duty AI, or takes it back — null when it
    /// has no such switch.
    ///
    /// <para>
    /// BossMod Reborn's AI is a mode, switched with <c>/bmrai</c>. Minerva's is not one: it
    /// publishes guidance for a rotation plugin to act on (must-not-act, safe spots, presets) and
    /// exposes no on/off command or gate. So with Minerva chosen nothing is sent, rather than a
    /// command going to a plugin that has never heard of it.
    /// </para>
    /// </summary>
    public static string? DutyAiCommand(string provider, bool on)
        => string.Equals(provider, MinervaProvider, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"/bmrai {(on ? "on" : "off")}";

    /// <summary>The chosen duty-AI is loaded. Upstream BossMod counts for the Reborn setting; it serves the same gates.</summary>
    public bool BossMod => UsesMinerva
        ? IsLoaded(MinervaInternalName)
        : IsLoaded(BossModRebornInternalName) || IsLoaded(BossModInternalName);

    public bool Theseus => IsLoaded(TheseusInternalName);

    /// <summary>
    /// Everything required to walk a quest is present.
    ///
    /// <para>
    /// TextAdvance used to be on this list and is no longer needed at all. Odysseus advances the
    /// talk box, answers the skip-cutscene prompt, presses Accept and Complete, picks optional
    /// rewards, and fills and hands over the request window itself. The one thing it cannot do is
    /// press Esc in a cutscene; Charon does that (TextAdvance still would, if it is installed).
    /// That costs time rather than correctness, so it is advice (<see cref="AdviceSummary"/>).
    /// </para>
    /// </summary>
    public bool CoreReady => Pathing && Lifestream;

    /// <summary>What is missing that a run can live without, or empty. Shown as a notice, never a gate.</summary>
    /// <param name="skipCutscenes">Cutscenes are set to Skip — only then does a missing Esc matter.</param>
    public string AdviceSummary(bool skipCutscenes)
        => skipCutscenes && !Charon && !TextAdvance
            ? "Charon not loaded — cutscenes play in full. Install Charon to have them skipped."
            : string.Empty;

    /// <summary>
    /// Human-readable reason a run cannot start, or empty when it can. Named so the UI and the run
    /// controller give the user the same sentence.
    /// </summary>
    public string MissingSummary()
    {
        var missing = new List<string>();
        if (!Pathing) missing.Add(UsesAriadne ? "Ariadne" : "vnavmesh");
        if (!Lifestream) missing.Add("Lifestream");
        return missing.Count == 0 ? string.Empty : "Missing: " + string.Join(", ", missing);
    }

    private bool IsLoaded(string internalName)
    {
        try
        {
            return _pluginInterface.InstalledPlugins.Any(p =>
                string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase)
                && p.IsLoaded
                && !p.IsOutdated);
        }
        catch
        {
            // Fail closed: an unreadable plugin list means we cannot promise the dependency.
            return false;
        }
    }
}
