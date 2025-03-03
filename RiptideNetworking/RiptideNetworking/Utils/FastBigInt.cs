// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Numerics;
using System.Text;

namespace Riptide.Utils
{
    internal class FastBigInt
	{
        private ulong[] data;
        private int maxIndex = 0;
        private int minIndex = 0;

		internal int MaxIndex => maxIndex;

		internal FastBigInt(int initialCapacity) {
			data = new ulong[initialCapacity];
		}

		internal FastBigInt(int initialCapacity, ulong initialValue) {
			data = new ulong[initialCapacity];
			data[0] = initialValue;
		}

		internal FastBigInt(int capacity, byte[] bytes) {
			data = new ulong[capacity / sizeof(ulong) + 1];
			Buffer.BlockCopy(bytes, 0, data, 0, capacity);
			maxIndex = data.Length - 1;
			AdjustMinAndMax();
		}

		public static explicit operator BigInteger(FastBigInt val) {
			byte[] bytes = new byte[(val.maxIndex + 1) * sizeof(ulong)];
			int offset = val.minIndex * 8;
			Buffer.BlockCopy(val.data, offset, bytes, offset, bytes.Length);
			return new BigInteger(bytes);
		}

		public static explicit operator FastBigInt(BigInteger val) {
			byte[] bytes = val.ToByteArray();
			FastBigInt result = new FastBigInt((bytes.Length + 7) / 8);
			Buffer.BlockCopy(bytes, 0, result.data, 0, bytes.Length);
			result.maxIndex = result.data.Length - 1;
			result.AdjustMinAndMax();
			return result;
		}

		public static bool operator <=(FastBigInt a, FastBigInt b) => Compare(a, b, (x, y) => x <= y);
		public static bool operator >=(FastBigInt a, FastBigInt b) => Compare(a, b, (x, y) => x >= y);
		public static bool operator <(FastBigInt a, FastBigInt b) => Compare(a, b, (x, y) => x < y);
		public static bool operator >(FastBigInt a, FastBigInt b) => Compare(a, b, (x, y) => x > y);

		private static bool Compare(FastBigInt a, FastBigInt b, Func<ulong, ulong, bool> comparator) {
			if(a.maxIndex != b.maxIndex) return comparator((ulong)a.maxIndex, (ulong)b.maxIndex);
			for(int i = a.maxIndex; i >= a.minIndex; i--) {
				if(a.data[i] == b.data[i]) continue;
				return comparator(a.data[i], b.data[i]);
			}
			return comparator((ulong)b.minIndex, (ulong)a.minIndex);
		}

		internal FastBigInt Copy() {
			FastBigInt copy = new FastBigInt(data.Length);
			Buffer.BlockCopy(data, 0, copy.data, 0, data.Length * sizeof(ulong));
			copy.maxIndex = maxIndex;
			copy.minIndex = minIndex;
			return copy;
		}

		internal FastBigInt CopySlice(int start, int length) {
			FastBigInt slice = new FastBigInt(length);
			int copyLength = Math.Min(length, maxIndex - start + 1);
			Buffer.BlockCopy(data, start * sizeof(ulong), slice.data, 0, copyLength * sizeof(ulong));
			slice.maxIndex = length - 1;
			slice.AdjustMinAndMax();
			return slice;
		}

		public override string ToString() {
			StringBuilder s = new StringBuilder(2 * maxIndex + 6);
			for(int i = 0; i <= maxIndex; i++) {
				s.Append(Convert.ToString((long)data[i], 2).PadLeft(64, '0'));
				s.Append(' ');
			}
			s.Append('\n');
			s.Append("min: ");
			s.Append(minIndex);
			s.Append(" max: ");
			s.Append(maxIndex);
			return s.ToString();
		}

		internal ulong[] GetData() => data;
		internal bool IsPowerOf2() => minIndex == maxIndex && data[minIndex].IsPowerOf2();
		internal int Log2() => data[maxIndex].Log2() + maxIndex * 64;

		internal int GetBytesInUse() {
			int bytes = maxIndex * sizeof(ulong);
			ulong max = data[maxIndex];
			if(max == 0 && maxIndex > 0) throw new Exception("Invalid data");
			while(max > 0) {
				max >>= 8;
				bytes++;
			}
			return bytes;
		}

		internal void Add(FastBigInt value, ulong mult) {
			minIndex = Math.Min(minIndex, value.minIndex);
			maxIndex = Math.Max(maxIndex + 1, value.maxIndex + 2);
			EnsureCapacity();
			int offset = value.minIndex;
			int len = value.maxIndex - offset + 3;
			FastBigInt slice = value.CopySlice(offset, len);
			slice.Mult(mult);
			ulong carry = 0;
			for(int i = 0; i < len; i++) {
				int off = i + offset;
				(data[off], carry) = AddUlongs(data[off], slice.data[i], carry);
			}
			if(carry != 0) throw new OverflowException("Addition overflow.");
			AdjustMinAndMax();
		}

