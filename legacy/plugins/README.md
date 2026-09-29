# Unported plugins

These two plugins were written for an earlier WinForms version of the app and have not been
ported to the current provider SDK. They are kept here so a port has something to port *from*.

Everything in this directory still carries the project's old name, **AudioSourceSwitcher**.
That is deliberate: this is archived source, and it is what these files actually were. Only
the references to live paths below were updated when the tool became `dongled`.

| Plugin | Vendor surface | Why it was not ported |
|---|---|---|
| `AudioSourceSwitcher.Plugin.CorsairIcue` | iCUE SDK, native `iCUESDK.dll` | iCUE is not installed on the development machine. A port would be evidenced by nothing but a clean build. |
| `AudioSourceSwitcher.Plugin.HyperX` | NGENUITY, vendor reflection over NetMQ IPC | Not installed; 793 lines of reflection over an undocumented vendor protocol. |

A port has to move to the current SDK's shape: source descriptors, the async start/stop
lifecycle of `IAudioSourceProvider`, and the same hardening the bundled plugins apply to
anything they read from vendor software.

## They do not build, and that is deliberate

- They are **not in `Dongled.slnx`**, so no solution build, test run, format check
  or vulnerability gate touches them. CI never restores them.
- They target `net8.0` and `ProjectReference` the old `AudioSourceSwitcher.Abstractions`,
  which no longer exists. Opening one in an IDE will fail to restore. This is an archive of
  source, not a buildable project.
- `AudioSourceSwitcher.Plugin.HyperX` pulls `NetMQ 4.0.1.13`, whose transitive closure carries
  three known advisories (`System.Drawing.Common` 5.0.0 Critical, `System.Formats.Asn1` 5.0.0
  High, `System.Security.Cryptography.Xml` 5.0.0 Moderate). Keeping the project out of the
  solution is what keeps those out of the product. The measured upgrade path — `NetMQ 4.0.4.2`
  with `System.Security.Cryptography.Xml` and `.Pkcs` pinned to `10.0.10` — belongs to whoever
  does the port.
- Their `CopyPluginToHostPluginsDir` targets still copy into the deleted WinForms output
  directory, and their `packages.lock.json` files are the original ones. Both are left as they
  were rather than half-corrected into something that looks maintained.

## Porting one

Start from `plugins/Dongled.Plugin.HyperXHid` or
`plugins/Dongled.Plugin.Logitech` — a ported plugin is the shortest description of
the target shape — then move the ported project into `plugins/`, add it to the `/plugins/`
folder in `Dongled.slnx`, and delete the copy here.

Rename as you go: a ported plugin is `Dongled.Plugin.CorsairIcue`, because the loader only
considers `Dongled.Plugin.*.dll` and the SDK it must reference is now `Dongled.Abstractions`.
