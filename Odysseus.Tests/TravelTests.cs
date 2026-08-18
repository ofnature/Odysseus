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

    /// <summary>
    /// The path data spells a destination "[Ul'dah] Goldsmiths' Guild" — its own way of saying
    /// which city — but the Aetheryte sheet and Lifestream both call the place "Goldsmiths' Guild".
    /// Passing the bracketed form matched nothing, so every aethernet hop was refused.
    /// </summary>
    [Theory]
    [InlineData("[Ul'dah] Goldsmiths' Guild", "Goldsmiths' Guild")]
    [InlineData("[Ul'dah] Aetheryte Plaza", "Aetheryte Plaza")]
    [InlineData("[Limsa Lominsa] The Aftcastle", "The Aftcastle")]
    [InlineData("Goldsmiths' Guild", "Goldsmiths' Guild")]   // already bare, left alone
    public void The_city_prefix_is_stripped_before_Lifestream_sees_it(string data, string expected)
        => Assert.Equal(expected, Odysseus.Services.Run.GameStepWorld.StripCity(data));

    /// <summary>
    /// Half a city can hold no aetheryte at all — Ul'dah's Steps of Thal (131) has six aethernet
    /// shards and nothing to teleport to. Reaching it from outside is a teleport to Ul'dah proper
    /// (130) and then a hop.
    /// </summary>
    [Fact]
    public void A_zone_with_no_aetheryte_is_reached_by_teleport_then_aethernet()
    {
        var w = World();
        w.AethernetByTerritory[131] = (Aetheryte: 9, Hop: "Goldsmiths' Guild", Lands: 130);
        w.AetheryteTerritories[9] = 130;
        var ex = new StepExecutor(w);
        ex.Begin(Interact(131, new Vector3(50, 0, 0)));

        ex.Tick();                       // teleport issued
        Assert.Contains("Teleport 9", w.Calls);
        w.TerritoryId = 130;             // landed in Steps of Nald
        for (var i = 0; i < 40 && ex.Status == StepStatus.Running; i++)
        {
            ex.Tick();
            w.Advance(0.5);
            if (w.Calls.Any(c => c.StartsWith("Aethernet"))) w.TerritoryId = 131;
        }

        Assert.Contains("Aethernet Goldsmiths' Guild", w.Calls);
        Assert.NotEqual(StepStatus.Failed, ex.Status);
    }

    /// <summary>
    /// A hop that matched nothing stops being busy at once. Treating that as arrival reported the
    /// wrong failure two phases later — "no aetheryte you have attuned" — when what actually
    /// happened was that the hop never started.
    /// </summary>
    [Fact]
    public void A_hop_that_goes_nowhere_says_so_rather_than_blaming_the_destination()
    {
        var w = World();
        w.TerritoryId = 130;
        w.AethernetTerritories["Goldsmiths' Guild"] = 131;
        w.ArriveOnTeleport = false;              // Lifestream takes the call and does nothing
        var ex = new StepExecutor(w);
        var step = Interact(131, new Vector3(50, 0, 0));
        step.AethernetShortcut = ["[Ul'dah] Aetheryte Plaza", "Goldsmiths' Guild"];
        ex.Begin(step);

        for (var i = 0; i < 60 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }

        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("never started", ex.FailReason);
        Assert.DoesNotContain("attuned", ex.FailReason);
    }

    /// <summary>
    /// A step that names its own shard keeps it. The path author picked the one beside the NPC;
    /// the resolver picked the one nearest the step's coordinates and sent the run to the
    /// Gladiators' Guild for a quest in the Goldsmiths'.
    /// </summary>
    [Fact]
    public void A_named_hop_is_not_replaced_by_the_route_resolver()
    {
        var w = World();
        w.TerritoryId = 130;
        w.AethernetTerritories["Goldsmiths' Guild"] = 131;
        // The resolver would have chosen this one, and must not get the chance.
        w.AethernetByTerritory[131] = (Aetheryte: null, Hop: "Gladiators' Guild", Lands: 131);
        var ex = new StepExecutor(w);
        var step = Interact(131, new Vector3(50, 0, 0));
        step.AethernetShortcut = ["[Ul'dah] Aetheryte Plaza", "Goldsmiths' Guild"];
        ex.Begin(step);
        ex.Tick();

        Assert.Contains("Aethernet Goldsmiths' Guild", w.Calls);
        Assert.DoesNotContain("Aethernet Gladiators' Guild", w.Calls);
    }

    /// <summary>
    /// The aethernet is only reachable from a shard or the city aetheryte. Asking for a hop from
    /// the middle of Ul'dah left the run standing still — the data names the shard to travel
    /// <i>from</i> for exactly this reason, and we had been using only the destination.
    /// </summary>
    [Fact]
    public void It_walks_to_an_aethernet_access_point_before_hopping()
    {
        var w = World();
        w.TerritoryId = 130;
        w.PlayerPosition = new Vector3(0, 0, 0);
        w.AethernetAccess[130] = new Vector3(40, 0, 0);          // the plaza, across the square
        w.AethernetTerritories["Goldsmiths' Guild"] = 131;
        w.ArriveOnMove = true;
        var ex = new StepExecutor(w);
        var step = Interact(131, new Vector3(50, 0, 0));
        step.AethernetShortcut = ["[Ul'dah] Aetheryte Plaza", "Goldsmiths' Guild"];
        ex.Begin(step);

        for (var i = 0; i < 20 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }

        var walked = w.Calls.FindIndex(c => c.StartsWith("Move 40,0,0"));
        var hopped = w.Calls.IndexOf("Aethernet Goldsmiths' Guild");
        Assert.True(walked >= 0, "never walked to the aethernet access point");
        Assert.True(hopped > walked, "hopped before reaching the access point");
    }

    /// <summary>Standing at the shard already, there is nothing to walk.</summary>
    [Fact]
    public void It_hops_straight_away_when_already_at_an_access_point()
    {
        var w = World();
        w.TerritoryId = 130;
        w.PlayerPosition = new Vector3(40, 0, 0);
        w.AethernetAccess[130] = new Vector3(40, 0, 0);
        w.AethernetTerritories["Goldsmiths' Guild"] = 131;
        var ex = new StepExecutor(w);
        var step = Interact(131, new Vector3(50, 0, 0));
        step.AethernetShortcut = ["[Ul'dah] Aetheryte Plaza", "Goldsmiths' Guild"];
        ex.Begin(step);
        ex.Tick();

        Assert.Contains("Aethernet Goldsmiths' Guild", w.Calls);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Move "));
    }

    /// <summary>
    /// The bracketed city is noise for a shard and part of the name for a plaza. Measured across
    /// the bundle's 139 aethernet names: without the second attempt all sixteen city plazas resolve
    /// to nothing, which is one name in eight.
    /// </summary>
    [Theory]
    [InlineData("[Ul'dah] Goldsmiths' Guild", "Ul'dah", "Goldsmiths' Guild")]
    [InlineData("[Ul'dah] Aetheryte Plaza", "Ul'dah", "Aetheryte Plaza")]
    [InlineData("[Limsa Lominsa] The Aftcastle", "Limsa Lominsa", "The Aftcastle")]
    [InlineData("[Crystarium] Aetheryte Plaza", "Crystarium", "Aetheryte Plaza")]
    [InlineData("Goldsmiths' Guild", "", "Goldsmiths' Guild")]
    public void The_bracketed_city_is_separated_from_the_stop_name(string data, string city, string name)
    {
        var (gotCity, gotName) = Odysseus.Services.Travel.AetheryteCatalog.SplitCity(data);
        Assert.Equal(city, gotCity);
        Assert.Equal(name, gotName);
    }

    /// <summary>Already in the other half of the city: the hop is the whole journey.</summary>
    [Fact]
    public void Standing_in_the_same_city_needs_only_the_hop()
    {
        var w = World();
        w.TerritoryId = 130;
        w.AethernetByTerritory[131] = (Aetheryte: null, Hop: "Goldsmiths' Guild", Lands: 131);
        var ex = new StepExecutor(w);
        ex.Begin(Interact(131, new Vector3(50, 0, 0)));
        ex.Tick();

        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport"));
        Assert.Contains("Aethernet Goldsmiths' Guild", w.Calls);
    }

    /// <summary>
    /// An unresolvable name is bad data, not a dead end. It used to stop the run; now it is logged
    /// — so the data problem stays visible — and the route is worked out instead.
    /// </summary>
    [Fact]
    public void Unknown_aetheryte_name_is_logged_and_routed_around()
    {
        var w = World();
        w.AttunedByTerritory[621] = 99;
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Made Up - Place"));
        ex.Tick();   // the teleport is issued by the phase, not by Begin

        Assert.NotEqual(StepStatus.Failed, ex.Status);
        Assert.Contains(w.Calls, c => c.Contains("Made Up - Place"));
        Assert.Contains("Teleport 99", w.Calls);
    }

    [Fact]
    public void Unknown_aetheryte_name_with_no_way_into_the_zone_still_stops()
    {
        var w = World();
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Made Up - Place"));

        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("territory 621", ex.FailReason);
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
