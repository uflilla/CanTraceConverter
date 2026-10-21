using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UcanView.Models;
using UcanDotNET;
using static UcanDotNET.USBcanServer;

namespace UcanView.Services
{
    /// <summary>
    /// Shares one <see cref="USBcanServer"/> / InitHardware per physical module so
    /// channel 0 and channel 1 can be opened from separate monitor windows.
    /// </summary>
    /// <remarks>
    /// <para>Locking order is <c>Gate</c> -> <c>SharedModule._ioLock</c> -> <c>UcanServerAccess.Lock</c>.
    /// A thread must never acquire an earlier lock while holding a later one, and no lock is
    /// ever held across a thread join.</para>
    /// <para>Driver event handlers only record state and signal the RX thread. Every call
    /// back into the driver happens on the RX thread or on a caller thread, never inside an
    /// event handler — the wrapper documents that as a deadlock hazard.</para>
    /// </remarks>
    internal static class UsbCanHardwareManager
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<byte, SharedModule> Modules = new Dictionary<byte, SharedModule>();

        public static bool TryAcquire(
            SessionConfig config,
            Action<CanMessageRecord> onMessage,
            Action onDisconnected,
            Action<CanBusStatus> onBusStatus,
            out AcquiredChannel acquired,
            out string error)
        {
            acquired = null;
            error = null;

            if (config == null)
            {
                error = "Session configuration is missing.";
                return false;
            }

            if (config.Channel > 1)
            {
                error = $"Unsupported CAN channel {config.Channel}.";
                return false;
            }

            // Disposal happens after the lock is released: DisposeHardware joins the RX
            // thread, and that thread may itself be waiting on Gate to report a disconnect.
            SharedModule abandoned = null;
            try
            {
                lock (Gate)
                {
                    SharedModule module = FindExistingModule(config.DeviceNumber, config.Channel);
                    bool createdHere = false;

                    if (module == null)
                    {
                        if (!TryCreateModule(config.DeviceNumber, out module, out error))
                            return false;
                        createdHere = true;
                    }

                    if (module.IsChannelOpen(config.Channel))
                    {
                        error = $"Channel {config.Channel} on device {module.DeviceNumber} is already open in another monitor window.";
                        if (createdHere)
                            abandoned = DetachModule_NoLock(module);
                        return false;
                    }

                    if (!module.TryOpenChannel(config, onMessage, onDisconnected, onBusStatus, out error))
                    {
                        if (!module.HasOpenChannels)
                            abandoned = DetachModule_NoLock(module);
                        return false;
                    }

                    acquired = new AcquiredChannel(module, config.Channel);
                    return true;
                }
            }
            finally
            {
                abandoned?.DisposeHardware();
            }
        }

        public static void Release(AcquiredChannel acquired)
        {
            if (acquired == null)
                return;

            SharedModule module;
            byte channel;
            bool disposeModule = false;

            lock (Gate)
            {
                module = acquired.Module;
                if (module == null)
                    return;

                channel = acquired.Channel;
                acquired.Detach();

                // Publish "closed" before any native teardown so the RX thread stops
                // reading this channel first (it re-checks the flag under _ioLock).
                module.MarkChannelClosed(channel);

                if (!module.HasOpenChannels &&
                    Modules.TryGetValue(module.DeviceNumber, out var current) &&
                    ReferenceEquals(current, module))
                {
                    Modules.Remove(module.DeviceNumber);
                    disposeModule = true;
                }
            }

            // Slow work happens outside Gate: no registry lock is held across USB I/O or a
            // thread join, so a disconnect running on the RX thread can never block on us.
            if (disposeModule)
                module.DisposeHardware();
            else
                module.CloseChannelIo(channel);
        }

        /// <summary>
        /// Stops all TX (including firmware cyclic), closes every channel, and releases
        /// USB-CAN hardware so other applications can open the device.
        /// </summary>
        public static void ReleaseAll()
        {
            SharedModule[] modules;
            lock (Gate)
            {
                modules = Modules.Values.ToArray();
                Modules.Clear();
            }

            foreach (var module in modules)
            {
                try
                {
                    module.DisposeHardware();
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, "Failed to release USB-CAN module " + module.DeviceNumber + ".");
                }
            }

