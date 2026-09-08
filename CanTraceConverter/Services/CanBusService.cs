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
        private UsbCanHardwareManager.AcquiredChannel _lease;
        private volatile bool _connected;
        private volatile bool _disposed;
        private SessionConfig _config;

        public event Action<CanMessageRecord> MessageReceived;
        public event Action<string> StatusChanged;
        public event Action Disconnected;
        public event Action<string> CanErrorStatusChanged;

        public bool IsConnected => _connected && _lease != null && _lease.IsConnected;
        public bool ListenOnly => _config?.ListenOnly ?? true;
        public tUcanHardwareInfoEx HardwareInfo => _lease != null ? _lease.HardwareInfo : default;
        public tUcanChannelInfo ChannelInfo => _lease != null ? _lease.ChannelInfo : default;
        public SessionConfig Config => _config;
        public string LastError { get; private set; }

        public bool Connect(SessionConfig config)
        {
            if (_connected)
                return true;

            LastError = null;
            _config = config;

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
                        OnLeaseCanError,
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
            RaiseStatus("USB device disconnected");
            AppLog.Warn("CAN bus disconnected from shared hardware lease.");
            try { CanErrorStatusChanged?.Invoke(null); } catch { /* ignore */ }
            try { Disconnected?.Invoke(); } catch { /* ignore subscriber errors */ }
        }

        private void OnLeaseCanError(string message)
        {
            try { CanErrorStatusChanged?.Invoke(message); } catch { /* ignore */ }
        }

        public bool SendOnce(uint canId, byte dlc, byte[] data, bool extended, bool rtr)
        {
            if (!IsConnected || ListenOnly || _lease == null)
                return false;

            return _lease.SendOnce(canId, dlc, data, extended, rtr);
        }

        public bool SendPeriodicFirmware(int intervalMs, uint canId, byte dlc, byte[] data, bool extended)
        {
            if (!IsConnected || ListenOnly || _lease == null)
                return false;

            return _lease.SendPeriodicFirmware(intervalMs, canId, dlc, data, extended);
        }

        public void StopPeriodicFirmware()
        {
            _lease?.StopPeriodicFirmware();
        }

        public void Disconnect()
        {
            // Mark disconnected first so any TX threads observing IsConnected stop immediately.
            _connected = false;

            var lease = Interlocked.Exchange(ref _lease, null);
            if (lease != null)
            {
                try { lease.StopPeriodicFirmware(); } catch { /* ignore */ }
                UsbCanHardwareManager.Release(lease);
            }

            try { CanErrorStatusChanged?.Invoke(null); } catch { /* ignore */ }
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

            void AddDevice(tUcanHardwareInfoEx hw, bool isUsed)
            {
                if (found.Exists(d => d.DeviceNumber == hw.m_bDeviceNr))
                {
                    var existing = found.Find(d => d.DeviceNumber == hw.m_bDeviceNr);
                    if (isUsed)
                        existing.InUse = true;
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

            try
            {
                var servers = USBcanServer.EnumerateHardware(
                    0, 254,
                    (int index, bool isUsed, tUcanHardwareInfoEx hw, ref bool doInit, ref bool tryNext) =>
                    {
                        AddDevice(hw, isUsed);
                        // Initialize unused devices so cold-plug scans return full hardware info.
                        doInit = !isUsed;
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

            // Reliable fallback: EnumerateHardware often fails to report a connected
            // module until InitHardware has succeeded once. Probe the first available
            // device directly so idle hardware is detected on the first scan.
            if (found.Count == 0)
                ProbeFirstAvailableDevice(AddDevice);

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
            bool initialized = false;
            try
            {
                probe = new USBcanServer();
                int result = probe.InitHardware(USBCAN_ANY_MODULE);
                if (result != (int)eUcanReturn.USBCAN_SUCCESSFUL)
                    return;

                initialized = true;
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
                if (probe != null && initialized)
                {
                    try { probe.Shutdown((byte)eUcanChannel.USBCAN_CHANNEL_ANY, true); }
                    catch { /* ignore */ }
                }
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
