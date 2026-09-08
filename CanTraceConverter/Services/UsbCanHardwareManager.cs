using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CanTraceConverter.Models;
using UcanDotNET;
using static UcanDotNET.USBcanServer;

namespace CanTraceConverter.Services
{
    /// <summary>
    /// Shares one <see cref="USBcanServer"/> / InitHardware per physical module so
    /// channel 0 and channel 1 can be opened from separate monitor windows.
    /// </summary>
    internal static class UsbCanHardwareManager
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<byte, SharedModule> Modules = new Dictionary<byte, SharedModule>();

        public static bool TryAcquire(
            SessionConfig config,
            Action<CanMessageRecord> onMessage,
            Action onDisconnected,
            Action<string> onCanError,
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

            lock (Gate)
            {
                SharedModule module = FindExistingModule(config.DeviceNumber, config.Channel);

                if (module == null)
                {
                    if (!TryCreateModule(config.DeviceNumber, out module, out error))
                        return false;
                }

                if (module.IsChannelOpen(config.Channel))
                {
                    error = $"Channel {config.Channel} on device {module.DeviceNumber} is already open in another monitor window.";
                    return false;
                }

                if (!module.TryOpenChannel(config, onMessage, onDisconnected, onCanError, out error))
                {
                    if (!module.HasOpenChannels)
                        RemoveModule_NoLock(module);
                    return false;
                }

                acquired = new AcquiredChannel(module, config.Channel);
                return true;
            }
        }

        public static void Release(AcquiredChannel acquired)
        {
            if (acquired == null)
                return;

            lock (Gate)
            {
                var module = acquired.Module;
                if (module == null)
                    return;

                module.CloseChannel(acquired.Channel);
                acquired.Detach();

                if (!module.HasOpenChannels)
                    RemoveModule_NoLock(module);
            }
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
                    module.StopAllActivity();
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

            var server = new USBcanServer();
            int result = server.InitHardware(requestedDeviceNumber);
            if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
            {
                error = DescribeHwError("InitHardware", result);
                try { server.Shutdown((byte)eUcanChannel.USBCAN_CHANNEL_ANY, true); } catch { /* ignore */ }
                return false;
            }

            var hw = new tUcanHardwareInfoEx();
            var ch0 = new tUcanChannelInfo();
            var ch1 = new tUcanChannelInfo();
            server.GetHardwareInfo(ref hw, ref ch0, ref ch1);

            byte resolved = hw.m_bDeviceNr;
            if (Modules.ContainsKey(resolved))
            {
                error = $"Device {resolved} is already open in this application.";
                try { server.Shutdown((byte)eUcanChannel.USBCAN_CHANNEL_ANY, true); } catch { /* ignore */ }
                return false;
            }

            module = new SharedModule(server, resolved, hw);
            Modules[resolved] = module;
            return true;
        }

        private static void RemoveModule_NoLock(SharedModule module)
        {
            if (module == null)
                return;

            Modules.Remove(module.DeviceNumber);
            module.DisposeHardware();
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

            public bool SendOnce(uint canId, byte dlc, byte[] data, bool extended, bool rtr) =>
                Module != null && Module.SendOnce(Channel, canId, dlc, data, extended, rtr);

            public bool SendPeriodicFirmware(int intervalMs, uint canId, byte dlc, byte[] data, bool extended) =>
                Module != null && Module.SendPeriodicFirmware(Channel, intervalMs, canId, dlc, data, extended);

            public void StopPeriodicFirmware() => Module?.StopPeriodicFirmware(Channel);

            internal void Detach() => Module = null;
        }

        internal sealed class SharedModule
        {
            private const long StatusPollIntervalMs = 300;

            private readonly object _sync = new object();
            private readonly ChannelSlot[] _channels = { new ChannelSlot(), new ChannelSlot() };
            private readonly AutoResetEvent _msgSignal = new AutoResetEvent(false);
            private readonly bool[] _statusPending = new bool[2];
            private readonly System.Diagnostics.Stopwatch _statusClock = System.Diagnostics.Stopwatch.StartNew();
            private long _lastStatusPollMs;
            private USBcanServer _server;
            private Thread _rxThread;
            private volatile bool _rxRunning;
            private bool _eventsHooked;
            private bool _disposed;