		internal void Sub(FastBigInt value, ulong mult) {
			minIndex = Math.Min(minIndex, value.minIndex);
			maxIndex = Math.Max(maxIndex, value.maxIndex + 1);
			EnsureCapacity();
			int offset = value.minIndex;
			int len = value.maxIndex - offset + 3;
			FastBigInt slice = value.CopySlice(offset, len);
			slice.Mult(mult);
			ulong carry = 0;
			for(int i = 0; i < len; i++) {
				int off = i + offset;
				(data[off], carry) = SubUlongs(data[off], slice.data[i], carry);
			}
			if(carry != 0) throw new OverflowException("Subtraction underflow.");
			AdjustMinAndMax();
		}

		internal void Mult(ulong mult) {
			if(mult.IsPowerOf2()) {
				LeftShift(mult.Log2());
				return;
			}
			maxIndex++;
			EnsureCapacity();
			ulong carry = 0;
			for(int i = minIndex; i <= maxIndex; i++) {
				ulong tempCarry = carry;
				(data[i], carry) = MultiplyUlong(data[i], mult);
				(data[i], tempCarry) = AddUlong(data[i], tempCarry);
				carry += tempCarry;
			}
			if(carry != 0) throw new OverflowException("Multiplication overflow.");
			AdjustMinAndMax();
		}

		internal ulong DivReturnMod(ulong div) {
			if(div == 0) throw new DivideByZeroException("Divisor cannot be zero.");

			if(div.IsPowerOf2()) return RightShift(div.Log2());

			minIndex = 0;
			ulong carry = 0;
			for(int i = maxIndex; i >= minIndex; i--)
				(data[i], carry) = DivideUlong(data[i], carry, div);
			AdjustMinAndMax();
			return carry;
		}

		internal void LeftShift(byte shiftBits) {
			if(shiftBits == 0) return;
			if(shiftBits == 64) {
				LeftShift1ULong();
				return;
			}
			if(shiftBits > 64) throw new ArgumentOutOfRangeException(nameof(shiftBits), "Shift bits cannot be greater than 64.");
			maxIndex++;
			EnsureCapacity();
			ulong carry = 0;
			for(int i = minIndex; i <= maxIndex; i++) {
				ulong val;
				ulong prevCarry = carry;
				(val, carry) = LeftShiftUlong(data[i], shiftBits);
				data[i] = val | prevCarry;
			}
			if(carry != 0) throw new DataMisalignedException($"Invalid carry: {this}");
			AdjustMinAndMax();
		}

		internal ulong RightShift(byte shiftBits) {
			if(shiftBits == 0) return 0;
			if(shiftBits == 64) return RightShift1ULong();
			if(shiftBits > 64) throw new ArgumentOutOfRangeException(nameof(shiftBits), "Shift bits cannot be greater than 64.");
			if(minIndex > 0) minIndex--;
			ulong carry = 0;
			for(int i = maxIndex; i >= minIndex; i--) {
				ulong val;
				ulong prevCarry = carry;
				(val, carry) = RightShiftUlong(data[i], shiftBits);
				data[i] = val | prevCarry;
			}
			if(minIndex != 0 && carry != 0) throw new DataMisalignedException($"Invalid minIndex: {this}");
			AdjustMinAndMax();
			return carry >> (64 - shiftBits);
		}

		internal void RightShiftArbitrary(int shiftBits) {
			if(shiftBits <= 0) return;
			int shiftUlongs = shiftBits / 64;
			shiftBits %= 64;
			if(shiftUlongs == 0) {
				RightShift((byte)shiftBits);
				return;
			}

			for(int i = minIndex; i <= maxIndex; i++) {
				if(i >= shiftUlongs) data[i - shiftUlongs] = data[i];
				data[i] = 0;
			}
			minIndex -= shiftUlongs;
			if(minIndex < 0) minIndex = 0;
			maxIndex -= shiftUlongs;
			if(maxIndex < 0) maxIndex = 0;
			RightShift((byte)shiftBits);
		}

		internal ulong TakeBits(int startBit, int length) {
			if(startBit < 0 || length < 0 || length > 64) throw new ArgumentOutOfRangeException();
			int startULong = startBit / 64;
			if(startULong >= data.Length) return 0;
			int shift = startBit % 64;
			ulong mask = CMath.GetMask(length);
			ulong val1 = (data[startULong] >> shift) & mask;
			int endULong = startULong + 1;
			if(shift + length <= 64 || endULong >= data.Length)
				return val1;
			int extraBits = shift + length - 64;
			ulong extraMask = (1UL << extraBits) - 1;
			ulong val2 = data[endULong] & extraMask;
			return val1 | (val2 << (64 - shift));
		}

		internal void LeftShift1ULong() {
			maxIndex++;
			EnsureCapacity();
			ulong carry = 0;
			for(int i = minIndex; i <= maxIndex; i++)
				(data[i], carry) = (carry, data[i]);
			if(carry != 0) throw new DataMisalignedException($"Invalid carry: {this}");
			AdjustMinAndMax();
		}

