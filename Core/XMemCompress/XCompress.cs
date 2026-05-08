using System.Runtime.InteropServices;

namespace FEZRepacker.Core.XMemCompress
{
    public static class XCompress
    {
        private enum XMemCodec
        {
            Default = 0,
            Lzx = 1
        };

        [StructLayout(LayoutKind.Explicit)]
        private struct XMemCodecParametersLzx
        {
            [FieldOffset(0)] public int Flags;
            [FieldOffset(4)] public int WindowSize;
            [FieldOffset(8)] public int CompressionPartitionSize;
        }

        [DllImport("xcompress.dll")]
        private static extern int XMemCreateDecompressionContext(
            XMemCodec codec, ref XMemCodecParametersLzx @params, int flags, ref IntPtr context);

        [DllImport("xcompress.dll")]
        private static extern int XMemDecompress(
            IntPtr context, byte[] destination, ref int destSize, byte[] src, int srcSize);

        [DllImport("xcompress.dll")]
        private static extern int XMemDestroyDecompressionContext(IntPtr pContext);

        [DllImport("xcompress.dll")]
        private static extern int XMemCreateCompressionContext(
            XMemCodec codec, ref XMemCodecParametersLzx @params, int flags, ref IntPtr context);

        [DllImport("xcompress.dll")]
        private static extern int XMemCompress(
            IntPtr context, byte[] destination, ref int destSize, byte[] src, int srcSize);

        [DllImport("xcompress.dll")]
        private static extern void XMemDestroyCompressionContext(IntPtr context);

        public static byte[] Decompress(byte[] data, int uncompressedSize)
        {
            var ctx = IntPtr.Zero;
            var codecParams = new XMemCodecParametersLzx
            {
                Flags = 0, WindowSize = 64 * 1024, CompressionPartitionSize = 256 * 1024
            };

            if (XMemCreateDecompressionContext(XMemCodec.Lzx, ref codecParams, 0, ref ctx) != 0)
            {
                throw new Exception("XMemCreateDecompressionContext failed");
            }
            
            var outputData = new byte[uncompressedSize];
            var outputDataLength = uncompressedSize;

            if (XMemDecompress(ctx, outputData, ref outputDataLength, data, data.Length) != 0)
            {
                XMemDestroyDecompressionContext(ctx);
                throw new Exception("XMemDecompress failed");
            }

            XMemDestroyDecompressionContext(ctx);
            if (outputDataLength != uncompressedSize)
            {
                throw new Exception("Decompression Failed");
            }

            return outputData;
        }

        public static byte[] Compress(byte[] data)
        {
            var ctx = IntPtr.Zero;
            var codecParams = new XMemCodecParametersLzx
            {
                Flags = 0, WindowSize = 64 * 1024, CompressionPartitionSize = 256 * 1024
            };
            
            if (XMemCreateCompressionContext(XMemCodec.Lzx, ref codecParams, 0, ref ctx) != 0)
            {
                throw new Exception("XMemCreateCompressionContext failed");
            }

            var outputDataLength = data.Length * 2;
            var outputData = new byte[outputDataLength];

            if (XMemCompress(ctx, outputData, ref outputDataLength, data, data.Length) != 0)
            {
                XMemDestroyCompressionContext(ctx);
                throw new Exception("XMemCompress failed");
            }
            
            XMemDestroyCompressionContext(ctx);
            Array.Resize(ref outputData, outputDataLength);
            
            return outputData;
        }
    }
}