            public byte DeviceNumber { get; }
            public tUcanHardwareInfoEx HardwareInfo { get; private set; }
            public bool HasOpenChannels => _channels.Any(c => c.Open);

            public SharedModule(USBcanServer server, byte deviceNumber, tUcanHardwareInfoEx hwInfo)
            {
                _server = server;
                DeviceNumber = deviceNumber;
                HardwareInfo = hwInfo;
                HookEvents();
            }

            public bool IsChannelOpen(byte channel) =>
                channel < _channels.Length && _channels[channel].Open;

            public tUcanChannelInfo GetChannelInfo(byte channel) =>
                channel < _channels.Length ? _channels[channel].Info : default;

            public bool TryOpenChannel(
                SessionConfig config,
                Action<CanMessageRecord> onMessage,
                Action onDisconnected,
                Action<string> onCanError,
                out string error)
            {
                error = null;
                byte channel = config.Channel;

                byte mode = config.ListenOnly
                    ? (byte)tUcanMode.kUcanModeListenOnly
                    : (byte)(tUcanMode.kUcanModeNormal | tUcanMode.kUcanModeTxEcho);

                if (CheckIs_G4(HardwareInfo))
                    mode |= (byte)tUcanMode.kUcanModeHighResTimer;

                int baudEx = CanMessageHelper.MapBaudRateKbps(config.BaudRateKbps, HardwareInfo);
                short wBtr = (short)eUcanBaudrate.USBCAN_BAUD_USE_BTREX;

                int result = _server.InitCan(
                    channel,
                    wBtr,
                    baudEx,
                    USBCAN_AMR_ALL,
                    USBCAN_ACR_ALL,
                    mode,
                    (byte)eUcanOutputControl.USBCAN_OCR_DEFAULT);

                if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                {
                    error = DescribeHwError("InitCan", result);
                    return false;
                }

                RefreshHardwareInfo();

                var slot = _channels[channel];
                slot.Open = true;
                slot.ListenOnly = config.ListenOnly;
                slot.OnMessage = onMessage;
                slot.OnDisconnected = onDisconnected;
                slot.OnCanError = onCanError;
                slot.Info = channel == 0
                    ? ReadChannelInfo(0)
                    : ReadChannelInfo(1);

                EnsureRxThread();
                return true;
            }

            public void CloseChannel(byte channel)
            {
                if (channel >= _channels.Length)
                    return;

                var slot = _channels[channel];
                if (!slot.Open)
                    return;

                try { StopPeriodicFirmware(channel, force: true); } catch { /* ignore */ }

                try
                {
                    // Keep hardware open if the other channel is still in use.
                    bool shutHardware = !_channels.Where((c, i) => i != channel && c.Open).Any();
                    _server?.Shutdown(channel, shutHardware);
                }
                catch
                {
                    // Ignore native shutdown errors.
                }

                slot.Open = false;
                slot.OnMessage = null;
                slot.OnDisconnected = null;
                slot.OnCanError = null;
                _statusPending[channel] = false;

                if (!HasOpenChannels)
                    StopRxThread();
            }

            /// <summary>
            /// Stop software-visible activity and firmware cyclic TX on every channel
            /// before hardware teardown.
            /// </summary>
            public void StopAllActivity()
            {
                for (byte channel = 0; channel < _channels.Length; channel++)
                {
                    try { StopPeriodicFirmware(channel, force: true); } catch { /* ignore */ }

                    lock (_sync)
                    {
                        _channels[channel].Open = false;
                        _channels[channel].OnMessage = null;
                        _channels[channel].OnDisconnected = null;
                        _channels[channel].OnCanError = null;
                    }
                }

                _rxRunning = false;
                try { _msgSignal.Set(); } catch { /* ignore */ }
            }

            public void DisposeHardware()
            {
                if (_disposed)
                    return;
                _disposed = true;

                try { StopAllActivity(); } catch { /* ignore */ }
                StopRxThread();
                UnhookEvents();

                try
                {
                    // Final hard close — frees the module for other applications.
                    _server?.Shutdown((byte)eUcanChannel.USBCAN_CHANNEL_ANY, true);
                }
                catch
                {
                    // Ignore.
                }

                _server = null;
                try { _msgSignal.Dispose(); } catch { /* ignore */ }
            }

