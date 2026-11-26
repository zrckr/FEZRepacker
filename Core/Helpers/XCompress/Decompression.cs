namespace XCompress
{
    using Common;
    using Decoding;
    using Extensions;

    public sealed class Decompressor
    {
        private readonly Stream _stream;

        private readonly Decoder _decoder;

        private readonly byte[] _header = new byte[4];

        private readonly byte[] _encodedRecord = new byte[ushort.MaxValue];

        private ArraySegment<byte> _record;

        private bool _finished;

        private Exception? _readError;

        public Decompressor(Stream stream, int windowSize)
        {
            if (stream is null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (!stream.CanRead)
            {
                throw new ArgumentException("Stream must be readable", nameof(stream));
            }

            _decoder = new Decoder(windowSize);
            _stream = stream;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer is null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (offset > buffer.Length || count > buffer.Length - offset)
            {
                throw new ArgumentException("Read range exceeds the buffer");
            }

            if (count == 0 || _finished)
            {
                return 0;
            }

            if (_readError is { } readError)
            {
                throw new IOException("Cannot continue after a failed read", readError);
            }

            if (_record.Count == 0)
            {
                try
                {
                    ReadNextRecord();
                }
                catch (Exception exception)
                {
                    _readError = exception;
                    throw;
                }

                if (_finished)
                {
                    return 0;
                }
            }

            var length = Math.Min(count, _record.Count);
            var data = _record.Array!;
            Array.Copy(data, _record.Offset, buffer, offset, length);
            _record = new ArraySegment<byte>(data, _record.Offset + length, _record.Count - length);
            return length;
        }

        private void ReadNextRecord()
        {
            var first = _stream.ReadByte();
            if (first < 0)
            {
                Finish();
                return;
            }

            int encodedSize;
            int decodedSize;
            if (first == 0xFF)
            {
                _stream.ReadExact(_header, 4);
                decodedSize = (_header[0] << 8) | _header[1];
                encodedSize = (_header[2] << 8) | _header[3];
            }
            else
            {
                _stream.ReadExact(_header, 1);
                encodedSize = (first << 8) | _header[0];
                decodedSize = Constants.RecordSize;
                if (encodedSize == 0)
                {
                    _stream.ReadExact(_header, 3);
                    if ((_header[0] | _header[1] | _header[2]) != 0 || _stream.ReadByte() != -1)
                    {
                        throw new InvalidDataException("Invalid stream trailer");
                    }

                    Finish();
                    return;
                }
            }

            if (encodedSize == 0 || decodedSize is < 1 or > Constants.RecordSize)
            {
                throw new InvalidDataException("Invalid record size");
            }

            _stream.ReadExact(_encodedRecord, encodedSize);
            _record = _decoder.ReadRecord(new ArraySegment<byte>(_encodedRecord, 0, encodedSize), decodedSize);
        }

        private void Finish()
        {
            _decoder.Finish();
            _finished = true;
        }
    }
}

namespace XCompress.Decoding
{
    using Common;
    using Huffman;
    using IO;
    using PreTree;

    internal struct CompressedBlock
    {
        public int Remaining { get; private set; }

        private readonly HuffmanTable _main;

        private readonly HuffmanTable _secondary;

        private readonly HuffmanTable? _aligned;

        private CompressedBlock(int remaining, HuffmanTable main, HuffmanTable secondary, HuffmanTable? aligned)
        {
            Remaining = remaining;
            _main = main;
            _secondary = secondary;
            _aligned = aligned;
        }

        public int DecodeToken(
            BitReader reader,
            DecoderOutput output,
            int recordRemaining,
            ref Registers registers)
        {
            var symbol = _main.ReadSymbol(reader);
            int count;

            if (symbol < Constants.LiteralCount)
            {
                output.WriteByte((byte)symbol);
                count = 1;
            }
            else
            {
                var candidate = ReadMatch(reader, symbol);
                if (candidate.Length > Math.Min(recordRemaining, Remaining))
                {
                    throw new InvalidDataException("LZX match exceeds the remaining block or record length");
                }

                var distance = candidate.Distance.Type switch
                {
                    DistanceType.R0 => registers.R0,
                    DistanceType.R1 => registers.R1,
                    DistanceType.R2 => registers.R2,
                    DistanceType.Value => candidate.Distance.Value,
                    _ => throw new ArgumentOutOfRangeException()
                };

                output.CopyMatch(distance, candidate.Length);

                registers = registers.Transform(candidate.Distance);
                count = candidate.Length;
            }

            Remaining -= count;
            return count;
        }

        private Match ReadMatch(BitReader reader, int symbol)
        {
            var matchSymbol = symbol - Constants.LiteralCount;
            var slot = new Slot(matchSymbol >> 3);
            var lengthCode = matchSymbol & 0b111;
            var length = 2 + lengthCode;

            if (lengthCode == 7)
            {
                length += _secondary.ReadSymbol(reader);
            }

            Distance distance;
            switch (slot.Value)
            {
                case 0:
                    distance = Distance.R0();
                    break;

                case 1:
                    distance = Distance.R1();
                    break;

                case 2:
                    distance = Distance.R2();
                    break;

                default:
                    {
                        var extraBits = (byte)slot.ExtraBits;
                        int offset;

                        if (_aligned is { } aligned && extraBits >= 3)
                        {
                            var high = (int)reader.ReadBits((byte)(extraBits - 3));
                            var low = aligned.ReadSymbol(reader);
                            offset = (high << 3) | low;
                        }
                        else
                        {
                            offset = (int)reader.ReadBits(extraBits);
                        }

                        // Recover the encoded distance from the slot, then remove the explicit-distance bias
                        var @base = (slot.Value - 2 * extraBits) << extraBits;
                        distance = Distance.FromValue(@base + offset - 2);
                        break;
                    }
            }

            return new Match(length, distance);
        }

        public static CompressedBlock ReadVerbatim(BitReader reader, int length, byte[] mains, byte[] secondaries)
        {
            return ReadBlock(reader, length, mains, secondaries, aligned: null);
        }

        public static CompressedBlock ReadAligned(BitReader reader, int length, byte[] mains, byte[] secondaries)
        {
            const int count = 8;

            var lengths = new byte[count];
            for (var i = 0; i < count; i++)
            {
                lengths[i] = (byte)reader.ReadBits(3);
            }

            var aligned = HuffmanTable.FromLengths(lengths);
            return ReadBlock(reader, length, mains, secondaries, aligned);
        }

        private static CompressedBlock ReadBlock(
            BitReader reader,
            int length,
            byte[] mainLengths,
            byte[] secondaryLengths,
            HuffmanTable? aligned)
        {
            const int count = Constants.LiteralCount;
            var literals = new ArraySegment<byte>(mainLengths, 0, count);
            var matches = new ArraySegment<byte>(mainLengths, count, mainLengths.Length - count);
            var seconds = new ArraySegment<byte>(secondaryLengths, 0, secondaryLengths.Length);

            var preTreeReader = new PreTreeReader(reader);
            preTreeReader.ReadCodeLengths(literals);
            preTreeReader.ReadCodeLengths(matches);
            preTreeReader.ReadCodeLengths(seconds);

            var main = HuffmanTable.FromLengths(mainLengths);
            var secondary = HuffmanTable.FromLengths(secondaryLengths);
            return new CompressedBlock(length, main, secondary, aligned);
        }
    }

    internal sealed class Decoder
    {
        private readonly DecoderOutput _output;

        private Registers _registers;

        private readonly byte[] _mainLengths;

        private readonly byte[] _secondaryLengths;

        private DecoderState _state;

        private CompressedBlock? _compressedBlock;

        private DecompressedBlock? _decompressedBlock;

        public Decoder(int windowSize)
        {
            if (windowSize is < (1 << 15) or > (1 << 21) || (windowSize & (windowSize - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize), "Invalid LZX window size");
            }

            var (lastSlot, _) = Distance.FromValue(windowSize - 3).ToSlot()!.Value;
            _state = DecoderState.StreamHeader;
            _output = new DecoderOutput(windowSize);
            _registers = new Registers();

            var mainSymbolCount = Constants.LiteralCount + 8 * (lastSlot.Value + 1);
            _mainLengths = new byte[mainSymbolCount];
            _secondaryLengths = new byte[Constants.SecondaryLiteralCount];
        }

        public ArraySegment<byte> ReadRecord(ArraySegment<byte> record, int decompressedSize)
        {
            if (decompressedSize is < 1 or > Constants.RecordSize)
            {
                throw new ArgumentOutOfRangeException(nameof(decompressedSize));
            }

            using var reader = new BitReader(new MemoryStream(record.Array!, record.Offset, record.Count, writable: false));
            _output.BeginRecord();

            while (decompressedSize != 0)
            {
                int count;
                switch (_state)
                {
                    case DecoderState.StreamHeader:
                        {
                            var intelE8 = reader.ReadBits(1);
                            if (intelE8 != 0)
                            {
                                throw new NotSupportedException("Intel E8 translation is unsupported");
                            }

                            _state = DecoderState.BlockHeader;
                            count = 0;
                            break;
                        }

                    case DecoderState.BlockHeader:
                        {
                            var blockType = (BlockType)reader.ReadBits(3);
                            var length = (int)reader.ReadBits(24);
                            if (length == 0)
                            {
                                throw new InvalidDataException("Invalid LZX block length");
                            }

                            switch (blockType)
                            {
                                case BlockType.Uncompressed:
                                    {
                                        _compressedBlock = null;
                                        _decompressedBlock = DecompressedBlock.Read(reader, length, ref _registers);
                                        _state = DecoderState.Uncompressed;
                                        break;
                                    }

                                case BlockType.Verbatim:
                                    {
                                        _compressedBlock = CompressedBlock.ReadVerbatim(reader, length, _mainLengths,
                                            _secondaryLengths);
                                        _decompressedBlock = null;
                                        _state = DecoderState.Compressed;
                                        break;
                                    }

                                case BlockType.Aligned:
                                    {
                                        _compressedBlock = CompressedBlock.ReadAligned(reader, length, _mainLengths,
                                            _secondaryLengths);
                                        _decompressedBlock = null;
                                        _state = DecoderState.Compressed;
                                        break;
                                    }

                                default:
                                    throw new InvalidDataException("Invalid LZX block type: " + blockType);
                            }

                            count = 0;
                            break;
                        }

                    case DecoderState.Uncompressed:
                        {
                            var block = _decompressedBlock!.Value;
                            count = block.CopyBytes(reader, _output, decompressedSize);
                            _decompressedBlock = block;

                            if (block.Remaining == 0)
                            {
                                _state = (block.Length % 2) == 0
                                    ? DecoderState.BlockHeader
                                    : DecoderState.UncompressedPadding;
                            }

                            break;
                        }

                    case DecoderState.UncompressedPadding:
                        {
                            // An odd raw block is padded only before the next block header
                            reader.ReadBytes(1);
                            _state = DecoderState.BlockHeader;
                            count = 0;
                            break;
                        }

                    case DecoderState.Compressed:
                        {
                            var block = _compressedBlock!.Value;
                            count = block.DecodeToken(reader, _output, decompressedSize, ref _registers);
                            _compressedBlock = block;

                            if (block.Remaining == 0)
                            {
                                _state = DecoderState.BlockHeader;
                            }

                            break;
                        }

                    default:
                        throw new InvalidOperationException();
                }

                decompressedSize -= count;
            }

            return _output.Record;
        }

        public void Finish()
        {
            if (_state is DecoderState.Uncompressed or DecoderState.Compressed)
            {
                throw new InvalidDataException("Stream ends inside an LZX block");
            }
        }
    }

    internal enum DecoderState
    {
        StreamHeader,
        BlockHeader,
        Uncompressed,
        UncompressedPadding,
        Compressed
    }

    internal sealed class DecoderOutput
    {
        public ArraySegment<byte> Record
        {
            get
            {
                return new ArraySegment<byte>(_record, 0, _recordLength);
            }
        }

        private readonly byte[] _window;

        private readonly int _windowMask;

        private readonly byte[] _record = new byte[Constants.RecordSize];

        private int _windowPosition;

        private int _historyLength;

        private int _recordLength;

        public DecoderOutput(int windowSize)
        {
            _window = new byte[windowSize];
            _windowMask = windowSize - 1;
        }

        public void BeginRecord()
        {
            _recordLength = 0;
        }

        public void WriteByte(byte value)
        {
            _record[_recordLength++] = value;
            _window[_windowPosition] = value;
            _windowPosition = (_windowPosition + 1) & _windowMask;
            if (_historyLength < _window.Length)
            {
                _historyLength++;
            }
        }

        public void WriteBytes(byte[] bytes)
        {
            foreach (var value in bytes)
            {
                WriteByte(value);
            }
        }

        public void CopyMatch(int distance, int length)
        {
            if (distance < 1 || distance > _historyLength)
            {
                throw new InvalidDataException("Invalid LZX match distance: " + distance);
            }

            // Each byte becomes available immediately, including within an overlapping match
            for (var i = 0; i < length; i++)
            {
                WriteByte(_window[(_windowPosition - distance) & _windowMask]);
            }
        }
    }

    internal struct DecompressedBlock
    {
        public int Length { get; }

        public int Remaining { get; private set; }

        private DecompressedBlock(int length, int remaining)
        {
            Length = length;
            Remaining = remaining;
        }

        public int CopyBytes(BitReader reader, DecoderOutput output, int recordRemaining)
        {
            var count = Math.Min(Remaining, recordRemaining);
            var raw = reader.ReadBytes(count);
            output.WriteBytes(raw);
            Remaining -= count;
            return count;
        }

        public static DecompressedBlock Read(BitReader reader, int length, ref Registers registers)
        {
            reader.AlignBeforeRaw();

            var bytes = reader.ReadBytes(12);
            registers = new Registers(
                ReadInt32(bytes, 0),
                ReadInt32(bytes, 4),
                ReadInt32(bytes, 8)
            );

            return new DecompressedBlock(length, remaining: length);
        }

        private static int ReadInt32(byte[] bytes, int offset)
        {
            return bytes[offset] |
                   (bytes[offset + 1] << 8) |
                   (bytes[offset + 2] << 16) |
                   (bytes[offset + 3] << 24);
        }
    }
}

namespace XCompress.Extensions
{
    internal static class StreamExtensions
    {
        public static void ReadExact(this Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Unable to read {buffer.Length} bytes: end of stream reached after {offset} bytes.");
                }

                offset += read;
            }
        }
    }
}

