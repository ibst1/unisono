# Unisono

Two-way sync between Google Drive folders and local folders, for machines where Google's
own Drive client cannot be installed (it needs admin for its driver and shell extension).
Unisono talks to the Drive v3 API directly over HTTPS and runs entirely under the user's
profile: no installer, no driver, no elevation.

Status: v0.2. Tray app (`Unisono.exe`) and command line (`UnisonoCli.exe`) over one
sync engine. Two-way sync with Google Drive or a Synology NAS, Google Docs as exports or
links, cloud-only files as links, file watcher, autostart.

## The app

Start `%USERPROFILE%\Program\Unisono\Unisono.exe`. The window is a WebView2 page with four tabs:

* **Accounts**: the OAuth client JSON, any number of Google accounts (each signs in once in
  the browser, with Google's account picker), and any number of Synology DSM logins (several
  users, several NAS). A NAS login asks for a two-factor code when needed and shows untrusted
  certificates with their SHA-256 so exactly those can be trusted.
* **Sync pairs**: add, edit and remove pairs with live status. A pair is a remote folder (a
  Google account's My Drive, all of it or a folder, or a NAS folder or whole shared folder)
  and a local folder. **Choose folders...** limits the pair to some subfolders: either *all
  except unchecked* (new folders are synced) or *only checked* (new folders are ignored).
  Folders already synced by another pair are marked in that picker.
* **Settings**: start with Windows, pause, detailed log, data and log folders.
* **Log**: live log with filters (changes, conflicts, warnings and errors) and text search;
  clicking a change shows the file in Explorer.

Closing the window keeps syncing in the tray; the icon is green (up to date), blue (syncing),
amber (paused) or red (problems). The window remembers its position and size. Only one
Unisono may sync at a time; `UnisonoCli sync/run` refuses while the app runs. Set
`UNISONO_DEVTOOLS=1` for the page's developer tools.

### Selective sync and nested pairs

What a pair leaves out is invisible to it and never touched on either side. A folder that is
only partly synced (it holds an included folder, or an excluded one, somewhere below) is never
deleted as a whole: deleting it on one side deletes its in-scope content on the other side
item by item. Example: 15 folders in My Drive, 14 to `D:\Work`, 1 to `%APPDATA%\Tool`: pair
one is *My Drive, all except* that folder; pair two is that folder alone.

Pairs may nest: a Drive pair's local folder can lie inside a NAS pair's local folder. The outer
pair then leaves that subfolder alone locally and remotely, so every file belongs to one pair.

### Safety and control

* **Previous versions**: before Unisono replaces a local file with a newer remote one, it keeps a
  copy under `versions\<pair>\<path>\` in the data folder (default 5 copies, 30 days; Settings).
  Deletions go to the Recycle Bin or the remote trash, conflicts keep both files.
* **Ignore rules**: gitignore-style patterns in Settings (all pairs), per pair, and in
  `.unisonoignore` files, which apply to their folder and below and sync to other computers.
  `*.log`, `__pycache__/`, `renv/library/`, `!keep.log`, `/only-at-this-level`. Ignored items
  are never touched on either side; a folder holding ignored items is never deleted as a whole.
* **Direction** per pair: two-way, *download only* (remote changes, deletions included, come
  here; nothing here goes up or is undone) or *upload only* (the reverse). In a one-way pair
  the source side wins where both changed; the replaced local file is kept as a version.
* **Preview** on a pair (or `UnisonoCli sync --dry-run`) shows what the next sync would do
  and changes nothing.
* **Automatic pause** on metered connections (on by default) and, optionally, during
  presentations and full-screen apps. "Sync now" still runs a round.
### Explorer menu

Inside synced folders, right-click (in Windows 11: *Show more options*):

* a file or folder in a Drive pair: **Open in Google Drive**; in a NAS pair: **Open in Synology Drive**
* a .docx/.xlsx/.pptx in a Drive pair: **Open in Google Docs / Sheets / Slides** (Office-compatibility
  editing); an exported Google Doc opens the original document
* **Shift+right-click** on the empty background of a folder: **Open this folder online (Unisono)**
* **Share on Google Drive...**: who has access (viewer, commenter, editor; change or remove),
  add people with an optional e-mail notification, general access (restricted, anyone with the
  link, your Google Workspace domain), copy the link. An exported Google Doc shares the original.
* **Share via Synology...**: File Station sharing links (gofile.me) with an optional password
  and a limit on the number of opens; copy or delete them. (DSM 7 ignores expiry dates for these
  links through its API, so none are offered.)

The entries are registered per user (no admin) and limited to synced folders with a path
condition. Explorer ignores such conditions on folder backgrounds, hence the Shift-only entry
there. Windows 11's compact menu only accepts packaged apps, which need admin to install. The
browser opens with the pair's Google account selected. Turn the menu off under Settings.

### Several computers

Each computer has its own settings, sign-ins and sync state, and by default its own pair list.
To use the same pairs everywhere, keep the list in a shared file (Settings > Pair list > *Share
via file*, e.g. in OneDrive) and point every computer at it. Local folders under your profile are
stored as `%USERPROFILE%\...` or `%APPDATA%\...`, so one definition fits computers with different
user names. A pair can be limited to some computers (*Computers* in the pair dialog). This is
also how other apps' settings folders are kept alike: a pair for `%APPDATA%\SomeApp` on the
computers that run it.
### Several accounts

Pairs name their Google account by e-mail address (config `Account`; empty means the first
account). With an *Internal* OAuth consent screen only accounts of that Google Workspace
organisation can sign in; other accounts need the screen set to *External* and published.
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

On the first sign-in (the *Sign in* button, or `UnisonoCli auth`) a browser opens. An *External* unverified app shows "Google hasn't
verified this app": click *Advanced* and *Go to Unisono (unsafe)*. That is your own client
id and your own account, so the warning is expected. The refresh token is then stored in
`%LOCALAPPDATA%\Unisono\tokens` and sign-in is silent from then on. `auth` prints which
client id and project it is using, so a wrong JSON is obvious before the browser opens.

## Synology NAS instead of Google

A pair can also point at a folder on a Synology NAS, through the File Station API on DSM's
HTTPS port (5001 by default). Nothing is installed on the PC and nothing but HTTPS is
used. The NAS must be reachable from where the PC is: a DDNS name with port 5001
forwarded, or a QuickConnect address (`https://myid.quickconnect.to`), which Unisono
resolves by following its redirect.

```
UnisonoCli nas login https://nas.example.com:5001 myuser
UnisonoCli init /home/Sync C:\Users\me\Sync --nas https://nas.example.com:5001 myuser --create
```

`nas login` asks for the DSM password in its own prompt, tries a login, asks for a
two-factor code if the account has one, and stores the password and the resulting device
token encrypted with DPAPI for the current Windows user in `%LOCALAPPDATA%\Unisono`.
Later logins need no code. `nas logout <server>` forgets the credentials. Add `--insecure`
to both commands for a self-signed DSM certificate.

Remote paths start with the shared folder as DSM shows it, e.g. `/home/Sync` or
`/photo/2026`. Differences from a Drive pair:

* No change feed: the folder is re-listed every poll (one request per subfolder), so the
  default poll is 60 s for NAS pairs. Keep the tree moderate or the poll long.
* No checksums: files are identified by size and modified time, which Unisono sets on
  upload. Content is compared by hashing after download when it has to be.
* Deletes go to the shared folder's recycle bin only if **Recycle Bin** is enabled for that
  shared folder in DSM (Control Panel > Shared Folder > Edit). Enable it.
* Google Docs settings and cloud-only links do not apply.

## Usage

```
UnisonoCli auth                                sign in and show account, quota and the changes token
UnisonoCli nas login <server> <user>             store DSM credentials after a test login
UnisonoCli init <Remote/Path> <localDir> [--create] [--nas <server> <user>]
                                              add a sync pair (--create makes the remote folder)
UnisonoCli sync [-v]                             one sync cycle for every pair
UnisonoCli run [-v]                              keep syncing: watch local folders, poll Drive
UnisonoCli config                                show config path and pairs
UnisonoCli ls [Drive/Path]                       list a Drive folder
UnisonoCli pull <Drive/Path> <localDir>          one-way mirror Drive -> local, never deletes
UnisonoCli changes <token>                       raw Drive change feed (diagnostic)
UnisonoCli version
```

Settings live in `%LOCALAPPDATA%\Unisono\config.json`, one object per pair:

```json
{
  "Pairs": [
    {
      "Kind": "Drive",
      "RemotePath": "Projects",
      "LocalPath": "C:\\Users\\me\\Projects",
      "GoogleDocs": "Export",
      "CloudOnlyMinMB": 0,
      "CloudOnlyFolders": ["Archive"],
      "PollSeconds": 30,
      "FullRescanMinutes": 15,
      "Account": "me@example.com",
      "ExcludeFolders": ["Private"]
    },
    {
      "Kind": "Synology",
      "RemotePath": "/home/Sync",
      "LocalPath": "C:\\Users\\me\\Sync",
      "Server": "https://nas.example.com:5001",
      "User": "myuser",
      "AllowInvalidCertificate": false,
      "PollSeconds": 60
    }
  ]
}
```

Before overwriting or deleting anything remotely, Unisono re-reads that item's metadata
directly (the Drive change feed can lag by minutes); a newer remote version then becomes
a conflict instead of being overwritten.

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
