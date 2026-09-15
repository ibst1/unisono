# Unisono

Two-way sync between a Google Drive folder and a local folder, for machines where
Google's own Drive client cannot be installed (it needs admin for its driver and shell
extension). Unisono talks to the Drive v3 API directly over HTTPS and runs entirely
under the user's profile: no installer, no driver, no elevation.

Status: v0.1, proving the API path. `auth`, `ls` and a one-way `pull` work. The sync
engine and the tray app come next.

## Requirements on the machine that runs it

* Windows 10/11, no admin needed.
* Outbound HTTPS to `www.googleapis.com`, `oauth2.googleapis.com` and `accounts.google.com`.
* Permission to run an unsigned exe from `%USERPROFILE%\Program\Unisono`.
* A loopback HTTP listener on 127.0.0.1 for the one-time OAuth redirect.

`tools\Probe-Environment.ps1` checks all of this and writes `tools\probe-<COMPUTERNAME>.txt`:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Probe-Environment.ps1
```

## One-time setup: your own OAuth client

Unisono has no built-in client id. Create one in your Google account (free, no admin,
takes a few minutes) and drop the JSON in `%LOCALAPPDATA%\Unisono\client_secret.json`.

1. Open <https://console.cloud.google.com/> and create a project, e.g. "Unisono".
2. **APIs & Services > Library**: enable **Google Drive API**.
3. **APIs & Services > OAuth consent screen**: User type *External*, app name "Unisono",
   your email as support and developer contact. Add the scope
   `https://www.googleapis.com/auth/drive`.
4. Set **Publishing status** to *In production* (click *Publish app*, skip verification).
   Leave it in *Testing* and refresh tokens expire after seven days, which means signing
   in again every week on every machine.
5. **APIs & Services > Credentials > Create credentials > OAuth client ID**, application
   type *Desktop app*, name "Unisono". Download the JSON.
6. Save it as `%LOCALAPPDATA%\Unisono\client_secret.json` on each machine.

On first `unisono auth` a browser opens. Because the app is unverified Google shows
"Google hasn't verified this app": click *Advanced* and *Go to Unisono (unsafe)*. That
is your own client id and your own account, so the warning is expected. The refresh token
is then stored in `%LOCALAPPDATA%\Unisono\tokens` and sign-in is silent from then on.

## Usage

```
unisono auth                         sign in and show account, quota and the changes token
unisono ls [Drive/Path]              list a Drive folder (My Drive when omitted)
unisono pull <Drive/Path> <localDir> mirror Drive -> local; adds and updates, never deletes
unisono version
```

Google-native files (Docs, Sheets, Slides) have no byte representation and are listed
as `<gdoc>` and skipped by `pull`.

## Building

`build.ps1` publishes a self-contained single-file exe to `%USERPROFILE%\Program\Unisono`
and to `dist\`. It uses a user-local SDK from `%LOCALAPPDATA%\dotnet` when present.

## Planned design

* Remote changes via `changes.list` with a stored page token (cheap polling, no rescans).
* Local changes via `FileSystemWatcher` plus a periodic full scan as safety net.
* A per-machine state database (Drive id, md5, mtime, size per path) so that local,
  remote and last-synced state can be compared three ways: add, update, delete and
  conflict are all distinguishable.
* Conflicts keep both copies, the loser renamed with the machine name and a timestamp.
* Deletes go to the Drive trash and the local Recycle Bin, never permanent.
* Tray app with status, pause and a log.
