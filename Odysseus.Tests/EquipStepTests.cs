using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>
/// The three verbs a class-unlock quest is made of: here is your tool, wear it, and save a gearset
/// for the class you have just become.
/// </summary>
public class EquipStepTests
{
    private const uint ChaserHammer = 2391;
    /// <summary>ClassJob 11 — Goldsmith, the class a Chaser Hammer makes you (8–15 are CRP..CUL).</summary>
    private const uint Goldsmith = 11;

    private static StepStatus Run(StepExecutor ex, FakeStepWorld world, int maxTicks = 200)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            var s = ex.Tick();
            if (s != StepStatus.Running) return s;
            world.Advance(0.5);
        }
        return ex.Status;
    }

    private static QuestStep Step(StepKind kind, uint? itemId = null) => new()
    {
        Kind = kind, KindName = kind.ToString(), TerritoryId = 400, ItemId = itemId,
    };

    [Fact]
    public void EquipItem_wears_the_quest_tool()
    {
        var world = new FakeStepWorld();
        world.Equippable.Add(ChaserHammer);
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.EquipItem, ChaserHammer));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains($"Equip {ChaserHammer}", world.Calls);
    }

    [Fact]
    public void Something_already_worn_is_left_alone()
    {
        var world = new FakeStepWorld();
        world.Equipped.Add(ChaserHammer);
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.EquipItem, ChaserHammer));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain($"Equip {ChaserHammer}", world.Calls);
    }

    [Fact]
    public void An_item_that_is_nowhere_to_be_found_stops_with_a_reason()
    {
        var world = new FakeStepWorld();
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.EquipItem, ChaserHammer));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("could not be equipped", ex.FailReason);
    }

    // ── Falling back to a gearset ──
    //
    // The class-unlock quests assume you own no tool for the class. On a character who already
    // plays it that premise is false, and the game changes class off the main hand — so any tool
    // for the class does what the quest wanted, which is what the gearset carries.

    [Fact]
    public void A_missing_class_tool_falls_back_to_that_classs_gearset()
    {
        var world = new FakeStepWorld { CurrentClassJob = 21 };   // on Warrior
        world.ToolClasses[ChaserHammer] = Goldsmith;
        world.SavedGearsets.Add(new GearsetInfo(7, Goldsmith, 0, 100, JobKind.Crafter));
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.EquipItem, ChaserHammer));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Gearset 7", world.Calls);
        Assert.Equal(Goldsmith, world.CurrentClassJob);
    }

    /// <summary>
    /// Already the class: a tool for it is in your hand. Equipping the weathered one the quest
    /// hands out would be a downgrade, so the step is simply satisfied.
    /// </summary>
    [Fact]
    public void Already_being_the_class_satisfies_the_step_without_touching_anything()
    {
        var world = new FakeStepWorld { CurrentClassJob = Goldsmith };
        world.ToolClasses[ChaserHammer] = Goldsmith;
        world.Equippable.Add(ChaserHammer);
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.EquipItem, ChaserHammer));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain($"Equip {ChaserHammer}", world.Calls);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Gearset"));
    }

    /// <summary>The item still wins when it is actually there — it is what the step asked for.</summary>
    [Fact]
    public void A_held_class_tool_is_equipped_rather_than_the_gearset()
    {
        var world = new FakeStepWorld { CurrentClassJob = 21 };
        world.ToolClasses[ChaserHammer] = Goldsmith;
        world.Equippable.Add(ChaserHammer);
        world.SavedGearsets.Add(new GearsetInfo(7, Goldsmith, 0, 100, JobKind.Crafter));
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.EquipItem, ChaserHammer));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains($"Equip {ChaserHammer}", world.Calls);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Gearset"));
    }

    /// <summary>
    /// The fallback is for class tools only. Gear that merely happens to be restricted — the Ixal
    /// wristgloves a craft requires — is itself the requirement, and no gearset stands in for it.
    /// </summary>
    [Fact]
    public void Ordinary_gear_gets_no_gearset_fallback()
    {
        var world = new FakeStepWorld { CurrentClassJob = Goldsmith };
        world.SavedGearsets.Add(new GearsetInfo(7, Goldsmith, 0, 100, JobKind.Crafter));
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.EquipItem, 8568));   // Ehcatl Wristgloves — no ToolClasses entry

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("could not be equipped", ex.FailReason);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Gearset"));
    }

    [Fact]
    public void A_missing_tool_with_no_gearset_for_its_class_says_both()
    {
        var world = new FakeStepWorld { CurrentClassJob = 21 };
        world.ToolClasses[ChaserHammer] = Goldsmith;
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.EquipItem, ChaserHammer));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("not held", ex.FailReason);
        Assert.Contains("no gearset for its class", ex.FailReason);
    }

    /// <summary>Equipping is a server round trip, so the slot filling is what counts as done.</summary>
    [Fact]
    public void An_equip_that_never_lands_faults_rather_than_hanging()
    {
        var world = new FakeStepWorld { EquipLandsNow = false };
        world.Equippable.Add(ChaserHammer);
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.EquipItem, ChaserHammer));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("never reached an equipment slot", ex.FailReason);
    }

    /// <summary>
    /// The step exists for the moment a class is first unlocked. On a character that already plays
    /// it, a second gearset would be worse than doing nothing — and this is also what makes the
    /// step safe to replay, which it has to be to survive a resumed sequence.
    /// </summary>
    [Fact]
    public void CreateGearset_does_nothing_when_the_class_already_has_one()
    {
        var world = new FakeStepWorld { CurrentClassJob = 11 };
        world.SavedGearsets.Add(new GearsetInfo(3, 11, 0, 100, JobKind.Crafter));
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.CreateGearset));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain("CreateGearset", world.Calls);
    }

    [Fact]
    public void CreateGearset_saves_one_for_a_class_that_has_none()
    {
        var world = new FakeStepWorld { CurrentClassJob = 11 };
        world.SavedGearsets.Add(new GearsetInfo(0, 24, 6, 90, JobKind.Combat));
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.CreateGearset));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("CreateGearset", world.Calls);
    }

    [Fact]
    public void CreateGearset_with_every_slot_taken_says_so()
    {
        var world = new FakeStepWorld { CurrentClassJob = 11, GearsetSlotFree = false };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.CreateGearset));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("all 100 are in use", ex.FailReason);
    }

    [Fact]
    public void UpdateGearset_overwrites_the_active_one()
    {
        var world = new FakeStepWorld();
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.UpdateGearset));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("UpdateGearset", world.Calls);
    }

    [Fact]
    public void UpdateGearset_with_no_active_gearset_says_so()
    {
        var world = new FakeStepWorld { HasActiveGearset = false };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.UpdateGearset));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("no active gearset", ex.FailReason);
    }

    /// <summary>
    /// The data tags every step with a territory, but for these it records where the path author
    /// stood rather than a requirement. Enforcing it stopped a run at the Free Company workshop
    /// (984) because the step was written in Ul'dah (131) — with nothing to walk to and no reason
    /// to be anywhere.
    /// </summary>
    [Theory]
    [InlineData(StepKind.EquipItem)]
    [InlineData(StepKind.CreateGearset)]
    [InlineData(StepKind.UpdateGearset)]
    [InlineData(StepKind.SwitchClass)]
    [InlineData(StepKind.Craft)]
    public void A_step_done_on_the_character_runs_in_any_zone(StepKind kind)
    {
        var world = new FakeStepWorld { TerritoryId = 984, CurrentClassJob = Goldsmith };
        world.Equipped.Add(ChaserHammer);
        world.Bag[5056] = 5;
        world.ClassJobs["Goldsmith"] = Goldsmith;
        world.SavedGearsets.Add(new GearsetInfo(7, Goldsmith, 0, 100, JobKind.Crafter));

        var step = new QuestStep
        {
            Kind = kind, KindName = kind.ToString(), TerritoryId = 131,
            ItemId = kind == StepKind.Craft ? 5056 : ChaserHammer,
            ItemCount = 1,
            TargetClass = "Goldsmith",
        };
        var ex = new StepExecutor(world);
        ex.Begin(step);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Move"));
    }

    /// <summary>A step that really is somewhere still says so — the gate is narrowed, not removed.</summary>
    [Fact]
    public void A_step_in_the_world_still_needs_the_right_zone()
    {
        var world = new FakeStepWorld { TerritoryId = 984 };
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", TerritoryId = 131, DataId = 5 });

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("you are in 984", ex.FailReason);
    }

    /// <summary>
    /// The case this was all for: press Start at the Free Company workshop with the quest in
    /// Ul'dah, and the run takes itself there rather than stopping — or, as Questionable does,
    /// waiting in silence for a zone change that is never coming.
    /// </summary>
    [Fact]
    public void A_step_in_another_zone_routes_itself_there_when_the_path_names_no_aetheryte()
    {
        var world = new FakeStepWorld { TerritoryId = 984 };
        world.AttunedByTerritory[131] = 9;
        world.AetheryteTerritories[9] = 131;
        world.Spawned.Add(5);
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", TerritoryId = 131, DataId = 5 });

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Teleport 9", world.Calls);
        Assert.Equal(131u, world.TerritoryId);
    }

    /// <summary>With nothing attuned there, it stops saying exactly that rather than going quiet.</summary>
    [Fact]
    public void With_nothing_attuned_in_the_zone_it_says_so()
    {
        var world = new FakeStepWorld { TerritoryId = 984 };
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", TerritoryId = 131, DataId = 5 });

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("no aetheryte there that you have attuned", ex.FailReason);
    }

    /// <summary>All three have to survive a replayed sequence without doing damage.</summary>
    [Fact]
    public void All_three_are_replay_safe()
    {
        Assert.True(new QuestStep { Kind = StepKind.EquipItem }.IsReplaySafe);
        Assert.True(new QuestStep { Kind = StepKind.CreateGearset }.IsReplaySafe);
        Assert.True(new QuestStep { Kind = StepKind.UpdateGearset }.IsReplaySafe);
    }
}
