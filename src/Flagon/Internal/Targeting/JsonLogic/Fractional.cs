using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Flagon.Internal.Targeting.JsonLogic;

/// <summary>
/// flagd <c>fractional</c>, bucketing generation v3 (flagd 0.18, <c>core/pkg/evaluator/fractional.go</c>): murmur3 x86
/// 32-bit (seed 0) over the deterministic CBOR encoding of the bucketing value (explicit first argument, else
/// <c>[flagKey, targetingKey]</c>), bucket <c>(hash * totalWeight) &gt;&gt; 32</c>. Every error yields null (DEFAULT).
/// </summary>
internal sealed class FractionalNode(Args args) : Node
{
    private const int StackWeights = 64;

    public override Value Eval(in Env env)
    {
        if (!args.Resolve(env, out var list) || list.Count < 1)
        {
            return Value.Null;
        }

        // A non-array first element is the explicit bucketing value; an array one is already a distribution.
        var input = Value.Null;
        var start = 0;
        if (args.Items is not { } items || !IsArrayShaped(items[0]))
        {
            input = list.Get(0, env);
            start = input.Kind == Kind.Array ? 0 : 1;
        }

        var count = list.Count - start;
        Span<int> weights = count <= StackWeights ? stackalloc int[count] : new int[count];
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            if (!TryDistribution(list, start + i, env, out _, out var weight))
            {
                return Value.Null;
            }

            weights[i] = weight;
            total += weight;
        }

        if (total > int.MaxValue)
        {
            return Value.Null;
        }

        uint hash;
        if (start == 1)
        {
            if (input.IsNull)
            {
                return Value.Null;
            }

            hash = Cbor.Hash(input);
        }
        else
        {
            if (!Values.TryGetMember(env.Context, Value.Root, TargetingContext.TargetingKeyName, out var key)
                || key.Kind != Kind.String || key.AsString.Length == 0)
            {
                return Value.Null;
            }

            hash = Cbor.HashPair(env.Context.FlagKey, key.AsString);
        }

        if (total == 0)
        {
            return Value.Null;
        }

        var bucket = ((ulong)hash * (ulong)total) >> 32;
        ulong rangeEnd = 0;
        for (var i = 0; i < count; i++)
        {
            rangeEnd += (ulong)weights[i];
            if (bucket < rangeEnd)
            {
                TryDistribution(list, start + i, env, out var variant, out _);
                return variant;
            }
        }

        return Value.Null;
    }

    private static bool IsArrayShaped(Node node) => node is ArrayNode || node is ConstNode { Value.Kind: Kind.Array };

    // One [variant, weight?] element: variant a string/bool/number/null; weight absent or null = 1, else a whole number
    // up to int.MaxValue, negatives clamped to 0.
    private bool TryDistribution(ArgList list, int index, in Env env, out Value variant, out int weight)
    {
        Value rawWeight;
        bool hasWeight;
        variant = Value.Null;
        weight = 0;
        if (args.Items?[index] is ArrayNode element)
        {
            if (element.Items.Length == 0)
            {
                return false;
            }

            variant = element.Items[0].Eval(env);
            hasWeight = element.Items.Length >= 2;
            rawWeight = hasWeight ? element.Items[1].Eval(env) : Value.Null;
        }
        else
        {
            var value = list.Get(index, env);
            var n = value.Kind == Kind.Array ? Values.Count(value) : 0;
            if (n == 0)
            {
                return false;
            }

            variant = Values.Item(value, 0);
            hasWeight = n >= 2;
            rawWeight = hasWeight ? Values.Item(value, 1) : Value.Null;
        }

        if (variant.Kind is Kind.Array or Kind.Object)
        {
            return false;
        }

        long w = 1;
        if (hasWeight && !rawWeight.IsNull)
        {
            if (rawWeight.Kind != Kind.Number)
            {
                return false;
            }

            var d = rawWeight.Number;
            w = (long)d;
            if (d != w || w > int.MaxValue)
            {
                return false;
            }
        }

        weight = (int)Math.Max(0, w);
        return true;
    }
}

/// <summary>RFC 8949 core deterministic CBOR (as fxamacker/cbor CoreDetEncOptions) into a reused buffer, then murmur3.</summary>
internal static class Cbor
{
    [ThreadStatic]
    private static byte[]? t_buffer;

    public static uint Hash(Value value)
    {
        var w = new Writer(t_buffer ??= new byte[256]);
        w.Write(value);
        t_buffer = w.Buffer;
        return Murmur3.Hash32(w.Written);
    }

    public static uint HashPair(string flagKey, string targetingKey)
    {
        var w = new Writer(t_buffer ??= new byte[256]);
        w.Head(4, 2);
        w.Text(flagKey);
        w.Text(targetingKey);
        t_buffer = w.Buffer;
        return Murmur3.Hash32(w.Written);
    }

    private struct Writer(byte[] buffer)
    {
        private int _length;

        public byte[] Buffer { get; private set; } = buffer;

