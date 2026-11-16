using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Peekaboo;

public sealed class CpuReading
{
    public double Total, User, System;
    public double[] PerCore = Array.Empty<double>();
}

internal sealed class CpuReader
{
    ulong _pIdle, _pKernel, _pUser;
    long[][]? _prevCores;

    public CpuReading Read()
    {
        var r = new CpuReading();
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong i = idle.Value, k = kernel.Value, u = user.Value;
            if (_pKernel != 0)
            {
                double di = i - _pIdle, dk = k - _pKernel, du = u - _pUser;
                double all = dk + du; // kernel time includes idle
                if (all > 0)
                {
                    r.User = du / all;
                    r.System = Math.Max(0, (dk - di) / all);
                    r.Total = Math.Clamp(r.User + r.System, 0, 1);
                }
            }
            _pIdle = i; _pKernel = k; _pUser = u;
        }
        r.PerCore = ReadCores();
        return r;
    }

    double[] ReadCores()
    {
        int n = Environment.ProcessorCount;
        int size = Marshal.SizeOf<Native.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(size * n);
        try
        {
            if (Native.NtQuerySystemInformation(8, buf, size * n, out int len) != 0) return new double[n];
            int count = Math.Min(n, len / size);
            var cur = new long[count][];
            var res = new double[count];
            for (int c = 0; c < count; c++)
            {
                var info = Marshal.PtrToStructure<Native.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buf + c * size);
                cur[c] = new[] { info.IdleTime, info.KernelTime, info.UserTime };
                if (_prevCores is not null && c < _prevCores.Length)
                {
                    double di = cur[c][0] - _prevCores[c][0];
                    double all = (cur[c][1] - _prevCores[c][1]) + (cur[c][2] - _prevCores[c][2]);
                    res[c] = all > 0 ? Math.Clamp(1 - di / all, 0, 1) : 0;
                }
            }
            _prevCores = cur;
            return res;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

public sealed class MemoryReading
{
    public ulong Total, Used, Available, Cached, CommitUsed, CommitLimit;
    public double UsedFraction => Total > 0 ? (double)Used / Total : 0;
    /// 1 normal, 2 elevated, 4 critical — same scale as the macOS build.
    public int PressureLevel => UsedFraction >= 0.92 ? 4 : UsedFraction >= 0.8 ? 2 : 1;
    public string PressureText => PressureLevel switch { 4 => "紧张", 2 => "偏高", _ => "正常" };
}

internal static class MemoryReader
{
    public static MemoryReading Read()
    {
        var r = new MemoryReading();
        var m = new Native.MEMORYSTATUSEX();
        if (Native.GlobalMemoryStatusEx(m))
        {
            r.Total = m.ullTotalPhys;
            r.Available = m.ullAvailPhys;
            r.Used = m.ullTotalPhys - m.ullAvailPhys;
        }
        if (Native.GetPerformanceInfo(out var p, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>()))
        {
            ulong page = (ulong)p.PageSize;
            r.Cached = (ulong)p.SystemCache * page;
            r.CommitUsed = (ulong)p.CommitTotal * page;
            r.CommitLimit = (ulong)p.CommitLimit * page;
        }
        return r;
    }
}

public sealed class DiskReading
{
    public string Name = "C:";
    public long Total, Free;
    public long Used => Math.Max(Total - Free, 0);
    public double UsedFraction => Total > 0 ? (double)Used / Total : 0;
}

internal static class DiskReader
{
    public static DiskReading Read(string? root = null)
    {
        root ??= Paths.SystemDrive;
        try
        {
            var d = new DriveInfo(root);
            return new DiskReading { Name = d.Name.TrimEnd('\\'), Total = d.TotalSize, Free = d.AvailableFreeSpace };
        }
        catch { return new DiskReading(); }
    }

    public static List<DiskReading> All() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DiskReading { Name = d.Name.TrimEnd('\\'), Total = d.TotalSize, Free = d.AvailableFreeSpace }).ToList();
}

internal sealed class NetworkReader
{
    long _in, _out;
    long _t;

    public (double inRate, double outRate) Read()
    {
        long i = 0, o = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                // Skip virtual adapters (Hyper-V, VPN, WSL) so traffic is not double counted.
                var d = n.Description.ToLowerInvariant();
                if (d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vpn") || d.Contains("wsl")) continue;
                var s = n.GetIPStatistics();
                i += s.BytesReceived; o += s.BytesSent;
            }
        }
        catch { }
        long now = Stopwatch.GetTimestamp();
        (double, double) result = (0, 0);
        if (_t != 0 && i >= _in && o >= _out)
        {
            double dt = (now - _t) / (double)Stopwatch.Frequency;
            if (dt > 0) result = ((i - _in) / dt, (o - _out) / dt);
        }
        _in = i; _out = o; _t = now;
        return result;
    }
}

public sealed record BatteryReading(double Percent, bool Charging, bool OnAC, int? MinutesLeft);

internal static class BatteryReader
{
    public static BatteryReading? Read()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return null;
        if ((s.BatteryFlag & 128) != 0 || s.BatteryLifePercent == 255) return null; // no battery
        return new BatteryReading(s.BatteryLifePercent / 100.0, (s.BatteryFlag & 8) != 0, s.ACLineStatus == 1,
                                  s.BatteryLifeTime > 0 ? s.BatteryLifeTime / 60 : null);
    }
}

/// GPU load from the "GPU Engine" performance counters (Windows 10 1709+). Best effort.
internal sealed class GpuReader
{
    PerformanceCounter[] _counters = Array.Empty<PerformanceCounter>();
    DateTime _refreshed = DateTime.MinValue;
    bool _broken;

    public double? Read()
    {
        if (_broken) return null;
        try
        {
            if ((DateTime.Now - _refreshed).TotalSeconds > 30)
            {
                foreach (var c in _counters) c.Dispose();
                var cat = new PerformanceCounterCategory("GPU Engine");
                _counters = cat.GetInstanceNames().Where(n => n.EndsWith("engtype_3D"))
                    .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true)).ToArray();
                foreach (var c in _counters) c.NextValue();
                _refreshed = DateTime.Now;
                return null;
            }
            double sum = 0;
            foreach (var c in _counters) { try { sum += c.NextValue(); } catch { } }
            return Math.Clamp(sum / 100, 0, 1);
        }
        catch { _broken = true; return null; }
    }
}
