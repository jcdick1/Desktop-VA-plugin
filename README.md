# Desk Agent — VoiceAttack plugin

A VoiceAttack plugin that talks to the **Switch‑LED Arduino** boards over serial, so you
can drive LEDs and react to physical switches **directly from VoiceAttack commands** —
no separate Desk Agent app running in the background.

It is a port of the standalone [`Desk_Agent`](https://github.com/jcdick1/Desk_Agent) WPF app.
The key idea: VoiceAttack is already excellent at keystrokes, launching/stopping processes,
waiting, branching, etc. So this plugin keeps **only** the two things VoiceAttack can't do by
itself, and hands everything else back to VoiceAttack:

| Desk_Agent feature | Where it lives now |
| --- | --- |
| Serial discovery + framing with the boards | **This plugin** |
| Turn LEDs on / off / toggle | **This plugin** (called from your commands) |
| A switch flip triggers an action | **This plugin** raises it → **your VoiceAttack command** |
| Keystroke on switch | A normal VoiceAttack command (native "Press key" action) |
| Start / stop / toggle a process | A normal VoiceAttack command |
| Process‑running → LED mirror | A VoiceAttack command/loop that calls `led.on` / `led.off` |
| HTTP API for LEDs | Not needed — call the plugin contexts instead |

---

## Requirements

- Windows, [VoiceAttack](https://voiceattack.com/) with **plugin support enabled**
  (Settings → General ▸ *Enable plugin support*).
- To build: the **.NET SDK** (`dotnet`) *or* Visual Studio 2022 with the ".NET Framework 4.8"
  targeting pack. The project targets **.NET Framework 4.8** because VoiceAttack is a
  .NET Framework app and can only load Framework assemblies.

## Build

```powershell
# from the repo root
dotnet build -c Release
```

The `Microsoft.NETFramework.ReferenceAssemblies` NuGet reference means you do **not** need a
full Visual Studio install or the standalone targeting pack — the .NET SDK alone can build the
net48 DLL. (Visual Studio users can just open `DeskAgentVA.sln` and build.)

Output: `DeskAgentVA\bin\Release\DeskAgentVA.dll`.

## Install into VoiceAttack

1. Find your VoiceAttack **Apps** folder. Default is:
   `C:\Program Files\VoiceAttack\Apps\`
   (or, for the Microsoft Store version, `Documents\VoiceAttack\Apps\`).
2. Create a subfolder for the plugin and copy the DLL into it:
   `...\VoiceAttack\Apps\DeskAgentVA\DeskAgentVA.dll`
   Each plugin **must** live in its own subfolder.
3. In VoiceAttack, make sure **Enable plugin support** is ticked, then restart VoiceAttack.
   You should see `Desk Agent (Switch/LED Bridge)` initialise in the VoiceAttack log, followed
   by lines like `Opened COM5, listening for a board...` and `Device 1 connected on COM5.`

The plugin auto‑discovers boards on all COM ports (rescan every 3 s), identifies each board by
the `ID` it reports, and reconnects automatically when a board is plugged/unplugged.

## Example profile

`examples/DeskAgent-Examples.vap` is an importable VoiceAttack profile that shows the exact
syntax. In VoiceAttack: **Profile ▸ Import Profile ▸** pick the file. It adds four commands in a
`Desk Agent` category:

| Command name | Actions | Demonstrates |
| --- | --- | --- |
| `Desk.Switch.1.5.On` | press `L`, then plugin `led.on;1;5` | self‑centering lever **down** |
| `Desk.Switch.1.6.On` | press `L`, then plugin `led.off;1;5` | same lever **up** |
| `Desk.Switch.2.3.On` | press `G`, then plugin `led.toggle;2;3` | momentary button |
| `test desk lights off` | plugin `leds.off;1` | spoken test — say it to check LED control with no hardware |

Open any command and look at the *Execute an external plugin function* action: the plugin is
selected by its ID and the request is in the **Plugin Context** box (e.g. `led.on;1;5`). Duplicate
a command, rename it to your switch's `Desk.Switch.<device>.<channel>.On`, and change the key and
context. The profile isn't tied to any game (no process override), so it loads anywhere.

> The example is plain‑XML and was built against VoiceAttack 2.1.8's export format. If your
> VoiceAttack is much older/newer and import complains, build the commands by hand from the table
> above — the plugin behaviour is identical either way.

---

## Controlling LEDs from a command

Add a **"Execute an external plugin function"** action to any command, pick
`Desk Agent (Switch/LED Bridge)`, and put the request in the **Plugin Context** box.

Context format is semicolon‑separated: `command;arg1;arg2`. Arguments may be literals or
VoiceAttack tokens (e.g. `{INT:Desk.Device}`). If you omit the arguments, the plugin falls back
to VoiceAttack variables (see below).

| Plugin Context | Effect |
| --- | --- |
| `led.on;1;5` | Device **1**, LED **5** on |
| `led.off;1;5` | Device 1, LED 5 off |
| `led.toggle;1;5` | Device 1, LED 5 toggle |
| `led.status;1;5` | Read LED 5 → sets `Desk.LedState` (boolean) |
| `leds.off;1` | All LEDs off on device 1 |
| `leds.mask;1;255` | Set device 1's 24‑bit LED mask (bit 0 = LED 1). `255` = LEDs 1–8 on |
| `device.connected;1` | Is device 1 present? → sets `Desk.Connected` (boolean) |
| `rescan` | Force an immediate serial rescan |

Devices are **1‑based**, LEDs are **1‑based** (LED 1 … 24), matching the board labelling.

### Input variables (used when the context omits the arguments)

| Variable | Type | Meaning |
| --- | --- | --- |
| `Desk.Device` | Integer | Target board ID |
| `Desk.Led` | Integer | Target LED number (1‑based) |
| `Desk.Mask` | Integer | 24‑bit mask for `leds.mask` |

So `led.on` with no args uses `Desk.Device` + `Desk.Led`; `led.on;1;5` ignores the variables.

### Output variables (set by the plugin after every call)

| Variable | Type | Meaning |
| --- | --- | --- |
| `Desk.Success` | Boolean | Did the call succeed? |
| `Desk.Message` | Text | `OK`, or the failure reason (e.g. `Device 1 not connected`) |
| `Desk.LedState` | Boolean | Result of `led.status` |
| `Desk.Connected` | Boolean | Result of `device.connected` |

**Example — "Toggle desk lamp LED" command:** one action ▸ *Execute an external plugin function*
▸ Context `led.toggle;1;1`. Done.

---

## Reacting to physical switches

When a switch changes, the plugin sets these variables and then runs a VoiceAttack command:

| Variable | Type | Meaning |
| --- | --- | --- |
| `Desk.Device` | Integer | Board that changed |
| `Desk.Button` | Integer | Switch number (1‑based) |
| `Desk.State` | Text | `On` or `Off` |
| `Desk.StateOn` | Boolean | `True` when the switch went on |

**Which command runs** — the plugin looks for these command names in order and runs the first
one that exists:

1. `Desk.Switch.<device>.<button>.<On|Off>` — e.g. `Desk.Switch.1.5.On`
2. `Desk.Switch.<device>.<button>` — e.g. `Desk.Switch.1.5` (fires for both on and off)
3. `((Desk Switch))` — a single catch‑all dispatcher; branch on the variables above

So you have two styles:

- **One command per switch/edge (simplest).** Create a command literally named
  `Desk.Switch.1.5.On` and give it a *Press key* action (e.g. press `F13`). Flip switch 5 on
  board 1 → that keystroke fires. Create `Desk.Switch.1.5.Off` for the release if you want it.
- **One dispatcher.** Create a command named `((Desk Switch))` and inside it use
  *Begin Text Compare* on `{TXT:Desk.State}` / `{INT:Desk.Button}` to decide what to do.

> The prefix (`Desk.Switch`) and dispatcher name (`((Desk Switch))`) can be overridden by
> setting text variables `Desk.CommandPrefix` and `Desk.DispatcherCommand` before the plugin
> initialises (e.g. in a profile "on load" command).

### Every switch is momentary — map the press (`.On`) edge

All of these controls are momentary: the SPDT push buttons, and the "toggle" levers, which are
self-centering (ON)-OFF-(ON) — up and down are each momentary and spring back to center. So
nothing has a resting on/off *position*; every actuation is a press. Put your work on the `.On`
command and leave `.Off` unmapped (the release edge then does nothing).

- A **self-centering up/down lever uses two channels** — one for up, one for down — because a
  single input bit has only two states. Each direction is its own momentary "button."
- A **momentary push button uses one channel.**

Unmapped **release** edges are silent; an unmapped **press** edge is logged. That log line is
also how you **find channel numbers**: actuate each switch and watch the VoiceAttack log for
`Switch <device>.<channel> -> On (no matching command...)`.

Because no switch holds a position, drive LEDs from *intent*, not from the switch:
up/down lever → `led.on` on one direction, `led.off` on the other; single button → `led.toggle`
to flip a logical state each press.

### Example A — self-centering up/down lever (landing gear)

Say device 1: pushing the lever **down** fires channel 5, **up** fires channel 6, and LED 5 is
the gear indicator. `L` is the sim's gear-toggle key. Two commands, press edge only:

- **`Desk.Switch.1.5.On`** (lever down) → *Press key* `L`, then plugin `led.on;1;5`
- **`Desk.Switch.1.6.On`** (lever up)  → *Press key* `L`, then plugin `led.off;1;5`

Push down = gear down + LED on; push up = gear up + LED off. If your sim has *separate*
gear-up / gear-down keys, use those in each command instead of `L` — cleaner still.

### Example B — momentary push button (toggles a state + its LED)

Device 2, button on channel 3. One command:

- **`Desk.Switch.2.3.On`** → *Press key* your hotkey, then plugin `led.toggle;2;3`

Each press sends the key once and flips the LED. No `.Off` command needed.

> **Naming the command:** put the exact text (e.g. `Desk.Switch.1.5.On`) in the *When I say*
> box. You'll never speak it — the plugin runs it by that name — and dotted names like this
> won't be triggered by voice in practice.

> **Bounce:** the firmware doesn't debounce. Cleanly wired SPDT buttons are usually fine, but if
> a single press ever fires twice, that's contact bounce — say so and a small per-channel
> debounce can be added to the plugin.

---

## How it maps to the old Desk_Agent config

The old `configuration.json` bound each button to a list of actions. Rebuild those as
VoiceAttack commands:

| Old action `Type` | Rebuild as |
| --- | --- |
| `LED On` / `LED Off` / `LED Toggle` (`device;led`) | Plugin context `led.on;device;led` etc. |
| `Keyboard` (`Alt;Tab`) | VoiceAttack *Press key(s)* action |
| `Process Start` (`notepad`) | VoiceAttack *Launch application* action |
| `Process Stop` (`notepad`) | VoiceAttack *Kill a running application* action |
| `Process Toggle` | VoiceAttack command with a *process‑running* condition |
| `ProcessMonitors` (LED mirrors a process) | A periodic command: check the process, call `led.on`/`led.off` |

---

## Serial protocol (reference)

57600 baud, newline terminated. Ported verbatim from `Desk_Agent` / the Arduino firmware.

- **Board → PC status frame** (18 chars, every ~1 s and on any switch change):
  `<` `ID`(2 hex) `SIZE`(2 hex) `BTN1 BTN2 BTN3`(3 bytes) `LED1 LED2 LED3`(3 bytes) `>`
  e.g. `<011800000000000000>`.
- **PC → Board command:** `<` `ACTION` `MASK`(3 bytes = 6 hex) `>`
  `ACTION` ∈ `U` (on) `D` (off) `T` (toggle) `S` (set all). Bit *(n‑1)* = channel *n*;
  byte 0 = channels 1–8, byte 1 = 9–16, byte 2 = 17–24.

Board `ID`s come from the `#define ID` in each sketch (e.g. Board24 = 1).

---

## Troubleshooting

- **Plugin doesn't load / not listed** — Enable plugin support in VoiceAttack settings, confirm
  the DLL is in its own subfolder under `Apps\`, and that it's the **net48** build. Check the
  VoiceAttack log at startup.
- **`Device N not connected`** — the board hasn't sent a frame yet, its `ID` differs from `N`,
  or another program (Arduino Serial Monitor, the old Desk Agent) holds the COM port. Only one
  app can own a serial port at a time.
- **Switch does nothing** — verify a command named exactly `Desk.Switch.<device>.<button>.<On|Off>`
  (or a `((Desk Switch))` dispatcher) exists. The plugin logs the names it looked for when none
  match.
