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
dotnet test                                         # 22 tests, all should pass
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
```

## Serial specifics that must not be undone

**Open `/dev/cu.*`, never `/dev/tty.*`.** `SerialPort.GetPortNames()` returns the
`tty` dial-in devices on macOS, and opening one blocks until carrier detect
asserts, which for most USB adapters never happens, so the app appears to hang.
`MacSerialPortEnumerator` scans `/dev` for `cu.` devices instead and hides the
`tty` duplicates.

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

## History file format

The tab-delimited format is a compatibility contract with the Windows build and
must keep round-tripping in both directions. Files from the original repo
(`ButtonA.txt`, `ButtonB.txt`, `raptor.ButtonA.txt`, the `COMMANDS` folder) are
the test corpus. `HistoryFileTests` covers the rules: runs of tabs collapse to
one separator, a bare command is its own description, a blank line toggles the
button shade, `**script` markers bracket a script block.

Deliberate divergence from Windows: entries keep file order here, where the
original stacks buttons upward so a loaded file reads bottom to top.

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

## Not ported yet

All present in the Windows build: the script runner (blocks parse and round-trip
through save, and appear as buttons, but clicking one does nothing yet),
XModem/YModem transfer, the ToolBox, Convertor and response analyzer windows,
multiple windows against several ports, and `COMET.ini` autoload at startup.
