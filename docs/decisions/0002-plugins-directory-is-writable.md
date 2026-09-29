# 2. The plugins directory is written by the app, not only read

**Date:** 2026-07-31
**Status:** Accepted
**Context:** Adding *Add plugin…* and *Remove* to the Plugins page.

## Decision

`StoragePaths.PluginsDirectory` is written by the app. The Plugins page extracts a user-chosen
`.zip` into it and deletes folders out of it.

`StoragePaths.StagingDirectory`, under the per-user data folder, is scratch space for extracting a
package. Everything in it is disposable, and `PluginInstaller.SweepStaging` empties it at every
start.

## The concern this answers

The obvious objection: *an app that writes to the place it loads code from is one bug away from
loading what it wrote.*

The concern is real. But *not writing* is not what answers it — approval is, and approval is
untouched by this decision.

## Why the trade is worth taking

1. **The directory is already assumed user-writable elsewhere in this codebase.**
   `Tray/NativeMethods.cs` pins its DLL search path to System32 explicitly, because "a planted
   `user32.dll` beside a user-writable install would be loaded ahead of the real one." The threat
   model already includes an attacker who can write next to the executable. This feature does not
   introduce that attacker, and an attacker who has it does not need this feature.
2. **The app writes no code it did not receive from the user in that moment.** Copying a zip the
   user picked is the same act as the user copying it in Explorer, performed with the same
   privileges and no more.
3. **Approval is unchanged and still gates loading.** `PluginManifest.Compute` hashes the whole
   directory before anything loads, and an unapproved or altered directory is refused. Extracting
   files changes nothing about whether they may run.

## The rule this must not break

**The write path must never become a way around the trust path.** Installing leaves a plugin
exactly as untrusted as one a user copied in by hand.

Four things enforce it, and they are the parts of the design to preserve if any of this is
refactored:

- **Nothing the installer writes grants trust.** Only the approval dialog writes
  `PluginConfig.ManifestSha256`. `PluginInstaller` never does.
- **Replacing clears the approval that covered the old files.** `ClearRecordedApproval` sets
  `Enabled = false`, nulls the hash and the timestamp, and saves. Carrying an approval across a
  replace would be precisely the bypass — different bytes running under consent the user gave to
  other bytes.
- **Removal forgets the entry entirely rather than disabling it.** A record that outlived its files
  would let a folder of the same name dropped in later match a hash the user approved for files
  they deliberately deleted.
- **The hash the user is shown and the hash recorded are computed at the destination**, not
  carried over from the package, so approval covers what actually landed.

Installing and approving stay two separate acts, and the approval dialog is the same dialog with
the same words either route reaches it by — built by one shared function rather than two copies
that would stay identical only until someone edited one of them.

## Consequences

- `Plugins\` is no longer safe to describe as read-only application content. Anything reasoning
  about it — a future installer, a packaging step, an integrity check over the install directory —
  has to account for it changing while the app runs.
- Loading stays startup-only, so an installed plugin is inert until the next start. The UI says so
  rather than implying a restart is optional.
- Replacing or removing a **loaded** plugin is refused while the app is running.
  `AssemblyDependencyResolver` resolves dependencies from disk and Windows holds them open, so a
  delete would tear partway and leave a folder that is neither version.
- An install is not transactional. Replacing a plugin deletes the old folder and then moves the
  new one in; if the move fails, the old version is gone and the user adds the zip again.
  Set-aside, verify and rollback machinery is more than a user-picked zip warrants — the loader's
  hash check, not the installer, is what stops a torn or altered folder from running.
- Zip validation is `ZipFile.ExtractToDirectory`, which refuses entries that would land outside
  the destination. There are no size, entry-count or depth limits: the user chose the file, and
  anything in it runs with their privileges once approved anyway.
