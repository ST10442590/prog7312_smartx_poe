namespace SmartX.Shared.Collections;

// An undirected weighted graph, stored as an adjacency list.
//
// Adjacency list rather than an adjacency matrix because a mesh is sparse:
// each node links to a handful of neighbours, not to every other node. A
// matrix for n nodes costs n-squared cells regardless of how few edges
// exist — at a thousand nodes that is a million entries to hold six
// thousand links.
//
// Edge weight is transmission cost: a function of distance and signal
// quality. Lower is cheaper, so the minimum spanning tree over these
// weights is the most power-efficient way to reach every node.
public sealed class MeshGraph
{
    // Vertex id -> its neighbours and the cost of reaching each.
    private readonly Dictionary<string, Dictionary<string, double>> _adjacency =
        new(StringComparer.OrdinalIgnoreCase);

    // Vertices currently reachable, i.e. powered and responding. A node that
    // has dropped out stays in the graph but is excluded from traversal, so
    // routing can be recalculated around it.
    private readonly HashSet<string> _online =
        new(StringComparer.OrdinalIgnoreCase);

    public int VertexCount => _adjacency.Count;

    public int EdgeCount
    {
        get
        {
            // Each undirected edge appears in two adjacency lists.
            var directed = _adjacency.Values.Sum(neighbours => neighbours.Count);
            return directed / 2;
        }
    }

    public IReadOnlyCollection<string> Vertices => _adjacency.Keys;

    public bool IsOnline(string id) => _online.Contains(id);

    public void AddVertex(string id, bool online = true)
    {
        if (!_adjacency.ContainsKey(id))
        {
            _adjacency[id] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        if (online) _online.Add(id);
        else _online.Remove(id);
    }

    public void SetOnline(string id, bool online)
    {
        if (online) _online.Add(id);
        else _online.Remove(id);
    }

    // Adds an undirected edge. Recorded in both directions, because a radio
    // link works both ways.
    public void AddEdge(string a, string b, double weight)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return;

        AddVertex(a);
        AddVertex(b);

        _adjacency[a][b] = weight;
        _adjacency[b][a] = weight;
    }

    public IReadOnlyDictionary<string, double> NeighboursOf(string id) =>
        _adjacency.TryGetValue(id, out var neighbours)
            ? neighbours
            : new Dictionary<string, double>();

    public double WeightBetween(string a, string b) =>
        _adjacency.TryGetValue(a, out var neighbours) &&
        neighbours.TryGetValue(b, out var weight)
            ? weight
            : double.PositiveInfinity;

    public List<MeshEdge> AllEdges()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<MeshEdge>();

        foreach (var (from, neighbours) in _adjacency)
        {
            foreach (var (to, weight) in neighbours)
            {
                // Canonical key so each undirected edge is emitted once.
                var key = string.CompareOrdinal(from, to) < 0
                    ? $"{from}|{to}"
                    : $"{to}|{from}";

                if (!seen.Add(key)) continue;

                edges.Add(new MeshEdge(from, to, weight));
            }
        }

