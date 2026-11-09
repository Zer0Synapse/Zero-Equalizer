# Zero Equalizer

Zero Equalizer is a Windows desktop app for adjusting Bass, Treble, and ten frequency bands on a playback device. It includes built-in and custom presets, a response graph, a manual preamp, and a bypass button. Each output remembers its own settings.

The preamp starts at 0 dB. Adjust it to set the overall EQ level. Tone changes and presets preserve that level. The response graph indicates peak boosts so you can adjust gain if you hear clipping.

## Get started

1. Extract the Windows release ZIP and open `ZeroEqualizer.exe`. The app requires Windows 10 or 11 and the .NET 8 Desktop Runtime.
2. Open **Audio setup** and choose **Install Equalizer APO**. The app downloads and verifies the official installer for your system. In Device Selector, enable its post-mix effect on the playback device you use. Keep Windows audio enhancements enabled for that output and restart if setup prompts you. [Equalizer APO installation and configuration](https://sourceforge.net/p/equalizerapo/wiki/Documentation/)
3. Back in Zero Equalizer, choose the output and click **Connect EQ**. Try **Bass Boost** and toggle **Bypass** while listening. **Disconnect EQ** removes Zero Equalizer's filters from the Equalizer APO configuration.

The app uses Equalizer APO as a separate audio engine. Its effects apply to apps sharing the connected Windows output. ASIO and WASAPI exclusive playback may bypass them. Equalizer APO's installer configures Windows to load unsigned audio effects. [Equalizer APO developer documentation](https://sourceforge.net/p/equalizerapo/wiki/Developer%20documentation/)

## Build from source

Install the .NET 8 SDK on Windows, then run these commands from the project folder.

```powershell
dotnet run --project .\ZeroEqualizer.csproj
.\Build-Release.ps1
```

The build script creates a portable Windows ZIP and a source ZIP in `release`. It reads their version from `ZeroEqualizer.csproj`. Equalizer APO is a separate installation.
