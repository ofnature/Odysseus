using System.Numerics;
using Odysseus.Services.Deliveries;
using Odysseus.Services.Gathering;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class OwnGathererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Keyed by item, as the real one is: a lookup that answers for anything is not a lookup.</summary>
    private sealed class Source : IGatheringSource
    {
        public Dictionary<uint, List<GatheringPointRef>> Points { get; } = new();
        public GatheringOrigin? For(uint itemId) => null;
        public IReadOnlyList<uint> BasesFor(uint itemId) => [];
        public IReadOnlyList<GatheringPointRef> PointsFor(uint itemId)
            => Points.TryGetValue(itemId, out var points) ? points : [];
    }

    private NodeAtlas Atlas(string json)
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, NodeAtlas.FileName);
        using (var raw = File.Create(file))
        using (var gzip = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionLevel.Optimal))
        using (var writer = new StreamWriter(gzip))
            writer.Write(json);
        return new NodeAtlas(file);
    }

    private (OwnGatherer Own, GatherRunner Runner) Ready()
    {
        var atlas = Atlas("""{ "31427": [ {"X":-18.4,"Y":12.4,"Z":-427.6}, {"X":-20.0,"Y":12.4,"Z":-430.0} ] }""");
        var source = new Source();
        source.Points[12535] = [new GatheringPointRef(31427, 401, 0, 60)]; // Glass Eye

        // A world far from the node and on the wrong class, so a real run has work to do.
        var world = new GatherWorldStub { TerritoryId = 129, CurrentClassJob = 34 };
        var runner = new GatherRunner(world, new StepExecutor(new FakeStepWorld()));
        // On, because these tests are about the gathering. It ships off; see the test below.
        var own = new OwnGatherer(runner, source, atlas, _ => { }) { Enabled = true };
        return (own, runner);
    }

    /// <summary>Enough of a world to start a runner; nothing here is exercised beyond that.</summary>
    private sealed class GatherWorldStub : IGatherWorld
    {
        public DateTime UtcNow { get; set; } = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
        public uint TerritoryId { get; set; }
        public Vector3 PlayerPosition { get; set; }
        public bool IsOccupied => false;
        public uint CurrentClassJob { get; set; }
        public bool IsMounted { get; set; }
        public void Dismount() => IsMounted = false;
        public bool EquipGearsetFor(uint classJobId, out string reason) { CurrentClassJob = classJobId; reason = string.Empty; return true; }
        public int CollectableCount(uint itemId, int minimumCollectability) => 0;
        public bool IsDataIdSpawned(uint dataId) => false;
        public float? DistanceToDataId(uint dataId) => null;
        public Vector3? PositionOfDataId(uint dataId) => null;
        public (uint NodeId, Vector3 Position, float Distance)? NearestLiveNode(IReadOnlyCollection<uint> nodeIds) => null;
        public void FaceDataId(uint dataId) { }
        public bool TryInteractWithDataId(uint dataId) => false;
        public bool NodeOpen => false;
        public bool ItemListOpen => false;
        public bool ExecutingAction => false;
        public CollectableState? Collectable => null;
        public bool SelectSlotFor(uint itemId) => false;
        public bool UseAction(uint actionId) => true;
        public void StopMoving() { }
        public string Conditions => string.Empty;
        public void DescribeOpenWindow() { }
        public void ForgetSlotAttempts() { }
        public void CloseNode() { }
        public void Log(string message) { }
    }

    [Fact]
    public void A_run_that_has_only_just_begun_reads_as_busy()
    {
        // It began in SwitchingJob, which an enumeration of "the busy states" missed — and a caller
        // seeing idle one tick after starting concludes it gave up before it moved, which is exactly
        // what "gathering stopped short" meant in the field.
        var (own, runner) = Ready();

        Assert.False(own.Busy);
        Assert.True(own.Start(12535, 6, 240));
        Assert.Equal(GatherRunState.SwitchingJob, runner.State);
        Assert.True(own.Busy, "a runner that has just started must not read as finished");
    }

    [Fact]
    public void Every_state_that_is_not_finished_counts_as_busy()
    {
        // The property is the inverse of three states, so a state added later cannot quietly mean
        // "done" — this pins that rather than the three names.
        var (own, runner) = Ready();
        own.Start(12535, 6, 240);

        foreach (var state in Enum.GetValues<GatherRunState>())
        {
            var finished = state is GatherRunState.Idle or GatherRunState.Done or GatherRunState.Faulted;
            if (finished) continue;
            Assert.True(state is GatherRunState.SwitchingJob or GatherRunState.Travelling
                or GatherRunState.Walking or GatherRunState.Opening or GatherRunState.Working
                or GatherRunState.Closing,
                $"{state} is a running state and must be covered by Busy");
        }

        own.Stop();
        Assert.Equal(GatherRunState.Idle, runner.State);
        Assert.False(own.Busy);
    }

    [Fact]
    public void It_is_off_until_switched_on_so_a_delivery_falls_back_to_the_handoff()
    {
        // Opening a node has wedged the client, so nothing reaches that path by accident: a
        // delivery sees "cannot gather this" and hands to GatherBuddy exactly as it always did.
        var (own, _) = Ready();
        own.Enabled = false;

        Assert.False(own.CanGather(12535));
        Assert.False(own.Start(12535, 6, 240) && own.Busy);
    }

    [Fact]
    public void An_item_with_no_node_is_declined_so_the_caller_can_fall_back()
    {
        var (own, _) = Ready();
        Assert.False(own.CanGather(99999));
        Assert.False(own.Start(99999, 1, 240));
        Assert.True(own.CanGather(12535));
    }
}
