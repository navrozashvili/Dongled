using System.Diagnostics;
using System.Reflection;
using AudioSourceSwitcher.Abstractions;
using NetMQ;
using NetMQ.Sockets;

namespace AudioSourceSwitcher.Plugin.HyperX;

/// <summary>
/// Presence detection for HyperX Cloud III S Wireless based on HyperX NGENUITY's local CommunicationHub IPC.
/// </summary>
public sealed class HyperXNgenuityPlugin : IAudioSourcePlugin
{
    public string Id => "hyperx-ngenuity";
    public string DisplayName => "HyperX NGENUITY Presence (CommunicationHub IPC)";

    // Stable id for "any Cloud III S Wireless headset is present (RF link established)".
    public string DefaultLogicalDeviceId => "hyperx:cloud-iii-s-wireless:any";

    private IPluginHost? _host;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly object _stateLock = new();
    private readonly Dictionary<ulong, bool> _connectedByBaseId = new();
    private readonly HashSet<ulong> _cloudIiisWirelessDongleBaseIds = new();
    private readonly HashSet<string> _reportedConnected = new(StringComparer.OrdinalIgnoreCase);

    private volatile string[] _lastProbedLogicalIds = Array.Empty<string>();

    public IReadOnlyList<string> ProbeLogicalDeviceIds()
        => _lastProbedLogicalIds;

