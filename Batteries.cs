using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

internal sealed class Batteries
{
    public int? Mouse, Left, Right, Case;
    public bool Charging;
    public string MouseStatus = "対応するHIDが見つかりません";
    public string SonyStatus = "USBドングルが見つかりません";

    private static int? Percent(byte value) { return value <= 100 ? (int?)value : null; }

    public static Batteries Read(List<Hid> devices, Action<string> trace = null)
    {
        var result = new Batteries();
        foreach (var hid in devices.Where(d => d.Vendor == 0x3710)
            .OrderBy(d => d.Product.IndexOf("VSPO", StringComparison.OrdinalIgnoreCase) < 0)
            .ThenBy(d => d.Path.IndexOf("&mi_02", StringComparison.OrdinalIgnoreCase) < 0))
        {
            var caps = hid.Report;
            if (caps.Page == 0xff02 && caps.Input >= 17 && caps.Output >= 17)
            {
                result.MouseStatus = "HID応答なし";
                var request = new byte[caps.Output];
                request[0] = 8;
                request[1] = 4;
                request[16] = 0x49;
                Batteries mouse = null;
                for (int attempt = 0; attempt < 2 && mouse == null; attempt++)
                {
                    mouse = hid.Exchange(request, (data, count) =>
                    {
                        if (trace != null && count >= 2 && data[0] == 8 && data[1] == 4)
                            trace("Pulsar power reply: " + BitConverter.ToString(data, 0, count));
                        return ParseMouse(data, count);
                    }, 1500);
                }
                if (mouse != null)
                {
                    result.Mouse = mouse.Mouse;
                    result.Charging = mouse.Charging;
                }
            }
            else if (caps.Page == 0xffff && caps.Usage == 1 && caps.Feature == 65)
            {
                result.MouseStatus = "HID応答なし";
                using (var file = hid.Open(false))
                {
                    if (file.IsInvalid) continue;
                    var battery = ReadFeature(file, 0x81);
                    if (battery.HasValue) result.Mouse = Percent(battery.Value);
                    if (result.Mouse.HasValue) result.Charging = ReadFeature(file, 0x83) == 1;
                }
            }
            if (!result.Mouse.HasValue) continue;
            result.MouseStatus = string.Format("{0} [VID 3710 / PID {1:X4}]", hid.Product, hid.Pid);
            break;
        }

        foreach (var hid in devices.Where(d => d.Vendor == 0x054c && d.Pid == 0x0ec2 &&
            d.Report.Page == 0xff04 && d.Report.Input == 64 && d.Report.Output == 64))
        {
            result.SonyStatus = "USBドングル検出・イヤホン応答なし";
            int transaction = Guid.NewGuid().GetHashCode() & 0xffff;
            var sony = hid.Exchange(SonyRequest(transaction), (data, count) => ParseSony(data, count, transaction), 1800);
            if (sony == null) continue;
            result.Left = sony.Left;
            result.Right = sony.Right;
            result.Case = sony.Case;
            result.SonyStatus = "USBドングル接続";
            break;
        }
        if (result.SonyStatus == "USBドングルが見つかりません" && devices.Any(d => d.Vendor == 0x054c && d.Pid == 0x0ec3))
            result.SonyStatus = "USBドングルのスイッチをPC側にしてください";
        return result;
    }

    internal static Batteries ParseMouse(byte[] data, int count)
    {
        if (count < 10 || data[0] != 8 || data[1] != 4 || data[6] > 100) return null;
        return new Batteries { Mouse = data[6], Charging = data[7] == 1 };
    }

    private static byte? ReadFeature(Microsoft.Win32.SafeHandles.SafeFileHandle file, byte register)
    {
        var request = new byte[65];
        request[2] = 8;
        request[3] = register;
        request[4] = 1;
        request[63] = (byte)(9 + register);
        if (!Hid.HidD_SetFeature(file, request, request.Length)) return null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Thread.Sleep(45);
            var reply = new byte[65];
            if (!Hid.HidD_GetFeature(file, reply, reply.Length)) return null;
            int sum = 0;
            for (int i = 1; i < 63; i++) sum += reply[i];
            if (reply[1] == 1 && reply[2] == 8 && reply[3] == register &&
                sum == (reply[63] | (reply[64] << 8))) return reply[7];
        }
        return null;
    }

    internal static byte[] SonyRequest(int transaction)
    {
        var report = new byte[64];
        byte[] header = { 2, 12, 1, 0, 0xfc, 8, 0x96, 0xc3, 0x41, 4, 1, (byte)transaction, (byte)(transaction >> 8) };
        Array.Copy(header, report, header.Length);
        int sum = 0;
        for (int i = 6; i < 13; i++) sum += report[i];
        report[13] = (byte)sum;
        return report;
    }

    internal static Batteries ParseSony(byte[] data, int count, int transaction)
    {
        if (count < 20 || data[0] != 2 || data[1] != 18 || data[2] != 4 || data[3] != 0xff ||
            data[4] != 15 || data[6] != 0x96 || data[7] != 0xc3 || data[8] != 0x14 ||
            data[9] != 4 || data[10] != 0x10 || (data[11] | (data[12] << 8)) != transaction) return null;
        int sum = 0;
        for (int i = 5; i < 19; i++) sum += data[i];
        if ((byte)sum != data[19]) return null;
        return new Batteries { Left = Percent(data[14]), Right = Percent(data[16]), Case = Percent(data[18]) };
    }
}
