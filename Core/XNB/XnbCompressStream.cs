using System.Text;

using FEZRepacker.Core.XMemCompress;

namespace FEZRepacker.Core.XNB
{
    internal class XnbCompressStream : Stream
    {
        private readonly Stream _source;
        private readonly bool _copyOriginalSource;
        private readonly MemoryStream _compressionBuffer;
        private long _readPosition;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _compressionBuffer.Length;
        public override long Position
        {
            get => _copyOriginalSource ? _source.Position : _readPosition;
            set => throw new NotSupportedException();
        }

        public XnbCompressStream(Stream source)
        {
            _source = source;
            _compressionBuffer = new MemoryStream();

            if (!TryProcessHeader(out var header, out var fileLength))
            {
                _copyOriginalSource = true;
                return;
            }

            using var reader = new BinaryReader(source, Encoding.UTF8, true);
            var decompressedData = reader.ReadBytes(fileLength - XnbHeader.Size);
            var compressedData = XCompress.Compress(decompressedData);

            header.Flags |= XnbHeader.XnbFlags.Compressed;
            header.Write(_compressionBuffer);

            using var writer = new BinaryWriter(_compressionBuffer, Encoding.UTF8, true);
            writer.Write(compressedData.Length + XnbHeader.Size + sizeof(UInt32));
            writer.Write(compressedData);
        }

        private bool TryProcessHeader(out XnbHeader header, out int fileLength)
        {
            var sourcePositionPreHeaderRead = _source.Position;
            if (!XnbHeader.TryRead(_source, out header))
            {
                fileLength = 0;
                _source.Position = sourcePositionPreHeaderRead;
                return false;
            }

            using var reader = new BinaryReader(_source, Encoding.UTF8, true);
            fileLength = reader.ReadInt32();
            return true;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_copyOriginalSource)
            {
                return _source.Read(buffer, offset, count);
            }

            _compressionBuffer.Position = _readPosition;
            int read = _compressionBuffer.Read(buffer, offset, count);
            _readPosition += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _compressionBuffer.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
