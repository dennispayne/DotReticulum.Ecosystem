using System.Buffers.Binary;
using System.Numerics;

namespace DotLXST;

/// <summary>The pinned Linux x86 NumPy long-double representation, not IEEE binary128.</summary>
internal static class RawExtendedFloat
{
    public static void Write(Span<byte> destination, float value)
    {
        destination.Clear();
        var bits = BitConverter.SingleToUInt32Bits(value);
        var fraction = bits & 0x7fffff;
        var exponent = (bits >> 23) & 0xff;
        var sign = (ushort)((bits >> 31) << 15);
        ulong significand;
        ushort extendedExponent;
        if (exponent == 0xff)
        {
            extendedExponent = 0x7fff;
            significand = 0x8000000000000000UL | ((ulong)fraction << 40);
            if (fraction != 0) significand |= 0x4000000000000000UL;
        }
        else if (exponent != 0)
        {
            extendedExponent = (ushort)(exponent - 127 + 16383);
            significand = (0x800000UL | fraction) << 40;
        }
        else if (fraction != 0)
        {
            var highest = BitOperations.Log2(fraction);
            extendedExponent = (ushort)(highest - 149 + 16383);
            significand = (ulong)fraction << (63 - highest);
        }
        else { extendedExponent = 0; significand = 0; }
        BinaryPrimitives.WriteUInt64LittleEndian(destination, significand);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)(sign | extendedExponent));
    }

    public static float Read(ReadOnlySpan<byte> source)
    {
        var significand = BinaryPrimitives.ReadUInt64LittleEndian(source);
        var descriptor = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]);
        var sign = (uint)(descriptor >> 15) << 31;
        var exponent = descriptor & 0x7fff;
        if (exponent == 0x7fff)
            return BitConverter.UInt32BitsToSingle(sign | (significand == 0x8000000000000000UL
                ? 0x7f800000U : 0x7fc00000U | (uint)(significand >> 40) & 0x7fffff));
        if (exponent != 0 && (significand & 0x8000000000000000UL) == 0)
            throw new FormatException("Invalid x86 extended-precision significand.");
        if (significand == 0) return BitConverter.UInt32BitsToSingle(sign);
        var binaryScale = (exponent == 0 ? 1 : exponent) - 16383 - 63;
        var highestExponent = BitOperations.Log2(significand) + binaryScale;
        if (highestExponent > 127) return BitConverter.UInt32BitsToSingle(sign | 0x7f800000);
        var targetScale = Math.Max(highestExponent - 23, -149);
        var shift = targetScale - binaryScale;
        ulong rounded;
        if (shift > 64) rounded = 0;
        else if (shift == 64) rounded = significand > 0x8000000000000000UL ? 1UL : 0UL;
        else if (shift > 0)
        {
            rounded = significand >> shift;
            var remainder = significand & ((1UL << shift) - 1);
            var halfway = 1UL << (shift - 1);
            if (remainder > halfway || (remainder == halfway && (rounded & 1) != 0)) rounded++;
        }
        else rounded = significand << -shift;
        var value = MathF.ScaleB(rounded, targetScale);
        return sign == 0 ? value : -value;
    }
}
