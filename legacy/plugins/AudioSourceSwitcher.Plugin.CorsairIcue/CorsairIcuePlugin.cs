using System.Reflection;
using AudioSourceSwitcher.Abstractions;
using AudioSourceSwitcher.Plugin.CorsairIcue.Interop;

namespace AudioSourceSwitcher.Plugin.CorsairIcue;

public sealed class CorsairIcuePlugin : IAudioSourcePlugin
{
    public string Id => "corsair-icue";
    public string DisplayName => "Corsair iCUE (SDK) Device Presence";
    public string DefaultLogicalDeviceId => "corsair:headset:any";

    private IPluginHost? _host;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Track what we've reported so we can emit deltas.
    private readonly HashSet<string> _reportedConnected = new(StringComparer.OrdinalIgnoreCase);

    // Snapshot for UI probing (updated by the poll loop).
    private volatile string[] _lastProbedLogicalIds = Array.Empty<string>();

    public IReadOnlyList<string> ProbeLogicalDeviceIds()
    {
        // Must be fast and safe for UI threads. Return a snapshot.
        return _lastProbedLogicalIds;
    }

    public void Start(IPluginHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
    }

    public void Stop()
    {
        var host = _host;
        _host = null;

        var cts = _cts;
        _cts = null;

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }

        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _loop = null;

        // Emit disconnects for anything we told the host was connected.
        if (host is not null)
        {
            foreach (var id in _reportedConnected.ToArray())
            {
                try { host.ReportDisconnected(id); } catch { /* ignore */ }
            }
        }
        _reportedConnected.Clear();

        try { CueSdk.TryDisconnectAndUnload(); } catch { /* ignore */ }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        var host = _host!;

        var pluginDir = GetPluginDirectory();
        host.Logger.Info($"Corsair iCUE plugin starting. PluginDir={pluginDir}");

        var lastInitLog = DateTimeOffset.MinValue;
        var lastSuccess = DateTimeOffset.MinValue;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!CueSdk.IsInitialized)
                {
                    if (DateTimeOffset.Now - lastInitLog > TimeSpan.FromSeconds(30))
                    {
                        host.Logger.Info("Initializing Corsair iCUE SDK (native DLL load + CorsairConnect)...");
                        lastInitLog = DateTimeOffset.Now;
                    }

                    if (!CueSdk.TryInitialize(pluginDir, out var initError))
                    {
                        host.Logger.Warn($"Corsair iCUE SDK not ready: {initError}");
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                        continue;
                    }

                    host.Logger.Info("Corsair iCUE SDK connected.");
                }

                CorsairDeviceInfo[] devices;
                if (!CueSdk.TryGetDevices(new CorsairDeviceFilter(CorsairDeviceType.Headset), out devices, out var error))
                    devices = Array.Empty<CorsairDeviceInfo>();

                if (error is not null)
                    host.Logger.Warn($"CorsairGetDevices returned error: {error}");

                var logicalIdsNow = BuildLogicalIds(devices);
                _lastProbedLogicalIds = logicalIdsNow.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                EmitDelta(host, logicalIdsNow);

                lastSuccess = DateTimeOffset.Now;
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                host.Logger.Error(ex, "Corsair iCUE plugin loop error; will retry.");

                // Treat as a connection loss; force re-init and emit disconnects.
                try { CueSdk.TryDisconnectAndUnload(); } catch { /* ignore */ }
                EmitDelta(host, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                // Back off a bit (but keep responsive).
                var sinceOk = DateTimeOffset.Now - lastSuccess;
                var delay = sinceOk < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);
                await Task.Delay(delay, token);
            }
        }

        host.Logger.Info("Corsair iCUE plugin stopped.");
    }

    private static string GetPluginDirectory()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        return string.IsNullOrWhiteSpace(location)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
    }

    private static HashSet<string> BuildLogicalIds(IReadOnlyList<CorsairDeviceInfo> devices)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (devices.Count == 0)
            return ids;

        // Generic: "any Corsair headset".
        ids.Add("corsair:headset:any");

        foreach (var d in devices)
        {
            var model = (d.model ?? string.Empty).Trim();
            var deviceId = (d.id ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(model))
                ids.Add($"corsair:headset:model:{Slug(model)}");

            if (!string.IsNullOrWhiteSpace(deviceId))
                ids.Add($"corsair:headset:id:{deviceId}");
        }

        return ids;
    }

    private void EmitDelta(IPluginHost host, HashSet<string> now)
    {
        foreach (var id in now)
        {
            if (_reportedConnected.Add(id))
                host.ReportConnected(id);
        }

        foreach (var prev in _reportedConnected.ToArray())
        {
            if (now.Contains(prev)) continue;
            _reportedConnected.Remove(prev);
            host.ReportDisconnected(prev);
        }
    }

    private static string Slug(string s)
    {
        // Keep it stable and reasonably human-readable.
        Span<char> buf = stackalloc char[s.Length];
        var j = 0;
        foreach (var ch in s.Trim())
        {
            if (char.IsLetterOrDigit(ch))
                buf[j++] = char.ToLowerInvariant(ch);
            else if (ch is ' ' or '-' or '_' or '.')
                buf[j++] = '-';
        }

        var slug = new string(buf[..j]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
    }
}


