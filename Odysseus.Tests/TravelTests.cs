using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;
using Odysseus.Services.Travel;

namespace Odysseus.Tests;

public class AetheryteCatalogTests
{
    // A slice of the real sheet: (id, aetheryte PlaceName, zone PlaceName, territory).
    private static readonly AetheryteCatalog Catalog = new(
    [
        (70u, "Foundation", "Foundation", 418u),
        (75u, "Moghome", "The Churning Mists", 400u),
        (98u, "The Ala Mhigan Quarter", "The Lochs", 621u),
        (104u, "Rhalgr's Reach", "Rhalgr's Reach", 635u),
        (71u, "Falcon's Nest", "Coerthas Western Highlands", 397u),
        (135u, "Slitherbough", "The Rak'tika Greatwood", 817u),
        (2u, "New Gridania", "New Gridania", 132u),
    ]);

    [Theory]
    [InlineData("Ishgard", 70u)]                                    // city alias
    [InlineData("Gridania", 2u)]                                    // city alias
    [InlineData("The Churning Mists - Moghome", 75u)]               // article kept upstream
    [InlineData("Churning Mists - Moghome", 75u)]                   // article dropped upstream
    [InlineData("Lochs - Ala Mhigan Quarter", 98u)]                 // article dropped on both halves
    [InlineData("Rhalgr's Reach", 104u)]                            // bare aetheryte name
    [InlineData("Coerthas Western Highlands - Falcon's Nest", 71u)] // sheet-exact
    [InlineData("Rak'tika - Slitherbough", 135u)]                   // shortened zone
    [InlineData("rhalgr's reach", 104u)]                            // case-insensitive
    public void Resolves_every_spelling_the_bundle_uses(string name, uint expected)
        => Assert.Equal(expected, Catalog.Resolve(name));

    [Fact]
    public void Unknown_names_resolve_to_null_not_a_guess()
    {
        Assert.Null(Catalog.Resolve("Nowhere - Nothing"));
        Assert.Null(Catalog.Resolve(""));
    }

    [Fact]
    public void Territory_lookup_follows_the_id()
    {
        Assert.Equal(621u, Catalog.TerritoryOf(98));
        Assert.Null(Catalog.TerritoryOf(9999));
    }
}

public class TravelExecutorTests
{
    private static QuestStep Interact(uint territory, Vector3 pos, string? aetheryte = null, string[]? aethernet = null) => new()
    {
        Kind = StepKind.Interact, KindName = "Interact", DataId = 7, TerritoryId = territory, Position = pos,
        AetheryteShortcut = aetheryte, AethernetShortcut = aethernet,
    };

    private static FakeStepWorld World()
    {
        var w = new FakeStepWorld { ArriveOnMove = true, TerritoryId = 100 };
        w.Aetherytes["Lochs - Ala Mhigan Quarter"] = 98;
        w.AetheryteTerritories[98] = 621;
        w.Spawned.Add(7);
        return w;
    }

    private static void Ticks(StepExecutor ex, FakeStepWorld w, int n, double s = 0.5)
    {
        for (var i = 0; i < n && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(s); }
    }

    [Fact]
    public void Wrong_zone_with_a_shortcut_teleports_first_then_walks()
    {
        var w = World();
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));

        Ticks(ex, w, 2);
        Assert.Contains("Teleport 98", w.Calls);
        Assert.Equal(621u, w.TerritoryId);

        // Simulate the zone load: busy, then not.
        w.IsTravelBusy = true; Ticks(ex, w, 2); w.IsTravelBusy = false;
        Ticks(ex, w, 20);
        Assert.Contains(w.Calls, c => c.StartsWith("Move 50,0,0"));
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void Right_zone_and_close_by_walks_without_teleporting()
    {
        var w = World();
        w.TerritoryId = 621;
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));
        Ticks(ex, w, 20);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport"));
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void Right_zone_but_far_away_still_teleports()
    {
        var w = World();
        w.TerritoryId = 621;
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(1000, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));
        Ticks(ex, w, 2);
        Assert.Contains("Teleport 98", w.Calls);
    }

    [Fact]
    public void Skip_teleport_flag_walks_even_with_a_shortcut()
    {
        var w = World();
        w.TerritoryId = 621;
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(1000, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"), skipTeleport: true);
        Ticks(ex, w, 20);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport"));
    }

    [Fact]
    public void Wrong_zone_with_no_shortcut_fails_clearly_instead_of_pathing()
    {
        var w = World();
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0)));
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("territory 621", ex.FailReason);
        Assert.Contains("you are in 100", ex.FailReason);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Move"));
    }

    [Fact]
    public void Unknown_aetheryte_name_fails_naming_it()
    {
        var w = World();
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Made Up - Place"));
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("Made Up - Place", ex.FailReason);
    }

    [Fact]
    public void Refused_teleport_fails_with_the_lifestream_hint()
    {
        var w = World();
        w.TeleportAccepted = false;
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));
        Ticks(ex, w, 2);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("Lifestream", ex.FailReason);
    }

    [Fact]
    public void Aethernet_hop_follows_the_teleport_and_precedes_the_walk()
    {
        var w = World();
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter",
            aethernet: ["[Ala Mhigo] Aetheryte Plaza", "[Ala Mhigo] The Royal Menagerie"]));

        Ticks(ex, w, 2);                       // teleport issued
        w.IsTravelBusy = true; Ticks(ex, w, 2); w.IsTravelBusy = false;
        Ticks(ex, w, 3);                       // teleport wait resolves, aethernet issued
        Assert.Contains("Aethernet [Ala Mhigo] The Royal Menagerie", w.Calls);
        w.IsTravelBusy = true; Ticks(ex, w, 2); w.IsTravelBusy = false;
        Ticks(ex, w, 20);
        var teleportAt = w.Calls.FindIndex(c => c.StartsWith("Teleport"));
        var aethernetAt = w.Calls.FindIndex(c => c.StartsWith("Aethernet"));
        var moveAt = w.Calls.FindIndex(c => c.StartsWith("Move"));
        Assert.True(teleportAt < aethernetAt && aethernetAt < moveAt, $"order was {teleportAt},{aethernetAt},{moveAt}");
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void A_teleport_that_never_starts_times_out_with_a_reason()
    {
        var w = World();
        w.ArriveOnTeleport = false; // accepted, but nothing happens
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));
        Ticks(ex, w, 60, s: 1);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("never started", ex.FailReason);
    }

    [Fact]
    public void Walking_across_a_zone_line_arrives_by_zone_change()
    {
        var w = new FakeStepWorld { TerritoryId = 100 };
        var ex = new StepExecutor(w);
        var step = new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 100, TargetTerritoryId = 101, Position = new Vector3(500, 0, 0) };
        ex.Begin(step);
        Ticks(ex, w, 3);
        Assert.Contains(w.Calls, c => c.StartsWith("Move"));
        w.TerritoryId = 101; // zoned, never "reached" the point
        Ticks(ex, w, 5);
        Assert.Equal(StepStatus.Done, ex.Status);
    }
}
