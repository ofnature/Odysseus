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
        w.Positions[7] = new Vector3(50, 0, 0); // where the steps put it — reach is measured for real
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

    /// <summary>
    /// The shape that cost a whole Return to Ivalice run: the path's author reached the NPC by
    /// teleporting to an aetheryte in the <i>neighbouring</i> zone and walking in, so the step
    /// names one whose territory is not the step's own. Standing in front of that NPC and pressing
    /// Start, the old rule saw "aetheryte's zone is not my zone" and teleported the run away — from
    /// where it had no recorded way back.
    /// </summary>
    [Fact]
    public void Standing_on_the_mark_does_not_teleport_to_a_neighbouring_zones_aetheryte()
    {
        var w = World();
        w.Aetherytes["Rhalgr's Reach"] = 104;
        w.AetheryteTerritories[104] = 635;       // the shortcut lands in the next zone over
        w.TerritoryId = 621;                     // we are already where the step is
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Rhalgr's Reach"));

        Ticks(ex, w, 20);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport"));
        Assert.Equal(621u, w.TerritoryId);
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    /// <summary>Same reasoning for the hop: on the mark already, the recorded hop is not the step.</summary>
    [Fact]
    public void Standing_on_the_mark_does_not_take_a_recorded_hop_out_of_the_zone()
    {
        var w = World();
        w.TerritoryId = 131;
        w.AethernetTerritories["Aetheryte Plaza"] = 130;   // the hop would leave the zone
        var ex = new StepExecutor(w);
        var step = Interact(131, new Vector3(50, 0, 0));
        step.AethernetShortcut = ["[Ul'dah] Goldsmiths' Guild", "[Ul'dah] Aetheryte Plaza"];
        ex.Begin(step);

        Ticks(ex, w, 20);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Aethernet"));
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    /// <summary>The rule is "on the mark", not "in the zone" — a far mark still travels as recorded.</summary>
    [Fact]
    public void A_far_mark_in_the_same_zone_still_uses_the_recorded_teleport()
    {
        var w = World();
        w.Aetherytes["Rhalgr's Reach"] = 104;
        w.AetheryteTerritories[104] = 635;
        w.TerritoryId = 621;
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(1000, 0, 0), aetheryte: "Rhalgr's Reach"));

        Ticks(ex, w, 2);
        Assert.Contains("Teleport 104", w.Calls);
    }

    /// <summary>
    /// In the Dark of Night (3159): the NPC after the fight ends in a cutscene that carries the
    /// character to Old Gridania. The next step began while it played, decided "teleport to New
    /// Gridania, then aethernet" from East Shroud, and — once the cutscene set the character down
    /// beside the very NPC it wanted — cast that teleport anyway and ran back. Where the character
    /// is, is only worth reading once the game has stopped moving it.
    /// </summary>
    [Fact]
    public void A_route_is_decided_after_the_cutscene_that_moves_you_not_during_it()
    {
        const uint EastShroud = 152, OldGridania = 133, NewGridania = 132;
        var w = new FakeStepWorld { ArriveOnMove = true, TerritoryId = EastShroud, InCutscene = true, IsOccupied = true };
        w.AethernetByTerritory[OldGridania] = (Aetheryte: 2, Hop: "Leatherworkers' Guild & Shaded Bower", Lands: OldGridania);
        w.AetheryteTerritories[2] = NewGridania;
        w.Spawned.Add(1026867);
        w.Positions[1026867] = new Vector3(-36, 7, -121);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.Interact, KindName = "Interact", DataId = 1026867,
            TerritoryId = OldGridania, Position = new Vector3(-36, 7, -121),
        });

        Ticks(ex, w, 20);                                   // the cutscene plays; nothing is decided
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport") || c.StartsWith("Aethernet"));

        // The cutscene sets the character down beside the NPC, in Old Gridania.
        w.TerritoryId = OldGridania;
        w.PlayerPosition = new Vector3(-34, 7, -119);
        w.InCutscene = false;
        w.IsOccupied = false;
        Ticks(ex, w, 20);

        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport") || c.StartsWith("Aethernet"));
        Assert.Contains("Interact 1026867", w.Calls);
    }

    /// <summary>When nothing moves the character, the route is decided as before — the wait costs one second.</summary>
    [Fact]
    public void Once_settled_a_step_in_another_zone_still_travels()
    {
        var w = World();
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));
        Ticks(ex, w, 6);
        Assert.Contains("Teleport 98", w.Calls);
    }

    /// <summary>A conversation left hanging must not hold travel forever: after the wait it decides anyway.</summary>
    [Fact]
    public void A_conversation_that_never_closes_does_not_hold_travel_for_good()
    {
        var w = World();
        w.IsOccupied = true;                                // stuck, and not this step's own chain
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));

        Ticks(ex, w, 40);                                   // 20s: still waiting
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport"));
        Ticks(ex, w, 30);                                   // past 30s: decides
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("deciding the route anyway"));
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

        Ticks(ex, w, 2);                 // the list is warmed, then the teleport issued
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

    /// <summary>
    /// Blood Ties (2617): the step before ended at the far end of Limsa's Upper Decks, with no shard
    /// in view, and the hop was asked for from there — Lifestream never went anywhere and the step
    /// faulted after 90s. The map places every shard: walk toward the nearest, look again once it is
    /// in view, walk the rest, then hop.
    /// </summary>
    [Fact]
    public void With_no_shard_in_view_it_walks_toward_the_one_the_map_shows()
    {
        var w = World();
        w.TerritoryId = 128;
        w.PlayerPosition = new Vector3(-185, 41, 185);
        w.MappedAccess[128] = ("The Aftcastle", new Vector3(16, 41, 72));
        w.AethernetTerritories["[Limsa Lominsa] Fishermen's Guild"] = 129;
        w.ArriveOnMove = true;
        var ex = new StepExecutor(w);
        var step = Interact(129, new Vector3(-189, 4, 178));
        step.AethernetShortcut = ["[Limsa Lominsa] The Aftcastle", "[Limsa Lominsa] Fishermen's Guild"];
        ex.Begin(step);

        for (var i = 0; i < 6 && !w.Calls.Contains("Move 16,41,72 fly=False"); i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("No aethernet shard in view"));
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Aethernet"));   // not from out here

        w.AethernetAccess[128] = new Vector3(30, 40, 90);                 // now loaded: the real one, off the marker
        for (var i = 0; i < 20 && !w.Calls.Any(c => c.StartsWith("Aethernet")); i++) { ex.Tick(); w.Advance(0.5); }

        var exact = w.Calls.FindIndex(c => c.StartsWith("Move 30,40,90"));
        var hopped = w.Calls.FindIndex(c => c.StartsWith("Aethernet [Limsa Lominsa] Fishermen's Guild"));
        Assert.True(hopped >= 0, "never hopped");
        Assert.True(exact >= 0 && exact < hopped, "hopped before reaching the shard itself");
    }

    /// <summary>
    /// The navmesh does not extend under a solid object, so a path to a shard ends a few yalms
    /// short and stays there — which is why jumping made a stalled approach complete. The last
    /// stretch is walked straight instead, because Lifestream has to interact with the shard and
    /// stopping fifteen yalms out had the hop refused every time.
    /// </summary>
    [Fact]
    public void A_mesh_path_that_ends_short_of_a_shard_is_finished_directly()
    {
        var w = World();
        w.TerritoryId = 418;
        w.PlayerPosition = new Vector3(0, 0, 0);
        w.AethernetAccess[418] = new Vector3(8, 0, 0);    // past interact range, inside the nudge
        w.AethernetTerritories["The Last Vigil"] = 419;
        w.MoveAccepted = true;
        var ex = new StepExecutor(w);
        var step = Interact(419, new Vector3(50, 0, 0));
        step.AethernetShortcut = ["[Ishgard] Aetheryte Plaza", "The Last Vigil"];
        ex.Begin(step);

        // The mesh move is issued and leaves us where we are; the straight finish then closes it.
        for (var i = 0; i < 12 && !w.Calls.Any(c => c.StartsWith("MoveDirect")); i++) { ex.Tick(); w.Advance(0.5); }

        Assert.Contains(w.Calls, c => c.StartsWith("MoveDirect 8,0,0"));
    }

    /// <summary>
    /// The Foundation case: the aethernet menu was open — the game plainly considered the player at
    /// the shard — while a distance measured from the object's origin still read as too far, and the
    /// approach stalled until the player jumped. Lifestream's own answer ends it instead.
    /// </summary>
    [Fact]
    public void The_games_own_answer_ends_the_approach_whatever_the_distance_says()
    {
        var w = World();
        w.TerritoryId = 418;
        w.PlayerPosition = new Vector3(0, 0, 0);
        w.AethernetAccess[418] = new Vector3(40, 0, 0);   // far enough that the walk starts
        w.AethernetTerritories["The Last Vigil"] = 419;
        var ex = new StepExecutor(w);
        var step = Interact(419, new Vector3(50, 0, 0));
        step.AethernetShortcut = ["[Ishgard] Aetheryte Plaza", "The Last Vigil"];
        ex.Begin(step);

        ex.Tick();                       // walking
        Assert.DoesNotContain("Aethernet The Last Vigil", w.Calls);

        w.AtAethernetShard = true;       // the menu opens; we never got within the distance
        ex.Tick();
        w.Advance(0.5);
        ex.Tick();

        Assert.Contains("Aethernet The Last Vigil", w.Calls);
    }

    /// <summary>At one before the step even begins, there is nothing to walk.</summary>
    [Fact]
    public void Standing_at_a_shard_already_skips_the_walk()
    {
        var w = World();
        w.TerritoryId = 418;
        w.AtAethernetShard = true;
        w.AethernetAccess[418] = new Vector3(40, 0, 0);
        w.AethernetTerritories["The Last Vigil"] = 419;
        var ex = new StepExecutor(w);
        var step = Interact(419, new Vector3(50, 0, 0));
        step.AethernetShortcut = ["[Ishgard] Aetheryte Plaza", "The Last Vigil"];
        ex.Begin(step);
        ex.Tick();

        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Move 40,0,0"));
        Assert.Contains("Aethernet The Last Vigil", w.Calls);
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

    /// <summary>
    /// Every Goldsmith quest walked out of the guild and back in. Its NPC sits a few paces from the
    /// Goldsmiths' Guild shard the step names, so taking the hop meant walking out to that shard,
    /// teleporting to it, and walking back. The aethernet is for crossing a city, not for standing
    /// still in one.
    /// </summary>
    [Fact]
    public void A_hop_that_lands_where_you_already_are_is_not_taken()
    {
        var w = World();
        w.TerritoryId = 131;
        w.PlayerPosition = new Vector3(0, 0, 0);
        w.AethernetTerritories["Goldsmiths' Guild"] = 131;
        w.AethernetAccess[131] = new Vector3(40, 0, 0);
        var ex = new StepExecutor(w);
        var step = Interact(131, new Vector3(20, 0, 0));           // the NPC, a short walk away
        step.AethernetShortcut = ["[Ul'dah] Aetheryte Plaza", "Goldsmiths' Guild"];
        ex.Begin(step);
        Ticks(ex, w, 20);

        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Aethernet"));
        Assert.Contains(w.Calls, c => c.StartsWith("Move 20,0,0"));
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    /// <summary>Far enough across the same zone and the hop still earns its detour.</summary>
    [Fact]
    public void A_long_walk_in_the_same_zone_still_takes_the_hop()
    {
        var w = World();
        w.TerritoryId = 131;
        w.PlayerPosition = new Vector3(0, 0, 0);
        w.AethernetTerritories["Goldsmiths' Guild"] = 131;
        var ex = new StepExecutor(w);
        var step = Interact(131, new Vector3(900, 0, 0));
        step.AethernetShortcut = ["[Ul'dah] Aetheryte Plaza", "Goldsmiths' Guild"];
        ex.Begin(step);
        ex.Tick();

        Assert.Contains("Aethernet Goldsmiths' Guild", w.Calls);
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
        Ticks(ex, w, 2);   // the teleport is issued by the phase, not by Begin — after the list is warmed

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
    public void A_duty_queued_by_the_duty_finder_does_not_travel_to_where_it_was_recorded()
    {
        // A City Fallen: the alliance raid was recorded on the Prima Vista's bridge (736), an
        // instanced area with no aetheryte. From Kugane, travel faulted "no aetheryte there"
        // before the duty rule ever said the raid is not something Odysseus runs.
        var w = World();
        w.TerritoryId = 628;
        w.Duties[281] = new Odysseus.Services.Quest.DutyDescription(281, "The Royal City of Rabanastre", false, 24);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.Duty, KindName = "Duty", TerritoryId = 736,
            ContentFinderConditionId = 281, DutyEnabled = false,
        });
        Ticks(ex, w, 6);

        Assert.Equal(StepStatus.Running, ex.Status);           // waiting on the player, not faulted on travel
        Assert.Contains("Rabanastre", ex.PhaseName);
        Assert.DoesNotContain("aetheryte", ex.PhaseName);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport"));
    }

    [Fact]
    public void A_far_mark_with_an_attuned_aetheryte_beside_it_teleports_in_zone()
    {
        // Thavnair: the accept NPC stood 340y off with a crystal right beside them — the
        // crystal beats even the flight. The rung wants a clear win: mark far, aetheryte near.
        var w = World();
        w.TerritoryId = 957;
        w.PlayerPosition = Vector3.Zero;
        var mark = new Vector3(800, 0, 0);
        w.AttunedAetherytesAt[130] = (957u, new Vector3(780, 0, 10));
        w.AetheryteTerritories[130] = 957;
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = 99, TerritoryId = 957, Position = mark });

        for (var i = 0; i < 6 && !w.Calls.Any(c => c.StartsWith("Teleport")); i++) Ticks(ex, w, 1);
        Assert.Contains("Teleport 130", w.Calls);

        // Near the mark already: no teleport, however close the crystal.
        var near = World();
        near.TerritoryId = 957;
        near.PlayerPosition = new Vector3(700, 0, 0);
        near.AttunedAetherytesAt[130] = (957u, new Vector3(780, 0, 10));
        var ex2 = new StepExecutor(near);
        ex2.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = 99, TerritoryId = 957, Position = mark });
        for (var i = 0; i < 4 && !near.Calls.Any(c => c.StartsWith("Move")); i++) Ticks(ex2, near, 1);
        Assert.DoesNotContain(near.Calls, c => c.StartsWith("Teleport"));
    }

    [Fact]
    public void A_teleport_that_never_starts_is_asked_again_before_it_is_believed()
    {
        // The gather runner switches job and teleports on the next beat: Lifestream says yes,
        // the cast falls into the gearset action lock, and nothing ever goes travel-busy.
        var w = World();
        w.ArriveOnTeleport = false;   // accepted, but the world never moves
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));
        Ticks(ex, w, 2);
        Assert.Equal(1, w.Calls.Count(c => c.StartsWith("Teleport")));

        Ticks(ex, w, 12);   // six seconds: past the four-second start grace, one re-ask in
        Assert.Equal(StepStatus.Running, ex.Status);
        Assert.True(w.Calls.Count(c => c.StartsWith("Teleport")) >= 2, "never asked again");
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("never started — asking again"));

        // The next ask takes: the world moves, the step goes on.
        w.ArriveOnTeleport = true;
        Ticks(ex, w, 30);
        Assert.NotEqual(StepStatus.Failed, ex.Status);

        // Nothing ever happening is still the honest fault, after the asks are spent.
        var stuck = World();
        stuck.ArriveOnTeleport = false;
        var ex2 = new StepExecutor(stuck);
        ex2.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));
        Ticks(ex2, stuck, 120);
        Assert.Equal(StepStatus.Failed, ex2.Status);
        Assert.Contains("never started", ex2.FailReason);
        Assert.Equal(3, stuck.Calls.Count(c => c.StartsWith("Teleport")));
    }

    [Fact]
    public void Refused_teleport_asks_again_before_faulting_with_the_lifestream_hint()
    {
        // The Qitari opener's gearset change refused the very next cast: the action lock was
        // still settling. A refusal is retried for a few seconds before it is believed.
        var w = World();
        w.TeleportAccepted = false;
        var ex = new StepExecutor(w);
        ex.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));
        Ticks(ex, w, 8);   // 4 seconds: refused twice, still trying
        Assert.Equal(StepStatus.Running, ex.Status);
        Assert.True(w.Calls.Count(c => c.StartsWith("Teleport")) >= 2);

        // The lock lifting mid-retry lets the travel proceed.
        w.TeleportAccepted = true;
        Ticks(ex, w, 6);
        Assert.NotEqual(StepStatus.Failed, ex.Status);

        // A refusal that never lifts is the honest fault it always was.
        var stubborn = World();
        stubborn.TeleportAccepted = false;
        var ex2 = new StepExecutor(stubborn);
        ex2.Begin(Interact(621, new Vector3(50, 0, 0), aetheryte: "Lochs - Ala Mhigan Quarter"));
        Ticks(ex2, stubborn, 40);
        Assert.Equal(StepStatus.Failed, ex2.Status);
        Assert.Contains("Lifestream", ex2.FailReason);
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
public class ZoneCrossingTests
{
    [Fact]
    public void Landing_in_the_zone_the_step_crosses_into_is_arrival_not_the_wrong_zone()
    {
        // Highway Robbery's first step: starts in Limsa's Lower Decks (129), ends in the Upper
        // Decks (128), and gets there by hopping the aethernet to The Aftcastle. Arriving is the
        // whole step; reading 128 as "you are in the wrong zone" faulted the quest on arrival.
        var step = new QuestStep
        {
            Kind = StepKind.None, KindName = "None",
            TerritoryId = 129, TargetTerritoryId = 128,
        };

        var world = new FakeStepWorld { TerritoryId = 128 };
        var ex = new StepExecutor(world);
        ex.Begin(step);
        for (var i = 0; i < 12 && ex.Status == StepStatus.Running; i++) { ex.Tick(); world.Advance(0.5); }

        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.Equal(string.Empty, ex.FailReason);
    }

