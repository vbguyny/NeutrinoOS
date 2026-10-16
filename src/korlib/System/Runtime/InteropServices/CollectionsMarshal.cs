// NeutrinoOS korlib - CollectionsMarshal
// Provides by-reference accessors over the collections in
// System.Collections.Generic. Packages on net10 (e.g. Tomlyn) use
// GetValueRefOrAddDefault to obtain a writable value slot in a
// Dictionary<TKey, TValue> without a second lookup.

using System.Collections.Generic;

namespace System.Runtime.InteropServices
{
    /// <summary>
    /// An unsafe class that provides a set of methods to access the underlying
    /// data representations of collections.
    /// </summary>
    public static class CollectionsMarshal
    {
        /// <summary>
        /// Gets a reference to the value for the specified key, adding a
        /// default-valued entry if the key is not present.
        /// </summary>
        /// <param name="dictionary">The dictionary to look up.</param>
        /// <param name="key">The key to look up.</param>
        /// <param name="exists">true if the key was already present.</param>
        /// <returns>A reference to the value slot for the key.</returns>
        public static ref TValue GetValueRefOrAddDefault<TKey, TValue>(
            Dictionary<TKey, TValue> dictionary, TKey key, out bool exists)
        {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));
            return ref dictionary.GetValueRefOrAddDefault(key, out exists);
        }
    }
}
