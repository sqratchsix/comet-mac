using System.Runtime.InteropServices;

namespace Comet.Core;

/// <summary>
/// A Stream over one claimed USB interface's bulk endpoints, so that
/// <see cref="ActiveSerialPort"/> can run the same read loop and idle-timer
/// coalescing against a modem's AT interface as it does against a /dev/cu device.
///
/// Reads poll libusb in short slices so that closing never waits on a transfer
/// that will not finish. Dispose takes both I/O locks before releasing the
/// interface, which keeps libusb from freeing the handle under a live transfer.
/// </summary>
public sealed class UsbBulkStream : Stream
{
    private const uint ReadSliceMs = 100;
    private const int TransferSize = 16384;

    private readonly object readLock = new();
    private readonly object writeLock = new();
    private readonly byte[] transferBuffer = new byte[TransferSize];
    private IntPtr handle;
    private readonly int interfaceNumber;
    private readonly byte inEndpoint;
    private readonly byte outEndpoint;
    private readonly int maxPacket;
    private volatile bool closed;

    // Bytes from a transfer larger than the caller's buffer, handed out on the next read.
    private byte[] leftover = Array.Empty<byte>();
    private int leftoverOffset;

    private UsbBulkStream(IntPtr handle, int interfaceNumber, byte inEndpoint, byte outEndpoint, int maxPacket)
    {
        this.handle = handle;
        this.interfaceNumber = interfaceNumber;
        this.inEndpoint = inEndpoint;
        this.outEndpoint = outEndpoint;
        this.maxPacket = maxPacket;
    }

    public override int WriteTimeout { get; set; } = 2000;
    public override bool CanTimeout => true;

    /// <summary>
    /// Opens the interface named by a path from <see cref="UsbBulkEnumerator"/>.
    /// Throws IOException with a readable reason when it cannot.
    /// </summary>
    public static UsbBulkStream Open(string path)
    {
        if (!UsbBulkEnumerator.TryParsePath(path, out int vendorId, out int productId, out string location,
                out int interfaceNumber))
            throw new IOException($"Not a USB interface path: {path}");
        if (!LibUsb.IsAvailable)
            throw new IOException("libusb is not installed. Install it with: brew install libusb");

        IntPtr device = IntPtr.Zero;
        nint count = LibUsb.libusb_get_device_list(LibUsb.Context, out IntPtr list);
        if (count < 0) throw new IOException($"libusb could not list devices ({count}).");

        IntPtr opened = IntPtr.Zero;
        try
        {
            for (int i = 0; i < count && device == IntPtr.Zero; i++)
            {
                IntPtr candidate = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                var descriptor = new byte[18];
                if (LibUsb.libusb_get_device_descriptor(candidate, descriptor) != 0) continue;
                int vid = descriptor[8] | (descriptor[9] << 8);
                int pid = descriptor[10] | (descriptor[11] << 8);
                if (vid == vendorId && pid == productId && UsbBulkEnumerator.GetLocation(candidate) == location)
                    device = candidate;
            }
            if (device == IntPtr.Zero) throw new IOException("The USB device is no longer connected.");

            if (!TryFindEndpoints(device, interfaceNumber, out byte inEp, out byte outEp, out int packet))
                throw new IOException($"USB interface {interfaceNumber} has no bulk endpoint pair.");

            int rc = LibUsb.libusb_open(device, out opened);
            if (rc != 0) throw new IOException($"Could not open the USB device (libusb {rc}).");

            rc = LibUsb.libusb_claim_interface(opened, interfaceNumber);
            if (rc != 0)
                throw new IOException($"Could not claim USB interface {interfaceNumber} (libusb {rc}). " +
                                      "Another program may be using it.");

            var stream = new UsbBulkStream(opened, interfaceNumber, inEp, outEp, packet);
            opened = IntPtr.Zero;
            return stream;
        }
        finally
        {
            if (opened != IntPtr.Zero) LibUsb.libusb_close(opened);
            LibUsb.libusb_free_device_list(list, 1);
        }
    }

