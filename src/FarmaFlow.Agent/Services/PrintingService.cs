using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace FarmaFlow.Agent.Services;

public sealed class PrintingService
{
    public string[] Printers() => PrinterSettings.InstalledPrinters.Cast<string>().Order().ToArray();

    public void PrintRaw(string printerName, byte[] bytes)
    {
        if (!Printers().Contains(printerName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Impressora não encontrada: {printerName}");
        if (!RawPrinter.OpenPrinter(printerName, out var handle, IntPtr.Zero)) throw new InvalidOperationException("Não foi possível abrir a impressora.");
        try
        {
            var info = new RawPrinter.DocInfo { DocumentName = "FarmaFlow", DataType = "RAW" };
            RawPrinter.StartDocPrinter(handle, 1, info);
            RawPrinter.StartPagePrinter(handle);
            var memory = Marshal.AllocCoTaskMem(bytes.Length);
            try { Marshal.Copy(bytes, 0, memory, bytes.Length); RawPrinter.WritePrinter(handle, memory, bytes.Length, out _); }
            finally { Marshal.FreeCoTaskMem(memory); }
            RawPrinter.EndPagePrinter(handle);
            RawPrinter.EndDocPrinter(handle);
        }
        finally { RawPrinter.ClosePrinter(handle); }
    }

    private static class RawPrinter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public sealed class DocInfo { [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName = ""; [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile; [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW"; }
        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
        [DllImport("winspool.drv", SetLastError = true)] public static extern bool ClosePrinter(IntPtr handle);
        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] public static extern int StartDocPrinter(IntPtr handle, int level, [In] DocInfo info);
        [DllImport("winspool.drv", SetLastError = true)] public static extern bool EndDocPrinter(IntPtr handle);
        [DllImport("winspool.drv", SetLastError = true)] public static extern bool StartPagePrinter(IntPtr handle);
        [DllImport("winspool.drv", SetLastError = true)] public static extern bool EndPagePrinter(IntPtr handle);
        [DllImport("winspool.drv", SetLastError = true)] public static extern bool WritePrinter(IntPtr handle, IntPtr bytes, int count, out int written);
    }
}
