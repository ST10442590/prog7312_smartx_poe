using System.Collections;

namespace SmartX.Shared.Collections;

// A self-balancing binary search tree, keyed on TKey.
//
// Why self-balancing matters here specifically: telemetry arrives in
// ascending timestamp order. Inserting sorted data into a plain binary
// search tree produces a tree where every node has only a right child — a
// linked list wearing a tree's clothing, with O(n) lookups. The whole point
// of using a tree is lost on precisely the data this system produces.
//
// AVL fixes that by keeping every subtree's left and right heights within
// one of each other, rotating when an insert breaks the invariant. Height
// stays at O(log n), so lookups and range queries stay fast however much
// history accumulates.
public sealed class AvlTree<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : IComparable<TKey>
{
    private Node? _root;

    public int Count { get; private set; }

    // Height of the tree. Exposed because it is the evidence that balancing
    // is working: for n nodes this should stay near log2(n), not approach n.
    public int Height => HeightOf(_root);

    // Rotations performed since construction, for the same reason.
    public long Rotations { get; private set; }

    // Comparisons made by the last operation, so a query's cost is visible.
    public int LastOperationComparisons { get; private set; }

    private sealed class Node
    {
        public required TKey Key { get; init; }
        public TValue Value { get; set; }
        public Node? Left { get; set; }
        public Node? Right { get; set; }

        // Cached rather than computed on demand. Recomputing height during
        // every balance check would make each insert O(n).
        public int Height { get; set; } = 1;

        public Node(TValue value) => Value = value;
    }

    private static int HeightOf(Node? node) => node?.Height ?? 0;

    // Left height minus right height. Outside -1..1, the node needs rotating.
    private static int BalanceOf(Node? node) =>
        node is null ? 0 : HeightOf(node.Left) - HeightOf(node.Right);

