namespace XCompress
{
    using Common;
    using Encoding;

    public sealed class Compressor
    {
        private const int TokenBufferLimit = 0xFFF8;

        private const int MatchBufferLimit = 0x7FF8;

        private const int FirstReparseTokenLimit = 0xFE00;

        private const int FirstReparseMatchLimit = 0x7E00;

        private const int InitialCostRefresh = 10_000;

        private const int CostRefreshInterval = 4_096;

        private readonly int _windowSize;

        private readonly int _partitionSize;

        private readonly int _mainSymbolCount;

        public Compressor(int windowSize, int partitionSize)
        {
            if (windowSize is < (1 << 15) or > (1 << 21) || (windowSize & (windowSize - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize), "Invalid window size");
            }

            if (partitionSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(partitionSize));
            }

            _windowSize = windowSize;
            _partitionSize = partitionSize;
            var (lastSlot, _) = Distance.FromValue(windowSize - 3).ToSlot()!.Value;
            _mainSymbolCount = Constants.LiteralCount + 8 * (lastSlot.Value + 1);
        }

        public void Compress(byte[] input, int inputLength, Stream output)
        {
            if (inputLength == 0)
            {
                return;
            }

            var tokenizer = new Tokenizer(input, inputLength, _windowSize, _partitionSize, _mainSymbolCount);
            using var encoder = new Encoder(input, inputLength, _mainSymbolCount, output);

            var selector = new TokenPrefixSelector(_mainSymbolCount);
            var pending = new PendingBlock(InitialCostRefresh);
            var firstPartitionEnd = (long)_windowSize + _partitionSize;
            var reparsed = false;

            while (tokenizer.TryPrepareBatch(out var direct, out var path))
            {
                if (path == null)
                {
                    var isMatch = direct.Type == TokenType.Match;
                    if (isMatch)
                    {
                        pending.Matches++;
                    }

                    tokenizer.Commit(direct);
                    if (tokenizer.Tokens.Count - pending.FirstToken >= TokenBufferLimit ||
                        (isMatch && pending.Matches >= MatchBufferLimit))
                    {
                        EndPendingBlock(tokenizer, encoder, selector, ref pending);
                    }
                }
                else
                {
                    while (tokenizer.Tokens.Count - pending.FirstToken + path.Count >= TokenBufferLimit ||
                           pending.Matches + path.Count >= MatchBufferLimit)
                    {
                        EndPendingBlock(tokenizer, encoder, selector, ref pending);
                    }

                    foreach (var token in path)
                    {
                        if (token.Type == TokenType.Match)
                        {
                            pending.Matches++;
                        }

                        tokenizer.Commit(token);
                    }
                }

                var tokenEnd = tokenizer.Tokens.Count;
                var pendingCount = tokenEnd - pending.FirstToken;
                if (path != null && pendingCount >= pending.RefreshAt)
                {
                    tokenizer.RefreshEstimates(pending.FirstToken, tokenEnd);
                    pending.RefreshAt += CostRefreshInterval;
                }

                var earlyReparse = path != null &&
                                   (pendingCount >= FirstReparseTokenLimit || pending.Matches >= FirstReparseMatchLimit);

                var reparseDue = tokenizer.Cursor % Constants.RecordSize == 0
                    ? tokenizer.Cursor >= firstPartitionEnd
                    : tokenizer.Cursor == inputLength;

                if (!reparsed && pending.FirstToken == 0 && (earlyReparse || reparseDue))
                {
                    var count = selector.FindPrefix(tokenizer.Tokens, 0);
                    tokenizer.RebuildEstimates(0, count);
                    tokenizer.Rewind();
                    pending.Matches = 0;
                    pending.RefreshAt = count;
                    reparsed = true;
                }

                if (tokenizer.Cursor == Constants.RecordSize && !tokenizer.HasEstimates)
                {
                    tokenizer.EnableEstimates();
                }
            }

            while (pending.FirstToken < tokenizer.Tokens.Count)
            {
                pending.FirstToken += WriteBlockPrefix(encoder, selector, tokenizer.Tokens, pending.FirstToken);
            }

            encoder.Complete();
        }

        private static void EndPendingBlock(
            Tokenizer tokenizer,
            Encoder encoder,
            TokenPrefixSelector selector,
            ref PendingBlock pending)
        {
            var tokens = tokenizer.Tokens;
            var end = pending.FirstToken + WriteBlockPrefix(encoder, selector, tokens, pending.FirstToken);
            for (var i = pending.FirstToken; i < end; i++)
            {
                if (tokens[i].Type == TokenType.Match)
                {
                    pending.Matches--;
                }
            }

            tokenizer.RebuildEstimates(pending.FirstToken, end);
            pending.FirstToken = end;
            var remaining = tokens.Count - end;
            pending.RefreshAt = remaining < CostRefreshInterval ? CostRefreshInterval : remaining + CostRefreshInterval;
        }

        private static int WriteBlockPrefix(
            Encoder encoder,
            TokenPrefixSelector selector,
            IReadOnlyList<Token> tokens,
            int start)
        {
            var count = selector.FindPrefix(tokens, start);
            var position = encoder.Decoded;
            encoder.WriteBlock(new PreparedBlock(tokens, start, count, encoder.MainSymbolCount));

            // A new record renews the allowance for distribution-based splits
            if (position / Constants.RecordSize != encoder.Decoded / Constants.RecordSize)
            {
                selector.Reset();
            }

            return count;
        }

        private struct PendingBlock
        {
            public int FirstToken;

            public int Matches;

            public int RefreshAt;

            public PendingBlock(int refreshAt)
            {
                FirstToken = 0;
                Matches = 0;
                RefreshAt = refreshAt;
            }
        }
    }
}

namespace XCompress.Common
{
    internal struct BlockHeader
    {
        public readonly BlockType Type;

        public readonly int DecompressedSize;

        public BlockHeader(BlockType type, int decompressedSize)
        {
            Type = type;
            DecompressedSize = decompressedSize;
        }
    }

