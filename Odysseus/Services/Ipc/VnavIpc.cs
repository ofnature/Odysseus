using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// vnavmesh, wrapped. Every call fails open: a missing navmesh plugin degrades movement to
/// "nothing happens", reported once, rather than throwing inside a run loop.
/// </summary>
public sealed class VnavIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<bool>? _isReady;
    private ICallGateSubscriber<float>? _buildProgress;
    private ICallGateSubscriber<bool>? _pathIsRunning;
    private ICallGateSubscriber<bool>? _pathfindInProgress;
    private ICallGateSubscriber<Vector3, bool, bool>? _moveTo;
    private ICallGateSubscriber<Vector3, bool, float, bool>? _moveCloseTo;
    private ICallGateSubscriber<List<Vector3>, bool, object>? _pathMoveTo;
    private ICallGateSubscriber<int>? _numWaypoints;
    private ICallGateSubscriber<object>? _stop;
    private ICallGateSubscriber<float, object>? _setTolerance;
    private ICallGateSubscriber<Vector3, float, float, Vector3?>? _nearestPointReachable;

    private bool _warned;

    public VnavIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>The navmesh for this zone is built and usable.</summary>
    public bool IsReady => Try(() =>
        (_isReady ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady")).InvokeFunc());

    /// <summary>
    /// How far through building the zone's mesh it is, 0..1 — or a negative number when it is not
    /// building at all. That distinction is the point: "not ready" covers both a mesh a minute away
    /// from being usable and one that is never coming, and only the second is a fault.
    /// </summary>
    public float BuildProgress
    {
        get
        {
            try
            {
                return (_buildProgress ??= _pluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress"))
                    .InvokeFunc();
            }
            catch
            {
                return -1f;
            }
        }
    }

    /// <summary>
    /// A path is being followed right now.
    ///
    /// <para>
    /// Also what BossMod watches — while this is true it suppresses its own movement — so there is
    /// no "who owns movement" toggle to manage between the two. Running a path <i>is</i> the
    /// handover, which removes a whole class of conflict before it can exist.
    /// </para>
    /// </summary>
    public bool IsPathRunning => Try(() =>
        (_pathIsRunning ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning")).InvokeFunc());

    private ICallGateSubscriber<List<Vector3>>? _listWaypoints;

    /// <summary>The waypoints of the path being followed right now; empty when idle or unavailable.</summary>
    public List<Vector3> ListWaypoints()
    {
        try
        {
            return (_listWaypoints ??= _pluginInterface.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints"))
                .InvokeFunc() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>A pathfind is still being computed — movement is pending, not failed.</summary>
    public bool IsPathfinding => Try(() =>
        (_pathfindInProgress ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress"))
        .InvokeFunc());

    /// <summary>Whether movement of any kind is in flight.</summary>
    public bool IsBusy => IsPathfinding || IsPathRunning;

    /// <summary>
    /// Waypoints in the current path. Zero once a pathfind has finished means it found nothing —
    /// which is how an unreachable destination announces itself, immediately, instead of being
    /// inferred from three minutes of standing still.
    /// </summary>
    public int WaypointCount
    {
        get
        {
            try
            {
                return (_numWaypoints ??= _pluginInterface.GetIpcSubscriber<int>("vnavmesh.Path.NumWaypoints"))
                    .InvokeFunc();
            }
            catch
            {
                // Unknown rather than zero: reporting "no path" from a missing gate would fault
                // every run the moment vnavmesh was absent.
                return -1;
            }
        }
    }

    /// <summary>Paths to a point and starts following it.</summary>
    public bool MoveTo(Vector3 destination, bool fly = false) => Try(() =>
        (_moveTo ??= _pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo"))
        .InvokeFunc(destination, fly));

    /// <summary>
    /// Walks straight at a point, with no pathfinding at all.
    ///
    /// <para>
    /// <c>Path.MoveTo</c> takes a literal waypoint list and follows it; only <c>SimpleMove.*</c>
    /// consults the mesh. That is what a step marked <c>DisableNavmesh</c> needs — those are the
    /// places the path authors found the mesh gets it wrong, so asking it again is the one thing
    /// that cannot work.
    /// </para>
    /// </summary>
    public bool MoveDirect(Vector3 destination, bool fly = false) => Try(() =>
    {
        (_pathMoveTo ??= _pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo"))
            .InvokeAction([destination], fly);
        return true;
    });

    /// <summary>Paths to within <paramref name="tolerance"/> of a point — for standing next to things.</summary>
    public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly = false) => Try(() =>
        (_moveCloseTo ??= _pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>(
            "vnavmesh.SimpleMove.PathfindAndMoveCloseTo")).InvokeFunc(destination, fly, tolerance));

    /// <summary>
    /// Snaps a point onto the walkable mesh, or null when nothing reachable is near it.
    ///
    /// <para>
    /// Map markers carry a map-space X and Y and no height at all, so a marker cannot be walked to
    /// as given — this is what turns one into a destination. Reachability matters as much as the
    /// height: a marker on the far side of a locked door snaps to nothing, which is exactly the
    /// signal that it is not the frontier yet.
    /// </para>
    /// </summary>
    public Vector3? NearestReachablePoint(Vector3 near, float halfExtentXZ = 20f, float halfExtentY = 20f)
    {
        try
        {
            return (_nearestPointReachable ??= _pluginInterface
                    .GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable"))
                .InvokeFunc(near, halfExtentXZ, halfExtentY);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ask vnavmesh to rebuild the current zone's mesh from live collision. The cure for a cache
    /// built before the world changed shape — a quest unlock opening a camp, a festival — which
    /// vnavmesh's cache key does not always notice. Takes a minute; IsReady goes false meanwhile.
    /// </summary>
    public bool Rebuild() => Try(() =>
    {
        (_rebuild ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.Rebuild")).InvokeFunc();
        return true;
    });

    private ICallGateSubscriber<bool>? _rebuild;

    public void Stop() => Try(() =>
    {
        (_stop ??= _pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop")).InvokeAction();
        return true;
    });

    public void SetTolerance(float tolerance) => Try(() =>
    {
        (_setTolerance ??= _pluginInterface.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance"))
            .InvokeAction(tolerance);
        return true;
    });

    private bool Try(Func<bool> call)
    {
        try
        {
            var result = call();
            _warned = false;
            return result;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"vnavmesh unavailable ({ex.GetType().Name}) — movement is disabled until it loads.");
            }

            return false;
        }
    }
}