    /// <summary>
    /// Gosetsu and Tsuyu (3070): Kugane's guard into the Ruby Bazaar offices, with the character
    /// already in the offices from the quest before. Nothing to hop to, nothing to talk to — done.
    /// </summary>
    [Fact]
    public void A_door_step_with_the_character_already_through_it_is_done()
    {
        var step = new QuestStep
        {
            Kind = StepKind.Interact, KindName = "Interact", DataId = 1019070, Position = new Vector3(151, 15, 96),
            TerritoryId = 628, TargetTerritoryId = 639,
            AethernetShortcut = ["[Kugane] Aetheryte Plaza", "[Kugane] The Ruby Bazaar"],
        };
        var world = new FakeStepWorld { TerritoryId = 639 };
        world.AethernetTerritories["[Kugane] The Ruby Bazaar"] = 628;
        var ex = new StepExecutor(world);
        ex.Begin(step);
        for (var i = 0; i < 6 && ex.Status == StepStatus.Running; i++) { ex.Tick(); world.Advance(0.5); }

        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Aethernet") || c.StartsWith("Teleport"));
    }

    [Fact]
    public void Somewhere_else_entirely_still_says_so()
    {
        var step = new QuestStep
        {
            Kind = StepKind.None, KindName = "None",
            TerritoryId = 129, TargetTerritoryId = 128,
        };

        var world = new FakeStepWorld { TerritoryId = 156 };
        var ex = new StepExecutor(world);
        ex.Begin(step);
        for (var i = 0; i < 12 && ex.Status == StepStatus.Running; i++) { ex.Tick(); world.Advance(0.5); }

        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("territory 129", ex.FailReason);
    }
}
public class StallJumpTests
{
    private static QuestStep WalkTo(Vector3 where) => new()
    {
        Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 137, Position = where, Mount = false,
    };