    internal readonly struct Token : IEquatable<Token>
    {
        public readonly TokenType Type;

        public byte Literal
        {
            get
            {
                if (Type != TokenType.Literal)
                {
                    throw new InvalidOperationException("Invalid token type");
                }

                return _literal;
            }
        }

        public Match Match
        {
            get
            {
                if (Type != TokenType.Match)
                {
                    throw new InvalidOperationException("Invalid token type");
                }

                return _match;
            }
        }

        public Symbol Symbol
        {
            get
            {
                switch (Type)
                {
                    case TokenType.Literal:
                        return new Symbol(_literal);

                    case TokenType.Match:
                        {
                            var slot = _match.Distance.ToSlot();
                            if (!slot.HasValue)
                            {
                                throw new InvalidOperationException("distance is outside the LZX slot range");
                            }

                            return _match.GetSymbolForSlot(slot.Value.Slot);
                        }

                    default:
                        throw new InvalidOperationException("Invalid token type");
                }
            }
        }

        public int Length
        {
            get
            {
                return Type switch
                {
                    TokenType.Literal => 1,
                    TokenType.Match => _match.Length,
                    _ => throw new InvalidOperationException("Invalid token type")
                };
            }
        }

        private readonly byte _literal;

        private readonly Match _match;

        private Token(TokenType type, byte literal, Match match)
        {
            Type = type;
            _literal = literal;
            _match = match;
        }

        public static Token FromLiteral(byte literal)
        {
            return new Token(TokenType.Literal, literal, default);
        }

        public static Token FromMatch(Match match)
        {
            return new Token(TokenType.Match, 0, match);
        }

        public bool Equals(Token other)
        {
            return Type == other.Type && _literal == other._literal && _match.Equals(other._match);
        }

        public override bool Equals(object? obj)
        {
            return obj is Token other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (int)Type;
                hashCode = (hashCode * 397) ^ _literal.GetHashCode();
                hashCode = (hashCode * 397) ^ _match.GetHashCode();
                return hashCode;
            }
        }
    }

    internal enum TokenType
    {
        Literal,
        Match
    }
}

namespace XCompress.Encoding
{
    using Common;
    using Huffman;
    using IO;
    using PreTree;

    internal sealed class Encoder : IDisposable
    {
        public int Decoded
        {
            get
            {
                return _records.Decoded;
            }
        }

        public int MainSymbolCount
        {
            get
            {
                return _previousMain.Length;
            }
        }

        private readonly byte[] _input;

        private readonly int _inputLength;

        private readonly RecordWriter _records;

        private readonly byte[] _previousMain;

        private readonly byte[] _previousLength = new byte[Constants.SecondaryLiteralCount];

        private Registers _registers = new();

        private bool _padAfterRawBlock;

        public Encoder(byte[] input, int inputLength, int mainSymbolCount, Stream output)
        {
            _input = input;
            _inputLength = inputLength;
            _records = new RecordWriter(output, inputLength);
            _previousMain = new byte[mainSymbolCount];
        }

        public void WriteBlock(PreparedBlock block)
        {
            var length = block.Header.DecompressedSize;
            if (length > _inputLength - Decoded)
            {
                throw new InvalidOperationException("Block exceeds the compression input");
            }

            if (block.Header.Type != BlockType.Uncompressed)
            {
                CheckRecordBoundaries(block);
            }

            if (_padAfterRawBlock)
            {
                _records.Bits.WriteBytes(0);
            }

            block.WriteHeader(_records.Bits, _previousMain, _previousLength);
            if (block.Header.Type == BlockType.Uncompressed)
            {
                WriteRawBlock(block);
            }
            else
            {
                _padAfterRawBlock = false;
                for (var i = 0; i < block.Count; i++)
                {
                    var token = block.GetToken(i);
                    block.WriteToken(_records.Bits, token);
                    if (token.Type == TokenType.Match)
                    {
                        _registers = _registers.Transform(token.Match.Distance);
                    }

                    _records.Advance(token.Length);
                }
            }
        }

        public void Complete()
        {
            _records.Complete();
        }

        public void Dispose()
        {
            _records.Dispose();
        }

        private void CheckRecordBoundaries(PreparedBlock block)
        {
            var position = Constants.RecordSize - _records.RecordRemaining;
            for (var i = 0; i < block.Count; i++)
            {
                position += block.GetToken(i).Length;
                if (position > Constants.RecordSize)
                {
                    throw new InvalidOperationException("Token crosses the XMem record boundary");
                }

                if (position == Constants.RecordSize)
                {
                    position = 0;
                }
            }
        }

        private void WriteRawBlock(PreparedBlock block)
        {
            for (var i = 0; i < block.Count; i++)
            {
                var token = block.GetToken(i);
                if (token.Type == TokenType.Match)
                {
                    _registers = _registers.Transform(token.Match.Distance);
                }
            }

            _records.Bits.AlignBeforeRaw();
            WriteRegister(_registers.R0);
            WriteRegister(_registers.R1);
            WriteRegister(_registers.R2);

            var remaining = block.Header.DecompressedSize;
            while (remaining != 0)
            {
                var count = Math.Min(remaining, _records.RecordRemaining);
                _records.Bits.WriteBytes(_input, Decoded, count);
                _records.Advance(count);
                remaining -= count;
            }

            _padAfterRawBlock = (block.Header.DecompressedSize & 1) != 0;
        }

