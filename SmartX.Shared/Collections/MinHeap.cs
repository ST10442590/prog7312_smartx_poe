namespace SmartX.Shared.Collections;

// A binary min-heap: the smallest priority is always at the root.
//
// Written by hand rather than using PriorityQueue<,> because Prim's
// algorithm needs one operation the built-in type does not offer —
// decreasing the priority of an item already in the heap. Without it, the
// usual workaround is to push duplicates and skip stale ones on pop, which
// bloats the heap to O(E) entries instead of O(V).
//
// The structure is an array, not linked nodes. For a node at index i:
//   parent     (i - 1) / 2
//   children   2i + 1 and 2i + 2
// That arithmetic is why heaps are fast: no pointer chasing, and the whole
// thing sits in one contiguous block.
public sealed class MinHeap<TItem> where TItem : notnull
{
    private readonly List<(TItem Item, double Priority)> _entries = [];

    // Where each item currently sits in the array, so DecreaseKey can find
    // it without scanning. This is what makes the decrease operation
    // O(log n) rather than O(n).
    private readonly Dictionary<TItem, int> _positions;

    public MinHeap(IEqualityComparer<TItem>? comparer = null) =>
        _positions = new Dictionary<TItem, int>(comparer);

    public int Count => _entries.Count;
    public bool IsEmpty => _entries.Count == 0;

    // Comparisons made since construction, so the cost is measurable.
    public long Comparisons { get; private set; }

    public bool Contains(TItem item) => _positions.ContainsKey(item);

    // Adds an item, or lowers its priority if it is already present and the
    // new priority is better.
    public void Push(TItem item, double priority)
    {
        if (_positions.TryGetValue(item, out var existing))
        {
            if (priority < _entries[existing].Priority)
            {
                _entries[existing] = (item, priority);
                SiftUp(existing);
            }
            return;
        }

        _entries.Add((item, priority));
        _positions[item] = _entries.Count - 1;
        SiftUp(_entries.Count - 1);
    }

    // Removes and returns the item with the lowest priority.
    public bool TryPop(out TItem item, out double priority)
    {
        if (_entries.Count == 0)
        {
            item = default!;
            priority = 0;
            return false;
        }

        (item, priority) = _entries[0];
        _positions.Remove(item);

        var last = _entries.Count - 1;

        if (last == 0)
        {
            _entries.RemoveAt(0);
            return true;
        }

        // Move the last entry to the root and sink it, rather than shifting
        // everything down.
        _entries[0] = _entries[last];
        _positions[_entries[0].Item] = 0;
        _entries.RemoveAt(last);

        SiftDown(0);
        return true;
    }

    public bool TryPeek(out TItem item, out double priority)
    {
        if (_entries.Count == 0)
        {
            item = default!;
            priority = 0;
            return false;
        }

        (item, priority) = _entries[0];
        return true;
    }

    // Moves an entry up until its parent is no larger than it.
    private void SiftUp(int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;

            Comparisons++;
            if (_entries[parent].Priority <= _entries[index].Priority) break;

            Swap(parent, index);
            index = parent;
        }
    }

    // Moves an entry down until both children are no smaller than it.
    private void SiftDown(int index)
    {
        var count = _entries.Count;

        while (true)
        {
            var left = 2 * index + 1;
            var right = 2 * index + 2;
            var smallest = index;

            if (left < count)
            {
                Comparisons++;
                if (_entries[left].Priority < _entries[smallest].Priority)
                    smallest = left;
            }

            if (right < count)
            {
                Comparisons++;
                if (_entries[right].Priority < _entries[smallest].Priority)
                    smallest = right;
            }

            if (smallest == index) break;

            Swap(index, smallest);
            index = smallest;
        }
    }

    private void Swap(int a, int b)
    {
        (_entries[a], _entries[b]) = (_entries[b], _entries[a]);
        _positions[_entries[a].Item] = a;
        _positions[_entries[b].Item] = b;
    }

    public void Clear()
    {
        _entries.Clear();
        _positions.Clear();
        Comparisons = 0;
    }
}