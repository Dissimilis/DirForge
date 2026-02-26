using DirForge.Models;

namespace DirForge.Services;

public sealed class InMemoryLogStore
{
    private readonly int _capacity;
    private readonly Queue<InMemoryLogEntry> _entries = new();
    private readonly object _sync = new();

    public InMemoryLogStore(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Add(InMemoryLogEntry entry)
    {
        lock (_sync)
        {
            if (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }
    }

    public IReadOnlyList<InMemoryLogEntry> GetLatest(int count)
    {
        lock (_sync)
        {
            if (count <= 0)
            {
                return [];
            }

            return _entries
                .Reverse()
                .Take(count)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }
}