        private void WriteRegister(int value)
        {
            _records.Bits.WriteBytes((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
        }
    }

    internal sealed class MatchFinder
    {
        public const int MinimumMatchLength = 2;

        public const int LongMatchThreshold = 50;

        private const int MaximumMatchLength = 257;

        private const int WindowSearchMargin = 4;

        public int Submitted { get; private set; }

        private readonly int[] _roots = new int[1 << 16];

        private readonly TreeNode[] _children;

        private readonly int _childMask;

        private readonly int _windowSize;

        private readonly int _partitionSize;

        private readonly int _inputLength;

        private readonly Distance[] _distances = new Distance[MaximumMatchLength + 1];

        private byte[] _memory = Array.Empty<byte>();

        private int _base;

        public MatchFinder(int inputLength, int windowSize, int partitionSize)
        {
            _inputLength = inputLength;
            _windowSize = windowSize;
            _partitionSize = partitionSize;
            var capacity = 2 * windowSize;
            _children = new TreeNode[Math.Min(capacity, inputLength)];
            _childMask = capacity - 1;
        }

        public void ClearTree()
        {
            Array.Clear(_roots, 0, _roots.Length);
            Array.Clear(_children, 0, _children.Length);
        }

        public void SubmitNext(byte[] input)
        {
            var start = Submitted;
            var end = start + Math.Min(Constants.RecordSize, _inputLength - start);
            if (start != 0)
            {
                var tail = Math.Max(start - LongMatchThreshold, _base);
                var oldest = Math.Max(0, start - _windowSize + LongMatchThreshold + WindowSearchMargin + 1);
                for (var position = start - 1; position >= tail; position--)
                {
                    Remove(position, oldest);
                }

                var retained = start - _base;
                if (_partitionSize != 0 && retained >= (long)_windowSize + _partitionSize)
                {
                    Array.Copy(_memory, _partitionSize, _memory, 0, retained - _partitionSize);
                    _base += _partitionSize;
                }
            }

            // Keep padded lookahead across compaction and overwrite it on submission.
            var required = checked(end - _base + MaximumMatchLength);
            if (_memory.Length < required)
            {
                var capacity = (int)Math.Min(int.MaxValue, Math.Max(required, 2L * _memory.Length));
                Array.Resize(ref _memory, capacity);
            }

            Array.Copy(input, start, _memory, start - _base, end - start);
            Submitted = end;

            if (start != 0)
            {
                var tail = Math.Max(start - LongMatchThreshold, _base);
                for (var position = tail; position < start; position++)
                {
                    QuickInsert(position);
                }
            }
        }

        public int Find(int position, Registers registers)
        {
            var longest = Insert(position, Oldest(position), findMatches: true);
            if (longest < MinimumMatchLength)
            {
                return 0;
            }

            var offset = position - _base;
            var repeatLength = MinimumMatchLength - 1;
            if (AddRepeat(offset, longest, registers.R0, Distance.R0(), ref repeatLength) <= LongMatchThreshold)
            {
                AddRepeat(offset, longest, registers.R1, Distance.R1(), ref repeatLength);
                AddRepeat(offset, longest, registers.R2, Distance.R2(), ref repeatLength);
            }

            var recordRemaining = Constants.RecordSize - 1 - position % Constants.RecordSize;
            longest = Math.Min(longest, Math.Min(Submitted - position, recordRemaining));
            return longest >= MinimumMatchLength ? longest : 0;
        }

        public Match GetMatch(int length)
        {
            return new Match(length, _distances[length]);
        }

        public void InsertSkipped(int position, Match candidate)
        {
            var length = candidate.Length;
            if (candidate.Distance.Type == DistanceType.Value && candidate.Distance.Value == 1 && length > 16)
            {
                length = 2;
            }

            for (var offset = 1; offset < length; offset++)
            {
                QuickInsert(position + offset);
            }
        }

        private int AddRepeat(int offset, int longest, int distance, Distance register, ref int repeatLength)
        {
            if (distance > offset)
            {
                return 0;
            }

            var length = CommonPrefix(offset - distance, offset, 0, longest);
            if (length > repeatLength)
            {
                for (var i = repeatLength + 1; i <= length; i++)
                {
                    _distances[i] = register;
                }

                repeatLength = length;
            }

            return length;
        }

        private int Oldest(int position)
        {
            return Math.Max(_base, position - _windowSize + WindowSearchMargin + 1);
        }

        private void QuickInsert(int position)
        {
            Insert(position, Oldest(position), findMatches: false);
        }

        private int Key(int offset)
        {
            return _memory[offset] | (_memory[offset + 1] << 8);
        }

        private int CommonPrefix(int source, int lookahead, int start, int limit)
        {
            var length = start;
            while (length < limit && _memory[source + length] == _memory[lookahead + length])
            {
                length++;
            }

            return length;
        }

        private int Insert(int position, int oldest, bool findMatches)
        {
            var offset = position - _base;
            var key = Key(offset);
            var index = position & _childMask;
            var candidate = _roots[key];

            // Store position plus one so that zero represents a missing link.
            _roots[key] = position + 1;
            if (candidate <= oldest)
            {
                _children[index] = default;
                return 0;
            }

            if (findMatches)
            {
                _distances[MinimumMatchLength] = Distance.FromValue(position - (candidate - 1));
            }

            var limit = findMatches ? MaximumMatchLength : LongMatchThreshold;
            var longest = MinimumMatchLength;
            var leftCommon = MinimumMatchLength;
            var rightCommon = MinimumMatchLength;
            var common = MinimumMatchLength;
            ref var leftLink = ref _children[index].Left;
            ref var rightLink = ref _children[index].Right;

            while (candidate > oldest)
            {
                var source = candidate - 1;
                var sourceOffset = source - _base;
                var sourceIndex = source & _childMask;
                var children = _children[sourceIndex];
                var length = CommonPrefix(sourceOffset, offset, common, limit);
                var goesLeft = length < limit && _memory[sourceOffset + length] < _memory[offset + length];
                ref var sideCommon = ref (goesLeft ? ref leftCommon : ref rightCommon);

                if (sideCommon < length)
                {
                    sideCommon = length;
                    if (longest < length)
                    {
                        if (findMatches)
                        {
                            var distance = Distance.FromValue(position - source);
                            for (var i = longest + 1; i <= length; i++)
                            {
                                _distances[i] = distance;
                            }
                        }

                        longest = length;
                        if (longest >= LongMatchThreshold)
                        {
                            leftLink = children.Left;
                            rightLink = children.Right;
                            return longest;
                        }
                    }

                    common = Math.Min(leftCommon, rightCommon);
                }

                if (goesLeft)
                {
                    rightLink = candidate;
                    rightLink = ref _children[sourceIndex].Left;
                    candidate = children.Left;
                }
                else
                {
                    leftLink = candidate;
                    leftLink = ref _children[sourceIndex].Right;
                    candidate = children.Right;
                }
            }

            leftLink = 0;
            rightLink = 0;
            return longest;
        }

        private void Remove(int position, int oldest)
        {
            var key = Key(position - _base);
            if (_roots[key] != position + 1)
            {
                return;
            }

            var index = position & _childMask;
            if (position < oldest)
            {
                _roots[key] = 0;
                _children[index] = default;
                return;
            }

            var left = _children[index].Left;
            var right = _children[index].Right;
            left = left > oldest ? left : 0;
            right = right > oldest ? right : 0;
            _children[index].Left = left;
            _children[index].Right = right;
            ref var link = ref _roots[key];

            while (true)
            {
                var useLeft = left > right;
                var candidate = useLeft ? left : right;
                link = candidate > oldest ? candidate : 0;
                if (link == 0)
                {
                    return;
                }

                var child = (candidate - 1) & _childMask;
                if (useLeft)
                {
                    link = ref _children[child].Right;
                    left = _children[child].Right;
                }
                else
                {
                    link = ref _children[child].Left;
                    right = _children[child].Left;
                }
            }
        }

        private struct TreeNode
        {
            public int Left;

            public int Right;
        }
    }

    internal sealed class MatchOptimizer
    {
        private const int ShortMatchSearchLimit = 0xEFD;

        private const int TwoByteFrontierDistanceLimit = 0x800;

        public bool HasEstimates
        {
            get
            {
                return _mainEstimates != null;
            }
        }

        private readonly byte[] _input;

        private readonly MatchFinder _finder;

        private readonly List<Token> _path = new();

        private ParseNode[] _nodes = new ParseNode[64];

        private int _nodeCount;

        private HuffmanTable? _mainEstimates;

        private HuffmanTable? _lengthEstimates;

        public MatchOptimizer(byte[] input, MatchFinder finder)
        {
            _input = input;
            _finder = finder;
        }

        public void SetEstimates(HuffmanTable main, HuffmanTable length)
        {
            _mainEstimates = main;
            _lengthEstimates = length;
        }

        public IReadOnlyList<Token> ChoosePath(int position, Registers registers, int longest)
        {
            var furthest = longest;
            _nodeCount = 0;
            EnsureNodes(furthest + 1);
            _nodes[0] = new ParseNode(0, -1, default, registers);
            RelaxPosition(position, 0, longest);

            for (var at = 1; at < furthest; at++)
            {
                var node = _nodes[at];
                if (!node.Reachable)
                {
                    continue;
                }

                var next = _finder.Find(position + at, node.Registers);
                if (next != 0)
                {
                    var candidate = _finder.GetMatch(next);
                    var reach = at + next;

                    if (next > MatchFinder.LongMatchThreshold || reach >= ShortMatchSearchLimit)
                    {
                        EnsureNodes(reach + 1);
                        _finder.InsertSkipped(position + at, candidate);
                        var cost = node.Cost + EstimateMatchBits(candidate);
                        _nodes[reach] = new ParseNode(
                            cost, at, Token.FromMatch(candidate), node.Registers.Transform(candidate.Distance));
                        furthest = reach;
                        break;
                    }

                    var extendsFrontier = next > MatchFinder.MinimumMatchLength ||
                                          candidate.Distance.Type != DistanceType.Value ||
                                          candidate.Distance.Value + 2 < TwoByteFrontierDistanceLimit;

                    if (extendsFrontier && reach > furthest)
                    {
                        EnsureNodes(reach + 1);
                        Array.Clear(_nodes, furthest + 1, reach - furthest);
                        furthest = reach;
                    }
                }

                RelaxPosition(position, at, next);
            }

            _path.Clear();
            var destination = furthest;
            while (_nodes[destination].Previous >= 0)
            {
                var node = _nodes[destination];
                _path.Add(node.Token);
                destination = node.Previous;
            }

            _path.Reverse();
            return _path;
        }

        private void RelaxPosition(int position, int at, int longest)
        {
            RelaxToken(at, Token.FromLiteral(_input[position + at]));
            for (var length = MatchFinder.MinimumMatchLength; length <= longest; length++)
            {
                RelaxToken(at, Token.FromMatch(_finder.GetMatch(length)));
            }
        }

        private void RelaxToken(int at, Token token)
        {
            var source = _nodes[at];
            var destination = at + token.Length;
            var tokenCost = token.Type == TokenType.Literal ? EstimateLiteralBits(token.Literal) : EstimateMatchBits(token.Match);
            var cost = source.Cost + tokenCost;
            EnsureNodes(destination + 1);
            if (_nodes[destination].Reachable && _nodes[destination].Cost <= cost)
            {
                return;
            }

            var registers = token.Type == TokenType.Match
                ? source.Registers.Transform(token.Match.Distance)
                : source.Registers;
            _nodes[destination] = new ParseNode(cost, at, token, registers);
        }

        private void EnsureNodes(int count)
        {
            if (count <= _nodeCount)
            {
                return;
            }

            if (count > _nodes.Length)
            {
                Array.Resize(ref _nodes, Math.Max(count, 2 * _nodes.Length));
            }

            Array.Clear(_nodes, _nodeCount, count - _nodeCount);
            _nodeCount = count;
        }

        private int EstimateLiteralBits(byte value)
        {
            if (_mainEstimates == null)
            {
                return 8;
            }

            return _mainEstimates.GetCode(value)?.BitLength ?? 11;
        }

        private int EstimateMatchBits(Match candidate)
        {
            var (slot, _) = candidate.Distance.ToSlot()!.Value;
            var distanceBits = slot.ExtraBits;
            var length = candidate.Length;
            var mainBits = 9;
            if (length == MatchFinder.MinimumMatchLength && distanceBits >= 16)
            {
                mainBits = 100;
            }
            else if (_mainEstimates != null)
            {
                mainBits = _mainEstimates.GetCode(candidate.GetSymbolForSlot(slot).Value)?.BitLength ?? 12;
            }

            var bits = mainBits + distanceBits;
            if (length >= 9)
            {
                bits += _lengthEstimates == null
                    ? 6
                    : _lengthEstimates.GetCode(length - 9)?.BitLength ?? 8;
            }

            return bits;
        }

        private readonly struct ParseNode
        {
            public readonly bool Reachable;

            public readonly int Cost;

            public readonly int Previous;

            public readonly Token Token;

            public readonly Registers Registers;

            public ParseNode(int cost, int previous, Token token, Registers registers)
            {
                Reachable = true;
                Cost = cost;
                Previous = previous;
                Token = token;
                Registers = registers;
            }
        }
    }

    internal sealed class PreparedBlock
    {
        private const int EstimatedTreeBits = 1200;

        private const int MinimumAlignedMatches = 100;

        public BlockHeader Header { get; }

        public int Count { get; }

        private readonly IReadOnlyList<Token> _tokens;

        private readonly int _start;

        private readonly HuffmanTable _main;

        private readonly HuffmanTable _length;

        private readonly HuffmanTable _aligned;

        public PreparedBlock(IReadOnlyList<Token> tokens, int start, int count, int mainSymbolCount)
        {
            _tokens = tokens;
            _start = start;
            Count = count;

            var main = new int[mainSymbolCount];
            var length = new int[Constants.SecondaryLiteralCount];
            var aligned = new int[8];
            var decompressedSize = 0;
            var matchCount = 0;
            var distanceBits = 0;

            for (var i = 0; i < count; i++)
            {
                var token = tokens[start + i];
                if (token.Type == TokenType.Literal)
                {
                    main[token.Literal]++;
                    decompressedSize++;
                    continue;
                }

                var candidate = token.Match;
                var (slot, offset) = candidate.Distance.ToSlot()!.Value;
                var symbol = candidate.GetSymbolForSlot(slot).Value;
                if (symbol >= main.Length)
                {
                    throw new InvalidOperationException("Match distance exceeds the compression window");
                }

                var matchLength = candidate.Length;
                main[symbol]++;
                decompressedSize += matchLength;
                matchCount++;
                if (matchLength >= 9)
                {
                    length[matchLength - 9]++;
                }

                var extraBits = slot.ExtraBits;
                distanceBits += extraBits;
                if (extraBits >= 3)
                {
                    aligned[offset & 7]++;
                }
            }

            if (decompressedSize is < 1 or > 0xFF_FFFF)
            {
                throw new InvalidOperationException("Invalid block length");
            }

            _main = HuffmanTable.FromFrequencies(main);
            _length = HuffmanTable.FromFrequencies(length);
            _aligned = HuffmanTable.FromFrequencies(aligned);
            var estimatedBits = EstimatedTreeBits + distanceBits + EstimateBits(main, _main) +
                                EstimateBits(length, _length);

            var alignedTotal = 0;
            var alignedPeak = 0;
            foreach (var frequency in aligned)
            {
                alignedTotal += frequency;
                alignedPeak = Math.Max(alignedPeak, frequency);
            }

            var type = BlockType.Verbatim;
            if (decompressedSize <= (estimatedBits + 7) / 8)
            {
                type = BlockType.Uncompressed;
            }
            else if (matchCount >= MinimumAlignedMatches && alignedPeak > alignedTotal / 5)
            {
                type = BlockType.Aligned;
            }

            Header = new BlockHeader(type, decompressedSize);
        }

        public Token GetToken(int index)
        {
            return _tokens[_start + index];
        }

        public void WriteHeader(BitWriter bits, byte[] previousMain, byte[] previousLength)
        {
            bits.WriteBits((int)Header.Type, 3);
            bits.WriteBits(Header.DecompressedSize, 24);
            if (Header.Type == BlockType.Uncompressed)
            {
                return;
            }

            if (Header.Type == BlockType.Aligned)
            {
                for (var symbol = 0; symbol < 8; symbol++)
                {
                    bits.WriteBits(_aligned.GetCode(symbol)?.BitLength ?? 0, 3);
                }
            }

            const int literals = Constants.LiteralCount;
            var main = GetLengths(_main, previousMain.Length);
            var length = GetLengths(_length, previousLength.Length);
            var preTreeWriter = new PreTreeWriter(bits);

            var literalLengths = new ArraySegment<byte>(main, 0, literals);
            var previousLiteralLengths = new ArraySegment<byte>(previousMain, 0, literals);
            var matchLengths = new ArraySegment<byte>(main, literals, main.Length - literals);
            var previousMatchLengths = new ArraySegment<byte>(previousMain, literals, previousMain.Length - literals);
            var secondaryLengths = new ArraySegment<byte>(length);
            var previousSecondaryLengths = new ArraySegment<byte>(previousLength);

            preTreeWriter.WriteCodeLengths(literalLengths, previousLiteralLengths);
            preTreeWriter.WriteCodeLengths(matchLengths, previousMatchLengths);
            preTreeWriter.WriteCodeLengths(secondaryLengths, previousSecondaryLengths);

            Array.Copy(main, previousMain, main.Length);
            Array.Copy(length, previousLength, length.Length);
        }

        public void WriteToken(BitWriter bits, Token token)
        {
            if (token.Type == TokenType.Literal)
            {
                WriteCode(bits, _main, token.Literal);
                return;
            }

            var candidate = token.Match;
            var (slot, offset) = candidate.Distance.ToSlot()!.Value;
            WriteCode(bits, _main, candidate.GetSymbolForSlot(slot).Value);
            if (candidate.Length >= 9)
            {
                WriteCode(bits, _length, candidate.Length - 9);
            }

            var extraBits = slot.ExtraBits;
            if (Header.Type == BlockType.Aligned && extraBits >= 3)
            {
                bits.WriteBits(offset >> 3, (byte)(extraBits - 3));
                WriteCode(bits, _aligned, offset & 7);
            }
            else
            {
                bits.WriteBits(offset, (byte)extraBits);
            }
        }

        private static byte[] GetLengths(HuffmanTable table, int count)
        {
            var lengths = new byte[count];
            for (var symbol = 0; symbol < count; symbol++)
            {
                lengths[symbol] = table.GetCode(symbol)?.BitLength ?? 0;
            }

            return lengths;
        }

        private static int EstimateBits(int[] frequencies, HuffmanTable table)
        {
            var bits = 0;
            var symbols = 0;
            for (var i = 0; i < frequencies.Length; i++)
            {
                var frequency = frequencies[i];
                if (frequency != 0)
                {
                    bits += frequency * table.GetCode(i)!.Value.BitLength;
                    symbols++;
                }
            }

            return bits + (symbols == 1 ? 1 : 0);
        }

        private static void WriteCode(BitWriter bits, HuffmanTable table, int symbol)
        {
            var code = table.GetCode(symbol) ?? throw new InvalidOperationException("Missing Huffman code");
            bits.WriteBits(code.Value, code.BitLength);
        }
    }

    internal sealed class RecordWriter : IDisposable
    {
        private const byte FinalRecordMarker = 0xFF;

        private static readonly byte[] StreamTrailer = new byte[5];

        public BitWriter Bits { get; }

        public int Decoded { get; private set; }

        public int RecordRemaining
        {
            get
            {
                return Constants.RecordSize - _recordDecoded;
            }
        }

        private readonly Stream _output;

        private readonly int _inputLength;

        private readonly MemoryStream _record = new(Constants.RecordSize);

        private readonly byte[] _header = new byte[5];

        private int _recordDecoded;

        public RecordWriter(Stream output, int inputLength)
        {
            _output = output;
            _inputLength = inputLength;
            Bits = new BitWriter(_record);
            if (inputLength != 0)
            {
                Bits.WriteBit(false);
            }
        }

        public void Advance(int length)
        {
            if (length > RecordRemaining)
            {
                throw new InvalidOperationException("Token crosses the XMem record boundary");
            }

            Decoded += length;
            _recordDecoded += length;
            if (_recordDecoded == Constants.RecordSize)
            {
                FlushRecord();
            }
        }

        public void Complete()
        {
            if (Decoded != _inputLength)
            {
                throw new InvalidOperationException("Encoded blocks do not cover the compression input");
            }

            if (_recordDecoded != 0)
            {
                FlushRecord();
            }
        }

        public void Dispose()
        {
            Bits.Dispose();
        }

        private void FlushRecord()
        {
            Bits.FlushBits();
            var encodedLength = (int)_record.Length;
            var isFinal = Decoded == _inputLength;
            if (encodedLength > ushort.MaxValue || (!isFinal && encodedLength >= (FinalRecordMarker << 8)))
            {
                throw new InvalidOperationException("Encoded record exceeds the XMem record length field");
            }

            var offset = 0;
            if (isFinal)
            {
                _header[0] = FinalRecordMarker;
                _header[1] = (byte)(_recordDecoded >> 8);
                _header[2] = (byte)_recordDecoded;
                offset = 3;
            }

            _header[offset] = (byte)(encodedLength >> 8);
            _header[offset + 1] = (byte)encodedLength;
            _output.Write(_header, 0, offset + 2);
            _output.Write(_record.GetBuffer(), 0, encodedLength);
            if (isFinal)
            {
                _output.Write(StreamTrailer, 0, StreamTrailer.Length);
            }

            _record.SetLength(0);
            _record.Position = 0;
            _recordDecoded = 0;
        }
    }

    internal sealed class Tokenizer
    {
        public int Cursor { get; private set; }

        public IReadOnlyList<Token> Tokens
        {
            get
            {
                return _tokens;
            }
        }

        public bool HasEstimates
        {
            get
            {
                return _optimizer.HasEstimates;
            }
        }

        private readonly byte[] _input;

        private readonly int _inputLength;

        private readonly int _mainSymbolCount;

        private readonly MatchFinder _finder;

        private readonly MatchOptimizer _optimizer;

        private readonly List<Token> _tokens = new();

        private Registers _registers = new();

        private int? _mainDummy;

        private int? _lengthDummy;

        public Tokenizer(byte[] input, int inputLength, int windowSize, int partitionSize, int mainSymbolCount)
        {
            _input = input;
            _inputLength = inputLength;
            _mainSymbolCount = mainSymbolCount;
            _finder = new MatchFinder(inputLength, windowSize, partitionSize);
            _optimizer = new MatchOptimizer(input, _finder);
        }

        public bool TryPrepareBatch(out Token direct, out IReadOnlyList<Token>? path)
        {
            direct = default;
            path = null;
            if (Cursor == _inputLength)
            {
                return false;
            }

            if (Cursor == _finder.Submitted)
            {
                _finder.SubmitNext(_input);
            }

            var longest = _finder.Find(Cursor, _registers);
            if (longest >= MatchFinder.LongMatchThreshold)
            {
                var candidate = _finder.GetMatch(longest);
                _finder.InsertSkipped(Cursor, candidate);
                direct = Token.FromMatch(candidate);
            }
            else if (longest != 0)
            {
                path = _optimizer.ChoosePath(Cursor, _registers, longest);
            }
            else
            {
                direct = Token.FromLiteral(_input[Cursor]);
            }

            return true;
        }

        public void Commit(Token token)
        {
            if (token.Length > _inputLength - Cursor)
            {
                throw new InvalidOperationException("Token exceeds the compression input");
            }

            Cursor += token.Length;
            if (token.Type == TokenType.Match)
            {
                _registers = _registers.Transform(token.Match.Distance);
            }

            _tokens.Add(token);
        }

        public void Rewind()
        {
            Cursor = 0;
            _registers = new Registers();
            _tokens.Clear();
            _mainDummy = null;
            _lengthDummy = null;
            _finder.ClearTree();
        }

        public void EnableEstimates()
        {
            _optimizer.SetEstimates(
                HuffmanTable.FromLengths(new byte[_mainSymbolCount]),
                HuffmanTable.FromLengths(new byte[Constants.SecondaryLiteralCount]));
        }

        public void RebuildEstimates(int start, int end)
        {
            UpdateEstimates(start, end, preserveDummies: false);
        }

        public void RefreshEstimates(int start, int end)
        {
            UpdateEstimates(start, end, preserveDummies: true);
        }

        private void UpdateEstimates(int start, int end, bool preserveDummies)
        {
            var main = new int[_mainSymbolCount];
            var length = new int[Constants.SecondaryLiteralCount];
            for (var i = start; i < end; i++)
            {
                var token = _tokens[i];
                main[token.Symbol.Value]++;
                if (token.Type == TokenType.Match && token.Match.Length >= 9)
                {
                    length[token.Match.Length - 9]++;
                }
            }

            // Keep dummy frequencies across refreshes of the same pending block
            var mainDummy = AddDummyFrequency(main, preserveDummies ? _mainDummy : null);
            var lengthDummy = AddDummyFrequency(length, preserveDummies ? _lengthDummy : null);
            _optimizer.SetEstimates(HuffmanTable.FromFrequencies(main), HuffmanTable.FromFrequencies(length));

            _mainDummy = preserveDummies ? mainDummy : null;
            _lengthDummy = preserveDummies ? lengthDummy : null;
        }

        private static int? AddDummyFrequency(int[] frequencies, int? savedSymbol)
        {
            if (savedSymbol is { } symbol)
            {
                frequencies[symbol]++;
                return symbol;
            }

            var soleSymbol = -1;
            for (var i = 0; i < frequencies.Length; i++)
            {
                if (frequencies[i] == 0)
                {
                    continue;
                }

                if (soleSymbol >= 0)
                {
                    return null;
                }

                soleSymbol = i;
            }

            if (soleSymbol < 0)
            {
                return null;
            }

            var dummy = soleSymbol == 0 ? 1 : 0;
            frequencies[dummy]++;
            return dummy;
        }
    }

    internal sealed class TokenPrefixSelector
    {
        private const int SampleLength = 0x400;

        private const int MinimumTokenCount = 0x1800;

        private const int MaximumSplitCount = 4;

        private const int DifferenceThreshold = 0x578;

        private const int SplitThreshold = 0x6A3;

        private static readonly int[] BitLengths = BuildBitLengths();

        private readonly int[] _left;

        private readonly int[] _right;

        private int _splitCount;

        public TokenPrefixSelector(int symbolCount)
        {
            _left = new int[symbolCount];
            _right = new int[symbolCount];
        }

        public void Reset()
        {
            _splitCount = 0;
        }

        public int FindPrefix(IReadOnlyList<Token> tokens, int start)
        {
            var count = tokens.Count - start;
            if (count < MinimumTokenCount || _splitCount >= MaximumSplitCount)
            {
                return count;
            }

            var symbols = new int[count];
            for (var i = 0; i < count; i++)
            {
                symbols[i] = tokens[start + i].Symbol.Value;
            }

            // Check for a sustained change before refining the block boundary.
            for (var before = 0x800; before < count - 0x1000; before += SampleLength)
            {
                var after = before + 0x800;
                if (Difference(symbols, before, after - SampleLength) <= DifferenceThreshold ||
                    Difference(symbols, before - SampleLength, after) <= DifferenceThreshold ||
                    Difference(symbols, before - 0x800, after + SampleLength) <= DifferenceThreshold)
                {
                    continue;
                }

                var bestScore = 0;
                var bestPosition = 0;
                for (var position = after - 0x600; position < after + 0x200; position += 0x40)
                {
                    var score = Difference(symbols, position - SampleLength, position);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPosition = position;
                    }
                }

                if (bestScore > SplitThreshold && bestPosition >= 0x1000)
                {
                    _splitCount++;
                    return bestPosition;
                }
            }

            return count;
        }

        private int Difference(int[] symbols, int first, int second)
        {
            Array.Clear(_left, 0, _left.Length);
            Array.Clear(_right, 0, _right.Length);
            for (var i = 0; i < SampleLength; i++)
            {
                _left[symbols[first + i]]++;
                _right[symbols[second + i]]++;
            }

            var score = 0;
            for (var i = 0; i < _left.Length; i++)
            {
                var left = BitLengths[_left[i]];
                var right = BitLengths[_right[i]];
                score += Math.Abs(left * left - right * right);
            }

            return score;
        }

        private static int[] BuildBitLengths()
        {
            var lengths = new int[SampleLength + 1];
            for (var i = 1; i < lengths.Length; i++)
            {
                lengths[i] = lengths[i >> 1] + 1;
            }

            return lengths;
        }
    }
}

namespace XCompress.Huffman
{
    internal partial class HuffmanTable
    {
        private const int CodeSpace = 1 << MaxCodeLength;

