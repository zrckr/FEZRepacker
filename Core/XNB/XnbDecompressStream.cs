using System.Text;

using FEZRepacker.Core.XMemCompress;

namespace FEZRepacker.Core.XNB
{
    internal class XnbDecompressStream : Stream
    {
        private readonly Stream _source;
        private readonly bool _copyOriginalSource;
        private readonly MemoryStream _decompressionBuffer;
        private readonly int _decompressedSize;
        private long _readPosition;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _copyOriginalSource ? _source.Length : _decompressedSize;
        public override long Position
        {
            get => _copyOriginalSource ? _source.Position : _readPosition;
            set => throw new NotSupportedException();
        }

        public XnbDecompressStream(Stream source)
        {
            _source = source;
            _decompressionBuffer = new MemoryStream();

            if (!TryProcessHeader(out var compressedSize, out var decompressedSize))
            {
                _copyOriginalSource = true;
                return;
            }

            _decompressedSize = decompressedSize;
            var compressedData = new byte[compressedSize];
            source.Read(compressedData, 0, compressedSize);
            var contentSize = decompressedSize - XnbHeader.Size;
            var decompressed = XCompress.Decompress(compressedData, contentSize);

            _decompressionBuffer.Write(decompressed, 0, decompressed.Length);

            if (_decompressionBuffer.Length != decompressedSize)
            {
                throw new XnbSerializationException(
                    $"XNB decompression data size mismatch - expected {decompressedSize}, got {_decompressionBuffer.Length}"
                );
            }
        }

        private bool TryProcessHeader(out int compressedSize, out int decompressedSize)
        {
            var sourcePositionPreHeaderRead = _source.Position;
            if (!XnbHeader.TryRead(_source, out var header) || (header.Flags & XnbHeader.XnbFlags.Compressed) == 0)
            {
                compressedSize = 0;
                decompressedSize = 0;
                _source.Position = sourcePositionPreHeaderRead;
                return false;
            }

            using var reader = new BinaryReader(_source, Encoding.UTF8, true);
            compressedSize = reader.ReadInt32();
            decompressedSize = reader.ReadInt32() + XnbHeader.Size;

            header.Flags &= ~XnbHeader.XnbFlags.Compressed;
            header.Write(_decompressionBuffer);
            new BinaryWriter(_decompressionBuffer).Write(decompressedSize);

            return true;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_copyOriginalSource)
            {
                return _source.Read(buffer, offset, count);
            }

            _decompressionBuffer.Position = _readPosition;
            int read = _decompressionBuffer.Read(buffer, offset, count);
            _readPosition += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _decompressionBuffer.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
