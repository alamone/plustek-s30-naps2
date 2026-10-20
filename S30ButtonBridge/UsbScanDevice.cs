using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace S30ButtonBridge;

/// <summary>
/// Minimal client for a still-image device bound to usbscan.sys (\\.\UsbscanN).
/// Mirrors what Plustek's ScanApi2_gl310_x64.dll does: overlapped handle,
/// IOCTL_WAIT_ON_DEVICE_EVENT for the interrupt pipe, WriteFile/ReadFile for bulk commands.
/// </summary>
sealed class UsbScanDevice : IDisposable
{
    // usbscan.h: CTL_CODE(FILE_DEVICE_USB_SCAN=0x8000, IOCTL_INDEX(0x800)+n, METHOD_BUFFERED, FILE_ANY_ACCESS)
    const uint IOCTL_WAIT_ON_DEVICE_EVENT = 0x80002008;
    const uint IOCTL_GET_DEVICE_DESCRIPTOR = 0x80002018;
    const uint IOCTL_GET_PIPE_CONFIGURATION = 0x80002028;
    const uint IOCTL_RESET_PIPE = 0x8000201C;
    const uint IOCTL_CANCEL_IO = 0x80002004;
    const int EVENT_PIPE = 0; // usbscan.h PIPE_TYPE
    const int USBSCAN_PIPE_INTERRUPT = 3;

    readonly SafeFileHandle _handle;

    public string Path { get; }
    public ushort VendorId { get; private set; }
    public ushort ProductId { get; private set; }
    public ushort BcdDevice { get; private set; }
    public int InterruptPacketSize { get; private set; } = 8;

    UsbScanDevice(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
    }

