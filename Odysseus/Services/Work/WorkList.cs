using System.Collections.Generic;
using System.Linq;
using Odysseus.Services.Deliveries;

namespace Odysseus.Services.Work;

/// <summary>The kinds of job a day's work is made of.</summary>
public enum WorkKind
{
    /// <summary>One allied society's dailies.</summary>
    SocietyDailies = 1,

    /// <summary>Deliveries for one custom delivery client, by route.</summary>
    Delivery = 2,
}

/// <summary>
/// One job on the list.
/// </summary>
/// <param name="TargetId">The society's id, or the delivery client's.</param>
/// <param name="Route">Which of a client's three routes; ignored for a society.</param>
/// <param name="Count">How many allowances to spend, or 0 for "as many as are left".</param>
public sealed record WorkItem(WorkKind Kind, uint TargetId, DeliveryRoute Route = DeliveryRoute.Craft, int Count = 0)
{
    /// <summary>Two entries for the same thing are the same job however many times they are added.</summary>
    public bool SameJobAs(WorkItem other)
        => Kind == other.Kind && TargetId == other.TargetId
           && (Kind != WorkKind.Delivery || Route == other.Route);

    public string Describe(string name) => Kind switch
    {
        WorkKind.SocietyDailies => $"{name} dailies{(Count > 0 ? $" ×{Count}" : "")}",
        WorkKind.Delivery => $"{name} {Route.ToString().ToLowerInvariant()} deliveries{(Count > 0 ? $" ×{Count}" : "")}",
        _ => name,
    };
}

/// <summary>
/// The day's work, in the order it should be done.
///
/// <para>
/// Deliberately a list and not a planner. Choosing what goes on it — allowance arithmetic, bonus
/// weeks, what is worth doing — is a separate job (M8); this is the thing that job will produce, and
/// the thing a run walks. Building it first means the ordering can be proven with two entries by
/// hand before anything is clever about filling it.
/// </para>
///
/// <para>
/// Pure and persistable: no game, no plugins, so the order can be pinned by tests.
/// </para>
/// </summary>
public sealed class WorkList
{
    private readonly List<WorkItem> _items = [];

    public IReadOnlyList<WorkItem> Items => _items;

    public int Count => _items.Count;

    /// <summary>Add a job, or replace the matching one so a second click changes the count rather than queueing twice.</summary>
    public void Add(WorkItem item)
    {
        var existing = _items.FindIndex(i => i.SameJobAs(item));
        if (existing >= 0)
            _items[existing] = item;
        else
            _items.Add(item);
    }

    public bool Remove(WorkItem item)
    {
        var at = _items.FindIndex(i => i.SameJobAs(item));
        if (at < 0) return false;
        _items.RemoveAt(at);
        return true;
    }

    public void Clear() => _items.Clear();

    public bool Contains(WorkItem item) => _items.Any(i => i.SameJobAs(item));

    /// <summary>Move a job up or down. Out-of-range indices are left alone rather than throwing.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count || from == to)
            return;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);
    }

    public void Replace(IEnumerable<WorkItem> items)
    {
        _items.Clear();
        foreach (var item in items)
            Add(item);
    }
}
