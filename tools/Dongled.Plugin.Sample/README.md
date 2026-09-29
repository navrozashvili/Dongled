# Sample plugin

A minimal `IAudioSourceProvider`: one assembly-level `[AudioSourceProvider]` attribute, one
provider type, one imaginary source. It is the worked example for plugin authors,
and it is the real artifact the loader tests are pointed at, so it cannot drift out of
date without a test failing.

It references only `Dongled.Abstractions`. A plugin never references the host.

To try it: build this project, copy the whole output directory into a folder under the
application's `Plugins` directory, start the app, and approve it on the Plugins page. Approval
records a hash over every file in that folder, so rebuilding the plugin means approving it again.

## Reporting a battery

A provider that can see a battery calls `context.ReportBattery(sourceId, percent, charge)`.

Call it once as soon as you know the source *has* a battery — with `percent: null` and
`ChargeState.Unknown` if that is all you know — because that first call is what tells the host
the source is battery-capable. Without it, a device that is switched off does not appear on the
Battery page at all.

Report only what your device actually supports. If it announces charging on a transition and
your provider started after that transition, report `ChargeState.Unknown` rather than a guess.
The host will not second-guess you, and a wrong charging indicator is worse than an absent one.
Any latching, ageing, or smoothing your hardware needs belongs in your provider — the host
stores what it is told and infers nothing. See `HyperXChargingTracker` in the HyperX plugin for
a worked example.

Reporting a battery is entirely optional.
