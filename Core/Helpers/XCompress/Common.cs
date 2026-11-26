namespace XCompress.Common
{
    internal enum BlockType
    {
        Verbatim = 1,
        Aligned = 2,
        Uncompressed = 3
    }

    internal static class Constants
    {
        public const int RecordSize = 32 * 1024;

        public const int LiteralCount = 256;

        public const int SecondaryLiteralCount = 249;
    }

    internal readonly struct Distance : IEquatable<Distance>
    {
        private const int MaxExplicitDistance = (1 << 21) - 3;

        public readonly DistanceType Type;

        public int Value
        {
            get
            {
                if (Type != DistanceType.Value)
                {
                    throw new InvalidOperationException("Invalid distance type");
                }

                return _value;
            }
        }

        private readonly int _value;

        private Distance(DistanceType type, int value)
        {
            Type = type;
            _value = value;
        }

        public (Slot Slot, int Offset)? ToSlot()
        {
            switch (Type)
            {
                case DistanceType.R0:
                    return (new Slot(0), 0);

                case DistanceType.R1:
                    return (new Slot(1), 0);

                case DistanceType.R2:
                    return (new Slot(2), 0);

                case DistanceType.Value:
                    {
                        if (_value < 1 || _value > MaxExplicitDistance)
                        {
                            return null;
                        }

                        var encoded = _value + 2;
                        var extraBits = Math.Min((int)Math.Log(encoded, 2) - 1, Slot.MaxExtraBits);
                        var slot = new Slot(2 * extraBits + (encoded >> extraBits));
                        var offset = encoded & ((1 << extraBits) - 1);
                        return (slot, offset);
                    }

                default:
                    throw new InvalidOperationException("Invalid distance type");
            }
        }

        public bool Equals(Distance other)
        {
            return Type == other.Type && _value == other._value;
        }

        public override bool Equals(object? obj)
        {
            return obj is Distance other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Type * 397) ^ _value;
            }
        }

        public static Distance R0()
        {
            return new Distance(DistanceType.R0, 0);
        }

        public static Distance R1()
        {
            return new Distance(DistanceType.R1, 0);
        }

        public static Distance R2()
        {
            return new Distance(DistanceType.R2, 0);
        }

        public static Distance FromValue(int value)
        {
            return new Distance(DistanceType.Value, value);
        }
    }

    internal enum DistanceType
    {
        R0,
        R1,
        R2,
        Value
    }

    internal readonly struct Match : IEquatable<Match>
    {
        private const int MinLength = 2;

        private const int MaxLength = 257;

        public readonly Distance Distance;

        public int Length
        {
            get
            {
                if (field == 0)
                {
                    throw new InvalidOperationException("Match is uninitialized");
                }

                return field;
            }
        }

        public Match(int length, Distance distance)
        {
            if (length is < MinLength or > MaxLength)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Match length must be between 2 and 257");
            }

            Length = length;
            Distance = distance;
        }

        public Symbol GetSymbolForSlot(Slot slot)
        {
            var lengthCode = Math.Min(Length - MinLength, 7);
            return new Symbol(256 + 8 * slot.Value + lengthCode);
        }

        public bool Equals(Match other)
        {
            return Length.Equals(other.Length) && Distance.Equals(other.Distance);
        }

        public override bool Equals(object? obj)
        {
            return obj is Match other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Length.GetHashCode() * 397) ^ Distance.GetHashCode();
            }
        }
    }

    internal readonly struct Registers
    {
        public readonly int R0;

        public readonly int R1;

        public readonly int R2;

        public Registers(int r0, int r1, int r2)
        {
            R0 = r0;
            R1 = r1;
            R2 = r2;
        }

        public Registers() : this(1, 1, 1) { }

        public Registers Transform(Distance distance)
        {
            return distance.Type switch
            {
                DistanceType.R0 => new Registers(R0, R1, R2),
                DistanceType.R1 => new Registers(R1, R0, R2),
                DistanceType.R2 => new Registers(R2, R1, R0),
                DistanceType.Value => new Registers(distance.Value, R0, R1),
                _ => throw new InvalidOperationException("Invalid distance type")
            };
        }
    }

    internal readonly struct Slot : IEquatable<Slot>
    {
        public const int MaxExtraBits = 17;

        public readonly int Value;

        public int ExtraBits
        {
            get
            {
                return Math.Min(Math.Max(0, Value / 2 - 1), MaxExtraBits);
            }
        }

        public Slot(int value)
        {
            Value = value;
        }

        public bool Equals(Slot other)
        {
            return Value == other.Value;
        }

        public override bool Equals(object? obj)
        {
            return obj is Slot other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value;
        }
    }

    internal readonly struct Symbol : IEquatable<Symbol>
    {
        public readonly ushort Value;

        public Symbol(int value)
        {
            Value = checked((ushort)value);
        }

        public bool Equals(Symbol other)
        {
            return Value == other.Value;
        }

        public override bool Equals(object? obj)
        {
            return obj is Symbol other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }
    }
}