        public IEnumerator<byte> GetCodeLengths()
        {
            foreach (var code in _codes)
            {
                if (code.HasValue)
                {
                    yield return code.Value.BitLength;
                }
                else
                {
                    yield return 0;
                }
            }
        }

        public static HuffmanTable FromFrequencies(params int[] frequencies)
        {
            var entries = new List<MinHeap.Entry>();
            for (var symbol = 0; symbol < frequencies.Length; symbol++)
            {
                var frequency = frequencies[symbol];
                if (frequency < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(frequencies), frequency, $"Frequency for symbol {symbol} must be non-negative");
                }

                if (frequency != 0)
                {
                    entries.Add(new MinHeap.Entry(symbol, (ulong)frequency));
                }
            }

            var symbolCount = frequencies.Length;
            var lengths = new byte[symbolCount];
            if (entries.Count == 0)
            {
                return FromLengths(lengths);
            }

            if (entries.Count > CodeSpace)
            {
                throw new ArgumentException("too many Huffman symbols");
            }

            if (entries.Count == 1)
            {
                var symbol = entries[0].Index;
                if (symbolCount == 1)
                {
                    lengths[symbol] = 1;
                    return FromLengths(lengths);
                }

                // LZX tables with one used symbol include a second, unused leaf
                var dummy = symbol == 0 ? 1 : 0;
                entries.Add(new MinHeap.Entry(dummy, 1));
            }

