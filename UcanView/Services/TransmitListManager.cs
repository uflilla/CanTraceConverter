using System;
using System.Collections.Generic;
using System.Globalization;
using UcanView.Controls;
using UcanView.Models;

namespace UcanView.Services
{
    public sealed class TransmitListManager : IDisposable
    {
        private const uint StandardIdMax = 0x7FF;
        private const uint ExtendedIdMax = 0x1FFFFFFF;

        private readonly Dictionary<Guid, PeriodicTransmitter> _cyclics = new Dictionary<Guid, PeriodicTransmitter>();
        private CanBusService _bus;

        /// <summary>Raised when a cyclic transmitter stops on its own (bus-off, TX failure).</summary>
        public event Action<Guid, string> CyclicFaulted;

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

            string idText = NormalizeIdText(row.CanIdHex);
            if (string.IsNullOrEmpty(idText) ||
                !uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out canId))
            {
                error = "Invalid CAN ID. Enter a hexadecimal identifier, for example 1A0.";
                return false;
            }

            // Range depends on the frame format, so an out-of-range ID is reported as the
            // input problem it is rather than as a generic transmit failure.
            uint max = extended ? ExtendedIdMax : StandardIdMax;
            if (canId > max)
            {
                error = extended
                    ? $"CAN ID {canId:X} is out of range. Extended identifiers stop at {ExtendedIdMax:X}."
                    : $"CAN ID {canId:X} is out of range. Standard identifiers stop at {StandardIdMax:X} — tick Ext for a 29-bit identifier.";
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

        private static string NormalizeIdText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string trimmed = text.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(2);
            if (trimmed.EndsWith("h", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 1);

            return trimmed.Trim();
        }

        public bool TrySendOnce(CanTransmitRow row, out string error)
        {
            error = null;
            var bus = _bus;
            if (bus == null || !bus.IsConnected)
            {
                error = "Not connected to CAN hardware.";
                return false;
            }

            if (bus.ListenOnly)
            {
                error = "Session is in listen-only mode.";
                return false;
            }

            if (bus.BusStatus.BlocksTransmit)
            {
                error = "Cannot transmit: " + bus.BusStatus.Message + ". Reset the CAN controller first.";
                return false;
            }

            if (!TryParse(row, out uint id, out byte dlc, out byte[] data, out bool ext, out error))
                return false;

            if (!bus.SendOnce(id, dlc, data, ext, false))
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

            var bus = _bus;
            if (bus == null || !bus.IsConnected)
            {
                error = "Not connected to CAN hardware.";
                return false;
            }

            if (bus.ListenOnly)
            {
                error = "Session is in listen-only mode.";
                return false;
            }

            if (bus.BusStatus.BlocksTransmit)
            {
                error = "Cannot transmit: " + bus.BusStatus.Message + ". Reset the CAN controller first.";
                return false;
            }

            if (!TryParse(row, out uint id, out byte dlc, out byte[] data, out bool ext, out error))
                return false;

            PeriodicTransmitter stale = null;
            lock (_cyclics)
            {
                if (_cyclics.TryGetValue(row.Id, out var existing))
                {
                    if (existing.IsRunning)
                    {
                        row.IsCyclicRunning = true;
                        return true;
                    }

                    // A transmitter that stopped on its own (bus-off, disconnect) used to stay
                    // registered, so Send reported "running" while nothing was transmitted.
                    _cyclics.Remove(row.Id);
                    stale = existing;
                }

                var rowId = row.Id;
                var transmitter = new PeriodicTransmitter();
                transmitter.Faulted += reason => OnTransmitterFaulted(rowId, transmitter, reason);
                transmitter.Start(bus, row.IntervalMs, id, dlc, data, ext);
                _cyclics[rowId] = transmitter;
            }

            StopSafely(stale);
            row.IsCyclicRunning = true;
            return true;
        }

        /// <summary>Runs on the transmit thread as it exits.</summary>
        private void OnTransmitterFaulted(Guid rowId, PeriodicTransmitter transmitter, string reason)
        {
            lock (_cyclics)
            {
                if (_cyclics.TryGetValue(rowId, out var current) && ReferenceEquals(current, transmitter))
                    _cyclics.Remove(rowId);
            }

            CyclicFaulted?.Invoke(rowId, reason);
        }

        public void StopCyclic(CanTransmitRow row)
        {
            if (row == null)
                return;

            PeriodicTransmitter transmitter = null;
            lock (_cyclics)
            {
                if (_cyclics.TryGetValue(row.Id, out transmitter))
                    _cyclics.Remove(row.Id);
            }

            // Stop outside the lock: Stop() joins the transmit thread.
            StopSafely(transmitter);
            row.IsCyclicRunning = false;
        }

        public void StopAll()
        {
            PeriodicTransmitter[] transmitters;
            lock (_cyclics)
            {
                transmitters = new PeriodicTransmitter[_cyclics.Count];
                _cyclics.Values.CopyTo(transmitters, 0);
                _cyclics.Clear();
            }

            // Signal every transmitter first, then join — so N transmitters cost one
            // timeout in total rather than one each.
            foreach (var transmitter in transmitters)
            {
                try { transmitter.RequestStop(); } catch { /* ignore */ }
            }

            foreach (var transmitter in transmitters)
                StopSafely(transmitter);

            // Always clear device-side cyclic TX if it was started.
            try { _bus?.StopPeriodicFirmware(); } catch { /* ignore */ }
        }

        private static void StopSafely(PeriodicTransmitter transmitter)
        {
            if (transmitter == null)
                return;

            try
            {
                transmitter.Stop();
                transmitter.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Stopping a cyclic transmitter failed.");
            }
        }

        public void Dispose()
        {
            CyclicFaulted = null;
            StopAll();
            _bus = null;
        }
    }
}