namespace XCompress.Huffman
{
    internal readonly struct HuffmanCode : IEquatable<HuffmanCode>
    {
        public readonly ushort Value;

        public readonly byte BitLength;

        public HuffmanCode(ushort value, byte bitLength)
        {
            Value = value;
            BitLength = bitLength;
        }

        public bool Equals(HuffmanCode other)
        {
            return Value == other.Value && BitLength == other.BitLength;
        }

        public override bool Equals(object? obj)
        {
            return obj is HuffmanCode other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Value.GetHashCode() * 397) ^ BitLength.GetHashCode();
            }
        }
    }

    internal partial class HuffmanTable
    {
        private const int MaxCodeLength = 16;
        
        private readonly List<HuffmanCode?> _codes;

        private HuffmanTable(List<HuffmanCode?> codes)
        {
            _codes = codes;
        }

        public HuffmanCode? GetCode(int symbol)
        {
            return _codes[symbol];
        }

        public static HuffmanTable FromLengths(params byte[] lengths)
        {
            var counts = new int[MaxCodeLength + 1];
            foreach (var bitLength in lengths)
            {
                if (bitLength > MaxCodeLength)
                {
                    throw new ArgumentException("Huffman code exceeds 16 bits");
                }

                if (bitLength != 0)
                {
                    counts[bitLength]++;
                }
            }

            var nextCode = new int[MaxCodeLength + 1];
            var firstUsed = 0;
            for (var bitLength = 1; bitLength <= MaxCodeLength; bitLength++)
            {
                var first = firstUsed << 1;
                if (counts[bitLength] > (1 << bitLength) - first)
                {
                    throw new ArgumentException("Huffman code lengths oversubscribe the code space");
                }

                nextCode[bitLength] = first;
                firstUsed = first + counts[bitLength];
            }

            var codes = new List<HuffmanCode?>(lengths.Length);
            foreach (var bitLength in lengths)
            {
                if (bitLength == 0)
                {
                    codes.Add(null);
                    continue;
                }

                // Visiting equal-length symbols in index order makes the code canonical
                int index = bitLength;
                codes.Add(new HuffmanCode((ushort)nextCode[index], bitLength));
                nextCode[index]++;
            }

            return new HuffmanTable(codes);
        }
    }
}

namespace XCompress.PreTree
{
    using Huffman;

    internal readonly struct Instruction
    {
        public const int LengthModulus = 17;

        public const int MinRunLength = 4;

        public const int LongZeroRunMinLength = 20;

        public const int MaxZeroRunLength = 51;

        public const int MaxDeltaRunLength = 5;

        public readonly int Count;

        public readonly byte? Delta;

        public int Symbol
        {
            get
            {
                if (Count == 0)
                {
                    throw new InvalidOperationException("Instruction is uninitialized");
                }

                return Delta switch
                {
                    null when Count < LongZeroRunMinLength => PreTreeSymbol.ShortZeroRun,
                    null => PreTreeSymbol.LongZeroRun,
                    var delta when Count == 1 => delta.Value,
                    _ => PreTreeSymbol.RepeatDelta
                };
            }
        }

        private Instruction(int count, byte? delta)
        {
            Count = count;
            Delta = delta;
        }

        public static Instruction ZeroRun(int count)
        {
            if (count is < MinRunLength or > MaxZeroRunLength)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            return new Instruction(count, null);
        }

        public static Instruction FromDelta(int count, byte delta)
        {
            if (count != 1 && (count < MinRunLength || count > MaxDeltaRunLength))
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (delta >= LengthModulus)
            {
                throw new ArgumentOutOfRangeException(nameof(delta));
            }

            return new Instruction(count, delta);
        }
    }

    internal sealed partial class PreTree
    {
        public const byte CodeLengthBits = 4;

        public readonly HuffmanTable Table;

        private PreTree(HuffmanTable table)
        {
            for (var symbol = 0; symbol < PreTreeSymbol.MaxCount; symbol++)
            {
                if (table.GetCode(symbol) is { BitLength: >= 1 << CodeLengthBits })
                {
                    throw new ArgumentException("Pretree code lengths must fit in four bits");
                }
            }

            Table = table;
        }
    }

    internal static class PreTreeSymbol
    {
        public const int ShortZeroRun = 17;

        public const int LongZeroRun = 18;

        public const int RepeatDelta = 19;

        public const int MaxCount = 20;
    }
}