            var heap = new MinHeap(entries);
            var children = new List<(int, int)>(heap.Count - 1);
            var leafOrder = new List<int>(heap.Count);

            while (heap.Count > 1)
            {
                var left = heap.Pop();
                var right = heap.Peek();
                if (left.Index < symbolCount) leafOrder.Add(left.Index);
                if (right.Index < symbolCount) leafOrder.Add(right.Index);

                // Replacing the second minimum in place preserves the LZX tie order
                var parent = symbolCount + children.Count;
                children.Add((left.Index, right.Index));
                heap.ReplaceMin(new MinHeap.Entry(parent, left.Weight + right.Weight));
            }

            var root = heap.Pop().Index;
            var counts = new int[MaxCodeLength + 1];
            var pending = new Stack<(int, int)>();
            pending.Push((root, 0));

            while (pending.Count > 0)
            {
                var (node, depth) = pending.Pop();
                if (node < symbolCount)
                {
                    counts[Math.Min(depth, MaxCodeLength)]++;
                }
                else
                {
                    var (left, right) = children[node - symbolCount];
                    pending.Push((right, depth + 1));
                    pending.Push((left, depth + 1));
                }
            }

            LimitCodeLengths(counts);
            var leafIndex = 0;
            for (byte bitLength = MaxCodeLength; bitLength >= 1; bitLength--)
            {
                for (var i = 0; i < counts[bitLength]; i++)
                {
                    var symbol = leafOrder[leafIndex];
                    lengths[symbol] = bitLength;
                    leafIndex++;
                }
            }