    /// <summary>Finds and opens the first \\.\UsbscanN whose descriptor matches vid/pid, or returns null.</summary>
    public static UsbScanDevice? Find(ushort vid, ushort pid)
    {
        for (int i = 0; i < 32; i++)
        {
            string path = $@"\\.\Usbscan{i}";
            var h = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }

            var dev = new UsbScanDevice(path, h);
            try
            {
                dev.ReadDescriptor();
                if (dev.VendorId == vid && dev.ProductId == pid)
                {
                    dev.ReadPipeConfiguration();
                    return dev;
                }
            }
            catch (Win32Exception) { }
            dev.Dispose();
        }
        return null;
    }

    void ReadDescriptor()
    {
        var buf = new byte[8]; // DEVICE_DESCRIPTOR { usVendorId, usProductId, usBcdDevice, usLanguageId }
        Ioctl(IOCTL_GET_DEVICE_DESCRIPTOR, buf, buf, TimeSpan.FromSeconds(5));
        VendorId = BitConverter.ToUInt16(buf, 0);
        ProductId = BitConverter.ToUInt16(buf, 2);
        BcdDevice = BitConverter.ToUInt16(buf, 4);
    }

    void ReadPipeConfiguration()
    {
        // USBSCAN_PIPE_CONFIGURATION { ULONG NumberOfPipes; USBSCAN_PIPE_INFORMATION PipeInfo[8]; }
        // USBSCAN_PIPE_INFORMATION { USHORT MaximumPacketSize; UCHAR EndpointAddress; UCHAR Interval; ULONG PipeType; }
        var buf = new byte[4 + 8 * 8];
        int n = Ioctl(IOCTL_GET_PIPE_CONFIGURATION, null, buf, TimeSpan.FromSeconds(5));
        if (n < 4) return;
        int pipes = Math.Min(BitConverter.ToInt32(buf, 0), 8);
        for (int i = 0; i < pipes; i++)
        {
            int off = 4 + i * 8;
            if (BitConverter.ToInt32(buf, off + 4) == USBSCAN_PIPE_INTERRUPT)
                InterruptPacketSize = Math.Max(BitConverter.ToUInt16(buf, off), (ushort)1);
        }
    }

    /// <summary>
    /// Resets the interrupt (event) pipe, re-synchronising the USB data toggle. If a previous reader was
    /// killed with a wait pending, host and scanner can disagree on the toggle and the host silently
    /// drops the next packet as a duplicate, i.e. the next button press is lost.
    /// </summary>
    public void ResetEventPipe() =>
        Ioctl(IOCTL_RESET_PIPE, BitConverter.GetBytes(EVENT_PIPE), Array.Empty<byte>(), TimeSpan.FromSeconds(5));

    /// <summary>Aborts every pending wait on the interrupt (event) pipe.</summary>
    public void CancelEventPipe() =>
        Ioctl(IOCTL_CANCEL_IO, BitConverter.GetBytes(EVENT_PIPE), Array.Empty<byte>(), TimeSpan.FromSeconds(5));

    /// <summary>Starts an event wait and returns without waiting (used to reproduce the driver's orphaned waits).</summary>
    public PendingWait BeginWait()
    {
        var op = new OverlappedOp(Math.Max(InterruptPacketSize, 8));
        bool ok = Native.DeviceIoControl(_handle, IOCTL_WAIT_ON_DEVICE_EVENT, IntPtr.Zero, 0,
            op.Buffer, Math.Max(InterruptPacketSize, 8), out _, op.Overlapped);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        if (!ok && err != Native.ERROR_IO_PENDING) { op.Dispose(); throw new Win32Exception(err); }
        return new PendingWait(this, op);
    }

    public sealed class PendingWait : IDisposable
    {
        readonly UsbScanDevice _dev;
        readonly OverlappedOp _op;
        internal PendingWait(UsbScanDevice dev, OverlappedOp op) { _dev = dev; _op = op; }
        public WaitHandle Handle => _op.Event;
        public bool Done => _op.Event.WaitOne(0);

        /// <summary>After Done: the packet, or null if the wait was cancelled / failed (with the error code).</summary>
        public byte[]? Result(out int error)
        {
            error = 0;
            if (!Native.GetOverlappedResult(_dev._handle, _op.Overlapped, out int n, false))
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }
            var b = new byte[n];
            Marshal.Copy(_op.Buffer, b, 0, n);
            return b;
        }

        public void Dispose()
        {
            if (!Done)
            {
                Native.CancelIoEx(_dev._handle, _op.Overlapped);
                Native.GetOverlappedResult(_dev._handle, _op.Overlapped, out _, true);
            }
            _op.Dispose();
        }
    }

    /// <summary>
    /// Blocks until the device posts an interrupt packet. Returns the packet, or null if
    /// <paramref name="cancel"/> was signalled. Throws Win32Exception on device errors (e.g. unplug).
    /// </summary>
    public byte[]? WaitForEvent(WaitHandle cancel) => WaitForEvent(cancel, Timeout.InfiniteTimeSpan, out _);

    /// <summary>
    /// Like <see cref="WaitForEvent(WaitHandle)"/>, but if nothing arrives within <paramref name="sweepEvery"/>,
    /// aborts every pending wait on the event pipe and returns an empty array (<paramref name="swept"/> = true).
    /// Plustek's driver (loaded in the WIA service during scans) polls the button with a zero-timeout wait it
    /// never cancels; that orphaned wait sits ahead of ours and swallows the next press. Sweeping clears it.
    /// </summary>
    public byte[]? WaitForEvent(WaitHandle cancel, TimeSpan sweepEvery, out bool swept)
    {
        swept = false;
        var buf = new byte[Math.Max(InterruptPacketSize, 8)];
        using var op = new OverlappedOp(buf.Length);
        bool ok = Native.DeviceIoControl(_handle, IOCTL_WAIT_ON_DEVICE_EVENT, IntPtr.Zero, 0,
            op.Buffer, buf.Length, out _, op.Overlapped);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        if (!ok && err != Native.ERROR_IO_PENDING) throw new Win32Exception(err);

        int which = WaitHandle.WaitAny(new[] { op.Event, cancel }, sweepEvery);
        if (which == 1)
        {
            Native.CancelIoEx(_handle, op.Overlapped);
            Native.GetOverlappedResult(_handle, op.Overlapped, out _, true);
            return null;
        }
        if (which == WaitHandle.WaitTimeout)
        {
            try { CancelEventPipe(); }                     // aborts orphans and our own wait
            catch (Exception ex) when (ex is Win32Exception or TimeoutException) { Native.CancelIoEx(_handle, op.Overlapped); }
            // A press may have landed in our wait just before the abort: keep it if so.
            if (Native.GetOverlappedResult(_handle, op.Overlapped, out int n, true) && n > 0)
            {
                Marshal.Copy(op.Buffer, buf, 0, n);
                return buf[..n];
            }
            swept = true;
            return Array.Empty<byte>();
        }
        if (!Native.GetOverlappedResult(_handle, op.Overlapped, out int got, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Marshal.Copy(op.Buffer, buf, 0, got);
        return buf[..got];
    }

    /// <summary>
    /// Decodes a button press the way ScanApi2_gl310's detect thread does. The S30 sends one packet per
    /// press, e.g. 04 00 01 B1: [2]=01/10/11 is a panel key, bit7 of [3] = pressed, low bits = ASCII
    /// function digit ('1' = 0x31). [2]=00 with [3]=1/2 is the older plain key code.
    /// </summary>
    public static int? ParseButton(byte[] p)
    {
        if (p.Length < 4 || p[0] != 0x04) return null;
        if (p[2] == 0x00) return p[3] is 1 or 2 ? p[3] : null;
        if (p[2] is 0x01 or 0x10 or 0x11 && (p[3] & 0x80) != 0)
        {
            int code = (p[3] & 0x40) != 0 ? p[3] & 0x3F : p[3] & 0x7F;
            return code >= 0x30 ? code - 0x30 : code;
        }
        return null;
    }

    /// <summary>
    /// Sets the scanner's standby timeout (0 = never) with the vendor "PWR" bulk command that Plustek's
    /// Scanner Utility sends: "PWR" 00 type(0 = scanner sleep) minutes%60 minutes/60 00. Reply starts "STA".
    /// </summary>
    public byte[] SetStandby(int minutes) =>
        Command(new byte[] { (byte)'P', (byte)'W', (byte)'R', 0, 0, (byte)(minutes % 60), (byte)(minutes / 60), 0 },
                8, TimeSpan.FromSeconds(10));

    public static bool IsStandbyReplyOk(byte[] reply) =>
        reply.Length >= 3 && reply[0] == 'S' && reply[1] == 'T' && reply[2] == 'A';

    /// <summary>Writes a command to the bulk-out pipe and reads a fixed-size reply from bulk-in.</summary>
    public byte[] Command(byte[] cmd, int replyLength, TimeSpan timeout)
    {
        using (var w = new OverlappedOp(cmd.Length))
        {
            Marshal.Copy(cmd, 0, w.Buffer, cmd.Length);
            bool ok = Native.WriteFile(_handle, w.Buffer, cmd.Length, out _, w.Overlapped);
            Complete(w, ok, timeout);
        }
        using var r = new OverlappedOp(replyLength);
        bool rok = Native.ReadFile(_handle, r.Buffer, replyLength, out _, r.Overlapped);
        int n = Complete(r, rok, timeout);
        var reply = new byte[n];
        Marshal.Copy(r.Buffer, reply, 0, n);
        return reply;
    }

    int Ioctl(uint code, byte[]? input, byte[] output, TimeSpan timeout)
    {
        int len = Math.Max(input?.Length ?? 0, output.Length);
        using var op = new OverlappedOp(len);
        if (input != null) Marshal.Copy(input, 0, op.Buffer, input.Length);
        bool ok = Native.DeviceIoControl(_handle, code, input == null ? IntPtr.Zero : op.Buffer, input?.Length ?? 0,
            op.Buffer, output.Length, out _, op.Overlapped);
        int n = Complete(op, ok, timeout);
        Marshal.Copy(op.Buffer, output, 0, Math.Min(n, output.Length));
        return n;
    }

    int Complete(OverlappedOp op, bool ok, TimeSpan timeout)
    {
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != Native.ERROR_IO_PENDING) throw new Win32Exception(err);
            if (!op.Event.WaitOne(timeout))
            {
                Native.CancelIoEx(_handle, op.Overlapped);
                Native.GetOverlappedResult(_handle, op.Overlapped, out _, true);
                throw new TimeoutException("USB request timed out");
            }
        }
        if (!Native.GetOverlappedResult(_handle, op.Overlapped, out int n, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return n;
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>Unmanaged OVERLAPPED + data buffer so the kernel never sees memory the GC could move.</summary>
    internal sealed class OverlappedOp : IDisposable
    {
        public readonly ManualResetEvent Event = new(false);
        public readonly IntPtr Overlapped;
        public readonly IntPtr Buffer;

        public OverlappedOp(int size)
        {
            Overlapped = Marshal.AllocHGlobal(32);
            for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(Overlapped, i, 0);
            Marshal.WriteIntPtr(Overlapped, 24, Event.SafeWaitHandle.DangerousGetHandle()); // OVERLAPPED.hEvent
            Buffer = Marshal.AllocHGlobal(Math.Max(size, 1));
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Buffer);
            Marshal.FreeHGlobal(Overlapped);
            Event.Dispose();
        }
    }
}

static class Native
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_IO_PENDING = 997;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize,
        IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, out int written, IntPtr overlapped);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, out int read, IntPtr overlapped);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool AttachConsole(int pid);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool AllocConsole();
}
