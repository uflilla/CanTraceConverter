using System;
using System.Collections.Generic;
using System.Globalization;
using CanTraceConverter.Controls;
using CanTraceConverter.Models;

namespace CanTraceConverter.Services
{
    public sealed class TransmitListManager : IDisposable
    {
        private readonly Dictionary<Guid, PeriodicTransmitter> _cyclics = new Dictionary<Guid, PeriodicTransmitter>();
        private CanBusService _bus;

        public void AttachBus(CanBusService bus) => _bus = bus;

        public bool TryParse(CanTransmitRow row, out uint canId, out byte dlc, out byte[] data, out bool extended, out string error)
        {
            canId = 0;
            dlc = row?.Dlc ?? 8;
            data = new byte[8];
            extended = row?.Extended ?? false;
            error = null;

            if (row == null)
            {
                error = "Invalid transmit row.";
                return false;
            }

            if (dlc > 8)
            {
                error = "DLC must be between 0 and 8.";
                return false;
            }

            string idText = row.CanIdHex?.Trim().Replace("h", "").Replace("0x", "");
            if (string.IsNullOrEmpty(idText) ||
                !uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out canId))
            {
                error = "Invalid CAN ID.";
                return false;
            }

            if (!HexByteFormatter.TryParseBytes(row.DataHex, dlc, out data, out error))
                return false;

            if (row.IntervalMs < 1)
            {
                error = "Interval must be at least 1 ms.";
                return false;
            }

            return true;
        }

        public bool TrySendOnce(CanTransmitRow row, out string error)
        {
            error = null;
            if (_bus == null || !_bus.IsConnected)
            {
                error = "Not connected to CAN hardware.";
                return false;
            }

            if (!TryParse(row, out uint id, out byte dlc, out byte[] data, out bool ext, out error))
                return false;

            if (!_bus.SendOnce(id, dlc, data, ext, false))
            {
                error = "Failed to send message.";
                return false;
            }

            return true;
        }

        public bool TryStartCyclic(CanTransmitRow row, out string error)
        {
            error = null;
            if (row == null)
                return false;

            if (row.IsCyclicRunning)
                return true;

            if (_bus == null || !_bus.IsConnected)
            {
                error = "Not connected to CAN hardware.";
                return false;
            }

            if (!TryParse(row, out uint id, out byte dlc, out byte[] data, out bool ext, out error))
                return false;

            lock (_cyclics)
            {
                if (_cyclics.ContainsKey(row.Id))
                {
                    row.IsCyclicRunning = true;
                    return true;
                }

                var transmitter = new PeriodicTransmitter();
                transmitter.Start(_bus, row.IntervalMs, id, dlc, data, ext);
                _cyclics[row.Id] = transmitter;
            }

            row.IsCyclicRunning = true;
            return true;
        }

        public void StopCyclic(CanTransmitRow row)
        {
            if (row == null)
                return;

            lock (_cyclics)
            {
                if (_cyclics.TryGetValue(row.Id, out var transmitter))
                {
                    transmitter.Stop();
                    transmitter.Dispose();
                    _cyclics.Remove(row.Id);
                }
            }

            row.IsCyclicRunning = false;
        }

        public void StopAll()
        {
            lock (_cyclics)
            {
                foreach (var transmitter in _cyclics.Values)
                {
                    try
                    {
                        transmitter.Stop();
                        transmitter.Dispose();
                    }
                    catch
                    {
                        // Ignore shutdown errors.
                    }
                }
                _cyclics.Clear();
            }

            _bus?.StopPeriodicFirmware();
        }

        public void SetAllRunningState(IEnumerable<CanTransmitRow> rows, bool running)
        {
            if (rows == null)
                return;

            foreach (var row in rows)
                row.IsCyclicRunning = running;
        }

        public void Dispose()
        {
            StopAll();
            _bus = null;
        }
    }
}
