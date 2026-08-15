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

    // ── Soft ──

    /// <summary>Rotation engine for quest combat, and the LAN relay the fleet window rides.</summary>
    public const string DaedalusInternalName = "Daedalus";

    /// <summary>Solo instanced duties. Reborn is the fork this fleet runs; upstream is accepted as a fallback.</summary>
    public const string BossModRebornInternalName = "BossModReborn";
    public const string BossModInternalName = "BossMod";

    /// <summary>Full duties (dungeons, trials) inside a quest.</summary>
    public const string TheseusInternalName = "Theseus";

    /// <summary>Test oracle only (see <see cref="QuestionableOracle"/>). Never required.</summary>
    public const string QuestionableInternalName = "Questionable";

    private readonly IDalamudPluginInterface _pluginInterface;

    public PluginPresence(IDalamudPluginInterface pluginInterface)
        => _pluginInterface = pluginInterface;

    public bool Vnavmesh => IsLoaded(VnavmeshInternalName);

    public bool Lifestream => IsLoaded(LifestreamInternalName);

    public bool TextAdvance => IsLoaded(TextAdvanceInternalName);

    public bool Daedalus => IsLoaded(DaedalusInternalName);

    public bool BossMod => IsLoaded(BossModRebornInternalName) || IsLoaded(BossModInternalName);

    public bool Theseus => IsLoaded(TheseusInternalName);

    public bool Questionable => IsLoaded(QuestionableInternalName);

    /// <summary>Everything required to walk a quest is present.</summary>
    public bool CoreReady => Vnavmesh && Lifestream && TextAdvance;

    /// <summary>
    /// Human-readable reason a run cannot start, or empty when it can. Named so the UI and the run
    /// controller give the user the same sentence.
    /// </summary>
    public string MissingSummary()
    {
        var missing = new List<string>();
        if (!Vnavmesh) missing.Add("vnavmesh");
        if (!Lifestream) missing.Add("Lifestream");
        if (!TextAdvance) missing.Add("TextAdvance");
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
