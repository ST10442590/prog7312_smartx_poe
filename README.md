# Smart-X IoT Mesh Ecosystem — Final Portfolio of Evidence

A .NET 10 telemetry gateway for a hybrid IoT mesh, built across three
submissions. Part 1 delivered ingestion and anomaly scoring. Part 2 added
real-time command processing with priority alerts, undo and constant-time
lookups. This final submission adds the layer the first two could not
provide: knowledge of the network's own shape — which node reaches the
gateway, by what route, at what power cost, and what happens to everything
downstream when one relay dies.

**Module:** PROG7312 / AAPD7112 — Programming 3B / Advanced Application Development
**Student:** Thabang Thamae (ST10442590)
**Video demonstration:** https://youtu.be/OXOgq16L_vA

---

## Contents

- [Requirements](#requirements)
- [Quick start](#quick-start)
- [What this submission adds](#what-this-submission-adds)
- [Testing the final-submission structures](#testing-the-final-submission-structures)
- [Testing the Part 2 structures](#testing-the-part-2-structures)
- [Architectural analysis](#architectural-analysis)
  - [The AVL tree](#the-avl-tree-ordered-history-that-cannot-degrade)
  - [The binary min-heap](#the-binary-min-heap-and-why-not-priorityqueue)
  - [The mesh graph](#the-mesh-graph-adjacency-list-bfs-and-dfs)
  - [Prim's minimum spanning tree](#prims-minimum-spanning-tree)
  - [Failure simulation](#failure-simulation-what-the-structures-are-actually-for)
- [Solution structure](#solution-structure)
- [Navigation](#navigation)
- [API reference](#api-reference)
- [Troubleshooting](#troubleshooting)
- [References](#references)

---

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 10.0 or later |
| Browser | Any modern browser with WebAssembly support |

Check your SDK:

```bash
dotnet --list-sdks
```

If no `10.x` entry appears, install the .NET 10 SDK from
<https://dotnet.microsoft.com/download>. No database, container runtime or
other external dependency is needed — the mesh is simulated in process.

---

## Quick start

```bash
git clone https://github.com/ST10442590/prog7312_smartx_poe.git
cd prog7312_smartx_poe
dotnet restore
dotnet build
```

The API and the client are **separate processes** and both must be running.
Open two terminals.

**Terminal 1 — the gateway API:**

```bash
dotnet run --project SmartX.Api
```

Listens on `http://localhost:5145`. On startup it registers 24 simulated
ESP32 nodes, begins publishing telemetry every 800 ms, and two seconds later
builds the mesh graph from the registered nodes.

**Terminal 2 — the dashboard client:**

```bash
dotnet run --project SmartX.Client
```

Listens on `http://localhost:5006` and opens a browser automatically.

Stop either with `Ctrl+C`.

> **Start the API first.** The client polls the gateway and will show
> "Gateway unreachable" until the API answers. It reconnects on its own once
> the API is up — no client restart needed.

> **Stop the API before rebuilding.** A running process holds a lock on
> `SmartX.Api.exe`, and the build cannot overwrite it. On Windows:
> `taskkill /IM SmartX.Api.exe /F`.

> **Give it thirty seconds.** The AVL history trees and the anomaly baselines
> are built from live telemetry. Immediately after startup each tree holds
> only a handful of readings, so the pruning and balancing figures below will
> look unremarkable. They become meaningful once a few hundred readings per
> device have accumulated.

---

## What this submission adds

| Structure | Where it lives | What it solves |
|---|---|---|
| AVL tree | `SmartX.Shared/Collections/AvlTree.cs` | Per-device telemetry history, queryable by time range without a full scan |
| Binary min-heap | `SmartX.Shared/Collections/MinHeap.cs` | The frontier for Prim's algorithm, with decrease-key |
| Weighted graph | `SmartX.Shared/Collections/MeshGraph.cs` | The mesh itself: adjacency list, BFS, DFS, Prim's MST |
| Outage-aware routing | `SmartX.Api/Services/MeshNetworkService.cs` | Topology construction, route tracing, failure simulation |
| Mesh dashboard | `SmartX.Client/Pages/Topology.razor` | SVG mesh map, route tracing, history queries, failure injection |

Everything in Parts 1 and 2 is still present and still running — the new
module sits alongside them rather than replacing them.

---

## Testing the final-submission structures

All of this is reachable from the **Mesh Topology** page at
`http://localhost:5006/topology`, and from the API directly. Let the API run
for at least a minute first so the trees have data.

### AVL tree — ordered history and pruned range queries

Per-device statistics, including each tree's height and how many rotations it
has performed:

```bash
curl http://localhost:5145/api/history
```

A representative response after a minute of telemetry:

```json
{
  "deviceCount": 24,
  "totalReadings": 946,
  "totalRotations": 806,
  "tallestTree": 6
}
```

946 readings across 24 trees, and the tallest tree is **6 levels deep**. That
number is the whole point of the structure and is explained under
[the AVL tree](#the-avl-tree-ordered-history-that-cannot-degrade) below.

A range query over one device's history. The convenience form asks for the
last N minutes, which is what the dashboard uses:

```bash
curl "http://localhost:5145/api/history/ESP32-A00/recent?minutes=2&take=200"
```

The general form takes explicit timestamps, and defaults to the last ten
minutes when both are omitted:

```bash
curl "http://localhost:5145/api/history/ESP32-A00"
curl "http://localhost:5145/api/history/ESP32-A00?from=2026-11-13T07:57:00Z&to=2026-11-13T07:59:00Z&take=500"
```

Either form reports `matchCount`, `treeSize`, `treeHeight`,
`unbalancedHeight` and `comparisons` alongside the readings themselves, so
the cost of the query is visible, not asserted.

The comparison endpoint runs the same window twice — once through the tree
and once as a linear scan — and returns both costs. Its defaults already
describe a narrow ten-second window, so it needs no parameters:

```bash
curl "http://localhost:5145/api/history/ESP32-A00/compare"
curl "http://localhost:5145/api/history/ESP32-A00/compare?fromSecondsAgo=10&toSecondsAgo=0"
```

Measured on a tree holding 399 readings, querying a 10-second window out of
roughly 5 minutes of history:

| Strategy | Comparisons | Matches returned |
|---|---|---|
| AVL range query | 20 | 12 |
| Linear scan | 399 | 12 |

Identical results, **95% fewer comparisons**. Use a narrow window: if the
window spans the entire tree there is nothing to prune, and both strategies
correctly report the same cost.

### Graph — the mesh, BFS and DFS

The graph itself, as the dashboard draws it:

```bash
curl http://localhost:5145/api/mesh/graph
```

25 vertices (24 sensors plus `GATEWAY`) and 71 weighted links. Reachability
from the gateway, computed by breadth-first search:

```bash
curl http://localhost:5145/api/mesh/reachability
```

A traversal from any starting vertex, showing both orders side by side:

```bash
curl http://localhost:5145/api/mesh/traverse/GATEWAY
```

BFS yields nodes in order of hop distance; DFS plunges down one branch
first. The same structure, two different questions — see
[the mesh graph](#the-mesh-graph-adjacency-list-bfs-and-dfs).

The shortest hop path from one node to the gateway:

```bash
curl http://localhost:5145/api/mesh/route/ESP32-X23
```

### Minimum spanning tree — the power-efficient backbone

```bash
curl http://localhost:5145/api/mesh/mst
```

Measured output:

| Measure | Value |
|---|---|
| Vertices spanned | 25 |
| Edges in the tree | 24 |
| Total weight | 186.4 |
| Weight of all 71 links | 733.1 |
| Saving | **74.6%** |
| `isComplete` | `true` |

A spanning tree over *n* vertices always has exactly *n − 1* edges, so 25
vertices and 24 edges is the structural proof that the result is a tree and
that nothing was left out. `isComplete: true` confirms every vertex was
reached.

### Failure simulation — all three structures at once

Take a relay offline and watch the mesh re-route around it:

```bash
curl -X POST http://localhost:5145/api/mesh/simulate-failure/ESP32-S18
```

`ESP32-S18` is a good target: it carries the cheapest gateway uplink in the
topology (weight 12.6) and has a degree of 7. Measured result:

| Measure | Before | After |
|---|---|---|
| MST weight | 186.4 | 194.6 |
| Devices re-routed | — | 4 |
| New next hop for those devices | ESP32-S18 | ESP32-V21 |
| `meshHealed` | — | `true` |
| Recompute time | — | 0.214 ms |

The mesh stays connected, the backbone costs 8.2 more to maintain, and four
nodes change parent. That is the answer the module exists to produce, and it
takes a BFS, a Prim's run and a heap to produce it.

---

## Testing the Part 2 structures

Still fully functional, from the **Command Stream** page at
`http://localhost:5006/stream` or from the API.

### Priority queue — a critical alert bypassing the stream

```bash
curl -X POST "http://localhost:5145/api/stream/simulate-alert?deviceId=ESP32-B01&severity=Informational"
curl -X POST "http://localhost:5145/api/stream/simulate-alert?deviceId=ESP32-A00&severity=Critical"
curl http://localhost:5145/api/stream/snapshot
```

`pendingAlerts` lists them in dequeue order: the Critical alert is first even
though it arrived second.

### HashSet — duplicate alert suppression

```bash
curl -X POST "http://localhost:5145/api/stream/simulate-alert?deviceId=ESP32-A00&severity=Critical"
curl -X POST "http://localhost:5145/api/stream/simulate-alert?deviceId=ESP32-A00&severity=Critical"
```

The first returns `"queued": true`; the second returns
`"suppressedAsDuplicate": true`.

### Stack — undoing manual overrides

```bash
curl -X POST http://localhost:5145/api/stream/command \
  -H "Content-Type: application/json" \
  -d "{\"deviceId\":\"ESP32-F05\",\"kind\":0,\"newState\":true}"

curl -X POST http://localhost:5145/api/stream/undo
```

Undo applies the *inverse* of the popped command rather than deleting
history, so the audit trail stays intact.

### Dictionary and SortedDictionary — lookups and ordered timeline

```bash
curl http://localhost:5145/api/index/by-mac/5C:CF:7F:00:00:00
curl http://localhost:5145/api/index/by-category/PowerConsumption
curl "http://localhost:5145/api/index/timeline?take=20"
curl http://localhost:5145/api/index/stats
```

### Predictive engine — teaching it a pattern

```bash
curl "http://localhost:5145/api/suggestions/search?query=ESP32-A00"
curl "http://localhost:5145/api/suggestions/search?query=ESP32-B01"
curl "http://localhost:5145/api/suggestions/search?query=ESP32-A00"
curl "http://localhost:5145/api/suggestions/search?query=ESP32-B01"
curl "http://localhost:5145/api/suggestions/search?query=ESP32-A00"
curl http://localhost:5145/api/suggestions
```

A suggestion for ESP32-B01 appears with its rationale. The model is
in-memory, so restarting the API clears it.

---

## Architectural analysis

### The AVL tree: ordered history that cannot degrade

Telemetry history has to answer one question well: *what did this device
report between time A and time B?* The obvious structure is a binary search
tree keyed on timestamp — ordered, and `O(log n)` to search.

Except that a plain BST is the **worst possible** structure for this data,
for a reason specific to telemetry: readings arrive in ascending timestamp
order. Every new key is larger than every key already stored, so every
insertion goes down the right spine. After *n* insertions the tree is a
linked list of height *n*, and the search that was supposed to cost
`O(log n)` costs `O(n)`.

This is not a theoretical worry. With 399 readings, a plain BST built from
this data has height 399. The API reports exactly that figure next to the
real one, so the degradation being avoided is visible rather than claimed:

```json
{
  "treeSize": 399,
  "treeHeight": 9,
  "unbalancedHeight": 399
}
```

Height 9 against a theoretical worst case of 399. The AVL invariant — the
heights of any node's two subtrees differ by at most one — is what produces
it. Each insertion walks back up the path it came down, recomputes heights,
and rotates wherever the balance factor reaches ±2. Because the insertions
are monotonic, almost every one triggers a rotation: the `/api/history`
endpoint reports **806 rotations across 946 insertions**. That ratio is not a
defect; it is the structure doing continuously what it was chosen to do. An
AVL tree with *n* nodes is guaranteed a height below `1.44 · log₂(n)`, so
height grows logarithmically no matter how adversarially ordered the input
is.

The tree exposes `Height`, `Rotations` and `LastOperationComparisons` as
public properties for exactly this reason — the dashboard and the API can
report the structure's internal state as evidence rather than asking the
reader to take the complexity claim on trust.

**Where the balancing pays off: the pruned range query.**

A range query is not a point search, so balance alone is not the whole story.
The range query exploits the *ordering* invariant as well: at any node, if
the node's key is already below the range's lower bound, every key in its
left subtree is below it too, and that entire subtree can be skipped
unvisited.

```csharp
private void RangeFrom(
    Node? node, TKey from, TKey to, List<KeyValuePair<TKey, TValue>> results)
{
    if (node is null) return;

    LastOperationComparisons++;

    var aboveLower = node.Key.CompareTo(from) >= 0;
    var belowUpper = node.Key.CompareTo(to) <= 0;

    // Only descend left if something to the left could still be in range.
    if (aboveLower) RangeFrom(node.Left, from, to, results);

    if (aboveLower && belowUpper)
        results.Add(new KeyValuePair<TKey, TValue>(node.Key, node.Value));

    // Only descend right if something to the right could still be in range.
    if (belowUpper) RangeFrom(node.Right, from, to, results);
}
```

The two `if` guards are the pruning. Each costs one comparison and can save
an entire subtree. The counter increments once per visited node, so the
returned `comparisons` figure is the true number of nodes touched, not an
estimate.

**How this scales.** A range query costs `O(log n + k)`: the descent to the
range boundary, plus one visit per matching key. A linear scan costs `O(n)`
regardless of how few readings match. The measured figures:

| Tree size | Window | Tree comparisons | Linear comparisons | Matches |
|---|---|---|---|---|
| 399 | 10 s of ~5 min | 20 | 399 | 12 |

Twenty comparisons to find twelve readings — roughly `log₂(399) ≈ 9` for the
descent plus the matches themselves. The gap widens as history grows: at
4,000 readings the scan costs ten times more while the tree query adds
roughly three comparisons. That is the difference between a dashboard that
stays responsive as the deployment ages and one that slows down linearly with
uptime.

**A detail worth noting: duplicate keys.** Two readings from the same device
can carry identical timestamps when the simulator fires twice inside one
tick. A BST cannot hold duplicate keys, and dropping a reading would be data
loss. `TelemetryHistory` resolves this by nudging the colliding key forward
by a tick until it is unique — the reading is kept, the ordering invariant
holds, and the displacement is far below the resolution anyone queries at.

### The binary min-heap, and why not `PriorityQueue`

Part 2 uses `PriorityQueue<TElement, TPriority>` for the alert queue, and
that was the right choice there. Prim's algorithm needs something the BCL
type cannot do.

Prim's grows a tree one vertex at a time, always taking the cheapest edge
that leaves the tree. When a new vertex joins, the cost of reaching some of
the vertices still outside may *drop*. The frontier therefore needs
**decrease-key**: lower an item's priority in place. `PriorityQueue<,>` has
no such operation — the usual workaround is to push the vertex again at its
new priority and discard stale pops, which inflates the heap beyond `V`
entries.

`MinHeap<TItem, TPriority>` instead maintains a position index alongside the
array:

```csharp
private readonly List<(TItem Item, TPriority Priority)> _nodes = new();
private readonly Dictionary<TItem, int> _positions;
```

`_positions` maps each item to its current index in the heap array. `Push` on
an item already present is therefore a decrease-key rather than a duplicate
insert: look up the index in `O(1)`, overwrite the priority, and sift up in
`O(log n)`. Without the index, finding the item would mean scanning the array
— `O(n)` — and the heap operation that is supposed to be logarithmic would
become linear. Every swap during sift-up and sift-down updates both entries
in the dictionary, which is what keeps the index honest.

**How this scales.** The heap holds at most `V` entries for the whole run, so
Prim's is `O(E log V)`. With duplicate-push instead, the heap can hold up to
`E` entries. On this mesh that is 71 against 25 — tolerable. On the thousands
of nodes the brief describes, with each node linked to several neighbours, it
is the difference between a heap sized by the node count and one sized by the
link count.

### The mesh graph: adjacency list, BFS and DFS

```csharp
private readonly Dictionary<string, Dictionary<string, double>> _adjacency;
private readonly HashSet<string> _online;
```

**Why an adjacency list and not a matrix.** A 25×25 adjacency matrix is 625
cells for 71 links — about 11% occupied. Mesh networks are sparse by nature:
a node has a handful of radio neighbours, not hundreds, so `E` grows roughly
linearly with `V` rather than quadratically. A matrix costs `O(V²)` memory
regardless, and iterating one node's neighbours means walking an entire row
of 25 cells to find 5 real links. The list costs `O(V + E)` and iterating a
node's neighbours touches only its actual neighbours. At 1,000 nodes the
matrix would need a million cells to store a few thousand links.

The inner dictionary is keyed by neighbour id, so "is A adjacent to B, and at
what cost?" is a double hash lookup — `O(1)` — not a scan of A's neighbour
list. That matters because Prim's asks exactly that question once per edge
examined.

**Why a separate `_online` set.** When a node fails it is not removed from
the graph — the topology is still the topology; one radio has simply stopped
answering. Every traversal consults `_online` before stepping onto a vertex.
That keeps failure simulation non-destructive: take a node offline, re-run
the traversals, bring it back, and the graph is unchanged. Membership tests
run on every neighbour of every visited node, which is why it is a `HashSet`
rather than a list.

**BFS and DFS answer different questions.** Both visit every reachable
vertex in `O(V + E)`, and the only structural difference is which collection
holds the pending work.

Breadth-first uses a **queue**, so vertices come out in order of hop
distance. That is what makes it the right tool for routing: the first time
BFS reaches a vertex, it has reached it by the fewest possible hops, so
recording each vertex's discovering parent yields the shortest hop path for
free. `PathFrom` then walks those parent links backwards from the target to
the gateway.

Depth-first uses a **stack**, so it plunges down one branch to its end before
backtracking. It answers connectivity and reachability questions — is this
subtree isolated, what is downstream of this relay — rather than distance
ones. It is implemented with an explicit `Stack<string>` rather than
recursion, deliberately: recursion depth on a graph is bounded by the number
of vertices, and a 5,000-node mesh would risk a stack overflow on a path that
an explicit stack handles in heap memory without difficulty.

Both traversals need a visited set. On a mesh — which, unlike a tree, has
cycles everywhere by design, because redundant links are the entire point of
a mesh — without one, a traversal does not terminate.

### Prim's minimum spanning tree

The question the MST answers is a real operational one. Every active radio
link costs power to maintain. The mesh has 71 links; keeping all of them hot
costs 733.1 units. But connectivity only requires a spanning tree — a subset
of links that touches every node with no cycles. The *minimum* spanning tree
is the cheapest such subset.

Prim's builds it greedily: start anywhere, then repeatedly add the cheapest
edge joining a vertex inside the tree to one outside.

```csharp
var bestWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
var bestFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

// ... inside the relaxation loop, for each neighbour of the vertex just added:

var known = bestWeight.TryGetValue(neighbour, out var existing)
    ? existing : double.PositiveInfinity;

if (weight < known)
{
    bestWeight[neighbour] = weight;
    bestFrom[neighbour] = current;
    frontier.Push(neighbour, weight);
}
```

The two dictionaries are the part worth explaining. `bestWeight` tracks the
cheapest known cost of attaching each outside vertex to the growing tree, and
`bestFrom` remembers which inside vertex offers it. They are maintained
*outside* the heap rather than being read back out of it, because a heap
supports "give me the minimum" and nothing else — there is no way to ask a
heap what priority it currently holds for an arbitrary item. Keeping the
answer in a dictionary makes the comparison an `O(1)` lookup, and the `Push`
that follows is a decrease-key rather than a duplicate insert precisely
because the heap maintains its own position index.

Getting this wrong is easy and silent. An earlier draft of this method
returned infinity for the current-best lookup unconditionally, which made
every relaxation look like an improvement: the algorithm still terminated,
still produced a spanning tree, and still reported a plausible-looking
weight — it was simply not the *minimum* one. The structural check is what
catches that class of error: a correct run over 25 vertices must produce
exactly 24 edges and report `isComplete: true`.

**How this scales.** Each vertex enters and leaves the heap once —
`V` operations at `O(log V)` each. Each edge is examined once from each end
and may trigger a decrease-key — `O(E log V)`. Total: `O(E log V)`. The
measured run over 25 vertices and 71 edges completes in well under a
millisecond; on a sparse mesh where `E ≈ c · V`, the cost grows as
`O(V log V)`, so a 1,000-node mesh is roughly 60 times the work of this
one, not 1,600 times. The alternative — enumerating spanning trees and
comparing them — is combinatorial and hopeless at any size.

**What it buys:** 186.4 against 733.1, a **74.6% reduction** in link
maintenance cost, while every node stays reachable.

### Failure simulation: what the structures are actually for

The failure endpoint is where the three structures stop being an exercise and
start answering a question an operator would ask: *if this relay dies, what
breaks?*

The sequence is: snapshot every node's hop count and next hop via BFS from
the gateway; compute the MST; mark the target offline; re-run both; compare.

The comparison is subtler than it looks. The first implementation compared
hop counts only, and reported zero re-routed devices on a failure that had
visibly changed the mesh. The reason is that a redundant mesh usually has an
*equal-length* alternative route — the node is still three hops from the
gateway, but via a completely different relay. Hop count alone cannot see
that. The fix compares the next hop too:

```csharp
var hopsChanged = hopsBefore != hopsAfter;
var nextHopChanged = !string.Equals(
    parentBefore, parentAfter, StringComparison.OrdinalIgnoreCase);

if (!hopsChanged && !nextHopChanged) continue;

rerouted.Add(new ReroutedNode(
    vertex, hopsBefore, hopsAfter, parentBefore ?? "—", parentAfter ?? "—"));
```

With that in place, failing `ESP32-S18` reports four nodes switching their
uplink from S18 to `ESP32-V21`, the MST cost rising from 186.4 to 194.6, and
`meshHealed: true` — the mesh survived, at a measurable price. The whole
recomputation takes 0.214 ms, which is what makes this viable as an
interactive dashboard feature rather than a batch report.

**A note on the topology itself.** The mesh is **synthetic**, and the code
says so. Real link costs would come from RSSI readings the simulated ESP32
nodes do not produce. `MeshNetworkService.LinkCost()` derives each weight
deterministically from the two device ids, so the same topology is rebuilt
identically on every run — which is what makes the figures in this README
reproducible rather than a one-off sample. The structure mirrors a real
deployment: full mesh within each sub-zone at low cost, one relay per
sub-zone uplinking to the gateway at higher cost, a redundant second uplink,
and cross-zone links. The algorithms are production-shaped; only the weights
are stand-ins.

---

## Solution structure

```
SmartX/
├── SmartX.Shared/     Class library referenced by both other projects
│   ├── Telemetry/     TelemetryPacket<T>, SensorReading, health windows, DTOs
│   ├── Commands/      Commands, alerts, suggestion and stream DTOs
│   ├── Sensors/       Registration profiles, categories, units, health states
│   ├── Collections/   RingBuffer<T>, jagged batch buffer, AvlTree<,>,
│   │                  MinHeap<,>, MeshGraph
│   └── Topology/      Deployment tree and recursive validator
├── SmartX.Api/        Minimal API gateway
│   └── Services/      Registry, simulator, archive, command stream, lookup
│                      index, suggestion engine, topology builder,
│                      telemetry history, mesh network service
└── SmartX.Client/     Blazor WebAssembly console
    ├── Components/    PulseTile
    ├── Pages/         Dashboard, registry, diagnostics, command stream,
    │                  mesh topology
    └── Services/      Typed API client
```

The four data structures written by hand — `RingBuffer<T>`, `AvlTree<,>`,
`MinHeap<,>` and `MeshGraph` — all live in `SmartX.Shared/Collections`, not
in the API. They are general-purpose: nothing in `AvlTree` knows about
telemetry, and nothing in `MeshGraph` knows about ESP32 nodes. Domain types
live in `SmartX.Shared` too, so the same validation logic runs in the browser
and on the server: the registration errors a user sees while typing are
produced by the identical method the API runs on submission.

---

## Navigation

All five modules are active.

| Module | Route | Part |
|---|---|---|
| Sensor Data Ingestion | `/` | 1 |
| Sensor Registry | `/devices` | 1 |
| Pipeline Diagnostics | `/diagnostics` | 1 |
| Real-Time Command Stream | `/stream` | 2 |
| Network Topology and Mesh Routing | `/topology` | Final |

Routing is client-side, so switching modules does not reload the application
or interrupt the polling loops. Each page disposes its own timer on
navigation, which is why moving between views does not leave orphaned
requests running against the API.

---

## API reference

### Final submission — mesh topology and history

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/mesh/graph` | Vertices, weighted links and online state |
| `GET` | `/api/mesh/reachability` | BFS reachability from the gateway |
| `GET` | `/api/mesh/traverse/{start}` | BFS and DFS orders from one vertex |
| `GET` | `/api/mesh/route/{deviceId}` | Shortest hop path to the gateway |
| `GET` | `/api/mesh/mst` | Prim's minimum spanning tree and its saving |
| `POST` | `/api/mesh/simulate-failure/{deviceId}` | Take a node offline and report the re-route |
| `GET` | `/api/history` | Per-device tree sizes, heights and rotations |
| `GET` | `/api/history/{deviceId}` | AVL range query — `from`, `to`, `take` |
| `GET` | `/api/history/{deviceId}/recent` | Last N minutes — `minutes`, `take` |
| `GET` | `/api/history/{deviceId}/compare` | Tree query vs linear scan — `fromSecondsAgo`, `toSecondsAgo` |

### Part 2 — command stream, lookups, suggestions

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/stream/snapshot` | Depths and contents of all four structures |
| `POST` | `/api/stream/command` | Issue a manual override |
| `POST` | `/api/stream/undo` | Revert the most recent command |
| `POST` | `/api/stream/simulate-alert` | Inject a test alert |
| `GET` | `/api/index/stats` | Index and timeline counters |
| `GET` | `/api/index/by-mac/{mac}` | O(1) MAC resolution |
| `GET` | `/api/index/by-category/{category}` | Category index |
| `GET` | `/api/index/timeline` | Ordered timeline |
| `GET` | `/api/index/timeline/range` | Range query |
| `GET` | `/api/suggestions` | Recommendations and the learned model |
| `GET` | `/api/suggestions/search` | Device search, also a training signal |
| `POST` | `/api/suggestions/attend/{deviceId}` | Dismiss a suggestion |
| `POST` | `/api/suggestions/reset` | Clear the learned model |

### Part 1 — ingestion, devices, diagnostics

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/health` | Liveness probe |
| `POST` | `/api/telemetry` | Ingest one packet |
| `POST` | `/api/telemetry/batch` | Ingest a buffered batch |
| `GET` | `/api/devices` | List registered sensors |
| `POST` | `/api/devices` | Register or update a sensor |
| `POST` | `/api/devices/{id}/attachments` | Upload a config file, photo or log |
| `GET` | `/api/mesh/health` | Pulse Grid tiles, worst first |
| `GET` | `/api/mesh/summary` | Mesh-wide counters |
| `GET` | `/api/topology/validate` | Recursive validation report |
| `GET` | `/api/diagnostics/benchmark` | Insertion and search timings |

OpenAPI document (development only): `http://localhost:5145/openapi/v1.json`

---

## Troubleshooting

**"Gateway unreachable" on the dashboard**
The API is not running, or is on a different port. Confirm the API terminal
shows `Now listening on: http://localhost:5145`.

**Build fails with "file is locked by SmartX.Api"**
The API is still running. Stop it with `Ctrl+C`, or
`taskkill /IM SmartX.Api.exe /F`.

**The mesh topology page shows no graph**
The graph is built two seconds after host start, from the registered device
list. Give the API a moment, then reload. If it stays empty, check
`/api/mesh/graph` directly — an empty `nodes` array means the registry was
empty when the build ran.

**Range query reports the same comparison count for both strategies**
The query window spans the whole tree, so there is nothing to prune — the
tree correctly visits every node. Narrow the window
(`fromSecondsAgo=10&toSecondsAgo=0`) and the gap appears.

**Tree heights and rotations look trivially small**
The trees are built from live telemetry. Let the API run for a minute or two.

**Failure simulation reports zero re-routed devices**
Pick a node that actually carries traffic — a sub-zone relay such as
`ESP32-S18`, `ESP32-G06`, `ESP32-A00` or `ESP32-M12`. A leaf node with a
single link has no downstream dependents to re-route.

**CORS errors in the browser console**
The API allows only `http://localhost:5006` and `https://localhost:7296`. If
your client started on a different port, update `WithOrigins` in
`SmartX.Api/Program.cs`.

**Port already in use**
Change `applicationUrl` in the relevant `Properties/launchSettings.json`, and
update the CORS origins if it was the client that moved.

---

## References

Ancker, J.S., Edwards, A., Nosal, S., Hauser, D., Mauer, E. & Kaushal, R.
2017. Effects of workload, work complexity, and repeated alerts on alert
fatigue in a clinical decision support system. *BMC Medical Informatics and
Decision Making*, 17(36):1–9.

Cormen, T.H., Leiserson, C.E., Rivest, R.L. & Stein, C. 2022. *Introduction
to algorithms*. 4th ed. Cambridge, MA: MIT Press.

Endsley, M.R. 1995. Toward a theory of situation awareness in dynamic
systems. *Human Factors*, 37(1):32–64.

Healey, C.G. & Enns, J.T. 2012. Attention and visual memory in visualization
and computer graphics. *IEEE Transactions on Visualization and Computer
Graphics*, 18(7):1170–1188.

Koivisto, J. & Hamari, J. 2019. The rise of motivational information systems:
a review of gamification research. *International Journal of Information
Management*, 45:191–210.

Sedgewick, R. & Wayne, K. 2011. *Algorithms*. 4th ed. Upper Saddle River, NJ:
Addison-Wesley.

Stelea, G.A., Sangeorzan, L. & Enache-David, N. 2025. Accessible IoT
dashboard design with AI-enhanced descriptions for visually impaired users.
*Future Internet*, 17(7):274.

Zong, J., Lee, C., Lundgard, A., Jang, J., Hajas, D. & Satyanarayan, A. 2022.
Rich screen reader experiences for accessible data visualization. *Computer
Graphics Forum*, 41(3):15–27.