namespace XCompress.Huffman
{
    using IO;

    internal partial class HuffmanTable
    {
        private int? GetSymbol(HuffmanCode code)
        {
            for (var i = 0; i < _codes.Count; i++)
            {
                if (_codes[i] is { } candidate && candidate.Equals(code))
                {
                    return i;
                }
            }

            return null;
        }

        public int ReadSymbol(BitReader reader)
        {
            var value = 0u;
            for (byte bitLength = 1; bitLength <= MaxCodeLength; bitLength++)
            {
                value = (value << 1) | reader.ReadBits(1);
                if (GetSymbol(new HuffmanCode((ushort)value, bitLength)) is { } symbol)
                {
                    return symbol;
                }
            }

            throw new InvalidDataException("Invalid Huffman code");
        }
    }
}

namespace XCompress.IO
{
    using Extensions;

    internal sealed class BitReader : IDisposable
    {
        private readonly Stream _stream;

        private readonly byte[] _wordBytes = new byte[2];

        private ushort _word;

        private byte _remaining;

        private Exception? _readError;

        public BitReader(Stream stream)
        {
            if (!stream.CanRead)
            {
                throw new ArgumentException("Stream must be readable", nameof(stream));
            }

            _stream = stream;
        }

        public uint ReadBits(byte count)
        {
            if (count > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Cannot read more than 32 bits at once");
            }

            var value = 0u;
            while (count != 0)
            {
                if (_remaining == 0)
                {
                    ReadExact(_wordBytes);
                    _word = (ushort)(_wordBytes[0] | (_wordBytes[1] << 8));
                    _remaining = 16;
                }

                var take = Math.Min(count, _remaining);
                _remaining -= take;

                var mask = (1u << take) - 1;
                var chunk = ((uint)_word >> _remaining) & mask;
                value = (value << take) | chunk;
                count -= take;
            }

            return value;
        }

