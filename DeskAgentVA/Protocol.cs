using System;

namespace DeskAgentVA
{
    /// <summary>
    /// Wire protocol for the Switch-LED Arduino boards.
    ///
    /// Boards talk over a serial port at 57600 baud, newline terminated.
    ///
    /// Board -> PC status frame (18 chars), sent every ~1s and on any button change:
    ///     '&lt;'  ID(2 hex)  SIZE(2 hex)  BTN1 BTN2 BTN3 (3 bytes, 6 hex)  LED1 LED2 LED3 (3 bytes, 6 hex)  '&gt;'
    ///     e.g. &lt;011800000000000000&gt;  = device 1, size 0x18, no buttons, no LEDs.
    ///
    /// PC -> Board command:
    ///     '&lt;'  ACTION(1 char)  MASK(3 bytes, 6 hex)  '&gt;'
    ///     ACTION 'U' = turn on, 'D' = turn off, 'T' = toggle, 'S' = set all.
    ///     MASK bit (n-1) set = LED n. byte0 = LEDs 1..8, byte1 = 9..16, byte2 = 17..24.
    ///
    /// Button/LED numbering is 1-based to match the physical labelling and the
    /// original Desk Agent configuration.
    /// </summary>
    internal static class Protocol
    {
        public const int BaudRate = 57600;
        public const int StatusFrameLength = 18;   // '<' + 16 payload chars + '>'
        public const int MaxChannels = 24;         // 3 bytes worth of switches / LEDs
        public const int StatusByteCount = 3;

        public const char ActionOn = 'U';
        public const char ActionOff = 'D';
        public const char ActionToggle = 'T';
        public const char ActionSet = 'S';

        /// <summary>
        /// Parse a single status frame such as "&lt;011800000000000000&gt;".
        /// Returns false for anything that is not a well formed frame.
        /// </summary>
        public static bool TryParseStatusFrame(string frame, out int deviceId, out byte[] buttons, out byte[] leds)
        {
            deviceId = 0;
            buttons = new byte[StatusByteCount];
            leds = new byte[StatusByteCount];

            if (string.IsNullOrEmpty(frame)) return false;
            if (frame.Length != StatusFrameLength) return false;
            if (frame[0] != '<' || frame[frame.Length - 1] != '>') return false;

            try
            {
                deviceId = Convert.ToByte(frame.Substring(1, 2), 16);

                // frame[3..4] is the board's channel-count field; not needed here.

                for (int i = 0; i < StatusByteCount; i++)
                    buttons[i] = Convert.ToByte(frame.Substring(i * 2 + 5, 2), 16);

                for (int i = 0; i < StatusByteCount; i++)
                    leds[i] = Convert.ToByte(frame.Substring(i * 2 + 11, 2), 16);

                return true;
            }
            catch
            {
                deviceId = 0;
                return false;
            }
        }

        /// <summary>Build a command that acts on a single LED (1-based).</summary>
        public static string BuildLedCommand(char action, int ledNumber)
        {
            if (ledNumber < 1 || ledNumber > MaxChannels)
                throw new ArgumentOutOfRangeException(nameof(ledNumber),
                    $"LED number must be between 1 and {MaxChannels}.");

            var mask = new byte[StatusByteCount];
            SetBit(mask, ledNumber);
            return BuildMaskCommand(action, mask);
        }

        /// <summary>Build a command from a raw 24-bit LED mask (bit 0 = LED 1).</summary>
        public static string BuildMaskCommand(char action, int mask24)
        {
            var mask = new byte[StatusByteCount];
            mask[0] = (byte)(mask24 & 0xFF);
            mask[1] = (byte)((mask24 >> 8) & 0xFF);
            mask[2] = (byte)((mask24 >> 16) & 0xFF);
            return BuildMaskCommand(action, mask);
        }

        public static string BuildMaskCommand(char action, byte[] mask3)
        {
            if (action != ActionOn && action != ActionOff && action != ActionToggle && action != ActionSet)
                throw new ArgumentException($"Unknown action '{action}'.", nameof(action));

            string hex = string.Empty;
            for (int i = 0; i < StatusByteCount; i++)
                hex += mask3[i].ToString("X2");

            return $"<{action}{hex}>";
        }

        /// <summary>True if the given 1-based channel bit is set in a 3-byte status array.</summary>
        public static bool IsBitSet(byte[] status, int channelNumber)
        {
            int zero = channelNumber - 1;
            int byteIndex = zero / 8;
            int bitIndex = zero % 8;
            if (byteIndex < 0 || byteIndex >= status.Length) return false;
            return (status[byteIndex] & (1 << bitIndex)) != 0;
        }

        private static void SetBit(byte[] status, int channelNumber)
        {
            int zero = channelNumber - 1;
            status[zero / 8] |= (byte)(1 << (zero % 8));
        }
    }
}
