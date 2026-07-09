using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskAgentVA
{
    /// <summary>
    /// Owns one serial port and the single Arduino board attached to it.
    /// Reads status frames, tracks button / LED state, raises an event on each
    /// button transition, and sends LED commands. This is the plugin-side
    /// equivalent of the Desk Agent's Communicator, minus all WPF plumbing.
    /// </summary>
    internal sealed class DeviceLink
    {
        public string PortName { get; }

        /// <summary>Board ID as reported in its status frames. 0 until first frame.</summary>
        public int DeviceId { get; private set; }

        /// <summary>True once the board has sent at least one valid frame.</summary>
        public bool Connected { get; private set; }

        // Latest button / LED state (3 bytes each), guarded by _stateLock.
        private readonly byte[] _buttons = new byte[Protocol.StatusByteCount];
        private readonly byte[] _leds = new byte[Protocol.StatusByteCount];
        private readonly byte[] _prevButtons = new byte[Protocol.StatusByteCount];
        private readonly object _stateLock = new object();

        private readonly StringBuilder _rxBuffer = new StringBuilder();
        private readonly object _rxLock = new object();
        private readonly object _writeLock = new object();

        private SerialPort _port;
        private bool _firstScan = true;

        private Task _pumpTask;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>Raised (deviceId, buttonNumber 1-based, isOn) on every button transition.</summary>
        public event Action<int, int, bool> ButtonChanged;

        /// <summary>Raised when a board first identifies itself, or drops off.</summary>
        public event Action<DeviceLink> ConnectionChanged;

        public DeviceLink(string portName)
        {
            PortName = portName;
        }

        public void Start()
        {
            _pumpTask = Task.Run(PumpAsync);
        }

        public Task StopAsync()
        {
            _cts.Cancel();
            ClosePort();
            return _pumpTask ?? Task.CompletedTask;
        }

        private async Task PumpAsync()
        {
            try
            {
                _port = new SerialPort(PortName, Protocol.BaudRate)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    NewLine = "\n"
                };
                _port.DataReceived += OnDataReceived;
                _port.Open();
                _firstScan = true;

                while (!_cts.IsCancellationRequested && _port.IsOpen)
                    await Task.Delay(200, _cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Port vanished, is in use, or was cancelled; treat as disconnected.
            }
            finally
            {
                ClosePort();
                if (Connected)
                {
                    Connected = false;
                    ConnectionChanged?.Invoke(this);
                }
            }
        }

        private void ClosePort()
        {
            var p = _port;
            _port = null;
            if (p == null) return;
            try { p.DataReceived -= OnDataReceived; } catch { }
            try { if (p.IsOpen) p.Close(); } catch { }
            try { p.Dispose(); } catch { }
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var p = _port;
            if (p == null) return;

            string data;
            try { data = p.ReadExisting(); }
            catch { return; }

            lock (_rxLock)
            {
                _rxBuffer.Append(data);
                string buffer = _rxBuffer.ToString();

                // Drop anything before the first frame start.
                int start = buffer.IndexOf('<');
                if (start < 0)
                {
                    _rxBuffer.Clear();
                    return;
                }
                if (start > 0)
                    buffer = buffer.Substring(start);

                int open, close;
                while ((open = buffer.IndexOf('<')) != -1 &&
                       (close = buffer.IndexOf('>', open)) != -1)
                {
                    string frame = buffer.Substring(open, close - open + 1);
                    buffer = buffer.Remove(open, close - open + 1);
                    ProcessFrame(frame);
                }

                _rxBuffer.Clear();
                _rxBuffer.Append(buffer);
            }
        }

        private void ProcessFrame(string frame)
        {
            if (!Protocol.TryParseStatusFrame(frame, out int id, out byte[] buttons, out byte[] leds))
                return;

            bool justConnected = false;
            var transitions = new System.Collections.Generic.List<Tuple<int, bool>>();

            lock (_stateLock)
            {
                if (!Connected || DeviceId != id)
                {
                    DeviceId = id;
                    justConnected = !Connected;
                    Connected = true;
                }

                Array.Copy(buttons, _buttons, _buttons.Length);
                Array.Copy(leds, _leds, _leds.Length);

                if (_firstScan)
                {
                    Array.Copy(_buttons, _prevButtons, _buttons.Length);
                    _firstScan = false;
                }
                else
                {
                    for (int ch = 1; ch <= Protocol.MaxChannels; ch++)
                    {
                        bool now = Protocol.IsBitSet(_buttons, ch);
                        bool was = Protocol.IsBitSet(_prevButtons, ch);
                        if (now != was)
                            transitions.Add(Tuple.Create(ch, now));
                    }
                    Array.Copy(_buttons, _prevButtons, _buttons.Length);
                }
            }

            if (justConnected)
                ConnectionChanged?.Invoke(this);

            foreach (var t in transitions)
                ButtonChanged?.Invoke(DeviceId, t.Item1, t.Item2);
        }

        /// <summary>Send a raw command string (already wrapped in &lt; &gt;).</summary>
        public void Send(string command)
        {
            var p = _port;
            if (p == null || !p.IsOpen)
                throw new InvalidOperationException($"Port {PortName} is not open.");

            lock (_writeLock)
                p.WriteLine(command);
        }

        /// <summary>Read whether the given 1-based LED is currently lit (from last frame).</summary>
        public bool IsLedOn(int ledNumber)
        {
            lock (_stateLock)
                return Protocol.IsBitSet(_leds, ledNumber);
        }

        /// <summary>Optimistically update our shadow of an LED so status reads are snappy.</summary>
        public void ShadowLed(int ledNumber, bool on)
        {
            lock (_stateLock)
            {
                int zero = ledNumber - 1;
                int bi = zero / 8, bit = zero % 8;
                if (bi < 0 || bi >= _leds.Length) return;
                if (on) _leds[bi] |= (byte)(1 << bit);
                else _leds[bi] &= (byte)~(1 << bit);
            }
        }
    }
}