        public void AlignBeforeRaw()
        {
            if (_remaining == 0)
            {
                ReadExact(_wordBytes);
            }

            _remaining = 0;
        }

        public byte[] ReadBytes(int count)
        {
            if (_remaining != 0)
            {
                throw new IOException("Raw bytes require word alignment");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            var bytes = new byte[count];
            ReadExact(bytes);
            return bytes;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        private void ReadExact(byte[] buffer)
        {
            if (_readError is { } readError)
            {
                throw new IOException("Cannot continue after an incomplete bitstream read", readError);
            }

            try
            {
                _stream.ReadExact(buffer, buffer.Length);
            }
            catch (Exception exception)
            {
                _readError = exception;
                throw;
            }
        }
    }
}

namespace XCompress.PreTree
{
    using Huffman;
    using IO;

    internal sealed partial class PreTree
    {
        public static PreTree FromLengths(byte[] lengths)
        {
            if (lengths.Length != PreTreeSymbol.MaxCount)
            {
                throw new ArgumentException("A pretree must have exactly 20 code lengths", nameof(lengths));
            }

            return new PreTree(HuffmanTable.FromLengths(lengths));
        }
    }

    internal sealed class PreTreeReader
    {
        private readonly BitReader _reader;

        public PreTreeReader(BitReader reader)
        {
            _reader = reader;
        }

