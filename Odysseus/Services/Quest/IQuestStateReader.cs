using System;
using System.Collections.Generic;

namespace Odysseus.Services.Quest;

/// <summary>
/// Reads quest progress straight out of the game. The one dependency everything else stands on:
/// resume, off-rails detection, quest selection and the run window all consume this and nothing
/// else. It is an interface so the tests can drive the pipeline without a game process, and so
/// the framework cut can ship with <see cref="NullQuestStateReader"/> while P0 builds the real one.
/// </summary>
public interface IQuestStateReader
{
    /// <summary>
    /// The live state of a specific accepted quest, or <see cref="QuestSnapshot.Unavailable"/> if
    /// it is not currently accepted (or nothing is readable). Never throws — a read failure is a
    /// UI state, reported through the fault callback the implementation was given.
    /// </summary>
    QuestSnapshot Read(ushort questId);

    /// <summary>Every quest currently in the journal, as live snapshots. Empty when unreadable.</summary>
    IReadOnlyList<QuestSnapshot> ReadAccepted();

    /// <summary>The character has already turned this quest in.</summary>
    bool IsComplete(ushort questId);

    /// <summary>The character has this quest in the journal right now.</summary>
    bool IsAccepted(ushort questId);
}

/// <summary>Framework stand-in: nothing readable, ever. Replaced by the real reader in P0.</summary>
public sealed class NullQuestStateReader : IQuestStateReader
{
    public QuestSnapshot Read(ushort questId) => QuestSnapshot.Unavailable;

    public IReadOnlyList<QuestSnapshot> ReadAccepted() => Array.Empty<QuestSnapshot>();

    public bool IsComplete(ushort questId) => false;

    public bool IsAccepted(ushort questId) => false;
}
