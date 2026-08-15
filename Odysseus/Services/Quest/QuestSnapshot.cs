using System;

namespace Odysseus.Services.Quest;

/// <summary>
/// One read of the game's own record of where a quest stands.
///
/// <para>
/// This is the whole checkpoint. Quest id, sequence and the six quest-work variables are all
/// server-side state: they survive a crash, a logout and a client restart, and they cost nothing
/// to read again. Nothing Odysseus persists is ever more authoritative than a fresh one of these —
/// where a saved ledger and this disagree, this wins.
/// </para>
///
/// <para>
/// <see cref="Variables"/> is what makes sub-sequence resume possible: path steps that carry a
/// <c>CompletionQuestVariablesFlags</c> mask are matched against it to find the first step whose
/// effect has not yet happened. Steps without a mask replay from the sequence's first step.
/// </para>
/// </summary>
public readonly record struct QuestSnapshot(
    ushort QuestId,
    byte Sequence,
    ReadOnlyMemory<byte> Variables)
{
    /// <summary>The game exposes six quest-work bytes per accepted quest.</summary>
    public const int VariableCount = 6;

    /// <summary>The sequence the game uses for "quest ready to hand in" — the terminal block in every path file.</summary>
    public const byte CompleteSequence = 255;

    /// <summary>
    /// Nothing readable — no active quest, or the reader has not been wired up. Distinct from
    /// "sequence 0" on purpose: sequence 0 means <i>accepted and at the start</i>, which is real
    /// progress, and a null reader must never look like that.
    /// </summary>
    public static QuestSnapshot Unavailable { get; } = new(0, 0, ReadOnlyMemory<byte>.Empty);

    /// <summary>True when this describes an actual accepted quest.</summary>
    public bool IsAvailable => QuestId != 0;

    /// <summary>The quest is at its hand-in step.</summary>
    public bool IsReadyToComplete => IsAvailable && Sequence == CompleteSequence;

    /// <summary>
    /// Whether every bit in <paramref name="mask"/> is set in the live variables. A null entry in
    /// the mask means "don't care" for that slot — the same convention the path data uses.
    /// </summary>
    public bool Satisfies(ReadOnlySpan<byte?> mask)
    {
        if (!IsAvailable || mask.Length != VariableCount || Variables.Length < VariableCount)
            return false;

        var live = Variables.Span;
        for (var i = 0; i < VariableCount; i++)
        {
            if (mask[i] is not { } wanted)
                continue;
            if ((live[i] & wanted) != wanted)
                return false;
        }
        return true;
    }

    public override string ToString()
        => IsAvailable
            ? $"quest {QuestId} seq {Sequence} vars [{string.Join(' ', Variables.ToArray())}]"
            : "no quest";
}
