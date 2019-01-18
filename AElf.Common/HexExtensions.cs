using System;
using AElf.Common;

namespace AElf.Common
{
    public static class HexExtensions
    {
        public static string ToHex(this long n)
        {
            return BitConverter.GetBytes(n).ToHex();
        }
        public static string ToHex(this int n)
        {
            return BitConverter.GetBytes(n).ToHex();
        }

        public static string DumpBase58(this int n)
        {
            return BitConverter.GetBytes(n).ToPlainBase58();
        }

        public static byte[] DumpByteArray(this int n)
        {
            return BitConverter.GetBytes(n);
        }
    }
}