        public void ReadCodeLengths(ArraySegment<byte> lengths)
        {
            var tree = ReadTree();
            var values = lengths.Array ?? Array.Empty<byte>();
            var index = lengths.Offset;
            var end = index + lengths.Count;

            while (index < end)
            {
                var instruction = ReadInstruction(tree);
                if (instruction.Count > end - index)
                {
                    throw new InvalidDataException("Pretree run exceeds the code length segment");
                }

                var length = instruction.Delta is { } delta
                    ? (byte)((values[index] + Instruction.LengthModulus - delta) % Instruction.LengthModulus)
                    : (byte)0;

                var runEnd = index + instruction.Count;
                for (; index < runEnd; index++)
                {
                    values[index] = length;
                }
            }
        }

        private PreTree ReadTree()
        {
            var lengths = new byte[PreTreeSymbol.MaxCount];
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                lengths[symbol] = (byte)_reader.ReadBits(PreTree.CodeLengthBits);
            }

            try
            {
                return PreTree.FromLengths(lengths);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Invalid pretree Huffman table", exception);
            }
        }

        private Instruction ReadInstruction(PreTree tree)
        {
            Instruction instruction;

            var symbol = tree.Table.ReadSymbol(_reader);
            if (symbol == PreTreeSymbol.ShortZeroRun)
            {
                instruction = Instruction.ZeroRun(Instruction.MinRunLength + (int)_reader.ReadBits(4));
            }
            else if (symbol == PreTreeSymbol.LongZeroRun)
            {
                instruction = Instruction.ZeroRun(Instruction.LongZeroRunMinLength + (int)_reader.ReadBits(5));
            }
            else if (symbol == PreTreeSymbol.RepeatDelta)
            {
                var count = Instruction.MinRunLength + (int)_reader.ReadBits(1);
                var delta = tree.Table.ReadSymbol(_reader);
                if (delta >= Instruction.LengthModulus)
                {
                    throw new InvalidDataException("Invalid pretree length delta");
                }

                instruction = Instruction.FromDelta(count, (byte)delta);
            }
            else
            {
                instruction = Instruction.FromDelta(1, (byte)symbol);
            }

            return instruction;
        }
    }
}
