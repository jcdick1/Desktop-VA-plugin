using System;

namespace DeskAgentVA
{
    /// <summary>
    /// VoiceAttack plugin that bridges the Switch-LED Arduino boards into VoiceAttack.
    ///
    /// It replaces the standalone Desk Agent: VoiceAttack itself now owns the
    /// keystrokes / process actions (via ordinary commands), and this plugin only
    /// does the two things VoiceAttack cannot do on its own:
    ///   * drive LEDs on the boards from your commands, and
    ///   * turn physical switch flips into command invocations.
    ///
    /// See README.md for the full command / variable contract.
    /// </summary>
    // Non-static class (with static members) so VoiceAttack's loader can instantiate the
    // type while still calling the required static entry points.
    public class DeskAgentPlugin
    {
        private static readonly object _lock = new object();
        private static SerialManager _manager;
        private static dynamic _proxy;
        private static bool _started;

        // Defaults; overridable via VoiceAttack text variables of the same name.
        private const string DefaultSwitchPrefix = "Desk.Switch";
        private const string DefaultDispatcher = "((Desk Switch))";

        // ---- Required VoiceAttack entry points --------------------------------

        public static string VA_DisplayName() => "Desk Agent (Switch/LED Bridge)";

        public static string VA_DisplayInfo() =>
            "Talks to the Switch-LED Arduino boards over serial.\r\n" +
            "Turn LEDs on/off/toggle from commands, and fire commands when physical switches change.\r\n" +
            "https://github.com/jcdick1  (ported from Desk_Agent)";

        public static Guid VA_Id() => new Guid("cad277e7-0217-4880-bc97-ce4602aeb54e");

        public static void VA_Init1(dynamic vaProxy)
        {
            _proxy = vaProxy;
            lock (_lock)
            {
                if (_started) return;

                _manager = new SerialManager { Log = LogInfo };
                _manager.ButtonChanged += OnButtonChanged;
                _manager.Start();
                _started = true;
            }
            LogInfo("Desk Agent plugin initialised; scanning serial ports.");
        }

        public static void VA_Exit1(dynamic vaProxy)
        {
            lock (_lock)
            {
                try { _manager?.Stop(); } catch { }
                _manager = null;
                _started = false;
            }
        }

        // Called when a running command is force-stopped. Nothing to unwind.
        public static void VA_StopCommand() { }

        public static void VA_Invoke1(dynamic vaProxy)
        {
            _proxy = vaProxy;

            string context;
            try { context = (string)vaProxy.Context ?? string.Empty; }
            catch { context = string.Empty; }

            if (_manager == null)
            {
                SetResult(vaProxy, false, "Plugin not initialised");
                return;
            }

            // Context grammar: "<command>[;arg1[;arg2]]" — case-insensitive command.
            var parts = context.Split(';');
            string cmd = parts.Length > 0 ? parts[0].Trim().ToLowerInvariant() : string.Empty;
            string arg1 = parts.Length > 1 ? parts[1].Trim() : null;
            string arg2 = parts.Length > 2 ? parts[2].Trim() : null;

            try
            {
                switch (cmd)
                {
                    case "led.on":
                    case "led.set":
                        DoLed(vaProxy, arg1, arg2, Protocol.ActionOn);
                        break;
                    case "led.off":
                        DoLed(vaProxy, arg1, arg2, Protocol.ActionOff);
                        break;
                    case "led.toggle":
                        DoLed(vaProxy, arg1, arg2, Protocol.ActionToggle);
                        break;
                    case "led.status":
                        DoLedStatus(vaProxy, arg1, arg2);
                        break;
                    case "leds.off":
                    case "leds.alloff":
                        DoAllOff(vaProxy, arg1);
                        break;
                    case "leds.mask":
                        DoMask(vaProxy, arg1, arg2);
                        break;
                    case "device.connected":
                    case "device.status":
                        DoDeviceStatus(vaProxy, arg1);
                        break;
                    case "rescan":
                        _manager.Rescan();
                        SetResult(vaProxy, true, "Rescan requested");
                        break;
                    case "":
                        SetResult(vaProxy, false, "No plugin context supplied");
                        break;
                    default:
                        SetResult(vaProxy, false, $"Unknown context '{context}'");
                        break;
                }
            }
            catch (Exception ex)
            {
                SetResult(vaProxy, false, ex.Message);
                LogError($"Invoke '{context}' failed: {ex.Message}");
            }
        }

        // ---- Context handlers -------------------------------------------------

        private static void DoLed(dynamic vaProxy, string devArg, string ledArg, char action)
        {
            int device = ResolveInt(vaProxy, devArg, "Desk.Device", 0);
            int led = ResolveInt(vaProxy, ledArg, "Desk.Led", 0);

            bool ok = _manager.SetLed(device, led, action, out string message);
            SetResult(vaProxy, ok, message);
        }

