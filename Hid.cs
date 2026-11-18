using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

internal sealed class Hid
{
    public string Path, Product;
    public ushort Vendor, Pid;
    public Caps Report;

    [StructLayout(LayoutKind.Sequential)]
    public struct Caps
    {
        public ushort Usage, Page, Input, Output, Feature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)]
        public ushort[] ReservedAndCounts;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Interface
    {
        public int Size;
        public Guid Class;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle file, [Out] byte[] buffer, int length);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_SetFeature(SafeFileHandle file, byte[] buffer, int length);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetFeature(SafeFileHandle file, [In, Out] byte[] buffer, int length);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr window, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, int index, ref Interface item);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref Interface item, IntPtr detail,
        int size, out int required, IntPtr device);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, int share, IntPtr security,
        int disposition, int flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    public SafeFileHandle Open(bool overlapped)
    {
        return CreateFile(Path, 0xc0000000, 3, IntPtr.Zero, 3, overlapped ? 0x40000000 : 0, IntPtr.Zero);
    }

    public static List<Hid> Enumerate(Action<string> trace = null)
    {
        var devices = new List<Hid>();
        Guid guid;
        HidD_GetHidGuid(out guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) return devices;
        try
        {
            for (int index = 0; ; index++)
            {
                var item = new Interface { Size = Marshal.SizeOf(typeof(Interface)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref item))
                {
                    if (trace != null) trace("HID enumeration ended: index=" + index + ", Win32=" + Marshal.GetLastWin32Error());
                    break;
                }
                int size;
                SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out size, IntPtr.Zero);
                if (size < 8) continue;
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref item, detail, size, out size, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    var ids = System.Text.RegularExpressions.Regex.Match(path, @"vid_(3710|054c)&pid_([0-9a-f]{4})",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!ids.Success) continue;
                    ushort vendor = Convert.ToUInt16(ids.Groups[1].Value, 16);
                    ushort pid = Convert.ToUInt16(ids.Groups[2].Value, 16);
                    if (vendor == 0x054c && pid != 0x0ec2 && pid != 0x0ec3) continue;
                    using (var file = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (file.IsInvalid && trace != null)
                            trace(string.Format("{0:X4}:{1:X4} Open failed: Win32={2}", vendor, pid, Marshal.GetLastWin32Error()));
                        IntPtr data;
                        if (file.IsInvalid || !HidD_GetPreparsedData(file, out data)) continue;
                        try
                        {
                            Caps caps;
                            if (HidP_GetCaps(data, out caps) != 0x110000) continue;
                            var name = new byte[256];
                            var product = HidD_GetProductString(file, name, name.Length)
                                ? Encoding.Unicode.GetString(name).TrimEnd('\0') : "";
                            devices.Add(new Hid { Path = path, Vendor = vendor, Pid = pid, Report = caps, Product = product });
                        }
                        finally { HidD_FreePreparsedData(data); }
                    }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    public T Exchange<T>(byte[] request, Func<byte[], int, T> parse, int milliseconds) where T : class
    {
        using (var file = Open(true))
        {
            if (file.IsInvalid) return null;
            try
            {
                // Buffer size 1 bypasses stream buffering so each write reaches HID immediately.
                using (var stream = new FileStream(file, FileAccess.ReadWrite, 1, true))
                {
                    var deadline = Stopwatch.StartNew();
                    var output = stream.WriteAsync(request, 0, request.Length);
                    if (!Finish(file, output, milliseconds)) return null;
                    var response = new byte[Report.Input];
                    while (deadline.ElapsedMilliseconds < milliseconds)
                    {
                        var input = stream.ReadAsync(response, 0, response.Length);
                        if (!Finish(file, input, milliseconds - (int)deadline.ElapsedMilliseconds)) return null;
                        int count = input.Result;
                        if (count == 0) return null;
                        var result = parse(response, count);
                        if (result != null) return result;
                    }
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (UnauthorizedAccessException) { }
            catch (AggregateException error)
            {
                error.Handle(e => e is IOException || e is OperationCanceledException || e is UnauthorizedAccessException);
            }
        }
        return null;
    }

    private static bool Finish(SafeFileHandle file, Task io, int milliseconds)
    {
        if (io.Wait(Math.Max(0, milliseconds))) return true;
        CancelIoEx(file, IntPtr.Zero);
        // Drain the canceled operation before releasing its file and report buffer.
        io.GetAwaiter().GetResult();
        return false;
    }
}
