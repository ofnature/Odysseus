using System;
using System.Collections.Generic;
using System.Linq;

namespace Odysseus.Services.Quest;

/// <summary>Why a priority entry is or is not runnable right now — shown beside each row.</summary>
public enum PriorityStatus
{
    Ready,
    /// <summary>In the journal — will run first (it is already under way).</summary>
    Accepted,
    Complete,
    /// <summary>Prerequisites not met, or an alternative was taken.</summary>
    Locked,
    LevelTooLow,
    NoPath,
    UnknownQuest,
}

public sealed record PriorityEntry(ushort QuestId, string Name, PriorityStatus Status, string Detail);

/// <summary>What the list needs to know to judge readiness. Interface so tests can fake it.</summary>
public interface IPriorityWorld
{
    bool IsComplete(ushort questId);
    bool IsAccepted(ushort questId);
    bool HasPath(ushort questId);
    int PlayerLevel { get; }
    CharacterFacts Character { get; }
}

/// <summary>
/// The user's ordered list of quests to run before the Main Scenario continues.
///
/// <para>
/// Order is priority. At every quest boundary — Start, and each roll-on after a completion —
/// the first entry that is <i>ready</i> runs before the next MSQ quest: not complete, unlocked
/// (prerequisites met, no alternative taken), level not above the character's, and with a
/// stored path. An entry already accepted counts as ready first of all, so an in-progress
/// priority quest is finished before anything else. Never interrupts a running quest.
/// </para>
///
/// <para>
/// Two toggles, both the user's: <see cref="Persist"/> keeps the list across sessions (off =
/// the list lives until the client closes); <see cref="AutoRemoveCompleted"/> drops entries once
/// the game says they are done, so the list is always "what is left".
/// </para>
/// </summary>
public sealed class PriorityList
{
    private readonly List<ushort> _ids = new();
    private readonly QuestCatalog _catalog;
    private readonly Action<IReadOnlyList<ushort>>? _persist;

    /// <param name="catalog">For names, levels, prerequisites.</param>
    /// <param name="initial">Saved ids to start from (only honoured when <paramref name="persist"/> is true).</param>
    /// <param name="persist">Whether the list is saved; read live.</param>
    /// <param name="save">Writes the current ids to wherever they persist. Called on every change while persisting.</param>
    public PriorityList(QuestCatalog catalog, IEnumerable<ushort>? initial, bool persist, Action<IReadOnlyList<ushort>>? save)
    {
        _catalog = catalog;
        _persist = save;
        Persist = persist;
        if (persist && initial is not null)
            foreach (var id in initial)
                if (id != 0 && !_ids.Contains(id))
                    _ids.Add(id);
    }

    public bool Persist { get; private set; }
    public bool AutoRemoveCompleted { get; set; }

    public IReadOnlyList<ushort> Ids => _ids;
    public int Count => _ids.Count;
    public bool Contains(ushort id) => _ids.Contains(id);

    public event Action? Changed;

    /// <summary>Turn persistence on (writes the current list) or off (the saved copy is cleared).</summary>
    public void SetPersist(bool persist)
    {
        Persist = persist;
        _persist?.Invoke(persist ? _ids : Array.Empty<ushort>());
    }

    public bool Add(ushort id)
    {
        if (id == 0 || _ids.Contains(id))
            return false;
        _ids.Add(id);
        Touched();
        return true;
    }

    public bool Insert(int index, ushort id)
    {
        if (id == 0 || _ids.Contains(id))
            return false;
        _ids.Insert(Math.Clamp(index, 0, _ids.Count), id);
        Touched();
        return true;
    }

    public bool Remove(ushort id)
    {
        if (!_ids.Remove(id))
            return false;
        Touched();
        return true;
    }

    public void Clear()
    {
        if (_ids.Count == 0) return;
        _ids.Clear();
        Touched();
    }

    public bool Move(ushort id, int delta)
    {
        var i = _ids.IndexOf(id);
        if (i < 0) return false;
        var j = Math.Clamp(i + delta, 0, _ids.Count - 1);
        if (i == j) return false;
        _ids.RemoveAt(i);
        _ids.Insert(j, id);
        Touched();
        return true;
    }

    /// <summary>Drop every completed entry (when <see cref="AutoRemoveCompleted"/>). Returns how many went.</summary>
    public int Prune(Func<ushort, bool> isComplete)
    {
        if (!AutoRemoveCompleted) return 0;
        var removed = _ids.RemoveAll(id => isComplete(id));
        if (removed > 0) Touched();
        return removed;
    }

    /// <summary>Every entry with its live status, in priority order.</summary>
    public IReadOnlyList<PriorityEntry> Entries(IPriorityWorld world)
        => _ids.Select(id => Judge(id, world)).ToList();

    /// <summary>The entry to run now, or null: an accepted one first, else the first ready one.</summary>
    public ushort? NextReady(IPriorityWorld world)
    {
        ushort? firstReady = null;
        foreach (var id in _ids)
        {
            var status = Judge(id, world).Status;
            if (status == PriorityStatus.Accepted && world.HasPath(id))
                return id;
            if (status == PriorityStatus.Ready && firstReady is null)
                firstReady = id;
        }
        return firstReady;
    }

    private PriorityEntry Judge(ushort id, IPriorityWorld world)
    {
        var listing = _catalog.ById(id);
        if (listing is null)
            return new PriorityEntry(id, $"Quest {id}", PriorityStatus.UnknownQuest, "not in the game's quest sheet");
        if (world.IsComplete(id))
            return new PriorityEntry(id, listing.Name, PriorityStatus.Complete, "complete");
        if (world.IsAccepted(id))
            return new PriorityEntry(id, listing.Name, world.HasPath(id) ? PriorityStatus.Accepted : PriorityStatus.NoPath,
                world.HasPath(id) ? "in the journal — runs first" : "in the journal, but no stored path");
        if (!listing.IsUnlockedBy(world.IsComplete) || !QuestCatalog.IsObtainable(listing, world.IsComplete, world.Character))
            return new PriorityEntry(id, listing.Name, PriorityStatus.Locked, "prerequisites not met");
        if (world.PlayerLevel > 0 && listing.ClassJobLevel > world.PlayerLevel)
            return new PriorityEntry(id, listing.Name, PriorityStatus.LevelTooLow, $"needs level {listing.ClassJobLevel}");
        if (!world.HasPath(id))
            return new PriorityEntry(id, listing.Name, PriorityStatus.NoPath, "no stored path — import or record");
        return new PriorityEntry(id, listing.Name, PriorityStatus.Ready, $"Lv {listing.ClassJobLevel}");
    }

    private void Touched()
    {
        if (Persist)
            _persist?.Invoke(_ids);
        Changed?.Invoke();
    }
}