    [Fact]
    public void A_walk_that_stops_getting_closer_jumps_once()
    {
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = false, IsMoving = true };
        w.PlayerPosition = new Vector3(0, 0, 0);
        var ex = new StepExecutor(w);
        ex.Begin(WalkTo(new Vector3(0, 0, 30)));

        // Three seconds of running on the spot is not yet a stall.
        for (var i = 0; i < 6; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.DoesNotContain(w.Calls, c => c.Contains("Jump"));

        for (var i = 0; i < 6; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Single(w.Calls, c => c.Contains("Jump"));

        // And it does not turn into a pogo stick while it stays stuck.
        for (var i = 0; i < 8; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Single(w.Calls, c => c.Contains("Jump"));
    }

    [Fact]
    public void A_walk_that_is_making_progress_is_left_alone()
    {
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = false, IsMoving = true };
        w.PlayerPosition = new Vector3(0, 0, 0);
        var ex = new StepExecutor(w);
        ex.Begin(WalkTo(new Vector3(0, 0, 60)));

        for (var i = 0; i < 20; i++)
        {
            ex.Tick();
            w.Advance(0.5);
            w.PlayerPosition = w.PlayerPosition with { Z = w.PlayerPosition.Z + 1f }; // a yalm a tick
        }
        Assert.DoesNotContain(w.Calls, c => c.Contains("Jump"));
    }

    /// <summary>
    /// A route round a building: walking steadily, the straight-line distance does not shrink for
    /// seconds on end. That is walking, not a snag — it used to be a hop at every such bend.
    /// </summary>
    [Fact]
    public void Walking_round_a_bend_is_not_a_stall()
    {
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = false, IsMoving = true };
        w.PlayerPosition = new Vector3(0, 0, 0);
        var ex = new StepExecutor(w);
        ex.Begin(WalkTo(new Vector3(0, 0, 60)));

        for (var i = 0; i < 20; i++)
        {
            ex.Tick();
            w.Advance(0.5);
            w.PlayerPosition = w.PlayerPosition with { X = w.PlayerPosition.X + 1f };   // sideways: no closer
        }
        Assert.DoesNotContain(w.Calls, c => c.Contains("Jump"));
    }

