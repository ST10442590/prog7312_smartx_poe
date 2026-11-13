using System.Diagnostics;
using SmartX.Shared.Collections;
using SmartX.Shared.Sensors;

namespace SmartX.Api.Services;

// Models the physical mesh as a weighted graph and answers routing
// questions about it.
//
// A note on honesty: the Smart-X deployment is simulated, so no real radio
// links exist to measure. This service generates a plausible topology
// instead — nodes link to others in the same sub-zone, to a few across zone
// boundaries, and the gateway anchors the centre. Edge weights are derived
// deterministically from the node identifiers, so the same mesh is produced
// every run.
//
// What is genuine is everything built on top: the traversals, the spanning
// tree, and the re-routing when a node goes offline all operate on the
// graph exactly as they would on measured link data.
public sealed class MeshNetworkService
{
    public const string GatewayId = "GATEWAY";

    private readonly DeviceRegistry _registry;
    private readonly ILogger<MeshNetworkService> _logger;
    private readonly Lock _gate = new();

    private MeshGraph _graph = new();
    private DateTimeOffset _builtAtUtc;

    public MeshNetworkService(
        DeviceRegistry registry, ILogger<MeshNetworkService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    // Rebuilds the topology from the current device registry.
    public void Build()
    {
        lock (_gate)
        {
            var graph = new MeshGraph();
            graph.AddVertex(GatewayId);

            var profiles = _registry.AllProfiles()
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var profile in profiles) graph.AddVertex(profile.Id);

            // Group by sub-zone: nodes physically near each other are the
            // ones that can hear each other.
            var subZones = profiles
                .GroupBy(p => $"{p.Zone}|{p.SubZone}", StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var subZone in subZones)
            {
                var members = subZone.ToList();

                // Within a sub-zone, every node can reach every other. Short
                // range, so these links are cheap.
                for (var i = 0; i < members.Count; i++)
                {
                    for (var j = i + 1; j < members.Count; j++)
                    {
                        graph.AddEdge(
                            members[i].Id,
                            members[j].Id,
                            LinkCost(members[i].Id, members[j].Id, baseCost: 4));
                    }
                }

                // One node per sub-zone acts as its relay to the gateway.
                // Picking the first by id keeps the topology reproducible.
                var relay = members[0];
                graph.AddEdge(
                    relay.Id, GatewayId, LinkCost(relay.Id, GatewayId, baseCost: 12));

                // A second, more expensive uplink from another member, so
                // losing the relay does not strand the whole sub-zone. This
                // is the redundancy that lets the mesh heal.
                if (members.Count > 3)
                {
                    graph.AddEdge(
                        members[3].Id,
                        GatewayId,
                        LinkCost(members[3].Id, GatewayId, baseCost: 22));
                }
            }

            // Cross-zone links between adjacent sub-zones. Longer range, so
            // more expensive — Prim's will avoid them unless they are the
            // only way through.
            for (var i = 0; i < subZones.Count - 1; i++)
            {
                var left = subZones[i].ToList();
                var right = subZones[i + 1].ToList();

                if (left.Count == 0 || right.Count == 0) continue;

                graph.AddEdge(
                    left[^1].Id,
                    right[0].Id,
                    LinkCost(left[^1].Id, right[0].Id, baseCost: 16));
            }

            _graph = graph;
            _builtAtUtc = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Mesh graph built: {Vertices} vertices, {Edges} edges.",
                graph.VertexCount, graph.EdgeCount);
        }

