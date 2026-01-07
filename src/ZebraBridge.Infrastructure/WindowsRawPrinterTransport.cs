using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public sealed class WindowsRawPrinterTransport : IPrinterTransport
{
    private readonly string _printerName;

    public WindowsRawPrinterTransport(string printerName)
    {
        _printerName = printerName ?? string.Empty;
    }

    public Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PrinterNotFoundException("Windows raw transport can only be used on Windows.");
        }

        if (string.IsNullOrWhiteSpace(_printerName))
        {
            throw new PrinterNotFoundException("Printer name is required.");
        }

        if (!RawPrinterHelper.SendBytesToPrinter(_printerName, data))
        {
            throw new PrinterCommunicationException("Failed to send data to Windows printer.");
        }

        return Task.CompletedTask;
    }

    private static class RawPrinterHelper
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private sealed class DOC_INFO_1
        {
            public string? pDocName;
            public string? pOutputFile;
            public string? pDataType;
        }

        [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

        [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [System.Runtime.InteropServices.In] DOC_INFO_1 di);

        [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        public static bool SendBytesToPrinter(string printerName, byte[] bytes)
        {
            if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            {
                return false;
            }

            try
            {
                var docInfo = new DOC_INFO_1
                {
                    pDocName = "Zebra ZPL Job",
                    pDataType = "RAW"
                };

                if (!StartDocPrinter(hPrinter, 1, docInfo))
                {
                    return false;
                }

                try
                {
                    if (!StartPagePrinter(hPrinter))
                    {
                        return false;
                    }

                    try
                    {
                        var unmanagedPointer = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(bytes.Length);
                        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, unmanagedPointer, bytes.Length);
                        var ok = WritePrinter(hPrinter, unmanagedPointer, bytes.Length, out var written);
                        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(unmanagedPointer);
                        return ok && written == bytes.Length;
                    }
                    finally
                    {
                        EndPagePrinter(hPrinter);
                    }
                }
                finally
                {
                    EndDocPrinter(hPrinter);
                }
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }
    }
}
