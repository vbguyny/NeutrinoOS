// NeutrinoOS korlib - ReadOnlyDictionary<TKey, TValue>
// Read-only wrapper around an IDictionary<TKey, TValue> (used by Tomlyn's
// TomlPolymorphismOptions.PropertyDescriptors, which wraps its descriptor
// dictionary before exposure).

using System.Collections.Generic;

namespace System.Collections.ObjectModel
{
    /// <summary>
    /// Represents a read-only, generic collection of key/value pairs.
    /// </summary>
    public class ReadOnlyDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        // Keep a direct Dictionary<TKey, TValue> reference when possible to
        // avoid interface dispatch on generic calls in the JIT (same pattern
        // as ReadOnlyCollection<T>).
        private readonly IDictionary<TKey, TValue> _dictionary;
        private readonly Dictionary<TKey, TValue>? _concreteDictionary;

        /// <summary>Initializes a new instance that wraps the specified dictionary.</summary>
        public ReadOnlyDictionary(IDictionary<TKey, TValue> dictionary)
        {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));
            _dictionary = dictionary;
            _concreteDictionary = dictionary as Dictionary<TKey, TValue>;
        }

        /// <summary>Gets the element that has the specified key.</summary>
        public TValue this[TKey key]
            => _concreteDictionary != null ? _concreteDictionary[key] : _dictionary[key];

        TValue IDictionary<TKey, TValue>.this[TKey key]
        {
            get => this[key];
            set => throw new NotSupportedException("Dictionary is read-only");
        }

        /// <summary>Gets the number of elements in the dictionary.</summary>
        public int Count => _concreteDictionary != null ? _concreteDictionary.Count : _dictionary.Count;

        /// <summary>Gets a collection containing the keys of the dictionary.</summary>
        public IEnumerable<TKey> Keys
            => _concreteDictionary != null ? _concreteDictionary.Keys : _dictionary.Keys;

        /// <summary>Gets a collection containing the values of the dictionary.</summary>
        public IEnumerable<TValue> Values
            => _concreteDictionary != null ? _concreteDictionary.Values : _dictionary.Values;

        ICollection<TKey> IDictionary<TKey, TValue>.Keys
            => _concreteDictionary != null ? _concreteDictionary.Keys : _dictionary.Keys;

        ICollection<TValue> IDictionary<TKey, TValue>.Values
            => _concreteDictionary != null ? _concreteDictionary.Values : _dictionary.Values;

        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => true;

        /// <summary>Determines whether the dictionary contains the specified key.</summary>
        public bool ContainsKey(TKey key)
            => _concreteDictionary != null ? _concreteDictionary.ContainsKey(key) : _dictionary.ContainsKey(key);

        /// <summary>Gets the value associated with the specified key.</summary>
        public bool TryGetValue(TKey key, out TValue value)
        {
            if (_concreteDictionary != null)
                return _concreteDictionary.TryGetValue(key, out value!);
            return _dictionary.TryGetValue(key, out value!);
        }

        /// <summary>Determines whether the dictionary contains a specific value.</summary>
        public bool ContainsValue(TValue value)
        {
            foreach (KeyValuePair<TKey, TValue> pair in this)
            {
                if (EqualityComparer<TValue>.Default.Equals(pair.Value, value))
                    return true;
            }
            return false;
        }

        /// <summary>Determines whether the dictionary contains a specific key/value pair.</summary>
        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            if (TryGetValue(item.Key, out TValue? value))
                return EqualityComparer<TValue>.Default.Equals(value, item.Value);
            return false;
        }

        /// <summary>Copies the elements of the dictionary to an array.</summary>
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));
            if (index < 0 || index > array.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (array.Length - index < Count)
                throw new ArgumentException("Destination array is not long enough");

            foreach (KeyValuePair<TKey, TValue> pair in this)
            {
                array[index++] = pair;
            }
        }

        /// <summary>Returns an enumerator that iterates through the dictionary.</summary>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
            => _concreteDictionary != null ? _concreteDictionary.GetEnumerator() : _dictionary.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => _concreteDictionary != null ? _concreteDictionary.GetEnumerator() : _dictionary.GetEnumerator();

        // Mutation is not supported.
        void IDictionary<TKey, TValue>.Add(TKey key, TValue value) => throw new NotSupportedException("Dictionary is read-only");
        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => throw new NotSupportedException("Dictionary is read-only");
        void ICollection<KeyValuePair<TKey, TValue>>.Clear() => throw new NotSupportedException("Dictionary is read-only");
        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item) => throw new NotSupportedException("Dictionary is read-only");
        bool IDictionary<TKey, TValue>.Remove(TKey key) => throw new NotSupportedException("Dictionary is read-only");
    }
}
