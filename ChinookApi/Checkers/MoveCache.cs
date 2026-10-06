namespace ChinookApi.Checkers;

public sealed class MoveCache
{
    readonly int _capacity;
    readonly long _ttlMs;
    readonly object _gate = new();
    readonly Dictionary<string, LinkedListNode<Entry>> _map = new();
    readonly LinkedList<Entry> _order = new();

    public MoveCache(int capacity, int ttlMinutes)
    {
        _capacity = Math.Max(1, capacity);
        _ttlMs = Math.Max(1, ttlMinutes) * 60_000L;
    }

    public bool TryGet(string key, out SuggestResponse? value)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node) || node.Value.Expires < Environment.TickCount64)
            {
                if (node != null)
                    Remove(node);
                value = null;
                return false;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(string key, SuggestResponse value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
                Remove(existing);
            var node = new LinkedListNode<Entry>(new Entry
            {
                Key = key,
                Value = value,
                Expires = Environment.TickCount64 + _ttlMs
            });
            _order.AddFirst(node);
            _map[key] = node;
            while (_map.Count > _capacity && _order.Last != null)
                Remove(_order.Last);
        }
    }

    void Remove(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        _map.Remove(node.Value.Key);
    }

    sealed class Entry
    {
        public string Key { get; set; } = "";
        public SuggestResponse Value { get; set; } = new();
        public long Expires { get; set; }
    }
}