            if (modules.Length > 0)
                AppLog.Info("Released {0} USB-CAN module(s).", modules.Length);
        }

        public static bool HasOpenModules
        {
            get { lock (Gate) return Modules.Count > 0; }
        }

        public static CanDeviceItem[] SnapshotOpenDevices()
        {
            lock (Gate)
            {
                return Modules.Values
                    .Select(m => new CanDeviceItem
                    {
                        DeviceNumber = m.DeviceNumber,
                        SerialNumber = m.HardwareInfo.m_dwSerialNr,
                        ProductCode = m.HardwareInfo.m_dwProductCode,
                        InUse = true,
                        Label = $"Device {m.DeviceNumber} (SN:{m.HardwareInfo.m_dwSerialNr}) — open in app"
                    })
                    .ToArray();
            }
        }

        private static SharedModule FindExistingModule(byte deviceNumber, byte channel)
        {
            if (deviceNumber == USBCAN_ANY_MODULE)
            {
                foreach (var module in Modules.Values)
                {
                    if (!module.IsChannelOpen(channel))
                        return module;
                }

                return null;
            }

            return Modules.TryGetValue(deviceNumber, out var existing) ? existing : null;
        }

        private static bool TryCreateModule(byte requestedDeviceNumber, out SharedModule module, out string error)
        {
            module = null;
            error = null;

            byte target = requestedDeviceNumber;

            // "First available" must never resolve onto a module this application already
            // owns: initialising a second handle to it and then releasing that handle would
            // deinitialise the hardware underneath a live session. Resolve to a concrete
            // free device number first.
            if (target == USBCAN_ANY_MODULE && Modules.Count > 0 &&
                !TryResolveFreeDeviceNumber_NoLock(out target, out error))
            {
                return false;
            }

            TrackedUcanServer server;
            int result;
            var hw = new tUcanHardwareInfoEx();
            byte nativeHandle;
            lock (UcanServerAccess.Lock)
            {
                server = UcanServerAccess.Create();
                try
                {
                    result = server.InitHardware(target);
                }
                catch (Exception ex)
                {
                    error = $"InitHardware failed: {ex.Message}";
                    SafeReleaseServer(server);
                    return false;
                }

                if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                {
                    error = DescribeHwError("InitHardware", result);
                    SafeReleaseServer(server);
                    return false;
                }

                nativeHandle = server.NativeHandle;
                var ch0 = new tUcanChannelInfo();
                var ch1 = new tUcanChannelInfo();
                server.GetHardwareInfo(ref hw, ref ch0, ref ch1);
            }

            byte resolved = hw.m_bDeviceNr;
            if (Modules.ContainsKey(resolved))
            {
                error = $"Device {resolved} is already open in this application.";
                AppLog.Warn("InitHardware({0}) resolved to device {1}, which is already open here; releasing the duplicate handle.",
                    target, resolved);
                SafeReleaseServer(server);
                return false;
            }

            module = new SharedModule(server, nativeHandle, resolved, hw);
            Modules[resolved] = module;
            return true;
        }

        /// <summary>
        /// Enumerates (without initialising) to find a device number this application has
        /// not already opened. Called with <see cref="Gate"/> held.
        /// </summary>
        private static bool TryResolveFreeDeviceNumber_NoLock(out byte deviceNumber, out string error)
        {
            deviceNumber = USBCAN_ANY_MODULE;
            error = null;

            var candidates = new List<byte>();
            try
            {
                lock (UcanServerAccess.Lock)
                {
                    USBcanServer.EnumerateHardware(
                        0, 254,
                        (int index, bool isUsed, tUcanHardwareInfoEx hw, ref bool doInit, ref bool tryNext) =>
                        {
                            // Never initialise during discovery — that would open hardware we are
                            // only inspecting, and the driver ignores it for in-use devices anyway.
                            doInit = false;
                            tryNext = true;
                            if (!isUsed && !candidates.Contains(hw.m_bDeviceNr))
                                candidates.Add(hw.m_bDeviceNr);
                        },
                        true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Could not enumerate devices while resolving auto-detect.");
            }

            foreach (var candidate in candidates.OrderBy(d => d))
            {
                if (!Modules.ContainsKey(candidate))
                {
                    deviceNumber = candidate;
                    return true;
                }
            }

            error = "No further USB-CAN device is available. Both channels of the connected module are already open.";
            return false;
        }

        /// <summary>Shuts a server down and disposes it, so the wrapper releases its handle
        /// registration and plug-and-play reference count.</summary>
        internal static void SafeReleaseServer(USBcanServer server)
        {
            if (server == null)
                return;

            try { Shutdown(server, (byte)eUcanChannel.USBCAN_CHANNEL_ALL, true); } catch { /* ignore */ }
            UcanServerAccess.Dispose(server);
        }

        /// <summary>
        /// Shuts down through <see cref="TrackedUcanServer.ShutdownIgnoringErrors"/> where possible.
        /// Servers returned by <c>EnumerateHardware</c> are plain wrappers, but they have no
        /// initialised channel whose error code could overflow the wrapper's own Shutdown.
        /// </summary>
        private static void Shutdown(USBcanServer server, byte channel, bool shutDownHardware)
        {
            if (server is TrackedUcanServer tracked)
                tracked.ShutdownIgnoringErrors(channel, shutDownHardware);
            else
                server.Shutdown(channel, shutDownHardware);
        }

        /// <summary>
        /// Unregisters a module and hands it back for disposal by the caller, once the caller
        /// has released <see cref="Gate"/>.
        /// </summary>
        private static SharedModule DetachModule_NoLock(SharedModule module)
        {
            if (module == null)
                return null;

            Modules.Remove(module.DeviceNumber);
            return module;
        }

        internal static string DescribeHwError(string operation, int code)
        {
            if (code == (int)eUcanReturn.USBCAN_ERR_DISCONECT)
                return $"{operation} failed: no USB-CAN device connected (0x{code:X}).";

            if (code == (int)eUcanReturn.USBCAN_ERR_HWINUSE)
                return $"{operation} failed: device already in use by another application (0x{code:X}).";

            if (code == (int)eUcanReturn.USBCAN_ERR_ILLHW)
                return $"{operation} failed: hardware not found or already in use (0x{code:X}). Check device, drivers, and other open sessions.";

            if (code == (int)eUcanReturn.USBCAN_ERRCMD_ALREADYINIT)
                return $"{operation} failed: channel already initialized (0x{code:X}).";

            if (code == (int)eUcanReturn.USBCAN_ERR_ILLCHANNEL)
                return $"{operation} failed: this module does not have that CAN channel (0x{code:X}).";

            return $"{operation} failed (0x{code:X}).";
        }

        internal sealed class AcquiredChannel
        {
            internal SharedModule Module { get; private set; }
            public byte Channel { get; }
            public byte DeviceNumber => Module?.DeviceNumber ?? 0;
            public tUcanHardwareInfoEx HardwareInfo => Module?.HardwareInfo ?? default;
            public tUcanChannelInfo ChannelInfo => Module?.GetChannelInfo(Channel) ?? default;
            public bool IsConnected => Module != null && Module.IsChannelOpen(Channel);

            internal AcquiredChannel(SharedModule module, byte channel)
            {
                Module = module;
                Channel = channel;
            }

            public bool SendOnce(uint canId, byte dlc, byte[] data, bool extended, bool rtr)
            {
                var module = Module;
                return module != null && module.SendOnce(Channel, canId, dlc, data, extended, rtr);
            }

            public bool SendPeriodicFirmware(int intervalMs, uint canId, byte dlc, byte[] data, bool extended)
            {
                var module = Module;
                return module != null && module.SendPeriodicFirmware(Channel, intervalMs, canId, dlc, data, extended);
            }

            public void StopPeriodicFirmware()
            {
                var module = Module;
                module?.StopPeriodicFirmware(Channel);
            }

            public bool ResetController(out string error)
            {
                var module = Module;
                if (module == null)
                {
                    error = "Not connected to CAN hardware.";
                    return false;
                }

                return module.ResetController(Channel, out error);
            }

            internal void Detach() => Module = null;
        }

        internal sealed class SharedModule
        {
            private const long StatusPollIntervalMs = 300;

            /// <summary>Status bits that mean a real bus or controller fault. Everything else
            /// the driver reports (queue empty, TX buffer full) is a normal transient state.</summary>
            private const int RealBusErrorMask =
                (int)eUcanCanStatus.USBCAN_CANERR_OVERRUN |
                (int)eUcanCanStatus.USBCAN_CANERR_BUSLIGHT |
                (int)eUcanCanStatus.USBCAN_CANERR_BUSHEAVY |
                (int)eUcanCanStatus.USBCAN_CANERR_BUSOFF |
                (int)eUcanCanStatus.USBCAN_CANERR_QOVERRUN |
                (int)eUcanCanStatus.USBCAN_CANERR_REGTEST |
                (int)eUcanCanStatus.USBCAN_CANERR_MEMTEST |
                (int)eUcanCanStatus.USBCAN_CANERR_TXMSGLOST;

            /// <summary>Serialises every call into the native wrapper for this module.
            /// The wrapper does not lock <c>ReadCanMsg</c> against <c>Shutdown</c>.</summary>
            private readonly object _ioLock = new object();

            /// <summary>Guards only the disconnect callback hand-off.</summary>
            private readonly object _sync = new object();

            private readonly ChannelSlot[] _channels = { new ChannelSlot(), new ChannelSlot() };
            private readonly AutoResetEvent _msgSignal = new AutoResetEvent(false);
            private readonly System.Diagnostics.Stopwatch _statusClock = System.Diagnostics.Stopwatch.StartNew();
            private long _lastStatusPollMs;
            private USBcanServer _server;

            /// <summary>The driver handle, kept so it can be released after a fatal disconnect
            /// (see <see cref="TrackedUcanServer"/>).</summary>
            private readonly byte _nativeHandle;
            private bool _nativeHandleReleased;
            private Thread _rxThread;
            private volatile bool _rxRunning;
            private volatile bool _disconnectRequested;
            private volatile bool _fatalDisconnect;
            private volatile bool _dead;
            private bool _eventsHooked;
            private int _disposed;
            private long _rxFaultCount;

            /// <summary>Unwrapped tick counts for the batch being dispatched. RX thread only.</summary>
            private long[] _ticksBuffer = new long[64];

            public byte DeviceNumber { get; }
            public tUcanHardwareInfoEx HardwareInfo { get; private set; }
            public bool HasOpenChannels => _channels.Any(c => c.Open);
            public bool IsDead => _dead;

            public SharedModule(USBcanServer server, byte nativeHandle, byte deviceNumber, tUcanHardwareInfoEx hwInfo)
            {
                _server = server;
                _nativeHandle = nativeHandle;
                DeviceNumber = deviceNumber;
                HardwareInfo = hwInfo;
                HookEvents();
                StartRxThread();
            }

            public bool IsChannelOpen(byte channel) =>
                channel < _channels.Length && _channels[channel].Open;

            public tUcanChannelInfo GetChannelInfo(byte channel) =>
                channel < _channels.Length ? _channels[channel].Info : default;

            public bool TryOpenChannel(
                SessionConfig config,
                Action<CanMessageRecord> onMessage,
                Action onDisconnected,
                Action<CanBusStatus> onBusStatus,
                out string error)
            {
                error = null;

                if (_dead || _server == null)
                {
                    error = "The USB-CAN module is no longer available.";
                    return false;
                }

                byte channel = config.Channel;

                byte mode = config.ListenOnly
                    ? (byte)tUcanMode.kUcanModeListenOnly
                    : (byte)(tUcanMode.kUcanModeNormal | tUcanMode.kUcanModeTxEcho);

                bool highResRequested = CheckIs_G4(HardwareInfo);
                if (highResRequested)
                    mode |= (byte)tUcanMode.kUcanModeHighResTimer;

                int baudEx = CanMessageHelper.MapBaudRateKbps(config.BaudRateKbps, HardwareInfo);
                short wBtr = (short)eUcanBaudrate.USBCAN_BAUD_USE_BTREX;

                int result;
                lock (_ioLock)
                {
                    if (_server == null)
                    {
                        error = "The USB-CAN module is no longer available.";
                        return false;
                    }

                    result = _server.InitCan(
                        channel,
                        wBtr,
                        baudEx,
                        USBCAN_AMR_ALL,
                        USBCAN_ACR_ALL,
                        mode,
                        (byte)eUcanOutputControl.USBCAN_OCR_DEFAULT);

                    if (result == (int)eUcanReturn.USBCAN_SUCCESSFUL)
                        RefreshHardwareInfo_NoLock();
                }

                if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                {
                    error = DescribeHwError("InitCan", result);
                    return false;
                }

                var slot = _channels[channel];

                // Resolve the timestamp scale once, here, rather than re-reading a shared
                // struct on the RX thread for every message.
                bool highResActive = highResRequested &&
                    (slot.Info.m_bMode & (byte)tUcanMode.kUcanModeHighResTimer) != 0;

                slot.ListenOnly = config.ListenOnly;
                slot.TimestampScaleMs = highResActive ? 0.1 : 1.0;
                slot.ResetTimeUnwrap();
                slot.LastStatusKey = -1;
                slot.OnMessage = onMessage;
                slot.OnDisconnected = onDisconnected;
                slot.OnBusStatus = onBusStatus;

                // Published last: readers test Open (volatile) before touching anything else.
                slot.Open = true;

                AppLog.Info("Device {0} CH{1} opened: {2} kBit/s, listen-only={3}, timestamp scale={4} ms/tick.",
                    DeviceNumber, channel, config.BaudRateKbps, config.ListenOnly, slot.TimestampScaleMs);

                SignalRx();
                return true;
            }

            /// <summary>
            /// Marks a channel closed so the RX thread stops reading it. Does no native I/O:
            /// callers run <see cref="CloseChannelIo"/> or <see cref="DisposeHardware"/> after
            /// releasing the registry lock.
            /// </summary>
            public bool MarkChannelClosed(byte channel)
            {
                if (channel >= _channels.Length)
                    return false;

                var slot = _channels[channel];
                if (!slot.Open)
                    return false;

                slot.Open = false;
                slot.OnMessage = null;
                slot.OnDisconnected = null;
                slot.OnBusStatus = null;
                return true;
            }

            /// <summary>
            /// Deinitialises one channel while the other one stays in use. Runs after
            /// <see cref="MarkChannelClosed"/>, so the RX thread is no longer reading it.
            /// </summary>
            public void CloseChannelIo(byte channel)
            {
                if (channel >= _channels.Length)
                    return;

                StopPeriodicFirmware(channel);

                lock (_ioLock)
                {
                    try
                    {
                        // Keep the hardware open — the other channel is still in use.
                        if (_server != null)
                            Shutdown(_server, channel, false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn(ex, "Shutdown of device " + DeviceNumber + " CH" + channel + " failed.");
                    }
                }
            }

            public void DisposeHardware()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                _dead = true;

                for (byte channel = 0; channel < _channels.Length; channel++)
                    MarkChannelClosed(channel);

                // Unhook before stopping the reader so no further callbacks arrive.
                UnhookEvents();

                _rxRunning = false;
                SignalRx();
                JoinRxThread();

                lock (_ioLock)
                {
                    var server = _server;
                    _server = null;

                    if (server != null)
                    {
                        try
                        {
                            // Firmware cyclic TX is only touched when it was actually started,
                            // and never after a fatal disconnect — the device is gone.
                            if (!_fatalDisconnect)
                            {
                                for (byte channel = 0; channel < _channels.Length; channel++)
                                    StopPeriodicFirmware_NoLock(server, channel);
                            }

                            // Final hard close — frees the module for other applications.
                            Shutdown(server, (byte)eUcanChannel.USBCAN_CHANNEL_ALL, true);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn(ex, "Final shutdown of device " + DeviceNumber + " failed.");
                        }

                        // Dispose releases the wrapper's static handle registration and its
                        // plug-and-play reference; without it, stale entries can absorb the
                        // fatal-disconnect callback meant for a later module.
                        UcanServerAccess.Dispose(server);
                    }
                }

                if (Interlocked.Read(ref _rxFaultCount) > 0)
                {
                    AppLog.Warn("Device {0}: {1} receive error(s) were suppressed during this session.",
                        DeviceNumber, Interlocked.Read(ref _rxFaultCount));
                }

                // _msgSignal is deliberately not disposed: a driver callback thread can still
                // be between its _rxRunning check and its Set(), and disposing here would
                // throw ObjectDisposedException on a thread we do not own.
            }

            public bool SendOnce(byte channel, uint canId, byte dlc, byte[] data, bool extended, bool rtr)
            {
                if (channel >= _channels.Length || !IsChannelOpen(channel) || _channels[channel].ListenOnly)
                    return false;

                try
                {
                    var msg = CanMessageHelper.ToHardwareMessage(canId, dlc, data, extended, rtr);
                    var list = new[] { msg };
                    int count = 0;

                    lock (_ioLock)
                    {
                        if (_server == null || !_channels[channel].Open)
                            return false;

                        int result = _server.WriteCanMsg(channel, ref list, ref count);
                        return result == (int)eUcanReturn.USBCAN_SUCCESSFUL && count > 0;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, "WriteCanMsg failed on device " + DeviceNumber + " CH" + channel + ".");
                    return false;
                }
            }

            public bool SendPeriodicFirmware(byte channel, int intervalMs, uint canId, byte dlc, byte[] data, bool extended)
            {
                if (channel >= _channels.Length || !IsChannelOpen(channel) ||
                    _channels[channel].ListenOnly || !CheckSupportCyclicMsg(HardwareInfo))
                {
                    return false;
                }

                try
                {
                    var msg = CanMessageHelper.ToHardwareMessage(canId, dlc, data, extended, false);
                    msg.m_dwTime = intervalMs;
                    var list = new[] { msg };

                    lock (_ioLock)
                    {
                        if (_server == null || !_channels[channel].Open)
                            return false;

                        int result = _server.DefineCyclicCanMsg(channel, ref list);
                        if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                            return false;

                        result = _server.EnableCyclicCanMsg(channel, (int)eUcanCyclicFlags.USBCAN_CYCLIC_FLAG_START);
                        if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                            return false;

                        _channels[channel].FirmwareCyclicActive = true;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, "Firmware cyclic TX could not be started on device " + DeviceNumber + ".");
                    return false;
                }
            }

            /// <summary>
            /// Stops device-side cyclic transmission. Only issues USB traffic when this module
            /// actually started it — the previous guard sent two round-trips on every teardown.
            /// </summary>
            public void StopPeriodicFirmware(byte channel)
            {
                if (channel >= _channels.Length || !_channels[channel].FirmwareCyclicActive)
                    return;

                lock (_ioLock)
                {
                    StopPeriodicFirmware_NoLock(_server, channel);
                }
            }

            private void StopPeriodicFirmware_NoLock(USBcanServer server, byte channel)
            {
                if (server == null || !_channels[channel].FirmwareCyclicActive)
                    return;

                try
                {
                    server.EnableCyclicCanMsg(channel, (int)eUcanCyclicFlags.USBCAN_CYCLIC_FLAG_STOPP);
                    var empty = new tCanMsgStruct[0];
                    server.DefineCyclicCanMsg(channel, ref empty);
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, "Stopping firmware cyclic TX on device " + DeviceNumber + " failed.");
                }
                finally
                {
                    _channels[channel].FirmwareCyclicActive = false;
                }
            }

            /// <summary>Resets the CAN controller — the documented recovery from bus-off.</summary>
            public bool ResetController(byte channel, out string error)
            {
                error = null;

                if (channel >= _channels.Length || !IsChannelOpen(channel))
                {
                    error = "Channel is not open.";
                    return false;
                }

                lock (_ioLock)
                {
                    if (_server == null)
                    {
                        error = "The USB-CAN module is no longer available.";
                        return false;
                    }

                    try
                    {
                        int result = _server.ResetCan(channel, (int)eUcanResetFlags.USBCAN_RESET_ALL);
                        if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                        {
                            error = DescribeHwError("ResetCan", result);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        error = "ResetCan failed: " + ex.Message;
                        return false;
                    }
                }

                _channels[channel].LastStatusKey = -1;
                SignalRx();
                AppLog.Info("CAN controller reset on device {0} CH{1}.", DeviceNumber, channel);
                return true;
            }

            // ---------------------------------------------------------------- RX thread

            private void StartRxThread()
            {
                _rxRunning = true;
                _rxThread = new Thread(RxLoop)
                {
                    IsBackground = true,
                    Name = $"CanRx-Dev{DeviceNumber}",
                    // Spends almost all its time blocked on the driver signal; when frames
                    // arrive it must drain them before the device FIFO overruns.
                    Priority = ThreadPriority.Highest
                };
                _rxThread.Start();
            }

            private void JoinRxThread()
            {
                var thread = _rxThread;
                if (thread == null || thread == Thread.CurrentThread)
                    return;

                try
                {
                    if (!thread.Join(2000))
                        AppLog.Warn("RX thread for device {0} did not stop within 2 s.", DeviceNumber);
                }
                catch { /* ignore */ }

                _rxThread = null;
            }

            private void SignalRx()
            {
                // The signal is intentionally never disposed, but a race with teardown can
                // still leave it unusable in edge cases — never let that escape.
                try { _msgSignal.Set(); } catch { /* ignore */ }
            }

            private void RxLoop()
            {
                var buffer = new tCanMsgStruct[64];

                while (_rxRunning)
                {
                    try
                    {
                        _msgSignal.WaitOne(50);

                        if (_disconnectRequested && ProcessPendingDisconnect())
                            break;

                        DrainReceiveQueue(buffer);
                        PollStatus();
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _rxFaultCount);
                        if (Interlocked.Read(ref _rxFaultCount) == 1)
                            AppLog.Warn(ex, "First receive-loop error on device " + DeviceNumber + "; further errors are counted only.");

                        if (_rxRunning)
                            Thread.Sleep(10);
                    }
                }
            }

            private void DrainReceiveQueue(tCanMsgStruct[] buffer)
            {
                if (_ticksBuffer.Length < buffer.Length)
                    _ticksBuffer = new long[buffer.Length];
                var ticksBuffer = _ticksBuffer;

                for (byte channel = 0; channel < _channels.Length; channel++)
                {
                    var slot = _channels[channel];
                    if (!slot.Open)
                        continue;

                    while (_rxRunning)
                    {
                        byte readChannel = channel;
                        int count = 0;
                        int result;

                        lock (_ioLock)
                        {
                            // Re-check inside the lock: a close may have landed while we waited.
                            if (_server == null || !slot.Open)
                                break;

                            result = _server.ReadCanMsg(ref readChannel, ref buffer, ref count);
                        }

                        if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL || count <= 0)
                            break;

                        if (ticksBuffer.Length < count)
                            _ticksBuffer = ticksBuffer = new long[count];

                        long readUtcTicks = PreciseClock.UtcNowTicks;
                        var handler = slot.OnMessage;
                        if (handler == null)
                            break; // Channel is closing — stop draining rather than spinning.

                        // Wall time is derived from the hardware timestamp, not from when the
                        // batch was read: every frame in a USB batch used to get the same wall
                        // time. The newest frame bounds the clock offset best, so the whole
                        // batch is unwrapped before any frame is mapped.
                        long newestTicks = long.MinValue;
                        for (int i = 0; i < count; i++)
                        {
                            ticksBuffer[i] = slot.UnwrapTime(buffer[i].m_dwTime, readUtcTicks);
                            if (ticksBuffer[i] > newestTicks)
                                newestTicks = ticksBuffer[i];
                        }
                        slot.ObserveWallClock(newestTicks, readUtcTicks);

                        for (int i = 0; i < count; i++)
                        {
                            try
                            {
                                long ticks = ticksBuffer[i];
                                var record = CanMessageHelper.FromHardwareMessage(
                                    buffer[i], slot.TimestampScaleMs, ticks, slot.ToWallTime(ticks));
                                handler.Invoke(record);
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref _rxFaultCount);
                                if (Interlocked.Read(ref _rxFaultCount) == 1)
                                    AppLog.Warn(ex, "First message dispatch error on device " + DeviceNumber + "; further errors are counted only.");
                            }
                        }
                    }
                }
            }

            /// <summary>
            /// Reads CAN status on a fixed cadence rather than only when the driver raises an
            /// event, so a fault that clears is reported as cleared.
            /// </summary>
            private void PollStatus()
            {
                long now = _statusClock.ElapsedMilliseconds;
                if (now - _lastStatusPollMs < StatusPollIntervalMs)
                    return;

                bool didWork = false;

                for (byte channel = 0; channel < _channels.Length; channel++)
                {
                    var slot = _channels[channel];
                    if (!slot.Open)
                        continue;

                    didWork = true;

                    int raw = 0;
                    lock (_ioLock)
                    {
                        if (_server == null || !slot.Open)
                            continue;

                        var status = new tStatusStruct();
                        int result = _server.GetStatus(channel, ref status);
                        if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                            continue;

                        raw = status.m_wCanStatus;
                    }

                    int key = raw & RealBusErrorMask;
                    if (key == slot.LastStatusKey)
                        continue;

                    slot.LastStatusKey = key;

                    var busStatus = CanBusStatus.FromRawStatus(raw);
                    var handler = slot.OnBusStatus;
                    try { handler?.Invoke(busStatus); }
                    catch (Exception ex) { AppLog.Warn(ex, "Bus status handler threw."); }

                    if (busStatus.IsError)
                        AppLog.Warn("Device {0} CH{1} bus status: {2} (0x{3:X}).", DeviceNumber, channel, busStatus.Message, raw);
                }

                if (didWork)
                    _lastStatusPollMs = now;
            }

            // ------------------------------------------------------------ driver events
            //
            // Every handler below runs on a driver-owned thread. The wrapper documents that
            // calling any class method from inside one risks a deadlock, and forbids it
            // outright after a fatal disconnect — so these only record state and signal.

            private void HookEvents()
            {
                if (_eventsHooked || _server == null)
                    return;

                _server.CanMsgReceivedEvent += OnCanMsgReceived;
                _server.StatusEvent += OnStatusEvent;
                _server.FatalDisconnectEvent += OnFatalDisconnect;
                USBcanServer.DisconnectEvent += OnDisconnect;
                _eventsHooked = true;
            }

            private void UnhookEvents()
            {
                if (!_eventsHooked)
                    return;

                try
                {
                    if (_server != null)
                    {
                        _server.CanMsgReceivedEvent -= OnCanMsgReceived;
                        _server.StatusEvent -= OnStatusEvent;
                        _server.FatalDisconnectEvent -= OnFatalDisconnect;
                    }
                    USBcanServer.DisconnectEvent -= OnDisconnect;
                }
                catch { /* ignore detach errors */ }
                finally
                {
                    _eventsHooked = false;
                }
            }

            private void OnCanMsgReceived(byte deviceNr, byte channel)
            {
                try
                {
                    if (_rxRunning)
                        SignalRx();
                }
                catch { /* never throw on a driver thread */ }
            }

            private void OnStatusEvent(byte deviceNr, byte channel)
            {
                try
                {
                    // Wake the reader; it re-reads status on its own cadence.
                    SignalRx();
                }
                catch { /* never throw on a driver thread */ }
            }

            private void OnDisconnect()
            {
                // Static event: fires for any module. The RX thread decides whether it was
                // ours by probing the hardware — a driver call that must not happen here.
                try
                {
                    if (_dead || !HasOpenChannels)
                        return;

                    _disconnectRequested = true;
                    SignalRx();
                }
                catch { /* never throw on a driver thread */ }
            }

            private void OnFatalDisconnect(byte deviceNr)
            {
                try
                {
                    if (deviceNr != DeviceNumber || _dead)
                        return;

                    _fatalDisconnect = true;
                    _disconnectRequested = true;
                    SignalRx();
                }
                catch { /* never throw on a driver thread */ }
            }

            /// <summary>Runs on the RX thread. Returns true when the module was torn down.</summary>
            private bool ProcessPendingDisconnect()
            {
                _disconnectRequested = false;

                bool fatal = _fatalDisconnect;
                AppLog.Info("Device {0}: {1} event received.", DeviceNumber, fatal ? "fatal disconnect" : "disconnect");
                if (!fatal && IsHardwareStillPresent())
                    return false;

                HandleDisconnect(fatal ? "Fatal USB disconnect" : "USB device disconnected");
                return true;
            }

            private bool IsHardwareStillPresent()
            {
                lock (_ioLock)
                {
                    if (_server == null)
                        return false;

                    try
                    {
                        var hw = HardwareInfo;
                        var ch0 = new tUcanChannelInfo();
                        var ch1 = new tUcanChannelInfo();
                        return _server.GetHardwareInfo(ref hw, ref ch0, ref ch1) == (int)eUcanReturn.USBCAN_SUCCESSFUL;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            private void HandleDisconnect(string message)
            {
                // Safe to take Gate here: Release never holds it across a join, so the UI
                // thread cannot be waiting on this thread while holding it.
                lock (Gate)
                {
                    // Under Gate, so no module of this application is initialised meanwhile
                    // and handed the same handle number.
                    if (_fatalDisconnect)
                        ReleaseAbandonedHandle();

                    if (Modules.TryGetValue(DeviceNumber, out var current) && ReferenceEquals(current, this))
                        Modules.Remove(DeviceNumber);
                }

                Action[] callbacks;
                lock (_sync)
                {
                    _dead = true;
                    callbacks = _channels
                        .Where(c => c.Open && c.OnDisconnected != null)
                        .Select(c => c.OnDisconnected)
                        .ToArray();

                    for (byte channel = 0; channel < _channels.Length; channel++)
                        MarkChannelClosed(channel);
                }

                _rxRunning = false;

                foreach (var cb in callbacks)
                {
                    try { cb(); } catch { /* ignore subscriber errors */ }
                }

                AppLog.Warn("Shared USB-CAN module {0} disconnected: {1}", DeviceNumber, message);
                DisposeHardware();
            }

            /// <summary>
            /// After a fatal disconnect the wrapper has dropped its handle without releasing it
            /// in the driver, and will not release it later (see <see cref="TrackedUcanServer"/>).
            /// Releasing it here lets the module be initialised again once it is plugged back in.
            /// </summary>
            private void ReleaseAbandonedHandle()
            {
                lock (_ioLock)
                lock (UcanServerAccess.Lock)
                {
                    if (_nativeHandleReleased || !(_server is TrackedUcanServer server))
                        return;
                    _nativeHandleReleased = true;

                    try
                    {
                        // Logged before the call too: if the driver blocks here, the log shows where.
                        AppLog.Info("Releasing driver handle {0} of disconnected device {1}.", _nativeHandle, DeviceNumber);
                        int result = server.DeinitAbandonedHandle(_nativeHandle, out bool callbackRaised);
                        AppLog.Info("Released driver handle {0} of disconnected device {1} (result 0x{2:X}, deinit callback {3}).",
                            _nativeHandle, DeviceNumber, result, callbackRaised ? "raised" : "not raised");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn(ex, "Releasing the driver handle of disconnected device " + DeviceNumber + " failed.");
                    }
                }
            }

            /// <summary>Caller must hold <see cref="_ioLock"/>.</summary>
            private void RefreshHardwareInfo_NoLock()
            {
                if (_server == null)
                    return;

                var hw = HardwareInfo;
                var ch0 = new tUcanChannelInfo();
                var ch1 = new tUcanChannelInfo();
                _server.GetHardwareInfo(ref hw, ref ch0, ref ch1);
                HardwareInfo = hw;
                _channels[0].Info = ch0;
                _channels[1].Info = ch1;
            }

            private sealed class ChannelSlot
            {
                /// <summary>Wall-clock offset may rise by this fraction of elapsed time, so a
                /// device crystal running slower than the PC clock is still tracked (200 ppm).</summary>
                private const long DriftAllowanceDivisor = 5000;

                private volatile bool _open;
                private uint _lastRawTime;
                private bool _haveRawTime;
                private long _lastUnwrapped;
                private long _lastReadUtcTicks;
                private bool _haveWallOffset;
                private long _wallOffsetUtcTicks;
                private long _wallOffsetUpdatedAt;

                /// <summary>Publishes the rest of the slot: written last on open, first on close.</summary>
                public bool Open
                {
                    get => _open;
                    set => _open = value;
                }

                public bool ListenOnly = true;
                public bool FirmwareCyclicActive;
                public double TimestampScaleMs = 1.0;
                public int LastStatusKey = -1;
                public tUcanChannelInfo Info;
                public volatile Action<CanMessageRecord> OnMessage;
                public volatile Action OnDisconnected;
                public volatile Action<CanBusStatus> OnBusStatus;

                public void ResetTimeUnwrap()
                {
                    _haveRawTime = false;
                    _lastRawTime = 0;
                    _lastUnwrapped = 0;
                    _lastReadUtcTicks = 0;
                    _haveWallOffset = false;
                    _wallOffsetUtcTicks = 0;
                    _wallOffsetUpdatedAt = 0;
                }

                /// <summary>
                /// Turns the device's 32-bit tick counter into a monotonic value. The wrapper
                /// types it as a signed int, so it would otherwise go negative partway through
                /// a long session and wrap entirely soon after. RX thread only.
                /// </summary>
                /// <remarks>
                /// The step from the previous frame is taken modulo 2^32, so a genuine wrap is
                /// just a small forward step. A frame that is slightly older than its
                /// predecessor (TX echo ordering) keeps its own time and is not mistaken for a
                /// wrap — that used to add 2^32 ticks (49.7 days) to every later timestamp.
                /// A large backward step means the counter restarted (controller reset), and
                /// time continues from the wall-clock gap so periods stay meaningful.
                /// </remarks>
                public long UnwrapTime(int rawSigned, long readUtcTicks)
                {
                    uint raw = unchecked((uint)rawSigned);

                    if (!_haveRawTime)
                    {
                        _haveRawTime = true;
                        _lastRawTime = raw;
                        _lastUnwrapped = raw;
                        _lastReadUtcTicks = readUtcTicks;
                        return _lastUnwrapped;
                    }

                    uint forward = unchecked(raw - _lastRawTime);
                    if (forward < 0x8000_0000u)
                    {
                        _lastUnwrapped += forward;
                        _lastRawTime = raw;
                        _lastReadUtcTicks = readUtcTicks;
                        return _lastUnwrapped;
                    }

                    uint backward = unchecked(_lastRawTime - raw);
                    if (backward <= MaxReorderTicks)
                        return _lastUnwrapped - backward;

                    double gapMs = Math.Max(0, readUtcTicks - _lastReadUtcTicks) / (double)TimeSpan.TicksPerMillisecond;
                    _lastUnwrapped += (long)Math.Round(gapMs / TimestampScaleMs);
                    _lastRawTime = raw;
                    _lastReadUtcTicks = readUtcTicks;
                    AppLog.Warn("CAN hardware timestamp counter restarted; continuing from the wall-clock gap ({0:F1} ms).", gapMs);
                    return _lastUnwrapped;
                }

                /// <summary>Backward steps up to one second are frame reordering, not a counter restart.</summary>
                private uint MaxReorderTicks => (uint)Math.Round(1000.0 / TimestampScaleMs);

                /// <summary>
                /// Refines the hardware-to-PC clock offset from a batch read at
                /// <paramref name="readUtcTicks"/>. Every frame is read some time after it was
                /// stamped, so (read time − hardware time of the newest frame) is an upper
                /// bound on the true offset; keeping the minimum converges on the batch with
                /// the least USB latency. RX thread only.
                /// </summary>
                public void ObserveWallClock(long newestUnwrappedTicks, long readUtcTicks)
                {
                    long candidate = readUtcTicks - ToUtcTicks(newestUnwrappedTicks);

                    if (!_haveWallOffset)
                    {
                        _haveWallOffset = true;
                        _wallOffsetUtcTicks = candidate;
                        _wallOffsetUpdatedAt = readUtcTicks;
                    }
                    else
                    {
                        long allowance = (readUtcTicks - _wallOffsetUpdatedAt) / DriftAllowanceDivisor;
                        if (allowance > 0)
                        {
                            _wallOffsetUtcTicks += allowance;
                            _wallOffsetUpdatedAt += allowance * DriftAllowanceDivisor;
                        }

                        if (candidate < _wallOffsetUtcTicks)
                            _wallOffsetUtcTicks = candidate;
                    }
                }

                /// <summary>Maps a hardware tick count onto the PC clock (local time). RX thread only.</summary>
                public DateTime ToWallTime(long unwrappedTicks) =>
                    new DateTime(_wallOffsetUtcTicks + ToUtcTicks(unwrappedTicks), DateTimeKind.Utc).ToLocalTime();

                private long ToUtcTicks(long unwrappedTicks) =>
                    (long)Math.Round(unwrappedTicks * TimestampScaleMs * TimeSpan.TicksPerMillisecond);
            }
        }
    }
}
