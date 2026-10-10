using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Bilgames.Tts.Kokoro;

/// <summary>An app holding video memory on the GPU (processes with the same name are added together).</summary>
public sealed record GpuMemoryUser(string Name, long Bytes, int Processes, bool IsThisApp);

/// <summary>
/// Which apps are using a GPU's dedicated memory, read from the same Windows performance counters
/// Task Manager uses ("GPU Process Memory › Dedicated Usage"). Local only; nothing leaves the PC.
/// </summary>
public static partial class GpuMemoryUsage
{
    private static readonly Dictionary<int, string> NameCache = new();
    private static readonly string SelfName = Process.GetCurrentProcess().ProcessName;

    [GeneratedRegex(@"^pid_(\d+)_luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex InstancePattern();

    /// <summary>The biggest users of <paramref name="gpu"/>'s dedicated memory, largest first; empty if unavailable.</summary>
    public static List<GpuMemoryUser> Read(GpuInfo gpu, int max = 6, long minBytes = 32L * 1024 * 1024)
    {
        var perProcess = new Dictionary<int, long>();
        foreach (var (instance, bytes) in Pdh.ReadLargeArray(@"\GPU Process Memory(*)\Dedicated Usage"))
        {
            var m = InstancePattern().Match(instance);
            if (!m.Success) continue;
            long luid = ((long)Convert.ToUInt32(m.Groups[2].Value, 16) << 32) | Convert.ToUInt32(m.Groups[3].Value, 16);
            if (luid != gpu.Luid) continue;
            int pid = int.Parse(m.Groups[1].Value);
            perProcess[pid] = perProcess.GetValueOrDefault(pid) + bytes;
        }

        int self = Environment.ProcessId;
        return perProcess.Where(p => p.Value > 0)
            .Select(p => (Pid: p.Key, Bytes: p.Value, Name: p.Key == self ? "Bilgames Reader" : NameOf(p.Key)))
            .Where(p => p.Name is not null)
            .GroupBy(p => p.Name!)
            .Select(g => new GpuMemoryUser(g.Key, g.Sum(p => p.Bytes), g.Count(), g.Any(p => p.Pid == self)))
            .Where(u => u.Bytes >= minBytes)
            .OrderByDescending(u => u.Bytes)
            .Take(max)
            .ToList();
    }

    /// <summary>A friendly name: the program's description ("Google Chrome"), else its process name. Null if it has exited.</summary>
    private static string? NameOf(int pid)
    {
        lock (NameCache)
        {
            if (NameCache.TryGetValue(pid, out var cached)) return cached;
        }
        string? name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName switch
            {
                "dwm" => "Desktop Window Manager (Windows)",
                "csrss" => "Windows (csrss)",
                _ when p.ProcessName.Equals(SelfName, StringComparison.OrdinalIgnoreCase) => "Bilgames Reader (another window)",
                _ => p.ProcessName,
            };
            if (name == p.ProcessName)
            {
                try
                {
                    var description = p.MainModule?.FileVersionInfo.FileDescription?.Trim();
                    if (!string.IsNullOrEmpty(description)) name = description;
                }
                catch { /* protected or elevated process: keep the process name */ }
            }
        }
        catch
        {
            return null; // exited between the snapshot and now
        }
        lock (NameCache)
        {
            if (NameCache.Count > 500) NameCache.Clear();
            NameCache[pid] = name;
        }
        return name;
    }

    /// <summary>Minimal PDH (Performance Data Helper) access for one wildcard counter of 64-bit values.</summary>
    private static class Pdh
    {
        private const uint PDH_FMT_LARGE = 0x00000400;
        private const uint PDH_MORE_DATA = 0x800007D2;

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        public static List<(string Instance, long Value)> ReadLargeArray(string path)
        {
            var result = new List<(string, long)>();
            if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0) return result;
            try
            {
                if (PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var counter) != 0) return result;
                if (PdhCollectQueryData(query) != 0) return result;
                uint size = 0;
                uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref size, out _, IntPtr.Zero);
                if (status != PDH_MORE_DATA || size == 0) return result;
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref size, out var count, buffer) != 0) return result;
                    // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR szName; { DWORD CStatus; union { …; LONGLONG largeValue; } } }
                    int itemSize = IntPtr.Size + 16, statusAt = IntPtr.Size, valueAt = IntPtr.Size + 8;
                    for (int i = 0; i < count; i++)
                    {
                        var item = buffer + i * itemSize;
                        if ((uint)Marshal.ReadInt32(item + statusAt) > 1) continue; // 0 valid, 1 new data
                        var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                        if (name is not null) result.Add((name, Marshal.ReadInt64(item + valueAt)));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                // Counters disabled or corrupted: report nothing rather than failing generation.
            }
            finally
            {
                PdhCloseQuery(query);
            }
            return result;
        }
    }
}