        public readonly ReadOnlySpan<byte> Written => Buffer.AsSpan(0, _length);

        private Span<byte> Take(int n)
        {
            if (_length + n > Buffer.Length)
            {
                var grown = new byte[Math.Max(Buffer.Length * 2, _length + n)];
                Buffer.AsSpan(0, _length).CopyTo(grown);
                Buffer = grown;
            }

            var span = Buffer.AsSpan(_length, n);
            _length += n;
            return span;
        }

        public void Head(int major, ulong argument)
        {
            var m = (byte)(major << 5);
            if (argument < 24)
            {
                Take(1)[0] = (byte)(m | (byte)argument);
            }
            else if (argument <= byte.MaxValue)
            {
                var s = Take(2);
                s[0] = (byte)(m | 24);
                s[1] = (byte)argument;
            }
            else if (argument <= ushort.MaxValue)
            {
                var s = Take(3);
                s[0] = (byte)(m | 25);
                BinaryPrimitives.WriteUInt16BigEndian(s[1..], (ushort)argument);
            }
            else if (argument <= uint.MaxValue)
            {
                var s = Take(5);
                s[0] = (byte)(m | 26);
                BinaryPrimitives.WriteUInt32BigEndian(s[1..], (uint)argument);
            }
            else
            {
                var s = Take(9);
                s[0] = (byte)(m | 27);
                BinaryPrimitives.WriteUInt64BigEndian(s[1..], argument);
            }
        }

        public void Text(string s)
        {
            var n = Encoding.UTF8.GetByteCount(s);
            Head(3, (ulong)n);
            Encoding.UTF8.GetBytes(s, Take(n));
        }

        public void Write(Value v)
        {
            switch (v.Kind)
            {
                case Kind.Null:
                    Take(1)[0] = 0xf6;
                    break;
                case Kind.Bool:
                    Take(1)[0] = v.AsBool ? (byte)0xf5 : (byte)0xf4;
                    break;
                case Kind.Number:
                    Number(v.Number);
                    break;
                case Kind.String:
                    Text(v.AsString);
                    break;
                case Kind.Array:
                    var n = Values.Count(v);
                    Head(4, (ulong)n);
                    for (var i = 0; i < n; i++)
                    {
                        Write(Values.Item(v, i));
                    }

                    break;
                default:
                    // Map keys sorted bytewise by their encoding (core deterministic). Allocates; rare path.
                    var entries = Values.Members(v).Select(m =>
                    {
                        var key = new Writer(new byte[16]);
                        key.Text(m.Key);
                        var value = new Writer(new byte[16]);
                        value.Write(m.Value);
                        return (Key: key.Written.ToArray(), Value: value.Written.ToArray());
                    }).ToList();
                    entries.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
                    Head(5, (ulong)entries.Count);
                    foreach (var (key, value) in entries)
                    {
                        key.CopyTo(Take(key.Length));
                        value.CopyTo(Take(value.Length));
                    }

                    break;
            }
        }

        // flagd normalises whole numbers to integers (major type 0/1); other floats take their shortest exact width.
        private void Number(double d)
        {
            if (double.IsFinite(d) && d == Math.Truncate(d))
            {
                if (d >= 0 && d < 18446744073709551616.0)
                {
                    Head(0, (ulong)d);
                    return;
                }

                if (d < 0 && d >= -9223372036854775808.0)
                {
                    Head(1, (ulong)(-1 - (long)d));
                    return;
                }
            }

            if (double.IsNaN(d) || (double)(Half)d == d)
            {
                var s = Take(3);
                s[0] = 0xf9;
                BinaryPrimitives.WriteHalfBigEndian(s[1..], (Half)d);
            }
            else if ((double)(float)d == d)
            {
                var s = Take(5);
                s[0] = 0xfa;
                BinaryPrimitives.WriteSingleBigEndian(s[1..], (float)d);
            }
            else
            {
                var s = Take(9);
                s[0] = 0xfb;
                BinaryPrimitives.WriteDoubleBigEndian(s[1..], d);
            }
        }
    }
}

/// <summary>MurmurHash3 x86 32-bit, seed 0 (twmb/murmur3 <c>Sum32</c>).</summary>
internal static class Murmur3
{
    public static uint Hash32(ReadOnlySpan<byte> data)
    {
        const uint c1 = 0xcc9e2d51, c2 = 0x1b873593;
        uint h = 0;
        var blocks = data.Length / 4;
        for (var i = 0; i < blocks; i++)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
            k = BitOperations.RotateLeft(k * c1, 15) * c2;
            h = BitOperations.RotateLeft(h ^ k, 13) * 5 + 0xe6546b64;
        }

        var tail = data[(blocks * 4)..];
        uint t = 0;
        switch (tail.Length)
        {
            case 3:
                t ^= (uint)tail[2] << 16;
                goto case 2;
            case 2:
                t ^= (uint)tail[1] << 8;
                goto case 1;
            case 1:
                t ^= tail[0];
                h ^= BitOperations.RotateLeft(t * c1, 15) * c2;
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
