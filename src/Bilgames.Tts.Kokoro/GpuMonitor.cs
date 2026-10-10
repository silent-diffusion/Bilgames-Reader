using Vortice.DXGI;

namespace Bilgames.Tts.Kokoro;

public sealed record GpuInfo(int AdapterIndex, string Name, long DedicatedBytes);

/// <summary>
/// Finds the best DirectX 12 GPU and reports how much video memory this process may still use
/// (DXGI's per-process budget minus current usage), so the engine can decide CPU vs GPU.
/// </summary>
public static class GpuMonitor
{
    private static GpuInfo? _best;
    private static bool _probed;

    /// <summary>The discrete GPU with the most dedicated memory (integrated/software adapters only if nothing better).</summary>
    public static GpuInfo? Best
    {
        get
        {
            if (_probed) return _best;
            _probed = true;
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
                GpuInfo? best = null;
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                {
                    using (adapter)
                    {
                        var d = adapter.Description1;
                        if ((d.Flags & AdapterFlags.Software) != 0) continue;
                        long mem = (long)(ulong)d.DedicatedVideoMemory;
                        // Below 2 GB of dedicated memory is an integrated GPU; Kokoro gains little there.
                        if (mem < 2L * 1024 * 1024 * 1024) continue;
                        if (best is null || mem > best.DedicatedBytes) best = new GpuInfo((int)i, d.Description.Trim(), mem);
                    }
                }
                _best = best;
            }
            catch (Exception ex) when (LogProbe(ex))
            {
                _best = null;
            }
            return _best;
        }
    }

    /// <summary>Why detection failed, if it did (shown in Settings for troubleshooting).</summary>
    public static string? ProbeError { get; private set; }

    private static bool LogProbe(Exception ex)
    {
        ProbeError = ex.GetType().Name + ": " + ex.Message + " @ " + ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
        return true;
    }

    /// <summary>Video memory this process can still allocate on the GPU, or 0 if unknown.</summary>
    public static long AvailableBytes(GpuInfo gpu)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            if (!factory.EnumAdapters1((uint)gpu.AdapterIndex, out var adapter).Success) return 0;
            using (adapter)
            {
                using var a3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
                if (a3 is null) return 0;
                var info = a3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                return (long)info.Budget - (long)info.CurrentUsage;
            }
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Video memory this process is currently using on the GPU.</summary>
    public static long UsedBytes(GpuInfo gpu)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            if (!factory.EnumAdapters1((uint)gpu.AdapterIndex, out var adapter).Success) return 0;
            using (adapter)
            {
                using var a3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
                return a3 is null ? 0 : (long)a3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;
            }
        }
        catch
        {
            return 0;
        }
    }
}
