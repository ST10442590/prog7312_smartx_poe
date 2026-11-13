using SmartX.Shared.Sensors;

namespace SmartX.Shared.Telemetry;

// One historical reading, as returned by a range query.
public sealed class HistoryEntryDto
{
    public DateTimeOffset TimestampUtc { get; set; }
    public double Value { get; set; }
    public TelemetryUnit Unit { get; set; }
}

// Result of a timestamp range query against a device's AVL tree.
public sealed class HistoryQueryDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }

    public int MatchCount { get; set; }

    // Readings held in this device's tree in total.
    public int TreeSize { get; set; }

    // Tree height. The evidence that balancing is working: for n readings
    // this stays near log2(n) rather than approaching n.
    public int TreeHeight { get; set; }

    // What an unbalanced tree built from this same sorted input would have
    // reached, for comparison.
    public int UnbalancedHeight { get; set; }

    // Node comparisons the query made. A linear scan would be TreeSize.
    public int Comparisons { get; set; }

    public double ElapsedMs { get; set; }

    public List<HistoryEntryDto> Entries { get; set; } = [];
}

// Per-device summary of the history store.
public sealed class HistoryStatsDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int Readings { get; set; }
    public int TreeHeight { get; set; }
    public long Rotations { get; set; }
    public string? EarliestUtc { get; set; }
    public string? LatestUtc { get; set; }
}

// Whole-store summary.
public sealed class HistoryOverviewDto
{
    public int DeviceCount { get; set; }
    public long TotalReadings { get; set; }
    public long TotalRotations { get; set; }
    public int TallestTree { get; set; }
    public List<HistoryStatsDto> Devices { get; set; } = [];
}

// Tree range query versus linear scan over the same window.
public sealed class RangeComparisonDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }

    public int TreeSize { get; set; }
    public int TreeHeight { get; set; }
    public int MatchCount { get; set; }

    // Nodes the pruned tree descent touched.
    public int TreeComparisons { get; set; }
    public double TreeElapsedMs { get; set; }

    // Readings a list-based scan would have examined: always the whole tree.
    public int ScanComparisons { get; set; }
    public double ScanElapsedMs { get; set; }
    public int ScanMatchCount { get; set; }

    // Proportion of the data the tree avoided looking at.
    public double PrunedPercent => ScanComparisons == 0
        ? 0
        : Math.Round((1.0 - (double)TreeComparisons / ScanComparisons) * 100, 1);

    public List<HistoryEntryDto> Sample { get; set; } = [];
}