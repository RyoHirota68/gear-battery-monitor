using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--diagnostics")
        {
            var trace = new List<string>();
            var devices = Hid.Enumerate(trace.Add);
            var battery = Batteries.Read(devices, trace.Add);
            File.WriteAllLines(args[1], trace.Concat(devices.Select(d => string.Format(
                "{0:X4}:{1:X4} {2} page={3:X4} usage={4:X4} in={5} out={6} feature={7}",
                d.Vendor, d.Pid, d.Product, d.Report.Page, d.Report.Usage, d.Report.Input, d.Report.Output, d.Report.Feature)))
                .Concat(new[] { battery.MouseStatus, battery.SonyStatus,
                    string.Format("M={0} L={1} R={2} C={3}", battery.Mouse, battery.Left, battery.Right, battery.Case) }));
            return;
        }

        bool first;
        using (var mutex = new Mutex(true, @"Local\VspoInzoneBatteryMonitor", out first))
        {
            if (!first) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var tray = new Tray()) Application.Run(tray);
        }
    }
}

internal sealed class Tray : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "VspoInzoneBatteryMonitor";
    private const int Low = 20;
    private readonly string[] names = { "VSPO! GEAR マウス", "INZONE Buds 左", "INZONE Buds 右", "INZONE Buds ケース" };
    private readonly NotifyIcon[] icons = new NotifyIcon[4];
    private readonly Icon[] images = new Icon[4];
    private readonly string[] imageKeys = new string[4];
    private readonly bool[] alerted = new bool[4];
    private readonly ToolStripMenuItem[] rows = new ToolStripMenuItem[4];
    private readonly ToolStripMenuItem mouseStatus = new ToolStripMenuItem { Enabled = false };
    private readonly ToolStripMenuItem sonyStatus = new ToolStripMenuItem { Enabled = false };
    private readonly ToolStripMenuItem startup = new ToolStripMenuItem("Windows サインイン時に起動");
    private readonly ToolStripMenuItem refresh = new ToolStripMenuItem("今すぐ更新");
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly Timer timer = new Timer { Interval = 30000 };
    private bool reading, stopped;

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    public Tray()
    {
        for (int i = 0; i < icons.Length; i++)
        {
            rows[i] = new ToolStripMenuItem { Enabled = false };
            menu.Items.Add(rows[i]);
            icons[i] = new NotifyIcon { ContextMenuStrip = menu };
            icons[i].MouseClick += (sender, args) => { if (args.Button == MouseButtons.Left) Refresh(); };
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(mouseStatus);
        menu.Items.Add(sonyStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(refresh);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripMenuItem("終了", null, (sender, args) => ExitThread()));
        refresh.Click += (sender, args) => Refresh();
        startup.Click += (sender, args) => ToggleStartup();
        menu.Opening += (sender, args) => UpdateStartup();
        ShowReadings(new Batteries());
        foreach (var icon in icons) icon.Visible = true;
        timer.Tick += (sender, args) => Refresh();
        timer.Start();
        // The first tick runs after the WinForms message loop is ready.
        var firstPoll = new Timer { Interval = 100 };
        firstPoll.Tick += (sender, args) => { firstPoll.Dispose(); Refresh(); };
        firstPoll.Start();
    }

    private async void Refresh()
    {
        if (reading || stopped) return;
        reading = true;
        refresh.Enabled = false;
        try
        {
            var readings = await Task.Run(() => Batteries.Read(Hid.Enumerate()));
            if (!stopped) ShowReadings(readings);
        }
        catch (Exception error)
        {
            if (!stopped) ShowReadings(new Batteries
            {
                MouseStatus = "読み取りエラー: " + error.GetType().Name,
                SonyStatus = "次回の更新で再試行します"
            });
        }
        finally
        {
            reading = false;
            if (!stopped) refresh.Enabled = true;
        }
    }

    private void ShowReadings(Batteries readings)
    {
        int?[] values = { readings.Mouse, readings.Left, readings.Right, readings.Case };
        var warnings = new List<string>();
        mouseStatus.Text = "マウス — " + readings.MouseStatus;
        sonyStatus.Text = "イヤホン — " + readings.SonyStatus;
        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i];
            bool charging = i == 0 && readings.Charging;
            string detail = value.HasValue ? value.Value + "%" : "取得不可";
            string label = names[i] + "  " + detail + (charging ? "（充電中）" : "");
            rows[i].Text = label;
            icons[i].Text = label;
            var color = !value.HasValue ? Color.FromArgb(163, 175, 192) :
                charging ? Color.FromArgb(119, 238, 178) :
                value <= Low ? Color.FromArgb(255, 135, 144) :
                i == 0 ? Color.FromArgb(113, 225, 236) : Color.FromArgb(196, 178, 255);
            string key = detail + color.ToArgb();
            if (key != imageKeys[i])
            {
                var previous = images[i];
                images[i] = DrawIcon(i, value, color);
                icons[i].Icon = images[i];
                if (previous != null) previous.Dispose();
                imageKeys[i] = key;
            }

            if (value.HasValue && (value > Low || charging)) alerted[i] = false;
            if (value.HasValue && value <= Low && !charging && !alerted[i])
            {
                alerted[i] = true;
                warnings.Add(label);
            }
        }
        if (warnings.Count > 0)
            icons[0].ShowBalloonTip(7000, "バッテリー残量が低下しています", string.Join("\n", warnings), ToolTipIcon.Warning);
    }

    private static Icon DrawIcon(int device, int? value, Color color)
    {
        using (var bitmap = new Bitmap(32, 32))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(Color.FromArgb(28, 35, 50)))
        using (var accent = new SolidBrush(color))
        using (var stroke = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var side = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var digits = new Font("Segoe UI", value == 100 ? 15 : 19, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var tile = new GraphicsPath())
        using (var center = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            tile.AddArc(1, 1, 10, 10, 180, 90);
            tile.AddArc(21, 1, 10, 10, 270, 90);
            tile.AddArc(21, 21, 10, 10, 0, 90);
            tile.AddArc(1, 21, 10, 10, 90, 90);
            tile.CloseFigure();
            graphics.FillPath(background, tile);

            if (device == 0)
            {
                graphics.DrawEllipse(stroke, 12, 3, 8, 10);
                graphics.DrawLine(stroke, 16, 5, 16, 7);
            }
            else if (device == 3)
            {
                graphics.DrawEllipse(stroke, 10, 5, 12, 7);
                graphics.DrawLine(stroke, 11, 8, 21, 8);
            }
            else
            {
                int ear = device == 1 ? 10 : 20;
                graphics.FillEllipse(accent, ear - 3, 3, 6, 6);
                graphics.DrawLine(stroke, ear, 7, ear, 12);
                graphics.DrawString(device == 1 ? "L" : "R", side, accent,
                    new RectangleF(device == 1 ? 15 : 6, 3, 10, 9), center);
            }
            graphics.DrawString(value.HasValue ? value.Value.ToString() : "–", digits,
                value.HasValue ? Brushes.White : accent,
                new RectangleF(1, 12, 30, 19), center);
            var handle = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }
    }

    private static string StartupCommand { get { return "\"" + Application.ExecutablePath + "\""; } }

    private void UpdateStartup()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            startup.Checked = key != null && string.Equals(key.GetValue(RunName) as string,
                StartupCommand, StringComparison.OrdinalIgnoreCase);
    }

    private void ToggleStartup()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (startup.Checked) key.DeleteValue(RunName, false);
                else key.SetValue(RunName, StartupCommand, RegistryValueKind.String);
            }
            UpdateStartup();
        }
        catch (Exception error)
        {
            MessageBox.Show("自動起動を設定できませんでした。\n" + error.Message, "Battery Monitor");
        }
    }

    protected override void ExitThreadCore()
    {
        stopped = true;
        timer.Dispose();
        foreach (var icon in icons) { icon.Visible = false; icon.Dispose(); }
        foreach (var image in images) if (image != null) image.Dispose();
        menu.Dispose();
        base.ExitThreadCore();
    }
}