        return edges;
    }

    public void Clear()
    {
        _adjacency.Clear();
        _online.Clear();
    }

    // --------------------------------------------------------------- BFS

    // Breadth-first search from a starting vertex.
    //
    // BFS visits in order of hop count, so the first time it reaches a
    // vertex is by the fewest hops. That is what makes it the right tool
    // for "which devices can still reach the gateway, and how many hops
    // away are they" — a question about connectivity, not cost.
    //
    // Offline vertices are skipped, so the result reflects the mesh as it
    // currently stands rather than as it was designed.
    public TraversalResult BreadthFirst(string start, bool respectOutages = true)
    {
        var result = new TraversalResult { Algorithm = "Breadth-first search", Start = start };

        if (!_adjacency.ContainsKey(start)) return result;
        if (respectOutages && !_online.Contains(start)) return result;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);

        result.HopCounts[start] = 0;
        result.Order.Add(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var hops = result.HopCounts[current];

            foreach (var (neighbour, _) in _adjacency[current].OrderBy(n => n.Key))
            {
                result.EdgesExamined++;

                if (respectOutages && !_online.Contains(neighbour)) continue;
                if (!visited.Add(neighbour)) continue;

                result.HopCounts[neighbour] = hops + 1;
                result.Parents[neighbour] = current;
                result.Order.Add(neighbour);
                queue.Enqueue(neighbour);
            }
        }

        // Anything in the graph the traversal never reached is cut off.
        foreach (var vertex in _adjacency.Keys)
        {
            if (!visited.Contains(vertex)) result.Unreachable.Add(vertex);
        }

        return result;
    }

    // --------------------------------------------------------------- DFS

    // Depth-first search, following one branch as far as it goes before
    // backtracking. Written with an explicit stack rather than recursion so
    // a large mesh cannot exhaust the call stack.
    public TraversalResult DepthFirst(string start, bool respectOutages = true)
    {
        var result = new TraversalResult { Algorithm = "Depth-first search", Start = start };

        if (!_adjacency.ContainsKey(start)) return result;
        if (respectOutages && !_online.Contains(start)) return result;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current)) continue;

            result.Order.Add(current);

            // Reversed so the alphabetically first neighbour is explored
            // first, which keeps the traversal order reproducible.
            foreach (var (neighbour, _) in _adjacency[current].OrderByDescending(n => n.Key))
            {
                result.EdgesExamined++;

                if (respectOutages && !_online.Contains(neighbour)) continue;
                if (visited.Contains(neighbour)) continue;

                result.Parents.TryAdd(neighbour, current);
                stack.Push(neighbour);
            }
        }

        foreach (var vertex in _adjacency.Keys)
        {
            if (!visited.Contains(vertex)) result.Unreachable.Add(vertex);
        }

        return result;
    }

    // Reconstructs the route from a traversal's parent map, walking back
    // from the destination to the start.
    public static List<string> PathFrom(TraversalResult traversal, string destination)
    {
        var path = new List<string>();
        var current = destination;

        // Guard against a malformed parent map rather than looping forever.
        var guard = 0;

        while (current is not null && guard++ < 1000)
        {
            path.Add(current);
            if (string.Equals(current, traversal.Start, StringComparison.OrdinalIgnoreCase))
                break;

            current = traversal.Parents.TryGetValue(current, out var parent)
                ? parent
                : null;
        }

        path.Reverse();

        // If the walk did not end at the start, the destination was never
        // reached and the partial path is meaningless.
        return path.Count > 0 &&
               string.Equals(path[0], traversal.Start, StringComparison.OrdinalIgnoreCase)
            ? path
            : [];
    }

    // ---------------------------------------------------- Prim's MST

    // Prim's algorithm: grows a tree outward from a starting vertex, always
    // taking the cheapest edge that reaches somewhere new.
    //
    // The result spans every reachable vertex at the lowest total weight —
    // which here means the set of radio links that reaches the whole mesh
    // for the least transmission power.
    //
    // Prim's rather than Kruskal's because this graph is adjacency-based
    // and connected: Prim's works outward from the gateway, which matches
    // how a real mesh forms, and it needs no union-find structure.
    public SpanningTreeResult PrimsMinimumSpanningTree(
        string start, bool respectOutages = true)
    {
        var result = new SpanningTreeResult { Algorithm = "Prim's algorithm", Start = start };

        if (!_adjacency.ContainsKey(start)) return result;
        if (respectOutages && !_online.Contains(start)) return result;

        var inTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Cheapest known edge weight into each vertex not yet in the tree,
        // and which vertex that edge comes from. The heap cannot be queried
        // for an item's current priority, so this is tracked alongside it.
        var bestWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var bestFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var frontier = new MinHeap<string>(StringComparer.OrdinalIgnoreCase);

        bestWeight[start] = 0;
        frontier.Push(start, 0);

        while (frontier.TryPop(out var current, out var cost))
        {
            // Already absorbed via a cheaper edge.
            if (!inTree.Add(current)) continue;

            // Every vertex except the first arrives through an edge.
            if (bestFrom.TryGetValue(current, out var from))
            {
                result.Edges.Add(new MeshEdge(from, current, Math.Round(cost, 2)));
                result.TotalWeight += cost;
            }

            foreach (var (neighbour, weight) in _adjacency[current])
            {
                result.EdgesExamined++;

                if (respectOutages && !_online.Contains(neighbour)) continue;
                if (inTree.Contains(neighbour)) continue;

                // Only record this edge if it beats the best one found so
                // far for that neighbour.
                var known = bestWeight.TryGetValue(neighbour, out var existing)
                    ? existing
                    : double.PositiveInfinity;

                if (weight < known)
                {
                    bestWeight[neighbour] = weight;
                    bestFrom[neighbour] = current;
                    frontier.Push(neighbour, weight);
                }
            }
        }

        result.VerticesSpanned = inTree.Count;
        result.HeapComparisons = frontier.Comparisons;
        result.TotalWeight = Math.Round(result.TotalWeight, 2);

        // A spanning tree over v vertices has exactly v-1 edges. Fewer means
        // the graph is not fully connected from this start.
        result.IsComplete = result.Edges.Count == inTree.Count - 1 &&
                            inTree.Count == OnlineVertexCount(respectOutages);

        return result;
    }

    // The heap does not expose priorities by item, so the best-known weight
    // is reconstructed from the adjacency data.
    
    private int OnlineVertexCount(bool respectOutages) =>
        respectOutages ? _online.Count : _adjacency.Count;
}

// One weighted link between two nodes.
public sealed record MeshEdge(string From, string To, double Weight);

// What a traversal found.
public sealed class TraversalResult
{
    public string Algorithm { get; set; } = string.Empty;
    public string Start { get; set; } = string.Empty;

    // Vertices in the order they were first visited.
    public List<string> Order { get; set; } = [];

    // How each vertex was reached, for reconstructing routes.
    public Dictionary<string, string> Parents { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Hops from the start. BFS only — DFS order says nothing about distance.
    public Dictionary<string, int> HopCounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Vertices the traversal could not reach, which on a live mesh means
    // they have been cut off by an outage.
    public List<string> Unreachable { get; set; } = [];

    public int EdgesExamined { get; set; }
    public int Visited => Order.Count;
}

// What Prim's produced.
public sealed class SpanningTreeResult
{
    public string Algorithm { get; set; } = string.Empty;
    public string Start { get; set; } = string.Empty;
    public List<MeshEdge> Edges { get; set; } = [];
    public double TotalWeight { get; set; }
    public int VerticesSpanned { get; set; }
    public int EdgesExamined { get; set; }
    public long HeapComparisons { get; set; }

    // False when the mesh is fragmented and one tree cannot reach everything.
    public bool IsComplete { get; set; }
}