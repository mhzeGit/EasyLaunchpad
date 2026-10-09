# Agent instructions

- After finishing implementation work, run `build.bat` from the repository root. It cleans generated build output, publishes the Release `win-x64` app to `dist`, and launches `dist\Launchpad.exe`.
- Do not substitute a plain `dotnet build` for this project workflow. Use `build.bat test` when tests are explicitly requested, and keep `nolaunch` off when the requested workflow includes running the app.
- `build.bat` stops running `Launchpad.exe` processes and recreates generated `dist`, `bin`, and `obj` output. Preserve source files and release artifacts outside those generated directories.
