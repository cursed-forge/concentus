/* Copyright (c) 2026 CursedForge

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   - Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

namespace Concentus.Common.CPlusPlus
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    /// <summary>
    /// CursedForge fork (branch cursedforge/no-alloc): per-codec scratch memory.
    /// </summary>
    /// <remarks>
    /// The C reference keeps its per-call scratch (VARDECL/ALLOC) on the stack.
    /// The managed port made each one a new heap array, so every encode and
    /// decode left tens of KB of garbage. Each OpusEncoder and OpusDecoder now
    /// owns one arena. The public entry point makes it current for the thread
    /// (<see cref="Enter"/>), and every former per-call allocation rents from
    /// it instead. The arena keeps every array it gives out, keyed by element
    /// type and exact length, and gives the same arrays again on the next call,
    /// so after a warm-up the codec allocates nothing.
    ///
    /// Contract, which keeps the output bit-exact with the original:
    /// - A rented array has the exact length asked for and is all zeros, the
    ///   same as <c>new T[n]</c>.
    /// - A rented array lives until the outermost entry point returns. Nested
    ///   calls (the decoder calls itself for PLC and transitions) never reset
    ///   the arena, so no array is given out twice in one call.
    /// - Only scratch that never escapes the call may be rented. State that a
    ///   codec keeps between calls stays a normal field.
    /// - With no current arena (an internal path called from outside an entry
    ///   point), every rent falls back to a plain allocation.
    ///
    /// The current arena is thread-static, so two codecs on two threads do not
    /// meet. One codec is still not safe on two threads at once, as before.
    /// </remarks>
    internal sealed class ScratchArena
    {
        [ThreadStatic]
        private static ScratchArena t_current;

        private static int s_nextTypeId;

        private object[] _pools = new object[16];

        private readonly List<Bucket> _buckets = new List<Bucket>();

        // Every rent of the current call, in order, so Release can undo the
        // rents made after a Mark (see Mark).
        private Bucket[] _log = new Bucket[256];

        private int _logCount;

        /// <summary>
        /// Makes <paramref name="arena"/> current for this thread and returns the
        /// arena that was current before (pass it to <see cref="Exit"/>). If the
        /// arena is already current (a nested call), nothing is reset.
        /// </summary>
        internal static ScratchArena Enter(ScratchArena arena)
        {
            ScratchArena previous = t_current;

            if (previous != arena)
            {
                arena.ResetAll();
                t_current = arena;
            }

            return previous;
        }

        /// <summary>Restores the arena that <see cref="Enter"/> returned.</summary>
        internal static void Exit(ScratchArena previous)
        {
            t_current = previous;
        }

        /// <summary>A zeroed array of exactly <paramref name="length"/> elements, the same as <c>new T[length]</c>.</summary>
        internal static T[] Rent<T>(int length)
        {
            ScratchArena arena = t_current;

            if (arena == null || length < 0)
            {
                // A negative length throws OverflowException, the same as before.
                return new T[length];
            }

            if (length == 0)
            {
                return EmptyArray<T>.Value;
            }

            return arena.ArrayPool<T>().Rent(arena, length);
        }

        /// <summary>
        /// A zeroed array of AT LEAST <paramref name="length"/> elements, with the
        /// length rounded up so a size that changes per frame (a pitch lag, a
        /// band width) still finds a pooled array. Use it only where the code
        /// never reads the array's Length.
        /// </summary>
        internal static T[] RentAtLeast<T>(int length)
        {
            if (length <= 0)
            {
                return Rent<T>(length);
            }

            return Rent<T>((length + 63) & ~63);
        }

        /// <summary>
        /// The position of the current call's rents. Pass it to
        /// <see cref="Release"/> when the scratch rented after it is no longer
        /// used, so a loop with a trip count that changes per frame does not
        /// grow the pool. Everything rented after the mark must be dead at the
        /// release, including what callees rented.
        /// </summary>
        internal static int Mark()
        {
            ScratchArena arena = t_current;
            return arena == null ? 0 : arena._logCount;
        }

        /// <summary>Gives back every rent made after <paramref name="mark"/> (see <see cref="Mark"/>).</summary>
        internal static void Release(int mark)
        {
            ScratchArena arena = t_current;

            if (arena == null)
            {
                return;
            }

            Bucket[] log = arena._log;

            for (int i = arena._logCount - 1; i >= mark; i--)
            {
                log[i].Used--;
                log[i] = null;
            }

            if (mark < arena._logCount)
            {
                arena._logCount = mark;
            }
        }

        /// <summary>The same as <c>Arrays.InitTwoDimensionalArray&lt;T&gt;(x, y)</c>, from the arena.</summary>
        internal static T[][] Rent2D<T>(int x, int y)
        {
            T[][] outer = Rent<T[]>(x);

            for (int c = 0; c < x; c++)
            {
                outer[c] = Rent<T>(y);
            }

            return outer;
        }

        /// <summary>
        /// An instance of <typeparamref name="T"/> for one call. It is NOT reset:
        /// the caller must put it in the state a new instance has (each type
        /// used here has a Reset method for that).
        /// </summary>
        internal static T RentObject<T>() where T : class, new()
        {
            ScratchArena arena = t_current;

            if (arena == null)
            {
                return new T();
            }

            return arena.ObjectPool<T>().Rent(arena);
        }

        internal static BoxedValueInt BoxInt(int value)
        {
            BoxedValueInt box = RentObject<BoxedValueInt>();
            box.Val = value;
            return box;
        }

        internal static BoxedValueShort BoxShort(short value)
        {
            BoxedValueShort box = RentObject<BoxedValueShort>();
            box.Val = value;
            return box;
        }

        internal static BoxedValueSbyte BoxSbyte(sbyte value)
        {
            BoxedValueSbyte box = RentObject<BoxedValueSbyte>();
            box.Val = value;
            return box;
        }

        private void ResetAll()
        {
            List<Bucket> buckets = _buckets;

            for (int i = 0; i < buckets.Count; i++)
            {
                buckets[i].Used = 0;
            }

            Array.Clear(_log, 0, _logCount);
            _logCount = 0;
        }

        private void Log(Bucket bucket)
        {
            if (_logCount == _log.Length)
            {
                Array.Resize(ref _log, _logCount * 2);
            }

            _log[_logCount++] = bucket;
        }

        private SizedArrayPool<T> ArrayPool<T>()
        {
            int id = TypeId<SizedArrayPool<T>>.Value;

            if (id >= _pools.Length)
            {
                Array.Resize(ref _pools, Math.Max(id + 1, _pools.Length * 2));
            }

            object pool = _pools[id];

            if (pool == null)
            {
                pool = new SizedArrayPool<T>();
                _pools[id] = pool;
            }

            return (SizedArrayPool<T>)pool;
        }

        private ObjectBucket<T> ObjectPool<T>() where T : class, new()
        {
            int id = TypeId<ObjectBucket<T>>.Value;

            if (id >= _pools.Length)
            {
                Array.Resize(ref _pools, Math.Max(id + 1, _pools.Length * 2));
            }

            object pool = _pools[id];

            if (pool == null)
            {
                var bucket = new ObjectBucket<T>();
                _buckets.Add(bucket);
                pool = bucket;
                _pools[id] = pool;
            }

            return (ObjectBucket<T>)pool;
        }

        private static class TypeId<TPool>
        {
            internal static readonly int Value = Interlocked.Increment(ref s_nextTypeId) - 1;
        }

        private static class EmptyArray<T>
        {
            internal static readonly T[] Value = new T[0];
        }

        private abstract class Bucket
        {
            internal int Used;
        }

        private sealed class ArrayBucket<T> : Bucket
        {
            private T[][] _items = new T[4][];
            private int _count;

            internal T[] Rent(int length)
            {
                T[] array;

                if (Used < _count)
                {
                    array = _items[Used++];
                    Array.Clear(array, 0, length);
                    return array;
                }

                array = new T[length];

                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, _count * 2);
                }

                _items[_count++] = array;
                Used = _count;
                return array;
            }
        }

        private sealed class SizedArrayPool<T>
        {
            private readonly Dictionary<int, ArrayBucket<T>> _byLength = new Dictionary<int, ArrayBucket<T>>();

            internal T[] Rent(ScratchArena arena, int length)
            {
                if (!_byLength.TryGetValue(length, out ArrayBucket<T> bucket))
                {
                    bucket = new ArrayBucket<T>();
                    _byLength.Add(length, bucket);
                    arena._buckets.Add(bucket);
                }

                arena.Log(bucket);
                return bucket.Rent(length);
            }
        }

        private sealed class ObjectBucket<T> : Bucket where T : class, new()
        {
            private T[] _items = new T[4];
            private int _count;

            internal T Rent(ScratchArena arena)
            {
                arena.Log(this);

                if (Used < _count)
                {
                    return _items[Used++];
                }

                T item = new T();

                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, _count * 2);
                }

                _items[_count++] = item;
                Used = _count;
                return item;
            }
        }
    }
}