    /// <summary>No mount allowed: Sprint is the fastest thing going on a long leg.</summary>
    [Fact]
    public void On_foot_where_no_mount_is_allowed_a_long_leg_sprints()
    {
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = false, IsMoving = true, CanMountHere = false };
        w.PlayerPosition = new Vector3(0, 0, 0);
        var ex = new StepExecutor(w);
        ex.Begin(WalkTo(new Vector3(0, 0, 80)));
        for (var i = 0; i < 6; i++) { ex.Tick(); w.Advance(0.5); w.PlayerPosition += new Vector3(0, 0, 3); }
        Assert.True(w.Sprints >= 1);
    }

    /// <summary>
    /// Ul'dah end to end with no shard named: the aethernet, when the hop wins by a clear margin.
    /// The Disciple of the Hand quests walked it one way and hopped back.
    /// </summary>
    [Fact]
    public void Across_a_city_with_no_shortcut_named_it_hops_the_aethernet()
    {
        var w = new FakeStepWorld { TerritoryId = 131, ArriveOnMove = true, CanMountHere = false };
        w.PlayerPosition = new Vector3(-120, 40, 120);
        w.AethernetAccess[131] = new Vector3(-118, 40, 118);
        w.CityHopTo = "Sapphire Avenue Exchange";
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 131, Position = new Vector3(130, 5, -30) });
        for (var i = 0; i < 6; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains(w.Calls, c => c.StartsWith("Aethernet Sapphire Avenue Exchange"));
    }

    /// <summary>
    /// Ul'dah's top level: the NPC 80 yalms up, 120 across, the Airship Landing shard named in the
    /// path. Close as the crow flies, but the hop is the way up — not the lift.
    /// </summary>
    [Fact]
    public void A_mark_on_another_level_takes_the_recorded_hop()
    {
        var w = new FakeStepWorld { TerritoryId = 130, ArriveOnMove = true, CanMountHere = false };
        w.PlayerPosition = new Vector3(-100, 4, -100);
        w.AethernetAccess[130] = new Vector3(-98, 4, -98);
        w.AethernetTerritories["[Ul'dah] Airship Landing"] = 130;
        var ex = new StepExecutor(w);
        var step = new QuestStep
        {
            Kind = StepKind.Interact, KindName = "Interact", DataId = 1004433, TerritoryId = 130, Position = new Vector3(-24, 83, -2),
            AethernetShortcut = ["[Ul'dah] Aetheryte Plaza", "[Ul'dah] Airship Landing"],
        };
        ex.Begin(step);
        for (var i = 0; i < 6; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains(w.Calls, c => c.StartsWith("Aethernet [Ul'dah] Airship Landing"));
    }

    /// <summary>Mounted where flying is unlocked, a leg the path does not mark flies anyway.</summary>
    [Fact]
    public void Mounted_where_flying_is_unlocked_every_leg_flies()
    {
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = false, IsMounted = true, CanFlyHere = true };
        w.PlayerPosition = new Vector3(0, 0, 0);
        var ex = new StepExecutor(w);
        ex.Begin(WalkTo(new Vector3(0, 0, 60)));
        for (var i = 0; i < 4; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains(w.Calls, c => c.StartsWith("Move 0,0,60 fly=True"));
    }

    /// <summary>The cities allow no mount, and nothing in them wants a hop.</summary>
    [Fact]
    public void Nothing_jumps_in_a_zone_that_allows_no_mount()
    {
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = false, IsMoving = true, CanMountHere = false };
        w.PlayerPosition = new Vector3(0, 0, 0);
        var ex = new StepExecutor(w);
        ex.Begin(WalkTo(new Vector3(0, 0, 30)));

        for (var i = 0; i < 20; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.DoesNotContain(w.Calls, c => c.Contains("Jump"));
    }
}
public class StepDismountTests
{
    [Fact]
    public void A_step_marked_dismount_gets_off_before_it_walks_and_stays_off()
    {
        var step = new QuestStep
        {
            Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 137,
            Position = new Vector3(0, 0, 80), Dismount = true, // far enough that it would normally mount
        };
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = false, IsMounted = true };
        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 10; i++) { ex.Tick(); w.Advance(0.5); }

        var off = w.Calls.FindIndex(c => c == "Dismount");
        var moved = w.Calls.FindIndex(c => c.StartsWith("Move"));
        Assert.True(off >= 0 && moved > off, string.Join(" | ", w.Calls));
        Assert.DoesNotContain("Mount", w.Calls);
    }

    /// <summary>A stop mid-trip stops the trip: the path, and an aethernet hop Lifestream has under way.</summary>
    [Fact]
    public void Cancelling_a_step_aborts_the_trip_under_way()
    {
        var w = new FakeStepWorld { TerritoryId = 132, ArriveOnMove = false };
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 132, Position = new Vector3(0, 0, 200) });
        for (var i = 0; i < 4; i++) { ex.Tick(); w.Advance(0.5); }
        ex.Cancel();
        Assert.Contains("AbortTravel", w.Calls);
    }

    /// <summary>
    /// Logistics of War (3304) puts you on an amaro that flies, in a zone you cannot fly in yet. The
    /// path's legs say fly; on the quest's mount they fly, and a mob below neither holds the trip
    /// nor takes you off it — off the amaro was off for good, and the sequence never advanced.
    /// </summary>
    [Fact]
    public void A_quest_mount_flies_and_is_never_dismounted_for_a_fight()
    {
        var w = new FakeStepWorld { TerritoryId = 813, ArriveOnMove = false, IsMounted = true, OnQuestMount = true, CanFlyHere = true };
        w.PlayerPosition = new Vector3(588, 9, 342);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 813, Position = new Vector3(-163, 6, -84), Fly = true });
        for (var i = 0; i < 6; i++) { ex.Tick(); w.Advance(0.5); }
        w.InCombat = true;
        for (var i = 0; i < 12; i++) { ex.Tick(); w.Advance(0.5); }

        Assert.Contains(w.Calls, c => c.StartsWith("Move") && c.EndsWith("fly=True"));
        Assert.DoesNotContain("Dismount", w.Calls);
        Assert.DoesNotContain("Mount", w.Calls);
    }

    /// <summary>
    /// The Rising Stones (351) has no aetheryte: it is entered by a door in Mor Dhona. Prelude in
    /// Violet (3149) faulted "no aetheryte there" with Saar standing in Mor Dhona.
    /// </summary>
    [Fact]
    public void A_zone_with_no_aetheryte_is_entered_by_its_door()
    {
        var w = new FakeStepWorld { TerritoryId = 156, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(15, 22, -600);
        w.Spawned.Add(2002881);
        w.Spawned.Add(1025549);
        w.Doors[351] = [new Odysseus.Services.Travel.Doorways.Door(156, 351, 2002881, new Vector3(21.1f, 22.3f, -631.3f))];
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = 1025549, TerritoryId = 351, Position = new Vector3(1, 0, -12) });

        for (var i = 0; i < 120 && ex.Status == StepStatus.Running; i++)
        {
            ex.Tick(); w.Advance(0.5);
            if (w.TerritoryId == 156 && w.Calls.Contains("Interact 2002881"))
            {
                w.TerritoryId = 351;
                w.PlayerPosition = new Vector3(0, 0, 20);
            }
        }
        Assert.Contains("Interact 2002881", w.Calls);
        Assert.Contains("Interact 1025549", w.Calls);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport"));
    }

    /// <summary>
    /// Eulmore early in Shadowbringers: its aetheryte will not attune, and every path in teleports
    /// there. The way back from Kholusia is its zone line into the Gatetown — walked into, nothing
    /// to press.
    /// </summary>
    [Fact]
    public void A_zone_line_door_is_walked_into()
    {
        var w = new FakeStepWorld { TerritoryId = 814, ArriveOnMove = true, CanMountHere = false };
        w.PlayerPosition = new Vector3(170, 356, 870);
        w.Spawned.Add(1027000);
        w.Doors[820] = [new Odysseus.Services.Travel.Doorways.Door(814, 820, 0, new Vector3(174f, 356.2f, 891.4f))];
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = 1027000, TerritoryId = 820, Position = new Vector3(1, -12, -150) });

        for (var i = 0; i < 120 && ex.Status == StepStatus.Running; i++)
        {
            ex.Tick(); w.Advance(0.5);
            if (w.TerritoryId == 814 && Vector3.Distance(w.PlayerPosition, new Vector3(174f, 356.2f, 891.4f)) < 1f)
            {
                w.TerritoryId = 820;
                w.PlayerPosition = new Vector3(0, -12, -160);
            }
        }
        Assert.Equal(820u, w.TerritoryId);
        Assert.Contains("Interact 1027000", w.Calls);
        Assert.DoesNotContain("Interact 0", w.Calls);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Teleport"));
    }

    [Fact]
    public void Starting_elsewhere_teleports_to_the_doors_zone_first()
    {
        var w = new FakeStepWorld { TerritoryId = 130 };
        w.AttunedByTerritory[156] = 24;
        w.Doors[351] = [new Odysseus.Services.Travel.Doorways.Door(156, 351, 2002881, new Vector3(21.1f, 22.3f, -631.3f))];
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = 1025549, TerritoryId = 351, Position = new Vector3(1, 0, -12) });
        for (var i = 0; i < 10 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains("Teleport 24", w.Calls);
    }

    /// <summary>
    /// A Still Tide (3283) starts in Kholusia, which a new character reaches only on the
    /// Crystarium's aspiring amaro tamer ("Travel to Kholusia?") — a ride no path step marks.
    /// </summary>
    [Fact]
    public void Kholusia_is_reached_from_the_crystarium_on_the_amaro()
    {
        var door = new Odysseus.Services.Travel.Doorways(() => []).Into(814).Single(d => d.From == 819);
        Assert.Equal(1029806u, door.DataId);
    }

    /// <summary>
    /// newtoon1 in the Ocular (844): the most used door into Kholusia stands in a zone it cannot
    /// reach, and taking it blindly faulted "no aetheryte there". The amaro's zone, the Crystarium,
    /// has an attuned aetheryte — go there.
    /// </summary>
    [Fact]
    public void An_unreachable_door_is_passed_over_for_one_that_can_be_reached()
    {
        var w = new FakeStepWorld { TerritoryId = 844 };
        w.AttunedByTerritory[819] = 133;
        w.Doors[814] =
        [
            new Odysseus.Services.Travel.Doorways.Door(895, 814, 2010829, new Vector3(1.4f, 0.8f, 2.5f)),
            new Odysseus.Services.Travel.Doorways.Door(819, 814, 1029806, new Vector3(62.4f, 36.2f, -169.4f)),
        ];
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 814, Position = new Vector3(639, 1, 534) });
        for (var i = 0; i < 10 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains("Teleport 133", w.Calls);
    }

    /// <summary>
    /// A mob swings on the way (A Still Tide, Kholusia): Daedalus fights, Minerva dodges, and every
    /// walk issued meanwhile was cut off and counted — "no path" after three, with a good route.
    /// The walk waits the fight out, off the mount, and finishes after.
    /// </summary>
    [Fact]
    public void A_fight_on_the_way_is_waited_out_off_the_mount()
    {
        var w = new FakeStepWorld { TerritoryId = 814, IsMounted = true, InCombat = true };
        w.PlayerPosition = new Vector3(649, 0, 552);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 814, Position = new Vector3(626, 3, 472) });

        for (var i = 0; i < 120; i++) { ex.Tick(); w.Advance(0.5); }   // a minute of fighting
        Assert.Equal(StepStatus.Running, ex.Status);
        Assert.Contains("Dismount", w.Calls);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Move 626"));

        w.IsMounted = false;   // the dismount took
        var before = w.Calls.Count;
        w.InCombat = false;
        w.ArriveOnMove = true;
        for (var i = 0; i < 40 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Equal(StepStatus.Done, ex.Status);
        var after = w.Calls.Skip(before).ToList();
        var mount = after.IndexOf("Mount");
        Assert.True(mount >= 0 && mount < after.FindIndex(c => c.StartsWith("Move 626")), string.Join(" | ", after));   // back on, then ride
    }

    /// <summary>Daedalus switched off: nobody would fight, so the walk carries on and leaves the mob behind.</summary>
    [Fact]
    public void With_daedalus_off_a_fight_does_not_stop_the_walk()
    {
        var w = new FakeStepWorld { TerritoryId = 814, InCombat = true, DaedalusDisabledByUser = true, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(649, 0, 552);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 814, Position = new Vector3(626, 3, 472) });
        for (var i = 0; i < 40 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    /// <summary>
    /// Teleported to the Crystarium for the amaro, the run walked from the aetheryte plaza to the
    /// Amaro Launch — whose own shard stands beside the amaro. The aethernet first, then the door.
    /// </summary>
    [Fact]
    public void A_door_across_a_city_is_reached_by_the_aethernet()
    {
        var w = new FakeStepWorld { TerritoryId = 819, ArriveOnMove = true, CanMountHere = false };
        w.PlayerPosition = new Vector3(-64, 20, -2);
        w.AethernetAccess[819] = new Vector3(-62, 20, -2);
        w.CityHopTo = "[Crystarium] The Amaro Launch";
        w.Doors[814] = [new Odysseus.Services.Travel.Doorways.Door(819, 814, 1029806, new Vector3(62.4f, 36.2f, -169.4f))];
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 814, Position = new Vector3(639, 1, 534) });
        for (var i = 0; i < 10; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains(w.Calls, c => c.StartsWith("Aethernet [Crystarium] The Amaro Launch"));
    }

    /// <summary>
    /// Ariadne updated mid-run: nothing could move while it reloaded, and the door's minute ran out
    /// before the amaro was reached. Pathing unavailable is not the door's time.
    /// </summary>
    [Fact]
    public void A_door_waits_out_pathing_that_is_reloading()
    {
        var w = new FakeStepWorld { TerritoryId = 819 };
        w.PlayerPosition = new Vector3(60, 36, -160);
        w.Spawned.Add(1029806);
        w.Doors[814] = [new Odysseus.Services.Travel.Doorways.Door(819, 814, 1029806, new Vector3(62.4f, 36.2f, -169.4f))];
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 814, Position = new Vector3(639, 1, 534) });
        ex.Tick(); w.Advance(0.5);
        w.NavmeshReady = false;
        for (var i = 0; i < 240; i++) { ex.Tick(); w.Advance(0.5); }   // two minutes of reloading
        Assert.Equal(StepStatus.Running, ex.Status);
    }

    /// <summary>
    /// Ariadne chose to teleport first (A Still Tide's hand-in, 16s via Stilltide against 38s on
    /// foot); standing still for the cast read as a stall, and the rescue hop cancelled it.
    /// </summary>
    [Fact]
    public void A_cast_mid_walk_is_not_a_stall_to_jump_out_of()
    {
        var w = new FakeStepWorld { TerritoryId = 814, IsMoving = true, IsCasting = true };
        w.PlayerPosition = new Vector3(600, 30, 100);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 814, Position = new Vector3(691, 30, 280) });
        for (var i = 0; i < 24; i++) { ex.Tick(); w.Advance(0.5); }   // twelve seconds of casting
        Assert.DoesNotContain(w.Calls, c => c.Contains("Jump"));
    }

    /// <summary>
    /// Ariadne waited ~40 s for Kholusia's mesh, then chose to teleport; the stall hop had run its four
    /// seconds and fired 21 ms later, before any cast — "teleport never started". Busy is not stuck.
    /// </summary>
    [Fact]
    public void Pathing_still_at_work_is_not_a_stall_to_jump_out_of()
    {
        var w = new FakeStepWorld { TerritoryId = 814, IsMoving = true, IsPathfinding = true };
        w.PlayerPosition = new Vector3(172, 39, 618);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 814, Position = new Vector3(742, 29, 266) });
        for (var i = 0; i < 80; i++) { ex.Tick(); w.Advance(0.5); }   // forty seconds of planning
        Assert.DoesNotContain(w.Calls, c => c.Contains("Jump"));
    }

    /// <summary>
    /// Closing Up Shop: its NPCs stand behind a wall in the Peaks that the mesh cannot cross; the
    /// Ala Mhigan Resistance gate guard passes you through. No path, so the gate, then the walk.
    /// </summary>
    [Fact]
    public void A_wall_with_a_gate_is_crossed_by_its_guard()
    {
        var w = new FakeStepWorld { TerritoryId = 620, CanMountHere = false, PathWaypointCount = 0 };
        w.PlayerPosition = new Vector3(-128, 305, 189);   // beside the guard, the wrong side of the wall
        w.Spawned.Add(1021557);
        w.Gates.Add(new Odysseus.Services.Travel.Doorways.Door(620, 620, 1021557, new Vector3(-130, 305, 190)));
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 620, Position = new Vector3(321, 324, 391) });

        for (var i = 0; i < 300 && ex.Status == StepStatus.Running; i++)
        {
            ex.Tick(); w.Advance(0.5);
            if (w.Calls.Contains("Interact 1021557") && w.PlayerPosition.X < 0)
            {
                w.PlayerPosition = new Vector3(300, 324, 380);   // the far side of the wall
                w.PathWaypointCount = 5;
                w.ArriveOnMove = true;
            }
        }
        Assert.Contains("Interact 1021557", w.Calls);
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    /// <summary>
    /// A gate guard's step names the zone it stands in as its target: the walk to him is still made.
    /// Read as "already crossed", the step stood 550 y away and called him missing.
    /// </summary>
    [Fact]
    public void A_gate_step_still_walks_to_its_guard()
    {
        var w = new FakeStepWorld { TerritoryId = 620, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(-223, 257, 741);
        w.Spawned.Add(1021557);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = 1021557, TerritoryId = 620, TargetTerritoryId = 620, Position = new Vector3(-130, 305, 190) });
        for (var i = 0; i < 30 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains("Mount", w.Calls);             // it set off for him — read as "already crossed", it never travelled
        Assert.Contains("Interact 1021557", w.Calls);
    }
}
