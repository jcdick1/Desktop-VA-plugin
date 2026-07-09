using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeskAgentVA
{
    /// <summary>
    /// Discovers Arduino boards across all serial ports, keeps a live registry
    /// keyed by board ID, and exposes LED control. Button transitions from every
    /// board are funnelled onto one background dispatch thread so that VoiceAttack
    /// work never blocks serial reading and events stay ordered.
    /// </summary>
    internal sealed class SerialManager
    {
        private readonly ConcurrentDictionary<string, DeviceLink> _links =
            new ConcurrentDictionary<string, DeviceLink>(StringComparer.OrdinalIgnoreCase);

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _scanTask;

        private readonly BlockingCollection<ButtonEvent> _events =
            new BlockingCollection<ButtonEvent>(new ConcurrentQueue<ButtonEvent>());
        private Thread _dispatchThread;

        public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>(deviceId, buttonNumber 1-based, isOn) delivered on the dispatch thread.</summary>
        public event Action<int, int, bool> ButtonChanged;

        /// <summary>Optional logging sink, e.g. VoiceAttack's log.</summary>
        public Action<string> Log { get; set; }

        public void Start()
        {
            _dispatchThread = new Thread(DispatchLoop)
            {
                IsBackground = true,
                Name = "DeskAgentVA.Dispatch"
            };
            _dispatchThread.Start();

            _scanTask = Task.Run(ScanLoopAsync);
        }

        public void Stop()
        {
            _cts.Cancel();
            _events.CompleteAdding();

            foreach (var link in _links.Values.ToList())
            {
                try { link.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            }
            _links.Clear();
        }

        private async Task ScanLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { ScanOnce(); }
                catch (Exception ex) { Log?.Invoke($"Port scan error: {ex.Message}"); }

                try { await Task.Delay(ScanInterval, _cts.Token).ConfigureAwait(false); }
                catch { }
            }
        }

        /// <summary>Force an immediate rescan (e.g. after plugging a board in).</summary>
        public void Rescan()
        {
            try { ScanOnce(); }
            catch (Exception ex) { Log?.Invoke($"Rescan error: {ex.Message}"); }
        }

        private void ScanOnce()
        {
            string[] ports;
            try { ports = SerialPort.GetPortNames(); }
            catch { return; }

            // Add links for newly appeared ports.
            foreach (var port in ports)
            {
                if (_links.ContainsKey(port)) continue;

                var link = new DeviceLink(port);
                link.ButtonChanged += OnLinkButtonChanged;
                link.ConnectionChanged += OnLinkConnectionChanged;

                if (_links.TryAdd(port, link))
                {
                    link.Start();
                    Log?.Invoke($"Opened {port}, listening for a board...");
                }
            }

            // Remove links for ports that have disappeared.
            foreach (var kvp in _links.ToArray())
            {
                if (ports.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase)) continue;

                if (_links.TryRemove(kvp.Key, out var gone))
                {
                    gone.ButtonChanged -= OnLinkButtonChanged;
                    gone.ConnectionChanged -= OnLinkConnectionChanged;
                    try { gone.StopAsync(); } catch { }
                    Log?.Invoke($"Port {kvp.Key} removed.");
                }
            }
        }

        private void OnLinkConnectionChanged(DeviceLink link)
        {
            if (link.Connected)
                Log?.Invoke($"Device {link.DeviceId} connected on {link.PortName}.");
            else
                Log?.Invoke($"Device on {link.PortName} disconnected.");
        }

        private void OnLinkButtonChanged(int deviceId, int button, bool isOn)
        {
            if (_events.IsAddingCompleted) return;
            try { _events.Add(new ButtonEvent(deviceId, button, isOn)); }
            catch { }
        }

        private void DispatchLoop()
        {
            try
            {
                foreach (var ev in _events.GetConsumingEnumerable())
                {
                    try { ButtonChanged?.Invoke(ev.DeviceId, ev.Button, ev.IsOn); }
                    catch (Exception ex) { Log?.Invoke($"Button handler error: {ex.Message}"); }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // ---- LED control -----------------------------------------------------

        private DeviceLink FindDevice(int deviceId) =>
            _links.Values.FirstOrDefault(l => l.Connected && l.DeviceId == deviceId);

        public bool IsConnected(int deviceId) => FindDevice(deviceId) != null;

        public IEnumerable<int> ConnectedDeviceIds =>
            _links.Values.Where(l => l.Connected).Select(l => l.DeviceId).Distinct().OrderBy(x => x);

        /// <summary>Turn a single LED on/off/toggle. Returns false if the device isn't present.</summary>
        public bool SetLed(int deviceId, int ledNumber, char action, out string message)
        {
            var link = FindDevice(deviceId);
            if (link == null)
            {
                message = $"Device {deviceId} not connected";
                return false;
            }

            try
            {
                link.Send(Protocol.BuildLedCommand(action, ledNumber));
                if (action == Protocol.ActionOn) link.ShadowLed(ledNumber, true);
                else if (action == Protocol.ActionOff) link.ShadowLed(ledNumber, false);
                else if (action == Protocol.ActionToggle) link.ShadowLed(ledNumber, !link.IsLedOn(ledNumber));

                message = "OK";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public bool SetMask(int deviceId, int mask24, out string message)
        {
            var link = FindDevice(deviceId);
            if (link == null)
            {
                message = $"Device {deviceId} not connected";
                return false;
            }

            try
            {
                link.Send(Protocol.BuildMaskCommand(Protocol.ActionSet, mask24));
                message = "OK";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public bool AllOff(int deviceId, out string message) => SetMask(deviceId, 0, out message);

        public bool TryGetLedState(int deviceId, int ledNumber, out bool isOn, out string message)
        {
            isOn = false;
            var link = FindDevice(deviceId);
            if (link == null)
            {
                message = $"Device {deviceId} not connected";
                return false;
            }

            isOn = link.IsLedOn(ledNumber);
            message = "OK";
            return true;
        }

        private readonly struct ButtonEvent
        {
            public readonly int DeviceId;
            public readonly int Button;
            public readonly bool IsOn;
            public ButtonEvent(int deviceId, int button, bool isOn)
            {
                DeviceId = deviceId;
                Button = button;
                IsOn = isOn;
            }
        }
    }
}
