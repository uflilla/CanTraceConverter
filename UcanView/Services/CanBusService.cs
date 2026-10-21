using System;
using System.Collections.Generic;
using System.Threading;
using UcanView.Models;
using UcanDotNET;
using static UcanDotNET.USBcanServer;

namespace UcanView.Services
{
    public sealed class CanBusService : IDisposable
    {
        private UsbCanHardwareManager.AcquiredChannel _lease;
        private volatile bool _connected;
        private volatile bool _disposed;
        private SessionConfig _config;
        private CanBusStatus _busStatus = CanBusStatus.Ok;

        /// <summary>
        /// Serialises <see cref="Connect"/> with <see cref="Disconnect"/>: a connect runs off the
        /// UI thread, and a window closed meanwhile must not leave the channel it opened held.
        /// Never taken by the receive thread, which <see cref="Disconnect"/> may join.
        /// </summary>
        private readonly object _connectLock = new object();

        public event Action<CanMessageRecord> MessageReceived;
        public event Action<string> StatusChanged;
        public event Action Disconnected;
        public event Action<CanBusStatus> BusStatusChanged;

        public bool IsConnected
        {
            get
            {
                var lease = _lease;
                return _connected && lease != null && lease.IsConnected;
            }
        }

        public bool ListenOnly => _config?.ListenOnly ?? true;

        /// <summary>False while the controller is bus-off — frames would not reach the wire.</summary>
        public bool CanTransmit => IsConnected && !ListenOnly && !_busStatus.BlocksTransmit;

        public CanBusStatus BusStatus => _busStatus;

        public tUcanHardwareInfoEx HardwareInfo
        {
            get { var lease = _lease; return lease != null ? lease.HardwareInfo : default; }
        }

        public tUcanChannelInfo ChannelInfo
        {
            get { var lease = _lease; return lease != null ? lease.ChannelInfo : default; }
        }

        public SessionConfig Config => _config;
        public string LastError { get; private set; }

        public bool Connect(SessionConfig config)
        {
            lock (_connectLock)
                return ConnectLocked(config);
        }

        private bool ConnectLocked(SessionConfig config)
        {
            if (_disposed)
            {
                LastError = "The session is closing.";
                return false;
            }

            if (_connected)
                return true;

            LastError = null;
            _config = config;
            _busStatus = CanBusStatus.Ok;

            try
            {
                if (!CanHardwareAvailability.TryProbeDriver(out string driverError))
                {
                    LastError = driverError;
                    AppLog.Warn("CAN driver probe failed: {0}", driverError);
                    RaiseStatus(driverError);
                    return false;
                }

                if (!UsbCanHardwareManager.TryAcquire(
                        config,
                        OnLeaseMessage,
                        OnLeaseDisconnected,
                        OnLeaseBusStatus,
                        out var lease,
                        out string error))
                {
                    LastError = error ?? "Could not open USB-CAN channel.";
                    AppLog.Warn("CAN acquire failed: {0}", LastError);
                    RaiseStatus(LastError);
                    return false;
                }

                _lease = lease;
                _connected = true;

                RaiseStatus($"Connected — Device {lease.DeviceNumber}, CH{config.Channel}, {config.BaudRateKbps} kBit/s");
                AppLog.Info("Connected to USB-CAN device {0}, channel {1}, {2} kBit/s, listen-only={3}.",
                    lease.DeviceNumber, config.Channel, config.BaudRateKbps, config.ListenOnly);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Connection failed: {ex.Message}";
                AppLog.Error(ex, "CAN connection failed.");
                RaiseStatus(LastError);
                Disconnect();
                return false;
            }
        }

        private void OnLeaseMessage(CanMessageRecord record)
        {
            if (_connected)
                MessageReceived?.Invoke(record);
        }

        private void OnLeaseDisconnected()
        {
            if (!_connected)
                return;

            _connected = false;
            _busStatus = CanBusStatus.Ok;
            RaiseStatus("USB device disconnected");
            AppLog.Warn("CAN bus disconnected from shared hardware lease.");
            try { BusStatusChanged?.Invoke(CanBusStatus.Ok); } catch { /* ignore */ }
            try { Disconnected?.Invoke(); } catch { /* ignore subscriber errors */ }
        }

        private void OnLeaseBusStatus(CanBusStatus status)
        {
            _busStatus = status ?? CanBusStatus.Ok;
            try { BusStatusChanged?.Invoke(_busStatus); } catch { /* ignore */ }
        }

        public bool SendOnce(uint canId, byte dlc, byte[] data, bool extended, bool rtr)
        {
            // Read the lease once: Disconnect() can null it between a check and a use.
            var lease = _lease;
            if (lease == null || !_connected || !lease.IsConnected || ListenOnly)
                return false;

            return lease.SendOnce(canId, dlc, data, extended, rtr);
        }

        public bool SendPeriodicFirmware(int intervalMs, uint canId, byte dlc, byte[] data, bool extended)
        {
            var lease = _lease;
            if (lease == null || !_connected || !lease.IsConnected || ListenOnly)
                return false;

            return lease.SendPeriodicFirmware(intervalMs, canId, dlc, data, extended);
        }