    private static void Recompute(Node node) =>
        node.Height = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right));

    // ------------------------------------------------------------ insert

    // Inserts a key, or replaces the value if the key already exists.
    public void Insert(TKey key, TValue value)
    {
        LastOperationComparisons = 0;
        _root = InsertAt(_root, key, value);
    }

    private Node InsertAt(Node? node, TKey key, TValue value)
    {
        if (node is null)
        {
            Count++;
            return new Node(value) { Key = key };
        }

        LastOperationComparisons++;
        var comparison = key.CompareTo(node.Key);

        if (comparison < 0)
        {
            node.Left = InsertAt(node.Left, key, value);
        }
        else if (comparison > 0)
        {
            node.Right = InsertAt(node.Right, key, value);
        }
        else
        {
            // Same key: replace rather than duplicate.
            node.Value = value;
            return node;
        }

        Recompute(node);
        return Rebalance(node);
    }

    // ---------------------------------------------------------- rotation

    // Restores the AVL invariant at this node, if an insert broke it.
    //
    // Four cases. Left-left and right-right need one rotation. Left-right
    // and right-left need two: the inner child is rotated first to convert
    // the case into one of the simple ones.
    private Node Rebalance(Node node)
    {
        var balance = BalanceOf(node);

        // Left-heavy.
        if (balance > 1)
        {
            // Left-right: rotate the child left first.
            if (BalanceOf(node.Left) < 0)
            {
                node.Left = RotateLeft(node.Left!);
            }
            return RotateRight(node);
        }

        // Right-heavy. This is the case sorted input triggers constantly.
        if (balance < -1)
        {
            // Right-left: rotate the child right first.
            if (BalanceOf(node.Right) > 0)
            {
                node.Right = RotateRight(node.Right!);
            }
            return RotateLeft(node);
        }

        return node;
    }

    private Node RotateRight(Node pivot)
    {
        var newRoot = pivot.Left!;
        pivot.Left = newRoot.Right;
        newRoot.Right = pivot;

        // Order matters: the demoted node's height must be recomputed first,
        // because the promoted node's height depends on it.
        Recompute(pivot);
        Recompute(newRoot);

        Rotations++;
        return newRoot;
    }

    private Node RotateLeft(Node pivot)
    {
        var newRoot = pivot.Right!;
        pivot.Right = newRoot.Left;
        newRoot.Left = pivot;

        Recompute(pivot);
        Recompute(newRoot);

        Rotations++;
        return newRoot;
    }

    // ------------------------------------------------------------ lookup

    public bool TryGet(TKey key, out TValue value)
    {
        LastOperationComparisons = 0;
        var node = _root;

        while (node is not null)
        {
            LastOperationComparisons++;
            var comparison = key.CompareTo(node.Key);

            if (comparison == 0)
            {
                value = node.Value;
                return true;
            }

            node = comparison < 0 ? node.Left : node.Right;
        }

        value = default!;
        return false;
    }

    public bool Contains(TKey key) => TryGet(key, out _);

    // ------------------------------------------------------- range query

    // Every entry with a key in [from, to], in ascending key order.
    //
    // This is the operation the whole structure exists for: "all readings
    // between 14:00 and 16:00 for Node A". It does not visit the whole tree.
    // At each node it compares against the bounds and prunes the subtrees
    // that cannot contain anything in range, so the cost is proportional to
    // the height plus the number of matches — not to the size of the tree.
    public List<KeyValuePair<TKey, TValue>> Range(TKey from, TKey to)
    {
        LastOperationComparisons = 0;
        var results = new List<KeyValuePair<TKey, TValue>>();

        // Tolerate reversed bounds rather than silently returning nothing.
        if (from.CompareTo(to) > 0) (from, to) = (to, from);

        RangeFrom(_root, from, to, results);
        return results;
    }

    private void RangeFrom(
        Node? node, TKey from, TKey to, List<KeyValuePair<TKey, TValue>> results)
    {
        if (node is null) return;

        LastOperationComparisons++;
        var aboveLower = node.Key.CompareTo(from) >= 0;
        var belowUpper = node.Key.CompareTo(to) <= 0;

        // Only descend left if something there could be in range.
        if (aboveLower) RangeFrom(node.Left, from, to, results);

        if (aboveLower && belowUpper)
        {
            results.Add(new KeyValuePair<TKey, TValue>(node.Key, node.Value));
        }

        // Only descend right if something there could be in range.
        if (belowUpper) RangeFrom(node.Right, from, to, results);
    }

    // Counts matches without materialising them, for when only the size of
    // a range matters.
    public int CountInRange(TKey from, TKey to)
    {
        LastOperationComparisons = 0;
        if (from.CompareTo(to) > 0) (from, to) = (to, from);
        return CountFrom(_root, from, to);
    }

    private int CountFrom(Node? node, TKey from, TKey to)
    {
        if (node is null) return 0;

        LastOperationComparisons++;
        var aboveLower = node.Key.CompareTo(from) >= 0;
        var belowUpper = node.Key.CompareTo(to) <= 0;

        var total = aboveLower && belowUpper ? 1 : 0;
        if (aboveLower) total += CountFrom(node.Left, from, to);
        if (belowUpper) total += CountFrom(node.Right, from, to);

        return total;
    }

    // ------------------------------------------------------------ extent

    public bool TryMin(out TKey key, out TValue value)
    {
        if (_root is null)
        {
            key = default!;
            value = default!;
            return false;
        }

        var node = _root;
        while (node.Left is not null) node = node.Left;

        key = node.Key;
        value = node.Value;
        return true;
    }

    public bool TryMax(out TKey key, out TValue value)
    {
        if (_root is null)
        {
            key = default!;
            value = default!;
            return false;
        }

        var node = _root;
        while (node.Right is not null) node = node.Right;

        key = node.Key;
        value = node.Value;
        return true;
    }

    public void Clear()
    {
        _root = null;
        Count = 0;
        Rotations = 0;
    }

    // In-order traversal yields keys in ascending order, which is what makes
    // the tree useful for time-series data in the first place.
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        foreach (var entry in InOrder(_root)) yield return entry;
    }

    private static IEnumerable<KeyValuePair<TKey, TValue>> InOrder(Node? node)
    {
        if (node is null) yield break;

        foreach (var left in InOrder(node.Left)) yield return left;
        yield return new KeyValuePair<TKey, TValue>(node.Key, node.Value);
        foreach (var right in InOrder(node.Right)) yield return right;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}