// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Collections;
using System.Collections.Generic;

namespace Riptide.Utils
{
	/// <summary>A list, that can remove and add the first element in O(1) time.</summary>
	/// <remarks>I also added SetUnchecked, since it was useful.</remarks>
	/// <typeparam name="T">The type of the elements of the list.</typeparam>
    internal class WrappingList<T> : IEnumerable<T>
	{
		private T[] buffer;
		private int start;
		private int count;

		internal WrappingList(int initialCapacity = 16) {
			if(initialCapacity < 1) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
			buffer = new T[NextPowerOfTwo(initialCapacity)];
			start = 0;
			count = 0;
		}

		internal T this[int index] {
			get {
				if(index >= count || index < 0) throw new IndexOutOfRangeException($"index: {index} Count: {count}");
				return buffer[IndexConverter(index)];
			}
			set {
				if(index >= count || index < 0) throw new IndexOutOfRangeException($"index: {index} Count: {count}");
				buffer[IndexConverter(index)] = value;
			}
		}

		internal int Count => count;
		/// <summary>This will always be a power of 2</summary>
		internal int Capacity => buffer.Length;
		
		/// <summary>This is why Capacity has to be a power of 2</summary>
		private int IndexConverter(int i) => (start + i) & (Capacity - 1);

		internal void AddFirst(T item) {
			MakeSpace(1);
			buffer[start] = item;
		}

		internal void RemoveFirst() {
			if(count == 0) throw new InvalidOperationException("List is empty.");
			buffer[start] = default;
			start = IndexConverter(1);
			count--;
		}

		internal void Add(T item) {
			if(count == Capacity) DoubleCapacity();
			this[count++] = item;
		}

		internal void Remove() {
			if(count == 0) throw new InvalidOperationException("List is empty.");
			this[--count] = default;
		}

		internal T Last() => this[count - 1];

		internal void Clear() {
			Array.Clear(buffer, 0, count);
			start = 0;
			count = 0;
		}

		public IEnumerator<T> GetEnumerator() {
			for(int i = 0; i < count; i++)
				yield return this[i];
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		internal void SetUnchecked(int index, T item) {
			if(index < 0) {
				MakeSpace(-index);
				buffer[start] = item;
				return;
			}
			if(index >= count) {
				IncreaseCapacity(++index);
				count = index--;
			}
			this[index] = item;
		}

		internal void AddRange(IEnumerable<T> items) {
			foreach(T item in items) Add(item);
		}

		internal void RemoveRange(int count) {
			for(int i = 0; i < count; i++) Remove();
		}

		internal int IndexOf(T item) {
			EqualityComparer<T> comparer = EqualityComparer<T>.Default;
			for(int i = 0; i < count; i++) 
				if(comparer.Equals(this[i], item)) return i;
			return -1;
		}

		internal void SetCapacity(int capacity) {
			if(capacity <= Count) return;
			capacity = NextPowerOfTwo(capacity);
			SetCapacityUnchecked(capacity);
		}

		private void IncreaseCapacity(int capacity) {
			if(capacity <= Capacity) return;
			capacity = NextPowerOfTwo(capacity);
			SetCapacityUnchecked(capacity);
		}

		private void MakeSpace(int amount) {
			int newSpace = count + amount;
			IncreaseCapacity(newSpace);
			start = IndexConverter(Capacity - amount);
			count = newSpace;
		}

		private void DoubleCapacity() => SetCapacityUnchecked(Capacity << 1);

		private void SetCapacityUnchecked(int capacity) {
			T[] newBuffer = new T[capacity];
			int right = Math.Min(count, Capacity - start);
			Array.Copy(buffer, start, newBuffer, 0, right);
			Array.Copy(buffer, 0, newBuffer, right, count - right);
			buffer = newBuffer;
			start = 0;
		}

		private static int NextPowerOfTwo(int x) {
			if(x <= 1) return 1;
			x--;
			x |= x >> 1;
			x |= x >> 2;
			x |= x >> 4;
			x |= x >> 8;
			x |= x >> 16;
			return x + 1;
		}
	}
}