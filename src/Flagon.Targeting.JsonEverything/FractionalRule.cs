using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Logic;

namespace Flagon.Targeting.JsonEverything;

/// <summary>
/// flagd's <c>fractional</c>, bucketing generation v3 (flagd 0.18): MurmurHash3 x86 32-bit (seed 0) over the RFC 8949
/// core-deterministic CBOR encoding of the bucketing value (an explicit first argument that is not an array, else
/// <c>[$flagd.flagKey, targetingKey]</c>), mapped to <c>(hash * totalWeight) &gt;&gt; 32</c> over the weighted buckets.
/// Any invalid input yields null.
/// </summary>
internal sealed class FractionalRule : IRule
{
    public JsonNode? Apply(JsonNode? args, Json.Logic.EvaluationContext context)
    {
        if (FlagdOperators.EvaluateArguments(args, context) is not { Count: > 0 } values || values[0] is null)
        {
            return null;
        }

        byte[] bucketingBytes;
        var first = 0;
        if (values[0] is not JsonArray)
        {
            bucketingBytes = Cbor.Encode(values[0]);
            first = 1;
        }
        else
        {
            if (!context.TryFind("targetingKey", out var key) || FlagdOperators.AsString(key) is not { Length: > 0 } targetingKey)
            {
                return null;
            }

            context.TryFind("$flagd.flagKey", out var flagKey);
            bucketingBytes = Cbor.Encode(new JsonArray(JsonValue.Create(FlagdOperators.AsString(flagKey) ?? string.Empty), JsonValue.Create(targetingKey)));
        }

        var buckets = new List<(JsonNode? Variant, long Weight)>(values.Count - first);
        long total = 0;
        for (var i = first; i < values.Count; i++)
        {
            if (values[i] is not JsonArray { Count: > 0 } bucket)
            {
                return null;
            }

            var variant = bucket[0];
            if (variant is not (null or JsonValue))
            {
                return null; // a variant must be a string, bool, number or null
            }

            long weight = 1;
            if (bucket.Count >= 2 && bucket[1] is not null)
            {
                if (bucket[1] is not JsonValue w || !JsonNumbers.TryGetDouble(w, out var d) || d != Math.Truncate(d))
                {
                    return null;
                }

                weight = (long)d;
                if (weight > int.MaxValue)
                {
                    return null;
                }

                weight = Math.Max(weight, 0);
            }

            total += weight;
            buckets.Add((variant, weight));
        }

        if (total > int.MaxValue || total == 0)
        {
            return null;
        }

        var bucketValue = ((ulong)Murmur3.Hash32(bucketingBytes) * (ulong)total) >> 32;
        ulong rangeEnd = 0;
        foreach (var (variant, weight) in buckets)
        {
            rangeEnd += (ulong)weight;
            if (bucketValue < rangeEnd)
            {
                return variant?.DeepClone();
            }
        }

        return null;
    }
}

/// <summary>RFC 8949 core-deterministic CBOR for JSON values, matching fxamacker/cbor <c>CoreDetEncOptions</c> after flagd's number normalisation.</summary>
internal static class Cbor
{
    public static byte[] Encode(JsonNode? node)
    {
        var buffer = new List<byte>(32);
        Write(buffer, node);
        return buffer.ToArray();
    }

    private static void Write(List<byte> buffer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                buffer.Add(0xf6);
                break;
            case JsonArray array:
                WriteHead(buffer, 4, (ulong)array.Count);
                foreach (var item in array)
                {
                    Write(buffer, item);
                }

                break;
            case JsonObject obj:
                var entries = obj.Select(p => (Key: EncodeText(p.Key), p.Value)).ToList();
                entries.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
                WriteHead(buffer, 5, (ulong)entries.Count);
                foreach (var (key, value) in entries)
                {
                    buffer.AddRange(key);
                    Write(buffer, value);
                }

                break;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        buffer.AddRange(EncodeText(value.GetValue<string>()));
                        break;
                    case JsonValueKind.True:
                        buffer.Add(0xf5);
                        break;
                    case JsonValueKind.False:
                        buffer.Add(0xf4);
                        break;
                    default:
                        if (!JsonNumbers.TryGetDouble(value, out var d))
                        {
                            buffer.Add(0xf6);
                            break;
                        }

                        WriteNumber(buffer, d);
                        break;
                }