		internal ulong RightShift1ULong() {
			if(minIndex > 0) minIndex--;
			ulong carry = 0;
			for(int i = maxIndex; i >= minIndex; i--)
				(data[i], carry) = (carry, data[i]);
			if(minIndex != 0 && carry != 0) throw new DataMisalignedException($"Invalid minIndex: {this}");
			return carry;
		}

		private void AdjustMinAndMax() {
			while(maxIndex > 0 && data[maxIndex] == 0) maxIndex--;
			while(minIndex < maxIndex && data[minIndex] == 0) minIndex++;
			if(maxIndex == 0) minIndex = 0;
		}

		private void EnsureCapacity() {
			if(maxIndex < data.Length) return;
			ulong[] newData = new ulong[data.Length * 2];
			Array.Copy(data, 0, newData, 0, data.Length);
			data = newData;
		}


		private static (ulong value, ulong carry) AddUlong(ulong val, ulong add) {
			ulong value = val + add;
			ulong carry = (value < val).ToULong();
			return (value, carry);
		}

		private static (ulong value, ulong carry) SubUlong(ulong val, ulong sub) {
			ulong value = val - sub;
			ulong carry = (value > val).ToULong();
			return (value, carry);
		}

		private static (ulong value, ulong carry) AddUlongs(ulong val, ulong add1, ulong add2) {
			ulong value = val + add1;
			ulong carry = (value < val).ToULong();
			ulong value2 = value + add2;
			carry += (value2 < value).ToULong();
			return (value2, carry);
		}

		private static (ulong value, ulong carry) SubUlongs(ulong val, ulong sub1, ulong sub2) {
			ulong value = val - sub1;
			ulong carry = (value > val).ToULong();
			ulong value2 = value - sub2;
			carry += (value2 > value).ToULong();
			return (value2, carry);
		}

		private static (ulong value, ulong carry) MultiplyUlong(ulong val, ulong mult) {
			ulong xLow = val & 0xFFFFFFFF;
			ulong xHigh = val >> 32;
			ulong yLow = mult & 0xFFFFFFFF;
			ulong yHigh = mult >> 32;

			ulong low = xLow * yLow;
			ulong high = xHigh * yHigh;
			ulong cross1 = xLow * yHigh;
			ulong cross2 = xHigh * yLow;

			(ulong value, ulong overflow) = AddUlongs(low, cross1 << 32, cross2 << 32);
			ulong carry = high + (cross1 >> 32) + (cross2 >> 32) + overflow;
			return (value, carry);
		}

		private static (ulong value, ulong carry) LeftShiftUlong(ulong val, int shift) {
			ulong value = val << shift;
			ulong carry = val >> (64 - shift);
			return (value, carry);
		}

		private static (ulong value, ulong carry) RightShiftUlong(ulong val, int shift) {
			ulong value = val >> shift;
			ulong carry = val << (64 - shift);
			return (value, carry);
		}

		private static (ulong value, ulong rem) DivideUlong(ulong val, ulong carry, ulong div) {
			if(carry == 0) return (val / div, val % div);
			ulong extra = IntermediateDivide(ref val, carry, div);
			return (val / div + extra, val % div);
		}

		private static ulong IntermediateDivide(ref ulong val, ulong carry, ulong div) {
			if(div <= uint.MaxValue) {
				ulong intermediate = val >> 32 | carry << 32;
				val &= uint.MaxValue;
				(ulong interDiv, ulong interMod) = (intermediate / div, intermediate % div);
				val |= interMod << 32;
				return interDiv << 32;
			}
			if(div > (ulong.MaxValue >> 1)) {
				BigInteger valAndCarry = new BigInteger(carry);
				valAndCarry <<= 64;
				valAndCarry += val;
				valAndCarry /= div;
				ulong res = (ulong)valAndCarry;
				val -= res * div;
				return res;
			}

			ulong value = 0;
			int shift = 0;
			while(carry >> shift != 0UL) {
				shift++;
				value <<= 1;
				carry <<= 1;
				carry += val >> 63;
				val <<= 1;
				if(carry < div) continue;
				carry -= div;
				value |= 1;
			}
			val >>= shift;
			int invShift = 64 - shift;
			val |= carry << invShift;
			return value << invShift;
		}

		internal void RoundToNextPowerOf2() {
			for(int i = minIndex; i < maxIndex; i++) data[i] = 0;
			bool roundUp = minIndex != maxIndex;
			ulong d = data[maxIndex];
			if(roundUp && d.IsPowerOf2()) data[maxIndex] = d << 1;
			else data[maxIndex] = d.NextPowerOf2();
			if(data[maxIndex] == 0) {
				maxIndex++;
				EnsureCapacity();
				data[maxIndex] = 1;
			}
		}

		internal int Log2Ceil() {
			if(minIndex == maxIndex) return data[minIndex].Log2Ceil() + maxIndex * 64;
			return (data[maxIndex] + 1).Log2Ceil() + maxIndex * 64;
		}
	}
}
