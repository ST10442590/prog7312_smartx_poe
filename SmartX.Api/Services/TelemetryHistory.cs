using System.Diagnostics;
using SmartX.Shared.Collections;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

// Historical telemetry, stored as one AVL tree per device keyed on
// timestamp.
//
// One tree per device rather than one global tree, because every query the
// dashboard asks is device-scoped: "all moisture readings between 14:00 and
// 16:00 for Node A". A single shared tree would hold 24 times the data and
// still need filtering by device after the range was extracted.
//
// The archive from Part 1 keeps a flat time-ordered List for sequential
// work. This is a different question: random access into a specific window
// for a specific device, which a list cannot answer without scanning.
public sealed class TelemetryHistory
{
    private readonly DeviceRegistry _registry;
    private readonly Lock _gate = new();

    private readonly Dictionary<string, AvlTree<DateTimeOffset, HistoryEntryDto>> _trees =
        new(StringComparer.OrdinalIgnoreCase);

    // Readings retained per device. Bounded because the gateway is not an
    // archive — older data belongs in cold storage.
    private const int MaxPerDevice = 20_000;

    public TelemetryHistory(DeviceRegistry registry) => _registry = registry;

    // Records a reading in its device's tree.
    public void Record(ITelemetryPacket packet)
    {
        lock (_gate)
        {
            if (!_trees.TryGetValue(packet.DeviceId, out var tree))
            {
                tree = new AvlTree<DateTimeOffset, HistoryEntryDto>();
                _trees[packet.DeviceId] = tree;
            }

            // Timestamps can collide at sub-millisecond resolution when a
            // batch is published in one tick. Nudging forward keeps every
            // reading addressable rather than overwriting the earlier one.
            var key = packet.CapturedAtUtc;
            while (tree.Contains(key)) key = key.AddTicks(1);

            tree.Insert(key, new HistoryEntryDto
            {
                TimestampUtc = key,
                Value = Math.Round(packet.NumericValue, 3),
                Unit = packet.Unit
            });

            // Rebuilding from the newest entries is cheap at this size and
            // keeps the tree balanced, which repeated root deletion would
            // not. Deletion in an AVL tree is where most implementations
            // get their bugs, and nothing here needs it.
            if (tree.Count > MaxPerDevice) TrimLocked(packet.DeviceId, tree);
        }
    }

    private void TrimLocked(
        string deviceId, AvlTree<DateTimeOffset, HistoryEntryDto> tree)
    {
        var keep = tree
            .Skip(tree.Count - MaxPerDevice / 2)
            .ToList();

        var rebuilt = new AvlTree<DateTimeOffset, HistoryEntryDto>();
        foreach (var (key, value) in keep) rebuilt.Insert(key, value);

        _trees[deviceId] = rebuilt;
    }

    // The query the brief describes: every reading for one device inside a
    // timestamp window.
    public HistoryQueryDto Query(
        string deviceId, DateTimeOffset from, DateTimeOffset to, int take = 200)
    {
        var profile = _registry.GetProfile(deviceId);

        var result = new HistoryQueryDto
        {
            DeviceId = deviceId,
            DeviceName = profile?.DisplayName ?? deviceId,
            FromUtc = from,
            ToUtc = to
        };

        lock (_gate)
        {
            if (!_trees.TryGetValue(deviceId, out var tree)) return result;

            var stopwatch = Stopwatch.StartNew();
            var matches = tree.Range(from, to);
            stopwatch.Stop();

            result.MatchCount = matches.Count;
            result.TreeSize = tree.Count;
            result.TreeHeight = tree.Height;
            result.Comparisons = tree.LastOperationComparisons;
            result.ElapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 4);

            // An unbalanced tree built from ascending timestamps degenerates
            // into a chain, so its height equals its node count. Showing
            // both makes the difference concrete rather than theoretical.
            result.UnbalancedHeight = tree.Count;

            // Cap what crosses the wire; the counts above describe the full
            // result regardless.
            result.Entries = matches.Count <= take
                ? matches.Select(m => m.Value).ToList()
                : matches.Skip(matches.Count - take).Select(m => m.Value).ToList();
        }

        return result;
    }

    // Convenience wrapper for "the last N minutes", which is what the
    // dashboard asks for most often.
    public HistoryQueryDto QueryRecent(string deviceId, int minutes, int take = 200)
    {
        var now = DateTimeOffset.UtcNow;
        return Query(deviceId, now.AddMinutes(-Math.Abs(minutes)), now, take);
    }

    // Runs the same range query two ways, so the tree's pruning can be
    // compared against the obvious alternative rather than asserted.
    //
    // A query whose window happens to span the whole tree will touch every
    // node either way — that is correct behaviour, not a failure. The
    // difference only appears on a narrow window, which is the case that
    // actually matters when history is deep.
    public RangeComparisonDto CompareRangeStrategies(
        string deviceId, DateTimeOffset from, DateTimeOffset to)
    {
        var profile = _registry.GetProfile(deviceId);

        var result = new RangeComparisonDto
        {
            DeviceId = deviceId,
            DeviceName = profile?.DisplayName ?? deviceId,
            FromUtc = from,
            ToUtc = to
        };

        lock (_gate)
        {
            if (!_trees.TryGetValue(deviceId, out var tree)) return result;

            result.TreeSize = tree.Count;
            result.TreeHeight = tree.Height;

            // Tree range query: descends from the root and prunes subtrees
            // that cannot contain anything in the window.
            var sw = Stopwatch.StartNew();
            var treeMatches = tree.Range(from, to);
            sw.Stop();

            result.TreeComparisons = tree.LastOperationComparisons;
            result.TreeElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 4);
            result.MatchCount = treeMatches.Count;

            // Linear scan: the alternative if history were kept in a list.
            // Every reading is examined regardless of the window.
            sw.Restart();
            var scanned = 0;
            var scanMatches = 0;

            foreach (var (key, _) in tree)
            {
                scanned++;
                if (key >= from && key <= to) scanMatches++;
            }
            sw.Stop();

            result.ScanComparisons = scanned;
            result.ScanElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 4);
            result.ScanMatchCount = scanMatches;

            result.Sample = treeMatches
                .TakeLast(5)
                .Select(m => m.Value)
                .ToList();
        }

        return result;
    }

    public HistoryOverviewDto Overview()
    {
        lock (_gate)
        {
            var devices = new List<HistoryStatsDto>(_trees.Count);

            foreach (var (deviceId, tree) in _trees)
            {
                var profile = _registry.GetProfile(deviceId);

                string? earliest = null;
                string? latest = null;

                if (tree.TryMin(out var minKey, out _))
                    earliest = minKey.ToString("HH:mm:ss");

                if (tree.TryMax(out var maxKey, out _))
                    latest = maxKey.ToString("HH:mm:ss");

                devices.Add(new HistoryStatsDto
                {
                    DeviceId = deviceId,
                    DeviceName = profile?.DisplayName ?? deviceId,
                    Readings = tree.Count,
                    TreeHeight = tree.Height,
                    Rotations = tree.Rotations,
                    EarliestUtc = earliest,
                    LatestUtc = latest
                });
            }

            return new HistoryOverviewDto
            {
                DeviceCount = devices.Count,
                TotalReadings = devices.Sum(d => (long)d.Readings),
                TotalRotations = devices.Sum(d => d.Rotations),
                TallestTree = devices.Count == 0 ? 0 : devices.Max(d => d.TreeHeight),
                Devices = devices.OrderBy(d => d.DeviceName).ToList()
            };
        }
    }
}