# Security

This document states the limits as plainly as the mitigations. Overclaiming is worse than not
claiming, so where something is not defended, it says so and says why.

## Reporting a vulnerability

Report privately through
[GitHub Security Advisories](https://github.com/navrozashvili/Dongled/security/advisories/new).
Please do not open a public issue for anything you believe is exploitable.

This is a spare-time project by one person. You will get an acknowledgement on a best-effort
basis, and a fix when there is a fix — there is no committed response window and no bounty.
If you would rather disclose publicly after a period of your choosing, say so in the report
and that is fine.

## Supported versions

The latest release only. There are no backported fixes to earlier tags.

## What the app does that is worth reviewing

- Sets the Windows default audio endpoint through `IPolicyConfig`, an undocumented COM
  interface (see below).
- Loads third-party plugin assemblies in-process, gated by user approval and a hash pin, or, for
  the plugins a release ships, by hashes compiled into the release.
- Connects outward to loopback ports owned by vendor software, and reads USB HID devices.
- Writes a per-user autorun value under `HKCU\...\Run` when the user enables that setting.
- Extracts user-chosen `.zip` files into the `Plugins` directory beside the executable.

## The limits

### A plugin runs in-process with your full privileges

This is the most important sentence in this document. A plugin is a .NET assembly loaded into
the application's own process. Once you approve it, it can do anything you can do: read your
files, open sockets, start processes.

The collectible `AssemblyLoadContext` each plugin is loaded into is a **versioning boundary,
not a sandbox**. It exists so two plugins can depend on different versions of the same library
without colliding, and so a plugin can be unloaded. It is not a security boundary and does not
restrict what a plugin may do.

Hash pinning defends against two specific things and no others:

- A DLL planted in the `Plugins` directory that you never approved. It will not load.
- Silent replacement of a plugin you did approve. Any file added, changed or removed inside an
  approved directory changes the manifest hash, re-blocks the plugin, and the Plugins page
  names the files that differ.

It does **not** contain a plugin you approved. And it does not stop an attacker who is already
executing code as your user — they can edit `config.json` and record their own hash. Integrity
pinning answers "are these the bytes the user consented to run", not "are these bytes safe".

**Install approximately zero plugins you have not read or do not trust**, exactly as you would
treat any other program you download and run.

### Bundled plugins are trusted as much as the executable

A release ships its plugins in `Plugins\` and does not ask you to approve them. The release
workflow hashes each plugin folder with the same manifest the loader uses and compiles the list
of directory names and hashes into `Dongled.Core.dll`. At startup, a directory whose name **and**
whole-folder hash both match an entry loads without a configuration entry.

This adds no new trust. The list is part of the host's own binaries, so anyone who can change it
can equally change the loader that reads it, or `Dongled.exe`. Trusting the bundled plugins is
the same decision as trusting the release you downloaded, and `SHA256SUMS` covers both.

What it does not do:

- It does not extend to anything else in `Plugins\`. A plugin you add yourself needs approval as
  before.
- It does not survive a change. One file added, changed or removed in a bundled plugin's folder
  changes its hash, and that plugin then needs approval like any other. The same files under a
  different folder name are not trusted either.
- It does not override you. Switching a bundled plugin off records an entry that keeps it off,
  whatever the list says.
- A build from source compiles in an empty list, so there every plugin needs approval.

Each plugin keeps its own version, and the release fails if a plugin's files change without a
version bump, so a given plugin version always means the same bytes.

### Installing a plugin is not trusting it

The Plugins page can install a plugin from a `.zip` and remove one, which means the app writes
to the directory it loads code from. That is a deliberate decision, recorded with its reasoning
in [`docs/decisions/0002-plugins-directory-is-writable.md`](docs/decisions/0002-plugins-directory-is-writable.md).

The rule that makes it safe is that **installing and approving are two separate acts, and only
the second grants anything**:

- The installer never writes an approval hash. Only the approval dialog does.
- Replacing an installed plugin clears the approval that covered the old files. Different bytes
  never run under consent given to other bytes.
- Removing a plugin forgets its entry entirely rather than disabling it, so a folder of the
  same name dropped in later cannot match a hash you approved for files you deleted.
- The hash you are shown and the hash recorded are both recomputed at the destination after the
  files land, not carried over from the package.

A plugin the app extracted for you is exactly as unapproved as one you copied in with Explorer.

### Local IPC is unauthenticated

The bundled plugins talk to vendor software over loopback. The app **only connects outward and
never listens**, so nothing here opens a port on your machine. But the vendor protocols have no
authentication, and neither the app nor the vendor can tell a real agent from an impostor:

- The Logitech G HUB plugin connects to `ws://localhost:9010/`. The URL is overridable through
  `%APPDATA%\Dongled\logitech-ghub.json`, and the plugin **validates that the
  override is a websocket address on the loopback interface** and refuses anything else, so the
  setting cannot be turned into a route off the machine. That file lives in the app's data
  folder rather than beside the plugin, because a writable file inside a hash-pinned directory
  would re-block the plugin on every write and would itself be a tampering surface.
- The HyperX HID plugin reads a USB HID device (VID `0x03F0`, PID `0x06BE`) directly. Any
  process able to open the same device can be a source of the same reports.

A local process that squats one of these endpoints first can feed false presence data. The
worst case is that it flips your default audio device — an annoyance and a possible
eavesdropping-adjacent nuisance if it points audio at a device you did not intend. It cannot
gain privileges through this path. There is no mitigation available without vendor-side
authentication, which does not exist, so it is documented rather than claimed fixed.

### `IPolicyConfig` is undocumented

Windows exposes no public API for setting the default audio endpoint. Every application that
does this, including this one, calls `IPolicyConfig`, an undocumented COM interface. This is a
**compatibility** risk rather than a security one: a Windows update may change or remove it, and
the app would stop being able to switch devices. The interface is declared with correct vtable
signatures, and the ten reserved slots the app does not use are declared so that calling one is
impossible rather than stack-corrupting.

### No code signing

Release artifacts are **unsigned**. There is no certificate. SmartScreen will warn on first run
and Windows cannot show you a publisher. Verify the `SHA256SUMS` file published with each
release against your download; that tells you the bytes are the ones the release workflow
built, which is the strongest guarantee currently on offer.

### The in-app updater trusts whoever controls the GitHub release

When you click Update, the app downloads the zip for its own flavor and the release's
`SHA256SUMS` from this repository's GitHub releases over HTTPS, refuses the zip if its hash does
not match, and then replaces its own files. That check catches a truncated or corrupted
download. It does **not** protect against a compromised GitHub account or release workflow:
whoever can publish a release can publish a matching `SHA256SUMS` beside it, and because
releases are unsigned there is nothing independent to check either against. The updater never
runs without your click, never touches `%APPDATA%\Dongled\` or a plugin folder you added
yourself, and refuses to run when it cannot write to its own folder rather than asking for
administrator.

### No plugin sandboxing beyond what is stated

There is no AppContainer, no separate process, no permission model for plugins. This is
out of scope by decision, not an oversight pending a fix.

## What the app deliberately does not do

- **It never requires administrator.** No code path requests elevation, and there is no UAC
  prompt anywhere in the application.
- **It writes nothing machine-wide.** Autorun is `HKCU` only. There is deliberately no
  `HKLM` autorun option: an `HKLM` autorun pointing at a binary in a user-writable location is a
  local privilege-escalation primitive, so the capability does not exist rather than being
  guarded.
- **It has no command-line interface**, so there is no argument handling to abuse.
- **It makes one kind of network request off the machine, and only when you open the window.**
  An official release asks `https://api.github.com/repos/navrozashvili/Dongled/releases/latest`
  whether a newer version exists: unauthenticated, with nothing in it beyond a User-Agent naming
  the app and its version, at most once every six hours (hourly after a failure), and only when
  you open the window — never on a timer and never while it sits in the tray. Turn it off in
  Settings; a build from source never makes it at all. A download happens only when you click
  Update. There is no telemetry and no analytics. Every other socket is one of the loopback
  connections described above.
- **It runs one instance**, guarded by a named mutex, so two copies cannot fight over the
  default device and the state file.
- **It does not enumerate or load code by scanning running processes.** Plugins are discovered
  only in their own directories, and each declares exactly one entry-point type through an
  assembly-level attribute — no constructor runs during discovery.

## How a plugin is actually loaded

Every step below fails closed. Any exception anywhere in the sequence means the plugin is
blocked, logged, and shown as blocked in the UI.

1. The directory must contain exactly one `Dongled.Plugin.*.dll`. Zero or several
   is not a candidate.
2. That file is opened once with `FileShare.Read`, denying writers, and the handle is held for
   the whole decision.
3. It must be a managed assembly, and its referenced `Dongled.Abstractions` version
   is read from that handle.
4. A manifest hash is computed over the **whole directory** — SHA-256 over the sorted list of
   (relative path, file hash) pairs. Hashing only the main DLL would leave its dependencies
   beside it unpinned and equally executable.
5. Config is consulted. An entry that is switched off means *never loaded*. Otherwise the
   directory is trusted only if its name and manifest hash match a plugin this release shipped,
   or its entry records exactly this manifest hash. No entry, or a hash mismatch, with no match
   in the shipped list, means *never loaded*.
6. The referenced Abstractions version must be compatible with the host's.
7. Only then is the assembly loaded, from the same handle, into a collectible load context.

Because the file is hashed and loaded from one held handle, there is no window in which the
file can be swapped between the two. Dependencies are resolved from the directory that was
hashed immediately beforehand.

## Supply chain

- Package versions are pinned centrally in `Directory.Packages.props`.
- `packages.lock.json` is committed for every project and CI restores with `--locked-mode`, so
  a dependency cannot change without a visible diff.
- `NuGet.config` clears all inherited sources and permits only nuget.org, with package source
  mapping.
- Builds are deterministic, with `TreatWarningsAsErrors`, .NET analyzers at `latest-all`, and
  nullable reference types enabled. `global.json` pins the exact .NET SDK, and CI uses it.
- Releases are built only by the CI workflow, from a commit on `master` that passed every gate
  above. Only the release job has write access to the repository. A shipped plugin's folder
  builds to the same bytes from any later commit that leaves its source unchanged; the release
  compares every plugin's hash with the previous release's and fails if one changed without a
  version bump.
- CI fails the build on any known vulnerability in the dependency tree, direct or transitive.
  The check fails loudly and distinctly if the scanner itself could not run, because a scanner
  that crashed is not a clean scan.

The two unported plugins under `legacy/` are archived source. They are outside the solution and
outside every build, restore, and CI job. One of them references a package whose transitive
closure carries known advisories; keeping it out of the solution is precisely what keeps those
out of the product. See [`legacy/plugins/README.md`](legacy/plugins/README.md).

## Where the app keeps your data

All under `%APPDATA%\Dongled\`, per-user, never machine-wide:

| Path | Contents |
|---|---|
| `config.json` | Settings, rules, and plugin approvals |
| `state.json` | Captured previous default devices |
| `window.json` | Window size and position |
| `logs\` | Rolling log files |
| `staging\` | Plugin install scratch space |

Plugins live in `Plugins\` beside the executable, because a plugin's dependencies have to be
somewhere the load context can resolve them from.

The **Copy for bug report** button on the Logs page strips your username from paths before
copying. Log files themselves are not sanitized — check one before attaching it to an issue.