    private static bool TryFindEndpoints(IntPtr device, int interfaceNumber, out byte inEp, out byte outEp,
        out int packet)
    {
        inEp = outEp = 0;
        packet = 64;
        if (LibUsb.libusb_get_active_config_descriptor(device, out IntPtr config) != 0) return false;
        try
        {
            int interfaceCount = Marshal.ReadByte(config, 4);
            IntPtr interfaces = Marshal.ReadIntPtr(config, 16);
            for (int i = 0; i < interfaceCount; i++)
            {
                IntPtr iface = interfaces + i * 16;
                if (Marshal.ReadInt32(iface, 8) < 1) continue;
                IntPtr alt = Marshal.ReadIntPtr(iface, 0);
                if (Marshal.ReadByte(alt, 2) != interfaceNumber) continue;
                return UsbBulkEnumerator.FindBulkEndpoints(Marshal.ReadIntPtr(alt, 16), Marshal.ReadByte(alt, 4),
                    out inEp, out outEp, out packet);
            }
            return false;
        }
        finally
        {
            LibUsb.libusb_free_config_descriptor(config);
        }
    }

    public override bool CanRead => !closed;
    public override bool CanWrite => !closed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);

    public override int Read(Span<byte> buffer) => ReadCore(buffer, CancellationToken.None);

    /// <summary>
    /// Blocks the calling thread. ActiveSerialPort already reads on its own task,
    /// so there is nothing to gain from a true async transfer here.
    /// </summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return ValueTask.FromResult(ReadCore(buffer.Span, cancellationToken));
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<int>(ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Returns at least one byte, or 0 once the stream is closed or the device is gone.</summary>
    private int ReadCore(Span<byte> destination, CancellationToken token)
    {
        if (destination.Length == 0) return 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            lock (readLock)
            {
                if (closed) return 0;

                if (leftoverOffset < leftover.Length)
                    return TakeLeftover(destination);

                // Always ask for a whole number of max-size packets so the device can
                // never overrun the transfer; anything past the caller's span is kept.
                int rc = LibUsb.libusb_bulk_transfer(handle, inEndpoint, transferBuffer, transferBuffer.Length,
                    out int transferred, ReadSliceMs);

                if (transferred > 0)
                {
                    leftover = transferBuffer.AsSpan(0, transferred).ToArray();
                    leftoverOffset = 0;
                    return TakeLeftover(destination);
                }

                if (rc == LibUsb.ErrorNoDevice) return 0;
                if (rc != 0 && rc != LibUsb.ErrorTimeout)
                    throw new IOException($"USB read failed (libusb {rc}).");
            }
        }
    }

    private int TakeLeftover(Span<byte> destination)
    {
        int n = Math.Min(destination.Length, leftover.Length - leftoverOffset);
        leftover.AsSpan(leftoverOffset, n).CopyTo(destination);
        leftoverOffset += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (writeLock)
        {
            if (closed) throw new ObjectDisposedException(nameof(UsbBulkStream));

            byte[] data = buffer.ToArray();
            uint timeout = (uint)Math.Max(1, WriteTimeout);
            int rc = LibUsb.libusb_bulk_transfer(handle, outEndpoint, data, data.Length, out int transferred,
                timeout);
            if (rc == LibUsb.ErrorTimeout) throw new TimeoutException("USB write timed out.");
            if (rc != 0) throw new IOException($"USB write failed (libusb {rc}).");
            if (transferred != data.Length) throw new IOException("USB write was cut short.");

            // A transfer that fills its last packet exactly needs a zero-length packet
            // to tell the device the message has ended.
            if (data.Length > 0 && data.Length % maxPacket == 0)
                LibUsb.libusb_bulk_transfer(handle, outEndpoint, Array.Empty<byte>(), 0, out _, timeout);
        }
    }

    /// <summary>Drops anything already read from the device but not yet returned.</summary>
    public void DiscardBuffered()
    {
        lock (readLock)
        {
            leftover = Array.Empty<byte>();
            leftoverOffset = 0;
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!closed)
        {
            closed = true;
            // Wait for any transfer in flight; reads give up within one slice.
            lock (readLock)
            lock (writeLock)
            {
                if (handle != IntPtr.Zero)
                {
                    LibUsb.libusb_release_interface(handle, interfaceNumber);
                    LibUsb.libusb_close(handle);
                    handle = IntPtr.Zero;
                }
            }
        }
        base.Dispose(disposing);
    }
}
