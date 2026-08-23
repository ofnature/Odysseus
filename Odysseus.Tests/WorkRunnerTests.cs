using Odysseus.Services.Deliveries;
using Odysseus.Services.Work;

namespace Odysseus.Tests;

public class WorkListTests
{
    private static WorkItem Society(uint id, int count = 0) => new(WorkKind.SocietyDailies, id, Count: count);
    private static WorkItem Delivery(uint id, DeliveryRoute route = DeliveryRoute.Craft, int count = 0)
        => new(WorkKind.Delivery, id, route, count);

    [Fact]
    public void Adding_the_same_job_twice_changes_it_rather_than_queueing_it_twice()
    {
        var list = new WorkList();
        list.Add(Society(1, count: 3));
        list.Add(Society(1, count: 6));

        var only = Assert.Single(list.Items);
        Assert.Equal(6, only.Count);
    }

    [Fact]
    public void A_clients_three_routes_are_three_separate_jobs()
    {
        var list = new WorkList();
        list.Add(Delivery(5, DeliveryRoute.Craft));
        list.Add(Delivery(5, DeliveryRoute.Gather));
        list.Add(Delivery(5, DeliveryRoute.Fish));
        Assert.Equal(3, list.Count);

        Assert.True(list.Remove(Delivery(5, DeliveryRoute.Gather)));
        Assert.Equal([DeliveryRoute.Craft, DeliveryRoute.Fish], list.Items.Select(i => i.Route));
    }

    [Fact]
    public void The_order_is_the_run_order_and_can_be_changed()
    {
        var list = new WorkList();
        list.Add(Society(1));
        list.Add(Society(2));
        list.Add(Delivery(5));

        list.Move(2, 0);
        Assert.Equal([WorkKind.Delivery, WorkKind.SocietyDailies, WorkKind.SocietyDailies], list.Items.Select(i => i.Kind));

        // Out of range is left alone rather than throwing at whoever is dragging a row.
        list.Move(9, 0);
        list.Move(0, -1);
        Assert.Equal(3, list.Count);
    }
}

public class WorkRunnerTests
{
    private sealed class Engines : IWorkEngines
    {
        public List<string> Started { get; } = [];
        public HashSet<uint> Refuse { get; } = [];
        public HashSet<uint> FaultOn { get; } = [];
        public bool Busy { get; private set; }
        public bool Faulted { get; private set; }
        public string FaultReason { get; private set; } = string.Empty;

        private uint _running;

        public bool StartSociety(uint societyId, int count, out string reason)
            => Start("society", societyId, count, out reason);

        public bool StartDelivery(uint clientId, DeliveryRoute route, int count, out string reason)
            => Start($"delivery/{route}", clientId, count, out reason);

        private bool Start(string what, uint id, int count, out string reason)
        {
            if (Refuse.Contains(id))
            {
                reason = "nothing left today";
                return false;
            }
            Started.Add($"{what} {id} x{count}");
            Busy = true;
            Faulted = false;
            _running = id;
            reason = string.Empty;
            return true;
        }

        /// <summary>The engine finishing, the way the plugin's tick would see it.</summary>
        public void FinishRunning()
        {
            Busy = false;
            if (FaultOn.Contains(_running))
            {
                Faulted = true;
                FaultReason = "could not reach the issuer";
            }
        }

        public string NameOf(WorkKind kind, uint targetId) => $"{kind}-{targetId}";
    }

    private static WorkItem Society(uint id, int count = 0) => new(WorkKind.SocietyDailies, id, Count: count);
    private static WorkItem Delivery(uint id, DeliveryRoute route = DeliveryRoute.Craft) => new(WorkKind.Delivery, id, route);

    /// <summary>Tick until it settles, letting each engine finish one job per pass.</summary>
    private static void Run(WorkRunner runner, Engines engines, int passes = 20)
    {
        for (var i = 0; i < passes && runner.State != WorkRunState.Done; i++)
        {
            runner.Tick();
            if (engines.Busy) { engines.FinishRunning(); runner.Tick(); }
        }
    }

    [Fact]
    public void Jobs_run_in_the_order_they_are_listed()
    {
        var engines = new Engines();
        var runner = new WorkRunner(engines, _ => { });
        runner.Begin([Society(1), Delivery(5, DeliveryRoute.Gather), Society(2)]);
        Run(runner, engines);

        Assert.Equal(WorkRunState.Done, runner.State);
        Assert.Equal(["society 1 x0", "delivery/Gather 5 x0", "society 2 x0"], engines.Started);
        Assert.All(runner.Outcomes, o => Assert.True(o.Ran));
    }

    [Fact]
    public void A_job_that_will_not_start_is_written_down_and_the_rest_still_run()
    {
        var engines = new Engines();
        engines.Refuse.Add(1);
        var runner = new WorkRunner(engines, _ => { });
        runner.Begin([Society(1), Society(2)]);
        Run(runner, engines);

        Assert.Equal(WorkRunState.Done, runner.State);
        Assert.Equal(["society 2 x0"], engines.Started);

        var skipped = Assert.Single(runner.Outcomes, o => !o.Ran);
        Assert.Equal("nothing left today", skipped.Note);
        Assert.Contains("1 skipped", runner.Status);
    }

    [Fact]
    public void A_job_that_faults_halfway_costs_that_job_and_not_the_day()
    {
        var engines = new Engines();
        engines.FaultOn.Add(1);
        var runner = new WorkRunner(engines, _ => { });
        runner.Begin([Society(1), Society(2)]);
        Run(runner, engines);

        Assert.Equal(WorkRunState.Done, runner.State);
        Assert.Equal(["society 1 x0", "society 2 x0"], engines.Started); // both were attempted
        Assert.Contains(runner.Outcomes, o => !o.Ran && o.Note.Contains("could not reach the issuer"));
        Assert.Contains(runner.Outcomes, o => o.Ran);
    }

    [Fact]
    public void One_shot_runs_the_head_of_the_list_and_stops()
    {
        var engines = new Engines();
        var runner = new WorkRunner(engines, _ => { });
        runner.Begin([Society(1), Society(2), Delivery(5)], limit: 1);
        Run(runner, engines);

        Assert.Equal(WorkRunState.Done, runner.State);
        Assert.Equal(["society 1 x0"], engines.Started);
        Assert.Single(runner.Outcomes);
    }

    [Fact]
    public void Nothing_is_started_on_top_of_something_already_running()
    {
        var engines = new Engines();
        var runner = new WorkRunner(engines, _ => { });
        runner.Begin([Society(1), Society(2)]);

        runner.Tick();                       // starts the first
        Assert.Single(engines.Started);
        runner.Tick(); runner.Tick();        // still busy: nothing else begins
        Assert.Single(engines.Started);

        engines.FinishRunning();
        runner.Tick();                       // notices it finished and records the outcome
        runner.Tick();                       // and only then starts the next
        Assert.Equal(2, engines.Started.Count);
    }

    [Fact]
    public void An_empty_list_is_done_rather_than_waiting_for_something()
    {
        var runner = new WorkRunner(new Engines(), _ => { });
        runner.Begin([]);
        Assert.Equal(WorkRunState.Done, runner.State);
        Assert.Equal("nothing to do", runner.Status);
    }
}
