using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

/// <summary>
/// Pins GatherBuddy's collectable rotation, which is what <see cref="CollectableRotation"/>
/// implements rule for rule. The yields are a live node's: Scour 450, Meticulous 400 — or the weak
/// field node at 200/150 where a test says so.
/// </summary>
public class CollectableRotationTests
{
    private static CollectableState At(int collectability, int integrity = 4, int gp = 800, bool scrutiny = false,
        int reserve = 0, int target = 600, int scour = 450, int meticulous = 400, int minimum = 600)
        => new(collectability, target, integrity, gp, scrutiny, reserve, scour, meticulous, minimum);

    [Fact]
    public void Clearing_the_bar_collects_rather_than_spending_more_integrity()
    {
        Assert.Equal(GatherMove.Collect, CollectableRotation.Next(At(600)));
        Assert.Equal(GatherMove.Collect, CollectableRotation.Next(At(1000)));
    }

    [Fact]
    public void The_last_point_banks_what_is_worth_banking_and_gambles_the_rest()
    {
        // At or above the floor: take it. Below it a Collect is junk, and Meticulous's chance of
        // leaving the point untouched is the only route to anything.
        Assert.Equal(GatherMove.Collect, CollectableRotation.Next(At(600, integrity: 1)));
        Assert.Equal(GatherMove.Collect, CollectableRotation.Next(At(450, integrity: 1, minimum: 450)));
        Assert.Equal(GatherMove.Meticulous, CollectableRotation.Next(At(300, integrity: 1)));
    }

    [Fact]
    public void A_swing_that_finishes_now_is_taken_meticulous_first()
    {
        // Meticulous reaches: preferred, since it may save the point as well.
        Assert.Equal(GatherMove.Meticulous, CollectableRotation.Next(At(250)));   // 250 + 400 ≥ 600

        // Only Scour reaches.
        Assert.Equal(GatherMove.Scour, CollectableRotation.Next(At(175)));        // 175 + 400 < 600 ≤ 175 + 450
    }

    [Fact]
    public void When_nothing_finishes_scrutiny_goes_first_while_the_gp_lasts()
    {
        // The field node: 200 a Scour against 600. No single action reaches, so Scrutiny — it
        // costs no integrity — and once it is up, Meticulous is the raise.
        var weak = At(0, target: 600, scour: 200, meticulous: 150, integrity: 5, gp: 894);
        Assert.Equal(GatherMove.Scrutiny, CollectableRotation.Next(weak));
        Assert.Equal(GatherMove.Meticulous, CollectableRotation.Next(weak with { ScrutinyUsed = true }));
    }

    [Fact]
    public void GP_that_is_not_there_or_is_spoken_for_is_not_spent()
    {
        var weak = At(0, target: 600, scour: 200, meticulous: 150, integrity: 5);
        Assert.Equal(GatherMove.Meticulous, CollectableRotation.Next(weak with { Gp = 199 }));
        Assert.Equal(GatherMove.Scrutiny, CollectableRotation.Next(weak with { Gp = 200 }));

        // A reserve keeps enough back that the next node is not started empty-handed.
        Assert.Equal(GatherMove.Meticulous, CollectableRotation.Next(weak with { Gp = 500, GpReserve = 400 }));
        Assert.Equal(GatherMove.Scrutiny, CollectableRotation.Next(weak with { Gp = 600, GpReserve = 400 }));
    }

    [Fact]
    public void Without_the_windows_numbers_it_takes_the_reliable_action()
    {
        Assert.Equal(GatherMove.Scour, CollectableRotation.Next(At(0, scour: 0, meticulous: 0)));
    }

    [Fact]
    public void The_weak_field_node_plays_scrutiny_meticulous_to_the_bar_then_collects()
    {
        // 5 integrity, Scour 200, Meticulous 150, target 600: Scrutiny in front of each raise,
        // Meticulous as the raise, Collect the moment the bar clears. The boost's size is the
        // game's business; 300 stands in for a boosted Meticulous.
        var moves = new List<GatherMove>();
        var state = At(0, target: 600, scour: 200, meticulous: 150, integrity: 5, gp: 894);
        var boosted = false;
        for (var i = 0; i < 12 && state.IntegrityLeft > 0 && moves.Count(m => m == GatherMove.Collect) == 0; i++)
        {
            var move = CollectableRotation.Next(state);
            moves.Add(move);
            state = move switch
            {
                GatherMove.Scrutiny => Boost(state),
                GatherMove.Meticulous => state with
                {
                    Collectability = state.Collectability + (boosted ? 300 : 150),
                    IntegrityLeft = state.IntegrityLeft - 1,
                    ScrutinyUsed = Spend(),
                },
                GatherMove.Collect => state with { Collectability = 0, IntegrityLeft = state.IntegrityLeft - 1 },
                _ => state,
            };
        }

        CollectableState Boost(CollectableState s) { boosted = true; return s with { ScrutinyUsed = true, Gp = s.Gp - 200 }; }
        bool Spend() { boosted = false; return false; }

        Assert.Equal([
            GatherMove.Scrutiny, GatherMove.Meticulous,     // 300
            GatherMove.Scrutiny, GatherMove.Meticulous,     // 600 — the bar is cleared
            GatherMove.Collect,                             // banked, two integrity still standing
        ], moves);
    }

    [Fact]
    public void The_action_ids_are_the_ones_the_sheet_gives()
    {
        Assert.Equal(240u, CollectableRotation.ActionId(GatherMove.Collect, 16));
        Assert.Equal(815u, CollectableRotation.ActionId(GatherMove.Collect, 17));
        Assert.Equal(22184u, CollectableRotation.ActionId(GatherMove.Meticulous, 16));
        Assert.Equal(22188u, CollectableRotation.ActionId(GatherMove.Meticulous, 17));
        Assert.Equal(22185u, CollectableRotation.ActionId(GatherMove.Scrutiny, 16));
    }
}
