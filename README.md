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
  copy under `versions\<pair>\<path>\` (default 5 copies, 30 days; Settings). The versions folder is the data
  folder's `versions` by default and can be moved anywhere outside the synced folders, e.g. to another drive
  (Settings > Previous versions folder); kept copies move along.
  Deletions go to the Recycle Bin or the remote trash, conflicts keep both files (notes and text files edited
  in different places are merged; see *How it syncs*).
* **Conflicts**: a file changed on both sides that could not be merged shows in the tray (a balloon that opens the
  Conflicts window, and an amber icon until every conflict copy is sorted out) and under **Conflicts...** on the
  Sync pairs tab: compare a copy with the current version line by line, keep one, or merge them. Merging puts both
  edits into the file; lines changed on both sides are written twice between `<<<<<<<` and `>>>>>>>` to sort out
  by hand. It is offered when the computer that made the copy knows the two versions' common ancestor.
* **Ignore rules**: gitignore-style patterns in Settings (all pairs), per pair, and in
  `.unisonoignore` files, which apply to their folder and below and sync to other computers.
  `*.log`, `__pycache__/`, `renv/library/`, `!keep.log`, `/only-at-this-level`. Ignored items
  are never touched on either side; a folder holding ignored items is never deleted as a whole.
* **Ignore suggestions**: every 15 minutes or so each pair is looked over for items that probably
  need not sync: build output next to the project file that rebuilds it (`bin/` and `obj/` beside a
  `.csproj`, `node_modules/` beside `package.json`, `target/`, virtual environments...), what a
  folder's own `.gitignore` leaves out, temporary, cache and log files, and folders whose files are
  created and deleted in bulk. Nothing is ignored by itself: the pair's card says what was found and
  Review lists the patterns, ticked when they are safe to drop (build output, caches, logs) and
  unticked when they might be wanted on every computer (settings, keys, `dist/`). Ticked patterns go
  into the pair's `.unisonoignore`; the rest keep syncing and are not suggested again (Edit > Suggest
  again). The tray tells about new suggestions once. Contents of `.git` folders are never judged.
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

Each computer has its own pairs, settings, sign-ins and sync state: set up the pairs on each
computer. Two computers syncing the same remote folder need no coordination.
### Several accounts

Pairs name their Google account by e-mail address (config `Account`; empty means the first
account). With an *Internal* OAuth consent screen only accounts of that Google Workspace
organisation can sign in; other accounts need the screen set to *External* and published.
## Requirements on the machine that runs it

* Windows 10/11, no admin needed.
* The .NET 8 Desktop Runtime (or 9, 10). Without it, build with `build.ps1 -SelfContained`, which bundles the
  runtime but makes every start take seconds.
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
2. Enable the APIs Unisono uses (search for each at the top, then *Enable*):
   * **Google Drive API**: required, for everything.
   * **Google Docs API**: needed to write edits of exported documents (docx) back into the Google
     document. Unisono uses it only to check that a document has no tabs, which a Word file cannot
     hold; without it, such edits are kept as a separate file next to the document instead. The
     Accounts tab shows whether it is enabled, with a link to do it.

   Nothing else: Sheets and Slides need no API of their own (their sheets and slides survive the
   round trip; open comments are checked through the Drive API).
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
  Exports are ordinary, writable files: save an edit in Word, Excel or PowerPoint and it is
  written back into the Google document. A document without tabs or open comments is replaced
  by the saved file (Drive converts it). A Google Docs document with tabs or comments is updated
  tab by tab through the Docs API instead: the Word file is split at its tab names, and only the
  differences go in (text, new or removed paragraphs, heading levels), so tabs, comments and all
  formatting stay; character formatting made in Word (bold, colours) is not carried over. When
  that cannot be done safely (a tab name, a table or an image was changed) or the Docs API is not
  enabled, the edit is kept as `Name (edited MACHINE time).docx` next to it, uploaded as an
  ordinary file, and the tray says why. In Word, tab names are titles and appear in the
  navigation pane; headings keep their numbers (Heading 1 in Word is Heading 1 in Google), so they
  appear beside the tab names there rather than beneath them. A write-back is converted by Drive; the previous content stays in the
  document's version history, and some Google-only formatting may shift in the round trip. When the document also changed in Google since, the local edit is kept as
  `Name (conflict MACHINE time).docx` next to it and the export shows the current version.
  The export is re-written when the document changes on Drive. Drive caps exports at 10 MB.
  Synology Office exports stay read-only: DSM offers no way to write them back.
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
* When both sides changed a note or text file (`.md`, `.txt`, up to 1 MB), the two edits are merged line by line
  against the last synced version (a three-way merge, as `git merge-file` does it). That version is kept for each
  such file under `base\<pair>\` in the data folder, checked by its md5 before use; when it is missing, Drive's own
  version history is asked for it. The merged file is used only when the edits touch different lines, with at least
  one unchanged line between them; it replaces both versions, which are kept among the previous versions first.
  Typical case: an app rewrites its own section of a note on one computer while you write below it on the other.
* Otherwise, when both sides changed a file, both are kept: the local version is renamed
  `Name (conflict MACHINE yyyy-MM-dd HHmm).ext` and uploaded, Drive's version takes the
  original name. No conflict markers are ever written into a file on their own.
* A file that exists on both sides with no history (first sync of a pre-existing folder)
  is adopted if the content is identical, otherwise treated as a conflict.
* Two machines syncing the same Drive folder need no coordination; Drive is the hub and
  each machine has its own state.

* Missed events need no replay: every cycle compares everything, so what happened while
  Unisono was not running (asleep, offline, work on another computer) is picked up by the first
  cycle, which runs at start, when the network comes back and when Windows wakes up. Drive
  changes are fetched incrementally from the change feed with the saved page token.
* Programs save in steps (R, Office: temp file, rename, delete). Unisono waits until a folder has
  been quiet for 2 s (at most 30 s), leaves files written to in the last 2 s for the next cycle,
  checks again that a file is really gone before deleting it remotely, and ignores Office's lock
  and temp files (`~$...`, `~WRL....tmp`, `8A3F2B1C.tmp`).

Limitations: Drive allows two siblings with the same name or names differing only in case, Windows
does not, so the first one wins and the rest are reported; Docs exports have no md5, so
change detection for them uses Drive's modified time.

## Files on demand

Tick *Files on demand* on a pair and it works like OneDrive: everything in the remote folder shows up in
Explorer, but files are online-only placeholders that take no space until something opens them; then they are
downloaded on the spot. Explorer shows the state with its usual icons (cloud = online-only, green outline = on
this computer, filled green = always kept, arrows = syncing), and its right-click menu decides what is local:

* **Always keep on this device** on a file or folder downloads it and keeps it up to date, new files in the folder too.
* **Free up space** makes it online-only again (only when nothing in it changed locally).

So a whole Drive can be browsed while only the folders you choose take space. Edits, new files, deletions and
renames work as in any pair; renaming or deleting an online-only file never downloads it. Unisono must be
running to open an online-only file (like OneDrive). It uses the Windows Cloud Files API, which allows no such
folder inside another provider's: pick one outside OneDrive and Work Folders, e.g. `%USERPROFILE%\Unisono\Drive`.
Turning it off (or removing the pair) turns what is local back into ordinary files; online-only files are
downloaded (or, when the pair is removed, simply dropped: they are still online).

## Folder pairs

The other side of a pair can also be an ordinary folder: a network share (`\\server\share\Projects`), a mapped or
USB drive, or another folder on the same computer. Everything else works as with Drive and the NAS: two-way or
one-way, selective sync, ignore rules, versions, renames and moves. Both folders are watched, so changes on either
side are picked up at once where Windows reports them (network shares usually do), and polled otherwise.

- Copies are written under a temporary name and swapped in, so nobody reading the other folder sees half a file.
- Deletions go to the Recycle Bin. Network folders have none, so there the item is moved to Unisono's own trash
  folder (`trash` in the data folder) instead of being deleted for good.
- If the other folder cannot be reached (VPN off, drive not connected), the pair reports it and waits; nothing
  is deleted locally.

From the command line: `UnisonoCli init \\server\share\Projects C:\Work\Projects --folder`.
## Encrypted pairs (Cryptomator vaults)

Tick *Encrypt on the other side* when adding a pair: the remote folder (empty, new, or an existing vault) then holds
a [Cryptomator](https://cryptomator.org) vault, vault format 8 with SIV_GCM, the format of Cryptomator 1.6 and later.
Names and content are encrypted on this computer; Drive or the NAS only ever see ciphertext. The same vault opens
in the Cryptomator apps (Windows, macOS, Linux, Android, iOS) with the same passphrase, so the files never depend on
Unisono. Unisono can also sync a vault made by Cryptomator.

- The passphrase is asked for once per computer; the two master keys are then kept DPAPI-protected for the Windows
  user (`%LOCALAPPDATA%\Unisono\crypt`). Without the passphrase the files cannot be recovered.
- Web view, sharing, links and Google Docs do not apply to encrypted pairs. Remote checksums say nothing about the
  plain content, so these pairs compare size and date, as a NAS does.
- Moving or renaming a folder moves only its small entry in the vault (directory contents live under the directory's
  id), so on Drive it stays a cheap move. On NAS and folder pairs a moved item gets a new id, and the other computer
  sees it as deleted and added.
- Encryption can be turned on and off for an existing pair. With plain files (or a vault) on the other side the folder
  is converted in place (`VaultConverter`), in turns and resumably; what was converted goes to the remote's trash at
  the end, where copies stay until it is emptied. Other computers stop syncing such a folder (their card says why)
  until their pair is switched too: a plain pair never syncs a vault as files, and an encrypted pair never reads a
  vanished vault as "everything was deleted".
- Every computer that knows the vault keeps copies of `vault.cryptomator` and `masterkey.cryptomator` next to the key
  and puts them back when they go missing or are damaged on the other side (also on request: *Put back vault files*).
- *Show recovery key* writes the vault's keys out (base32 in groups of four, with a checksum). With it, *Recover with
  recovery key* opens the vault again with a new passphrase when the passphrase or `masterkey.cryptomator` is lost.
  It opens the vault on its own: keep it as safe as the passphrase.
- The crypto (AES-SIV, scrypt, AES Key Wrap, AES-GCM) is in `CryptomatorPrimitives.cs`, `CryptomatorVault.cs` and
  `CryptomatorStore.cs`; the primitives were checked against the RFC 5297, 7914 and 3394 test vectors.

## Renames and moves

A file or folder renamed or moved on one side is renamed or moved on the other, instead of
being deleted and transferred again (which would also cost it its Drive history, sharing and
links). Locally, a moved file is recognised by size, modified time and checksum, a moved folder
by its contents; on Drive by the item's id; on a NAS (whose ids are paths) by size and time,
folders by their contents. Anything ambiguous falls back to delete plus copy.

## Tests

`tools\Test-Unisono.ps1` runs end-to-end tests against the signed-in Google account and, with
`-Suite nas`, a NAS: two simulated computers per remote, in throwaway folders under `%TEMP%`
(the script refuses to run against the real settings folder), and a temporary remote folder
that is removed afterwards.

```powershell
powershell -ExecutionPolicy Bypass -File tools\Test-Unisono.ps1                  # drive + ui
powershell -ExecutionPolicy Bypass -File tools\Test-Unisono.ps1 -Suite folder -OtherFolder \\server\share\tests
powershell -ExecutionPolicy Bypass -File tools\Test-Unisono.ps1 -Suite nas -NasServer myid -NasUser me -NasFolder "/home/tests"
```
## Building

`build.ps1` publishes framework-dependent single-file exes (`-SelfContained` for a bundled runtime) to `%USERPROFILE%\Program\Unisono`
and to `dist\`. It uses a user-local SDK from `%LOCALAPPDATA%\dotnet` when present.
Set `UNISONO_DATA` to redirect config, state and logs (sign-in stays in
`%LOCALAPPDATA%\Unisono`), which is how the end-to-end test runs two pairs against one
Drive folder on a single machine.