        private static void DoLedStatus(dynamic vaProxy, string devArg, string ledArg)
        {
            int device = ResolveInt(vaProxy, devArg, "Desk.Device", 0);
            int led = ResolveInt(vaProxy, ledArg, "Desk.Led", 0);

            bool ok = _manager.TryGetLedState(device, led, out bool isOn, out string message);
            TrySetBoolean(vaProxy, "Desk.LedState", isOn);
            SetResult(vaProxy, ok, message);
        }

        private static void DoAllOff(dynamic vaProxy, string devArg)
        {
            int device = ResolveInt(vaProxy, devArg, "Desk.Device", 0);
            bool ok = _manager.AllOff(device, out string message);
            SetResult(vaProxy, ok, message);
        }

        private static void DoMask(dynamic vaProxy, string devArg, string maskArg)
        {
            int device = ResolveInt(vaProxy, devArg, "Desk.Device", 0);
            int mask = ResolveInt(vaProxy, maskArg, "Desk.Mask", 0);
            bool ok = _manager.SetMask(device, mask, out string message);
            SetResult(vaProxy, ok, message);
        }

        private static void DoDeviceStatus(dynamic vaProxy, string devArg)
        {
            int device = ResolveInt(vaProxy, devArg, "Desk.Device", 0);
            bool connected = _manager.IsConnected(device);
            TrySetBoolean(vaProxy, "Desk.Connected", connected);
            SetResult(vaProxy, true, connected ? "connected" : "not connected");
        }

        // ---- Switch -> command dispatch --------------------------------------

        private static void OnButtonChanged(int device, int button, bool isOn)
        {
            var vaProxy = _proxy;
            if (vaProxy == null) return;

            string state = isOn ? "On" : "Off";

            // Publish the event details so any command can read them.
            TrySetInt(vaProxy, "Desk.Device", device);
            TrySetInt(vaProxy, "Desk.Button", button);
            TrySetText(vaProxy, "Desk.State", state);
            TrySetBoolean(vaProxy, "Desk.StateOn", isOn);

            string prefix = GetTextOrDefault(vaProxy, "Desk.CommandPrefix", DefaultSwitchPrefix);
            string dispatcher = GetTextOrDefault(vaProxy, "Desk.DispatcherCommand", DefaultDispatcher);

            // Precedence: most specific existing command wins.
            string specificState = $"{prefix}.{device}.{button}.{state}";   // e.g. Desk.Switch.1.5.On
            string specificAny = $"{prefix}.{device}.{button}";             // e.g. Desk.Switch.1.5

            if (TryExecute(vaProxy, specificState)) return;
            if (TryExecute(vaProxy, specificAny)) return;
            if (TryExecute(vaProxy, dispatcher)) return;

            // Momentary (SPDT) buttons are usually mapped on the press (.On) edge only, so an
            // unmapped release (.Off) edge is normal and stays quiet. An unmapped press is
            // more likely a mistake, so surface that one.
            if (isOn)
                LogInfo($"Switch {device}.{button} -> {state} (no matching command: " +
                        $"'{specificState}', '{specificAny}', or '{dispatcher}')");
        }

        // ---- vaProxy helpers (all defensive: vaProxy is dynamic) -------------

        private static bool TryExecute(dynamic vaProxy, string commandName)
        {
            if (string.IsNullOrEmpty(commandName)) return false;
            try
            {
                if (!(bool)vaProxy.CommandExists(commandName)) return false;
                vaProxy.ExecuteCommand(commandName);
                return true;
            }
            catch (Exception ex)
            {
                LogError($"ExecuteCommand('{commandName}') failed: {ex.Message}");
                return false;
            }
        }

        private static int ResolveInt(dynamic vaProxy, string arg, string variableName, int fallback)
        {
            if (!string.IsNullOrEmpty(arg) && int.TryParse(arg, out int fromContext))
                return fromContext;

            try
            {
                int? v = vaProxy.GetInt(variableName);
                if (v.HasValue) return v.Value;
            }
            catch { }

            return fallback;
        }

        private static string GetTextOrDefault(dynamic vaProxy, string variableName, string fallback)
        {
            try
            {
                string v = vaProxy.GetText(variableName);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }
            return fallback;
        }

        private static void SetResult(dynamic vaProxy, bool success, string message)
        {
            TrySetBoolean(vaProxy, "Desk.Success", success);
            TrySetText(vaProxy, "Desk.Message", message ?? string.Empty);
        }

        private static void TrySetText(dynamic vaProxy, string name, string value)
        { try { vaProxy.SetText(name, value); } catch { } }

        private static void TrySetInt(dynamic vaProxy, string name, int value)
        { try { vaProxy.SetInt(name, value); } catch { } }

        private static void TrySetBoolean(dynamic vaProxy, string name, bool value)
        { try { vaProxy.SetBoolean(name, value); } catch { } }

        private static void LogInfo(string message)
        { try { _proxy?.WriteToLog("Desk Agent: " + message, "blue"); } catch { } }

        private static void LogError(string message)
        { try { _proxy?.WriteToLog("Desk Agent: " + message, "red"); } catch { } }
    }
}
