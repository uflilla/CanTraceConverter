using System;
using System.Collections.Generic;
using System.Threading;
using CanTraceConverter.Models;
using UcanDotNET;
using static UcanDotNET.USBcanServer;

namespace CanTraceConverter.Services
{
    public sealed class CanBusService : IDisposable
    {
        private USBcanServer _server;
        private Thread _rxThread;
        private volatile bool _connected;
        private volatile bool _disposed;
        private volatile bool _stopRx;
        private byte _channel;
        private SessionConfig _config;
        private tUcanHardwareInfoEx _hwInfo;
        private tUcanChannelInfo _channelInfo;
        private readonly AutoResetEvent _msgSignal = new AutoResetEvent(false);
        private bool _eventsHooked;

        public event Action<CanMessageRecord> MessageReceived;
        public event Action<string> StatusChanged;
        public event Action Disconnected;

        public bool IsConnected => _connected;
        public bool ListenOnly => _config?.ListenOnly ?? true;
        public tUcanHardwareInfoEx HardwareInfo => _hwInfo;
        public tUcanChannelInfo ChannelInfo => _channelInfo;
        public SessionConfig Config => _config;
        public string LastError { get; private set; }

        public bool Connect(SessionConfig config)
        {
            if (_connected)
                return true;

            LastError = null;
            _config = config;
            _channel = config.Channel;

            try
            {
                if (!CanHardwareAvailability.TryProbeDriver(out string driverError))
                {
                    LastError = driverError;
                    AppLog.Warn("CAN driver probe failed: {0}", driverError);
                    RaiseStatus(driverError);
                    return false;
                }

                _server = new USBcanServer();
                HookEvents();

                int result = _server.InitHardware(config.DeviceNumber);
                if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                {
                    LastError = DescribeError("InitHardware", result);
                    AppLog.Warn("InitHardware failed: {0}", LastError);
                    RaiseStatus(LastError);
                    UnhookEvents();
                    CleanupServer();
                    return false;
                }

                ReadHardwareInfo();

                byte mode = config.ListenOnly
                    ? (byte)tUcanMode.kUcanModeListenOnly
                    : (byte)(tUcanMode.kUcanModeNormal | tUcanMode.kUcanModeTxEcho);

                if (CheckIs_G4(_hwInfo))
                    mode |= (byte)tUcanMode.kUcanModeHighResTimer;

                int baudEx = CanMessageHelper.MapBaudRateKbps(config.BaudRateKbps, _hwInfo);
                short wBtr = (short)eUcanBaudrate.USBCAN_BAUD_USE_BTREX;

                result = _server.InitCan(
                    config.Channel,
                    wBtr,
                    baudEx,
                    USBCAN_AMR_ALL,
                    USBCAN_ACR_ALL,
                    mode,
                    (byte)eUcanOutputControl.USBCAN_OCR_DEFAULT);

                if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                {
                    LastError = DescribeError("InitCan", result);
                    AppLog.Warn("InitCan failed: {0}", LastError);
                    RaiseStatus(LastError);
                    SafeShutdown(config.Channel);
                    UnhookEvents();
                    CleanupServer();
                    return false;
                }

                ReadHardwareInfo();

                _connected = true;
                _stopRx = false;
                _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "CanRxThread" };
                _rxThread.Start();

                RaiseStatus($"Connected — Device {config.DeviceNumber}, CH{config.Channel}, {config.BaudRateKbps} kBit/s");
                AppLog.Info("Connected to USB-CAN device {0}, channel {1}, {2} kBit/s, listen-only={3}.",
                    config.DeviceNumber, config.Channel, config.BaudRateKbps, config.ListenOnly);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Connection failed: {ex.Message}";
                AppLog.Error(ex, "CAN connection failed.");
                RaiseStatus(LastError);
                UnhookEvents();
                CleanupServer();
                return false;
            }
        }

        private static string DescribeError(string operation, int code)
        {
            if (code == (int)eUcanReturn.USBCAN_ERR_DISCONECT)
                return $"{operation} failed: no USB-CAN device connected (0x{code:X}).";

            if (code == (int)eUcanReturn.USBCAN_ERR_HWINUSE)
                return $"{operation} failed: device/channel already in use by another application (0x{code:X}).";

            if (code == (int)eUcanReturn.USBCAN_ERR_ILLHW)
                return $"{operation} failed: hardware not found (0x{code:X}). Check device and drivers.";

            return $"{operation} failed (0x{code:X}).";
        }

