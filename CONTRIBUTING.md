# Contributing

Thanks for helping improve Media Toolbox.

## Before opening a change

1. Keep the five core tools working and preserve the dark/light theme behavior.
2. Keep FFmpeg argument construction, validation, temporary files, and output
   naming in `MediaProcessor`, not in the WPF controls.
3. Keep job settings immutable and pass progress/cancellation through the
   media service API.
4. Build and run the smoke checks:

   ```powershell
   dotnet build MediaToolbox.Wpf\MediaToolbox.Wpf.csproj -c Release
   dotnet run --project MediaToolbox.Wpf.SmokeTests\MediaToolbox.Smoke.csproj -c Release
   ```

5. Do not commit local FFmpeg executables, generated media, `bin`/`obj`
   folders, or published app folders.

For bug reports, include concise reproduction steps and relevant error output.
Do not attach private videos or audio; synthetic samples are preferred.
