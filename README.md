# Zero Equalizer

Zero Equalizer is a Windows desktop app for adjusting Bass, Treble, and ten frequency bands on a playback device. It includes built-in and custom presets, a response graph, a manual preamp, and a bypass button. Each output remembers its own settings.

The preamp starts at 0 dB. Tone changes and presets preserve that level. If boosted peaks crackle, click **Reduce clipping** to lower the preamp with estimated headroom for your current curve. EQ changes apply after you release a dragged slider.

## Get started

1. Extract the Windows release ZIP and open `ZeroEqualizer.exe`. The app requires Windows 10 or 11 and the .NET 8 Desktop Runtime.
2. Open **Audio setup** and choose **Install Equalizer APO**. The app downloads and verifies the official installer for your system. In Device Selector, enable its post-mix effect on the playback device you use. Keep Windows audio enhancements enabled for that output and restart if setup prompts you. [Equalizer APO installation and configuration](https://sourceforge.net/p/equalizerapo/wiki/Documentation/)
3. Back in Zero Equalizer, choose the output and click **Connect EQ**. Try **Bass Boost** and toggle **Bypass** while listening. **Disconnect EQ** removes Zero Equalizer's filters from the Equalizer APO configuration.

To process Spotify or another app, choose **Routed app audio** and select your headphones or speakers as the playback output. This mode uses an installed [VB-CABLE](https://vb-audio.com/Cable/). Keep your regular Windows output selected as the default. Disconnect Zero Equalizer's system EQ if prompted, then click **Start app EQ**. While the chosen app is playing, open **Windows volume mixer** and set its output to the selected cable input. Zero Equalizer plays the cable's audio through the selected output and applies EQ to that route. Only send the apps you want to process to that cable. Set those apps' output back to **Default** when you stop using the route.

Version 1.3.0 and later check this project's GitHub Releases on startup and verify the new Windows package before opening it. Download 1.3.0 manually once when upgrading from an earlier version.

The app uses Equalizer APO as a separate audio engine. Its effects apply to apps sharing the connected Windows output. ASIO and WASAPI exclusive playback may bypass them. Equalizer APO's installer configures Windows to load unsigned audio effects. [Equalizer APO developer documentation](https://sourceforge.net/p/equalizerapo/wiki/Developer%20documentation/)

## Build from source

Install the .NET 8 SDK on Windows, then run these commands from the project folder.

```powershell
dotnet run --project .\ZeroEqualizer.csproj
.\Build-Release.ps1
```

The build script creates a portable Windows ZIP, a source ZIP, and `update.json` in `release`. Attach the portable ZIP and `update.json` to the matching `v` tag on GitHub. The manifest describes the exact ZIP created by that build.