        private void ReadHardwareInfo()
        {
            if (_server == null)
                return;

            var ch0 = new tUcanChannelInfo();
            var ch1 = new tUcanChannelInfo();
            _server.GetHardwareInfo(ref _hwInfo, ref ch0, ref ch1);
            _channelInfo = _config.Channel == 0 ? ch0 : ch1;
        }

        private void HookEvents()
        {
            if (_eventsHooked || _server == null)
                return;

            _server.CanMsgReceivedEvent += OnCanMsgReceived;
            USBcanServer.DisconnectEvent += OnDisconnect;
            _server.FatalDisconnectEvent += OnFatalDisconnect;
            _eventsHooked = true;
        }

        private void UnhookEvents()
        {
            if (!_eventsHooked)
                return;

            try
            {
                if (_server != null)
                    _server.CanMsgReceivedEvent -= OnCanMsgReceived;
                USBcanServer.DisconnectEvent -= OnDisconnect;
                if (_server != null)
                    _server.FatalDisconnectEvent -= OnFatalDisconnect;
            }
            catch
            {
                // Ignore errors while detaching native callbacks.
            }
            finally
            {
                _eventsHooked = false;
            }
        }

        private void OnCanMsgReceived(byte handle, byte channel)
        {
            if (_connected)
                _msgSignal.Set();
        }

        private void OnDisconnect()
        {
            HandleDisconnect("USB device disconnected");
        }

        private void OnFatalDisconnect(byte handle)
        {
            HandleDisconnect("Fatal USB disconnect");
        }

        private void HandleDisconnect(string message)
        {
            if (!_connected)
                return;

            _connected = false;
            _stopRx = true;
            _msgSignal.Set();
            RaiseStatus(message);
            AppLog.Warn("CAN bus disconnected: {0}", message);
            try { Disconnected?.Invoke(); } catch { /* ignore subscriber errors */ }
        }

