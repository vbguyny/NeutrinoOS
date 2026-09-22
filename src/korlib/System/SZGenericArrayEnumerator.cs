// ProtonOS korlib - generic single-dimension array enumerator
//
// The runtime returns this enumerator when a T[] is dispatched through
// IEnumerable<T>/IEnumerator<T> (array MethodTables carry no interface map,
// so the kernel interface resolver creates one of these on demand).

using System.Collections;
using System.Collections.Generic;

namespace System;

/// <summary>
/// Enumerates the elements of a single-dimension array through the generic
/// IEnumerable&lt;T&gt;/IEnumerator&lt;T&gt; interfaces.
/// </summary>
/// <typeparam name="T">The element type of the array.</typeparam>
public sealed class SZGenericArrayEnumerator<T> : IEnumerator<T>, IEnumerator, IDisposable
{
    private readonly T[] _array;
    private int _index;

    public SZGenericArrayEnumerator(T[] array)
    {
        _array = array;
        _index = -1;
    }

    /// <summary>
    /// Gets the element at the current position of the enumerator.
    /// </summary>
    public T Current
    {
        get
        {
            if (_index >= 0 && _index < _array.Length)
                return _array[_index];
            return default!;
        }
    }

    // Explicit non-generic implementation (different return type).
    object? IEnumerator.Current => Current;

    /// <summary>
    /// Advances the enumerator to the next element of the array.
    /// </summary>
    public bool MoveNext()
    {
        if (_index + 1 < _array.Length)
        {
            _index++;
            return true;
        }

        _index = _array.Length;
        return false;
    }

    /// <summary>
    /// Sets the enumerator to its initial position.
    /// </summary>
    public void Reset()
    {
        _index = -1;
    }

    /// <summary>
    /// No resources to release.
    /// </summary>
    public void Dispose()
    {
    }
}
