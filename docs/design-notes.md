# ViewLight design notes

**These are internal notes, not the manual.** They record why things are the way they are - what was
measured, what was tried, what was deliberately left out - and they are written for whoever changes the
code next. If you want to know how to *use* ViewLight, read [the readme](../readme.md) instead; nothing
in here is needed to run it.

The rest of this file is that record, in the order it was written.

---

# ViewLight

A proof-of-concept GUI launcher for **Imlight** (the [Revive101](https://revive101.de/) private
Wizard101 server).

It exists to remove the fiddly parts of running a private server: finding your client, getting a server
build onto disk, editing `Imlight.ini` by hand, and remembering to start the server *before* the client.

- Imlight: <https://github.com/Revive101/Imlight>
- Documentation: <https://revive101.github.io/Imlight/imlight/>
- Revive101: <https://revive101.de/>

> **Status: proof of concept.** The launcher side (locations, install/update, config editing, warm-up,
> client launch) is implemented and covered by tests, and the server it installs has been booted for
> real on Windows. The whole workflow has been played through by hand on this machine - install the
> server, press **Play**, log in with the client and create a character. Reading *item names* out of
> `root.wad` is deliberately stubbed - see [root.wad and items](#rootwad-and-items).

## What it does

1. **Asks where things are.** On first run ViewLight wants two folders, and it will not show Home until
   it has them. It can find your client for you.
2. **Downloads the server.** One button fetches the newest Windows build from the Imlight GitHub
   releases and unpacks it into your server folder.
3. **Edits settings for you.** Admin credentials, tutorial behaviour, the starter kit, ports, world data
   and the embedded database port are all fields that write straight into `Config/Imlight.ini`.
4. **Warms up, then plays.** Press **Play** and it starts the server, waits until the account server is
   genuinely listening, then launches the client pointed at it - signed in as the account chosen on Home,
   without the game's login screen.
5. **Updates without losing anything.** Updating replaces the binaries and merges your settings back
   over the new defaults.

## Requirements

- Windows (the Wizard101 client is Windows-only; Steam detection reads the Windows registry)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build; the app targets `net10.0`
- A Wizard101 client you obtained yourself - Imlight is BYOD and ships no game files
- Internet access for the first install

## Running it

```bash
dotnet build ViewLight.slnx
dotnet run --project src/ViewLight
```

## The two folders

ViewLight stores the **game folder**, not the executable, and resolves everything relative to it:

| Thing | Path, relative to the game folder |
| --- | --- |
| Game client | `bin\WizardGraphicalClient.exe` |
| Root archive | `Data\GameData\root.wad` |

So the game folder is `...\steamapps\common\Wizard101`, *not* the `bin` folder inside it. Picking the
`bin` folder is the easy mistake to make, and ViewLight recognises it and corrects it up to the game
folder when it can.

Everything else can live anywhere on disk. The server folder is ViewLight's own and is created for you
if it does not exist.

### Finding your client automatically

**Detect** looks for a Wizard101 install, in this order:

1. Steam's registry keys - `HKCU\Software\Valve\Steam` (`SteamPath`), `HKLM` `...\Valve\Steam`
   (`InstallPath`) and `SteamExe`;
2. every library in `steamapps\libraryfolders.vdf`, so installs on a second drive are found too;
3. the conventional KingsIsle locations under `Program Files (x86)`, `Program Files` and `ProgramData`.

A candidate only counts if it actually contains one of the two files above, so a stray folder named
`Wizard101` is not mistaken for the game.

## The tabs

| Tab | What it is for |
| --- | --- |
| **Home** | The Wizard101 banner and its Play button; everything about the server sits in one quiet strip beneath it |
| **Profile** | What this launcher knows about the account Play signs in as on this server: picture and banner, playtime, standing, the score circle, and the wizards on it |
| **Profile → Achievements** | Everything that account has earned on this server, grouped by category, with a milestone card for each ladder. A page of Profile rather than a tab of its own, opened from the score circle |
| **Settings** | Both locations, every value ViewLight writes into `Config/Imlight.ini`, the theme, and the launcher's own close prompt |
| **Log** | Launcher messages and live server output |
| **About** | Versions, links, and what is still stubbed |

There is deliberately no separate menu for starter items: it is a setting, so it lives in Settings
alongside everything else.

### Home is the game, not the server

Home is built around one thing: getting into Wizard101. A wide banner carries the game's own art, its
wordmark, and the Play button; below it the server is present but deliberately unremarkable - a status
dot, one line of state, and the start/stop/log buttons, followed by the build and update panel. Which
account Play signs in as lives on **Profile**, with the rest of what the launcher knows about that account -
the picker is one list, and it is the same choice either way. The
intent is that pressing Play looks like starting a game rather than operating a server, with the
knobs still there for when something needs them.

#### The two builds, and which way round they are

Home shows two builds: the **server build** ViewLight installed (the release tag) and the **client build**
the game itself reports. The client's revision is read out of the client rather than assumed, in this
order:

1. `bin\revision.dat` - one line, `r806919.Wizard_1_610`. It is the client's own statement of what it is,
   and it is there before the game has ever been started.
2. `bin\WizardClient.log` - the line the client writes the first time it runs,
   `revision 'r806919.Wizard_1_610'`. That scan is anchored on the word `revision`, because the log is
   full of other lines carrying a revision in a different shape, and it stops at the first match, because
   a log that has been appended to for hours is megabytes long.

Neither file is trusted to be readable. A missing or unreadable one means the build is simply unknown, and
an unknown build is never reported as agreeing with the server's.

When the two disagree, the sentence shown beside both builds names the one that is out of date instead of
leaving the user
to compare revision numbers: an older client is called **outdated** and told to update the game, a newer
one is told to update the server, and when a revision number cannot be read out of either build the
message names both without guessing at a fix. The ordering is `ClientBuildReader.Compare` and the wording
is `MainWindowViewModel.DescribeBuildMismatch`, both pure, so both are tested without a client or aserver present.

#### The art is fetched, not shipped

The banner art belongs to KingsIsle, and ViewLight is free software that redistributes nothing of
theirs - no game files, no artwork. So instead of embedding it in the build, the launcher fetches each
piece **once** from the address the art is already published at and caches it under
`%LOCALAPPDATA%\ViewLight\art\`. Later starts use the cached files and need no connection at all.

- a download is only accepted when it both starts with the PNG signature **and** ends with the PNG end
  marker, so a refused request answered with an HTML page, or a transfer cut short, is thrown away
  rather than cached, and a partial file never survives a failed attempt;
- a cached file that fails that check is refetched rather than trusted, which is what stops one bad
  download leaving the banner permanently artless;
- if there is no art and none can be fetched, Home draws a plain banner with a **WIZARD101** wordmark
  rather than an empty gap, and the fetch is retried when you next open Home. `GameArt` also records the
  box art and cover art addresses - unused for now, for a library tile later.

The window icon is deliberately left as the platform default: that icon is the game's, and it does not
belong on the launcher's own window.

#### Light and dark, or whatever the desktop says

**Settings** has a **Theme** picker: *System* (the default), *Light* or *Dark*. Both are defined in
`App.axaml` as theme dictionaries over the same brush keys, so every surface follows the choice while
the window is open - switching is instant, with no restart. *System* is Avalonia's own "ask the
desktop", which is why it behaves the same on Windows and on Linux.

The surfaces are neutral greys in both themes. An earlier version tinted every one of them blue, which
read as a cold blue interface rather than a dark one; the purple accent stays, because that is what the
Play button is built on.

**The look is a design language, not a platform.** Windows 11 was the reference for how this should
feel - a surface with depth and translucency - but nothing here depends on Windows to get that look.
There is no Mica, no acrylic, no vibrancy: translucency is ordinary alpha (`Border.glass`), the glossy
button is a gradient split hard down the middle (`Button.heroPlay`), and the banner scrim is a gradient
so the art stays visible instead of sitting behind a composited blur. Every one of those renders the
same way on Windows and on a Linux build, which is the point - see [a Linux build](#a-linux-build).

## Settings the launcher writes

All of these live in `{server}/Config/Imlight.ini`. That file stays the single source of truth - the
launcher never keeps a shadow copy, which is why an update can simply merge it back.

| INI key | Section | Meaning |
| --- | --- | --- |
| `AdminAccountUsername` / `AdminAccountPassword` | `Database` | The account Imlight creates on boot |
| `TutorialDisabled` | `Character` | Skip the tutorial; the starter kit is granted on first login instead |
| `DefaultItems` | `Character` | The starter kit template IDs |
| `StartingLevel`, `StartingZone`, `BaseGoldPouch` | `Character` | Where new characters begin |
| `GameServerIP`, `GameServerPort`, `GameServerCount`, `RealmNames` | `Game Server` | Realms to create |
| `LoginServerPort` | `Login Server` | The port the client is aimed at, and the one waited on at startup |
| `EmbeddedDatabasePort` | `Database` | Where the embedded RavenDB listens |
| `PatchServerInternalUrl` | `Patch Server` | The patch server Imlight loads `root.wad` from |
| `SpiralDBAutoFetch` | `Database` | Pull the latest world data on boot |
| `LogLevel` | `Logging` | Server log verbosity |
| `GameRevision`, `EnforceRevision` | `Global Settings` | Revision pinning |

### Starter items are hard-locked

`DefaultItems` is what a character is granted after the tutorial. **The client bricks a character if
fewer than two items are granted**, so ViewLight refuses to write a kit below two:

- **Remove** stops at two and explains *"Can't be less than two."*;
- the reset button restores the wand + deck pair rather than clearing the list, because an empty kit is
  the worst case;
- **Save** validates the count first and refuses the whole save - nothing else is written either -
  until the kit is valid;
- a config that was hand-edited to something too small is flagged as soon as it is loaded.

The other end is capped too: RavenDB batches at 16, so 16 is the maximum.

### Starter settings

**Settings -> Apply starter settings** does the three things a fresh server wants:

- sets `TutorialDisabled = true`, so new characters skip the tutorial;
- narrows `DefaultItems` to `87256,126983` (a Tier 1 wand and the tutorial deck) and rewrites the
  comment above it to explain what was removed;
- writes the admin username and password currently in the form.

The stock config hands out QA and resist gear through the tutorial
(`1317124`-`1317133`, `1317234`, `284071`). Applying the preset removes those from a normal
character's start.

### How the launcher edits the INI

`Imlight.ini` is documentation as much as configuration: almost every key has a comment above it. Rather
than parse it into a dictionary and rewrite it, the launcher keeps the file as an ordered list of lines
and only rewrites the values it was asked to change. It preserves comments, blank lines, spacing, inline
comments, key casing, CRLF line endings and the file's encoding - including files that are not valid
UTF-8, which Imlight's own shipped config is not. Anything a future Imlight release adds, such as the
`[CrownShop]` section, is preserved rather than dropped.

The test suite asserts a load-then-save of a stock config is **byte-for-byte identical**.

## Installing and updating

Releases come from `Revive101/Imlight`. Every Imlight release is published as a *prerelease*, so the
launcher lists releases and sorts by publish date rather than using GitHub's `/releases/latest` (which
404s for this repository). It installs the `Imlight-win-x64*.zip` asset - around 260 MB - and uses its
`Imlight.Director.exe`.

The archives are flat self-contained deployments: the zip root *is* the install root, so it is extracted
directly into your server folder.

**Updating is an overlay, never a wipe.** Files are copied over the top, so:

- `logs/`, `cache/`, `spiraldb-cache/` and the embedded RavenDB folder are untouched, because nothing is
  ever deleted;
- `Config/Imlight.ini` is read *before* the overlay and merged back afterwards, so your values win and
  any new keys a release introduces appear at their defaults;
- the previous config is kept beside it as `Imlight.ini.bak`.

`Config/akka.conf` is part of the build and *is* replaced on update.

Two smaller conveniences: a completed download is cached, so a retry does not re-download 260 MB, and
archives that wrap everything in a single top-level folder (as some earlier releases did) are unwrapped
automatically.

### The embedded RavenDB port

Imlight's stock config points the embedded database at port **8080**, which is one of the most contested
ports on a machine. When something else already holds it, Imlight dies during boot instead of reporting a
usable error.

So on every install and update the launcher checks `EmbeddedDatabasePort`:

- still on the stock `8080` -> replaced with a random free port;
- set to something else and free -> **left alone**, so a deliberate choice survives updates;
- set to something else that is taken -> replaced with a random free port.

Free ports are picked from 20000-49000, below the range Windows hands out to outbound connections. The
chosen port is reported in the Log, and **Settings** has a *Find a free port* button plus the value
itself if you would rather choose.

### The patch server

**This is the one that stops the server dead.** Imlight loads `Root.wad` from a patch server, and its
stock config points at `http://127.0.0.1:12369` - an address that only answers if you started your own
patch server there. With no patch server and no cached `Root.wad`, Imlight throws from deep inside
resource loading, nothing catches it, and the .NET runtime tears the process down:

```
The server stopped unexpectedly (exit code -532462766).
```

That number is `0xE0434352`, which is not an exit code so much as a signature for "unhandled .NET
exception". The server's own log said it plainly, three lines before it died:

```
[ERR] Patch server endpoint is not available! Continuing without patch server.
[ERR] Patch server is not reachable. Cannot load Root.wad.
```

So the launcher does two things about it:

- it repairs the config the same way it repairs the RavenDB port, on every install, update and start:
  a blank value, or the stock localhost address **with nothing listening on it**, becomes
  `https://patcher.phill030.de` - the patch server the Revive101 team runs;
- it reports a failure with the *cause*, not just a number: a known crash code is explained, and the
  last few `[ERR]`/`[FTL]` lines from the server's log are quoted back into the Log pane.

A value you chose yourself is never touched, even if it looks like the stock address - that address is
exactly where a self-hosted patch server would be. Advanced users running their own
[Aurorium](https://github.com/Revive101/Aurorium) just set `PatchServerInternalUrl` (or the **Patch
server** field in Settings) and the launcher leaves it alone. *Use the public one* puts the Revive101
address back.

The first boot after this is fixed downloads `Root.wad` (about 281 MB) into the launcher's cache, and
the server keeps a copy of it afterwards, so it is a one-time cost.

## Warming up the server

Starting Imlight is not instant: the Director brings up the patch and login servers, fetches `Root.wad`
from the patch server (and caches it - see [the patch server](#the-patch-server)), loads SpiralDB and
creates the game realms.

Pinging the server's address would prove nothing - the machine answers either way. So the launcher
watches readiness in two ways, and treats them differently, because they are not equally certain:

1. **The server's own readiness line** - `Imlight may now be connected to.` - is the server stating that
   it has finished starting and is about to serve logins. It is taken at its word: the client is started
   the moment it appears, with **no settle window**. Waiting after a direct announcement would only
   delay every session by ten seconds for nothing.
2. **The login port opening** is only a guess by comparison - a socket accepts connections slightly
   before the account server can serve them - so when *that* is what fired, the launcher holds for a
   **10 second settle window** before calling the server ready.

Either signal is enough on its own, the settle window only ever applies to the port probe, and the Log
always names whichever signal actually fired. The readiness line is checked before the port on every
pass, so a port that opened mid-boot cannot make an announcement wait out a window it does not need.

That line is read from the file Serilog actually writes - `logs\log20261013.txt`, not the `logs\log.txt`
the config names, because the daily rolling interval puts the date in the name - and only from what *this*
run has written to it. Imlight appends to a daily file, so a readiness line from an earlier run today is
still sitting in there and is not evidence that this server is up. On the first run of a day the
launcher watches today's name even though the file does not exist yet, rather than falling back to
yesterday's, which nothing will ever write to again.

**Play** starts the server, waits for that, then launches
`bin\WizardGraphicalClient.exe -L {GameServerIP} {LoginServerPort}` - plus `-U ..{accountId} {sessionKey}`
when there is an account to sign in as, see [signing in without the login
screen](#signing-in-without-the-login-screen) - **with its working directory set to `bin\`**, not to the
game folder. The client reads `PatchConfig.xml`, loads its message tables and writes
`WizardClient.log` relative to its working directory, and all of those live in `bin\`. Started from the
game folder it logs `Could not open ConfigFile PatchConfig.xml for reading.`, fails to load a single
message module, and never reaches the login screen.

Closing ViewLight stops the server it started - and so does killing it outright. See
[the server cannot outlive the launcher](#the-server-cannot-outlive-the-launcher).

### Playing somewhere else

Play's own press is the above. The caret beside it opens the other ways to play, and one rule decides
the shape of all of them:

> **Only this launcher's own server can sign anybody in.** `-U` continues a session the server already
> issued a key for, so a server that never issued one cannot be signed in to - which is every server
> that is not this one. Joining elsewhere is therefore `-L {host} {port}` and nothing more, and
> Wizard101 shows its own login screen.

- **Join a server…** takes an address - `host` or `host:port`, the port defaulting to **12000**. Nothing
  local is started; the client is pointed straight at it. The address is remembered in `launcher.json`
  so the box is filled in next time.
- **Find servers on my network** is the only discovery there can be. Nothing publishes a list of Imlight
  servers, so a server across the internet has to be typed in, the way a Minecraft server list works.
  What the launcher *can* do is sweep what is local: for each IPv4 address this machine holds, every
  address in its `/24` - minus this machine - is asked whether anything accepts a TCP connection on
  **12000**, 64 at a time, 600 ms each. Whatever answers is listed with a Join of its own. A hit means
  **something is listening on 12000**, which is a good guess at an Imlight server rather than a promise,
  and is never called ready to play. The sweep is clamped to a `/24` on purpose, so a machine on a `/16`
  cannot turn one press into 65k connections.
- **Wizard101 official** is `login.us.wizard101.com:12000`, and it is two menu entries rather than one
  because of **`-ST`**. Imlight's own launch-argument table names `-ST` "Steam required": a Steam-linked
  Wizard101 account cannot sign in without it, and a standalone account must not be given it. Guessing
  would mean a refused login with nothing on screen explaining why, so the account type is asked for and
  remembered (`OfficialUsesSteam`) rather than inferred.

While the game is on somebody else's server the status rows say so: the server row names the address and
keeps the same "ready" dot instead of reporting a local server that is not running, **Stop the server**
is hidden because there is nothing local to stop, the login hint is suppressed, and the account picker
explains that it has nothing to sign anyone in with there. The client is still followed as a process, so
"Wizard101 is running" and "Wizard101 was closed" keep meaning what they say.

### A server that never shut down

If ViewLight is killed - a crash, Task Manager, a reboot - a Director it started can keep running, and it
keeps every port it bound: 12000, the realms, the patch server, the embedded database. Pressing **Play**
then starts a *second* Director that cannot bind any of them, so the server it started is unusable and the
ports are held by a process nobody is looking at any more.

[The job object](#the-server-cannot-outlive-the-launcher) is the first line of defence against that. This
is the backstop, for anything that still gets away: a server an older ViewLight started before the tie
existed, one on a machine that refused the job, or one started by hand from this folder.

So before starting, ViewLight stops any `Imlight.Director.exe` **that was started from the executable in
your own server folder**, waits for the login port to come free, and says in the Log which process id it
stopped. An Imlight running from anywhere else on the machine is somebody else's server and is never
touched - matching is on the exact executable path, and a process whose path cannot be read is left alone
rather than guessed at.

The new process is started from the folder holding `Imlight.Director.exe`, which is your server folder.
That is what Imlight resolves its relative paths against: `LogPath = ./logs/log.txt` lands in
`{server}\logs\`, as the log files on a real install show.

### The server cannot outlive the launcher

Closing ViewLight stops the server it started. On its own that is not enough, because the interesting
failure is *killing* the launcher - Task Manager, a crash, a reboot - which used to leave the Director
running with every port it had bound.

So the server is tied to the launcher rather than merely asked to stop. Before ViewLight starts the
Director it creates a Windows **job object** configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, and
assigns every server it starts to it:

- when the launcher process ends, for **any** reason, the kernel closes the job's handles and Windows
  terminates everything left in the job. A hard kill is covered, which the runner's own stop path cannot
  cover because by then the runner is gone;
- the Director's children inherit the job, so the game realms it spawns cannot outlive the launcher
  either - and so cannot the embedded RavenDB server, which runs as a child process of the Director
  rather than inside it;
- the only rule configured is "stop when the launcher stops". No memory, CPU or process-count limits, so
  nothing about how the server itself runs changes.

On a machine that refuses the job - or off Windows, where there is no such thing - the launcher still
starts the server and says in the Log that it could not tie it, rather than pretending. The fallback is
the old behaviour: a killed launcher can strand a server until the next Play reaps it.

**Closing while the server runs asks first.** Stopping the server disconnects anyone playing on it, so
closing ViewLight with a live server shows an *are you sure* instead of ending a session silently. The
prompt names what is running, and its **Don't ask me again** checkbox turns the prompt off; that
preference lives in `launcher.json` and is turned back on under **Settings**, so it is never a one-way
door. Escape, the title-bar cross and the default button all mean *keep it running* - stopping the server
takes a deliberate click.

## Signing in without the login screen

**This was measured, not read.** The client documents `-U ..[ID] [PasswordHash] [Username]`, and
nothing in Imlight's source builds that argument, so the shape had to come from a real client. What it
turned out to be is the single most important thing to know about this feature:

> **`-U` does not sign anybody in.** It continues a sign-in the server already believes happened. So the
> launcher does not hand over a password, and cannot: it hands over a *session key*.

### What the client does with `-U`

A real `WizardGraphicalClient.exe` was started against a real server with each spelling, and what it
sent was captured by putting a recording proxy in front of the login port (the client resolves the host
it is given and dials both the port and the port after it, so both were proxied):

| Arguments passed | What happened |
| --- | --- |
| `-L 127.0.0.1 12000` | Login screen. This is also the documented behaviour: no `-U` means the internal login menu. |
| `-U <username> <password>` | Login screen. Ignored - the value does not parse. |
| `-U <accountId> <value>` | Login screen. Also ignored. |
| `-U ..<accountId> <value>` | **Login screen skipped**, and `MSG_USER_VALIDATE` was sent. |

So the value after `-U` has to parse as `..%llu` - a `..` and a 64-bit GID - and that GID is the account
id, not a username. A third argument (`-U ..[ID] [PasswordHash] [Username]`) changes nothing: the same
203-byte request went out with and without it.

The request that follows is `LOGIN_7_PROTOCOL.MSG_USER_VALIDATE` (service 7, message 15 - the `_MsgOrder`
Imlight's own docs list), carrying the account GID, an 88-character base64 `PassKey3` (a SHA512), the
machine id as a GID, the locale, and the patch client id. Against a server holding no session key for
that account and machine, Imlight answers `MSG_USER_VALIDATE_RSP` with error `246825817` -
`UserValidateResult.ValidateFailed` - which the client shows as *invalid username or password*.

### What the server needs

Imlight's `UserValidator.Validate` is short and decides everything: find the account by `UserID`, then
take the session key stored for `(AccountId, MachineID)`, then check the key the client derived from the
value it was handed. `ClientKeyCollection` is what produces those keys: **one document per account**,
rewritten delete-then-store on each sign-in, carrying `AccountId`, `MachineId` and `ClientKey2`, with a
30-hour expiry in its metadata. Handing the client the stored `ClientKey2` therefore satisfies the whole
check without a password or the client's own hashing ever being involved.

That was confirmed twice against the running server, by reading the reply to `MSG_USER_VALIDATE`:

- with the stored key, the reply is error `0` - success - followed by the server list and realm list;
- with any other value, error `246825817` and no session.

The second confirmation matters more: the key was **issued by the launcher itself** - written into the
session-key collection in the same shape Imlight writes, after deleting the stale one - and the server
accepted it and the client went on to the character screen. The feature does not depend on a key that
happens to be lying around.

### What the launcher does

- **Reads the database** (`PlayerDatabaseClient`) while the server is up, over the embedded RavenDB port
  from `Imlight.ini`. Accounts come back as `AccountId` + `Username`, session keys as
  `AccountId`, `MachineId`, `ClientKey2`, and the expiry metadata.
- **Guards the shape before believing any of it**, anchored on the admin account Imlight creates on boot:
  that account must be there, and its `PasswordHash` must be the password in `Imlight.ini` hashed the way
  Imlight hashes it. Any document that does not look the way it should switches automatic sign-in off and
  says why, rather than acting on a guess. Writes are behind the same guard, and only ever write a
  session-key document this launcher built in full.
- **Picks the account**, on the **Profile** tab: an account the player chooses, remembered by id,
  defaulting to the only one there is. The names are remembered as well, so the picker is filled in the moment the launcher
  opens rather than only after the server has been started once - see
  [why the account list is remembered and the key is not](#why-the-account-list-is-remembered-and-the-key-is-not).
- **Chooses a key** (`AutoLogin`): the stored one when it is this machine's, or when this machine is not
  known yet; otherwise it issues one for this machine. A key with under half an hour left is replaced
  rather than handed over mid-session. The session key is a credential, so it never reaches the Log.
- **Learns this machine's id**, which is the one thing it cannot work out for itself. A key that appears
  or changes while the player is signing in was written by a sign-in on this machine, and carries the
  machine id the server recorded; that is what gets remembered. A key that has not moved teaches nothing,
  on purpose - adopting an untouched key's machine id is how a launcher comes to believe it is another
  machine. Until it is known, the stored key is handed over anyway, because the database belongs to the
  server on this machine; if it turns out to be another machine's the client refuses it and the sign-in
  that follows teaches the launcher, so the wrong answer costs one login screen and nothing else.

### Why the account list is remembered and the key is not

The player database only answers while the server is running, which used to mean the account picker sat
empty on every launcher start until Play had been pressed once. An empty picker reads as having forgotten
who you sign in as, which is what it was being taken for. So the accounts - `AccountId` and `Username` -
are written into `launcher.json` whenever the database is read: when the server becomes ready, and again
just before it is stopped, so an account added during a session is captured too. On the next start the
picker is filled from that list immediately, with the remembered account selected, and the card says the
list is remembered until a live read confirms it.

**Only this server's accounts are ever shown.** The list is written down with the folder it was read from
(`CachedAccountsServerPath`, kept in the same relative form as the folder settings themselves so a portable
folder that moved still matches), and it is used only when the launcher is pointed back at that folder
*and* that server's `Imlight.ini` names both the admin username and its password. Accounts live in a
server's own player database, so a name read from somewhere else is a name *this* server has never heard
of - and a stale list used to be shown regardless, which put somebody else's accounts in the picker as if
they existed here. A list written before the folder was recorded, or one that does not match, is simply not
shown; the next live read writes it down again, folder and all. Until a server here has named its admin
account there is no account at all, so the screen says **No profile** instead of filling the gap with a
memory. Profiles are filtered the same way: one kept for an account this server does not have is not
offered, and the screen says it is unlisted rather than dropping it silently.

**The session key is deliberately not cached**, even though it would save a read. Keys live in the
server's own database rather than in memory, so **restarting the server does not invalidate them**:
`ClientKeyCollection` has no purge on boot, each document carries a 30-hour `@expires`, and a restart
leaves the key exactly where it was. They are replaced when the player signs in from the client's own
login screen, and they expire after 30 hours. That is exactly why the key is re-read at launch instead of
remembered: a remembered key that had since been replaced or had expired would fail the server's check,
and the client would show an error where it would otherwise have shown a login screen. Re-reading costs
one HTTP call to a database on this machine, it always has the current answer, and it has to happen
anyway - Play waits for the server to be ready, and the database is only readable then.

**Creating an account** is done in Settings, in the same place the admin account is set up, and writes
straight into the server's player database in the shape Imlight writes its own - so it works in the game
with no restart, and needs nothing from the server but its files. It is the same thing Imlight does with
its in-game `.account create $username $password` chat command, typed after signing in as the server's
admin account, which is what the Profile tab still points a user at as the alternative. `-C [GID]` -
loading straight into a character - stays out of scope on purpose.

## Profiles, playtime and achievements

**A profile is what a Wizard101 account is to *this machine*.** The account itself is the server's and lives
in its player database; the launcher only ever reads it. Everything on the **Profile** tab beyond the
account - the name and picture it is shown under, the time played, the wizards, the achievements - is the
launcher's own, kept in `profiles.json` (and in `profiles/<account id>/` for its two images) and sent
nowhere. That is the design rather than an omission: achievements are earned against your own server, with
one launcher never talking to another, so joining somebody else's server earns nothing here and this one
earns nothing there.

**The one account a server starts with** is the admin account in `Config/Imlight.ini`. It is the only
account that exists before anybody has signed in, so while there is no profile to show, the Profile tab
names it, says it is the account the server starts with, and says how to make another - **Create an
account** in Settings, or Imlight's own `.account create $username $password` in the game at Hall Monitor
level. It becomes an ordinary profile by itself the first time the server runs and its database can be
read, because a profile is keyed by the account's own id and the id is only learned there. With no server, or with one whose config names no admin account, there is no
account and the screen says **No profile** - which is also why a remembered name from another server is
never offered as one.

**Two sources, and nothing else.** Both belong to the server this launcher runs:

| Source | Gives |
| --- | --- |
| The player database, read every 10 seconds while the server is up | Accounts, wizards (level, school, gold, quest registry, spellbook, and the packed name key behind a name), who is signed in right now, and the server's own command log |
| The server's log file, watched for new lines only | Wizard sign-ins, zone entries, duel results, the commands that went by, and which world each zone belongs to |

Playtime is measured from the database's **online list** rather than from the game window being open, and a
gap longer than one poll interval is never credited: a launcher that was closed and opened again finds the
account still signed in, and the time in between is time it did not see. Sessions are counted from the same
list. A log watched for the first time starts at the end of the current file, so an earlier run's lines in
today's log are not folded in again.

**Refusing to guess is most of the work.** The database names accounts and characters, so what is read
there is attributed exactly. The log names a wizard, a zone and a duel's winner, but never says which
account a duel was fought by - so a duel is credited only when exactly one profile was signed in when it
ended, and left uncredited when two were (`ProfileLedger.UncreditedDuels` counts those). A wizard name in
the log waits for the account it belongs to to appear online, and is dropped after two minutes rather than
attached to whoever appears next. Nothing is split, shared out or guessed.

A profile also carries a **standing**: clean, or the number of commands the server has recorded against it
(`CommandLog`, read from the database, or counted from the log - the larger of the two, never their sum,
because they are the same commands seen twice). That is a marker rather than a judgement, and it is what
gives a locally earned level its weight: nobody handed it out.

**Score, ladders and the clean bonus.** A badge's worth comes from its difficulty (10, 25, 50 or 100), and
the total is summed from the catalog rather than stored - so a badge can be re-priced without rewriting
anybody's profile, and an id the catalog no longer knows cannot add points to a total no screen could
explain. Three ladders are *generated* rather than written out: one rung per school per five levels, up to the
server's own `Character.MaxLevel`; one per ten duels won; and one per twenty-five ordinary creatures beaten,
up to the size of the server's own creature list. Generated because a run of near-identical rows is exactly
where a gap or a repeated level hides, and because the top of a ladder is the server's to decide rather than
the launcher's to assume (`AchievementCatalog.UseMaxLevel` and `UseCreatureCount` are called with what the
server itself says; the rungs above a wizard's own cap never come up).

The rungs are badges - they are what is earned, stored, logged and scored - but they are **shown as one
milestone card per ladder**, not as a tile each. Forty rungs per school as forty badges is a wall nobody
reads; the same ladder as "Storm Wizard, now level 12, next milestone level 15, 40 points earned here, +20 at
the next" with a bar between the last rung passed and the next one is the same information in one read, and
the bar measures the distance to the next milestone rather than the fraction of a ladder nobody finishes.
Every rung is still a **milestone** (`IsMilestone`), which is what the toast switch in Settings acts on: that
switch decides whether a badge is *announced*, never whether it is earned. A clean standing multiplies the whole score by two, and the note beside the score says
so - a doubled total with nothing on screen to account for it is a total the badges underneath cannot
explain.

**Discovery, and the one thing the log does not say.** The zones a wizard has been in come straight off the
log - `added to zone WizardZone_UnicornWay` - but which world a zone is in is nowhere in it: the client's own
zone table is inside an archive the launcher cannot open, and the name carries no world. What the server
*does* do is name a zone twice over as it loads it: once by the path it came from, whose first segment is the
world, and once by the name the game calls it.

```
[INF] Game world created new zone: WizardCity/WC_Streets/WC_Unicorn
[DBG] Zone WizardCity/WC_Streets/WC_Unicorn begins load.
[INF] Zone WizardZone_UnicornWay loaded in 170ms.
[DBG] Jordan DeathBane added to zone WizardZone_UnicornWay.
```

So the launcher pairs the two as it watches - the path is held until the zone it belongs to is named - and
writes the pairing down. That is the whole of it: **no table of worlds and zones is shipped**, and a world
the server has never loaded a zone of is a world the launcher does not know, which is why the world badges
are generated from what has been learned rather than from a list. A zone entered before the pairing was
learned is credited when it arrives, exactly as a boss beaten before the creature list was read is; a zone
the server only numbers (an instance, a house, `WizardZone_00000878`) is recorded as visited and never
attributed to a world, because there is no world to learn from an id. What is learned lives in
`profiles.json` beside the profiles, so a world discovered last week is still known after a restart.

The group is sub-divided by world for the same reason the rest of the screen is grouped at all: a world is
the thing a zone is part of, and "which of Wizard City have I actually seen" is the question a list of zone
names cannot answer.

**Honest limits**, said on the screen as well as here:

- playtime and sessions cover only this launcher's own server, and only what it was running for;
- zone and duel badges need `Logging.LogLevel = DEBUG`, because that is where Imlight writes those lines -
  the Profile tab offers that switch beside the badges waiting on it rather than changing the config quietly;
- a **world** is known only once the server has been watched loading a zone of it, because that is the only
  place the two are ever said together - so a world nobody has been near is not offered a badge, and a zone
  the server only numbers is never attributed to one at all;
- a wizard's *name* is not in the database as text (Imlight stores the indices it was built from, resolved
  through the game's own name tables), so a name comes from the log when that names the wizard signing in,
  and otherwise from `Root.wad` when a game folder is known. A character neither source can name is shown by
  its id rather than under someone else's. Which of the game's two first-name tables a key is read from is
  the launcher's reading of the client's own gender flag, and a value it does not recognise resolves to no
  name at all rather than to a guess;
- deleting `profiles.json` resets playtime and achievements. It touches neither the server nor the game.

## root.wad and items

The client's item definitions live in `Data\GameData\root.wad`, and reading them is what should populate
the starter kit picker.

**What is real:** `KiWadArchive` parses the actual `root.wad` archive index - magic header, archive
revision, file count, and whether `TemplateManifest.xml` is present. Verified against a real Steam
install: 173,088 files, revision 1, template manifest present. The Starter items panel shows exactly what
was found.

**What is stubbed:** turning those templates into *named items*. Item definitions inside `root.wad` are
KingsIsle `ObjectProperty` object graphs: a hash-keyed binary format where field names are replaced by
32-bit string hashes and each behaviour block is type-tagged. Decoding it needs Imcodec's `BindSerializer`
together with the `PropertyClass` types that Imcodec *generates at build time* for a specific client
revision - it cannot be recovered from raw bytes alone.

Until that is wired up, the picker lists the item IDs Imlight's own stock config documents, and says so in
the UI. Nothing is invented.

**To finish it:** add a reference to `Imcodec.ObjectProperty` (and run its generator against a `wiztype`
dump for the target revision), then implement `RootWadItemService.LoadCatalogAsync` to walk
`TemplateManifest.xml` and read the item name behaviour off each template. That is how
[Imview](https://github.com/Revive101/Imview) does it.

## Project layout

```
src/ViewLight/
  Models/      launcher settings, release and item models
  Services/    IniFile, KiWadArchive, ImlightConfig, GameFolderLocator, PortAllocator,
               release lookup, installer, runner, server process guard, job object, client launcher
  ViewModels/  MainWindowViewModel - one view model owning all launcher state
  Views/       MainWindow shell, SetupView, and one view per tab
  Styles/      dark theme colours and control styles
tests/ViewLight.Tests/
```

`MainWindowViewModel` deliberately holds all the state: the game folder, server folder, parsed INI,
release being installed and running server all affect each other, and splitting them up would mean a lot
of cross-notification for a proof of concept.

Launcher state (chosen folders, installed build, the remembered accounts) is stored in
`%LOCALAPPDATA%\ViewLight\launcher.json`. The launcher's own profiles and everything they have earned are
in `profiles.json` beside it, with each profile's picture and banner under `profiles/<account id>/` - kept
apart because a corrupt badge file must not cost the user their folders. Downloaded archives and extraction
scratch space live alongside both. In portable mode all of it sits in the launcher's own folder instead -
see below.

## Portable mode

The launcher is portable when a `portable.txt` file sits next to its executable; **Settings → Portable
mode** is a toggle that creates or deletes it, and an empty file does just as well - which is what makes
the folder portable on arrival on a machine that has never run it. `PortableMode.Apply` is the first thing
`Program.Main` does, before Avalonia or any view model exists, because every path the launcher derives has
to be settled before anything asks for one.

| | Normal | Portable |
| --- | --- | --- |
| Launcher data (settings, cached art) | `%LOCALAPPDATA%\ViewLight` | `<launcher folder>` |
| Default game folder | whatever *Detect* finds | `<launcher folder>\game` |
| Default server folder | `%LOCALAPPDATA%\ViewLight\server` | `<launcher folder>\server` |

Because `LauncherPaths` is the single source of those paths, making the launcher portable was a change
to one class rather than a change to every caller. The marker is a file rather than a build flag so the
same download is portable or not depending only on what is beside it.

**Stored paths are relative when they point inside the launcher.** `launcher.json` records `game` rather
than `C:\stick\game`, which is what lets the folder be moved - to another drive, another user, a USB
stick - and still find everything. A folder *outside* the launcher is stored as the absolute path,
because no relative path describes it and pretending otherwise would break the moment the launcher moved.
Both translations live in `LauncherPaths.ToStoredPath`/`ResolveStoredPath`, applied on save and on load,
so the rest of the launcher only ever sees real paths.

### Copying the game in

Settings → Portable mode offers to copy the game folder into the launcher's folder, which is the
difference between a portable launcher and a self-contained one. `GameClientCopyService` enumerates the
source, checks the destination drive's free space, and copies with progress and a cancellation token -
several gigabytes is long enough that a silent button would look broken. It refuses a folder that is not
a Wizard101 install, refuses when either folder is inside the other (copying a folder into itself would
grow without end, and into its own parent would overwrite the files being read), and never touches the
source install, so a failure leaves the user with a working game.

### A Steam copy stays Steam's

This is the caveat that matters, and it is deliberately reported rather than worked around:

> A Steam copy of Wizard101 loads its own `bin\steam_api.dll`, which talks to the Steam client. Copying
the files does not remove that dependency: with no Steam client installed and signed in, the game will
not start, however the files are laid out. A standalone install from KingsIsle's website ships neither
that library nor `bin\steam_appid.txt`, and so has no such dependency.

Launch arguments do not help here. The client's `-ST` is Imlight's way of being *told* that an account is
a Steam one - it is what a Steam-linked account needs to sign in to Wizard101's own server, and what a
standalone account must not be sent - so it is about which server will accept the account, not about
whether Steam has to be present. `GameFolderLocator.IsSteamInstall` recognises such an install by
`bin\steam_api.dll` or `bin\steam_appid.txt`, and `GameClientCopyService.SteamCaveat` is reported before
the copy starts and again when it finishes, so nobody concludes that copying the files was enough. The
honest recommendation is to copy a standalone install and to leave a Steam copy where Steam put it.

## Tests

```bash
dotnet test ViewLight.slnx
```

488 tests covering the parts most likely to break quietly: INI round-tripping and merging (including
unknown sections such as `[CrownShop]` surviving an update), the KIWAD
reader, game folder detection and the too-deep-path migration, free port selection, release selection,
item list parsing, the starter kit limits, the config repairs and the live loopback probe that decides
whether a "self-hosted patch server" is really there, the crash-exit-code explanation, the client's
working directory and start-up arguments, the leftover-server guard (driven against real processes, one
from "this" install and one from "another" that must survive), the log file being resolved to the daily
file Serilog writes rather than the name in the config (including the first run of a day, when today's
file does not exist yet), the warm-up logic driven against real child processes and real sockets -
including its refusal to believe a readiness line an earlier run left behind - the job object that ties
the server to the launcher (driven against real processes, including one spawned by a job member, which
must die with the job), the runner actually putting the server it starts into that job and saying so when
there is no job to put it in, the close-prompt rule and its persisted "don't ask me again", the theme
normalising anything unusable back to following the desktop and surviving a value written by a future
version, the art cache refusing an HTML answer, a refused request and a truncated download without
leaving anything behind, replacing a cached file that is not a whole image, serving a cached copy
with the network unavailable, the player database being read and refused when its documents are not the
shape the launcher knows - a session key with no key in it, a document with no id, an admin hash that is
not the password in `Imlight.ini` - a session key being reused, replaced, or issued (and never written at
all when the guard has not passed), the rule that decides whether the client is signed in at all,
including that another machine's key is never handed over - the portable marker and the relative-path
round trip (a portable launcher writes `game` into its settings file and reads a real path back out),
the copy service refusing a folder that is not a Wizard101 install and refusing to copy a folder into
itself, the Steam-install detection the Steam caveat hangs off, and the build ordering that decides
whether the user is told to update the game or the server - and a full install-then-update cycle that
asserts user settings, runtime data, a deliberately chosen database port and a chosen patch server all
survive.

The profile system is covered the same way: the log parser driven by the exact lines Imlight writes,
playtime and session counting across polls (including a gap between two of them not being credited), duel
attribution refusing to hand a win to nobody in particular, badges never granted twice and never taken back
by a later read that has not caught up, wizards, quests and the command log read from the database, which
remembered accounts may be shown and which may not, and the words the screen uses when there is no profile
to show.

## Verified against a real install

The patch server repair was checked on the server folder this launcher had installed, not just against
tests. `ServerConfigRepair.Apply` rewrote exactly one line - the patch server URL - leaving every
comment, blank line and other key untouched, and the Director then booted for real:

```
INF Patch server at URL https://patcher.phill030.de/V_r803238.Wizard_1_610 found and set
INF Attempting to download file ... /Data/GameData/Root.wad. Content size: 281 MB
INF Login server created with name Imlight.Login under port 12000.
INF Game server pool registered new game server Imlight.Game.Centaur (realm: Centaur) on port 12333.
INF Embedded database initialized.
INF Created admin account.
INF Imlight may now be connected to.
```

Ports 12000, 12333-12335 and 12500 were listening, the embedded database answered on its own port, and
the process was then shut down cleanly. Before the fix the same install died at `Root.wad` with exit
code `-532462766`.

The launch edge cases were checked the same way, on that install:

- **Working directory of the client.** `ClientLauncher.Launch` started the real
  `bin\WizardGraphicalClient.exe -L 127.0.0.1 12000`. The log it wrote into `bin\` says
  `Loaded PatchConfig.xml`, `revision 'r806919.Wizard_1_610'` and has no `Could not open ConfigFile`
  line. Started from the game folder instead, the same client logged that error plus eleven failed
  asset loads and never got past its first screen.
- **A server already holding the ports.** A real Director was started and left running on
  12000/12333-12335/12500/42552, then `ServerRunner.Start` was invoked exactly as the Play button does.
  The Log reported `Stopped the leftover server process (PID …)`, and the Director it then started
  claimed the same ports and reached the ready state - its own log shows the second boot with no
  `[ERR]`/`[FTL]` lines at all.
- **The tie to the launcher.** The real `Imlight.Director.exe` was started the way Play starts it and
  booted to `Imlight may now be connected to.` with 12000 and 12333-12335 listening. Closing the job -
  the exact kernel action of a killed launcher - took the Director with it: no `Imlight.Director` left
  running, port 12000 free again, and the runner reporting the process gone. The other close path was
  checked on the same install: `ServerRunner.Dispose`, which is what the window's shutdown calls, killed
  the server, released the ports, left the runner in the stopped state and logged no unexpected-stop
  error on the way out.

The full workflow has been played through by hand on this machine with this launcher: server install,
**Play**, the client logging in against the warmed-up server, and character creation. The client has
therefore been launched against a live server end to end.

What that still does **not** cover: the crash-reporting path was not forced on a real failure, because
forcing one means deleting the cached `Root.wad`; and the close prompt was not clicked through in the
GUI. The dialog was verified to parse and build under the real Avalonia platform, and the rule and its
stored preference are covered by tests, but nobody has been shown that prompt by a real close.

## A Linux build

Not scheduled, and not started. Recorded here because Imlight ships a Linux build, and the shape of the
Windows-only code decides how cheap that later port is.

**The honest caveat first:** Imlight's *server* has a Linux build, but the *game client* is a Windows
binary. A "Linux build" of ViewLight would mean a native launcher and a native server, with the client
running under Wine or Proton - so the game folder is a prefix rather than a directory tree, and locating
it is a different problem from the one `GameFolderLocator` solves today. Worth confirming before
anything is built on it.

What is Windows-specific right now, and where it is kept:

| Windows-only thing | Where | What Linux needs instead |
| --- | --- | --- |
| Job object that stops the server with the launcher | `ProcessJob` | A process group killed on exit (it already returns nothing off Windows, and the runner says so in the Log rather than pretending) |
| Steam registry keys, `Program Files` paths | `GameFolderLocator` | Steam's `libraryfolders.vdf` under `~/.steam`, and Proton prefixes |
| `bin\WizardGraphicalClient.exe`, `Data\GameData\root.wad`, `Imlight.Director.exe` | `GameFolderLocator`, `ClientLauncher`, `ImlightConfig` | Per-platform names and separators, asked for rather than hard-coded |
| The `Imlight-win-x64*.zip` release asset | the installer | The Linux asset of the same release |
| `WinExe`, `app.manifest` | the project file | Nothing, on Linux |

Most of the Windows-ness is already behind those four seams, so the rule for new work is simply: **ask
the seam, do not test the platform inline.** New platform facts belong in `GameFolderLocator`,
`ProcessJob` or `ImlightConfig` rather than in a new `OperatingSystem.IsWindows()` branch somewhere in
the middle of the app, and no new visual effect may depend on an OS-composited material.

There is no Linux machine here to build on. A simulated environment would only smoke-test the parts
that do not touch the client; a Linux CI runner building and running the test suite is what would really
settle it.

## Not done yet

- A Linux build (above) - nothing started, the seams recorded so it stays cheap
- Real `root.wad` item decoding (above)
- Selecting a specific older release instead of always taking the newest
- **Starting straight into a character.** The account is what Play hands to the client, and characters stay
  the game's own screen (`-C [GID]` is deliberately out of scope). A wizard's GID is already read for the
  profile, so the pieces are there if it is ever wanted.
- **Anything that follows you around.** Profiles, playtime and achievements are local to this machine by
  design, and one launcher never talks to another. Sharing them would need somewhere to share them to.
- **Explaining the server's own first-boot crash.** It is reported and press-Play-again works, but the
  cause is in Imlight's asset prefetch rather than in anything the launcher controls - see
  [rough edges](#rough-edges-in-imlight-itself).

Three entries that used to sit here have been done and are recorded above rather than deleted: the
**game-first Home screen** (banner, hero Play button, status demoted to one strip), **creating an account
from the launcher** (`PlayerDatabaseProfiles` writes the same document Imlight does, from Settings), and a
**wizard's name and school** (the log first, then the game's own name tables out of `Root.wad` via
`WizardNameTable`, and the id only when neither source can name the character).

## The account card, and the achievements screen

Two pieces of the profile are not pages, and both were built around the same rule: **the launcher shows a
state it is actually in, or it says it has nothing.**

**The card in the corner of the window** is who Play signs in as - not a step on the way to pressing Play,
so it is drawn on every page rather than only on Home. Closed it is the picture and the name; clicking it
opens it into the account's banner, its name, its badge count and its score circle; clicking *that* opens
the account section. The pointer leaving it folds it back after a moment
(`MainWindowViewModel.ProfileCardShouldFold` and `ProfileCardLingerDuration`), and the fold is guarded
against the countdown expiring in the same instant the pointer comes back, because folding then is the
card vanishing from under the hand reaching for it.

The open card is a `Panel`, not a clipped picture: the banner is the **fill** of a rounded border, so
nothing rectangular is left showing outside the corners, and the bottom half is a `ProfileCardGradientBrush`
defined in both theme dictionaries - transparent to the theme's own surface. That is what lets the picture
fade into the surface behind the window and the name and score be read in the theme's own text colour,
rather than white on a scrim that only works in one theme. The picture sits bottom-left and the score
circle bottom-right, which is the same information the profile page shows, in the same order.

**The achievements screen is the profile's second page rather than a section of its own.**
`MainWindowViewModel.ShowsAchievements(section, page)` is what decides it, and the answer is *only* on
Profile: the screen belongs to the account whose badges it shows, so a launcher left on another section
would be showing badges for nobody. Opening a section always lands on its first page
(`GoTo`/`OpenSection`), so a profile card can never be left standing on somebody's achievements.

**Both views are computed from that state, so the state has to announce itself.** This was a real bug and
is now a permanent test: `ShowProfilePage` and `ShowAchievementsPage` are computed properties, and the
section was notified without them - so clicking **Profile** selected the tab and drew nothing in it,
because neither of the two views had been told to show itself. `RefreshDerived` now raises them, and
`TheProfileTabIsToldToShowItselfWhenItIsOpened` asserts the notifications rather than the view, so the
regression cannot come back through a refactor of the XAML. It is also the shape to remember when adding a
third page: a computed `IsVisible` is not a fact until something says it changed.

## Rough edges in Imlight itself

The launcher does not paper over these, and none of them are launcher bugs. They are written down because
users hit them on a first run and would otherwise read them as the launcher's fault.

| What happens | What it actually is |
| --- | --- |
| An account made in Settings before the server has ever run does nothing | There is no player database yet, so there is no collection to write into. The admin account in `Imlight.ini` is what creates it on first boot - so a server has to be started once before any account exists to be listed or written |
| The first boot downloads its assets and can then stop by itself | Nothing in the launcher's own startup path: the Director fetches `Root.wad` (about 281 MB) and dies after the fetch, unreproduced. Pressing **Play** again boots it normally, and it has not been seen to recur after a successful first boot |
| A new zone is slow to load | The zone's assets come from the patch server, and the public one is a community machine rather than KingsIsle's infrastructure. The server is local; the assets are not |

A launcher-side difference worth keeping in view: the third one is *reported* rather than detected - the
launcher cannot tell a congested patch server from a slow disk, so it says nothing about the cause and the
readme explains it instead.

## Packaging and releases

Source and builds both live at <https://github.com/Smealm/ViewLight>. Every build is published as a
GitHub **prerelease**, which matches what Imlight itself does and, more to the point, matches what this
launcher is: a proof of concept that is played with rather than depended on.

The build shipped in a release is produced by exactly two commands:

```bash
dotnet publish src/ViewLight/ViewLight.csproj -c Release -r win-x64 --self-contained true -o publish
7z a ViewLight-win-x64.zip ./publish/*
```

**Self-contained on purpose.** The launcher's audience is people who want to play a private Wizard101
server without first installing a development toolchain, so the zip carries its own runtime and needs
neither .NET nor administrator rights. The cost is size, and it is worth paying here: every alternative
ends in "install the .NET 10 desktop runtime" instructions on a page that wanted one button.

The zip root *is* the launcher folder, and that is deliberate rather than incidental - it is what makes
`portable.txt` work. A user can unpack the zip beside a `portable.txt` marker (or an empty file of that
name), and the launcher then keeps its settings, server and cached art inside that folder instead of in
`%LOCALAPPDATA%\ViewLight`. Wrapping the build in a top-level folder would move the executable one level
away from where the marker is looked for, so the archive stays flat, the same way Imlight's own Windows
archives are.

The `docs/screenshots/` images in the readme are the launcher itself on this machine, not mockups: Home,
Profile, the achievements screen behind the score circle, and a badge arriving as a toast over the game.
They are the one thing in the repository that is not code, which is why they are named after what they
show rather than after the file names they arrived with.

- The admin password is intentionally not stored anywhere except `Imlight.ini`: it is read from there on
  load and written back when you save
