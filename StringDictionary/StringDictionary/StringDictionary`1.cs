using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CA2208
#pragma warning disable CS8601
#pragma warning disable CS8603
#pragma warning disable CS8618
#pragma warning disable CS8619
#pragma warning disable CS8625
#pragma warning disable CS8632

// ReSharper disable ALL

namespace Herta
{
    /// <summary>
    ///     String dictionary
    /// </summary>
    /// <typeparam name="TValue">Type</typeparam>
    public sealed class StringDictionary<TValue>
    {
        /// <summary>
        ///     Buckets
        /// </summary>
        private int[] _buckets;

        /// <summary>
        ///     Entries
        /// </summary>
        private Entry[] _entries;

        /// <summary>
        ///     FastModMultiplier
        /// </summary>
        private ulong _fastModMultiplier;

        /// <summary>
        ///     Count
        /// </summary>
        private int _count;

        /// <summary>
        ///     FreeList
        /// </summary>
        private int _freeList;

        /// <summary>
        ///     FreeCount
        /// </summary>
        private int _freeCount;

        /// <summary>
        ///     Version
        /// </summary>
        private int _version;

        /// <summary>
        ///     Structure
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StringDictionary() : this(4)
        {
        }

        /// <summary>
        ///     Structure
        /// </summary>
        /// <param name="capacity">Capacity</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StringDictionary(int capacity)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "MustBeNonNegative");
            if (capacity < 4)
                capacity = 4;
            Initialize(capacity);
        }

        /// <summary>
        ///     Get or set value
        /// </summary>
        /// <param name="key">Key</param>
        public TValue this[ReadOnlySpan<char> key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                    throw new ArgumentNullException(nameof(key));
                ref var value = ref FindValue(key);
                if (!Unsafe.IsNullRef(ref Unsafe.AsRef(in value)))
                    return value;
                throw new KeyNotFoundException(key.ToString());
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                    throw new ArgumentNullException(nameof(key));
                TryInsertOverwriteExisting(key, value);
            }
        }

        /// <summary>
        ///     Get or set value
        /// </summary>
        /// <param name="key">Key</param>
        public TValue this[string key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (key == null)
                    throw new ArgumentNullException(nameof(key));
                ref var value = ref FindValue(key);
                if (!Unsafe.IsNullRef(ref Unsafe.AsRef(in value)))
                    return value;
                throw new KeyNotFoundException(key);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (key == null)
                    throw new ArgumentNullException(nameof(key));
                TryInsertOverwriteExistingString(key, value);
            }
        }

        /// <summary>
        ///     Is empty
        /// </summary>
        public bool IsEmpty => _count - _freeCount == 0;

        /// <summary>
        ///     Count
        /// </summary>
        public int Count => _count - _freeCount;

        /// <summary>
        ///     Capacity
        /// </summary>
        public int Capacity => _entries.Length;

        /// <summary>
        ///     Keys
        /// </summary>
        public KeyCollection Keys => new(this);

        /// <summary>
        ///     Values
        /// </summary>
        public ValueCollection Values => new(this);

        /// <summary>
        ///     Clear
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            var count = _count;
            if (count > 0)
            {
                Array.Clear(_buckets, 0, count);
                Array.Clear(_entries, 0, count);
                _count = 0;
                _freeList = -1;
                _freeCount = 0;
            }
        }

        /// <summary>
        ///     Add
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(ReadOnlySpan<char> key, in TValue value)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                throw new ArgumentNullException(nameof(key));
            TryInsertThrowOnExisting(key, value);
        }

        /// <summary>
        ///     Add
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(string key, in TValue value)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            TryInsertThrowOnExistingString(key, value);
        }

        /// <summary>
        ///     Try add
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Added</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(ReadOnlySpan<char> key, in TValue value)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                throw new ArgumentNullException(nameof(key));
            return TryInsertNone(key, value);
        }

        /// <summary>
        ///     Try add
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Added</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(string key, in TValue value)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            return TryInsertNoneString(key, value);
        }

        /// <summary>
        ///     Remove
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Removed</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(ReadOnlySpan<char> key)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                throw new ArgumentNullException(nameof(key));
            uint collisionCount = 0;
            var hashCode = (uint)XxHash.Hash32(key);
            ref var bucket = ref GetBucket(hashCode);
            var last = -1;
            var i = bucket - 1;
            while (i >= 0)
            {
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                {
                    if (last < 0)
                        bucket = entry.Next + 1;
                    else
                        _entries[last].Next = entry.Next;
                    entry.Next = -3 - _freeList;
                    _freeList = i;
                    _freeCount++;
                    return true;
                }

                last = i;
                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entries.Length)
                    throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }

            return false;
        }

        /// <summary>
        ///     Remove
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Removed</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(ReadOnlySpan<char> key, out TValue value)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                throw new ArgumentNullException(nameof(key));
            uint collisionCount = 0;
            var hashCode = (uint)XxHash.Hash32(key);
            ref var bucket = ref GetBucket(hashCode);
            var last = -1;
            var i = bucket - 1;
            while (i >= 0)
            {
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                {
                    if (last < 0)
                        bucket = entry.Next + 1;
                    else
                        _entries[last].Next = entry.Next;
                    value = entry.Value;
                    entry.Next = -3 - _freeList;
                    _freeList = i;
                    _freeCount++;
                    return true;
                }

                last = i;
                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entries.Length)
                    throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }

            value = default;
            return false;
        }

        /// <summary>
        ///     Contains key
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Contains key</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(ReadOnlySpan<char> key)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                throw new ArgumentNullException(nameof(key));
            return !Unsafe.IsNullRef(ref Unsafe.AsRef(in FindValue(key)));
        }

        /// <summary>
        ///     Try to get the value
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Got</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(ReadOnlySpan<char> key, out TValue value)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                throw new ArgumentNullException(nameof(key));
            ref var valRef = ref FindValue(key);
            if (!Unsafe.IsNullRef(ref Unsafe.AsRef(in valRef)))
            {
                value = valRef;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        ///     Get value ref
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="exists">Exists</param>
        /// <returns>Value ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueRef(ReadOnlySpan<char> key, out bool exists)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                throw new ArgumentNullException(nameof(key));
            ref var valRef = ref FindValue(key);
            exists = !Unsafe.IsNullRef(ref Unsafe.AsRef(in valRef));
            return ref valRef;
        }

        /// <summary>
        ///     Get value ref or add default
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="exists">Exists</param>
        /// <returns>Value ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueRefOrAddDefault(ReadOnlySpan<char> key, out bool exists)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(key)))
                throw new ArgumentNullException(nameof(key));
            var hashCode = (uint)XxHash.Hash32(key);
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entries.Length)
                    break;
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                {
                    exists = true;
                    return ref entry.Value;
                }

                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entries.Length)
                    throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - _entries[_freeList].Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }

                index = count;
                _count = count + 1;
            }

            ref var newEntry = ref _entries[index];
            newEntry.HashCode = hashCode;
            newEntry.Next = bucket - 1;
            newEntry.Key = key.ToString();
            newEntry.Value = default;
            bucket = index + 1;
            _version++;
            exists = false;
            return ref newEntry.Value;
        }

        /// <summary>
        ///     Ensure capacity
        /// </summary>
        /// <param name="capacity">Capacity</param>
        /// <returns>New capacity</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int EnsureCapacity(int capacity)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "MustBeNonNegative");
            var currentCapacity = _entries.Length;
            if (currentCapacity >= capacity)
                return currentCapacity;
            _version++;
            var newSize = HashHelpers.GetPrime(capacity);
            Resize(newSize);
            return newSize;
        }

        /// <summary>
        ///     Trim excess
        /// </summary>
        /// <returns>New capacity</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int TrimExcess() => TrimExcess(_count);

        /// <summary>
        ///     Trim excess
        /// </summary>
        /// <param name="capacity">Capacity</param>
        /// <returns>New capacity</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int TrimExcess(int capacity)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "MustBeNonNegative");
            var newSize = HashHelpers.GetPrime(capacity);
            var oldEntries = _entries;
            var currentCapacity = _entries.Length;
            if (newSize >= currentCapacity)
                return currentCapacity;
            var oldCount = _count;
            _version++;
            Initialize(newSize);
            var newEntries = _entries;
            var newCount = 0;
            for (var i = 0; i < oldCount; ++i)
            {
                var hashCode = oldEntries[i].HashCode;
                if (oldEntries[i].Next >= -1)
                {
                    ref var entry = ref newEntries[newCount];
                    entry = oldEntries[i];
                    ref var bucket = ref GetBucket(hashCode);
                    entry.Next = bucket - 1;
                    bucket = newCount + 1;
                    newCount++;
                }
            }

            _count = newCount;
            _freeCount = 0;
            return newSize;
        }

        /// <summary>
        ///     Find value
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Value</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref TValue FindValue(ReadOnlySpan<char> key)
        {
            var hashCode = (uint)XxHash.Hash32(key);
            var i = GetBucket(hashCode);
            uint collisionCount = 0;
            i--;
            do
            {
                if ((uint)i >= (uint)_entries.Length)
                    return ref Unsafe.NullRef<TValue>();
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                    return ref entry.Value;
                i = entry.Next;
                collisionCount++;
            } while (collisionCount <= (uint)_entries.Length);

            throw new InvalidOperationException("ConcurrentOperationsNotSupported");
        }

        /// <summary>
        ///     Initialize
        /// </summary>
        /// <param name="capacity">Capacity</param>
        /// <returns>New capacity</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Initialize(int capacity)
        {
            var size = HashHelpers.GetPrime(capacity);
            _freeList = -1;
            _buckets = new int[size];
            _entries = new Entry[size];
            _fastModMultiplier = Unsafe.SizeOf<nint>() == 8 ? HashHelpers.GetFastModMultiplier((uint)size) : 0;
        }

        /// <summary>
        ///     Resize
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Resize() => Resize(HashHelpers.ExpandPrime(_count));

        /// <summary>
        ///     Resize
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Resize(int newSize)
        {
            var buckets = new int[newSize];
            var entries = new Entry[newSize];
            var count = _count;
            Array.Copy(_entries, entries, count);
            _buckets = buckets;
            _fastModMultiplier = Unsafe.SizeOf<nint>() == 8 ? HashHelpers.GetFastModMultiplier((uint)newSize) : 0;
            for (var i = 0; i < count; ++i)
            {
                ref var entry = ref entries[i];
                if (entry.Next >= -1)
                {
                    ref var bucket = ref GetBucket(entry.HashCode);
                    entry.Next = bucket - 1;
                    bucket = i + 1;
                }
            }

            _entries = entries;
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryInsertOverwriteExisting(ReadOnlySpan<char> key, in TValue value)
        {
            var hashCode = (uint)XxHash.Hash32(key);
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entries.Length)
                    break;
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                {
                    entry.Value = value;
                    return;
                }

                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entries.Length)
                    throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - _entries[_freeList].Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }

                index = count;
                _count = count + 1;
            }

            ref var newEntry = ref _entries[index];
            newEntry.HashCode = hashCode;
            newEntry.Next = bucket - 1;
            newEntry.Key = key.ToString();
            newEntry.Value = value;
            bucket = index + 1;
            _version++;
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryInsertThrowOnExisting(ReadOnlySpan<char> key, in TValue value)
        {
            var hashCode = (uint)XxHash.Hash32(key);
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entries.Length)
                    break;
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                    throw new ArgumentException($"AddingDuplicateWithKey, {key.ToString()}");
                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entries.Length)
                    throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - _entries[_freeList].Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }

                index = count;
                _count = count + 1;
            }

            ref var newEntry = ref _entries[index];
            newEntry.HashCode = hashCode;
            newEntry.Next = bucket - 1;
            newEntry.Key = key.ToString();
            newEntry.Value = value;
            bucket = index + 1;
            _version++;
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryInsertNone(ReadOnlySpan<char> key, in TValue value)
        {
            var hashCode = (uint)XxHash.Hash32(key);
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entries.Length)
                    break;
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                    return false;
                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entries.Length)
                    throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - _entries[_freeList].Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }

                index = count;
                _count = count + 1;
            }

            ref var newEntry = ref _entries[index];
            newEntry.HashCode = hashCode;
            newEntry.Next = bucket - 1;
            newEntry.Key = key.ToString();
            newEntry.Value = value;
            bucket = index + 1;
            _version++;
            return true;
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryInsertOverwriteExistingString(string key, in TValue value)
        {
            var hashCode = (uint)XxHash.Hash32(key.AsSpan());
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entries.Length)
                    break;
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                {
                    entry.Value = value;
                    return;
                }

                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entries.Length)
                    throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - _entries[_freeList].Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }

                index = count;
                _count = count + 1;
            }

            ref var newEntry = ref _entries[index];
            newEntry.HashCode = hashCode;
            newEntry.Next = bucket - 1;
            newEntry.Key = key;
            newEntry.Value = value;
            bucket = index + 1;
            _version++;
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryInsertThrowOnExistingString(string key, in TValue value)
        {
            var hashCode = (uint)XxHash.Hash32(key.AsSpan());
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entries.Length)
                    break;
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                    throw new ArgumentException($"AddingDuplicateWithKey, {key}");
                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entries.Length)
                    throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - _entries[_freeList].Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }

                index = count;
                _count = count + 1;
            }

            ref var newEntry = ref _entries[index];
            newEntry.HashCode = hashCode;
            newEntry.Next = bucket - 1;
            newEntry.Key = key;
            newEntry.Value = value;
            bucket = index + 1;
            _version++;
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryInsertNoneString(string key, in TValue value)
        {
            var hashCode = (uint)XxHash.Hash32(key.AsSpan());
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entries.Length)
                    break;
                ref var entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.AsSpan().SequenceEqual(key))
                    return false;
                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entries.Length)
                    throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - _entries[_freeList].Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }

                index = count;
                _count = count + 1;
            }

            ref var newEntry = ref _entries[index];
            newEntry.HashCode = hashCode;
            newEntry.Next = bucket - 1;
            newEntry.Key = key;
            newEntry.Value = value;
            bucket = index + 1;
            _version++;
            return true;
        }

        /// <summary>
        ///     Get bucket ref
        /// </summary>
        /// <param name="hashCode">HashCode</param>
        /// <returns>Bucket ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref int GetBucket(uint hashCode) => ref Unsafe.SizeOf<nint>() == 8 ? ref _buckets[HashHelpers.FastMod(hashCode, (uint)_buckets.Length, _fastModMultiplier)] : ref _buckets[hashCode % _buckets.Length];

        /// <summary>
        ///     Copy to
        /// </summary>
        /// <param name="buffer">Buffer</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyTo(Span<KeyValuePair<string, TValue>> buffer)
        {
            var count = _count - _freeCount;
            var entries = _entries;
            var offset = 0;
            for (var index = 0; index < _count && count != 0; ++index)
            {
                ref var local = ref entries[index];
                if (local.Next >= -1)
                {
                    buffer[offset++] = new KeyValuePair<string, TValue>(local.Key, local.Value);
                    --count;
                }
            }
        }

        /// <summary>
        ///     Get enumerator
        /// </summary>
        /// <returns>Enumerator</returns>
        public Enumerator GetEnumerator() => new(this);

        /// <summary>
        ///     Entry
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Entry
        {
            /// <summary>
            ///     HashCode
            /// </summary>
            public uint HashCode;

            /// <summary>
            ///     Next
            /// </summary>
            public int Next;

            /// <summary>
            ///     Key
            /// </summary>
            public string Key;

            /// <summary>
            ///     Value
            /// </summary>
            public TValue Value;
        }

        /// <summary>
        ///     Enumerator
        /// </summary>
        public struct Enumerator
        {
            /// <summary>
            ///     NativeDictionary
            /// </summary>
            private readonly StringDictionary<TValue> _nativeDictionary;

            /// <summary>
            ///     Version
            /// </summary>
            private readonly int _version;

            /// <summary>
            ///     Index
            /// </summary>
            private int _index;

            /// <summary>
            ///     Current
            /// </summary>
            private KeyValuePair<string, TValue> _current;

            /// <summary>
            ///     Structure
            /// </summary>
            /// <param name="nativeDictionary">NativeDictionary</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(StringDictionary<TValue> nativeDictionary)
            {
                var handle = nativeDictionary;
                _nativeDictionary = handle;
                _version = handle._version;
                _index = 0;
                _current = default;
            }

            /// <summary>
            ///     Move next
            /// </summary>
            /// <returns>Moved</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                var handle = _nativeDictionary;
                if (_version != handle._version)
                    throw new InvalidOperationException("EnumFailedVersion");
                while ((uint)_index < (uint)handle._count)
                {
                    ref var entry = ref handle._entries[_index++];
                    if (entry.Next >= -1)
                    {
                        _current = new KeyValuePair<string, TValue>(entry.Key, entry.Value);
                        return true;
                    }
                }

                _index = handle._count + 1;
                _current = default;
                return false;
            }

            /// <summary>
            ///     Current
            /// </summary>
            public KeyValuePair<string, TValue> Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _current;
            }
        }

        /// <summary>
        ///     Key collection
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct KeyCollection
        {
            /// <summary>
            ///     NativeDictionary
            /// </summary>
            private readonly StringDictionary<TValue> _nativeDictionary;

            /// <summary>
            ///     Count
            /// </summary>
            public int Count => _nativeDictionary.Count;

            /// <summary>
            ///     Structure
            /// </summary>
            /// <param name="nativeDictionary">NativeDictionary</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal KeyCollection(StringDictionary<TValue> nativeDictionary) => _nativeDictionary = nativeDictionary;

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyTo(Span<string> buffer)
            {
                var count = _nativeDictionary._count - _nativeDictionary._freeCount;
                var entries = _nativeDictionary._entries;
                var offset = 0;
                for (var index = 0; index < _nativeDictionary._count && count != 0; ++index)
                {
                    ref var local = ref entries[index];
                    if (local.Next >= -1)
                    {
                        buffer[offset++] = local.Key;
                        --count;
                    }
                }
            }

            /// <summary>
            ///     Get enumerator
            /// </summary>
            /// <returns>Enumerator</returns>
            public Enumerator GetEnumerator() => new(_nativeDictionary);

            /// <summary>
            ///     Enumerator
            /// </summary>
            public struct Enumerator
            {
                /// <summary>
                ///     NativeDictionary
                /// </summary>
                private readonly StringDictionary<TValue> _nativeDictionary;

                /// <summary>
                ///     Index
                /// </summary>
                private int _index;

                /// <summary>
                ///     Version
                /// </summary>
                private readonly int _version;

                /// <summary>
                ///     Current
                /// </summary>
                private string _currentKey;

                /// <summary>
                ///     Structure
                /// </summary>
                /// <param name="nativeDictionary">NativeDictionary</param>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal Enumerator(StringDictionary<TValue> nativeDictionary)
                {
                    var handle = nativeDictionary;
                    _nativeDictionary = handle;
                    _version = handle._version;
                    _index = 0;
                    _currentKey = default;
                }

                /// <summary>
                ///     Move next
                /// </summary>
                /// <returns>Moved</returns>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool MoveNext()
                {
                    var handle = _nativeDictionary;
                    if (_version != handle._version)
                        throw new InvalidOperationException("EnumFailedVersion");
                    while ((uint)_index < (uint)handle._count)
                    {
                        ref var entry = ref handle._entries[_index++];
                        if (entry.Next >= -1)
                        {
                            _currentKey = entry.Key;
                            return true;
                        }
                    }

                    _index = handle._count + 1;
                    _currentKey = default;
                    return false;
                }

                /// <summary>
                ///     Current
                /// </summary>
                public string Current
                {
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    get => _currentKey;
                }
            }
        }

        /// <summary>
        ///     Value collection
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct ValueCollection
        {
            /// <summary>
            ///     NativeDictionary
            /// </summary>
            private readonly StringDictionary<TValue> _nativeDictionary;

            /// <summary>
            ///     Count
            /// </summary>
            public int Count => _nativeDictionary.Count;

            /// <summary>
            ///     Structure
            /// </summary>
            /// <param name="nativeDictionary">NativeDictionary</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ValueCollection(StringDictionary<TValue> nativeDictionary) => _nativeDictionary = nativeDictionary;

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyTo(Span<TValue> buffer)
            {
                var count = _nativeDictionary._count - _nativeDictionary._freeCount;
                var entries = _nativeDictionary._entries;
                var offset = 0;
                for (var index = 0; index < _nativeDictionary._count && count != 0; ++index)
                {
                    ref var local = ref entries[index];
                    if (local.Next >= -1)
                    {
                        buffer[offset++] = local.Value;
                        --count;
                    }
                }
            }

            /// <summary>
            ///     Get enumerator
            /// </summary>
            /// <returns>Enumerator</returns>
            public Enumerator GetEnumerator() => new(_nativeDictionary);

            /// <summary>
            ///     Enumerator
            /// </summary>
            public struct Enumerator
            {
                /// <summary>
                ///     NativeDictionary
                /// </summary>
                private readonly StringDictionary<TValue> _nativeDictionary;

                /// <summary>
                ///     Index
                /// </summary>
                private int _index;

                /// <summary>
                ///     Version
                /// </summary>
                private readonly int _version;

                /// <summary>
                ///     Current
                /// </summary>
                private TValue _currentValue;

                /// <summary>
                ///     Structure
                /// </summary>
                /// <param name="nativeDictionary">NativeDictionary</param>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal Enumerator(StringDictionary<TValue> nativeDictionary)
                {
                    var handle = nativeDictionary;
                    _nativeDictionary = handle;
                    _version = handle._version;
                    _index = 0;
                    _currentValue = default;
                }

                /// <summary>
                ///     Move next
                /// </summary>
                /// <returns>Moved</returns>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool MoveNext()
                {
                    var handle = _nativeDictionary;
                    if (_version != handle._version)
                        throw new InvalidOperationException("EnumFailedVersion");
                    while ((uint)_index < (uint)handle._count)
                    {
                        ref var entry = ref handle._entries[_index++];
                        if (entry.Next >= -1)
                        {
                            _currentValue = entry.Value;
                            return true;
                        }
                    }

                    _index = handle._count + 1;
                    _currentValue = default;
                    return false;
                }

                /// <summary>
                ///     Current
                /// </summary>
                public TValue Current
                {
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    get => _currentValue;
                }
            }
        }
    }
}