        // Reflect who is actually responding right now.
        SyncOutages();
    }

    // Marks vertices online or offline according to live device health, so
    // traversals route around nodes that have genuinely dropped out.
    public void SyncOutages()
    {
        var now = DateTimeOffset.UtcNow;
        var tiles = _registry.Snapshot(now);

        lock (_gate)
        {
            // The gateway is mains-powered infrastructure, not a battery
            // node, so it is always treated as up.
            _graph.SetOnline(GatewayId, true);

            foreach (var tile in tiles)
            {
                _graph.SetOnline(
                    tile.DeviceId, tile.State != HealthState.Disconnected);
            }
        }
    }

    // Deterministic pseudo-distance from the two identifiers, so the same
    // pair always produces the same link cost.
    private static double LinkCost(string a, string b, double baseCost)
    {
        var hash = 0;
        foreach (var c in a) hash = hash * 31 + c;
        foreach (var c in b) hash = hash * 31 + c;

        // 0 to 7.9 of jitter on top of the base cost.
        var jitter = Math.Abs(hash % 80) / 10.0;
        return Math.Round(baseCost + jitter, 2);
    }

    // ----------------------------------------------------------- queries

    public MeshGraph Graph
    {
        get { lock (_gate) return _graph; }
    }

    public DateTimeOffset BuiltAtUtc
    {
        get { lock (_gate) return _builtAtUtc; }
    }

    // Which devices can currently reach the gateway, and in how many hops.
    public TraversalResult Reachability(bool respectOutages = true)
    {
        lock (_gate) return _graph.BreadthFirst(GatewayId, respectOutages);
    }

    public TraversalResult Traverse(
        string start, bool depthFirst, bool respectOutages = true)
    {
        lock (_gate)
        {
            return depthFirst
                ? _graph.DepthFirst(start, respectOutages)
                : _graph.BreadthFirst(start, respectOutages);
        }
    }

    // The fewest-hop route from a device back to the gateway.
    public List<string> RouteToGateway(string deviceId, bool respectOutages = true)
    {
        lock (_gate)
        {
            // Searching outward from the device means the parent map points
            // back toward it, so the reconstructed path runs device first,
            // gateway last — which is the direction a packet travels.
            var traversal = _graph.BreadthFirst(deviceId, respectOutages);
            return MeshGraph.PathFrom(traversal, GatewayId);
        }
    }

    public SpanningTreeResult MinimumSpanningTree(bool respectOutages = true)
    {
        lock (_gate) return _graph.PrimsMinimumSpanningTree(GatewayId, respectOutages);
    }

    // Compares the mesh before and after a node is taken out, which is the
    // self-healing behaviour the brief describes.
    public FailureImpactResult SimulateFailure(string deviceId)
    {
        lock (_gate)
        {
            var before = _graph.BreadthFirst(GatewayId, respectOutages: true);
            var mstBefore = _graph.PrimsMinimumSpanningTree(GatewayId, respectOutages: true);

            var wasOnline = _graph.IsOnline(deviceId);

            var stopwatch = Stopwatch.StartNew();
            _graph.SetOnline(deviceId, false);

            var after = _graph.BreadthFirst(GatewayId, respectOutages: true);
            var mstAfter = _graph.PrimsMinimumSpanningTree(GatewayId, respectOutages: true);
            stopwatch.Stop();

            // Restore, since this is a what-if rather than a change.
            _graph.SetOnline(deviceId, wasOnline);

            var strandedNow = after.Unreachable
                .Where(v => !before.Unreachable.Contains(v, StringComparer.OrdinalIgnoreCase))
                .Where(v => !string.Equals(v, deviceId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Devices whose hop count changed still reach the gateway, but
            // by a different route. That is the mesh healing.
            var rerouted = new List<ReroutedNode>();

            foreach (var (vertex, hopsAfter) in after.HopCounts)
            {
                if (!before.HopCounts.TryGetValue(vertex, out var hopsBefore)) continue;
                if (hopsBefore == hopsAfter) continue;

                rerouted.Add(new ReroutedNode(vertex, hopsBefore, hopsAfter));
            }

            return new FailureImpactResult
            {
                FailedDevice = deviceId,
                ReachableBefore = before.Visited,
                ReachableAfter = after.Visited,
                StrandedDevices = strandedNow,
                ReroutedDevices = rerouted.OrderBy(r => r.DeviceId).ToList(),
                MstWeightBefore = mstBefore.TotalWeight,
                MstWeightAfter = mstAfter.TotalWeight,
                MeshHealed = strandedNow.Count == 0,
                RecalculationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)
            };
        }
    }
}

public sealed record ReroutedNode(string DeviceId, int HopsBefore, int HopsAfter);

// What happens to the mesh when one node is lost.
public sealed class FailureImpactResult
{
    public string FailedDevice { get; set; } = string.Empty;
    public int ReachableBefore { get; set; }
    public int ReachableAfter { get; set; }

    // Devices that can no longer reach the gateway at all.
    public List<string> StrandedDevices { get; set; } = [];

    // Devices still connected, but via a different number of hops.
    public List<ReroutedNode> ReroutedDevices { get; set; } = [];

    public double MstWeightBefore { get; set; }
    public double MstWeightAfter { get; set; }

    // True when every device still reaches the gateway, which is what the
    // redundant uplinks exist to achieve.
    public bool MeshHealed { get; set; }

    public double RecalculationMs { get; set; }
}