        private void RxLoop()
        {
            var buffer = new tCanMsgStruct[64];

            while (!_stopRx && _connected)
            {
                try
                {
                    _msgSignal.WaitOne(50);

                    while (_connected && !_stopRx && _server != null)
                    {
                        byte readChannel = _channel;
                        int count = 0;
                        int result = _server.ReadCanMsg(ref readChannel, ref buffer, ref count);
                        if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL || count <= 0)
                            break;

                        var wallTime = DateTime.Now;
                        for (int i = 0; i < count; i++)
                        {
                            try
                            {
                                var record = CanMessageHelper.FromHardwareMessage(
                                    buffer[i], _hwInfo, _channelInfo, wallTime);
                                MessageReceived?.Invoke(record);
                            }
                            catch
                            {
                                // Skip malformed messages without stopping capture.
                            }
                        }
                    }
                }
                catch
                {
                    if (!_stopRx)
                        Thread.Sleep(10);
                }
            }
        }

        public bool SendOnce(uint canId, byte dlc, byte[] data, bool extended, bool rtr)
        {
            if (!_connected || ListenOnly || _server == null)
                return false;

            try
            {
                var msg = CanMessageHelper.ToHardwareMessage(canId, dlc, data, extended, rtr);
                var list = new[] { msg };
                int count = 0;
                int result = _server.WriteCanMsg(_channel, ref list, ref count);
                return result == (int)eUcanReturn.USBCAN_SUCCESSFUL && count > 0;
            }
            catch
            {
                return false;
            }
        }

        public bool SendPeriodicFirmware(int intervalMs, uint canId, byte dlc, byte[] data, bool extended)
        {
            if (!_connected || ListenOnly || _server == null || !CheckSupportCyclicMsg(_hwInfo))
                return false;

            try
            {
                var msg = CanMessageHelper.ToHardwareMessage(canId, dlc, data, extended, false);
                msg.m_dwTime = intervalMs;

                var list = new[] { msg };
                int result = _server.DefineCyclicCanMsg(_channel, ref list);
                if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                    return false;

                result = _server.EnableCyclicCanMsg(_channel, (int)eUcanCyclicFlags.USBCAN_CYCLIC_FLAG_START);
                return result == (int)eUcanReturn.USBCAN_SUCCESSFUL;
            }
            catch
            {
                return false;
            }
        }

        public void StopPeriodicFirmware()
        {
            if (_server == null || !_connected)
                return;

            try
            {
                _server.EnableCyclicCanMsg(_channel, (int)eUcanCyclicFlags.USBCAN_CYCLIC_FLAG_STOPP);
                var empty = new tCanMsgStruct[0];
                _server.DefineCyclicCanMsg(_channel, ref empty);
            }
            catch
            {
                // Ignore shutdown errors.
            }
        }

        public void Disconnect()
        {
            _connected = false;
            _stopRx = true;
            _msgSignal.Set();

            SafeShutdown(_channel);
            try { _rxThread?.Join(2000); } catch { /* ignore */ }
            UnhookEvents();
            CleanupServer();
            RaiseStatus("Disconnected");
            AppLog.Info("CAN bus service disconnected.");
        }

        private void SafeShutdown(byte channel)
        {
            if (_server == null)
                return;

            try
            {
                StopPeriodicFirmware();
                _server.Shutdown(channel, true);
            }
            catch
            {
                // Ignore native shutdown errors.
            }
        }

        private void CleanupServer()
        {
            _server = null;
        }

        private void RaiseStatus(string msg)
        {
            try { StatusChanged?.Invoke(msg); } catch { /* ignore subscriber errors */ }
        }

        public static DeviceEnumerationResult EnumerateDevices()
        {
            var result = new DeviceEnumerationResult();
            var found = new List<CanDeviceItem>();

            if (!CanHardwareAvailability.TryProbeDriver(out string driverError))
            {
                result.DriverAvailable = false;
                result.Warning = driverError;
                result.Devices = CreateFallbackDevices();
                return result;
            }

            result.DriverAvailable = true;

            try
            {
                void AddDevice(tUcanHardwareInfoEx hw)
                {
                    if (!found.Exists(d => d.DeviceNumber == hw.m_bDeviceNr))
                    {
                        found.Add(new CanDeviceItem
                        {
                            DeviceNumber = hw.m_bDeviceNr,
                            SerialNumber = hw.m_dwSerialNr,
                            ProductCode = hw.m_dwProductCode,
                            InUse = false
                        });
                    }
                }

                var servers = USBcanServer.EnumerateHardware(
                    0, 254,
                    (int index, bool isUsed, tUcanHardwareInfoEx hw, ref bool doInit, ref bool tryNext) =>
                    {
                        AddDevice(hw);
                        doInit = false;
                        tryNext = true;
                    },
                    true);

                if (servers != null)
                {
                    foreach (var s in servers)
                    {
                        if (s == null)
                            continue;

                        try
                        {
                            var hw = new tUcanHardwareInfoEx();
                            var ch0 = new tUcanChannelInfo();
                            var ch1 = new tUcanChannelInfo();
                            s.GetHardwareInfo(ref hw, ref ch0, ref ch1);
                            AddDevice(hw);
                        }
                        catch
                        {
                            // Ignore individual device read failures.
                        }
                        finally
                        {
                            try { s.Shutdown((byte)eUcanChannel.USBCAN_CHANNEL_ANY, true); }
                            catch { /* ignore */ }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warning = $"Device scan failed: {ex.Message}";
            }

            if (found.Count == 0)
            {
                result.Warning = string.IsNullOrWhiteSpace(result.Warning)
                    ? "No USB-CAN devices detected. Connect hardware or choose auto-detect."
                    : result.Warning;
                result.Devices = CreateFallbackDevices();
            }
            else
            {
                result.Devices = found.ToArray();
            }

            return result;
        }

        private static CanDeviceItem[] CreateFallbackDevices() =>
            new[]
            {
                new CanDeviceItem
                {
                    DeviceNumber = 255,
                    SerialNumber = 0,
                    Label = "First available device (auto-detect)"
                }
            };

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Disconnect();
            _msgSignal.Dispose();
        }
    }

    public sealed class DeviceEnumerationResult
    {
        public CanDeviceItem[] Devices { get; set; } = Array.Empty<CanDeviceItem>();
        public string Warning { get; set; }
        public bool DriverAvailable { get; set; }
    }
}