            return FromLengths(lengths);
        }

        private static void LimitCodeLengths(int[] counts)
        {
            var occupied = 0;
            for (var bitLength = 1; bitLength < counts.Length; bitLength++)
            {
                var count = counts[bitLength];
                occupied += count << (MaxCodeLength - bitLength);
            }

            while (occupied > CodeSpace)
            {
                // Splitting a shorter code preserves its space.
                // Removing one longest code restores one unit of space and preserves the leaf count.
                int? shorter = null;
                for (var bitLength = MaxCodeLength - 1; bitLength >= 1; bitLength--)
                {
                    if (counts[bitLength] != 0)
                    {
                        shorter = bitLength;
                        break;
                    }
                }

                if (shorter == null)
                {
                    throw new InvalidOperationException("An overfull tree within the symbol limit has a shorter code");
                }

                counts[shorter.Value] -= 1;
                counts[shorter.Value + 1] += 2;
                counts[MaxCodeLength] -= 1;
                occupied -= 1;
            }
        }
    }

    internal sealed class MinHeap
    {
        public int Count
        {
            get
            {
                return _entries.Count;
            }
        }

        private readonly IList<Entry> _entries;

        public MinHeap(IList<Entry> entries)
        {
            _entries = entries;
            for (var parent = _entries.Count / 2 - 1; parent >= 0; parent--)
            {
                SiftDown(parent);
            }
        }