        public void StopPeriodicFirmware()
        {
            var lease = _lease;
            lease?.StopPeriodicFirmware();
        }

        /// <summary>Resets the CAN controller — the documented recovery path from bus-off.</summary>
        public bool ResetController(out string error)
        {
            var lease = _lease;
            if (lease == null || !_connected)
            {
                error = "Not connected to CAN hardware.";
                return false;
            }

            if (!lease.ResetController(out error))
                return false;

            _busStatus = CanBusStatus.Ok;
            try { BusStatusChanged?.Invoke(CanBusStatus.Ok); } catch { /* ignore */ }
            RaiseStatus("CAN controller reset.");
            return true;
        }

        public void Disconnect()
        {
            // Mark disconnected first so any TX threads observing IsConnected stop immediately.
            _connected = false;

            lock (_connectLock)
                DisconnectLocked();
        }

        private void DisconnectLocked()
        {
            _connected = false;
            _busStatus = CanBusStatus.Ok;

            var lease = Interlocked.Exchange(ref _lease, null);
            if (lease != null)
            {
                try { lease.StopPeriodicFirmware(); } catch { /* ignore */ }
                UsbCanHardwareManager.Release(lease);
            }

            try { BusStatusChanged?.Invoke(CanBusStatus.Ok); } catch { /* ignore */ }
            RaiseStatus("Disconnected");
            AppLog.Info("CAN bus service disconnected.");
        }

        /// <summary>
        /// Application-wide hard stop: all modules, channels, and firmware cyclic TX.
        /// </summary>
        public static void ReleaseAllHardware()
        {
            UsbCanHardwareManager.ReleaseAll();
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

            // Opening hardware during a scan would take the module away from a live session
            // for the duration, so discovery stays passive whenever anything is connected.
            bool sessionsOpen = UsbCanHardwareManager.HasOpenModules;

            void AddDevice(tUcanHardwareInfoEx hw, bool isUsed)
            {
                var existing = found.Find(d => d.DeviceNumber == hw.m_bDeviceNr);
                if (existing != null)
                {
                    if (isUsed)
                        existing.InUse = true;
                    if (existing.ProductCode == 0)
                        existing.ProductCode = hw.m_dwProductCode;
                    return;
                }

                found.Add(new CanDeviceItem
                {
                    DeviceNumber = hw.m_bDeviceNr,
                    SerialNumber = hw.m_dwSerialNr,
                    ProductCode = hw.m_dwProductCode,
                    InUse = isUsed
                });
            }

            // Include devices already opened by this application.
            foreach (var open in UsbCanHardwareManager.SnapshotOpenDevices())
            {
                if (!found.Exists(d => d.DeviceNumber == open.DeviceNumber))
                    found.Add(open);
            }

            // The scan runs on a background thread while sessions open and close elsewhere;
            // the wrapper's enumeration state and plug-and-play count are not thread-safe.
            lock (UcanServerAccess.Lock)
            try
            {
                var servers = USBcanServer.EnumerateHardware(
                    0, 254,
                    (int index, bool isUsed, tUcanHardwareInfoEx hw, ref bool doInit, ref bool tryNext) =>
                    {
                        AddDevice(hw, isUsed);
                        // Initialize unused devices so cold-plug scans return full hardware
                        // info — but never while a monitor session holds a module.
                        doInit = !isUsed && !sessionsOpen;
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
                            AddDevice(hw, false);
                        }
                        catch
                        {
                            // Ignore individual device read failures.
                        }
                        finally
                        {
                            UsbCanHardwareManager.SafeReleaseServer(s);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warning = $"Device scan failed: {ex.Message}";
            }

            // Reliable fallback: EnumerateHardware often fails to report a connected
            // module until InitHardware has succeeded once. Probe the first available
            // device directly so idle hardware is detected on the first scan.
            if (found.Count == 0 && !sessionsOpen)
            {
                lock (UcanServerAccess.Lock)
                    ProbeFirstAvailableDevice(AddDevice);
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
                found.Sort((a, b) => a.DeviceNumber.CompareTo(b.DeviceNumber));
                result.Devices = found.ToArray();
            }

            return result;
        }

        private static void ProbeFirstAvailableDevice(Action<tUcanHardwareInfoEx, bool> addDevice)
        {
            USBcanServer probe = null;
            try
            {
                probe = UcanServerAccess.Create();
                int result = probe.InitHardware(USBCAN_ANY_MODULE);
                if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                    return;

                var hw = new tUcanHardwareInfoEx();
                var ch0 = new tUcanChannelInfo();
                var ch1 = new tUcanChannelInfo();
                probe.GetHardwareInfo(ref hw, ref ch0, ref ch1);
                addDevice(hw, false);
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Device probe failed.");
            }
            finally
            {
                UsbCanHardwareManager.SafeReleaseServer(probe);
            }
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
        }
    }

    public sealed class DeviceEnumerationResult
    {
        public CanDeviceItem[] Devices { get; set; } = Array.Empty<CanDeviceItem>();
        public string Warning { get; set; }
        public bool DriverAvailable { get; set; }
    }
}
