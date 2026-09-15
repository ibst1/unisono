# Unisono

Two-way sync between Google Drive folders and local folders, for machines where Google's
own Drive client cannot be installed (it needs admin for its driver and shell extension).
Unisono talks to the Drive v3 API directly over HTTPS and runs entirely under the user's
profile: no installer, no driver, no elevation.

Status: v0.1. Two-way sync of files and folders, Google Docs as exports or links,
cloud-only files as links, a console daemon with a file watcher. A tray app and autostart
are next.

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
2. Enable **Google Drive API** (search for it at the top, then *Enable*).
3. **Google Auth Platform > Branding**: app name "Unisono", your email as support and
   developer contact.
4. **Google Auth Platform > Audience**: if the project is in a Google Workspace
   organisation and you sync that account's Drive, leave it *Internal*: no verification,
   no warning screen, tokens never expire. Otherwise choose *External* and click
   *Publish app* (skip verification). In *Testing* status refresh tokens die after seven
   days, which means signing in again every week on every machine.
5. **Google Auth Platform > Clients > Create client**, application type *Desktop app*,
   name "Unisono". Download the JSON.
6. Save it as `%LOCALAPPDATA%\Unisono\client_secret.json` on each machine.

On first `unisono auth` a browser opens. An *External* unverified app shows "Google hasn't
verified this app": click *Advanced* and *Go to Unisono (unsafe)*. That is your own client
id and your own account, so the warning is expected. The refresh token is then stored in
`%LOCALAPPDATA%\Unisono\tokens` and sign-in is silent from then on. `auth` prints which
client id and project it is using, so a wrong JSON is obvious before the browser opens.

## Usage

```
unisono auth                                  sign in and show account, quota and the changes token
unisono init <Drive/Path> <localDir> [--create]  add a sync pair (--create makes the Drive folder)
unisono sync [-v]                             one sync cycle for every pair
unisono run [-v]                              keep syncing: watch local folders, poll Drive
unisono config                                show config path and pairs
unisono ls [Drive/Path]                       list a Drive folder
unisono pull <Drive/Path> <localDir>          one-way mirror Drive -> local, never deletes
unisono changes <token>                       raw Drive change feed (diagnostic)
unisono version
```

Settings live in `%LOCALAPPDATA%\Unisono\config.json`, one object per pair:

```json
{
  "Pairs": [
    {
      "DrivePath": "Projects",
      "LocalPath": "C:\\Users\\me\\Projects",
      "GoogleDocs": "Export",
      "CloudOnlyMinMB": 0,
      "CloudOnlyFolders": ["Archive"],
      "PollSeconds": 30,
      "FullRescanMinutes": 15
    }
  ]
}
```

* **GoogleDocs**: `Export` writes Docs/Sheets/Slides/Drawings as docx/xlsx/pptx/png,
  `Link` writes a `.url` shortcut that opens the document on Drive, `Skip` ignores them.
  Exports are one-way and marked read-only: a local edit is reported in the log and never
  uploaded, and the file is re-exported when the document changes on Drive. Drive caps
  exports at 10 MB.
* **CloudOnlyMinMB** / **CloudOnlyFolders**: matching files stay on Drive and appear
  locally as `Name.ext.url`, which Explorer shows as "Name.ext" with a link icon; double
  click opens the file on Drive. Put a real file with that name in the folder and it
  becomes a normally synced file (the link is removed). Cloud-only files can only be
  deleted on Drive.
* **PollSeconds**: how often Drive is asked for changes when nothing happens locally.
  Local changes are picked up by a file watcher within a few seconds regardless.
* **FullRescanMinutes**: the change feed is eventually consistent (a folder trash was seen
  to take minutes to appear), so the whole folder is re-listed this often as a safety net.

## How it syncs

Each cycle compares three things per path: what Drive has (an id-keyed index kept current
with `changes.list`, so nothing is re-listed), what the disk has (a fresh stat walk; md5 is
only computed when size or mtime moved) and what both looked like the last time they agreed
(the state file under `%LOCALAPPDATA%\Unisono\state`). That third leg is what tells a
deletion from a never-seen file, and a local edit from a remote one.

Rules:

* Deletes go to the Drive trash and the Recycle Bin, never permanent.
* A folder is only deleted on the other side when nothing inside it changed since the
  last sync; otherwise it is kept and re-created.
* When both sides changed a file, both are kept: the local version is renamed
  `Name (conflict MACHINE yyyy-MM-dd HHmm).ext` and uploaded, Drive's version takes the
  original name.
* A file that exists on both sides with no history (first sync of a pre-existing folder)
  is adopted if the content is identical, otherwise treated as a conflict.
* Two machines syncing the same Drive folder need no coordination; Drive is the hub and
  each machine has its own state.

Limitations: renames are seen as delete plus add (correct, but re-transfers the data);
Drive allows two siblings with the same name or names differing only in case, Windows
does not, so the first one wins and the rest are reported; Docs exports have no md5, so
change detection for them uses Drive's modified time.

## Building

`build.ps1` publishes a self-contained single-file exe to `%USERPROFILE%\Program\Unisono`
and to `dist\`. It uses a user-local SDK from `%LOCALAPPDATA%\dotnet` when present.
Set `UNISONO_DATA` to redirect config, state and logs (sign-in stays in
`%LOCALAPPDATA%\Unisono`), which is how the end-to-end test runs two pairs against one
Drive folder on a single machine.
