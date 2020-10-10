namespace libslzsharp2
{
    using System;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    public class SlzState
    {
        private enum StreamState
        {
            SLZ_ST_INIT, /* stream initialized */
            SLZ_ST_EOB, /* header or end of block already sent */
            SLZ_ST_FIXED, /* inside a fixed huffman sequence */
            SLZ_ST_LAST, /* last block, BFINAL sent */
            SLZ_ST_DONE, /* BFINAL+EOB sent BFINAL */
            SLZ_ST_END /* end sent (BFINAL, EOB, CRC + len) */
        }

        public enum SlzFormat
        {
            SLZ_FMT_GZIP, /* RFC1952: gzip envelope and crc32 for CRC */
            SLZ_FMT_ZLIB, /* RFC1950: zlib envelope and adler-32 for CRC */
            SLZ_FMT_DEFLATE, /* RFC1951: raw deflate, and no crc */
        };


        #region Public Members

        public int Level;
        public SlzFormat Format;

        #endregion

        /// <summary>
        /// Log2 of the size of the hash table used for the references table.
        /// </summary>
        private static readonly int HASH_BITS = 13;

        private static readonly byte[] ZlibHeader =
        {
                    0x78, 0x01
                }; // 32k win, deflate, chk=1

        /// <summary>
        /// GZip Header
        /// </summary>
        private static readonly byte[] GzipHeader =
        {
                    0x1F, 0x8B, // ID1, ID2
                    0x08, 0x00, // Deflate, flags (none)
                    0x00, 0x00, 0x00, 0x00, // mtime: none
                    0x04, 0x03
                }; // fastest comp, OS=Unix

        #region State Management
        private StreamState _state = StreamState.SLZ_ST_INIT;
        private uint _crc32 = 0;
        private uint _ilen = 0;
        private uint _qbits = 0;
        private uint _queue = 0;


        private int _inpos = 0;
        private int _outpos = 0;
        private int _totalOut = 0;


        public byte[] inbuf;
        public byte[] outbuf;


        public SlzState(int level, SlzFormat format)
        {



            SlzState.slz_init(this, level, format);

        }



        public byte this[int idx]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => this.outbuf[this._outpos + idx];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => this.outbuf[this._outpos + idx] = value;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddWidthIncrement(byte b)
        {
            this[0] = b;
            this._outpos++;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Increment(int amt)
        {
            this._outpos += amt;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Copy(byte[] input, int offset, int len)
        {
            /*
            var spanSource = new Span<byte>(input, offset, len);
            var vectorSource = MemoryMarshal.Cast<byte, Vector<uint>>(spanSource);

            var spanTarget = new Span<byte>(this.outbuf, this._outpos, len);
            var vectorTarget = MemoryMarshal.Cast<byte, Vector<uint>>(spanSource);


            int i = 0;

            // find the first non-match via the vector code
            for (; i < vectorTarget.Length; i++)
            {
                if (vectorTarget[i] != vectorSource[i])
                    break;
            }

            for (i = i * sizeof(uint); i < len; i++)
            {
                if (spanTarget[i] != spanSource[i])
                    break;
            }
            */






            // a)
            // Buffer.BlockCopy(input, offset, this.outbuf, this._outpos, len);
            // this._outpos += len;

            // b) this is simple
            for (int i = 0; i < len; i++)
            {
                this[i] = input[i + offset];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyWithIncrement(byte[] input, int len)
        {
            for (int i = 0; i < len; i++)
            {
                this[i] = input[i];
            }

            this._outpos += len;

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyWithIncrement(byte[] input, int offset, int len)
        {
            for (int i = 0; i < len; i++)
            {
                this[i] = input[i + offset];
            }

            this._outpos += len;
        }

        #endregion



        /* sets <count> BYTES to -32769 in <refs> so that any uninitialized entry will
         * verify (pos-last-1 >= 32768) and be ignored. <count> must be a multiple of
         * 128 bytes and <refs> must be at least one count in length. It's supposed to
         * be applied to 64-bit aligned data exclusively, which makes it slightly
         * faster than the regular memset() since no alignment check is performed.
         */
        private static void ResetBackReferences(ulong[] refs)
        {
            Span<ulong> spanRefs = refs;
            spanRefs.Fill(unchecked((uint)-32769));

            ////// TODO: this could just be a memset if we were smarted about how the array worked
            //for (int i = 0; i < refs.Length; i++)
            //{
            //    refs[i] = unchecked((uint)-32769);
            //}
        }


        /* Initializes stream <strm> for use with the gzip format (rfc1952). The
         * compression level passed in <level> is set. This value can only be 0 (no
         * compression) or 1 (compression) and other values will lead to unpredictable
         * behaviour. The function always returns 0.
         */
        private static long slz_rfc1952_init(SlzState strm, int level)
        {

            strm._state = StreamState.SLZ_ST_INIT;
            strm.Level = level;
            strm.Format = SlzFormat.SLZ_FMT_GZIP;
            strm._crc32 = 0;
            strm._ilen = 0;
            strm._qbits = 0;
            strm._queue = 0;
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint slz_hash(uint a)
        {
            return ((a << 19) + (a << 6) - a) >> (32 - HASH_BITS);
        }


        // this is called 1.5 billion times but is slow, how can I do this without specifically unsafe code
        //private static void enqueue24(SlzState strm, uint x, uint xbits)
        //{
        //    ulong queue = strm._queue + ((ulong)x << (int)strm._qbits);
        //    uint qbits = strm._qbits + xbits;

        //    if (qbits >= 32)
        //    {
        //        MemoryMarshal.Cast<byte, uint>(strm.outbuf.AsSpan(strm._outpos, 4))[0] = (uint) queue;
        //        queue >>= 32;
        //        qbits -= 32;
        //        strm._outpos += 4;
        //    }


        //    strm._queue = queue;
        //    strm._qbits = qbits;
        //}


        /* enqueue code x of <xbits> bits (LSB aligned, at most 24) and copy complete
                * bytes into out buf. X must not contain non-zero bits above xbits. Prefer
                * enqueue8() when xbits is known for being 8 or less.
                */

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void enqueue24(SlzState strm, uint x, uint xbits)
        {
            uint queue = strm._queue + (x << (int)strm._qbits);
            uint qbits = strm._qbits + xbits;


            if (qbits >= 16)
            {
                strm[0] = (byte)queue;
                strm[1] = (byte)(queue >> 8);
                strm._outpos += 2;
                queue >>= 16;
                qbits -= 16;
            }

            if (qbits >= 8)
            {
                qbits -= 8;
                strm[0] = (byte) queue;
                strm._outpos++;                
                queue >>= 8;
            }

            strm._qbits = qbits;
            strm._queue = queue;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void enqueue8(SlzState strm, uint x, uint xbits)
        {
            uint queue = strm._queue + (x << unchecked((int)strm._qbits));
            uint qbits = strm._qbits + xbits;

            if (((int)(qbits - 8) >= 0))
            {
                qbits -= 8;
                strm[0] = (byte) queue;
                strm._outpos++;                
                queue >>= 8;
            }

            strm._qbits = qbits;
            strm._queue = queue;
        }


        /* only valid if buffer is already aligned */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void copy_32b(SlzState strm, uint x)
        {

            strm[0] = (byte)x;
            strm[1] = (byte)(x >> 8);
            strm[2] = (byte)(x >> 16);
            strm[3] = (byte)(x >> 24);

            strm.Increment(4);
        }


        /* only valid if buffer is already aligned */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void copy_16b(SlzState strm, uint x)
        {
            strm[0] = (byte) x;
            strm[1] = (byte) (x >> 8);
            strm._outpos += 2;           
        }

        /* align to next byte */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void flush_bits(SlzState strm)
        {
            if (strm._qbits > 0)
            {
                strm[0]=(byte)strm._queue;
                strm._outpos++;
            }


            if (strm._qbits > 8)
            {
                strm[0] = (byte) (strm._queue >> 8);
                strm._outpos++;                
            }

            strm._queue = 0;
            strm._qbits = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void send_eob(SlzState strm)
        {
            enqueue8(strm, 0, 7); // direct encoding of 256 = EOB (cf RFC1951)
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void send_huff(SlzState strm, uint code)
        {
            code = Tables.FixedHuffman[code];
            uint bits = code & 15;
            code >>= 4;
            enqueue24(strm, code, bits);
        }

        /* copies <len> litterals from <buf>. <more> indicates that there are data past
         * buf + <len>. <len> must not be null.
         */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void copy_lit_huff(SlzState strm, int offset, int len, bool more)
        {
            uint pos;

            /* This ugly construct limits the mount of tests and optimizes for the
             * most common case (more > 0).
             */
            if (strm._state == StreamState.SLZ_ST_EOB)
            {
                //                eob:
                strm._state = more ? StreamState.SLZ_ST_FIXED : StreamState.SLZ_ST_LAST;
                enqueue8(strm, 2 + (more ? (uint)0 : 1), 3); // BFINAL = !more ; BTYPE = 01
            }
            else if (!more)
            {
                send_eob(strm);
                strm._state = more ? StreamState.SLZ_ST_FIXED : StreamState.SLZ_ST_LAST;
                enqueue8(strm, 2 + (more ? (uint)0 : 1), 3); // BFINAL = !more ; BTYPE = 01
                                                             //goto eob;
            }

            pos = 0;
            while (pos < len)
            {
                send_huff(strm, strm.inbuf[offset + pos++]);
            }
        }


        /* copies <len> litterals from <buf>. <more> indicates that there are data past
         * buf + <len>. <len> must not be null.
         */
        private static void copy_lit(SlzState strm, int offset, uint len, bool more)
        {
            uint len2;

            do
            {
                len2 = len;
                if (len2 > 65535)
                    len2 = 65535;

                len -= len2;

                if (strm._state != StreamState.SLZ_ST_EOB)
                    send_eob(strm);

                strm._state = (more || len > 0) ? StreamState.SLZ_ST_EOB : StreamState.SLZ_ST_DONE;

                // TODO: confrim this logic
                enqueue8(strm, !(more || (len > 0)) ? (uint)1 : 0, 3); // BFINAL = !more ; BTYPE = 00

                // enqueue8(strm, strm._state == StreamState.SLZ_ST_DONE ? (uint)0 : 1, 3); // BFINAL = !more ; BTYPE = 00 

                flush_bits(strm);
                copy_16b(strm, len2); // len2
                copy_16b(strm, ~len2); // nlen2

                strm.Copy(strm.inbuf, offset, (int)len2);
                strm.Increment((int)len2);
            } while (len > 0);
        }


        /* This version computes the crc32 of <buf> over <len> bytes, doing most of it
  in 32-bit chunks.
 */
        // buf must point to unit span, b must point to the backing buffer fully
        // this isn't unrolled like they do but it could be
        private static uint slz_crc32_by4(uint crc, ReadOnlySpan<uint> buf, ReadOnlySpan<byte> b, int len)
        {

            int i = 0;

            // by unit
            for (; i < buf.Length; i++)
            {
                crc ^= buf[i];
                crc = Crc32.crc32_uint32(crc);
            }

            // anything left
            i = i * sizeof(uint);

            for (; i < len; i++)
            {
                crc = Crc32.crc32_char(crc, b[i]);
            }

            return crc;
        }


        /* uses the most suitable crc32 function to update crc on <buf, len> */
        private static uint update_crc(uint crc, byte[] buf, int len)
        {
            // var crc1 = Crc32.slz_crc32_by1(crc, buf, 0, len);
            // return crc1;

            ReadOnlySpan<byte> byteSpan = new ReadOnlySpan<byte>(buf, 0, len);
            ReadOnlySpan<uint> uintSpan = MemoryMarshal.Cast<byte, uint>(byteSpan);


            var crc2 = slz_crc32_by4(crc, uintSpan, byteSpan, len);

            return crc2;
        }



        /* Sends the zlib header for stream <strm> into buffer <buf>. When it's done,
         * the stream state is updated to SLZ_ST_EOB. It returns the number of bytes
         * emitted which is always 2. The caller is responsible for ensuring there's
         * always enough room in the buffer.
         */
        private static int slz_rfc1950_send_header(SlzState strm)
        {
            strm.CopyWithIncrement(ZlibHeader, 0, ZlibHeader.Length);
            strm._state = StreamState.SLZ_ST_EOB;
            return ZlibHeader.Length;
        }

        /* Original version from RFC1950, verified and works OK */
        private static uint slz_adler32_by1(uint crc, byte[] buf, int len)
        {

            uint s1 = crc & 0xffff;
            uint s2 = (crc >> 16) & 0xffff;
            int n;

            for (n = 0; n < len; n++)
            {
                s1 = (s1 + buf[n]) % 65521;
                s2 = (s2 + s1) % 65521;
            }

            return (s2 << 16) + s1;
        }


        /* Computes the adler32 sum on <buf> for <len> bytes. It avoids the expensive
    * modulus by retrofitting the number of bytes missed between 65521 and 65536
    * which is easy to count : For every sum above 65536, the modulus is offset
    * by (65536-65521) = 15. So for any value, we can count the accumulated extra
    * values by dividing the sum by 65536 and multiplying this value by
    * (65536-65521). That's easier with a drawing with boxes and marbles. It gives
    * this :
    *          x % 65521 = (x % 65536) + (x / 65536) * (65536 - 65521)
    *                    = (x & 0xffff) + (x >> 16) * 15.
    */
        private static uint slz_adler32_block(uint crc, byte[] buf, long len)
        {

            long s1 = crc & 0xffff;
            long s2 = (crc >> 16);
            long blk;
            long n;
            long offset = 0;

            do
            {
                blk = len;

                /* ensure we never overflow s2 (limit is about 2^((32-8)/2) */
                if (blk > (1U << 12))
                    blk = 1U << 12;
                len -= blk;

                for (n = 0; n < blk; n++)
                {
                    s1 = (s1 + buf[
                              offset + n]
                        ); // TODO: optimize me obviously, we can compte N and N + BLK to window this
                    s2 = (s2 + s1);
                }

                /* Largest value here is 2^12 * 255 = 1044480 < 2^20. We can
                 * still overflow once, but not twice because the right hand
                 * size is 225 max, so the total is 65761. However we also
                 * have to take care of the values between 65521 and 65536.
                 */
                s1 = (s1 & 0xffff) + 15 * (s1 >> 16);
                if (s1 > 65521)
                    s1 -= 65521;

                /* For s2, the largest value is estimated to 2^32-1 for
                 * simplicity, so the right hand side is about 15*65535
                 * = 983025. We can overflow twice at most.
                 */
                s2 = (s2 & 0xffff) + 15 * (s2 >> 16);
                s2 = (s2 & 0xffff) + 15 * (s2 >> 16);
                if (s2 > 65521)
                    s2 -= 65521;

                offset += blk;
            } while (len > 0);

            return (uint)((s2 << 16) + s1);
        }


        /* Sends the gzip header for stream <strm> into buffer <buf>. When it's done,
         * the stream state is updated to SLZ_ST_EOB. It returns the number of bytes
         * emitted which is always 10. The caller is responsible for ensuring there's
         * always enough room in the buffer.
         */
        private static int slz_rfc1952_send_header(SlzState strm)
        {
            strm.CopyWithIncrement(GzipHeader, 0, GzipHeader.Length);
            strm._state = StreamState.SLZ_ST_EOB;
            return GzipHeader.Length;
        }

        /* Encodes the block according to rfc1952. This means that the CRC of the input
         * block is computed according to the CRC32 algorithm. If the header was never
         * sent, it may be sent first. The number of output bytes is returned.
         */
        private static int slz_rfc1952_encode(SlzState strm, byte[] output, byte[] input, long ilen, bool more)
        {
            if (strm._state == StreamState.SLZ_ST_INIT)
            {
                slz_rfc1952_send_header(strm);
            }

            strm._crc32 = update_crc(strm._crc32, input, (int)ilen);

            // todo: this is already moved along from the header
            slz_rfc1951_encode(strm, output, input, ilen, more);



            return (int)strm._outpos;
        }

        /* Flushes any pending for stream <strm> into buffer <buf>, then sends BTYPE=1
         * and BFINAL=1 if needed. The stream ends in SLZ_ST_DONE. It returns the number
         * of bytes emitted. The trailer consists in flushing the possibly pending bits
         * from the queue (up to 7 bits), then possibly EOB (7 bits), then 3 bits, EOB,
         * a rounding to the next byte, which amounts to a total of 4 bytes max, that
         * the caller must ensure are available before calling the function.
         */
        private static int slz_rfc1951_finish(SlzState strm, byte[] buf)
        {
            strm.outbuf = buf;
            strm._outpos = 0;

            if (strm._state == StreamState.SLZ_ST_FIXED || strm._state == StreamState.SLZ_ST_LAST)
            {
                strm._state = (strm._state == StreamState.SLZ_ST_LAST)
                    ? StreamState.SLZ_ST_DONE
                    : StreamState.SLZ_ST_EOB;
                send_eob(strm);
            }

            if (strm._state != StreamState.SLZ_ST_DONE)
            {
                /* send BTYPE=1, BFINAL=1 */
                enqueue8(strm, 3, 3);
                send_eob(strm);
                strm._state = StreamState.SLZ_ST_DONE;
            }

            flush_bits(strm);

            // return the output length
            return strm._outpos;
        }


        /* Flushes pending bits and sends the gzip trailer for stream <strm> into
         * buffer <buf>. When it's done, the stream state is updated to SLZ_ST_END. It
         * returns the number of bytes emitted. The trailer consists in flushing the
         * possibly pending bits from the queue (up to 24 bits), rounding to the next
         * byte, then 4 bytes for the CRC and another 4 bytes for the input length.
         * That may abount to 4+4+4 = 12 bytes, that the caller must ensure are
         * available before calling the function. Note that if the initial header was
         * never sent, it will be sent first as well (10 extra bytes).
         */
        private static int slz_rfc1952_finish(SlzState strm, byte[] buf)
        {
            strm.outbuf = buf;
            strm._outpos = 0;


            if (strm._state == StreamState.SLZ_ST_INIT)
            {
                slz_rfc1952_send_header(strm);
            }

            slz_rfc1951_finish(strm, strm.outbuf);
            copy_32b(strm, strm._crc32);
            copy_32b(strm, strm._ilen);
            strm._state = StreamState.SLZ_ST_END;

            return strm._outpos;
        }

        /* Flushes pending bits and sends the gzip trailer for stream <strm> into
         * buffer <buf>. When it's done, the stream state is updated to SLZ_ST_END. It
         * returns the number of bytes emitted. The trailer consists in flushing the
         * possibly pending bits from the queue (up to 24 bits), rounding to the next
         * byte, then 4 bytes for the CRC. That may abount to 4+4 = 8 bytes, that the
         * caller must ensure are available before calling the function. Note that if
         * the initial header was never sent, it will be sent first as well (2 extra
         * bytes).
         */
        private static int slz_rfc1950_finish(SlzState strm, byte[] buf)
        {
            strm.outbuf = buf;
            strm._outpos = 0;

            if (strm._state == StreamState.SLZ_ST_INIT)
            {
                slz_rfc1952_send_header(strm);
            }


            slz_rfc1951_finish(strm, strm.outbuf);

            strm[0] = (byte)((strm._crc32 >> 24) & 0xff);
            strm[1] = (byte)((strm._crc32 >> 16) & 0xff);
            strm[2] = (byte)((strm._crc32 >> 8) & 0xff);
            strm[3] = (byte)((strm._crc32 >> 0) & 0xff);

            strm._outpos += 4;
            strm._state = StreamState.SLZ_ST_END;

            return strm._outpos;
        }


        /* Flushes pending bits and sends the trailer for stream <strm> into buffer
         * <buf> if needed. When it's done, the stream state is updated to SLZ_ST_END.
         * It returns the number of bytes emitted. The trailer consists in flushing the
         * possibly pending bits from the queue (up to 24 bits), rounding to the next
         * byte, then 4 bytes for the CRC when doing zlib/gzip, then another 4 bytes
         * for the input length for gzip. That may abount to 4+4+4 = 12 bytes, that the
         * caller must ensure are available before calling the function.
         */
        public static int slz_finish(SlzState strm, byte[] buf)
        {
            int ret;

            if (strm.Format == SlzFormat.SLZ_FMT_GZIP)
                ret = slz_rfc1952_finish(strm, buf);
            else if (strm.Format == SlzFormat.SLZ_FMT_ZLIB)
                ret = slz_rfc1950_finish(strm, buf);
            else /* deflate for other ones */
                ret = slz_rfc1951_finish(strm, buf);

            return ret;
        }


        ref struct ReadOnlyCompositeSpan
        {
            private Span<uint> Offset0;
            private Span<uint> Offset1;
            private Span<uint> Offset2;
            private Span<uint> Offset3;
            // private Span<byte> Input;

            public uint this[int index]
            {
                get
                {
                    switch (index % 4)
                    {
                        case 0:
                            return Offset0[index >> 2];
                        case 1:
                            return Offset1[index >> 2];
                        case 2:
                            return Offset2[index >> 2];
                        default:
                            return Offset3[index >> 2];
                    }
                }

                set
                {
                    switch (index % 4)
                    {
                        case 0:
                            Offset0[index >> 2] = value;
                            break;
                        case 1:
                            Offset1[index >> 2] = value;
                            break;
                        case 2:
                            Offset2[index >> 2] = value;
                            break;
                        default:
                            Offset3[index >> 2] = value;
                            break;
                    }
                }
            }

            // constructs 4 uint indexsers at each position
            public ReadOnlyCompositeSpan(byte[] input)
            {
                // Input = new Span<byte>(input, 0, input.Length);
                Offset0 = MemoryMarshal.Cast<byte, uint>(new Span<byte>(input, 0, input.Length));
                Offset1 = MemoryMarshal.Cast<byte, uint>(new Span<byte>(input, 0, input.Length));
                Offset2 = MemoryMarshal.Cast<byte, uint>(new Span<byte>(input, 0, input.Length));
                Offset3 = MemoryMarshal.Cast<byte, uint>(new Span<byte>(input, 0, input.Length));
            }
        }

        /// <summary>
        /// Deflate Encoding
        /// </summary>
        /// <param name="strm"></param>
        /// <param name="output"></param>
        /// <param name="input"></param>
        /// <param name="ilen"></param>
        /// <param name="more"></param>
        /// <returns></returns>
        private static long slz_rfc1951_encode(SlzState strm, byte[] output, byte[] input, long ilen,
            bool more)
        {
            strm._ilen += (uint)ilen;

            long rem = ilen;
            ulong pos = 0;
            ulong last;
            uint word = 0;
            long mlen;
            uint h;
            ulong ent;

            uint plit = 0;
            uint bit9 = 0;
            uint dist, code;

            // all of the back references it's a 8K block
            ulong[] refs = new ulong[1 << HASH_BITS];

            ResetBackReferences(refs);

            strm.inbuf = input;
            strm.outbuf = output;


            // what I want in 5 span<T>
            // 0, 1, 2, 3 
            // and a byte one
            // then I want to choose which one I use the by offset


            if (strm.Level == 0)
            {
                plit = (uint)ilen;
                pos = (uint)ilen;
                bit9 = 52; /* force literal dump */
                goto final_lit_dump;
            }


            while (rem >= 4)
            {
                word = ((uint)input[pos] << 8) + ((uint)input[pos + 1] << 16) + ((uint)input[pos + 2] << 24);
                word = ((uint)input[pos + 3] << 24) + (word >> 8);

                // this makes a lot of instructions for some reason
                // uint word2 = ((uint) input[pos + 3] << 24) + ((((uint)input[pos] << 8) + ((uint)input[pos + 1] << 16) + ((uint)input[pos + 2] << 24)) >> 8);


                h = slz_hash(word);

                ent = refs[h];
                last = (uint)ent;
                ent >>= 32;
                refs[h] = ((ulong)pos) + ((ulong)word << 32);

                if ((uint)ent != word)
                {
                    // send_as_lit:
                    rem--;
                    plit++;
                    bit9 += (byte)word >= 144 ? 1U : 0U;
                    pos++;
                    continue;
                }

                /* We reject pos = last and pos > last+32768 */
                if ((ulong)(pos - last - 1) >= 32768)
                {
                    // send_as_lit:
                    rem--;
                    plit++;
                    bit9 += (byte)word >= 144 ? 1U : 0U;
                    pos++;
                    continue;
                }

                /* Note: cannot encode a length larger than 258 bytes */
                // mlen = memmatch(in +pos + 4, in +last + 4, (rem > 258 ? 258 : rem) - 4) + 4;
                mlen = memmatch(input, input, (int)pos + 4, (int)last + 4, (int)(rem > 258 ? 258 : rem) - 4) + 4;

                if (bit9 >= 52 && mlen < 6)
                {
                    // send_as_lit:
                    rem--;
                    plit++;
                    bit9 += (byte)word >= 144 ? 1U : 0U;
                    pos++;
                    continue;
                }

                /* compute the output code, its size and the length's size in
                 * bits to know if the reference is cheaper than literals.
                 */
                code = Tables.FixedHuffmanLength[mlen];

                /* direct mapping of dist->huffman code */
                dist = Tables.FixedHuffmanDistance[pos - last - 1];

                /* if encoding the dist+length is more expensive than sending
                 * the equivalent as bytes, lets keep the literals.
                 */
                if ((dist & 0x1f) + (code >> 16) + 8 >= 8 * mlen + bit9)
                {
                    // send_as_lit:
                    rem--;
                    plit++;
                    bit9 += (byte)word >= 144 ? 1U : 0U;
                    pos++;
                    continue;
                }

                /* first, copy pending literals */
                if (plit != 0)
                {
                    /* Huffman encoding requires 9 bits for octets 144..255, so this
                     * is a waste of space for binary data. Switching between Huffman
                     * and no-comp then huffman consumes 52 bits (7 for EOB + 3 for
                     * block type + 7 for alignment + 32 for LEN+NLEN + 3 for next
                     * block. Only use plain literals if there are more than 52 bits
                     * to save then.
                     */
                    if (bit9 >= 52)
                        copy_lit(strm, (int)pos - (int)plit, plit, true);
                    else
                        copy_lit_huff(strm, (int)pos - (int)plit, (int)plit, true);

                    plit = 0;
                }

                /* use mode 01 - fixed huffman */
                if (strm._state == StreamState.SLZ_ST_EOB)
                {
                    strm._state = StreamState.SLZ_ST_FIXED;
                    enqueue8(strm, 0x02, 3); // BTYPE = 01, BFINAL = 0
                }

                /* copy the length first */
                enqueue24(strm, code & 0xFFFF, code >> 16);

                /* in fixed huffman mode, dist is fixed 5 bits */
                enqueue24(strm, dist >> 5, dist & 0x1f);
                bit9 = 0;
                rem -= mlen;
                pos += (ulong)mlen;
            }


            if (rem > 0)
            {
                /* we're reading the 1..3 last bytes */
                plit += (uint)rem;
                do
                {
                    bit9 += (input[pos++] >= 144) ? (uint)1 : 0;
                } while ((--rem) > 0);
            }

            final_lit_dump:
            /* now copy remaining literals or mark the end */
            if (plit > 0)
            {
                if (bit9 >= 52)
                    copy_lit(strm, (int)pos - (int)plit, plit, more);
                else
                    copy_lit_huff(strm, (int)pos - (int)plit, (int)plit, more);
            }

            return strm._outpos;
        }

        /* Initializes stream <strm> for use with the zlib format (rfc1952). The
         * compression level passed in <level> is set. This value can only be 0 (no
         * compression) or 1 (compression) and other values will lead to unpredictable
         * behaviour. The function always returns 0.
         */
        private static int slz_rfc1950_init(SlzState strm, int level)
        {

            strm._state = StreamState.SLZ_ST_INIT;
            strm.Level = level;
            strm.Format = SlzFormat.SLZ_FMT_ZLIB;
            strm._crc32 = 1; // rfc1950/zlib starts with initial crc=1
            strm._ilen = 0;
            strm._qbits = 0;
            strm._queue = 0;
            return 0;
        }

        /* Encodes the block according to rfc1950. This means that the CRC of the input
         * block is computed according to the ADLER32 algorithm. If the header was never
         * sent, it may be sent first. The number of output bytes is returned.
         */
        private static long slz_rfc1950_encode(SlzState strm, byte[] output, byte[] input, long ilen,
            bool more)
        {

            long ret = 0;

            if (strm._state == StreamState.SLZ_ST_INIT)
            {
                ret += slz_rfc1950_send_header(strm);
            }

            strm._crc32 = slz_adler32_block(strm._crc32, input, ilen);


            // todo: this is already moved along from the header
            ret += slz_rfc1951_encode(strm, output, input, ilen, more);

            return ret;
        }

        /* Initializes stream <strm> for use with raw deflate (rfc1951). The CRC is
         * unused but set to zero. The compression level passed in <level> is set. This
         * value can only be 0 (no compression) or 1 (compression) and other values
         * will lead to unpredictable behaviour. The function always returns 0.
         */
        private static long slz_rfc1951_init(SlzState strm, int level)
        {
            strm._state = StreamState.SLZ_ST_EOB; // no header
            strm.Level = level;
            strm.Format = SlzFormat.SLZ_FMT_DEFLATE;
            strm._crc32 = 0;
            strm._ilen = 0;
            strm._qbits = 0;
            strm._queue = 0;
            return 0;
        }

        /* Encodes the block according to the format used by the stream. This means
         * that the CRC of the input block may be computed according to the CRC32 or
         * adler-32 algorithms. The number of output bytes is returned.
         */
        public static long slz_encode(SlzState strm, byte[] output, byte[] input, long ilen, bool more)
        {
            long ret;

            strm.outbuf = output;
            strm._outpos = 0;

            if (strm.Format == SlzFormat.SLZ_FMT_GZIP)
                ret = slz_rfc1952_encode(strm, output, input, ilen, more);
            else if (strm.Format == SlzFormat.SLZ_FMT_ZLIB)
                ret = slz_rfc1950_encode(strm, output, input, ilen, more);
            else /* deflate for other ones */
                ret = slz_rfc1951_encode(strm, output, input, ilen, more);

            return ret;
        }


        /* Initializes stream <strm>. It will configure the stream to use format
         * <format> for the data, which must be one of SLZ_FMT_*. The compression level
         * passed in <level> is set. This value can only be 0 (no compression) or 1
         * (compression) and other values will lead to unpredictable behaviour. The
         * function should always return 0.
         */
        private static long slz_init(SlzState strm, int level, SlzFormat format)
        {

            long ret;

            if (format == SlzFormat.SLZ_FMT_GZIP)
                ret = slz_rfc1952_init(strm, level);
            else if (format == SlzFormat.SLZ_FMT_ZLIB)
                ret = slz_rfc1950_init(strm, level);
            else
            {
                /* deflate for anything else */
                ret = slz_rfc1951_init(strm, level);
                strm.Format = format;
            }


            return ret;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long memmatch(byte[] a, byte[] b, int pos, int last, int max)
        {
            // we are going to compare 64 at a time which is GRATE
            var spanA = new ReadOnlySpan<byte>(a, (int)pos, (pos + max >= a.Length ? a.Length - pos : max)); // a window into that array
            var spanB = new ReadOnlySpan<byte>(b, (int)last, (last + max >= a.Length ? a.Length - last : max)); // a window into that array
            var vectorA = MemoryMarshal.Cast<byte, Vector<uint>>(spanA);
            var vectorB = MemoryMarshal.Cast<byte, Vector<uint>>(spanB);

            int i = 0;

            // find the first non-match via the vector code
            for (; i < vectorA.Length; i++)
            {
                if (vectorA[i] != vectorB[i])
                    break;
            }

            for (i = i * sizeof(uint); i < spanA.Length; i++)
            {
                if (spanA[i] != spanB[i])
                    break;
            }

            return i;
        }


        // todo: optimize me
        //private static long memmatch(byte[] a, byte[] b, long pos, long last, long max)
        //{
        //    long len = 0;

        //    while (len < max)
        //    {
        //        if (a[len + pos] != b[len + last])
        //            break;

        //        len++;
        //    }

        //    // var othertest = memmatchBySpan(a, b, (int)pos, (int)last, (int)max, (int)len);

        //    return len;
        //}


    }
}