                break;
        }
    }

    private static byte[] EncodeText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var buffer = new List<byte>(bytes.Length + 9);
        WriteHead(buffer, 3, (ulong)bytes.Length);
        buffer.AddRange(bytes);
        return buffer.ToArray();
    }

    // flagd's normalizeValue: whole-valued floats in [0, 2^64) are unsigned ints, in [-2^63, 0) negative ints;
    // anything else is a float in its shortest lossless width.
    private static void WriteNumber(List<byte> buffer, double d)
    {
        if (!double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Truncate(d))
        {
            if (d >= 0 && d < 18446744073709551616.0)
            {
                WriteHead(buffer, 0, (ulong)d);
                return;
            }

            if (d < 0 && d >= long.MinValue)
            {
                WriteHead(buffer, 1, (ulong)(-1 - (long)d));
                return;
            }
        }

        if (double.IsNaN(d))
        {
            buffer.AddRange([0xf9, 0x7e, 0x00]);
            return;
        }

        var half = (Half)d;
        if ((double)half == d)
        {
            buffer.Add(0xf9);
            Span<byte> h = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(h, BitConverter.HalfToUInt16Bits(half));
            buffer.AddRange(h.ToArray());
            return;
        }

        var single = (float)d;
        if ((double)single == d)
        {
            buffer.Add(0xfa);
            Span<byte> s = stackalloc byte[4];
            BinaryPrimitives.WriteSingleBigEndian(s, single);
            buffer.AddRange(s.ToArray());
            return;
        }

        buffer.Add(0xfb);
        Span<byte> f = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(f, d);
        buffer.AddRange(f.ToArray());
    }

    private static void WriteHead(List<byte> buffer, int major, ulong value)
    {
        var type = (byte)(major << 5);
        switch (value)
        {
            case < 24:
                buffer.Add((byte)(type | (byte)value));
                break;
            case <= byte.MaxValue:
                buffer.Add((byte)(type | 24));
                buffer.Add((byte)value);
                break;
            case <= ushort.MaxValue:
                buffer.Add((byte)(type | 25));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
                break;
            case <= uint.MaxValue:
                buffer.Add((byte)(type | 26));
                for (var shift = 24; shift >= 0; shift -= 8) buffer.Add((byte)(value >> shift));
                break;
            default:
                buffer.Add((byte)(type | 27));
                for (var shift = 56; shift >= 0; shift -= 8) buffer.Add((byte)(value >> shift));
                break;
        }
    }
}

/// <summary>MurmurHash3 x86 32-bit, seed 0 (as <c>twmb/murmur3.Sum32</c>).</summary>
internal static class Murmur3
{
    public static uint Hash32(ReadOnlySpan<byte> data)
    {
        const uint c1 = 0xcc9e2d51, c2 = 0x1b873593;
        uint h = 0;
        var blocks = data.Length / 4;
        for (var i = 0; i < blocks; i++)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4, 4));
            k *= c1;
            k = uint.RotateLeft(k, 15);
            k *= c2;
            h ^= k;
            h = uint.RotateLeft(h, 13);
            h = (h * 5) + 0xe6546b64;
        }

        var tail = data[(blocks * 4)..];
        uint k1 = 0;
        switch (tail.Length)
        {
            case 3:
                k1 ^= (uint)tail[2] << 16;
                goto case 2;
            case 2:
                k1 ^= (uint)tail[1] << 8;
                goto case 1;
            case 1:
                k1 ^= tail[0];
                k1 *= c1;
                k1 = uint.RotateLeft(k1, 15);
                k1 *= c2;
                h ^= k1;
                break;
        }

        h ^= (uint)data.Length;
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }
}
