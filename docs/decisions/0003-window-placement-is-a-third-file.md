# 3. Window placement is a third file, and the window sizes itself

**Date:** 2026-07-31
**Status:** Accepted
**Context:** On a 4K display the window opened enormous with a small column of content in the middle
of it, and a tray app that is set to start with Windows opened its window at every logon.

## Decision

Three things, one of which touches an invariant.

### The window sizes and positions itself

Nothing did before. `MainWindow` never called `AppWindow.Resize`, so the window inherited
`CW_USEDEFAULT`, which Windows scales off the display — on a 4K monitor, most of the screen. The
pages cap their content at 760 device-independent pixels, so every extra pixel of width arrived as
empty margin rather than as more of anything.

`WindowGeometry` (in `App/Presentation`, pure, tested) now states the default size in DIPs —
1150×780, chosen against the content rather than the screen — and converts to the physical pixels
`AppWindow` actually takes, using the DPI of the display the window opens on. The minimum size,
900×600 DIPs, goes onto `OverlappedPresenter.PreferredMinimumWidth`/`Height`, which is in the same
physical screen coordinates and is **not** scaled for the caller.

The single-column pages (Status, Settings, Battery) now centre their 760-DIP column instead of
pinning it left. The cap itself stays: a readable measure is deliberate, and the defect was the
window, not the column.

### `window.json` — a third file, and the one-writer rule survives

`StoragePaths` gains `WindowFile`. It sits beside `config.json` and `state.json` and holds one
`WindowPlacement`: position, size, and whether the window was maximized, all in physical screen
pixels.

It is a third file rather than a section of `AppConfig`, which is the part worth recording. The UI
is the only writer of `config.json`, so on the letter of the rule a `window` section would have
been legal. It would still have been wrong: `SettingsViewModel` loads one `AppConfig` when the page
is navigated to and saves that whole object on every toggle, so a placement written while that page
was open would be silently reverted by the next theme change. Each file still has exactly one
writer, and now no writer holds a stale copy of another's data.

`WindowPlacementStore` fails soft on **both** halves, which is where it parts company with the other
two stores. Theirs let a failed write reach the caller because a setting or a captured device that
silently did not persist changes what the app does. This one is written while the window is being
hidden or the process is ending: there is no page left to show an error on, and the cost of a lost
write is that the window opens where it would on a first run.

A remembered maximized state is applied when the window is first shown rather than when the
placement is applied, because `OverlappedPresenter.Maximize` shows the window as well as maximizing
it — doing it earlier would put a window on screen before the theme had been chosen, and would put
one on screen at all for a launch that asked to start in the notification area. `ShowFromTray` is
therefore the only path that puts this window on screen; nothing calls `Activate` directly.

A remembered placement is validated against the displays that are actually attached before it is
used — a laptop that was undocked, a monitor that was unplugged, a resolution that changed. A
placement that lands nowhere the user could click is discarded for the centred default. A sideways
overhang is kept, because a user who put the window there meant it and can still grab the part of
the title bar that is on screen; a window above the work area is pulled down, because that title bar
cannot be grabbed at all and Windows never lets one be dragged there.

### `app.startMinimized` — a setting, not a command-line switch

The app can start with no window, showing only the notification-area icon.

The obvious implementation is the one this rejects: write `"…exe" --tray` into the Run value and
parse that one flag. The app deliberately has no command-line handling — `Program.Main` ignores its
arguments rather than parsing them, so there is no switch to misuse — and a flag that decides what
the process does on startup would reintroduce exactly that, whatever it happened to control today.

So it is a setting, and it applies to every launch rather than only the one at logon. That is the
real cost of the choice: double-clicking the executable also starts hidden. It is a small one for a
tray app whose work runs whether or not a window is open — clicking the icon opens the window, and
so does launching a second copy, which the single-instance guard turns into "show yourself".

The window is never activated on that path, rather than shown and then closed, which is what would
flash a window across the screen at every logon. The tray icon is created directly with
H.NotifyIcon's `ForceCreate` — the visual tree that would otherwise create it loads on first
activation, which never happens. Efficiency mode is declined: it is that method's default and it
suits an app that is genuinely idle while hidden, but this one is polling devices the whole time it
is in the tray. If `ForceCreate` fails the window is shown after all, because a process with neither
a window nor an icon is one the user can neither see nor stop.

## Consequences

- A fourth thing to delete when resetting a user's state by hand, and `window.json` is one more file
  under `%APPDATA%\Dongled`. It is safe to delete at any time.
- `AppConfig` gains `StartMinimized` without a schema bump: an added property with a `false` default
  reads correctly from a v1 file and writes a v1 file.
- Anyone adding a per-window setting later should put it in `window.json` rather than growing
  `AppConfig`, for the stale-copy reason above.
