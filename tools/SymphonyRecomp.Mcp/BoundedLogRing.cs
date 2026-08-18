namespace SymphonyRecomp.Mcp;

internal sealed class BoundedLogRing(int capacity)
{
    private readonly Queue<string> _lines = new(capacity);
    private readonly object _gate = new();

    public void Add(string source, string line)
    {
        string value = $"[{source}] {line}";
        if (value.Length > 4096) value = value[..4096];
        lock (_gate)
        {
            _lines.Enqueue(value);
            while (_lines.Count > capacity) _lines.Dequeue();
        }
    }

    public string[] Snapshot(int maximum, Func<string, string> sanitize)
    {
        lock (_gate)
            return _lines.TakeLast(maximum).Select(sanitize).ToArray();
    }
}
