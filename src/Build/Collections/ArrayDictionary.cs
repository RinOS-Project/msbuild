// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;

#nullable disable

namespace Microsoft.Build.Collections
{
    /// <summary>
    /// Lightweight, read-only IDictionary implementation using two arrays
    /// and O(n) lookup.
    /// Requires specifying capacity at construction and does not
    /// support reallocation to increase capacity.
    /// </summary>
    /// <typeparam name="TKey">Type of keys</typeparam>
    /// <typeparam name="TValue">Type of values</typeparam>
    internal class ArrayDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDictionary, IReadOnlyDictionary<TKey, TValue>
    {
        private TKey[] keys;
        private TValue[] values;

        private int count;

        public ArrayDictionary(int capacity)
        {
            keys = new TKey[capacity];
            values = new TValue[capacity];
        }

        public static IDictionary<TKey, TValue> Create(int capacity)
        {
            return new ArrayDictionary<TKey, TValue>(capacity);
        }

        public TValue this[TKey key]
        {
            get
            {
                TryGetValue(key, out var value);
                return value;
            }

            set
            {
                var comparer = KeyComparer;
                for (int i = 0; i < count; i++)
                {
                    if (comparer.Equals(key, keys[i]))
                    {
                        values[i] = value;
                        return;
                    }
                }

                Add(key, value);
            }
        }

        object IDictionary.this[object key]
        {
            get => this[(TKey)key];
            set => throw new NotSupportedException("ArrayDictionary is read-only after construction.");
        }

        public ICollection<TKey> Keys => new List<TKey>(keys, 0, count);

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => new List<TKey>(keys, 0, count);

        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => new List<TValue>(values, 0, count);

        ICollection IDictionary.Keys => new List<TKey>(keys, 0, count);

        public ICollection<TValue> Values => new List<TValue>(values, 0, count);

        ICollection IDictionary.Values => new List<TValue>(values, 0, count);

        private IEqualityComparer<TKey> KeyComparer => EqualityComparer<TKey>.Default;

        private IEqualityComparer<TValue> ValueComparer => EqualityComparer<TValue>.Default;

        public int Count => count;

        public bool IsReadOnly => true;

        bool IDictionary.IsFixedSize => true;

        object ICollection.SyncRoot => this;

        bool ICollection.IsSynchronized => false;

        public void Add(TKey key, TValue value)
        {
            if (ContainsKey(key))
            {
                throw new ArgumentException("An item with the same key has already been added.", nameof(key));
            }

            if (count < keys.Length)
            {
                keys[count] = key;
                values[count] = value;
                count += 1;
            }
            else
            {
                throw new InvalidOperationException($"ArrayDictionary is at capacity {keys.Length}");
            }
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            Add(item.Key, item.Value);
        }

        public void Clear()
        {
            throw new NotSupportedException("ArrayDictionary is read-only after construction.");
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            var keyComparer = KeyComparer;
            var valueComparer = ValueComparer;
            for (int i = 0; i < count; i++)
            {
                if (keyComparer.Equals(item.Key, keys[i]) && valueComparer.Equals(item.Value, values[i]))
                {
                    return true;
                }
            }

            return false;
        }

        public bool ContainsKey(TKey key)
        {
            var comparer = KeyComparer;
            for (int i = 0; i < count; i++)
            {
                if (comparer.Equals(key, keys[i]))
                {
                    return true;
                }
            }

            return false;
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            if (array is null) throw new ArgumentNullException(nameof(array));
            if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            if (array.Length - arrayIndex < count) throw new ArgumentException("The destination array is too small.", nameof(array));
            for (int i = 0; i < count; i++)
            {
                array[arrayIndex + i] = new KeyValuePair<TKey, TValue>(keys[i], values[i]);
            }
        }

        void ICollection.CopyTo(Array array, int index)
        {
            if (array is null) throw new ArgumentNullException(nameof(array));
            if (array.Rank != 1) throw new ArgumentException("The destination array must be one-dimensional.", nameof(array));
            if (array.GetLowerBound(0) != 0) throw new ArgumentException("The destination array must have a zero lower bound.", nameof(array));
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (array.Length - index < count) throw new ArgumentException("The destination array is too small.", nameof(array));

            if (array is KeyValuePair<TKey, TValue>[] pairs)
            {
                CopyTo(pairs, index);
                return;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    array.SetValue(new DictionaryEntry(keys[i], values[i]), index + i);
                }
            }
            catch (InvalidCastException)
            {
                throw new ArgumentException("The destination array has an incompatible element type.", nameof(array));
            }
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        IDictionaryEnumerator IDictionary.GetEnumerator()
        {
            return new Enumerator(this, emitDictionaryEntries: true);
        }

        public bool Remove(TKey key)
        {
            throw new NotSupportedException("ArrayDictionary is read-only after construction.");
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            throw new NotSupportedException("ArrayDictionary is read-only after construction.");
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            var comparer = KeyComparer;
            for (int i = 0; i < count; i++)
            {
                if (comparer.Equals(key, keys[i]))
                {
                    value = values[i];
                    return true;
                }
            }

            value = default;
            return false;
        }

        bool IDictionary.Contains(object key)
        {
            if (key is not TKey typedKey)
            {
                return false;
            }

            return ContainsKey(typedKey);
        }

        void IDictionary.Add(object key, object value)
        {
            throw new NotSupportedException("ArrayDictionary is read-only after construction.");
        }

        void IDictionary.Remove(object key)
        {
            throw new NotSupportedException("ArrayDictionary is read-only after construction.");
        }

        private struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>, IDictionaryEnumerator
        {
            private readonly ArrayDictionary<TKey, TValue> _dictionary;
            private readonly bool _emitDictionaryEntries;
            private int _position;

            public Enumerator(ArrayDictionary<TKey, TValue> dictionary, bool emitDictionaryEntries = false)
            {
                this._dictionary = dictionary;
                this._position = -1;
                this._emitDictionaryEntries = emitDictionaryEntries;
            }

            public KeyValuePair<TKey, TValue> Current
            {
                get
                {
                    EnsureCurrent();
                    return new KeyValuePair<TKey, TValue>(
                        _dictionary.keys[_position],
                        _dictionary.values[_position]);
                }
            }

            private DictionaryEntry CurrentDictionaryEntry
            {
                get
                {
                    EnsureCurrent();
                    return new DictionaryEntry(_dictionary.keys[_position], _dictionary.values[_position]);
                }
            }

            object IEnumerator.Current => _emitDictionaryEntries ? CurrentDictionaryEntry : Current;

            object IDictionaryEnumerator.Key => _dictionary.keys[_position];

            object IDictionaryEnumerator.Value => _dictionary.values[_position];

            DictionaryEntry IDictionaryEnumerator.Entry => CurrentDictionaryEntry;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                _position += 1;
                return _position < _dictionary.Count;
            }

            public void Reset()
            {
                _position = -1;
            }

            private void EnsureCurrent()
            {
                if (_position < 0 || _position >= _dictionary.Count)
                {
                    throw new InvalidOperationException("The enumerator is not positioned on an element.");
                }
            }
        }
    }
}