        public Entry Peek()
        {
            return _entries[0];
        }

        public Entry Pop()
        {
            var lastIndex = _entries.Count - 1;
            var last = _entries[lastIndex];
            _entries.RemoveAt(lastIndex);

            if (_entries.Count == 0)
            {
                return last;
            }

            return ReplaceMin(last);
        }

        public Entry ReplaceMin(Entry entry)
        {
            var min = _entries[0];
            _entries[0] = entry;
            SiftDown(0);
            return min;
        }

        private void SiftDown(int parent)
        {
            var entry = _entries[parent];
            var child = 2 * parent + 1;

            // Equal weights keep the left child and leave an equal parent in place
            while (child < _entries.Count)
            {
                var right = child + 1;
                if (right < _entries.Count && _entries[right].Weight < _entries[child].Weight)
                {
                    child = right;
                }

                if (entry.Weight <= _entries[child].Weight)
                {
                    break;
                }

                _entries[parent] = _entries[child];
                parent = child;
                child = 2 * parent + 1;
            }

            _entries[parent] = entry;
        }

        internal readonly struct Entry
        {
            public readonly int Index;

            public readonly ulong Weight;

            public Entry(int index, ulong weight)
            {
                Index = index;
                Weight = weight;
            }
        }
    }
}

namespace XCompress.IO
{
    internal sealed class BitWriter : IDisposable
    {
        private readonly Stream _stream;