            public bool SendOnce(byte channel, uint canId, byte dlc, byte[] data, bool extended, bool rtr)
            {
                if (_server == null || !IsChannelOpen(channel) || _channels[channel].ListenOnly)
                    return false;

                try
                {
                    var msg = CanMessageHelper.ToHardwareMessage(canId, dlc, data, extended, rtr);
                    var list = new[] { msg };
                    int count = 0;
                    int result = _server.WriteCanMsg(channel, ref list, ref count);
                    return result == (int)eUcanReturn.USBCAN_SUCCESSFUL && count > 0;
                }
                catch
                {
                    return false;
                }
            }

            public bool SendPeriodicFirmware(byte channel, int intervalMs, uint canId, byte dlc, byte[] data, bool extended)
            {
                if (_server == null || !IsChannelOpen(channel) || _channels[channel].ListenOnly || !CheckSupportCyclicMsg(HardwareInfo))
                    return false;

                try
                {
                    var msg = CanMessageHelper.ToHardwareMessage(canId, dlc, data, extended, false);
                    msg.m_dwTime = intervalMs;
                    var list = new[] { msg };
                    int result = _server.DefineCyclicCanMsg(channel, ref list);
                    if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                        return false;

                    result = _server.EnableCyclicCanMsg(channel, (int)eUcanCyclicFlags.USBCAN_CYCLIC_FLAG_START);
                    if (result == (int)eUcanReturn.USBCAN_SUCCESSFUL)
                        _channels[channel].FirmwareCyclicActive = true;
                    return result == (int)eUcanReturn.USBCAN_SUCCESSFUL;
                }
                catch
                {
                    return false;
                }
            }

            public void StopPeriodicFirmware(byte channel) => StopPeriodicFirmware(channel, force: false);

            public void StopPeriodicFirmware(byte channel, bool force)
            {
                if (_server == null || channel >= _channels.Length)
                    return;

                if (!force && !IsChannelOpen(channel) && !_channels[channel].FirmwareCyclicActive)
                    return;

                try
                {
                    _server.EnableCyclicCanMsg(channel, (int)eUcanCyclicFlags.USBCAN_CYCLIC_FLAG_STOPP);
                    var empty = new tCanMsgStruct[0];
                    _server.DefineCyclicCanMsg(channel, ref empty);
                }
                catch
                {
                    // Ignore.
                }
                finally
                {
                    _channels[channel].FirmwareCyclicActive = false;
                }
            }

            private void EnsureRxThread()
            {
                if (_rxRunning)
                    return;

                _rxRunning = true;
                _rxThread = new Thread(RxLoop)
                {
                    IsBackground = true,
                    Name = $"CanRx-Dev{DeviceNumber}"
                };
                _rxThread.Start();
            }

            private void StopRxThread()
            {
                _rxRunning = false;
                try { _msgSignal.Set(); } catch { /* ignore */ }

                var thread = _rxThread;
                if (thread != null && thread != Thread.CurrentThread)
                {
                    try { thread.Join(2000); } catch { /* ignore */ }
                }

                if (thread == null || thread != Thread.CurrentThread)
                    _rxThread = null;
            }

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
                catch
                {
                    // Ignore detach errors.
                }
                finally
                {
                    _eventsHooked = false;
                }
            }

            private void OnCanMsgReceived(byte deviceNr, byte channel)
            {
                if (_rxRunning)
                    _msgSignal.Set();
            }

            private void OnStatusEvent(byte deviceNr, byte channel)
            {
                if (channel < _statusPending.Length)
                    _statusPending[channel] = true;
            }

            private void OnDisconnect()
            {
                // Static event — ignore if this module's hardware is still reachable
                // (another USB-CAN device may have been unplugged).
                if (!HasOpenChannels || _server == null)
                    return;

                try
                {
                    var hw = HardwareInfo;
                    var ch0 = new tUcanChannelInfo();
                    var ch1 = new tUcanChannelInfo();
                    int result = _server.GetHardwareInfo(ref hw, ref ch0, ref ch1);
                    if (result == (int)eUcanReturn.USBCAN_SUCCESSFUL)
                        return;
                }
                catch
                {
                    // Fall through to disconnect handling.
                }

                HandleDisconnect("USB device disconnected");
            }

            private void OnFatalDisconnect(byte deviceNr)
            {
                if (deviceNr != DeviceNumber)
                    return;

                HandleDisconnect("Fatal USB disconnect");
            }