    public void Start(IPluginHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
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

        lock (_stateLock)
        {
            _connectedByBaseId.Clear();
            _cloudIiisWirelessDongleBaseIds.Clear();
            _lastProbedLogicalIds = Array.Empty<string>();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var host = _host!;
        host.Logger.Info("HyperX NGENUITY plugin starting (CommunicationHub IPC).");

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!TryCreateHubReflection(host.Logger, out var hub, out var reason))
                {
                    host.Logger.Warn($"HyperX NGENUITY plugin: cannot load CommunicationHub types. {reason}");
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    continue;
                }

                using (hub)
                {
                    // Optional bootstrap: discover Cloud III S Wireless dongle baseId(s) via request/reply.
                    TryBootstrapCloudIiisWirelessDongles(host, hub);

                    // Main subscription loop (blocks until cancelled or an error occurs).
                    await SubscribeLoopAsync(host, hub, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                host.Logger.Error(ex, "HyperX NGENUITY plugin loop error; will retry.");
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
        }

        host.Logger.Info("HyperX NGENUITY plugin stopped.");
    }

    private async Task SubscribeLoopAsync(IPluginHost host, CommunicationHubReflection hub, CancellationToken token)
    {
        // Connect to a CommunicationHub publisher port (6890..6899 maps to 7890..7899, but we don't require
        // "seeing traffic" to consider the connection successful; initial state is queried via requests).
        SubscriberSocket? sub = null;
        string? connectPath = null;

        try
        {
            var topic = new byte[] { 13, 4, 0 }; // [13, Notification.WirelessRFDongleStatusNotification, 0]

            for (var port = 7890; port <= 7899 && !token.IsCancellationRequested; port++)
            {
                try
                {
                    sub?.Dispose();
                    sub = new SubscriberSocket();
                    sub.Options.ReceiveHighWatermark = 1000;

                    connectPath = $"tcp://localhost:{port}";
                    sub.Connect(connectPath);
                    sub.Subscribe(topic);

                    host.Logger.Info($"HyperX NGENUITY plugin: connected to CommunicationHub publisher at {connectPath}");
                    break;
                }
                catch
                {
                    try { sub?.Dispose(); } catch { /* ignore */ }
                    sub = null;
                    connectPath = null;
                }
            }

            if (sub is null || connectPath is null)
                throw new InvalidOperationException("No matching CommunicationHub publisher found in 7890..7899.");

            // Main receive loop.
            while (!token.IsCancellationRequested)
            {
                if (!sub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(200), out var frame) || frame is null)
                    continue;

                ProcessFrame(host, hub, frame);
            }
        }
        finally
        {
            if (sub is not null)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(connectPath))
                        sub.Disconnect(connectPath);
                }
                catch
                {
                    // ignore
                }
                sub.Dispose();
            }
        }
    }

    private void ProcessFrame(IPluginHost host, CommunicationHubReflection hub, byte[] frame)
    {
        // Requirement: frame is topic prefix bytes + flatbuffer payload.
        if (frame.Length < 4)
            return;

        var topic = frame.AsSpan(0, 3).ToArray();
        var payload = frame.AsSpan(3).ToArray();

        if (!hub.TryParseWirelessRfDongleStatusNotification(payload, out var baseId, out var connected))
            return;

        // If we successfully bootstrapped dongle baseIds, ignore notifications for other devices.
        lock (_stateLock)
        {
            if (_cloudIiisWirelessDongleBaseIds.Count > 0 && !_cloudIiisWirelessDongleBaseIds.Contains(baseId))
                return;
        }

        bool changed;
        lock (_stateLock)
        {
            changed = !_connectedByBaseId.TryGetValue(baseId, out var prev) || prev != connected;
            if (changed)
                _connectedByBaseId[baseId] = connected;

            UpdateProbeSnapshot_NoThrow();
        }

        if (!changed)
            return;

        // Requirement: maintain a map { baseId -> connected } and emit events when it changes.
        var logical = BaseIdLogicalDeviceId(baseId);
        if (connected)
        {
            if (_reportedConnected.Add(logical))
                host.ReportConnected(logical);
        }
        else
        {
            if (_reportedConnected.Remove(logical))
                host.ReportDisconnected(logical);
        }

        // Also emit the aggregate "any" id.
        EmitAggregateAnyDelta(host);

        // Topic is currently unused, but keep it for easy debugging if needed.
        _ = topic;
    }

    private void EmitAggregateAnyDelta(IPluginHost host)
    {
        var anyNowConnected = false;
        lock (_stateLock)
        {
            foreach (var kvp in _connectedByBaseId)
            {
                if (kvp.Value)
                {
                    anyNowConnected = true;
                    break;
                }
            }
        }

        if (anyNowConnected)
        {
            if (_reportedConnected.Add(DefaultLogicalDeviceId))
                host.ReportConnected(DefaultLogicalDeviceId);
        }
        else
        {
            if (_reportedConnected.Remove(DefaultLogicalDeviceId))
                host.ReportDisconnected(DefaultLogicalDeviceId);
        }
    }

    private void UpdateProbeSnapshot_NoThrow()
    {
        try
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                DefaultLogicalDeviceId
            };

            foreach (var baseId in _connectedByBaseId.Keys)
                ids.Add(BaseIdLogicalDeviceId(baseId));

            _lastProbedLogicalIds = ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            // Contract: best-effort probe, never throw.
        }
    }

    private static string BaseIdLogicalDeviceId(ulong baseId)
        => $"hyperx:cloud-iii-s-wireless:baseid:{baseId}";

    private void TryBootstrapCloudIiisWirelessDongles(IPluginHost host, CommunicationHubReflection hub)
    {
        try
        {
            if (!hub.TryGetDeviceList(out var baseIds) || baseIds.Length == 0)
                return;

            var discovered = new HashSet<ulong>();
            foreach (var baseId in baseIds)
            {
                if (!hub.TryGetDeviceInformation(baseId, out var productName, out var isDongle))
                    continue;

                if (!isDongle)
                    continue;

                // Cloud III S Wireless identifier in CommunicationHub.Products enum: CloudIIISWireless
                if (!string.Equals(productName, "CloudIIISWireless", StringComparison.OrdinalIgnoreCase))
                    continue;

                discovered.Add(baseId);
            }

            if (discovered.Count == 0)
                return;

            // Seed initial connection state so the plugin reports devices immediately (not only after a notification).
            var initialConnected = new List<ulong>();
            lock (_stateLock)
            {
                _connectedByBaseId.Clear();
                foreach (var baseId in discovered)
                {
                    if (hub.TryGetWirelessRfConnectionStatus(baseId, out var connected))
                    {
                        _connectedByBaseId[baseId] = connected;
                        if (connected)
                            initialConnected.Add(baseId);
                    }
                    else
                    {
                        // Unknown state; keep an entry so it shows up in probing.
                        _connectedByBaseId[baseId] = false;
                    }
                }
            }

            lock (_stateLock)
            {
                _cloudIiisWirelessDongleBaseIds.Clear();
                foreach (var id in discovered)
                    _cloudIiisWirelessDongleBaseIds.Add(id);
                UpdateProbeSnapshot_NoThrow();
            }

            // Emit initial connected events.
            foreach (var baseId in initialConnected.OrderBy(x => x))
            {
                var logical = BaseIdLogicalDeviceId(baseId);
                if (_reportedConnected.Add(logical))
                    host.ReportConnected(logical);
            }
            EmitAggregateAnyDelta(host);

            host.Logger.Info($"HyperX NGENUITY plugin: bootstrapped Cloud III S Wireless dongle baseId(s): {string.Join(", ", discovered.OrderBy(x => x))}");
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"HyperX NGENUITY plugin bootstrap failed; continuing without filtering. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryCreateHubReflection(IAppLogger logger, out CommunicationHubReflection hub, out string reason)
    {
        hub = null!;
        reason = string.Empty;

        var installDir = TryFindNgenuityInstallDir(logger);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            reason = "NGENUITY install directory not found (is NGENUITY running/installed?).";
            return false;
        }

        var commonPath = Path.Combine(installDir, "NGENUITY3.Common.dll");
        if (!File.Exists(commonPath))
        {
            reason = $"NGENUITY3.Common.dll not found under '{installDir}'.";
            return false;
        }

        try
        {
            hub = new CommunicationHubReflection(commonPath);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Failed to load CommunicationHub reflection. {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string? TryFindNgenuityInstallDir(IAppLogger logger)
    {
        // Prefer locating a running NGENUITY3 process (gives exact install dir).
        try
        {
            foreach (var p in Process.GetProcessesByName("NGENUITY3"))
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                        return Path.GetDirectoryName(exe);
                }
                catch
                {
                    // ignore and keep trying
                }
            }
        }
        catch
        {
            // ignore
        }

        // Common install locations.
        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(pf, "NGENUITY");
            if (Directory.Exists(candidate))
                return candidate;
        }
        catch
        {
            // ignore
        }

        try
        {
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var candidate = Path.Combine(pfx86, "NGENUITY");
            if (Directory.Exists(candidate))
                return candidate;
        }
        catch
        {
            // ignore
        }

        logger.Info("HyperX NGENUITY plugin: could not find NGENUITY install directory.");
        return null;
    }

    /// <summary>
    /// Reflection wrapper around NGENUITY's FlatBuffers-generated CommunicationHub types.
    /// We load their own Google.FlatBuffers.dll to avoid assembly version mismatches.
    /// </summary>
    private sealed class CommunicationHubReflection : IDisposable
    {
        private static readonly byte[] HxAck = { 119, 1, 36 };

        private readonly string _ngCommonPath;
        private readonly string _ngDir;

        private readonly Assembly _commonAsm;
        private readonly Assembly _flatbuffersAsm;

        private readonly Type _byteBufferType;
        private readonly ConstructorInfo _byteBufferCtor;
        private readonly MethodInfo _byteBufferToSizedArray;

        private readonly Type _flatBufferBuilderType;
        private readonly ConstructorInfo _flatBufferBuilderCtor;
        private readonly PropertyInfo _flatBufferBuilderDataBuffer;

        private readonly Type _messageType;
        private readonly MethodInfo _getRootAsMessage;
        private readonly PropertyInfo _messageBaseId;
        private readonly PropertyInfo _messageActionType;
        private readonly MethodInfo _messageActionAsIncomingNotification;
        private readonly MethodInfo _messageActionAsAddRequest;
        private readonly MethodInfo _messageActionAsAddCommand;
        private readonly PropertyInfo _messageErrMessage;

        private readonly Type _incomingNotificationType;
        private readonly PropertyInfo _incomingNotificationNotificationType;
        private readonly MethodInfo _incomingNotificationAsWirelessRfDongleStatus;

        private readonly Type _wirelessRfDongleStatusType;
        private readonly PropertyInfo _wirelessRfDongleStatusConnected;

        private readonly Type _addRequestType;
        private readonly MethodInfo _addRequestRequestDataAsDeviceList;

        private readonly Type _deviceListType;
        private readonly MethodInfo _deviceListGetBaseIdsArray;

        private readonly Type _addCommandType;
        private readonly MethodInfo _addCommandCommandDataAsDeviceInformation;
        private readonly MethodInfo _addCommandCommandDataAsWirelessRfConnectionStatus;

        private readonly Type _deviceInformationType;
        private readonly PropertyInfo _deviceInformationProduct;
        private readonly PropertyInfo _deviceInformationIsDongle;

        private readonly Type _wirelessRfConnectionStatusType;
        private readonly PropertyInfo _wirelessRfConnectionStatusConnected;

        // Build helpers
        private readonly Type _actionEnumType;
        private readonly Type _notificationEnumType;
        private readonly Type _productsEnumType;
        private readonly Type _requestDataEnumType;
        private readonly Type _commandDataEnumType;

        private readonly MethodInfo _messageStartMessage;
        private readonly MethodInfo _messageEndMessage;
        private readonly MethodInfo _messageFinishMessageBuffer;
        private readonly MethodInfo _messageAddBaseId;
        private readonly MethodInfo _messageAddAction;
        private readonly MethodInfo _messageAddActionType;

        private readonly Type _deviceListRequestBuilderType;
        private readonly MethodInfo _deviceListStart;
        private readonly MethodInfo _deviceListEnd;

        private readonly Type _addRequestBuilderType;
        private readonly MethodInfo _addRequestStart;
        private readonly MethodInfo _addRequestEnd;
        private readonly MethodInfo _addRequestAddRequestData;
        private readonly MethodInfo _addRequestAddRequestDataType;

        private readonly Type _deviceInformationBuilderType;
        private readonly MethodInfo _deviceInformationStart;
        private readonly MethodInfo _deviceInformationEnd;

        private readonly Type _wirelessRfConnectionStatusBuilderType;
        private readonly MethodInfo _wirelessRfConnectionStatusStart;
        private readonly MethodInfo _wirelessRfConnectionStatusEnd;

        private readonly Type _addCommandBuilderType;
        private readonly MethodInfo _addCommandStart;
        private readonly MethodInfo _addCommandEnd;
        private readonly MethodInfo _addCommandAddCommandData;
        private readonly MethodInfo _addCommandAddCommandDataType;

        public CommunicationHubReflection(string ngCommonPath)
        {
            _ngCommonPath = Path.GetFullPath(ngCommonPath);
            _ngDir = Path.GetDirectoryName(_ngCommonPath) ?? AppContext.BaseDirectory;

            // Ensure we load NGENUITY's exact Google.FlatBuffers.dll (assembly version pinning).
            var fbPath = Path.Combine(_ngDir, "Google.FlatBuffers.dll");
            if (File.Exists(fbPath))
                _flatbuffersAsm = Assembly.LoadFrom(fbPath);
            else
                _flatbuffersAsm = Assembly.Load("Google.FlatBuffers"); // last-resort

            _commonAsm = Assembly.LoadFrom(_ngCommonPath);

            _byteBufferType = RequireType(_flatbuffersAsm, "Google.FlatBuffers.ByteBuffer");
            _byteBufferCtor = _byteBufferType.GetConstructor(new[] { typeof(byte[]) })
                ?? throw new MissingMethodException(_byteBufferType.FullName, ".ctor(byte[])");
            _byteBufferToSizedArray = _byteBufferType.GetMethod("ToSizedArray", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException(_byteBufferType.FullName, "ToSizedArray()");

            _flatBufferBuilderType = RequireType(_flatbuffersAsm, "Google.FlatBuffers.FlatBufferBuilder");
            _flatBufferBuilderCtor = _flatBufferBuilderType.GetConstructor(new[] { typeof(int) })
                ?? throw new MissingMethodException(_flatBufferBuilderType.FullName, ".ctor(int)");
            _flatBufferBuilderDataBuffer = _flatBufferBuilderType.GetProperty("DataBuffer", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMemberException(_flatBufferBuilderType.FullName, "DataBuffer");

            _messageType = RequireType(_commonAsm, "CommunicationHub.Message");
            _getRootAsMessage = _messageType.GetMethod(
                                    "GetRootAsMessage",
                                    BindingFlags.Public | BindingFlags.Static,
                                    binder: null,
                                    types: new[] { _byteBufferType },
                                    modifiers: null)
                                ?? throw new MissingMethodException(_messageType.FullName, "GetRootAsMessage(ByteBuffer)");
            _messageBaseId = RequireProperty(_messageType, "BaseId");
            _messageActionType = RequireProperty(_messageType, "ActionType");
            _messageErrMessage = RequireProperty(_messageType, "ErrMessage");
            _messageActionAsIncomingNotification = RequireMethod(_messageType, "ActionAsIncomingNotification");
            _messageActionAsAddRequest = RequireMethod(_messageType, "ActionAsAddRequest");
            _messageActionAsAddCommand = RequireMethod(_messageType, "ActionAsAddCommand");

            _incomingNotificationType = RequireType(_commonAsm, "CommunicationHub.IncomingNotification");
            _incomingNotificationNotificationType = RequireProperty(_incomingNotificationType, "NotificationType");
            _incomingNotificationAsWirelessRfDongleStatus = RequireMethod(_incomingNotificationType, "NotificationAsWirelessRFDongleStatusNotification");

            _wirelessRfDongleStatusType = RequireType(_commonAsm, "CommunicationHub.WirelessRFDongleStatusNotification");
            _wirelessRfDongleStatusConnected = RequireProperty(_wirelessRfDongleStatusType, "Connected");

            _addRequestType = RequireType(_commonAsm, "CommunicationHub.AddRequest");
            _addRequestRequestDataAsDeviceList = RequireMethod(_addRequestType, "RequestDataAsDeviceList");

            _deviceListType = RequireType(_commonAsm, "CommunicationHub.DeviceList");
            _deviceListGetBaseIdsArray = RequireMethod(_deviceListType, "GetBaseIdsArray");

            _addCommandType = RequireType(_commonAsm, "CommunicationHub.AddCommand");
            _addCommandCommandDataAsDeviceInformation = RequireMethod(_addCommandType, "CommandDataAsDeviceInformation");
            _addCommandCommandDataAsWirelessRfConnectionStatus = RequireMethod(_addCommandType, "CommandDataAsWirelessRFConnectionStatus");

            _deviceInformationType = RequireType(_commonAsm, "CommunicationHub.DeviceInformation");
            _deviceInformationProduct = RequireProperty(_deviceInformationType, "Product");
            _deviceInformationIsDongle = RequireProperty(_deviceInformationType, "IsDongle");

            _wirelessRfConnectionStatusType = RequireType(_commonAsm, "CommunicationHub.WirelessRFConnectionStatus");
            _wirelessRfConnectionStatusConnected = RequireProperty(_wirelessRfConnectionStatusType, "Connected");

            _actionEnumType = RequireType(_commonAsm, "CommunicationHub.Action");
            _notificationEnumType = RequireType(_commonAsm, "CommunicationHub.Notification");
            _productsEnumType = RequireType(_commonAsm, "CommunicationHub.Products");
            _requestDataEnumType = RequireType(_commonAsm, "CommunicationHub.RequestData");
            _commandDataEnumType = RequireType(_commonAsm, "CommunicationHub.CommandData");

            // Message build helpers
            _messageStartMessage = RequireMethod(_messageType, "StartMessage", BindingFlags.Public | BindingFlags.Static);
            _messageEndMessage = RequireMethod(_messageType, "EndMessage", BindingFlags.Public | BindingFlags.Static);
            _messageFinishMessageBuffer = RequireMethod(_messageType, "FinishMessageBuffer", BindingFlags.Public | BindingFlags.Static);
            _messageAddBaseId = RequireMethod(_messageType, "AddBaseId", BindingFlags.Public | BindingFlags.Static);
            _messageAddAction = RequireMethod(_messageType, "AddAction", BindingFlags.Public | BindingFlags.Static);
            _messageAddActionType = RequireMethod(_messageType, "AddActionType", BindingFlags.Public | BindingFlags.Static);

            // DeviceList request build helpers
            _deviceListRequestBuilderType = _deviceListType;
            _deviceListStart = RequireMethod(_deviceListRequestBuilderType, "StartDeviceList", BindingFlags.Public | BindingFlags.Static);
            _deviceListEnd = RequireMethod(_deviceListRequestBuilderType, "EndDeviceList", BindingFlags.Public | BindingFlags.Static);

            _addRequestBuilderType = _addRequestType;
            _addRequestStart = RequireMethod(_addRequestBuilderType, "StartAddRequest", BindingFlags.Public | BindingFlags.Static);
            _addRequestEnd = RequireMethod(_addRequestBuilderType, "EndAddRequest", BindingFlags.Public | BindingFlags.Static);
            _addRequestAddRequestData = RequireMethod(_addRequestBuilderType, "AddRequestData", BindingFlags.Public | BindingFlags.Static);
            _addRequestAddRequestDataType = RequireMethod(_addRequestBuilderType, "AddRequestDataType", BindingFlags.Public | BindingFlags.Static);

            // DeviceInformation request build helpers
            _deviceInformationBuilderType = _deviceInformationType;
            _deviceInformationStart = RequireMethod(_deviceInformationBuilderType, "StartDeviceInformation", BindingFlags.Public | BindingFlags.Static);
            _deviceInformationEnd = RequireMethod(_deviceInformationBuilderType, "EndDeviceInformation", BindingFlags.Public | BindingFlags.Static);

            // WirelessRFConnectionStatus request build helpers
            _wirelessRfConnectionStatusBuilderType = _wirelessRfConnectionStatusType;
            _wirelessRfConnectionStatusStart = RequireMethod(_wirelessRfConnectionStatusBuilderType, "StartWirelessRFConnectionStatus", BindingFlags.Public | BindingFlags.Static);
            _wirelessRfConnectionStatusEnd = RequireMethod(_wirelessRfConnectionStatusBuilderType, "EndWirelessRFConnectionStatus", BindingFlags.Public | BindingFlags.Static);

            _addCommandBuilderType = _addCommandType;
            _addCommandStart = RequireMethod(_addCommandBuilderType, "StartAddCommand", BindingFlags.Public | BindingFlags.Static);
            _addCommandEnd = RequireMethod(_addCommandBuilderType, "EndAddCommand", BindingFlags.Public | BindingFlags.Static);
            _addCommandAddCommandData = RequireMethod(_addCommandBuilderType, "AddCommandData", BindingFlags.Public | BindingFlags.Static);
            _addCommandAddCommandDataType = RequireMethod(_addCommandBuilderType, "AddCommandDataType", BindingFlags.Public | BindingFlags.Static);
        }

        public void Dispose()
        {
            // Nothing to dispose: assemblies remain loaded for process lifetime.
        }

        public bool TryParseWirelessRfDongleStatusNotification(byte[] payload, out ulong baseId, out bool connected)
        {
            baseId = 0;
            connected = false;

            try
            {
                var msg = GetRootAsMessage(payload);
                var actionType = _messageActionType.GetValue(msg);
                if (actionType is null || Convert.ToInt32(actionType) != 3) // IncomingNotification
                    return false;

                var incoming = _messageActionAsIncomingNotification.Invoke(msg, Array.Empty<object>());
                if (incoming is null)
                    return false;

                var notificationType = _incomingNotificationNotificationType.GetValue(incoming);
                if (notificationType is null || Convert.ToInt32(notificationType) != 4) // WirelessRFDongleStatusNotification
                    return false;

                var status = _incomingNotificationAsWirelessRfDongleStatus.Invoke(incoming, Array.Empty<object>());
                if (status is null)
                    return false;

                baseId = (ulong)(_messageBaseId.GetValue(msg) ?? 0UL);
                connected = (bool)(_wirelessRfDongleStatusConnected.GetValue(status) ?? false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetDeviceList(out ulong[] baseIds)
        {
            baseIds = Array.Empty<ulong>();

            try
            {
                if (!TryCreateRequestSocket(out var socket, out var connectPath))
                    return false;

                using (socket)
                {
                    socket.SendFrame(BuildDeviceListRequest());
                    if (!socket.TryReceiveFrameBytes(TimeSpan.FromSeconds(1), out var resp) || resp is null)
                        return false;

                    var msg = GetRootAsMessage(resp);
                    var err = (string?)_messageErrMessage.GetValue(msg);
                    if (!string.IsNullOrWhiteSpace(err))
                        return false;

                    var addReq = _messageActionAsAddRequest.Invoke(msg, Array.Empty<object>());
                    if (addReq is null)
                        return false;

                    var deviceList = _addRequestRequestDataAsDeviceList.Invoke(addReq, Array.Empty<object>());
                    if (deviceList is null)
                        return false;

                    var arr = _deviceListGetBaseIdsArray.Invoke(deviceList, Array.Empty<object>());
                    baseIds = (arr as ulong[]) ?? Array.Empty<ulong>();
                    return baseIds.Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetDeviceInformation(ulong baseId, out string productName, out bool isDongle)
        {
            productName = string.Empty;
            isDongle = false;

            try
            {
                if (!TryCreateRequestSocket(out var socket, out var _))
                    return false;

                using (socket)
                {
                    socket.SendFrame(BuildDeviceInformationRequest(baseId));
                    if (!socket.TryReceiveFrameBytes(TimeSpan.FromSeconds(1), out var resp) || resp is null)
                        return false;

                    var msg = GetRootAsMessage(resp);
                    var err = (string?)_messageErrMessage.GetValue(msg);
                    if (!string.IsNullOrWhiteSpace(err))
                        return false;

                    var addCmd = _messageActionAsAddCommand.Invoke(msg, Array.Empty<object>());
                    if (addCmd is null)
                        return false;

                    var devInfo = _addCommandCommandDataAsDeviceInformation.Invoke(addCmd, Array.Empty<object>());
                    if (devInfo is null)
                        return false;

                    var prod = _deviceInformationProduct.GetValue(devInfo);
                    if (prod is null)
                        return false;

                    productName = prod.ToString() ?? string.Empty;
                    isDongle = (bool)(_deviceInformationIsDongle.GetValue(devInfo) ?? false);
                    return !string.IsNullOrWhiteSpace(productName);
                }
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetWirelessRfConnectionStatus(ulong baseId, out bool connected)
        {
            connected = false;

            try
            {
                if (!TryCreateRequestSocket(out var socket, out var _))
                    return false;

                using (socket)
                {
                    socket.SendFrame(BuildWirelessRfConnectionStatusRequest(baseId));
                    if (!socket.TryReceiveFrameBytes(TimeSpan.FromSeconds(1), out var resp) || resp is null)
                        return false;

                    var msg = GetRootAsMessage(resp);
                    var err = (string?)_messageErrMessage.GetValue(msg);
                    if (!string.IsNullOrWhiteSpace(err))
                        return false;

                    var addCmd = _messageActionAsAddCommand.Invoke(msg, Array.Empty<object>());
                    if (addCmd is null)
                        return false;

                    var status = _addCommandCommandDataAsWirelessRfConnectionStatus.Invoke(addCmd, Array.Empty<object>());
                    if (status is null)
                        return false;

                    connected = (bool)(_wirelessRfConnectionStatusConnected.GetValue(status) ?? false);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool TryCreateRequestSocket(out RequestSocket socket, out string connectPath)
        {
            socket = null!;
            connectPath = string.Empty;

            for (var port = 6890; port <= 6899; port++)
            {
                var s = new RequestSocket();
                var path = $"tcp://localhost:{port}";
                s.Connect(path);

                // send handshake and accept any response (NGENUITY's client code doesn't validate the bytes).
                s.SendFrame(HxAck);
                if (s.TryReceiveFrameBytes(TimeSpan.FromSeconds(1), out var _))
                {
                    socket = s;
                    connectPath = path;
                    return true;
                }

                try { s.Disconnect(path); } catch { /* ignore */ }
                s.Dispose();
            }

            return false;
        }

        private object GetRootAsMessage(byte[] bytes)
        {
            var bb = _byteBufferCtor.Invoke(new object[] { bytes });
            return _getRootAsMessage.Invoke(null, new[] { bb })!;
        }

        private byte[] BuildDeviceListRequest()
        {
            var builder = _flatBufferBuilderCtor.Invoke(new object[] { 1024 });

            _deviceListStart.Invoke(null, new[] { builder });
            var deviceListOffset = _deviceListEnd.Invoke(null, new[] { builder })!;
            var deviceListOffsetVal = GetOffsetValue(deviceListOffset);

            _addRequestStart.Invoke(null, new[] { builder });
            _addRequestAddRequestData.Invoke(null, new[] { builder, deviceListOffsetVal });
            var requestDataEnum = Enum.Parse(_requestDataEnumType, "DeviceList");
            _addRequestAddRequestDataType.Invoke(null, new[] { builder, requestDataEnum });
            var addReqOffset = _addRequestEnd.Invoke(null, new[] { builder })!;
            var addReqOffsetVal = GetOffsetValue(addReqOffset);

            _messageStartMessage.Invoke(null, new[] { builder });
            _messageAddAction.Invoke(null, new[] { builder, addReqOffsetVal });
            var actionEnum = Enum.Parse(_actionEnumType, "AddRequest");
            _messageAddActionType.Invoke(null, new[] { builder, actionEnum });
            var msgOffset = _messageEndMessage.Invoke(null, new[] { builder })!;
            _messageFinishMessageBuffer.Invoke(null, new[] { builder, msgOffset });

            return BuilderToSizedArray(builder);
        }

        private byte[] BuildDeviceInformationRequest(ulong baseId)
        {
            var builder = _flatBufferBuilderCtor.Invoke(new object[] { 1024 });

            _deviceInformationStart.Invoke(null, new[] { builder });
            var devInfoOffset = _deviceInformationEnd.Invoke(null, new[] { builder })!;
            var devInfoOffsetVal = GetOffsetValue(devInfoOffset);

            _addCommandStart.Invoke(null, new[] { builder });
            var cmdDataEnum = Enum.Parse(_commandDataEnumType, "DeviceInformation");
            _addCommandAddCommandDataType.Invoke(null, new[] { builder, cmdDataEnum });
            _addCommandAddCommandData.Invoke(null, new[] { builder, devInfoOffsetVal });
            var addCmdOffset = _addCommandEnd.Invoke(null, new[] { builder })!;
            var addCmdOffsetVal = GetOffsetValue(addCmdOffset);

            _messageStartMessage.Invoke(null, new[] { builder });
            _messageAddBaseId.Invoke(null, new[] { builder, baseId });
            _messageAddAction.Invoke(null, new[] { builder, addCmdOffsetVal });
            var actionEnum = Enum.Parse(_actionEnumType, "AddCommand");
            _messageAddActionType.Invoke(null, new[] { builder, actionEnum });
            var msgOffset = _messageEndMessage.Invoke(null, new[] { builder })!;
            _messageFinishMessageBuffer.Invoke(null, new[] { builder, msgOffset });

            return BuilderToSizedArray(builder);
        }

        private byte[] BuildWirelessRfConnectionStatusRequest(ulong baseId)
        {
            var builder = _flatBufferBuilderCtor.Invoke(new object[] { 1024 });

            _wirelessRfConnectionStatusStart.Invoke(null, new[] { builder });
            var statusOffset = _wirelessRfConnectionStatusEnd.Invoke(null, new[] { builder })!;
            var statusOffsetVal = GetOffsetValue(statusOffset);

            _addCommandStart.Invoke(null, new[] { builder });
            var cmdDataEnum = Enum.Parse(_commandDataEnumType, "WirelessRFConnectionStatus");
            _addCommandAddCommandDataType.Invoke(null, new[] { builder, cmdDataEnum });
            _addCommandAddCommandData.Invoke(null, new[] { builder, statusOffsetVal });
            var addCmdOffset = _addCommandEnd.Invoke(null, new[] { builder })!;
            var addCmdOffsetVal = GetOffsetValue(addCmdOffset);

            _messageStartMessage.Invoke(null, new[] { builder });
            _messageAddBaseId.Invoke(null, new[] { builder, baseId });
            _messageAddAction.Invoke(null, new[] { builder, addCmdOffsetVal });
            var actionEnum = Enum.Parse(_actionEnumType, "AddCommand");
            _messageAddActionType.Invoke(null, new[] { builder, actionEnum });
            var msgOffset = _messageEndMessage.Invoke(null, new[] { builder })!;
            _messageFinishMessageBuffer.Invoke(null, new[] { builder, msgOffset });

            return BuilderToSizedArray(builder);
        }

        private int GetOffsetValue(object offsetStruct)
        {
            // Offset<T> is a struct; depending on FlatBuffers version, it may expose Value as:
            // - a public property, OR
            // - a public field.
            var t = offsetStruct.GetType();

            var prop = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (prop is not null && prop.PropertyType == typeof(int))
                return (int)(prop.GetValue(offsetStruct) ?? 0);

            var field = t.GetField("Value", BindingFlags.Public | BindingFlags.Instance);
            if (field is not null && field.FieldType == typeof(int))
                return (int)(field.GetValue(offsetStruct) ?? 0);

            // Fallback: some builds use lowercase field name.
            field = t.GetField("value", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null && field.FieldType == typeof(int))
                return (int)(field.GetValue(offsetStruct) ?? 0);

            throw new MissingMemberException(t.FullName, "Value");
        }

        private byte[] BuilderToSizedArray(object flatBufferBuilder)
        {
            var dataBuffer = _flatBufferBuilderDataBuffer.GetValue(flatBufferBuilder)
                             ?? throw new InvalidOperationException("FlatBufferBuilder.DataBuffer returned null.");
            var msg = _getRootAsMessage.Invoke(null, new[] { dataBuffer })!;
            var bb = _messageType.GetProperty("ByteBuffer", BindingFlags.Public | BindingFlags.Instance)!.GetValue(msg)!;
            return (byte[])_byteBufferToSizedArray.Invoke(bb, Array.Empty<object>())!;
        }

        private static Type RequireType(Assembly asm, string name)
            => asm.GetType(name, throwOnError: true, ignoreCase: false)!;

        private static PropertyInfo RequireProperty(Type t, string name)
            => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
               ?? throw new MissingMemberException(t.FullName, name);

        private static MethodInfo RequireMethod(Type t, string name)
            => RequireMethod(t, name, BindingFlags.Public | BindingFlags.Instance);

        private static MethodInfo RequireMethod(Type t, string name, BindingFlags flags)
            => t.GetMethod(name, flags)
               ?? throw new MissingMethodException(t.FullName, name);
    }
}


