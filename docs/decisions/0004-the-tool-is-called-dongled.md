# 4. The tool is called dongled

**Date:** 2026-08-03
**Status:** Accepted
**Context:** Renaming before the first public release, and moving to a new repository.

## Decision

The project is **dongled** — the daemon that watches your dongle. Everything that carries an
identity takes that name:

| | Before | After |
|---|---|---|
| Namespace root, projects | `AudioSourceSwitcher.*` | `Dongled.*` |
| Shipped binary | `AudioSourceSwitcher.App.exe` | `Dongled.exe` |
| Plugin discovery pattern | `AudioSourceSwitcher.Plugin.*.dll` | `Dongled.Plugin.*.dll` |
| Plugin SDK (pinned by the loader) | `AudioSourceSwitcher.Abstractions` | `Dongled.Abstractions` |
| Per-user data | `%APPDATA%\AudioSourceSwitcher\` | `%APPDATA%\Dongled\` |
| Redirect variable | `AUDIOSOURCESWITCHER_DATA_DIR` | `DONGLED_DATA_DIR` |
| Instance handshake variable | `AUDIOSOURCESWITCHER_AWAIT_INSTANCE` | `DONGLED_AWAIT_INSTANCE` |
| Single-instance mutex | `Local\AudioSourceSwitcher.SingleInstance` | `Local\Dongled.SingleInstance` |
| `HKCU\...\Run` value | `AudioSourceSwitcher` | `Dongled` |

`Dongled.exe` rather than `Dongled.App.exe`: the project keeps the `.App` suffix to distinguish
it from `Dongled.Core`, but that is an internal distinction and there is no reason to make a
user read it in Task Manager. `AppProjectReferenceTests` asserts the divergence so a future
rename cannot silently drop the override.

## Why rename at all

The old name described one of the two things the app does. Battery reporting arrived after the
name did and was never covered by it, and no amount of qualifying fixed that.

The deeper problem was register. `AudioSourceSwitcher` reads like a product — something with a
landing page and a version-comparison table. This is a utility one person wrote to fix one
annoyance, and at most a handful of people will ever run it. A tool name says that honestly.

It also dissolves the battery objection rather than answering it. Tools are named after their
main verb: `htop` does not mention that it shows memory, `rsync` does not mention SSH. Nobody
reads a tool name as a feature list, so the name no longer owes battery a mention.

## Why now, specifically

**The project name is part of the plugin ABI.** `PluginLoader` and `PluginPackageReader` accept
exactly one `Dongled.Plugin.*.dll` per directory, and `PluginLoadContext` pins
`Dongled.Abstractions` to the default load context so plugin and host agree on type identity. A
third-party plugin therefore has to be *named* after this project and has to reference an
assembly named after it.

Renaming before the first release costs one mechanical commit. Renaming after it is a breaking
change for every plugin author, and — because approval hashes cover file names — silently
re-blocks every installed plugin for every user. This was the last moment it was free.

## What was not renamed, and why

- **`legacy/`** keeps `AudioSourceSwitcher.Plugin.*` throughout. It is archived source from an
  earlier version, and those are the names those files actually had. Renaming them would make
  the archive claim to be something it was not. Only its README's references to *live* paths
  were updated.

A reader who meets the old name there is one link from this file.

## Why a new repository

The history came with it — the new repository is the old one's `.git`, so all 63 commits, both
branches and the `pre-refactor` tag are intact, and every rename was made with `git mv` so
`--follow` still works through it.

A rename of the existing GitHub repository would have preserved the same things and installed a
redirect. Starting a new repository was chosen anyway: the old one had never been published, so
there was nothing to redirect and no stars, forks or issues to carry, and a clean repository
under the new name avoids explaining a rename to everyone who ever clones it. The old
repository stays where it is, to be archived.

## Consequences

- **Existing local configuration is orphaned.** `%APPDATA%\AudioSourceSwitcher\` is not read
  and not migrated. The app carries no migration code, and with no released users there
  is nothing to migrate but a developer's own scratch config. Anyone who wants theirs back
  copies the folder.
- **A previously registered autorun entry is orphaned too.** The old `HKCU\...\Run` value is
  named `AudioSourceSwitcher` and points at a path that no longer exists. Nothing removes it;
  turn *Start with Windows* off in the old build first, or delete the value by hand.
- **Any plugin built against the old SDK will not load**, by design. It references
  `AudioSourceSwitcher.Abstractions`, which the loader no longer pins and no longer supplies, so
  it fails the assembly-identity check rather than loading into a split type graph.
