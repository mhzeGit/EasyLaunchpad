# Agent instructions

- After finishing implementation work, run `build.bat` from the repository root. It cleans generated build output, publishes the Release `win-x64` app to `dist`, and launches `dist\Launchpad.exe`.
- Do not substitute a plain `dotnet build` for this project workflow. Use `build.bat test` when tests are explicitly requested, and keep `nolaunch` off when the requested workflow includes running the app.
- `build.bat` stops running `Launchpad.exe` processes and recreates generated `dist`, `bin`, and `obj` output. Preserve source files and release artifacts outside those generated directories.

## Keep the GitHub build current

The repository is `https://github.com/mhzeGit/EasyLaunchpad` (branch `main`). Source lives in git; builds live only on the GitHub Release `v<version>` as a single zip that is replaced on every publish. `release\` is git-ignored: never commit build output (zips, DLLs, EXEs) to the repository.

Every change that affects the shipped app (anything under `src\`, build scripts, resources) is not finished until the GitHub Release holds a build of it:

1. Run `build.bat` as above for the local run.
2. Run `.\publish-release.bat nopause` from the repository root (cmd does not search the current directory, so keep the `.\`). It runs the tests, builds `release\EasyLaunchpad-<version>-win-x64.zip`, and uploads it to the GitHub Release, replacing the previous file. It aborts without publishing if the tests or the build fail. Do not pass `notest` unless the user asks.
3. Commit your source changes and push to `origin main`.
   - Stage explicit paths only (never `git add -A` / `git add .`): the working tree may hold someone else's in-progress edits and the index may already hold staged changes. Commit with `git commit -m "<message>" -- <paths>` so only those paths are committed.
   - The release zip is built from the working tree. If there are unrelated uncommitted changes, say so in your summary.
4. If `publish-release.bat` fails because GitHub sign-in is missing, do not try to log in, look for tokens, or work around it. Finish step 3 and tell the user to double-click `publish-release.bat` once to sign in; the Release is out of date until then.
5. Changes that do not alter the app (README, tests only, this file) do not need steps 1-2, but still commit and push them.
6. The version lives in `VERSION` inside `build-release.bat`. Do not bump it unless the user asks. When they do, bump it there; `publish-release.bat` then creates a new GitHub Release and tag for it instead of overwriting the old one.

In your final message, state the commit hash that was pushed and whether the GitHub Release was updated.
