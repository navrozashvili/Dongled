### AudioSourceSwitcher Corsair iCUE Plugin

This plugin uses the **Corsair iCUE SDK** to detect **Corsair headset presence** and emits `ConnectedOrReady` / `Disconnected` events to AudioSourceSwitcher.

#### Install / Deploy

- **Build**: build the solution; the project copies its DLL into:
  - `AudioSourceSwitcher/bin/<Configuration>/net8.0-windows/Plugins/AudioSourceSwitcher.Plugin.CorsairIcue.dll`
- **Native SDK DLL**: place the iCUE SDK native DLL next to the plugin in one of these locations:
  - `Plugins/x64/iCUESDK.dll` (recommended for x64)
  - `Plugins/x64/CUESDK.dll`
  - `Plugins/iCUESDK.dll`
  - `Plugins/CUESDK.dll`

You can also set an explicit path:

- `AUDIO_SOURCE_SWITCHER_CORSAIR_SDK_DLL` = full path to `iCUESDK.dll` / `CUESDK.dll`

#### Logical device IDs emitted

- **Any Corsair headset**: `corsair:headset:any`
- **By model**: `corsair:headset:model:<slug>`
- **By device id**: `corsair:headset:id:<id>`

Use these strings as `LogicalDeviceId` in the app Settings mappings.

#### Notes

- iCUE must be running and **SDK / third-party control** must be enabled in iCUE settings.
- The plugin currently polls every ~2 seconds.











