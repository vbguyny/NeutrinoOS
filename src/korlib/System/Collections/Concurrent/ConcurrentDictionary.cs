// NeutrinoOS korlib - ConcurrentDictionary<TKey, TValue>
// Thread-safe dictionary facade for net10 packages (Humanizer caches).
// The device runs JIT workloads cooperatively (single core), so the
// operations delegate to the non-concurrent Dictionary implementation.

using System.Collections.Generic;

namespace System.Collections.Concurrent
{
    /// <summary>
    /// Represents a thread-safe collection of key/value pairs that can be
    /// accessed by multiple threads concurrently.
    /// </summary>
    public class ConcurrentDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        private readonly Dictionary<TKey, TValue> _dictionary;

        public ConcurrentDictionary()
        {
            _dictionary = new Dictionary<TKey, TValue>();
        }

        public ConcurrentDictionary(IEqualityComparer<TKey>? comparer)
        {
            _dictionary = new Dictionary<TKey, TValue>(comparer);
        }

        public ConcurrentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
        {
            _dictionary = new Dictionary<TKey, TValue>();
            foreach (KeyValuePair<TKey, TValue> pair in collection)
            {
                _dictionary[pair.Key] = pair.Value;
            }
        }

        public TValue this[TKey key]
        {
            get => _dictionary[key];
            set => _dictionary[key] = value;
        }

        public int Count => _dictionary.Count;
        public bool IsEmpty => _dictionary.Count == 0;
        public bool IsReadOnly => false;

        public ICollection<TKey> Keys => _dictionary.Keys;
        public ICollection<TValue> Values => _dictionary.Values;

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _dictionary.Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _dictionary.Values;

        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);

        public bool TryGetValue(TKey key, out TValue value) => _dictionary.TryGetValue(key, out value!);

        public bool TryAdd(TKey key, TValue value)
        {
            if (_dictionary.ContainsKey(key))
                return false;
            _dictionary.Add(key, value);
            return true;
        }

        public bool TryRemove(TKey key, out TValue value)
        {
            if (_dictionary.TryGetValue(key, out value!))
            {
                _dictionary.Remove(key);
                return true;
            }
            value = default!;
            return false;
        }

        public bool TryRemove(TKey key) => _dictionary.Remove(key);

        public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
        {
            if (_dictionary.TryGetValue(key, out TValue? current) &&
                EqualityComparer<TValue>.Default.Equals(current, comparisonValue))
            {
                _dictionary[key] = newValue;
                return true;
            }
            return false;
        }

        public TValue GetOrAdd(TKey key, TValue value)
        {
            if (_dictionary.TryGetValue(key, out TValue? existing))
                return existing!;
            _dictionary.Add(key, value);
            return value;
        }

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (_dictionary.TryGetValue(key, out TValue? existing))
                return existing!;
            TValue created = valueFactory(key);
            _dictionary.Add(key, created);
            return created;
        }

        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (_dictionary.TryGetValue(key, out TValue? existing))
            {
                TValue updated = updateValueFactory(key, existing!);
                _dictionary[key] = updated;
                return updated;
            }
            _dictionary.Add(key, addValue);
            return addValue;
        }

        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (_dictionary.TryGetValue(key, out TValue? existing))
            {
                TValue updated = updateValueFactory(key, existing!);
                _dictionary[key] = updated;
                return updated;
            }
            TValue created = addValueFactory(key);
            _dictionary.Add(key, created);
            return created;
        }

        /// <summary>Removes all keys and values from the dictionary.</summary>
        public void Clear() => _dictionary.Clear();

        /// <summary>Copies the key/value pairs to a new array.</summary>
        public KeyValuePair<TKey, TValue>[] ToArray()
        {
            KeyValuePair<TKey, TValue>[] result = new KeyValuePair<TKey, TValue>[_dictionary.Count];
            int i = 0;
            foreach (KeyValuePair<TKey, TValue> pair in _dictionary)
            {
                result[i++] = pair;
            }
            return result;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dictionary.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _dictionary.GetEnumerator();

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            if (_dictionary.TryGetValue(item.Key, out TValue? value))
                return EqualityComparer<TValue>.Default.Equals(value, item.Value);
            return false;
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            foreach (KeyValuePair<TKey, TValue> pair in _dictionary)
            {
                array[index++] = pair;
            }
        }

        public void Add(TKey key, TValue value) => _dictionary.Add(key, value);

        public void Add(KeyValuePair<TKey, TValue> item) => _dictionary.Add(item.Key, item.Value);

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            if (_dictionary.TryGetValue(item.Key, out TValue? value) &&
                EqualityComparer<TValue>.Default.Equals(value, item.Value))
            {
                return _dictionary.Remove(item.Key);
            }
            return false;
        }

        public bool Remove(TKey key) => _dictionary.Remove(key);
    }
}
