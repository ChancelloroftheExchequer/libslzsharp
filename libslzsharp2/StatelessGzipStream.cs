namespace libslzsharp2
{
    using System;
    using System.IO;
    using System.IO.Compression;

    /// <summary>
    /// Drop-in replacement for GzipStream
    /// </summary>
    public class StatelessGzipStream : Stream
    { 
        private readonly Stream _targetStream;
        private readonly SlzState _state = new SlzState(9, SlzState.SlzFormat.SLZ_FMT_GZIP);

        public StatelessGzipStream(Stream targetStream, CompressionMode mode, bool leavOpen = false)
        {
            if (mode == CompressionMode.Decompress)
            {
                throw new NotSupportedException(
                    $"Decompression is not supported for {nameof(StatelessGzipStream)}.");
            }

            this._targetStream = targetStream;
        }

        public override void Flush()
        {
            // TODO: array pool, whats the maximum this can be?
            byte[] output = new byte[4096];

            long written = SlzState.slz_finish(this._state, output);

            if (written > 0)
            {
                this._targetStream.Write(output, 0, (int)written);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
#if PEDANTIC
            // TODO: code pedantic checks on the parameters
#endif
            byte[] otput = new byte[2 * count];

            long written = SlzState.slz_encode(this._state, otput, buffer, count, true);

            if (written > 0)
            {
                this._targetStream.Write(otput, 0, (int)written);
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}