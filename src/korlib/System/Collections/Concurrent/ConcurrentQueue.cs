// NeutrinoOS korlib - System.Collections.Concurrent.ConcurrentQueue<T>
//
// Minimal FIFO queue so packages built against the BCL shape (M8:
// Microsoft.Extensions.ObjectPool keeps overflow items in a
// ConcurrentQueue<T>) resolve and run on the device. Managed threads on
// NeutrinoOS run cooperatively - a method body cannot be preempted - so
// the head/tail bookkeeping below is race-free without CAS loops; the
// type keeps the ConcurrentQueue name and member shapes for source
// compatibility.

namespace System.Collections.Concurrent;

/// <summary>A thread-cooperative concurrent queue (see file header).</summary>
public class ConcurrentQueue<T>
{
    private T[] _items;
    private int _head;
    private int _tail;
    private int _count;

    /// <summary>Creates an empty queue.</summary>
    public ConcurrentQueue()
    {
        _items = new T[8];
    }

    /// <summary>Number of items currently queued.</summary>
    public int Count => _count;

    /// <summary>True when no items are queued.</summary>
    public bool IsEmpty => _count == 0;

    /// <summary>Append an item (grows the storage when full).</summary>
    public void Enqueue(T item)
    {
        if (_count == _items.Length)
            Grow();
        _items[_tail] = item;
        _tail = _tail + 1 == _items.Length ? 0 : _tail + 1;
        _count++;
    }

    /// <summary>Take the oldest item; false (and default) when empty.</summary>
    public bool TryDequeue(out T result)
    {
        if (_count == 0)
        {
            result = default!;
            return false;
        }
        result = _items[_head];
        _items[_head] = default!;
        _head = _head + 1 == _items.Length ? 0 : _head + 1;
        _count--;
        return true;
    }

    /// <summary>Drop all items without touching the backing storage size.</summary>
    public void Clear()
    {
        for (int i = 0; i < _items.Length; i++)
            _items[i] = default!;
        _head = 0;
        _tail = 0;
        _count = 0;
    }

    private void Grow()
    {
        var bigger = new T[_items.Length * 2];
        for (int i = 0; i < _count; i++)
        {
            int index = _head + i;
            if (index >= _items.Length)
                index -= _items.Length;
            bigger[i] = _items[index];
        }
        _items = bigger;
        _head = 0;
        _tail = _count;
    }
}
