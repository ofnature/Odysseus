using Odysseus.Services.Quest;

namespace Odysseus.Tests;

public class QuestSnapshotTests
{
    private static QuestSnapshot At(ushort quest, byte seq, params byte[] vars)
        => new(quest, seq, vars);

    [Fact]
    public void Unavailable_never_looks_like_progress()
    {
        // Sequence 0 is real progress (accepted, at the start). A null reader must be distinguishable.
        var none = QuestSnapshot.Unavailable;
        Assert.False(none.IsAvailable);
        Assert.False(none.IsReadyToComplete);
        Assert.False(none.Satisfies(new byte?[] { null, null, null, null, null, null }));

        var atStart = At(1622, 0, 0, 0, 0, 0, 0, 0);
        Assert.True(atStart.IsAvailable);
    }

    [Fact]
    public void Sequence_255_is_the_hand_in()
    {
        Assert.True(At(1622, 255, 0, 0, 0, 0, 0, 0).IsReadyToComplete);
        Assert.False(At(1622, 3, 0, 0, 0, 0, 0, 0).IsReadyToComplete);
    }

    [Fact]
    public void Satisfies_matches_the_path_data_convention()
    {
        // From 1622_Mogwin's Trial: "0 0 0 0 0 0 -> 16 16 0 0 0 32", flags [null,null,null,null,null,32].
        var before = At(1622, 1, 0, 0, 0, 0, 0, 0);
        var after = At(1622, 1, 16, 16, 0, 0, 0, 32);
        byte?[] mask = [null, null, null, null, null, 32];

        Assert.False(before.Satisfies(mask));
        Assert.True(after.Satisfies(mask));
    }

    [Fact]
    public void Satisfies_is_a_bitmask_not_an_equality()
    {
        // Second step of the same quest: "16 16 0 0 0 32 -> 32 17 0 0 0 160". 160 = 128|32, so a mask
        // of 32 must still be satisfied after the second step even though the byte is no longer 32.
        var afterSecond = At(1622, 1, 32, 17, 0, 0, 0, 160);
        Assert.True(afterSecond.Satisfies([null, null, null, null, null, 32]));
        Assert.True(afterSecond.Satisfies([null, null, null, null, null, 128]));
        Assert.False(afterSecond.Satisfies([null, null, null, null, null, 64]));
    }

    [Fact]
    public void Satisfies_rejects_a_malformed_mask()
    {
        var live = At(1622, 1, 16, 16, 0, 0, 0, 32);
        Assert.False(live.Satisfies([null, 32]));
        Assert.False(live.Satisfies([]));
    }
}
