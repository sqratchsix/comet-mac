# COMET for macOS – working notes

macOS port of [COMET](https://github.com/sqratchsix/COMET), a .NET Framework 4.8
WinForms serial terminal that runs on Windows only. This repo rewrites the UI in
Avalonia on .NET 10 and shares a platform-neutral core.

Read this before changing anything. Most of what follows was learned the hard
way and is not visible from the code.

## Environment

`dotnet` is **not on the default PATH** for non-interactive tools. It lives in
`~/.dotnet` (SDK 10.0.400, installed via `dotnet-install.sh`, no sudo). `.zshrc`
exports it for interactive shells. Any script or tool invocation needs:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

The solution file is `Comet.slnx`, the new SDK format, not `Comet.sln`.

## Commands

```bash
dotnet build                                        # whole solution
dotnet test                                         # 60 tests, all should pass
dotnet run --project src/Comet.Desktop              # run from source, needs a display
scripts/build-app.sh                                # build/COMET.app, self-contained arm64
dotnet run --project tools/Comet.Shot -- docs/comet.png   # render the UI to a PNG
```

## Seeing the UI

Two constraints make the obvious approaches fail:

**A GUI process needs an unlocked screen.** macOS will not hand a display link
to an app while the session is locked, and Avalonia then dies at startup with
`Avalonia.Native was not able to start the RenderTimer. Native error code is:
-6661`. That error means the screen is locked or there is no GUI session. It is
not a code fault. Check with:

```bash
ioreg -n Root -d1 -a | grep -A1 CGSSessionScreenIsLocked
```

**A sandboxed shell has no window server access**, so launch the bundle through
`open build/COMET.app` rather than running the binary directly. Verify it came
up, and read its window title and menus, through the accessibility API:

```bash
osascript -e 'tell application "System Events" to tell process "Comet.Desktop" to get title of window 1'
```

**`screencapture` returns only the desktop wallpaper** unless the calling
terminal has been granted Screen Recording permission, so it is useless for
reviewing the interface. Use `tools/Comet.Shot` instead: it renders MainWindow
off screen through Avalonia's headless platform with Skia and writes a real PNG,
in about two seconds. That is the way to iterate on design, and it also
regenerates the README screenshot. `MainViewModel.LoadSampleContent()` fills the
window with representative content for it and is not called by the app itself.

## Layout

```
src/Comet.Core        Serial I/O, port discovery, formatting, history file format.
                      No UI dependency. Only macOS-specific file is the port enumerator.
src/Comet.Desktop     Avalonia UI.
  Themes/Comet.axaml    Colour tokens and font families.
  Themes/Controls.axaml Control styles: buttons, chips, combos, text fields.
  Views/MainWindow.axaml Layout.
tests/                xUnit, including serial round trips over a pseudo terminal.
tools/Comet.Shot      Headless renderer described above.
scripts/build-app.sh  Publishes and assembles the .app bundle.
docs/icon/            App icon sources: traced SVGs and the original artwork.
                      build/COMET.icns and Assets/comet.ico are rendered from them.
```

## Serial specifics that must not be undone

**Open `/dev/cu.*`, never `/dev/tty.*`.** `SerialPort.GetPortNames()` returns the
`tty` dial-in devices on macOS, and opening one blocks until carrier detect
asserts, which for most USB adapters never happens, so the app appears to hang.
`MacSerialPortEnumerator` scans `/dev` for `cu.` devices instead and hides the
`tty` duplicates.

**Bluetooth serial ports are hidden.** macOS makes a `/dev/cu.*` for every
paired Bluetooth Classic device with a serial service (speakers, tablets,
phones), and opening one makes the Mac try to connect to it. The enumerator
drops any port below a node run by `com.apple.IOUserBluetoothSerialDriver`
(or an `IOBluetooth*` class on older releases), including
`Bluetooth-Incoming-Port`.

**Friendly names come from `ioreg`,** replacing the Windows build's WMI
`Win32_PnPEntity` query. It walks the tree from each `IOSerialBSDClient` node up
to the nearest USB ancestor to find the product name, which is how
`/dev/cu.usbserial-0001` is shown as `CP2102 USB to UART Bridge Controller`.

**Do not go back to `SerialPort.DataReceived`.** It is backed by a polling loop
on Unix and drops events under load. `ActiveSerialPort` runs a dedicated read
loop on `BaseStream` and keeps the original's idle-timer coalescing, so a reply
arriving in several packets still reaches the terminal as one message. Bytes are
buffered raw and decoded on read, so switching ASCII/HEX mid-stream is safe.

Text is decoded as Latin-1 rather than 7-bit ASCII so bytes above 0x7F survive.

**DTR defaults to on.** USB CDC bridges such as the XIAO MG24's SAMD11 discard
all traffic while DTR is low, and .NET opens ports with it low.

**Cellular modems are USB bulk ports, not serial ports.** Quectel and similar
modules expose their AT ports as vendor-class (0xFF) USB interfaces. Linux binds
them with its `option` driver; macOS binds nothing, so no `/dev/cu.*` appears.
`UsbBulkEnumerator` lists those interfaces through libusb with paths like
`usb:2c7c:0801@1-2.4.3:2`, and `ActiveSerialPort` opens them as a
`UsbBulkStream` through the same read loop. libusb is loaded at run time, from
the bundle first (`build-app.sh` copies Homebrew's), so it stays optional.
Interfaces a macOS driver already owns (`libusb_kernel_driver_active`), such
as a CH340 or CP210x bridge that is also a `/dev/cu` port, are skipped so a
device is never listed twice. Interfaces named CMSIS-DAP are hidden, and bulk ports are never auto-opened at
startup, because claiming a modem's DIAG port unasked is not harmless.

## History file format

The tab-delimited format is a compatibility contract with the Windows build and
must keep round-tripping in both directions. Files from the original repo
(`ButtonA.txt`, `ButtonB.txt`, `raptor.ButtonA.txt`, the `COMMANDS` folder) are
the test corpus. `HistoryFileTests` covers the rules: runs of tabs collapse to
one separator, a bare command is its own description, a blank line toggles the
button shade, `**script` markers bracket a script block.

Buttons stack upward as on Windows: the first entry sits at the foot of the
pane and each new one lands on top, so a loaded file reads bottom to top. The
collection itself stays in file order and a `ReversibleStackPanel` does the
flipping, so saving still writes the file the way it was read.

## Design

The visual language is deliberately shared with
[mmyyxx](https://github.com/sqratchsix/mmyyxx), which lives at
`~/Developer/mmyyxx`. **Take numbers from its SwiftUI sources rather than
eyeballing a screenshot**, since a Retina screenshot makes everything look about
twice its real size:

- `Sources/UI/Theme.swift` – colour tokens, already mirrored into `Comet.axaml`
- `Sources/UI/ChannelStrip.swift` – `ToggleChipStyle`, the chip spec: radius 4,
  `panelRaised` when off with secondary text, tint fill with white text and a
  soft glow when on
- `Sources/UI/ContentView.swift` – 14pt outer padding, header, status bars, the
  footer chip on a 14% wash of its own colour

The type scale is small: 7 to 13pt labels under a 17pt title, tracked uppercase
for section and chip labels.

Chips are tinted by function, following how mmyyxx tints sends, FX return and
mute differently. Class `chip` is accent for what goes on the wire, `chip view`
is green for what only changes the view, `chip line` is amber for hardware
control lines.

## Typeface

The app embeds Nunito under SIL OFL 1.1, with the licence beside the files in
`src/Comet.Desktop/Assets/Fonts`. Two reasons not to reach for the system font:
macOS hides SF Rounded behind the dot-prefixed family `.SF NS Rounded`, which
Avalonia cannot resolve so it silently falls back to Helvetica, and the face is
not redistributable.

The five weights are **static instances**, generated from the upstream variable
font with `fontTools.varLib.instancer`. Avalonia matches named faces rather than
a variable weight axis, so shipping the variable font renders everything at
Regular. If a weight is ever added, instance it the same way.

## Window height

The port panel never scrolls. `MinHeight` on `MainWindow` is the height at
which every setting fits, measured from a headless render (431px of settings
needs a 644px window; 660 leaves some room), and `FitMinHeightToPortPanel`
raises it at run time if real font metrics need more. Adding a control to the
panel means raising `MinHeight` too.

## Terminal

Timestamps are applied per line by `TerminalLineStamper` in the view, on the UI
thread, so continuation lines of a multi-line reply are stamped and a reply
split across reads is never stamped mid-line. The view model attaches the time
the text arrived to every message, always. Each line's stamp is a `StampRun`
holding that time, and the TIMESTAMP chip and format box only rewrite those
runs, so switching stamps on shows them for output that is already there.

## Windows and menus

File > New Window opens another `MainWindow` with its own view model and port,
and calls `OpenFirstAvailablePort`, which relies on ports opening exclusively to
skip the ones other windows hold. Nothing auto-opens in the first window.

"The folder COMET runs from" (`MainViewModel.AppFolder`, Open Containing Folder,
Load Commands) is the folder holding `COMET.app`, since the binary sits in
`COMET.app/Contents/MacOS`. Load Commands lists `.txt` files there through
`CommandFileFinder`, which is depth and count limited because that folder may be
`/Applications`.

The history pane has one context menu for the pane and its buttons. A tunnelling
right-button `PointerPressed` records which button was under the pointer, and
the menu's `Opening` handler shows that button's items. `Opening` only fires for
a real context request, not for `ContextMenu.Open()` from code.

The header's A B C D buttons load `ButtonA.txt`..`ButtonD.txt` from that same
folder, replacing the smart buttons, as the Windows build's AButton_A..D did.
The originals live in the repo root with `COMMANDS/` (copied from the Windows
repo); `build-app.sh` copies them beside `COMET.app` with `rsync --update`, so a
copy saved from inside COMET is not overwritten by a rebuild.

In-window `MenuItem.Click` handlers must take `RoutedEventArgs` or the XAML
compiler fails with AVLN3000; `NativeMenuItem.Click` takes plain `EventArgs`.

## Gotchas

- XML comments cannot contain `--`, so no `<!-- ==== SECTION ==== -->` dividers
  in `.axaml`. The build fails with an opaque `AVLN1001` stack trace.
- A negated binding such as `{Binding !IsHexMode}` will not write back. Pairs of
  radio buttons or chips need a real inverse property, as `IsAsciiMode` is.
- `Application.Name` in `App.axaml` sets the macOS menu bar title. Without it
  the app menu reads "Avalonia Application" no matter what `CFBundleName` says.
- `src/Comet.Desktop/app.manifest` is inert Windows template leftover.

## Commit conventions

Commit as `sqratchsix <sqratchsix@users.noreply.github.com>`, otherwise
contributions credit the wrong account.

## Scripts

Script buttons run through `Comet.Core.ScriptRunner`, a port of the Windows
`runScript` / `scriptHandleFunction`. `ScriptCommandCatalog` is the function
table shown in Script Commands and used for the editor's hints; it was written
against the runner, because the Windows `ScriptCommandsForm` had drifted from
its own runner (wrong argument order for `response_str`, missing `log`,
`unicode`, `user_input_log`). `ScriptRunnerTests.CatalogCoversEveryFunction...`
keeps the two in step.

Step delay and loops were memory-only on Windows. They are saved as leading
`**delay` / `**loop` lines, which the Windows runner executes the same way or
ignores, and default values write nothing, so untouched files round-trip
byte for byte.

The runner runs on a task; `MainViewModel.Host` marshals anything that touches
bound properties onto the UI thread. Response functions read a capture buffer
fed from `DataReceived`. `**time`, log confirmations and errors go to the
footer status, never the terminal.

## The terminal is wire traffic only

Only text sent and received on the port goes in the terminal. Every status,
confirmation and error message goes to `StatusMessage` in the footer through
`SetStatus`. `TerminalTextKind` deliberately has no "system" kind.

## Not ported yet

All present in the Windows build: XModem/YModem transfer (the script functions
are listed but report "not available"), the ToolBox, Convertor and response
analyzer windows, Open All Ports / Tile / Close Other Instances, the
command-button edit dialog, and `COMET.ini` autoload at startup.