            private void HandleDisconnect(string message)
            {
                Action[] callbacks;
                lock (_sync)
                {
                    callbacks = _channels
                        .Where(c => c.Open && c.OnDisconnected != null)
                        .Select(c => c.OnDisconnected)
                        .ToArray();

                    foreach (var slot in _channels)
                    {
                        slot.Open = false;
                        slot.OnMessage = null;
                        slot.OnCanError = null;
                        slot.OnDisconnected = null;
                    }
                }

                _rxRunning = false;
                try { _msgSignal.Set(); } catch { /* ignore */ }

                lock (Gate)
                {
                    if (Modules.TryGetValue(DeviceNumber, out var current) && ReferenceEquals(current, this))
                        Modules.Remove(DeviceNumber);
                }

                foreach (var cb in callbacks)
                {
                    try { cb(); } catch { /* ignore */ }
                }

                AppLog.Warn("Shared USB-CAN module {0} disconnected: {1}", DeviceNumber, message);
                DisposeHardware();
            }

            private void RxLoop()
            {
                var buffer = new tCanMsgStruct[64];

                while (_rxRunning)
                {
                    try
                    {
                        _msgSignal.WaitOne(50);
                        DrainReceiveQueue(buffer);
                        PollPendingStatus();
                    }
                    catch
                    {
                        if (_rxRunning)
                            Thread.Sleep(10);
                    }
                }
            }

            private void DrainReceiveQueue(tCanMsgStruct[] buffer)
            {
                if (_server == null)
                    return;

                for (byte channel = 0; channel < 2; channel++)
                {
                    if (!_channels[channel].Open)
                        continue;

                    while (_rxRunning && _server != null)
                    {
                        byte readChannel = channel;
                        int count = 0;
                        int result = _server.ReadCanMsg(ref readChannel, ref buffer, ref count);
                        if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL || count <= 0)
                            break;

                        var wallTime = DateTime.Now;
                        var handler = _channels[channel].OnMessage;
                        if (handler == null)
                            continue;

                        for (int i = 0; i < count; i++)
                        {
                            try
                            {
                                var record = CanMessageHelper.FromHardwareMessage(
                                    buffer[i], HardwareInfo, _channels[channel].Info, wallTime);
                                handler.Invoke(record);
                            }
                            catch
                            {
                                // Skip malformed messages.
                            }
                        }
                    }
                }
            }

            private void PollPendingStatus()
            {
                if (_server == null)
                    return;

                // Throttle GetStatus (a blocking USB round-trip). Without this, a busy or
                // erroring bus fires StatusEvent continuously and starves ReadCanMsg on the
                // RX thread, overflowing the driver FIFO and corrupting RX timing/period.
                long now = _statusClock.ElapsedMilliseconds;
                if (now - _lastStatusPollMs < StatusPollIntervalMs)
                    return;
                _lastStatusPollMs = now;

                for (byte channel = 0; channel < 2; channel++)
                {
                    if (!_statusPending[channel] || !_channels[channel].Open)
                        continue;

                    _statusPending[channel] = false;

                    try
                    {
                        var status = new tStatusStruct();
                        int result = _server.GetStatus(channel, ref status);
                        if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                            continue;

                        string message = GetCanStatusMessage(status.m_wCanStatus);
                        bool isOk = status.m_wCanStatus == (short)eUcanCanStatus.USBCAN_CANERR_OK
                                    || string.IsNullOrWhiteSpace(message)
                                    || message.IndexOf("no error", StringComparison.OrdinalIgnoreCase) >= 0;

                        var handler = _channels[channel].OnCanError;
                        handler?.Invoke(isOk ? null : message);
                    }
                    catch
                    {
                        // Ignore status read failures.
                    }
                }
            }

            private void RefreshHardwareInfo()
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

            private tUcanChannelInfo ReadChannelInfo(byte channel)
            {
                RefreshHardwareInfo();
                return _channels[channel].Info;
            }

            private sealed class ChannelSlot
            {
                public bool Open;
                public bool ListenOnly = true;
                public bool FirmwareCyclicActive;
                public tUcanChannelInfo Info;
                public Action<CanMessageRecord> OnMessage;
                public Action OnDisconnected;
                public Action<string> OnCanError;
            }
        }
    }
}
