using System.Runtime.CompilerServices;

namespace libslzsharp2
{
    public static class Crc32
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint crc32_char(uint crc, byte x)
        {
            return Tables.Crc32Fast[0, (crc ^ x) & 0xff] ^ (crc >> 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint crc32_uint32(uint data)
        {
            data = Tables.Crc32Fast[3, (data >> 0) & 0xff] ^
                   Tables.Crc32Fast[2, (data >> 8) & 0xff] ^
                   Tables.Crc32Fast[1, (data >> 16) & 0xff] ^
                   Tables.Crc32Fast[0, (data >> 24) & 0xff];

            return data;
        }

        public static uint slz_crc32_by1(uint crc, byte[] buf, int offset, int len)
        {

            int n;

            for (n = offset; n < offset + len; n++)
                crc = crc32_char(crc, buf[n]);
            return crc;
        }
    }
}