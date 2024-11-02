// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md


using System.Runtime.CompilerServices;

namespace Riptide.Utils
{
	internal static class CMath
	{
		internal static ushort Clamp(this ushort value, ushort min, ushort max) {
			if(value < min) return min;
			if(value > max) return max;
			return value;
		}

		/// <remarks>Rounds down and includes 0 as 0.</remarks>
		internal static byte Log2(this ulong value) {
			int bits = 0;
			for(int step = 32; step > 0; step >>= 1) {
				if(value < (1UL << step)) continue;
				value >>= step;
				bits += step;
			}
			return (byte)bits;
		}

		internal static bool IsPowerOf2(this ulong value) {
			if(value == 0) return false;
			return (value & (value - 1)) == 0;
		}

		internal static ulong GetMask(int bits) => (1UL << bits) - 1UL - (bits == 64).ToULong();

		internal static bool IsRealNumber(this float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);

		internal static unsafe uint ConvUInt(this float value) {
			int i = *(int*)&value;
			if(i < 0) i = int.MaxValue - i;
			return i.Conv();
		}

		internal static unsafe float ConvFloat(this uint value) {
			int i = value.Conv();
			if(i < 0) i = int.MaxValue - i;
			return *(float*)&i;
		}

		internal static bool IsRealNumber(this double value)
			=> !double.IsNaN(value) && !double.IsInfinity(value);

		internal static unsafe ulong ConvULong(this double value) {
			long l = *(long*)&value;
			if(l < 0) l = long.MaxValue - l;
			return l.Conv();
		}

		internal static unsafe double ConvDouble(this ulong value) {
			long l = value.Conv();
			if(l < 0) l = long.MaxValue - l;
			return *(double*)&l;
		}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static unsafe uint ToUInt(this float value) => *(uint*)&value;
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static unsafe float ToFloat(this uint value) => *(float*)&value;
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static unsafe ulong ToULong(this double value) => *(ulong*)&value;
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static unsafe double ToDouble(this ulong value) => *(double*)&value;
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static unsafe ulong ToULong(this bool value) => *(byte*)&value;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static byte Conv(this sbyte value) => (byte)(value + (1 << 7));
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static sbyte Conv(this byte value) => (sbyte)(value - (1 << 7));
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static ushort Conv(this short value) => (ushort)(value + (1 << 15));
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static short Conv(this ushort value) => (short)(value - (1 << 15));
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static uint Conv(this int value) => (uint)(value + (1 << 31));
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static int Conv(this uint value) => (int)value - (1 << 31);
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static ulong Conv(this long value) => (ulong)(value + (1L << 63));
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static long Conv(this ulong value) => (long)value - (1L << 63);
	}
}