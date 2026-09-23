using System;
using System.Runtime.InteropServices;

namespace Spark
{
    public static class TapeFFI
    {
        private const string DllName = "tape.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong TapeCreate([MarshalAs(UnmanagedType.LPStr)] string cPath, long startUnixMs);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int TapeWriteFrame(ulong handle, long timestampMs, [MarshalAs(UnmanagedType.LPStr)] string cJsonStr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int TapeClose(ulong handle);
    }
}