        private readonly byte[] _wordBytes = new byte[2];

        private ushort _word;

        private byte _used;

        private bool _disposed;

        public BitWriter(Stream stream)
        {
            if (!stream.CanWrite)
            {
                throw new ArgumentException("Stream must be writable", nameof(stream));
            }

            _stream = stream;
        }

        public void WriteBit(bool bit)
        {
            ThrowIfDisposed();
            AppendBit(bit);
        }

        public void WriteBits(int value, byte numberOfBits)
        {
            ThrowIfDisposed();
            if (numberOfBits > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(numberOfBits), "Cannot write more than 32 bits at once");
            }

            if (numberOfBits != 32 && value >> numberOfBits != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Value does not fit in the specified number of bits");
            }

            for (var shift = numberOfBits - 1; shift >= 0; shift--)
            {
                AppendBit(((value >> shift) & 1) != 0);
            }
        }

        public void FlushBits()
        {
            ThrowIfDisposed();
            if (_used != 0)
            {
                _word <<= 16 - _used;
                EmitWord();
            }
        }

        public void AlignBeforeRaw()
        {
            if (_used == 0)
            {
                WriteBits(0, 16);
                return;
            }

            FlushBits();
        }

        public void WriteBytes(params byte[] bytes)
        {
            WriteBytes(bytes, 0, bytes.Length);
        }

        public void WriteBytes(byte[] bytes, int offset, int count)
        {
            ThrowIfDisposed();
            if (_used != 0)
            {
                throw new InvalidOperationException("Raw bytes require word alignment");
            }

            _stream.Write(bytes, offset, count);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    FlushBits();
                }
                finally
                {
                    _disposed = true;
                    _stream.Dispose();
                }
            }
        }

        private void AppendBit(bool bit)
        {
            _word = (ushort)((_word << 1) | (bit ? 1 : 0));
            _used++;
            if (_used == 16)
            {
                EmitWord();
            }
        }

        private void EmitWord()
        {
            _wordBytes[0] = (byte)_word;
            _wordBytes[1] = (byte)(_word >> 8);
            _word = 0;
            _used = 0;
            _stream.Write(_wordBytes, 0, _wordBytes.Length);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BitWriter));
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
        public static PreTree FromFrequencies(int[] frequencies)
        {
            if (frequencies.Length != PreTreeSymbol.MaxCount)
            {
                throw new ArgumentException("A pretree must have exactly 20 frequencies", nameof(frequencies));
            }

            return new PreTree(HuffmanTable.FromFrequencies(frequencies));
        }
    }

    internal sealed class PreTreeWriter
    {
        private readonly BitWriter _writer;

        public PreTreeWriter(BitWriter writer)
        {
            _writer = writer;
        }

        public void WriteCodeLengths(ArraySegment<byte> current, ArraySegment<byte> previous)
        {
            var (instructions, frequencies) = BuildInstructions(current, previous);
            var tree = PreTree.FromFrequencies(frequencies);

            using var lengths = tree.Table.GetCodeLengths();
            while (lengths.MoveNext())
            {
                _writer.WriteBits(lengths.Current, PreTree.CodeLengthBits);
            }

            foreach (var instruction in instructions)
            {
                WriteInstruction(tree, instruction);
            }
        }

        private static (List<Instruction>, int[]) BuildInstructions(ArraySegment<byte> current, ArraySegment<byte> previous)
        {
            if (current.Count != previous.Count)
            {
                throw new ArgumentException("Code length segments must have the same count", nameof(previous));
            }

            var currentLengths = current.Array ?? Array.Empty<byte>();
            var previousLengths = previous.Array ?? Array.Empty<byte>();
            for (var i = 0; i < current.Count; i++)
            {
                if (currentLengths[current.Offset + i] >= Instruction.LengthModulus)
                {
                    throw new ArgumentException("Code lengths must be between 0 and 16", nameof(current));
                }

                if (previousLengths[previous.Offset + i] >= Instruction.LengthModulus)
                {
                    throw new ArgumentException("Code lengths must be between 0 and 16", nameof(previous));
                }
            }

            var instructions = new List<Instruction>(current.Count);
            var frequencies = new int[PreTreeSymbol.MaxCount];
            var index = 0;
            while (index < current.Count)
            {
                var length = currentLengths[current.Offset + index];
                var limit = length == 0 ? Instruction.MaxZeroRunLength : Instruction.MaxDeltaRunLength;
                var repeats = 0;

                // Count matching entries after the current one to preserve the LZX run boundaries.
                while (repeats < limit && repeats < current.Count - index - 1 &&
                       currentLengths[current.Offset + index + 1 + repeats] == length)
                {
                    repeats++;
                }

                var count = repeats < Instruction.MinRunLength ? 1 : repeats;
                Instruction instruction;
                if (length == 0 && count > 1)
                {
                    instruction = Instruction.ZeroRun(count);
                }
                else
                {
                    var delta = (previousLengths[previous.Offset + index] + Instruction.LengthModulus - length) %
                                Instruction.LengthModulus;
                    instruction = Instruction.FromDelta(count, (byte)delta);
                }

                frequencies[instruction.Symbol]++;
                if (count > 1 && instruction.Delta is { } repeatedDelta)
                {
                    frequencies[repeatedDelta]++;
                }

                instructions.Add(instruction);
                index += count;
            }

            return (instructions, frequencies);
        }

        private void WriteInstruction(PreTree tree, Instruction instruction)
        {
            var symbol = instruction.Symbol;
            WriteSymbol(tree, symbol);

            switch (symbol)
            {
                case PreTreeSymbol.ShortZeroRun:
                    _writer.WriteBits(instruction.Count - Instruction.MinRunLength, 4);
                    break;

                case PreTreeSymbol.LongZeroRun:
                    _writer.WriteBits(instruction.Count - Instruction.LongZeroRunMinLength, 5);
                    break;

                case PreTreeSymbol.RepeatDelta:
                    _writer.WriteBits(instruction.Count - Instruction.MinRunLength, 1);
                    WriteSymbol(tree, instruction.Delta!.Value);
                    break;
            }
        }

        private void WriteSymbol(PreTree tree, int symbol)
        {
            var code = tree.Table.GetCode(symbol) ??
                       throw new InvalidOperationException("Pretree instruction has no Huffman code");
            _writer.WriteBits(code.Value, code.BitLength);
        }
    }
}
