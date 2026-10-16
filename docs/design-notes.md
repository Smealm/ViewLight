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
4. **Warms up, then plays.** Press **Play** and it starts the patch server if there is one (and waits
   until it answers), starts the Imlight server, waits until the account server is genuinely listening,
   then launches the client pointed at it - signed in as the account chosen on Home, without the game's
   login screen.
5. **Updates without losing anything.** Updating replaces the binaries and merges your settings back
   over the new defaults.

## Requirements

- Windows (the Wizard101 client is Windows-only; Steam detection reads the Windows registry)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build; the app targets `net10.0`
- **A .NET runtime the launcher's own database can run in**, which the release carries beside the
  executable and a build from source does not: the RavenDB the server ships is framework-dependent even
  though Imlight's own build is not. The copy beside the launcher is used, a machine-wide install is what
  it falls back to, and the first screen names what is missing when neither has it - checked there rather
  than discovered at a database that will not start
- An Imlight install - the launcher stands down without one, and says so rather than showing a Home screen
  whose only working button is a download
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

### Imlight is a requirement, and the launcher stops and says so

**There is no launcher without Imlight.** ViewLight is a launcher for it: the server is what it installs,
configures, runs and plays. And since the launcher's own data moved into a database run from the RavenDB
the server ships ([task 4](#4-the-launchers-own-data-moves-into-a-launcher-database---done)) - the profiles,
the playtime, the badges, kept in the launcher's own folder - the install is also where the *engine* for the
launcher's own notes comes from. So a launcher with no install has nothing to configure, nothing to start,
and nowhere to keep your progress.

That was the user's call rather than the code's: the launcher used to be able to sit there with no server
and offer a download, which is a screen of buttons that do not work. What happens now is `MainWindow`'s
fourth layer - after the folders question - which covers the whole window until there is an install it
can work with. `ImlightRequirements.For` is the rule, and what it checks is the order the pieces have to be
fixed in:

| What | Why it is needed |
| --- | --- |
| A folder | So there is somewhere to look. |
| `Config\Imlight.ini` in it | That is what "installed" means everywhere else in the launcher. |
| `Imlight.Director.exe` | Play starts it. |
| `RavenDBServer\Raven.Server.dll` | The database the launcher's own data lives in. |
| A .NET runtime that database can run in | It is framework-dependent, so something has to provide one: the runtime shipped beside the launcher, or a machine-wide install (below). |

The screen says which piece is missing in a sentence, keeps the folder box so another folder can be
pointed at, offers the download, and has **Check again** for somebody who installed .NET or copied an
install across while it was open. Nothing behind it is shown - not the navigation, not the account card -
so there is no half-working launcher to wander into.

**The .NET gap also says how to install it.** Its link is a version's page of downloads rather than an
installer, and nothing on that page says which of its several downloads this launcher wants, so the screen
says it: press it, take *ASP.NET Core Runtime* for Windows x64, run the installer, come back and press
**Check again**. It says the last part because it is true - the runtime answer is forgotten when the button
is pressed, so a restart is not needed - and it is the last step precisely so that installing the runtime
and being believed look like one action rather than two.

**Check again also settles the launcher in.** Once nothing is missing it does what a start with the
dependency already there would have done: opens the server's own RavenDB, so profiles and accounts are read
without starting the server. Without it, somebody who installed .NET would get the launcher back with its
own database still shut until the next run - the gate would have lifted and nothing else would have
changed. The button is still not a *Play*: nothing here starts Imlight.

**A .NET runtime has to come from somewhere, and it is not obvious why.** Measured on a real install:
Imlight's Windows build is *self-contained* (`Imlight.Director.runtimeconfig.json` has
`includedFrameworks`, with `coreclr.dll` and `hostfxr.dll` beside it), so Imlight itself needs nothing from
the machine. The RavenDB inside it is the other way round: `RavenDBServer\Raven.Server.runtimeconfig.json`
names `Microsoft.NETCore.App` **and** `Microsoft.AspNetCore.App` as shared frameworks, and that folder
holds 198 DLLs with no ASP.NET Core among them. `EmbeddedRaven` starts it with a `dotnet` host, so a
runtime has to exist and has to have the frameworks the database asks for. What it asks for is read from
that file rather than hard-coded, and the rule is the runtime's own roll-forward: the same framework, the
same major, a version at least as high. An Imlight that ever shipped the database self-contained would
need nothing here and the screen would not ask for anything.

**Where that runtime comes from is the launcher's own release, not the user's machine.** The release puts a
whole .NET installation in a `runtime` folder beside the executable and the database is started with that
- see [task 16](#16-the-runtime-ships-with-the-launcher-so-nothing-has-to-be-installed---done), which is
where the choice between the two lives. So the screen above is what is left for the cases where neither
works out: a build from source, which carries no runtime, and a machine with none installed either. The
launcher itself still **starts** on a machine with no .NET at all - the launcher, the folders question, and
this screen all work - and only the server's database is what cannot run. The screen uses that shape
deliberately: it says the launcher itself starts without .NET, names the frameworks the database asked for,
and links to the version's page, so a machine missing one dependency is walked through it instead of
meeting a launcher that never appears, does nothing, or dies with an error nobody can read. Nothing starts
a database before that screen is satisfied: holding one open for a launcher nobody can use is work for
nothing.

**One state that is now unreachable, kept anyway.** Home's one button used to say *Download server* where
there was no server, and it is what downloaded one. It cannot any more - the launcher does not show Home
without an install - so `PrimaryActionLabel`'s download state and the branch behind it are now defensive
rather than reachable. They stay, because the button's two jobs are the button's two jobs and deleting half
of it would make the gate's screen the only place a download could ever come from; what is written down
here is that Home is not where a user meets it.

## The tabs

| Tab | What it is for |
| --- | --- |
| **Home** | The Wizard101 banner and its Play button; everything about the server sits in one quiet strip beneath it |
| **Profile** | What this launcher knows about the account Play signs in as on this server: picture and banner, playtime, standing, the score circle, and the wizards on it |
| **Profile → Achievements** | Everything that account has earned on this server, grouped by category, with a milestone card for each ladder. A page of Profile rather than a tab of its own, opened from the score circle |
| **Settings** | Both locations, every value ViewLight writes into `Config/Imlight.ini`, the patch server (Aurorium) and its own config, the theme, and the launcher's own close prompt |
| **Log** | Everything the launcher, the server and the patch server have said, drawn as one terminal |
| **About** | Versions, links, and what is still stubbed |

There is deliberately no separate menu for starter items: it is a setting, so it lives in Settings
alongside everything else.

### Home is the game, not the server

Home is built around one thing: getting into Wizard101. A wide banner carries the game's own art, its
wordmark, and the Play button; below it the server is present but deliberately unremarkable - a status
dot, one line of state, and the start/stop/log buttons, followed by the build and update panel. The one
row added since is the patch server's, drawn only while the launcher runs one and only when Play has
something on screen to attach it to, so a launcher with no patch server looks exactly as it did. Which
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

### The Log is a terminal, not a list of rows

**What was asked for.** The Log drawn as a terminal rather than a row per line, with the display faults
that came with the rows fixed: art and tables printed by a process arriving in the shape they were written
in, and a copy taking a whole region rather than one line. Checkboxes for the launcher, the server and the
patch server, so one of them can be quietened down. And a line that repeats over and over folded to a
single line with the count, openable so the lines can be read and copied, and foldable again.

**The rows were the fault, not the formatting.** Every line was a `ListBoxItem` with a three-column grid
around it, so a line had to fit the column it was given: a process's ASCII art was re-wrapped and the
indentation that made it readable was gone, and a `SelectableTextBlock` per row means a drag selects one
row. `LogDocument` now builds the Log as **blocks**: one run of lines drawn as one piece of monospace text
per block, never wrapped, with a horizontal scrollbar instead of a fold.

**A block is a run of lines with the same severity and source**, which is what lets a block carry one
colour without any per-line decoration, and it is capped at 200 lines. The cap is not cosmetic: a block is
one piece of text to lay out, so appending a line only ever re-lays out the last block rather than the five
thousand lines behind it. Blocks are also why a copy takes as much as the block - up to 200 lines - rather
than one row. One thing to know about the cost: the Log is drawn as a document with no virtualization
behind it, so a Log that alternates between sources line by line is a lot of small blocks on screen at
once. Five thousand lines is the ceiling that keeps that affordable, and a real session - a launcher's own
messages, then long stretches of one process - is a handful of blocks, not hundreds.

**Appending touches the bottom block and nothing else**, except when the Log is full and the oldest line is
dropped, which takes the first line off the top block and drops the block itself once it is empty. That
keeps a busy server's output from costing a rebuild of the whole Log per line, and it means the blocks
always hold exactly the lines the Log is keeping - which is asserted: building the Log line by line and
building it again from the lines it holds have to draw the same screen.

**Folding is decided by everything about a line except when it arrived.** Thirty identical lines are one
line with `+30` where the severity tag would be - the count goes in a column that is already being read, so
a folded run does not disturb the alignment of the lines around it - and pressing it swaps the marker to
`-30` and draws the lines underneath with the time each one actually arrived. The fold is remembered by the
run's first line rather than by the block object, which is what lets a fold stay open across a filter
change: changing a box rebuilds the whole Log, and a run that folded itself back up under somebody
halfway through copying it out would be the Log undoing their work.

**The three boxes are a source, set where a line is written.** Launcher lines are the launcher's;
everything a runner reports carries the process it is about, its own narration included - somebody
quietening the server down wants the line that says the server stopped as much as they want the line the
server printed. Hidden lines are still held and still count against the 5,000-line cap, so unticking a box
is asking not to look at something rather than asking for it to be thrown away. The consequence is
recorded rather than hidden: with the server hidden and the server spamming, its lines are what the cap
drops - the line under the Log says how many are being held back, so a quiet-looking Log explains itself
to an empty one that says nothing.

**Verified, and how.** `LogDocument` is tested on its own: what folds and what does not, opening and
folding back, the filters and what they leave, the 200-line cap, the 5,000-line one, a folded run counting
down as its oldest lines are dropped, the two ways of building the Log agreeing, and the columns a line is
written in. The launcher has no view test in it, so the layout itself was checked against a real Avalonia
window - hidden, but real, so the styles and the fonts were the launcher's own - and it holds: the fold row
is at the same pixel as the text under it and exactly one line tall, a 1,600-pixel line of art is drawn as
one line with the Log scrolling sideways, the message column is the same for every kind of line, opening a
run draws every line at that column, opening a run above the viewport does not drag the page to the bottom,
and the tail is followed as it arrives.

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

**Skipping the tutorial is one setting with three consequences, and the setting now says so.**
`TutorialDisabled` puts a new character straight into the starting zone, but three things the tutorial does
are not done behind its back: the school's first spell is not granted, the starter items are not equipped,
and the tutorial's own items can be granted a second time. All three are the game's behaviour rather than
the launcher's, and all three are fixable in the game - walk to Ravenwood, learn the school's spell from the
trainer, then equip the starter items out of the inventory - so the card carries that as a warning while
the setting is on rather than leaving it to be discovered by a character that starts wrong. The warning
lives in the view rather than in the view model because it is a paragraph of instructions with nothing to
compute; everything else on that card is a value the launcher writes.

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

#### Running one instead of visiting one

The repair above makes a session *work*; it does not make it local. Imlight still fetches the game's own
files from the community's machine, which is the last part of the stack that does not live on this PC -
and the reason a zone load is slow on a busy day. So the launcher can run the patch server itself:
[Aurorium](https://github.com/Revive101/Aurorium), the Revive101 project's own file server, which fetches
the assets and serves them over HTTP.

**Install is one file.** Unlike an Imlight release, Aurorium publishes `aurorium.exe` directly rather
than an archive, so installing it is a download, a copy, and a config file - `PatchServerInstallService`
had no unpacking to do and no overlay to merge. The release lookup is therefore a second repository to
ask, which is why `GitHubReleaseFeed` exists: the refusal messages, the prerelease ordering and the JSON
parsing are the same for both, and they now live once.

**The config is written, but only when there is a reason.** Aurorium generates its own `config.toml` with
its author's defaults, and the launcher has an opinion about the endpoint - because that is the address
Imlight is pointed at - and about the three fetch settings below it, because a first fetch that finishes
is the difference between a local patch server and a decoration. `PatchServerConfig.EnsureConfig` writes
the whole file in Aurorium's documented shape when it is missing or names another port, and otherwise
only when the file is **an unmodified copy of a template this launcher wrote earlier**: the two templates
it has shipped are compared byte for byte, so a machine set up before those settings changed is updated
rather than left running the cadence it was set up with.

The comparison is what keeps the other half true. A config somebody has grown by hand (a different
`fetch_interval`, a bigger download queue) matches no template of ours, so it is never rewritten - the
file stops being ours the moment somebody edits it - and the test asserts both: the hand-written line
survives, and the untouched copy of the old template does not.

The fetch settings are the launcher's own values rather than Aurorium's, and each one is there for a
measured reason: `fetch_interval` is 300 seconds against its 28800 (see below - it is the retry cadence,
not just the new-revision timer), `concurrent_downloads` is 4 against its 2, and `file_logging` is on
because a fetch that will not finish should leave something to read.

**The default port is not arbitrary.** Aurorium's default and Imlight's stock `PatchServerInternalUrl`
are both `127.0.0.1:12369`, so a patch server on the default port is the one Imlight already looks for -
and `ServerConfigRepair` leaves the stock address alone when something is genuinely listening on it. The
two constants live in different layers (`LauncherSettings.PatchServerPort` in `Models`,
`PatchServerConfig.DefaultPort` in `Services`), so a test keeps them in step the same way the section
names are kept in step.

**Readiness is the endpoint, and it is probed like the server's.** Aurorium logs through `tracing` and
prints nothing that reliably means "the file server is bound" - any such line changes with a build or a
log level. What does mean it is a TCP connection to the address it was told to serve on being accepted,
which is exactly what the client will do, so `PatchServerRunner` probes the port and then waits a short
settle window (2s, against the Imlight server's 10s: the HTTP server behind the socket is up almost
immediately). The measured run below is what the settle window is for.

**Answering is not serving, and that is why nothing asks.** Aurorium binds its endpoint seconds
after it starts and only then begins work: it connects to Wizard101's own patch server, fetches
`LatestFileList.xml`/`.bin` for the new revision into `{save_directory}/{revision}/`, writes that revision
and its asset list **into its database**, and only then downloads the assets themselves into the same
folder. Its routes serve off the disk. Between "the port is open" and "the files are there", every real
request is a 404 - and Imlight reports it as:

```
ERR : Error while downloading file http://127.0.0.1:12369/V_r803238.Wizard_1_610/Data/GameData/Root.wad
      from patch server endpoint: Response status code does not indicate success: 404 (Not Found)
```

and then dies, because it cannot load `Root.wad`. Worse, Imlight's own reachability check makes this
invisible: `GetServerUrlStatus` sends a `HEAD` to `{url}/V_{revision}` and treats *any* response - 404
included - as "the endpoint is available", so Imlight walks into this believing the patch server is
fine.

`GET /revisions` does not answer it either, and that is the trap: the revision row and its asset list are
written **before** the downloads start, so a fresh patch server lists a revision whose files are not on
disk yet. `LatestFileList` is not a useful proxy either, because Aurorium serves it from the requested
revision directly, bypassing the asset lookup - so it can be there while `Root.wad` is not. The only
question that would answer it is a request for the files themselves.

**What the first fetch actually costs, measured.** Aurorium's own log at `trace` level, run against this
machine's install, is the source for the numbers here. Revision `V_r806919.Wizard_1_610` is **3825
assets, 18.39 GB of them**, of which `Data/GameData/Root.wad` alone is 281 MiB (295405277 bytes, summed
from the revision's own `LatestFileList.xml`). The upstream chain is worth
writing down once because none of it is configurable and all of it matters: the patch server on
`patch.us.wizard101.com:12500` speaks Wizard101's own protocol over a raw socket and hands Aurorium two
URLs - the manifest at
`http://versionak.us.wizard101.com/WizPatcher/V_r806919.Wizard_1_610/Windows/LatestFileList.bin` and the
asset prefix `http://versionak.us.wizard101.com/WizPatcher/V_r806919.Wizard_1_610/LatestBuild` - and every
asset is fetched from `{prefix}/{file}`. The launcher configures none of this beyond the host and port it
was handed, because there is nothing else to configure.

At the several MB/s this connection manages, the whole revision is over an hour of continuous running,
which is what a correctly configured patch server does when it is left alone: **1169 of the 3825 files and 2.2 GB in the
nine minutes it was observed here**, with no failures on the small files. Two things stop it:

- **Aurorium gives every request two minutes.** A file that cannot arrive inside that is aborted -
  `Failed to read chunk: error decoding response body` - and the `.part` is deleted, so nothing is
  resumed. Measured twice in a row on this machine: `Equipment-HighDetail.wad` started 15:00:35 and failed
  15:02:35, `Equipment-WorldData.wad` started 15:02:28 and failed 15:04:28. Exactly 120 seconds each. At
  this connection's speed that is a coin toss for `Root.wad`, which is why the fetch needs more than one
  attempt at it.
- **A pass that ends sleeps for `fetch_interval`.** Aurorium's default of eight hours is the whole of what
  "the patch server is up and nothing is happening" looks like: one aborted big file and the revision waits
  most of a day. The launcher writes five minutes instead.

The third thing was the launcher's own. The fetch only ever advances while the process is alive, and
stopping the server used to stop the patch server with it - so the shape of a first session was: Play
starts Aurorium, Aurorium fetches for two minutes, Imlight 404s on `Root.wad`, the session ends, the stop
that follows takes the download with it. 2.2 GB of an 18.39 GB job per attempt - and the files it gave up
on, which are the ones that matter for a boot, still not there. That is what "the patch server has no assets and nothing happens" was.

#### Filling it in from a copy of the game

**The insight is that there is nothing to guess at.** A patch server does not choose a revision: Wizard101's
own patch server hands it one over the socket, and Aurorium mirrors exactly that revision - here
`V_r806919.Wizard_1_610`, fetched from `versionak.us.wizard101.com/WizPatcher/...`. A Steam install of
Wizard101 comes off the same channel, and its own `Bin\revision.dat` says `r806919.Wizard_1_610`, which is
the revision the patch server's folder is named after. The two are the same build - and that is checkable
rather than merely plausible, on the real files:

| Compared | Result |
| --- | --- |
| The revision's own file list (3825 files, 18.39 GB) against the Steam build beside it | 3725 present, **0 at the wrong size**, 100 absent - all of `Windows\Bin\` (3) and `PatchClient\` (97) |
| Every file the patch server had already fetched against that build, byte for byte | **2700 compared, 0 different** (78 s, 5 GB) |

**The patch server itself cannot close that gap, and that is the point.** Aurorium fetches a file only when
it is missing and never looks again at one it has - `if save_path.exists()` - and it aborts a download that
runs past its own two-minute limit. So the large files are not slow, they are unreachable: every pass starts
them again, every pass gives up on them, and a server can sit at "fetching" forever while those files are
never written. Handing it a copy is not a shortcut around a slow job; for those files it is the only route
there is. Measured on this machine the day it was built: the patch server held **2703 of the 3825 files**,
and the game folder next to it could supply **1025 of the rest, 13.76 GB** - the remaining 97 being the
`Windows\Bin`/`PatchClient` files that neither has. Copying 13.76 GB is minutes of local IO; fetching it is
over an hour, and the `Equipment-WorldData.wad`s of the revision never finish at all.

**Exactness is the whole safety argument, and it is enforced rather than assumed.** Three rules, and they
are the difference between filling a patch server in and inventing one:

| Rule | Why it is there |
| --- | --- |
| The revision being filled is the one **the server asks for**, and only ever that one | `GameRevision` is both what Imlight asks the patch server for and what the server accepts clients on, so filling in the client's own revision instead would leave the server asking for a folder the patch server does not have - the 404 the whole arrangement exists to avoid. This is the safety net the copy is, rather than a second way to collect a revision (entry 23) |
| The install it comes from **is** that revision, read from the game itself (`PatchServerPreload.IsTheBuild`) | the revision folder is named after the revision and only exists once Aurorium has fetched it, so a build of any other revision has nowhere to go and is refused rather than partly given |
| Not one file the revision names may be present at a different length, in the whole build - any one of them refuses the copy completely | the right name at the wrong length is a different build's file, cannot be repaired later (the patch server only ever fetches what is missing, so it would serve that file forever) and is evidence that this install is not this revision at all. A machine with one of those is a machine nothing may be taken from (entry 23) |
| Files the revision names that this install does not have are left for the patch server | they are not this machine's to give, and inventing a file for a revision is the one thing this must never do |
| Nothing is overwritten, and every file is written as `<name>.preloadpart` and then moved into place | what a patch server already has is what it is prepared to serve, and Aurorium skips by name alone - so a copy interrupted halfway must not leave something that looks downloaded. The suffix is deliberately not `.part`, which is Aurorium's own name for a download in flight |

**The plan comes from the patch server's own file list**, `LatestFileList.xml` in the revision folder,
written by Aurorium on a first pass before any download - not from a list the launcher fetches itself. That
keeps the two views of the revision identical by construction, and it is why the card in Settings says there
is nothing to plan against until the patch server has started on that revision at least once: an unstarted
patch server has no revision folder. (The endpoint binds before the file list is written, which is why the
plan is worked out again every time the patch server's state changes rather than only when it comes up.)

| Piece | What it does |
| --- | --- |
| `PatchServerManifest` | reads a revision's `LatestFileList.xml` for paths and sizes (`TryRead` returns nothing rather than throwing, since a folder being written to is read as half an XML document) |
| `PatchServerPreload.Plan` | what would be copied and what would not, counted out of that file list: already held, held at another size, not in the build, and in the build at another size - which refuses the whole copy (entry 23). Nothing here writes |
| `PatchServerPreload.RunAsync` | the copy itself: off the UI thread, cancellable, resumable by being re-planned, never throwing - a failure is a line in the report |
| `MainWindowViewModel.DescribePreload` | the sentence under the folder box: how many files of the revision it holds, how many of the rest are here, and how much they weigh |
| `PreloadPatchServerCacheCommand` | the button in Settings and nowhere else: Play does not copy, so this is a deliberate act about the files the download cannot finish |

**Play does not copy anything into the patch server, and this is the decision this section changed its
mind about.** The copy used to be offered before every session, once per revision, with the patch server's
own download cast as the long way round. That is backwards. A patch server *is* a thing that fetches a
revision: it writes the revision's file list, asks Wizard101's own patch server for what that list names,
skips what it already has, and serves what it has written. The launcher dropping files into its folders is
not a faster path through that, it is a path around it - the files arrive without the patch server having
downloaded them, so it has no record of them beyond a name it skips, and whether it indexed or verified
them is not something the launcher can see. Measured on this machine the copies were identical, which is
what made the shortcut defensible at all; but "we compared the bytes" is not "this is the workflow the
software is built around", and the failure mode when they differ is a quest that breaks hours later with
nothing on screen to connect it to the copy.

So the download is the path, and the launcher's part in it is to report it. The copy is still there - the
button under Settings → Patch server, where it is pressed on purpose - because a download is not always
enough on its own: Aurorium aborts any request that runs past its own two-minute limit, and the files large
enough to run past it are ones it can never finish whatever the connection. That is a known obstacle to
press a button about, rather than something for Play to do on the way past, and everything the plan says is
still true - the revision's own file list, the sizes in it, nothing overwritten, nothing invented.

#### Watching it fetch

**The patch server does not expose its progress, so the launcher measures it.** Aurorium's HTTP API is
`/revisions`, `/latest` and `/{revision}/{file_path}`, and nothing about a fetch in flight; its progress
bars are drawn with `indicatif` on its own console, and its database knows which assets *should* arrive
rather than which have. What it does leave behind is the two things a measurement needs: the revision's own
`LatestFileList.xml`, written before the first asset is fetched, and a folder of the files it has managed to
write. `PatchServerFetch.Measure` counts one against the other, off the disk, and asks the patch server
nothing - which is also why watching a fetch cannot perturb it.

Four rules make that count mean what it is shown as:

| Rule | Why it is there |
| --- | --- |
| A file is held when its length is the length the revision gives it | Aurorium skips a file by name alone, so anything at that name is a file it will not fetch - but a name at the wrong length is another build's file, and calling that held would overstate what is here |
| A file is looked for in the patch server's other revisions as well | an unchanged asset between two revisions is served out of the revision that introduced it and never written into the newer folder, so counting only that folder would call a revision thousands of files short that it has been serving all along |
| `.part` bytes count as progress, and as nothing else | a download in flight is most of a large file's bytes already on disk, which is what makes the figure move while one file is being written rather than only when it finishes - and it is not something the patch server can serve, so it is counted apart from the files it has |
| A preload's own staging name is neither | `.preloadpart` is a local copy on its way in; it is counted when it is moved into place, which is exactly when the patch server counts it too |

**Which file it is on, and how big that file is, is most of what somebody actually wants to know.** The same
walk that counts the revision finds the `.part` files Aurorium writes while a download is in flight - its own
name for a file that is not there yet - and names each against the revision's own file list, so what is said
is `Equipment-WorldData.wad (12.3 MB of 281.4 MB)` rather than a percentage of something unnamed. Several are
in flight at once, so the count comes with it and the leading name is the one written to most recently: one
name presented as "the" file would be a guess dressed up as a fact. The Log gets the same sentence whenever
the file changes and once a minute besides - a line per sample would bury everything else the launcher has to
say about a session that is mostly an hours-long download, and no line at all would leave the one thing a
first run spends its time on invisible in the place the launcher keeps everything else.

**And whether the revision being fetched is the one the server asks for, which is the one question about a
fetch that is not about progress.** A patch server fetches what Wizard101 serves and nothing can ask it for
something else, so a server pinned to another revision gets a 404 for its own `Root.wad` and then sits at
"warming up" forever. Both revisions are named in that sentence, because which of the two is behind is the
whole of what to do about it, and the mismatch is said the moment it is seen rather than on the next interval
- it is not progress but an answer to why a session will fail. The same failure is also read out of the
<i>server's</i> log (`ServerLogFacts.PatchServerRefusedAFile`), which is where it exists in the server's own
words, and reported on Home with what the launcher does about it; the two halves are the same fact seen from
either end, and they say the same thing.

**The estimate is a measurement too.** `PatchServerFetchPace` times the bytes that have landed. A rate is
taken only from a window of at least fifteen seconds - a single slow sample would otherwise become an absurd
figure - and each window's rate is blended into the one before it rather than replacing it, so the number on
screen settles instead of flickering. **A fetch that has stopped moving is said as such**: a whole window
with nothing in it drops the estimate and says so, because Aurorium gives up on a file that runs past its
request limit and then sleeps `fetch_interval` seconds before trying it again, and a countdown that went on
running through that would be a lie the user could watch not come true. Nothing is predicted from nothing:
an estimate needs its first window, and until it has one the line says how much is left and that there is no
estimate yet.

**The patch server's own row is not one of the lines.** Home's row for it is the process and stays that way;
what that process is fetching is the paragraph underneath it, and the same line and bar sit in
Settings → Patch server, where a patch server started by hand is watched from. It is the one thing about a
patch server that decides a session - Imlight asks for `Root.wad` during its own boot and reads it out of
its own cache when the patch server has not got it yet - so a session about to fall back to a cached copy is
one started next to a line saying why. The watch runs exactly as long as the patch server does, because that
is the only time a fetch advances, and it is there for a patch server started from Settings as much as for
Play's own.

**The first version of this asked exactly that, and it is gone.** `PatchServerAvailabilityProbe` asked for
`LatestFileList.xml`, `LatestFileList.bin` and `Data/GameData/Root.wad` under the revision a session
wanted - three requests whose bodies were never read, so asking about a 281 MB archive cost one response
header - and then acted on the answer: the row on Home grew a second state (*Running — fetching files*),
Settings grew a line saying what was missing and a *Check again* button, a half-minute watch asked again
by itself, Imlight was repointed at the public patch server whenever the answer was no, and a further rule
existed to put the launcher's own address back afterwards. Every one of those is a second-order
consequence of a question whose answer changes under it, and none of them are the launcher's to decide.

What is left is what the launcher actually knows: **the address and the port, which it was given.** While
its own patch server is up, Imlight is told to use it, and the patch server serves what it has. Nothing
asks whether a revision's files have arrived, so a patch server still fetching its first revision is a
session that fails the way any patch server serving nothing fails - Imlight reports the 404 and stops -
rather than one the launcher quietly sends somewhere else. The one request still made to a patch server is
`GET /revisions` (`PatchServerRevisions`), and it is made for one reason: the revision sync below needs
to know which revision it holds.

**The fetch is reported, kept alive, measured - and waited for, for exactly one file.** Five things, and no
sixth:

| Rule | Where |
| --- | --- |
| Ask once, before the server starts, whether the patch server can serve the file the boot asks for - `GET {address}/V_{GameRevision}/Data/GameData/Root.wad`, headers only, the same request Imlight makes first | `PatchServerServing` |
| Wait for that one file rather than starting a server that will 404 on it, with the fetch line on screen and **Play anyway** beside it as the way out | `WaitForPatchServerFilesAsync` and `ShouldWaitForPatchServerFiles` |
| Say so in the Log when it cannot be had - naming the file, and saying the fetch keeps running while ViewLight is open | `WarnIfPatchServerIsStillFetchingAsync` |
| Leave the patch server running when the server stops **if** that is the answer, because stopping it is stopping the download | `PatchServerStopsWithTheServer` |
| Say how far along the fetch is, which file it is on, how big that file is, whether it is even the right revision, and roughly when the rest will be done | `PatchServerFetch` and `PatchServerFetchPace` |

The answer is remembered only so the fourth rule can be stated, and the Stop button says *Stop the server*
rather than *Stop the servers* while it applies - a button that promises to stop something it leaves
running is worse than one that says which of the two it means. `Stop` in the patch server card stops it
outright, because that is a person asking. Nothing is repointed on any of this: a session sent on before
the patch server has its files is one that loads `Root.wad` from Imlight's own cache if there is one and
stops on the 404 if there is not - but it stops next to a Log line that says why, and the patch server is
still fetching while the user reads it.

#### Waiting for the one file the boot needs

**This is the one place Play waits, and the reason it is one file rather than a revision.** Imlight's warm-up
asks its patch server for `Data/GameData/Root.wad` and nothing else - the rest of the revision is asked for by
the client, as it plays - so a patch server that has not fetched that file is one the server cannot finish
warming up against. Waiting for a whole revision before starting anything would be 18 GB and hours on a first
run; waiting for one file is usually minutes, and it is the difference between a server that boots and one
that sits at "warming up" with nothing on screen to explain it.

The wait is asked rather than assumed, in the same way the fetch line is: a `HEAD` on the boot file is the
same request Imlight makes first, so an answer means the boot about to start will get its files. Two things
keep it from being a trap. It is **always escapable** - Home shows what is being waited for with the fetch
line under it and a **Play anyway** button beside it, and taking that path is written into the Log, because a
session started against a fetching patch server is one that will 404 rather than find its files and nothing
else would connect the two. And it **ends by itself** if the patch server goes away, since a patch server
that is not running cannot be waited for. The sync is asked again on every pass of the wait, which is what
makes a first run work at all: the patch server has no revision to list until its own first pass has written
one, and a session that started out waiting for the server's published revision is moved onto the patch
server's the moment there is one.

**Time spent waiting is not the server being slow.** Play's own readiness deadline counts from when the
server is spawned, so the wait is added back onto it - otherwise a session that spent eleven minutes waiting
on a download would time out before Imlight had been given its chance to boot.

**Order: patch server, then server, then client.** The patch server is started and waited for *before*
the Director, because the Director reads it during its own boot - starting it afterwards would only serve
the next session. That is why the wait lives at the top of `StartServerAsync` instead of in `PlayAsync`:
Play goes through the same door, and a server started by hand from Settings gets the same order.

**A patch server failing is not a failed session.** Imlight keeps its own cached copy of `Root.wad`, so
the launcher reports the failure - Log, Home's row, Settings' state - and starts the server anyway.
Refusing to play because an optional convenience did not start would be the worse trade, and the
alternative (silently continuing) is what the row and the log lines exist to prevent. A patch server that
has not fetched its files *yet* is the one case that is waited for rather than reported through, and even
that is a wait with a button on it - see [waiting for the one file the boot
needs](#waiting-for-the-one-file-the-boot-needs).

**Imlight is pointed at it, or at nothing of the launcher's making.** `MainWindowViewModel.PatchServerAddressFor`
is the whole rule, stated as a static function so it can be tested without a patch server running: this
machine's patch server is the one that was chosen, it is installed, it is up - and then Imlight is given
`PatchServerConfig.EndpointUrl(port)`, built from the address and port the launcher was set to. Anything
else is **no change at all**:

| Situation | What Imlight is given |
| --- | --- |
| This machine's patch server chosen, installed, and up | This machine's patch server, on the configured port |
| A remote patch server chosen, or nothing installed here, or it is not up | Nothing - the config is left exactly as it is |

**One question, and the rest follows from it.** The first version of this was a checkbox called *manage the
patch server*, and the address rule read it - so a patch server this machine owned but that the user had
started themselves was one the address was left alone for, and a user who said "use a remote one" without
uninstalling anything left the launcher with two contradictory settings and no way to say which won. The
setting is now the choice itself - `UseLocalPatchServer`, on by default, offered as two radio buttons
because there is no "off" for a patch server Imlight cannot boot without - and everything hangs off
`PatchServerIsThisMachines` (that choice *and* something installed): the address, whether Play starts one,
whether Home shows a row for it, and whether a session's files may be copied into its folder. *Start it with
the server, and stop it with the server* (`PatchServerManaged`) is left to mean exactly that, the process
and nothing else.

**Asked once, on a fresh install, between the server arriving and Home.** *Whose patch server* is the one
question in the launcher that nobody was ever asked, and leaving it unasked had a consequence rather than a
default. A fresh machine had `UseLocalPatchServer` on with nothing installed to be it, so
`PatchServerIsThisMachines` was false, `ApplyPatchServerToConfig` left Imlight's address exactly as its
config had it - the public one, once `ServerConfigRepair` had replaced the stock localhost address nothing
was listening on - and every session quietly depended on a connection for the game's own files. What that
costs is not subtle once the game is played offline: a zone, a creature, or a single asset the machine has
never cached cannot be fetched, and a session asked for one is stuck there.

So the launcher asks, on a screen of its own after the Imlight download:

| Answer | What the launcher does | What it costs |
| --- | --- | --- |
| This machine's own - the default, and the one the screen argues for | downloads `aurorium.exe` there and then, into `LauncherPaths.DefaultPatchServerPath`, and Play starts it and points Imlight at it | about 9 MB, and then the patch server's own first start - which is where the revision's file list and the fetch belong, and where the launcher's line about how far along that fetch is starts counting |
| The public patch server | nothing: Imlight keeps the address its config already has | a connection whenever the game wants something it has not already cached |

Asked only where there is something to answer. `PatchServerChoiceAnswered` records that the question was
put, and the screen is gated on all three of *the folders are known*, *Imlight is installed*, and *it has
not been answered yet* - so a machine that already has a patch server is never stopped by it (installing one
is an answer), and a launcher is never asked twice. *Continue* is only available for the public answer or for
a local patch server that is genuinely installed, because an answer of *mine* with nothing to be it is not an
answer at all. The fetch itself is not started by walking onto the screen - that is still Play's - but the
screen does offer to start it, once, in the form of one button: *Prepare the files now*, which starts the
patch server and shows its progress right there (entry 23). Waving that through leaves everything where it
was, with Play starting it as before, so nothing begins by itself either way.

**The sync only ever asks this machine's patch server**, because that is the one it has an address for:
`GET /revisions` goes to `LocalPatchServerUrl`. With a remote patch server in use the switch therefore has
nothing to read and does nothing - which is a limit rather than a decision, and is recorded in
[To be added](#to-be-added).

The second row is what makes the public patch server a choice rather than a fallback, and it is a
deliberate removal rather than an oversight. An earlier version put `ImlightConfig.PublicPatchServerUrl`
back whenever the local patch server could not serve yet, and needed a second rule
(`PatchServerConfig.IsOwnAddress`, a loopback address on the launcher's own port) to tell an address it
had written apart from one the user had chosen. Both are gone: nothing rewrites an address the launcher
was not asked to write, so there is nothing to undo, and a session pointed at the public patch server or
at somebody else's Aurorium is a session the launcher has no opinion about. `ServerConfigRepair` still
replaces the unusable stock address Imlight ships, which is a different thing - that one is repaired
because nothing can answer on it.

The address is decided on every start, before the Director reads its config, because a reinstall, an
update or a hand-edit can put the public address back. Nothing is written while the patch server is down,
and nothing is written when the two already agree - which is the common case on this machine, where
`PatchServerInternalUrl` is already `http://127.0.0.1:12369`.

**A patch server can only hold what upstream currently offers, so the two revisions have to be made to agree -
and the launcher's default is to move the server's side of it.** This is the other half of a local stack, and
it is a config value rather than a process: Imlight asks a patch server for
`{PatchServerInternalUrl}/V_{GameRevision}` and accepts clients whose revision matches the same value, while
a server build pins the revision it was published with. The moment the game has been patched past that build
the two disagree - Imlight asks for a revision the patch server does not hold, which is the 404 above, and
with `EnforceRevision` on the same client is turned away at login.

**The direction is forced by which of the two can be told anything.** Aurorium decides what to fetch from one
place and one place only - the patch host in its config, which it asks for the newest file list - so there is
no way to ask it for a particular revision, older or newer, and no way to stop it fetching the one Wizard101
is serving. The server's `GameRevision`, by contrast, is a value in a config file the launcher already writes
for other reasons. So the sync - on by default - reads the revision the patch server actually holds and
writes it into `GameRevision`: the server then asks for files the patch server has, and accepts the client
that has just been updated. Both values saying the same thing is the only state either of them works in.

That is the change of mind in entry 24. An earlier build of this had it the other way round, on the argument
that moving the server is a decision the launcher cannot check: whether a given server build still runs with a
newer revision of the game is up to what changed between two releases of somebody else's software. What
turned it round is which state is *reachable*. Holding the server at its published revision leaves the patch
server the side that needs something it cannot fetch, and the hand-off below is the only thing that can give
it - which needs this machine's game to be exactly that revision, which is exactly the thing that stops being
true as soon as the game is patched. A pinned pair is therefore one that stays broken, while a synced pair
can only be wrong in ways the two projects themselves decide. Off is kept for a server deliberately held at
one revision.

Three rules, all pure and all tested, carry it:

| Rule | What it says |
| --- | --- |
| `PatchServerConfig.GameRevisionFrom` | The game revision a patch server's name stands for, and `null` for a name that is not one of its own |
| `PatchServerConfig.NewestGameRevision` | Which of several revisions is newest: ordered by the number each name carries, not by the order they were listed in, because a server that was handed a revision out of turn would otherwise have its first entry read as its newest |
| `MainWindowViewModel.RevisionToMirror` | The value to write into `GameRevision`, or `null` when there is nothing to write because the config already asks for it |

The sync has no timer and no button of its own: it rides on `GET /revisions`
(`Services/PatchServerRevisions.cs`), which is the only request the launcher makes to a patch server and
is made for this alone. So it is applied whenever the launcher starts a patch server - Play's
own start, the Start button in Settings, and the first-run screen's *Prepare the files now* - and the moment
the toggle is turned on while one is already running, which is what fills its box in without waiting for
Play. A patch server that is not up is not asked and nothing is remembered about it: the revision a session
asks for is the config's until a patch server answers.

**And it is applied again while Play waits, which is where it earns its keep.** A patch server has no
revision to list until its own first pass has written one into its database, which can be seconds after its
endpoint answers - so a machine whose first start asks for a revision the patch server has not listed yet
would wait for a file that is never coming. The wait therefore asks the sync again on every pass (entry 24),
and a session that started out waiting for the server's published revision is moved onto the patch server's
the moment it has one.

Turning it off puts back the value the config had when the launcher first wrote one, **and only that value**
(`RevisionToRestore`): if the config no longer holds what the launcher wrote - a newer server build, an
edit by hand - the value in it belongs to whoever put it there, and the rule that keeps a patch address the
launcher did not write from being rewritten keeps this one too. The `GameRevision` box is greyed out
while the sync is on rather than taking edits it is about to overwrite, and the line under it says which
of the three states it is in: set from the patch server, waiting for one, or moved on since.

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
  that account must be there, and its `PasswordHash` must be shaped the way Imlight shapes one -
  `base64(SHA512(…))`, 88 characters decoding to 64 bytes. Any document that does not look the way it
  should switches automatic sign-in off and says why, rather than acting on a guess. Writes are behind the
  same guard, and only ever write a session-key document this launcher built in full - or, since the
  password repair below, that one field on the admin account.
- **Puts the config's password on the admin account** (`AdminPasswordDrift`, `AlignAdminPasswordAsync`),
  because Imlight will not: its boot calls `DatabaseUtilities.CreateEmbeddedDatabaseAccount` with the
  password from `Config/Imlight.ini` on *every* start, but `AccountCollection.CreateAccount` refuses a
  username it already holds - so an account keeps the password it was created with, and the game refuses
  the sign-in the launcher's own Settings screen advertises.

  **This was found by measuring, and the first two versions of it were wrong.** The real install had an
  admin account created at the first boot holding `base64(SHA512("compsci"))` - the password Imlight ships
  - minutes after the config had been changed to a generated one, with the account otherwise entirely
  Imlight's own (`accounts/1-A`, a `Raven/Hilo/accounts` document beside it, `Raven-Clr-Type` naming its
  `Account` model, `AuthLevel` `"Administrator"`). Requiring the config's password and the stored hash to
  agree made that database unreadable and account creation impossible - "*"admin" in the database does not
  hash the password set in Imlight.ini*", with the same sentence blocking the Settings button - which is
  why the password's *value* is no longer part of what makes a database recognisable, and why the
  divergence is a fact the launcher acts on instead.

  The consequence to be aware of: `Imlight.ini` is the authority on that one password, so a change made in
  the game - `.account password`, which only an administrator can issue - is put back on the next read.
  That is the trade the launcher makes for the password on its own screen being one that works.

  The write itself is a whole-document `PUT` with that one field changed and the server-owned metadata
  dropped, and it carries the account's document along with the drift rather than fetching it again. That
  is the second correction: the database's single-document read does not answer on the route this Raven
  build serves - `GET /databases/{db}/docs?id=…` returns the **paged listing** whatever the id - so the
  first version sent a listing where a document was expected and was refused with
  `DocumentCollectionMismatchException: Changing 'accounts/1-A' from 'Accounts' to '@empty' via update is
  not supported`. Both errors were invisible to the unit tests (which drive a fake handler) and to the
  build; they were found by pointing the launcher at the real database and reading what came back.
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

**Creating an account is the server's job, and it stayed that way.** It used to be done from Settings: the
launcher wrote the account document into the player database itself, in the shape Imlight writes, fenced by
a guard (`PlayerDatabaseContents.IsTrustworthy`) whose whole purpose was to refuse unless the database read
back as the layout the launcher expected. It worked, and it was the launcher authoring the server's own
storage - a second author of a format only Imlight should be defining. The server has no need of it, because
it makes accounts its own way: `.account create $username $password`, typed by the admin account inside the
graphical client. So the card is gone from Settings, the code behind it is reachable from no view, and the
launcher's reads are the whole of its interest in that database. `-C [GID]` - loading straight into a
character - stays out of scope on purpose.

## Profiles, playtime and achievements

**A profile is what a Wizard101 account is to *this machine*.** The account itself is the server's and lives
in its player database; the launcher only ever reads it. Everything on the **Profile** tab beyond the
account - the name and picture it is shown under, the time played, the wizards, the achievements - is the
launcher's own, kept in the launcher's own database - one document per profile, its pictures as attachments on
it, and one document for the learned worlds - and sent nowhere. That is the design rather than an omission: achievements are earned against your own server, with
one launcher never talking to another, so joining somebody else's server earns nothing here and this one
earns nothing there.

**The one account a server starts with** is the admin account in `Config/Imlight.ini`. It is the only
account that exists before anybody has signed in, so while there is no profile to show, the Profile tab
names it, says it is the account the server starts with, and says how to make another - Imlight's own
`.account create $username $password` in the game at Hall Monitor level. It becomes an ordinary profile by itself the first time the server runs and its database can be
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
explain. Four ladders are *generated* rather than written out: one rung per school per five levels, up to the
server's own `Character.MaxLevel`; one per ten duels won; one per twenty-five creatures beaten, to a fixed top
rung, because that ladder counts kills rather than different creatures (task 11); and one for the time played - half an hour, an hour, and
then five hours at a time to a thousand, whose last rung is the badge **Wizard of a Thousand Hours** rather
than one more rung (task 12). Generated because a run of near-identical rows is exactly
where a gap or a repeated level hides, and because the top of a ladder is the server's to decide rather than
the launcher's to assume (`AchievementCatalog.UseMaxLevel` is called with what the server itself says; the
rungs above a wizard's own cap never come up).

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
attributed to a world, because there is no world to learn from an id. What is learned is one document in
that same database, so a world discovered last week is still known after a restart.

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
- a **creature** beaten is counted from the duel itself - a creature's circle a spell of the players' side
  reached, in a duel the players won, one per creature in it (task 11). Which of a duel's creatures was a
  **boss** is the server's own `creature.manifest`'s answer, and that file does not exist, so the boss badges
  are the ones still waiting; a creature nothing reached is not counted at all, and neither is one that never
  planned a turn before it died;
- a wizard's *name* is not in the database as text (Imlight stores the indices it was built from, resolved
  through the game's own name tables), so a name comes from the log when that names the wizard signing in,
  and otherwise from `Root.wad` when a game folder is known. A character neither source can name is shown by
  its id rather than under someone else's. Which of the game's two first-name tables a key is read from is
  the launcher's reading of the client's own gender flag, and a value it does not recognise resolves to no
  name at all rather than to a guess;
- deleting the launcher's own data - `database` under the launcher's root - resets playtime and
  achievements. It touches neither the server nor the game.

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
               release lookup, installer, runner, server process guard, job object, client launcher,
               and the patch server's own release lookup, installer, config and runner
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
documents in the launcher's own RavenDB, under `database\` beside that file, with each profile's picture and
banner as attachments on its own document - so one directory is the whole of the notes, and nothing in them
is a file that can be lost on its own. Downloaded archives and extraction scratch space live alongside both.
In portable mode all of it sits in the launcher's own folder instead - see below.

**The server folder is not one of those places.** Imlight's install folder holds what Imlight put there, and
the only thing the launcher writes into it is `Config\Imlight.ini` - the file Imlight expects an outside tool
to edit - plus the release overlay when it is installing or updating Imlight, which is the launcher doing its
job rather than keeping something of its own. Its settings, profiles, notes database, art cache, downloads
and staging are all under `%LOCALAPPDATA%\ViewLight`, or in the launcher's own folder in portable mode. The
one exception used to be that its own database was *run* out of `<server>\RavenDBServer`, which is what broke
an update; it now has its own pruned copy of that engine, `ravendb\` beside `database\` - see
[task 18](#18-the-launchers-own-data-stops-borrowing-the-servers-files---done).

## Portable mode

The launcher is portable when a `portable.txt` file sits next to its executable; **Settings → Portable
mode** is a toggle that creates or deletes it, and an empty file does just as well - which is what makes
the folder portable on arrival on a machine that has never run it. `PortableMode.Apply` is the first thing
`Program.Main` does, before Avalonia or any view model exists, because every path the launcher derives has
to be settled before anything asks for one.

| | Normal | Portable |
| --- | --- | --- |
| Launcher data (settings, cached art, and the database the profiles live in) | `%LOCALAPPDATA%\ViewLight` | `<launcher folder>` |
| Default game folder | whatever *Detect* finds | `<launcher folder>\game` |
| Default server folder | `%LOCALAPPDATA%\ViewLight\server` | `<launcher folder>\server` |
| Default patch server folder | `%LOCALAPPDATA%\ViewLight\patchserver` | `<launcher folder>\patchserver` |

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

**A finished copy is read where it landed, and is not offered again.** Two things went wrong on the first
real portable setup, and both came from the same place: the launcher only re-reads the folder when something
tells it to. A portable launcher starts out pointed at its own `game` folder - that is the default in
`LauncherSettingsStore.CreateDefault` - so a copy writing into that folder is **no change of path at all**.
`ClientPath = result.Destination` was therefore a no-op, nothing re-checked anything, and Settings went on
reporting *root.wad was not found in the selected game folder* and *bin\WizardGraphicalClient.exe was not
found here* about a folder that had just been filled with both, until the folder was set by hand.
`CopyGameIntoPortableAsync` now calls `RefreshClientState()` on the way out for exactly that reason - the
update path in `MainWindowViewModel.ClientUpdate` already did, which is what made the gap visible by contrast
- so the copy is read where it landed and the status lines are about the folder as it now is.

**And the card could not stop asking, because it was gated on saying something as well as doing something.**
`ShowPortableClientCard` was `PortableNeedsClient || IsCopyingGame || HasCopyGameMessage`, and the message is
what is left after a copy finishes: a card headed *copy your game into this folder*, offering to make a copy
that is already there. The ask is now its own property (`ShowPortableClientPrompt`) with its own heading
(*your game is already in this folder* once the copy is in), so what remains when there is a game in the
folder is the line reporting how the copy went. Nothing is hidden that should be seen: a copy that has fallen
behind the machine's install is a different card entirely - that is a game which *is* here and wants
replacing - and the two cannot be mistaken for each other.

### A Steam copy may need Steam, and that is KingsIsle's call

The Steam build is worth copying rather than a thing to avoid, and the first version of this warning had
that backwards. It starts with no Steam client running, and it arrives with the game's own cached patch
files - a head start for a patch server on the same machine that nothing else offers. What makes it worth a
warning is the other half of the same file:

> This is a Steam copy of Wizard101. It plays without the Steam client at the moment and it comes with the
> game's own cached patch files - but it carries `bin\steam_api.dll`, and KingsIsle decides what that library
> does: a later build may require Steam to be installed and signed in, and this copy would stop working the
> day it does. A standalone install from KingsIsle's website ships neither that library nor
> `bin\steam_appid.txt`, so it is the copy that stands on its own for certain.

So it is a *possible* dependency rather than a present one, and the wording says which. The earlier version
said the game would not start without Steam, which is wrong about the build in front of the user and reads as
a launcher reporting a game somebody is already playing as broken - and the file that could change it is
named, so the warning can be checked against the folder it is about.

Launch arguments do not help here, either way. The client's `-ST` is Imlight's way of being *told* that an
account is a Steam one - it is what a Steam-linked account needs to sign in to Wizard101's own server, and
what a standalone account must not be sent - so it is about which server will accept the account, not about
whether Steam has to be present. `GameFolderLocator.IsSteamInstall` recognises such an install by
`bin\steam_api.dll` or `bin\steam_appid.txt`, and `GameClientCopyService.SteamCaveat` is reported wherever a
copy or an update is offered from one, so nobody has to find this out from a game that stopped starting.

## Tests

```bash
dotnet test ViewLight.slnx
```

849 tests covering the parts most likely to break quietly: INI round-tripping and merging (including
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
whether the user is told to update the game or the server, and a full install-then-update cycle that
asserts user settings, runtime data, a deliberately chosen database port and a chosen patch server all
survive. The Log is covered both as the document the view draws and as the page that draws it: what folds
and what does not, a folded run opening and folding back with every line's own time, a fold staying open
across a filter change, the three sources' boxes and what they leave held back, the 200-line block and the
5,000-line Log, and the columns every kind of line is written in - with the layout itself checked against a
real window outside the suite, there being no view test in it to do that.

The patch server is covered the same way and for the same reason: the port it is written for being the
one Imlight already points at, the config being written when it is missing or names another port and
**left alone when it already agrees** (asserted by appending a hand-written line and checking it is still
there), the endpoint read back out of a config that was hand-edited (unquoted values, comments, other
sections, nonsense ports), the release lookup preferring the newest build that actually ships
`aurorium.exe`, the installer writing the executable and its config and **not** re-downloading a cached
one, the single request the launcher makes to a patch server driven through a fake one (the names come back
in the order the server listed them; a trailing slash on the address is not a second request; an answer
that is not a JSON list of names - an object, a list of numbers, an HTML error page - is no revision at
all; a name that is empty or null is dropped rather than passed on; a refused, unreachable or mistyped
address answers with nothing rather than an error, and is not asked at all when it is not a URL), the
runner driven against real child processes and real sockets - waiting for a real port to open, refusing to
call itself ready while nothing is listening, reporting an exit as a fault, being stopped by the launcher's
job, stopping a leftover patch server from this install before starting a new one - and the rule that
decides which patch server a session gets: this machine's address and port while the launcher is running
one and it is up, and no change at all otherwise, so nothing is written back to the public patch server and
nothing a user chose is ever rewritten. The revision sync is covered
the same way and from both ends: what a patch server's revision name is worth as a `GameRevision` (and
that a name which is not one is worth `null`), which of several revisions is newest when the list is not
in order, that nothing is written when the config already asks for it, that turning the sync off puts
back only a value the launcher itself wrote, and - through the launcher's own settings and config files -
that the setting is remembered between runs and defaults to synced.

What entry 24 added is covered the same way, and mostly as rules rather than as processes, because that is
where the decisions are: that a start waits only for a patch server this session will actually ask and only
when it cannot answer yet (all four combinations, including the two where it must not wait), that the wait's
way out is offered only while it is waiting, that a file being downloaded is named with the size the
revision's own list gives it rather than the size of what has arrived, that the newest of several downloads
is the one named, and that a fetch of a revision other than the server's says which two disagree - said
before the progress, because it is not progress. The server's side of that is a parser test: the real
`Error while downloading file …/V_r803238.Wizard_1_610/Data/GameData/Root.wad … 404 (Not Found)` line is
read into its revision and its file, an address with no `V_` segment is not a fact at all, and the notice
it produces says both revisions and names the way out - once per address, because the server retries it.
The cleanup rules are pinned down from the other end: an older revision the server asks for is kept and an
older one the client happens to be is not, which is the change entry 24 made.

The profile system is covered the same way: the log parser driven by the exact lines Imlight writes,
playtime and session counting across polls (including a gap between two of them not being credited), duel
attribution refusing to hand a win to nobody in particular, badges never granted twice and never taken back
by a later read that has not caught up, wizards, quests and the command log read from the database, which
remembered accounts may be shown and which may not, and the words the screen uses when there is no profile
to show. Also, and each of these is a fault found by using the launcher rather than by reading it: a school
read in either spelling the server writes it in (its template id, and the enum's name, which is what a real
database holds), a duel's creatures read from the slots they planned turns on and its spells from the slots
they reached, the same sigil fought twice writing the same duel id and starting from nothing the second time,
a creature's own spell crediting nothing, a duel won with no creature line in it crediting nothing, and kills
surviving a restart - a group heading announcing
its own numbers when they move and staying silent when they do not, the rows the profile screen draws a
wizard from being values that move when the wizard does, and the time played ladder's rungs, its named top
rung, and that the heap at the top is the one badge in the catalog not priced by its difficulty.

**Where those notes are kept is covered as a pair rather than as a database.** `IProfilePersistence` has two
implementations - the database the launcher uses, and the file the suite builds stores over and an older
launcher's notes are read through - and both are driven by one contract, so attaching, refusing to save
while detached, the pictures as attachments, and the promise that a store with nowhere to write says so
instead of quietly writing a file are asserted twice from one set of expectations. The client is covered on
its own: the URLs it builds, the statuses it accepts, a paged read, and the answers it refuses - a document
with no id, a listing that is not the wrapped shape, a page claiming more than it sent. The import has its
own five rules - a file with profiles in it, a file with nothing in it, a file that will not parse, a run
that has already happened, and a file that turns up after one has - and the last two are checked against a
real database rather than only against a fake one.

The Imlight requirement - the launcher's first screen, and the rule behind it - is covered by twenty-six
tests of its own: which piece each gap reports, that a database carrying its own runtime asks nothing of
anything, that a framework-dependent one asks for exactly what its own file names, the roll-forward rule
that decides whether a runtime can run it, the shape of a `--list-runtimes` listing, which of the two
runtimes is chosen when both are there and when only one of them fits (`BundleWith` decides what each one
offers, and a build that ships a runtime is not allowed to turn on whose machine this is), what a short
runtime is told - that it is the shipped one and that a machine-wide install is the fix - that the .NET
gaps carry the steps to install it while the gaps this screen fixes itself carry none, and the launcher
standing down the moment an install appears - no restart, no stale answer. The screen itself was checked in
a real window, there being no view test in the suite to do it: it covers the whole window, no navigation
behind it is reachable, the two buttons are the commands they should be - Check again, and an install
rather than Play - a missing runtime turns it back on with the link the database's own file asked for, the
steps are on screen inside the window rather than scrolled out of it, and pressing the button with the
runtime now present stands the launcher up and opens the database behind it.

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

**The patch server was run for real, from the release the launcher downloads.** Aurorium `v4.0.2`
(8.3 MB, `aurorium.exe`) was installed through `PatchServerInstallService`, `PatchServerConfig` wrote a
config for a free port, and `PatchServerRunner` started it. It accepted the file - which is the part
tests cannot prove, since the launcher is writing another project's config format - and lined up exactly
with what the launcher expects:

```
[Server] INFO aurorium::db: Database aurorium.db initialized!
[Server] INFO aurorium: File server listening on 127.0.0.1:24575
[Server] INFO aurorium: Checking for a new revision @ patch.us.wizard101.com:12500
[Info]   Port 24575 on 127.0.0.1 is open. Waiting 2 seconds for the patch server to be able to answer…
[Success] Patch server is ready — its endpoint on 127.0.0.1:24575 is answering.
```

Two things that run is the evidence for: the config the launcher writes is accepted verbatim (the endpoint
in the log is the one written, not Aurorium's default), and the `tracing` output really does not carry a
line worth keying readiness on - hence the endpoint probe, which is as far as the launcher's judgement of
a patch server goes.

**What a patch server does before it can serve was measured on that same machine, and it is why nothing
asks.** A fresh Aurorium was watched from the moment it answered: the endpoint was open and accepting
connections while the process spent a little over five minutes fetching `V_r806919.Wizard_1_610` - the
revision `patch.us.wizard101.com` currently offers - into its own folder. Two things follow from that. A
session pointed at it inside that window gets `404` for `Data/GameData/Root.wad` and stops - which is the
failure the launcher now waits out, with a way past it, rather than working around (entry 24). And the
revision it fetches is upstream's rather than the server build's, so a server pinned by `GameRevision` to an
older build is the case the hand-off or the sync below exists for.

**The one request the launcher makes was then re-checked against the real thing.** This machine's own
install - Aurorium `v4.0.2`, started from its folder with the config `PatchServerConfig` wrote, on
`127.0.0.1:12369` - answered:

```
GET /revisions -> 200 OK
content-type: application/json; charset=utf-8
content-length: 26

["V_r806919.Wizard_1_610"]
```

`PatchServerRevisions` reads that as one revision, `PatchServerConfig.NewestGameRevision` turns it into
`r806919.Wizard_1_610`, and against this machine's `Imlight.ini` - pinned to `r803238.Wizard_1_610` - that
is exactly what the sync writes, with nothing written when the config already asks for it. Which is what
this machine's `Imlight.ini` now holds: `GameRevision = r806919.Wizard_1_610` and
`PatchServerInternalUrl = http://127.0.0.1:12369`, the launcher's own address, so the rule above has
nothing to change. `EnforceRevision` is the switch a refused login turns on, and it is off on this install.

**Filling a patch server in from a copy of the game was then run against this machine's real folders.**
The plan was built from the live `V_r806919.Wizard_1_610` file list and the portable launcher's own game
folder (a copy of a Steam install): 3825 files, 2703 held, 1025 to copy (13.76 GB), 97 in neither - the same
picture the file-by-file comparison above draws, arrived at from the other end. One of those files was then
copied for real - `Data/GameData/GUI-WorldData.wad`, 181 MB, one of the files Aurorium's two-minute limit
means it can never fetch - into a throwaway revision folder built from the real file list. SHA-256 identical
to the original, under its own name, no `.preloadpart` left behind, and a re-plan afterwards counting it as
held.

The full workflow has been played through by hand on this machine with this launcher: server install,
**Play**, the client logging in against the warmed-up server, and character creation. The client has
therefore been launched against a live server end to end. What has *not* been done by hand since is a full
session served from the local patch server - the patch server was proven ready to serve (above), but the
client has not been played through against it.

What that still does **not** cover: the crash-reporting path was not forced on a real failure, because
forcing one means deleting the cached `Root.wad`; the close prompt was not clicked through in the GUI; and
neither copy entry point has been clicked through - Play's offer and the button in Settings share one plan
and one method, and both are covered by tests, but no one has driven the dialog or watched a real 13.76 GB
copy from the card. The close prompt's dialog was verified to parse and build under the real Avalonia
platform; the offer dialog is written the same way and its XAML compiles with every binding checked, but
nobody has seen it render. Both prompts' rules and stored preferences are covered by tests, and neither has
been shown by a real action.

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

**The next round of work is [To be added](#to-be-added), at the end of this file** - the changes that came
out of using the launcher, each with the reason it exists, in the order they will be done. Anything picked
up later starts there.

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
- **A local patch server still fetches its first revision from upstream.** It has to - that is where the
  game's files live. The launcher watches that fetch and says how far along it is and roughly how long the
  rest will take, from the revision's own file list counted against the disk (see
  [watching it fetch](#watching-it-fetch)), it waits for the one file the server's boot asks for before
  starting the server (entry 24), and it keeps the server's accepted revision in step with what the patch
  server holds. What it can also do is hand it the files a copy of the game already has, when this machine's
  client is exactly the revision the server asks for and not one file of it is at another length - most of
  the revision, and all of the files too large for the patch server's own two-minute download limit (see
  [filling it in from a copy of the game](#filling-it-in-from-a-copy-of-the-game)) - and what is left after
  that is fetched as before.
- **Switching a session over mid-flight.** Once the patch server can serve, the *next* Play uses it; the
  running Imlight read its config at boot and is not repointed. Doing so would mean restarting the server
  under the player.
- **Updating Aurorium while it runs.** The install stops it first, because Windows will not replace a
  running executable.
- **Switching to portable mode does not move what is already there.** Turning it on points the launcher at
  folders inside its own folder and downloads a server and a patch server into them; the ones that were
  already installed elsewhere are left where they are, and turning it off again leaves the portable ones
  behind in the same way. On the machine this was first reported from that meant two complete server installs
  (the one in `%LOCALAPPDATA%\ViewLight` and the portable one), each with its own `Imlight.ini` and its own
  database, which is several hundred megabytes and two things that can drift apart. Moving rather than
  copying is not obviously right either - it is the user's install, and there are two answers (move it, or
  keep both and say so) - so the work is: offer the choice when the mode changes, do the move when it is
  wanted, and end any process left running out of the folder being left behind either way. See
  [task 17](#17-an-update-replaces-the-servers-files-which-means-letting-go-of-them-first---done) for why it
  is not the same bug as a file that is in use.

Three entries that used to sit here have been done and are recorded above rather than deleted: the
**game-first Home screen** (banner, hero Play button, status demoted to one strip), a **wizard's name and
school** (the log first, then the game's own name tables out of `Root.wad` via `WizardNameTable`, and the id
only when neither source can name the character), and **creating an account from the launcher**
(`PlayerDatabaseProfiles` writes the same document Imlight does) - which was then deliberately undone. The
card is gone and the launcher does not write that database any more, for the reason recorded in
[To be added](#to-be-added).

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
| A server that has never booted has no accounts at all | There is no player database yet, so there is nothing to list, nothing to sign in as, and nothing to type `.account create` as. The admin account in `Imlight.ini` is what creates that database on the first boot, so a server has to be started once before any account exists |
| The administrator password in `Imlight.ini` is not the one the account has | Imlight creates that account once and never applies a later password change to it, so the account keeps the password it was made with. The launcher puts the configured one on it whenever it reads the database, and says so under **Settings → Administrator account** - without that, the password on the screen was not one anything would accept. That write is the one instance left of the overstep the account card was removed for: it is waiting on a decision rather than on code, and is recorded in [To be added](#to-be-added) |
| The first boot downloads its assets and can then stop by itself | Nothing in the launcher's own startup path: the Director fetches `Root.wad` (about 281 MB) and dies after the fetch, unreproduced. Pressing **Play** again boots it normally, and it has not been seen to recur after a successful first boot |
| No boss badge moves, however many duels are won | **`manifests/creature.manifest` never gets written.** The launcher reads it, and would rather read it than guess - it is what says which creatures are bosses - but the file is written by `Imlight.Director`'s `ManifestGenerator`, and **nothing in Imlight ever constructs it**: the class is `internal`, its constructor is `internal`, and there is no `new ManifestGenerator()` anywhere in the source. It is a generator that has to be wired to a call before the file it writes can exist, and until then the launcher's own screen says the list is missing rather than counting nothing quietly (`ProfileBossesText`). Creature kills themselves no longer wait on it: they are counted from the duel's own lines (task 11) |
| A new zone is slow to load | The zone's assets come from the patch server, and the public one is a community machine rather than KingsIsle's infrastructure. The server is local; the assets are not - running the launcher's own patch server is what fixes this, once it has finished its first fetch |
| `404 (Not Found)` downloading `Root.wad` from a `127.0.0.1` patch server | One of two things, and the launcher now says which. Either the local patch server has not finished fetching that revision yet - its first minutes, and the honest answer is to leave it running, which is what Play waits for before it starts the server at all - or the server is asking for a revision the patch server does not have, which the launcher reads out of the server's own log and says on Home with both revisions named. That second one is what the sync exists for and it is applied at every start, so the fix is to stop the server and press Play again; if the sync is off it is the reason. See [running one instead of visiting one](#running-one-instead-of-visiting-one). The launcher does not redirect the session for either of them: the address it was given is the address Imlight keeps |
| `TcpListener for Imlight.Patch on port 12500 failed` | Imlight's **own** patch listener (`PatchServerPort`, the one the *client* talks to) could not bind, which means something else already holds that port: a previous Imlight still running, or the launcher's patch server configured to the same port. The launcher's card warns about the second; the first is a leftover the leftover-server guard normally clears |

A launcher-side difference worth keeping in view: the third one is *reported* rather than detected - the
launcher cannot tell a congested patch server from a slow disk, so it says nothing about the cause and the
readme explains it instead.

## Packaging and releases

Source and builds both live at <https://github.com/Smealm/ViewLight>. Every build is published as a
GitHub **prerelease**, which matches what Imlight itself does and, more to the point, matches what this
launcher is: a proof of concept that is played with rather than depended on.

**The version is written down once, in the project file.** `<Version>` in `src/ViewLight/ViewLight.csproj`
is the only copy of it: About reads the number off the assembly, and a build with no `Version` property
reports `1.0.0` by the SDK's own default - which is what the first build did. It is bumped by hand when a
build is published, and the tag and the release are named after it, so the number in the window, the number
on the release and the commit that produced both agree. `v1.0.0` was the first build; `v1.1.0` adds the
patch server, the launcher's own database, and the screen that stands down when Imlight or the runtime it
needs is missing; `v1.2.0` adds the cloud backup, the live profile and achievement updates, and the runtime
the launcher carries rather than asks for; `v1.3.0` makes the patch server something the launcher manages
rather than merely runs - the wait for its files before the server starts, the fetch it reports while it
works, the revision the two are kept on together, and the cleanup of the revisions it has finished with.

**Three accounts of every build, for three different readers.** `CHANGELOG.md` is the plain one - per
release, what was added, changed, fixed and removed, in the language of somebody who uses the launcher
rather than somebody who changes it. The release notes on GitHub are the sales pitch, screenshots and all.
This file is the third, and the only one that says *why*. The readme is the manual, and it was rewritten for
this build to be one: shorter, plainer, and stripped of the reasoning that belongs here instead. The four
are meant to agree, which is something to check when a build is published rather than something that keeps
itself true.

The build shipped in a release is produced by exactly these commands:

```bash
dotnet publish src/ViewLight/ViewLight.csproj -c Release -r win-x64 --self-contained true -o publish
curl -fsSL https://dot.net/v1/dotnet-install.ps1 -o .tmp/dotnet-install.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .tmp/dotnet-install.ps1 -Runtime aspnetcore -Channel 10.0 -InstallDir publish/runtime
7z a ViewLight-win-x64.zip ./publish/*
```

The two in the middle are the runtime the database in the launcher's own folder runs in:
[dotnet-install](https://dot.net/v1/dotnet-install.ps1) writes a real .NET installation - the host, and
`shared/Microsoft.NETCore.App` and `shared/Microsoft.AspNetCore.App` under it - into `publish/runtime`
without touching the machine it runs on, which is exactly the layout `DotnetHost` looks for. Three choices
in there are deliberate: `powershell` rather than `pwsh`, because every Windows has the first and the
script needs nothing newer; `aspnetcore` rather than `dotnet`, because ASP.NET Core is what carries both
frameworks the database names; and a channel rather than a pinned version, because what the database is
matched by is the runtime's own roll-forward, so a newer patch of the same major is a match.

**Self-contained on purpose, and now truly so.** The launcher's audience is people who want to play a
private Wizard101 server without first installing a development toolchain, so the zip carries its own
runtime and needs neither .NET nor administrator rights. Bundling a *second* runtime looks like paying for
that twice, and it is the only part of this that was worth arguing about: the runtime the launcher is
published into cannot be borrowed, because the database is a framework-dependent application and what it
resolves frameworks out of is an installation layout - a host and `shared/` - rather than a folder of
DLLs. So the honest choices were "ask the user for a runtime" (which is what this used to do, and what the
first screen was for) or "ship a second copy of one". The second copy is 107 MB on disk against a launcher
that publishes at 104 MB - a zip of 43.0 MB becoming 87.6 MB, both measured - and it buys the thing the
whole project is for: unpack the zip, run the exe, and nothing has to be installed first. The cost is
size, and it is worth paying here: every alternative ends in "install the .NET 10 desktop runtime"
instructions on a page that wanted one button. `v1.2.0` is the first build that carries one, and the zips before
it - `v1.1.0` and earlier - carry no `runtime` folder and still ask the machine for one, which is what
the first screen is for.

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

## To be added

Twenty-four changes that came out of *using* the launcher rather than out of reading its code, written down
here in the order they will be done. Each one records the reason as much as the job - they came out of a
conversation, and a later reader should be able to finish any of them without having it again. The finished
ones stay in the list and point at the section above that now describes the rule properly, which is the same
habit [Not done yet](#not-done-yet) keeps.

### 1. The patch server is part of the stack, and Settings asks one question about it - *done*

**What was asked for.** The patch server was presented as an optional extra with a checkbox to turn it off.
It is not optional - Imlight has one or it does not boot - so what is actually being chosen is *whose*: this
machine's, or somebody else's. Settings asks that one question, and everything that follows from the answer
sits underneath it: the remote address (the public instance, or an address of your own) in one branch, and
the folder, the port, *start it with the server, stop it with the server*, *fill it in from the game folder
if it has nothing yet*, and the revision sync in the other. The patch server address moved out of the
**Network** card, because it is a patch server setting.

**What came out of it.** Two new launcher settings and one rule change. `UseLocalPatchServer` (on by
default) says which branch is in play, and `PatchServerPreloadAutomatically` (off by default, and since removed - see
[22](#22-the-launcher-watches-the-patch-server-fetch-and-stops-copying-files-into-it---done)) turned Play's
offer into something that just happens. The rule for pointing Imlight at this machine's patch server used to
be *the launcher is running it* - `PatchServerManaged`, which is really "start it with the server" - and is
now *this machine's patch server is the one that was chosen*. A patch server started by hand from Settings
is therefore used by the next Play instead of the address being left as it was, and Home's row, the fetch
report, the copy offer and the sync all hang off the one answer rather than off three separate switches.
Written up under [the patch server](#the-patch-server) and
[filling it in from a copy of the game](#filling-it-in-from-a-copy-of-the-game).

**One limit carried forward from this.** The revision sync asks *this machine's* patch server and only
that one (`GET /revisions` goes to `LocalPatchServerUrl`), so with a remote patch server in use the switch
has nothing to read and does nothing - the case where the game has been patched past the server build being
much more likely against the public instance than against a local one. It should either be hidden in the
remote branch or be taught to ask the configured address; it is written here rather than left in the code
because neither is obviously right.

### 2. The launcher does not write the server's database - *done*

**What was asked for.** Making an account from Settings means the launcher writing documents into Imlight's
own player database. That is an overstep: the launcher's job is to install, configure and run the server,
not to be a second author of its storage. The server already has a way to make accounts - `.account create
$username $password`, typed by the admin account inside the graphical client - and that should be the only
way.

**What came out of it.** The **Create an account** card is gone from Settings, replaced by the two steps
that do work: sign in as the admin account in the game, run the command, close the client, then pick the
account the server made here. Nothing else about accounts changed - the account list, the picker and the
profile are reads. The code that wrote the document is left where it is and reachable from no view, so the
decision can be reversed in one edit if the server ever grows a command channel a launcher may use; what
must not come back quietly is a button. One more write the launcher still makes is the same overstep - the
admin password it puts onto the account so the one in `Imlight.ini` is the one that works
(`PlayerDatabase.AlignAdminPasswordAsync`) - and that one is waiting on a decision rather than on code.
Written up under [signing in without the login screen](#signing-in-without-the-login-screen).

### 3. Skipping the tutorial has consequences, and the setting says which - *done*

**What was asked for.** Skipping the tutorial is one checkbox and three separate consequences that the game
never mentions: the school-specific spells the tutorial hands out are never granted, the starter items are
not equipped on the new character, and the tutorial's own items can arrive twice. All three are fixable by
hand - walk to Ravenwood, learn your school's spell, then equip the starting gear out of the inventory - and
the setting should say so *before* it is turned on rather than leaving it to be discovered.

**What came out of it.** A description under the checkbox, and a warning that appears while the tutorial is
disabled. Written up under [starter settings](#starter-settings).

### 4. The launcher's own data moves into a launcher database - *done*

**What was asked for.** The launcher keeps its own state in two JSON files in its data root: `launcher.json`
(settings, folders, and the last known account list) and `profiles.json` (profiles, playtime, sessions,
badges, and which world each zone belongs to). Both are read whole, changed in memory and written whole, and
both only ever grow. After years of use - every badge, every session, every zone ever entered - `profiles.json`
is a file the launcher rewrites on any change, sifts through on every read, and cannot answer a question
about without holding all of it. A database answers the same questions live and reads only what is wanted.

**The decision.** RavenDB, and specifically *the RavenDB the server already ships*. `EmbeddedRaven` already
starts `Raven.Server.dll` out of the Imlight install against the server's data directory so the launcher can
read the player database while Imlight is not running; giving the launcher a database of its own is that same
code path with a different data directory, port and settings file - and `EmbeddedRavenSetup.SettingsPathFor`
already puts that settings file in the launcher's own folder. Same binaries, same storage format, same
codebase as Imlight. One process at a time is already the rule for the
server's database (RavenDB locks the data directory) and applies here too, with the difference that nothing
else ever opens this one - so the launcher can hold it open for as long as it runs.

**The one premise of that decision that did not hold, and what came of it.** "No new dependency to ship"
is true of *binaries* and false of *runtime*: the database is a framework-dependent application, and the
install it comes out of is self-contained, so what the launcher depends on is a .NET runtime with the
frameworks the database names. That was measured against a real install and written up under
[Imlight is a requirement](#imlight-is-a-requirement-and-the-launcher-stops-and-says-so). The first answer
was to make the runtime part of the requirement and have the first screen ask for it. That answer did not
last: asking a user to install a runtime so that a launcher they unpacked can keep notes about a game is a
step that should not be there at all, and it is the one step a bundling step in the release can delete -
which is what [task 16](#16-the-runtime-ships-with-the-launcher-so-nothing-has-to-be-installed---done) did.
The premise now holds: the launcher's own data has no dependency that is not in its own zip.

**Decided while starting it.** The profile images - `profiles/<account id>/avatar.png` and its banner -
become **attachments on the profile document** rather than files beside the database. A file is what a
`Bitmap` wants and an attachment is what a backup wants, and the backup is the case that cannot be
improvised later: task 5 is specified as "a set of database directories plus a manifest, and nothing else",
which is only true if the pictures are in the database. The UI reads them back as a stream.

**What was built, in the order it had to be.** First the requirement and its screen: `ImlightRequirements`,
`DotnetHost`, the fourth layer in `MainWindow`, and 14 tests. Then the launcher's own instance
(`LauncherDatabase`, held open for the run by `MainWindowViewModel.LauncherDatabase`) and the document shapes
(`LauncherDocuments`). Then the move itself: the client (`LauncherDatabaseClient`), the persistence contract
and its two implementations (`ProfilePersistence`), `ProfileStore` on top of them, the pictures as
attachments, and the one-time import (`ProfilesImport`) - 50 tests between them, and 58 checks against a
real RavenDB across three throwaway harnesses: a database started, written to and restarted; the notes used
through the store across that restart; and an old launcher's file migrated, with a restored copy of it
refused. The last of those was then run through the launcher's own wiring rather than through the store
alone: a view model over the real install, with every launcher file redirected into a scratch folder so the
real notes were never touched. It starts the launcher's instance under the launcher's own root, makes its
database, and shows the migration running - `profiles.json` renamed to `profiles.json.imported`, with its
profiles and picture in the database - and nothing writes a `profiles.json` at any point.

**Where that database lives, in both modes.** This was the point of doing the instance first: it is the
launcher's own folder, not the server's data directory and not a second copy of anything.

| | Standard | Portable |
| --- | --- | --- |
| Documents | `%LOCALAPPDATA%\ViewLight\database` | `<launcher folder>\database` |
| The instance's settings | `...\ViewLight\raven\launcher-settings.json` | `<launcher folder>\raven\launcher-settings.json` |
| The instance's log | `...\ViewLight\raven\launcher-logs` | `<launcher folder>\raven\launcher-logs` |

`database\` is one directory holding the whole of the launcher's own data, which is what task 5's backup
needs it to be. The server's data directory is where Imlight keeps *its* documents, and the two are never the
same folder - a database pointed at the other's directory is one of them holding the lock the other needs.

**Two decisions made while building it.** The port is a free port picked per run and written down nowhere:
the server's database uses the port Imlight's config names because the server has to reach it on that
address, while nothing outside the launcher ever reaches this one - and a port left occupied by something
else cannot then leave the launcher with a database it cannot start. And the instance is started *when the
requirement screen is satisfied* rather than on first use, so a profile is never read from a database that has
to be started first - which is also why it is held for the whole run rather than handed over like the
server's: nothing else opens it, including Imlight.

The cost of that is honest and visible: while Imlight is idle the launcher now runs **two** Raven instances,
the server's (borrowed, so accounts can be read) and its own. Both are wanted once the next step lands, and
the second one is empty until then.

**What the engine actually does, checked against the real one rather than assumed.** The instance was started
from this machine's real install against a scratch root: it comes up on its own port, creates `System`
first and `Databases` when the launcher's database is made, and reports its own data directory in its own
settings file. Documents are written with `PUT /databases/LauncherData/docs?id=...` (201) and read back
through `/docs?start=0&pageSize=512`, which answers `{"Results":[...]}` - the same wrapped listing Imlight's
own player-database read already unwraps - with `@metadata.@collection` telling a profile from the map. A
picture is an attachment: `PUT`/`GET`/`DELETE /databases/LauncherData/attachments?id=...&name=avatar.png`
answered 201, the same bytes back, 204, and 404 after. Those are the shapes the store will be built on, so
they were worth knowing before it is.

**And then the notes themselves.** `ProfileStore` keeps the same surface it always had - the profiles, their
zone map, `GetOrCreate`, `Save`, the pictures - and underneath it is `IProfilePersistence`: the database for
the launcher (`DatabaseProfilePersistence`), and the file for the suite and for reading an old launcher's
notes (`JsonProfilePersistence`). The store is built before the database is up, so it starts with nowhere to
keep anything and says so rather than quietly writing a file: a launcher that wrote its notes to a file *and*
to a database would have two answers to every question and no way to know which was newer. It is attached
when the database answers, which is also when the ledger re-reads the learned worlds it took a copy of while
the store was still empty.

**The pictures are attachments on the profile document.** `ProfileStore.ReadImageAsync` hands back the bytes
for the screen to decode and why there are none when that is the answer (`ProfilePicture`), replacing the path
it used to resolve and the profile screen used to read off disk; a picture now exists for exactly one profile,
travels with it, and is part of a backup of the directory. What the screen does with the two answers is task 7
below. The
file implementation keeps its pictures as files and leaves a forgotten one where it is, which is the one
place the two differ and is written down in `DeleteImageAsync`.

**Reads and writes are synchronous and pictures are not**, which is deliberate: a save happens inside the
pass that read the server's log, and making all of it asynchronous would ripple through the profile system to
buy nothing against a local database - while a picture is picked by somebody pressing a button, which is
asynchronous already. The client never resumes on a captured context, so the synchronous two cannot deadlock
the UI thread.

**The migration, which is 4a.** `ProfilesImport` runs once, when the database is first attached: the profiles,
the zone map and every picture the profiles name become documents and attachments, and the file is renamed to
`profiles.json.imported`. The marker that says it was done is **a document in the database**, not a flag in a
file - the settings file is the one thing a backup does not carry, so a marker there would let a restored
backup pour an old file over newer notes. A file that will not parse changes nothing at all and says so: half
a file acted on is worse than a file left alone.

**What has to move, and what must not.** Profiles, playtime, sessions, badges and zone worlds: all the
launcher's own notes about a server, none of it the server's. **Settings stay a file.** They are read before
anything else can be, and a database whose location comes from the settings cannot be read before the
settings have been read - which is also why `portable.txt` is a file rather than a setting. That is worth
writing down because "move everything into the database" is the obvious next thought and it does not work.

**The last known account list stays a file too.** `launcher.json` keeps the folders and the accounts the
launcher last read, and that list is a *cache* of what the server said rather than a record of the launcher's
own: the profiles are the record, and they are what moves.

**Sizing.** Not a rewrite. `ProfileStore` is the only reader and writer of `profiles.json`, behind
`Profiles`, `GetOrCreate`, `Save` and `AdoptImage`, so the work is one class plus whatever query shapes the
profile and achievements screens turn out to want.

**Sizing, with one caveat.** `ProfileStore` is still the only reader and writer of `profiles.json`, so the
work is one class plus whatever query shapes the profile and achievements screens want. The caveat is the
suite: eight test files build a `ProfileStore` over a temp file, and a real RavenDB per test is not
viable. So the persistence goes behind a narrow interface, with the files-and-memory implementation the
suite runs against and the database implementation for the launcher - and one check of the database path
against a real one.

### 4a. Importing `profiles.json` - *done, with task 4*

**What was asked for.** An existing launcher's data has to arrive in the database without being retyped.

**How it works.** `ProfilesImport.RunAsync` reads the file once, writes a document per profile, the
zone-to-world map, and an attachment for every picture the profiles name, then renames the file to
`profiles.json.imported`. The marker that says it was done is a document in the database rather than a flag
in a file, so restoring a backup cannot re-import old data over newer - and the rename is cleanup that a
downgrade to the previous build can still read. A file that will not parse changes nothing and says so,
which is the same shape every other read of strange data in this project has.

**One rule the tests are built around.** It runs exactly once, including when there was nothing to move: a
file that turns up later, out of an old backup, is not notes this launcher has never seen - it is an older
copy of notes the database already has, and it is left alone. That is checked against a real database, not
only against a fake one.

### 5. A cloud backup of the few databases that matter - *done*

**What was asked for.** A safety net for the data that cannot be re-downloaded. A new machine, a new
launcher install and a fresh server install should end with the accounts, the wizards, the profiles, the
playtime and the badges back - not with a re-run of everything that produced them. Sign in to a cloud
drive from Settings, and everything that matters is restored by itself.

**What is backed up, and what is not.** The launcher's database (task 4) and the server's own RavenDB data
directory, which is where accounts, wizards and everything they own live. Deliberately **not** the game's
files (18.39 GB of patch server data), the client, the server binaries, the cached art and downloads (all
re-downloadable), or `Imlight.ini` (machine-specific paths that would be wrong on the machine doing the
restoring). A backup is a set of database directories plus a manifest, and nothing else.

**The manifest is what makes it automatic.** Every backup carries, per database, a content hash and the
time it was taken. That is enough to answer the ordinary question without asking anybody: remote newer than
the last thing this machine uploaded, and local unchanged, means restore; local newer means upload. Like
Steam Cloud, the ordinary case is guessed right and says nothing, and only a real conflict - both sides
changed since the common ancestor they were last seen equal at - stops and asks. A sync that announced every
upload and every download would be a stream of notifications about the machine doing exactly what it was
told to do.

**The first sync of all takes the cloud's copy - which is [task 21](#21-a-first-sync-never-writes-over-the-cloud---done)'s correction.**
With no ancestor there is nothing to weigh the two sides against, and this originally asked: a question whose
"keep this machine's data" button writes a fresh install's empty database over the only backup there is. The
cloud wins instead - restore first, upload afterwards, never the other way round.

**The constraint.** RavenDB holds its data directory under an exclusive lock, so nothing is copied *or*
restored while anything has it open. The sync therefore runs while the launcher is idle and nothing is
running - which is exactly the window `EmbeddedRaven` already manages, so the taking-turns code is there and
this rides on it. A restore is applied before the next server start, never under a running server. Task 6
below is that rule written out properly: the three moments it may run at, and what it does with each database
at each of them.

**The decision: Google Drive, in the app's own hidden folder.** Two access modes were described. The one to
build is the app-sandboxed one: the `drive.appdata` scope gives the launcher a folder of its own inside the
user's Drive that it can create, read, write and delete in, that the user cannot see in the Drive UI, and
that cannot touch a single file outside it. The broader modes are the ones to avoid - `drive.file` needs a
file picker and gives per-file access, and full `drive` is the user's whole Drive.

**One correction to write down before it is built.** "No refresh token needed" is not achievable: an
offline sync has to upload while nobody is watching, and that is what a refresh token is for. What the
`drive.appdata` scope buys is not the absence of a token but its limits - a token that can only ever reach
the app's own folder in the user's Drive. So: browser sign-in (Google's installed-app flow with a loopback
redirect, so the launcher never sees the password), `access_type=offline`, and the refresh token kept in the
launcher's own database rather than in a file.

**Left open.** Where the OAuth client id comes from - a client id shipped for the app, or one the user
registers - and whether the sign-in is per machine or per Drive account. Neither changes the shape above.
And Drive is the choice because it is what the user already has: the manifest, the hashes and the merge rule
are the same against any of the file-sync services, and only the sign-in and the API calls would change.
Look up current install guidance for the chosen provider when this is built rather than working from these
notes alone.

**Provider guidance, looked up rather than assumed.** The Gravity Index - the catalogue of third-party
services this project integrates against - carries no Google Drive entry, so there was nothing to compare it
to: Google Drive stands as the choice above, because it is the account the user already has and because the
whole sync is provider-neutral except for the sign-in and the handful of API calls. The shapes that were
built are Google's own documented installed-app flow: authorization-code with **PKCE**, a loopback redirect,
`access_type=offline` and `prompt=consent` so a refresh token comes back every time, then a refresh-token
exchange for each unattended upload. `drive.appdata` is the only scope asked for.

**What was built, in the order it had to be.** The manifest first, because it is the part that cannot be
improvised later: `BackupManifest` (per database, a content hash and the time, with equality that ignores the
time and the recorded sizes), `DirectoryFingerprint` (a directory as one SHA-256, over paths and bytes in a
fixed order and nothing else), `BackupSync` (the three-way rule, pure), `BackupArchive` (the zip: the
directories and the manifest, written to a temporary name and moved into place so a reader never sees half an
archive, restored into `<name>.restoring` and only then swapped in), and `BackupSet` (which two directories -
resolved the way the launcher resolves them to open them, never a second guess). Then the plumbing:
`ICloudBackupStore` (two names and nothing else, so the app-scoped folder is the only place a bug could
reach), `CloudBackup` (hash, read, decide, move - archive first and manifest second, and a manifest that will
not read is a conflict rather than an empty remote, so an unreadable backup is never overwritten). Then Google:
`GoogleDriveBackupStore` (Drive v3 against `appDataFolder`, find-then-create-or-replace because Drive has no
"replace") and `GoogleOAuth` (the installed-app flow, with a raw `TcpListener` for the redirect rather than
`HttpListener`, which would want a URL reservation the launcher has no business asking for). Then
`CloudBackupState`, then the view model and the Settings card. 60-odd tests, against fakes rather than against
Google or a live RavenDB: the rule in a table, the hash's determinism, an archive round trip, the request
shapes a Drive call has, and the sync end to end over an in-memory store.

**The one correction, and it is about the lock rather than the cloud.** The notes above said the refresh token
is "kept in the launcher's own database rather than in a file". It is - `CloudBackupState.WriteRefreshTokenAsync`
writes a document - but the notes did not see what that costs, because the *other* constraint is that a
RavenDB data directory cannot be copied or replaced while anything has it open, and the launcher holds **two**
of them: its own, for the whole run, and the server's, while Imlight is idle. A sync therefore cannot simply
run "while the launcher is idle"; it has to put both databases down to take the copy and start them again
afterwards - which is what `RunCloudBackupAsync` does, and why a backup stops and restarts the launcher's own
database around itself. The token stays in the database (the sign-in has to outlive a run and it is where the
launcher's long-lived state goes); the *ancestor* does not, and that is deliberate: it is the hash of the
launcher's own directory, so writing it there would change the thing it describes and the two sides would take
turns uploading for ever. It lives in `backup-sync.json` beside the settings - the one thing a restore must
not carry, because a machine that has just restored has nothing to compare against until it has hashed what it
restored.

**A database that cannot be read is not a database that is gone.** The one bug found while building it,
and it was a real one: a directory that exists but cannot be walked - another process holding it, which is
exactly what a RavenDB lock is - hashed to "absent", and absent is a change, so the sync would have planned an
upload saying a database had been emptied. The next restore would then have acted on it. The fix is that the
read failure is carried apart from the manifest (`BackupPlanResult.LocalProblem`), a failed local read is a
conflict before anything is decided, and even a forced **Back up now** refuses a manifest built from it. Two
regression tests hold it: one that a locked directory reports a reason rather than an absence, and one that a
plan made while the data is held asks instead of uploading.

**Content decides, never clocks.** Nothing in the sync reads `TakenAt`. Two machines disagree about the time,
a clock change or a time-zone move would be enough to make one side look newer than it is, and the hashes are
the same answer everywhere - so "which side changed" is answered against the ancestor by content alone. The
time is carried because a person asked when their last backup was, and that is all it is for.

**Both answers to the open question, rather than one.** The notes above left "where the OAuth client id comes
from" open, and it is now a choice on the screen instead of a decision made here. *My own Google app (bring
your own)* is the user's own Desktop-app client, pasted into Settings - what was built first, and what this
build uses. *ViewLight's built-in Google sign-in* is a client id the launcher carries, so signing in needs no
setup beyond the button.

**The built-in side is written and works, and is greyed out until there is a client to name.** Nothing about it
is unfinished code: the same installed-app flow, the same PKCE verifier, the same loopback redirect - a public
client sends **no secret at all**, because Google's own guidance says a secret shipped inside an installed
application is not secret and the verifier is what binds the redirect to this process instead. `GoogleOAuth`
therefore sends `client_secret` only when there is one, and `GoogleOAuth.BuiltInClientId` is where a real id
goes. What is missing is the registration rather than the workflow: a client id belongs to a Google Cloud
project somebody owns, and this build has none - there is no backend behind the application today. So the
option is `IsEnabled`-bound to `HasBuiltInClient`, and the same rule is enforced again in the setter, because
a disabled control is not a check. The moment a client id is registered, putting it in that one constant is the
whole of switching the option on - and the readme says exactly that where a user meets it.

**Left for a later build, and written down rather than hidden.** The client id and secret are the user's, not
shipped: a Desktop-app OAuth client is registered once and pasted into Settings, which is the "one the user
registers" half of the open question left above, and it is the side that ships no credential belonging to
anyone else. The automatic sync runs when the feature is on - at launcher start, before a server start, and
when a server stops (task 6) - and that means stopping and restarting both databases each time to find out that
nothing has changed: correct, and heavier than it should be; the cheap answer, comparing the remote manifest to
the ancestor before touching anything local, only saves work when there is a remote at all, so it is a smaller
win than it looks and is left for when the feature has
been used in anger. And whether a sign-in is per machine or per Drive account is still whatever the person
doing it decides, because the token is this machine's.

### 6. Cloud syncing only databases that are not in use - *done*

**What was asked for.** The rule stated as a rule, and enforced rather than intended: **a sync may only ever
copy a database nothing is using.** The server is running, so its database is not copied. The server is about
to start, so both databases are closed, copied, and only then handed over. The server has stopped, so both are
copied while they are free. The same rule covers a restore, and the launcher may stand down its own database
for the moment it takes, because that database is the launcher's own. Nothing may be edited while it runs, and
the screen should say so rather than looking broken.

**The three moments.** Launcher start, which is the earliest a sign-in can be read from the launcher's own
database. Server stop, once Imlight has let go of the player database and the launcher has taken it back. And
the way *into* a server, which is the moment the rule above is really about. The first two are found by the
launcher itself and asked for in the background through one rule, `CloudSyncMayStart`; the third is asked for
by the start that is taking the databases (`StartServerAsync`) and *waited for*, because that caller is the one
about to need them.

**The way into a server is the awkward one, and the delivery is the point.** Play hands the player database to
Imlight: it releases the database and then spawns the server. A sync that started in that window would take
the database back - and start it again - underneath the boot it is standing in the middle of, which is why the
first build of this simply refused to sync there (`playInFlight`, and the automatic gate). That refusal is still
there for everything automatic, and it is now the only thing it applies to: `_handingOverToServer` keeps the
automatic path out while the handover sync is asked for directly, and stays up until the server has actually
been spawned, because the config work between the two is not instant either.

**What the handover sync does with each database afterwards is the part that is not symmetry.** The player
database is left **closed**: Imlight is about to open it, and starting it here only to stop it again would be a
RavenDB boot paid for on the way into every session. The launcher's own database is started again, in the
background, because no server ever competes with it for that folder and the profiles and their pictures are
read from it while Imlight boots. `handOverToServer` is what says which of the two is happening.

**A start that arrives during a sync waits for it.** `_cloudSyncTask` is the sync that is running, and a
handover joins it rather than starting a second one: the databases are closed underneath the first, and the
server cannot be handed a database that is being copied.

**A conflict is still not decided here, and Play is not held up by one.** The handover sync runs the same
plan as every other: unchanged here and there is nothing, one side newer is that side moved, both sides changed
is a conflict - and a conflict is a question, not an answer. So the session plays with this machine's copy and
the card says what the two ways out are, which is the same thing the automatic path does at a start. Blocking a
Play on a question somebody has already answered by pressing the button would be the launcher deciding for them,
and there is no way to tell whose copy was meant to win.

**An open is not a lock, and that was a real bug.** `ReleaseIdleDatabaseAsync` did nothing when the launcher
was not *holding* the database - and that is exactly what it is while RavenDB is still starting. Press Play in
the first seconds of a launcher start, and the launcher's own RavenDB went on coming up onto the port and the
data directory Imlight was taking; Imlight then died on a lock it cannot see, with an exit code and nothing
else. The open is now abandoned where it stands (`_idleDatabaseAbandoned`, checked at every step), its process
is killed, and the open unwinds before the release returns - so it cannot come up a moment later, onto the very
port the server is taking. `SomethingHoldsThePlayerDatabase` is the rule, pure, so the case that was missed is
written down: an open that is still running holds the database.

**Nothing is edited while it runs.** The window says what is happening in the corner
(`CloudSyncNotice`), and the pages are turned off (`IsEditingAllowed`) for as long as it lasts: the notes live
in one of the databases the sync has stopped, so a setting changed or a picture picked in that window is a
change the launcher could not keep. The nav is left alone - looking at another page is not an edit.

### 7. The profile screen's notes arrive at once - *done*

**What was asked for.** A picture, a banner or the rest of a profile could take seconds to appear when the
launcher started, and a picture that had just been chosen often needed a launcher restart before it showed.
Three ideas came with it: update the screen live, cache lookups with a way to tell a stale one, and improve the
query itself.

**The restart was a real bug, and it was in the key rather than in the database.** A picture is kept under a
name of the launcher's own choosing - `avatar.png`, `banner.png` - and replacing one reuses that name, which is
deliberate: a picture exists for exactly one profile and a replacement should not leave the old one behind as an
attachment nothing shows. But the screen's cache key was *the names*, so a replacement produced an identical
key, the screen concluded that nothing had changed, and the old picture stayed up until a restart. The key is
now the profile, the two names, the revision of the notes (`ProfileStore.Revision`, which moves when they are
read again, as a restore does) and this run's own count of replacements - and replacing one calls
`ForgetPicture`, which drops the decoded copy and moves that count.

**A read that failed was remembered as an answer.** `ReadImageAsync` returned `null` for both "this profile has
no picture", which is the ordinary case, and "the database would not hand it back", which is a moment. The key
was remembered either way, so one miss - while the launcher's database was still warming up, say - left the
initial-letter circle there for the rest of the run. A read now reports both (`ProfilePicture`), and a failed
one is retried after `ProfilePollInterval` instead of being kept. `PicturesNeedReading` is the rule, pure, so
both halves of it are pinned down without a screen or a database.

**Caching, in the process rather than on disk.** Decoded pictures are kept by profile and name
(`_decodedPictures`), so switching profile and switching back is not two more reads; the avatar and the banner
are read at the same time rather than one after the other; and every profile's pictures are read in the
background when the list of profiles changes, the selected one first, so the card in the corner of the window
is drawn from the cache and a switch is already answered.

**The quiet cost was in the writing rather than the reading.** A save wrote *every* profile document plus the
zone map - a document per account, one after the other, over HTTP and synchronously while the launcher was
holding the UI. Playtime ticks every ten seconds while somebody is signed in, so the poll was rewriting
profiles that had not changed, every ten seconds, in the middle of the screen. `DatabaseProfilePersistence` now
remembers the document it wrote for each profile and writes only what is different, and forgets that memory
whenever the notes are read again - because a read is what says what the database holds now, and a restore
replaces documents this launcher never wrote.

**Not done, deliberately: a cache on disk.** The pictures could be shown before the embedded RavenDB has
finished starting if the last known notes were kept in a file as well, and that is the other half of the
"seconds to appear" complaint - but it would be a second copy of the launcher's own notes, and the rule the
launcher has kept everywhere else is one answer per question. The database is the answer; the screen waits for
it, and warm-up is what makes the wait end at the earliest moment it can.

**What is not claimed here.** Nothing was timed. This environment has no Avalonia display and no Imlight
install, so what is written down is the shape of the work - the reads that no longer happen, the writes that no
longer happen, the retry that does - rather than a measured number of milliseconds. The suite covers the rules
(`ProfilePictureCacheTests`, the write-skip and picture-read cases in `ProfilePersistenceTests`), and the
remaining wiring is what would need a real install to watch.

### 8. Everything on the achievements screen updates live - *done*

**What was asked for.** On the achievements screen the badges themselves light up as they are earned and the
score follows immediately, but the *heading* of a category does not: "unlocked 3 of 40 · 90 points", the bar
under it, and the tick beside the name are all as they were when the screen was opened. Reported as "progress
bar, number of completed achievements on collapsable and some other UI elements".

**Why the headings lag when the tiles do not.** They are two different mechanisms. The tiles come out of
`ObservableCollection<ProfileBadge>` and the group is refilled in place (`ProfileBadgeGroup.Replace`), so a
badge that has just been earned redraws by itself. The heading's numbers are *computed* - `Unlocked`, `Total`,
`Points`, `Summary`, `ProgressText`, `Fraction`, `HasUnlocked` - and nothing raises a change for them, so a
binding to `Summary` is read once and then never asked again. Same for the two lines that decide whether the
ladders and the discovered worlds are shown at all (`HasMilestones`, `HasWorlds`).

**The fix.** `Replace` raises the change for the derived half when, and only when, something it was derived
from changed - which is the same rule the collections already follow, and is what keeps a poll ten seconds
apart from redrawing a screen nobody has changed.

### 9. The wizards on the profile update live - *done*

**What was asked for.** The wizards section of the profile screen is only right after a restart: a wizard
made since the launcher started does not appear, and a level, a school, a gold total or a finished quest does
not move while the server is running, even though the launcher reads the player database every ten seconds
and the data behind it is current.

**Why.** Two faults with one cause. The rows are the launcher's own mutable objects (`ProfileWizard`), and
the screen is bound to `SelectedProfile.Wizards` - a `List<ProfileWizard>` whose *instance* never changes.
`RefreshProfileDerived` does raise `ProfileWizards`, but the value handed to the binding is the same list, so
there is nothing for the screen to see; and even a redraw would print the same numbers, because nothing on a
row raises a change when a field on it moves. A wizard that appears is an *addition* to a list nobody is
watching, which is why it needs the restart.

**The fix, following the badges.** A row per wizard as an immutable snapshot (`ProfileWizardRow`), the same
shape as `ProfileBadge` and for the same reason: a row that has changed is a different value, so "has anything
changed" is one comparison and the answer decides whether the collection is touched at all. The screen then
binds a list whose *contents* move rather than one whose objects do, and a new wizard or a level-up is a new
row. The wizard's own numbers are still the thing that is stored and merged - the rows are only what is on
screen.

### 10. A wizard's school is read the way the server writes it - *done*

**What was asked for.** The profile shows "School not read" for every wizard on a real server, which also
stops that school's level ladder from ever progressing.

**The cause, and it was a real bug with real evidence.** Imlight persists a wizard's school as the
`MagicSchool` **enum**, and RavenDB writes Imlight's enums by *name*. Read out of this machine's own player
database (`Playerdata`, via the embedded server's HTTP API), the admin account document says
`"AuthLevel":"Administrator"` - and `AuthLevel` is an enum in Imlight's `Account`. So
`MagicSchoolBehavior.MagicSchool` arrives as `"Storm"`, never as `83375795`, and the launcher's reader
accepted a JSON **number** and nothing else. The suite could not have caught it: the wizard documents the
tests build carry `MagicSchoolBehavior` with a `Level` and no `MagicSchool` at all, so the one field that was
wrong was the one field no test ever wrote.

**The fix.** `WizardSchool.NameOf` accepts a school's name as well as its id - the two spellings are the same
school, and the file-local docs already claimed text was accepted while the implementation only took a
*numeric* string - and the reader takes the field in either shape. An unrecognised value is still no school
rather than a guess, which is what keeps a creature's school (Sun, Moon, Star, Shadow) from being read as a
wizard's.

### 11. Creature kills are counted from the duel the server logs - *done*

**What was asked for.** Nothing the launcher does counts a creature killed, so the creature ladder never
moves and no boss is ever beaten. The suggestion that came with it: the server logs what a spell hits and who
was in the duel, so a spell that hit four creatures in a duel the players won can be read as four creatures
defeated - an assumption, but an assumption about lines the server actually writes.

**The first attempt rested on a line that means something else, and one real duel is what showed it.** It read
`| Slot 4 | Participant Wraith-WCBoss-L05 joined` and matched that name against `creature.manifest`: for a
creature, the thought went, the participant's `m_debugName` *is* the name the manifest carries, because the
generator prefers the same field. Both halves of that are wrong, and Imlight's own source says so:
`ManifestGenerator` builds the object it names from `FinalizeCoreObject(templateId)`, whose `CoreObjectInfo`
is blank - no zone tag - so `m_debugName` is empty on that path and the manifest's name is the template's
`m_objectName`. A live participant, meanwhile, is built from the zone's own spawn info, so its debug name is
the **zone tag** it spawned under. Here is a real duel of two creatures, won, from this machine's own server
log - the account's only wizard, and the second fight at the same sigil:

```
Duel 4854107232535182118 | Created. Grace period over in 3.75
Duel 4854107232535182118 | Slot 1 | Participant 140DD joined
Duel 4854107232535182118 | Slot 0 | Smart: true | Aggressive: true | Selfish: true
Duel 4854107232535182118 | Slot 1 | Smart: true | Aggressive: true | Selfish: false
Duel 4854107232535182118 | Slot 4 | Caster is casting spell 661258515 against target 0
Duel 4854107232535182118 | Slot 4 | Spell 661258515 hits targets [0]
Duel 4854107232535182118 | Slot 4 | Spell 1427280917 hits targets [1]
Duel 4854107232535182118 | Duel ended. Players win.
... the same sigil fought again, 17 seconds later: same id, same slots, this time fled and lost ...
```

**What that log says, fact by fact.** `140DD` is a debug name, not a template id and not a manifest name, so
nothing could ever have been matched to it - the participant line is read as the server's record of who took a
slot and **nothing is counted from it**. The line that does say which slots hold creatures is the attitude
line, which only ever comes from `CombatCreatureAIComponent`: a slot a creature planned a turn on holds a
creature, said by the process that knows. And `CombatGroupReservations` states the rest outright -
*"Circles 0-3 belong to the creatures, 4-7 to the players"* - which its own circle-choosing code walks, so a
player is never put on a creature's circle and a spell cast from 4 or above is the player's side casting. The
third fact is that **a duel id is the sigil's, not the fight's**: the same id comes round again for the next
battle at that sigil, which is why the `Created.` line is read at all - without it the second fight would
inherit the first one's slots.

**The rule.** A duel the players won leaves behind one creature beaten per creature slot a spell of the
players' side reached, once each - the slots a creature was seen planning on, reached by a cast from a
player's circle, in a duel the players won. Every part of it is a line the server writes, and the circle layout is
not believed on its own: a duel in which no creature ever planned a turn proves nothing and credits nothing.
What a badge earned in that duel quotes is the line that ended it rather than the spell that reached the
creature: the pass quotes one line for everything it turns on, and a spell's line is not evidence of a duel
badge - see `Award`.

**It counts kills, and that is a change of meaning worth saying out loud.** A duel never says *which* creature
was on a slot, so this is a count of creatures beaten rather than a list of different ones: beating the same
creature again counts again, and the creature ladder (`Creatures Beaten`, one rung per twenty-five) is built on
that count, with a fixed top rung of 500 instead of one sized by the server's creature list. The creatures that
*are* identified - because the server named them - still go into the list of what was beaten, once each, and
that list is what a boss badge reads; the two leave different things behind on purpose, and the screen's own
words say which is which.

**What it deliberately does not claim.** A creature nothing hit is not counted, and neither is one that joined
a fight and died before it ever planned a turn - the count can only ever be under, which is the same rule the
duels and the bosses already follow. Nothing is quoted onto a badge for a count either: the duel's own pass
quotes the line that ended the duel.

**What the user will still not see, until Imlight is changed.** The bosses. A boss badge needs a creature's
**template id**, and no line in the log carries one - which is what
`| Creature <templateId> joined`, still unwritten, is for, and what `manifests/creature.manifest` is keyed by.
That file is still not written by anything (see
[Rough edges in Imlight itself](#rough-edges-in-imlight-itself)), so `BossesDefeated` stays at zero and the
profile screen says why - while the counts above, which need no file at all, move as duels are won.

**Checked against the real thing, not just the tests.** The 1348 lines of the day's log this machine's server
actually wrote were run through the launcher's own reader (`ServerLogFacts` and `ProfileLedger`, the same two
calls the launcher makes per poll): one duel won with two creatures on it, one duel fled and lost, and the
profile that came out holds `CreaturesKilled = 2`, `DuelsWon = 1`, `DuelsLost = 1`, `CreaturesDefeated` empty
and `BossesDefeated = 0` - exactly what that log says and no more.

### 12. Time played becomes a ladder instead of a handful of badges - *done*

**What was asked for.** Playtime is currently five badges at five fixed thresholds, which says nothing about
the hours between them. Asked for instead: a milestone rung starting at half an hour, then an hour, then five
hours, ten, fifteen, twenty, and five hours at a time from there to a cap of a thousand - with the final rung
worth a heap of points and granting a badge called **Wizard of a Thousand Hours**.

**How it lands.** A fourth generated ladder (`playtime`), a rung per threshold, which is what the milestone
cards and the Settings announcement switch already know how to draw and to stay quiet about. The five fixed
playtime badges move into it - their thresholds are all rungs of the new ladder - and the two that are not
about time (`first-steps` for signing in at all, `a-regular` for twenty-five sessions) stay badges.

**The finale.** A rung of a ladder is named from the ladder (`Time played, 1,000 hours`), so the last one
would not read as the badge that was asked for. `MilestoneTrack` gains one optional word for its top rung, so
the thousandth hour is *itself* the badge named **Wizard of a Thousand Hours** rather than a second badge
arriving beside it - and it is scored by the ladder rather than by its difficulty, because a heap of points
the size of the other legendaries would not be the reward that was asked for.

**What this changes for a profile that already has the old badges.** The old ids (`settling-in`,
`five-hours`, ...) are no longer in the catalog, so they stop being drawn and stop scoring: the catalog is the
whole list, and a profile cannot be scored from a badge nothing defines. The hours themselves are not lost -
they are the playtime the ladder is measured in - so the rungs up to what the account has played are earned on
the next read. Five fixed thresholds cannot sit beside a ladder of the same five thresholds; the hour would
have been scored twice.

### 13. The sign-in itself: kept, explained, and stoppable - *done*

**What was asked for.** Three reports about the same card: the sign-in does not survive a restart, so it has to
be done again every session; Google answers with a 401 unless the client secret is pasted in, which the readme
had said could be left blank; and closing the browser window the sign-in opens leaves the launcher waiting for
a sign-in that never arrives, with no way back to the button that started it.

**The 401 was one trailing space, and it was measured rather than guessed.** The client id in the settings on
this machine ends in a space - a copy out of the Cloud console picks one up and a settings box shows nothing -
and the id goes into a form body, where Google's lookup sees the space as part of the value. Three probes
against the real client, with a deliberately bogus refresh token so that only the client's own validity is
under test: the id **as stored** with the secret answers `401 invalid_client` *"The OAuth client was not
found."*; the id with the space **stripped** answers `400 invalid_grant` *"Bad Request"*, which is the client
and the secret being accepted and the token alone being refused; and the stripped id with **no** secret answers
`400 invalid_request` *"client_secret is missing."* So the space is the whole of the first report and the
missing secret is the whole of the second. `GoogleOAuth.Normalize` now strips whitespace, quotes and the
zero-width characters a web page can smuggle into a copy wherever an id or a secret is used, and the sign-in
writes the cleaned values back into the settings, so what is sent and what is shown cannot drift apart again.

**A Web-application client's secret is not optional, and the readme said it was.** A Desktop-app client is
bound to the redirect by the PKCE verifier and needs no secret - which is what the readme described as if it
were the only client type. A Web-application client has to send its secret on every token call, and the third
probe above is Google's answer when it does not. Both the readme and the Settings card now say which type needs
which, and - because a 401 says nothing a user can act on - `GoogleOAuth.Refused` reads the error code and its
description and names the cause: an unrecognised client, a missing or wrong secret, a redirect address the
client does not allow, a sign-in cancelled at Google, or a refresh token that has expired or been revoked. The
raw line is kept in the sentence rather than dropped, so anything unrecognised still reaches the Log.

**The wait was uncancellable, and the redirect was not the only thing a browser sends.** Two separate ways to
sit on a sign-in that had already failed. The first: the loopback socket took *the first connection* as the one
redirect, and a browser opens connections of its own beside a page - a preconnect, a probe, the request for the
icon - so a request carrying no code ended the wait with no code and no reason, and the real redirect that
followed had nobody listening. The listener now stays open and answers one connection at a time until a request
actually carries a code or an error, and drops a connection that says nothing rather than waiting on it. The
second: closing the browser leaves nothing to receive and nothing that can report it, and the launcher had no
way to be told. The wait is now registered against a cancellation token, the sign-in command holds it, and a
**Cancel sign-in** button appears beside the sign-in button for exactly as long as the wait lasts - with the
status line saying, while it waits, that closing the window leaves ViewLight waiting and where to press.

**Restoring data is not signing out.** The refresh token is a document in the launcher's own database, and a
restore replaces that whole directory with the copy in the cloud - which can be one taken before this machine
ever signed in. The sign-in this run already holds is therefore written back the next time the database comes
up (`CloudBackupState.ReadRefreshTokenOrRestoreAsync`), and only that one: a launcher that starts with no
sign-in anywhere stays signed out, or the sign-out button would not hold. The same review found the write
itself unchecked - a sign-in that could not be stored still said *signed in*, and the next start asked for it
again with nothing to explain why - so the result of that write is now read and reported.

**What holds it.** `GoogleSignInTests` drives the real loopback socket rather than a stub: it cancels a wait and
checks it ends with a sentence about cancelling, lets a wait run out and checks it says nothing was saved, and
opens the browser's own stray request followed by the real redirect to check that the redirect is still the one
that is caught. The normalisation and the five refusals are checked against the messages Google actually sent,
and `CloudBackupStateTests` checks the restore case in both directions - a sign-in this run holds is written
back, and one the database already has is not written over.

### 14. An install that is there is not an install that is gone - *done*

**What was reported.** "Installed server detection seems to be broken: the server is installed, it says it is
not. Reinstalling works, and later it can show up as uninstalled again."

**Three failures wearing one sentence.** `ImlightConfig.TryLoad` answered `null` for *three* things - no file,
a file another process had open, and a file this account could not read - and every caller read `null` as the
first one. So an install that was plainly in the folder was announced as missing: the requirement screen said
`does not hold an Imlight install`, and the Settings row said `No server installed here yet
(Config/Imlight.ini not found)`. That is what a reinstall "fixing" it looks like from the outside: rewriting
the config takes the lock off, the next read works, and the message was wrong the whole time. `ImlightConfig.Read`
now returns the config *and* whether a file was there at all, and the failure is read off the exception -
`FileNotFoundException` is "no file", everything else is "there and not readable" - because `File.Exists`
answers `false` for a file held open with no sharing, which is the very case being told apart. What is there and
busy is now a gap of its own (`ImlightGap.ConfigUnreadable`) with a sentence, no accusation, and the Check
again button that was always on that screen; and the read itself waits up to three quarters of a second and
tries again, because everything that holds a config file - an install unpacking a build over the folder, the
server writing its own settings as it boots, a scanner reading what was just written - lets go in far less than
that.

**And the same mistake one level down.** The .NET probe ran `dotnet --list-runtimes` once, gave it ten seconds,
and read *no answer at all* as *an answer of nothing*: an empty framework list, cached for the rest of the run,
which made a machine that has the runtime look like one that does not - `NoRuntime`, with steps for installing
something it already had. `DotnetHost.Ask` now answers `null` for "would not say" as against an empty list for
"it has nothing", asks twice before giving up, and **nothing about a failure to answer is cached**: a probe that
timed out is a fact about a moment, not about the runtime. Where a host genuinely cannot be asked, the
requirement is treated as met and the database says for itself what is wrong when it is started, with its own
port and exit code in the Log - the launcher being wrong about a runtime is worse than a database that fails
with a message.

**Still per-machine truth rather than per-run.** The framework rule and the roll-forward it uses are unchanged
and still pinned by their tests: a shared framework with the same name and major at an equal or higher version
is a match, another major is not. What changed is only what an *unanswered* probe means.

### 15. A copy of the game is a build, and builds go stale - *done*

**What was asked for.** Portable mode plays the game out of the launcher's own folder, and that copy is made
once, so it can be an older build than the one installed on the machine with nothing in the folder itself to
say so. The ask was a check at start-up that finds the local install, says when it is newer, and offers an
update - by either of two methods, one that rewrites the whole game and one that reads both copies and writes
only the difference.

**Where the question belongs, and why it is not the patch server's version of it.** The launcher already
compares client builds in one place - the preload plan, which checks whether the patch server's mirrored files
match the build being played - but that is about a *server's* files. This is about the launcher's own copy
being behind the machine it lives on, which the patch server knows nothing about. What is reused is the part
that was already the truth: `ClientBuildReader` reads the revision out of the client itself
(`bin\revision.dat`, falling back to the client's log) and orders two builds by the number in them, so neither
"newer" nor "the same" is guessed at. `PortableClientUpdate.Check` is the whole of the question, and it is a
pure function of two folders and one remembered path.

**The install to compare against is found rather than assumed.** The folder the copy was made from is written
down when a copy finishes (`LauncherSettings.PortableGameSourcePath`, stored through the launcher's own path
rules, so a portable folder carried to another drive keeps it), and it is used as given - a remembered install
is a stable answer where a search is not. With nothing remembered, the usual places are searched, which is also
what makes a portable launcher carried to a second machine useful: it finds *that* machine's install. A
remembered folder that is no longer a game, or one inside the copy's own folder, is passed over rather than
trusted.

**Nothing is claimed from a build that cannot be read.** Either revision being unreadable means no comparison
is made at all: a client states its own build once it has been started, so a copy that has never run has no
revision to read, and "I could not tell" must not be reported as "you are out of date". The reasons a check
decided against saying anything are kept in the result for the Log, and the card is only ever drawn when there
is genuinely a newer build to move to.

**Two methods, one result - so the choice is the user's.** `GameClientUpdateService` plans first and writes
afterwards, which is what lets it check the free space before removing anything and say "nothing to do" without
writing a byte. `Replace` plans every source file to be written and every file in the copy to be removed;
`Mirror` writes a file when it is missing, a different length, or a different hash, and removes what the newer
build does not have. The difference between them is *only* what they spend - one writes the whole game, the
other reads both trees to find out what to write - so the launcher does not decide it: the card offers both,
says what each costs, and defaults to the reading method. Both finish by removing the folders a newer build
dropped, so a build that deletes a folder does not leave its skeleton in the copy for ever.

**Automatic check, manual update.** The check runs on the client state refreshing, which is once at start-up
and again whenever the game folder changes, and it is deliberately not in `RefreshDerived` - it reads registry
keys and walks the usual install locations, and it is only ever interesting when the folder is what changed.
The update runs when it is asked for and not before, **Not now** puts the offer away for the run rather than for
ever, and afterwards the folder is re-read rather than assumed: that is what makes the offer disappear when the
update worked, and the card stays up to say what it did. `GameClientUpdateTests` drives both methods over two
real build trees, checks the copy equals the source file for file afterwards, and drives the whole thing
through a portable launcher and its command.

### 16. The runtime ships with the launcher, so nothing has to be installed - *done*

**What was asked for.** The launcher's own data lives in the RavenDB the server ships, that database is a
framework-dependent application, and the answer until now was to ask the user for a runtime - a screen, a
link, a version to match, an installer to run. For a project whose whole promise is "unpack the zip and run
the exe", that is a step that should not exist. The ask was to delete it: bundle a runtime with the build,
accept a few more megabytes of download and disk, and take the requirement off the user's machine.

**The runtime the launcher is already published into cannot be borrowed, which is why there are two.** A
self-contained publish is a flat folder of DLLs plus `coreclr.dll`, and a framework-dependent application
resolves its shared frameworks out of an *installation layout* - a host, `host/fxr/`, and
`shared/Microsoft.NETCore.App` and `shared/Microsoft.AspNetCore.App` under one root. Nothing in the
launcher's own output is that, so "the launcher already carries a runtime" does not help the database
inside it, however much it sounds like it should. The alternative to a second copy is what was already
there: ask the machine for one. The user's answer was the second copy, and it is the one that leaves
nothing for a user to do.

**One folder, found one way.** `DotnetHost.BundledRuntimeFolderName` is `runtime` and
`BundledRuntimeDirectory` is that folder beside the executable - inside the launcher's own folder, which is
the one `portable.txt` is looked for in, so a portable launcher carried to another machine carries its
runtime with it and the whole release folder can be moved between drives. `DotnetHost.FindBundled` looks for the muxer in it
(`dotnet.exe`, or `dotnet` elsewhere), and it is a settable `Func` for the same reason `Find` is: whether a
build ships a runtime is a fact a test has to be able to decide. What is found has to be the muxer rather
than the assemblies, because pointing a host at a runtime of its own is done with an environment variable -
`DOTNET_ROOT`, and the architecture-specific spelling of it that the host prefers - and
`DotnetHost.UseRuntimeRoot` sets both to that folder when `EmbeddedRaven` starts the database.

**Which runtime is used is one decision, and it can now say which one fell short.** `DotnetHost.HostFor`
returns a `DotnetHostChoice`: the host, the root to point it at when it is the bundled one, which of the two
it is, and - when a host was asked and answered short - the framework it cannot offer. The order is the one
the ask implies: the bundle when it can run the database *or when it would not answer*; a machine-wide
install when the bundle cannot but the machine can (a database that has moved to a newer major than the
shipped runtime is better served than by refusing to start); and, when neither fits, still the bundle if
there is one - so the screen can name the runtime a user can actually see, and say that installing one on
the machine is what gives the launcher something else to use. Nothing anywhere is `NoDotnet`, the one case
where both places are named. "Would not answer" is still not "not enough": a probe that times out is a
fact about the moment, and the application that genuinely cannot run reports its own failure into the Log.

**The screen stays, because a build from source has no bundle.** `ImlightRequirements.For` checks a runtime
exactly as it did - the same call, in the same fourth layer - and its two messages now differ in a way that
matters to what a user does next: a short machine-wide runtime is named by its path, and a short shipped
runtime is named as *the runtime ViewLight ships with*, in a sentence that says the machine-wide install is
the fix. The guide behind the link opens by saying the same thing, since "install the .NET runtime" reads
differently once the launcher may already be carrying one. Which runtime the database actually ended up in
is written into the Log when the launcher's own database starts (`EmbeddedRaven.HostDescription`): on a
machine with no .NET of its own, that line is the first thing worth knowing.

**How the release fills it.** Two commands, between the publish and the zip: fetch
[dotnet-install.ps1](https://dot.net/v1/dotnet-install.ps1), then run it into `publish/runtime` - the exact
lines are under [Packaging and releases](#packaging-and-releases). It writes a real installation - the
muxer, `host/fxr/`, and both shared frameworks - without touching the machine it runs on. `aspnetcore`
rather than `dotnet` because ASP.NET Core is what carries *both* frameworks the database names; a channel
rather than a pinned version because the database is matched against the runtime's own roll-forward, so a
newer patch of the same major is a match, and pinning one would mean editing the release notes every time
.NET shipped a patch. Measured on this machine: the runtime is 107 MB on disk, the launcher publishes at
104 MB, and the zip goes from 43.0 MB to 87.6 MB. Nothing is paid in memory or in start-up time for it:
one of the two runtimes is used and never both, so the database is still one .NET process, and picking
between them is the same single probe - one process launch, remembered for the run - that the machine-wide
search already cost.

**Verified with the real thing rather than with an answer.** With that runtime installed into the test
project's own output folder - so the production `FindBundled` found it, un-stubbed - and with
`DotnetHost.Find` answering *no runtime on this machine*, the launcher's own check said Imlight was usable,
the database was started with the muxer out of that folder and `DOTNET_ROOT` pointing at it, and the real
RavenDB in `%LOCALAPPDATA%\ViewLight\server` came up on a free port, answered, and stopped cleanly when
asked. That check was a scratch harness and is not kept: what it proved is a property of the layout and of
`EmbeddedRaven`, and what *is* kept pins the choice itself - `BundleWith` decides what each of the two
runtimes offers, and `TheRuntimeBesideTheLauncherIsWhatRunsTheDatabase` asserts the host, the root it is
pointed at, both environment variables, and that a machine with nothing installed is still told it is
usable.

### 17. An update replaces the server's files, which means letting go of them first - *done*

**What was reported.** An update of an install that was already at the newest build - triggered from
Settings - ended in the Log as:

```
INFO   Updating server to r806919.Wizard_1_610. Existing settings will be kept.
ERROR  Install failed: The process cannot access the file
       '...\server\RavenDBServer\Acornima.dll' because it is being used by another process.
```

The suspicion in the report was that the launcher was in two minds about the install: replacing it while
also reading its database. That was right about the state and wrong about which install - it was not
mixing up two folders, and it knew the install was there (it said "Updating"). It was holding the files.

**What was holding them.** Both of the launcher's databases are the server's own `Raven.Server.dll`, started
out of `<server>\RavenDBServer` so that the storage, the port and the documents are Imlight's rather than a
reimplementation - which means that while either of them runs, that folder's ~200 assemblies are loaded and
cannot be written over. Both of them run there: the launcher's own database (`-c ...\raven\launcher-settings.json`,
held for the whole run) and the player database while Imlight is not running
(`-c ...\raven\embedded-settings.json`). An overlay that replaces those assemblies therefore collides with

the launcher itself, every time - and the overlay is what an update is. Confirmed on the machine it was
reported from, with both processes listed by their command lines and both running from the portable server
folder the update was writing into.

**Measured rather than assumed.** Against a database started from a copy of those binaries: writing over one
of its DLLs fails with exactly the message above; `EmbeddedRaven.StopAsync` then takes about 180 ms, and the
file is still not writable for about 110 ms after the process is gone. So stopping is necessary and not quite
sufficient, which is why the overlay waits a moment rather than giving up at the first refusal, and why what
it reports when the wait runs out is about a process rather than about a file.

**The fix, in three parts.** `ServerInstallService.InstallAsync` takes a `beforeWriting` callback, invoked
once immediately before the first file is written and never at all if the release never gets that far - so a
download that fails costs no stopped database. The launcher passes
`MainWindowViewModel.ReleaseDatabasesForInstallAsync`, which closes the client, stops the launcher's own
database and gives the player database back, in that order, through the same paths the cloud sync uses;
whichever way the install ends, the `finally` starts both again in the background, and both opens are
single-flight now so that a second ask is answered by the start already running rather than by a second
database on the same port. Neither database is needed for the minute an update takes - they are read again as
soon as they are back. And since stopping the launcher's own databases cannot help against the one thing that
legitimately holds that folder - Imlight, running - the install refuses while the server is up, and a hold
that outlives the wait is reported as `LockedFileMessage`: the file it could not replace, and the button to
press once the server is stopped.

**Verified against the failure itself.** A scratch harness (not kept) started a real database from a copy of
the server's binaries and then ran the real install pipeline over that folder, with a release archive holding
the very file from the report. Without the handover: failure after 1181 ms - the retry budget, so the wait is
bounded - reported as *A file in the server folder is in use by another process ... if Imlight is running,
stop it and press Download latest again*. With it: success in 2151 ms, `Acornima.dll` replaced by the
archive's, the database stopped and its port silent. What is kept in the suite is the message and the wait
(`ServerInstallServiceTests`: a file held for the whole install is reported that way, a file held for half a
second is waited out, and the callback is asked for the folder before the first byte and not at all for a
download that failed).

**The unrelated thing that looked related.** That machine has two complete server installs - the one in
`%LOCALAPPDATA%\ViewLight` it was switched away from and the portable one beside the launcher - because
portable mode downloads into its own folders rather than moving what is already there. It is a real mess
(see [Not done yet](#not-done-yet)) and a plausible suspect for a file in use, but it is not this one: the
processes holding the folders ran out of the portable install, with the portable launcher's own settings
files, and nothing at all was running from the old location. What followed from it is
[task 18](#18-the-launchers-own-data-stops-borrowing-the-servers-files---done).

### 18. The launcher's own data stops borrowing the server's files - *done*

**The rule, as it was put.** The server folder is *Imlight's*, managed by Imlight rather than by the launcher.
The launcher writes the files Imlight expects an outside tool to write - `Imlight.ini` - and nothing else; its
own data goes under `%LOCALAPPDATA%\ViewLight`, or a folder inside the launcher's own folder in portable
mode, in both cases its own and nowhere near the install. The question that came with it was whether the
launcher was breaking that rule anywhere. It was not, with one exception that was not a *file* at all.

**The audit.** Every write the launcher makes into a server folder, from the code: `Config\Imlight.ini` and its
`.bak` (the file an outside tool is expected to edit), and the release overlay when it installs or updates
Imlight. Everything else it owns is under `LauncherPaths.DataRoot` - `launcher.json`, `profiles.json`,
`profiles\<account>\`, `art\`, `downloads\`, `staging\`, `backup\`, `database\`, `raven\` - and the two
`PatchServerPreload` writes go into the *patch server's* revision folder, which is the patch server's own
install. Two couplings were left, and neither is launcher data: the launcher's databases *ran* the server's
`Raven.Server.dll`, and the idle player database opens the server's own data directory (that one is the
server's data, written by the server's own engine, which is the feature rather than the leak).

**The exception, and why it mattered.** Running the server's binaries is not storing anything in its folder,
but it has every consequence of sharing one: while either of the launcher's databases was up, those ~200
assemblies were loaded and could not be replaced, so an Imlight update failed against the launcher itself
(task 17). A copy of its own is what makes the launcher's own notes independent of Imlight's folder in the
way the rule describes - and it is what stops an update colliding with the launcher at all.

**What the copy is.** `LauncherRavenEngine` keeps it in `ravendb\` under the launcher's own root, which means
`%LOCALAPPDATA%\ViewLight\ravendb` normally and `<launcher folder>\ravendb` in portable mode, so a portable
folder still carries everything it needs. It is made *from the installed server* rather than shipped or
downloaded: installing Imlight is what brings a new engine, the launcher copies it at the version that
install has, and a version stamp written last (`.viewlight-engine.json`) is what says a copy finished - a
folder without it is what a machine that lost power left behind, and it is not run. Copying happens off the
UI thread, before the launcher's database starts and again after an install, because those are the two
moments nothing is running out of either copy.

**It is pruned, and the pruning was measured.** The engine is 567 MB as shipped, of which 422 MB is
`runtimes\` - native libraries for every platform RavenDB supports. A copy keeps this machine's runtime
folder and the fallback the host resolves through, and drops the rest: 179 MB, 288 files. The rule is a list
of *known other platforms* rather than a list of wanted files, so a folder nobody here has heard of is copied
rather than dropped - pruning conservatively costs 30 MB more than pruning cleverly and cannot cost a database
that will not start. The pruned copy was then made to do real work: started, created its database, wrote and
read back a document, stored and read back an attachment, stopped clean.

**Which engine runs what, deliberately.** The launcher's own notes run on the launcher's own copy. The
*player* database still runs on the engine inside the install, because its data directory is Imlight's and is
written by the engine Imlight ships today: opening it with an older copy of RavenDB - the one this launcher
happened to be built against - is a risk nobody asked for, and the same engine as the server's next boot is
the right answer for the server's data. The install's engine is also the fallback for the launcher's own
database, tried once and only when its own copy fails to start: a copy made from an engine older than the data
it is asked to open, or one a half-finished copy damaged, is not a reason for the launcher to have no notes at
all. Which engine a running database is on, and which runtime it was started with, are written into the Log.

**What the change deliberately does not touch.** The requirement screen still asks for the engine inside the
install: Imlight cannot boot without its own RavenDB, so an install missing it is still an install to fix. And
the backup set is unchanged - it is two directories, named explicitly, and the engine copy is not one of them.
It is machinery rather than notes: it is re-derived from the install at no download cost, and backing up
179 MB of another program's binaries would make every backup bigger for something that can be made again. That
was the one thing worth checking about it, since the launcher's notes database *did* move out of the server
folder once before and a backup has to follow where the notes are - this time the notes have not moved.

**Verified against the real thing.** With a real (pruned) engine standing in for an install's, the production
path made the copy - 288 files, 179 MB, version 7.2.4.72015, the version the install had - and the launcher
then chose it rather than the install's, started on it, created its database in `database\` under the portable
root it was given, wrote and read a document, stored and read back an attachment, and stopped with the port
silent. The fallback still resolves the install's engine. That harness is not kept; what is kept is
`LauncherRavenEngineTests` (the folder it lives in, the pruning rule, the copy and its stamp, a copy that is
not made twice, one that is replaced when the install's engine moves on, one without its stamp is not a copy,
and no install at all), the resolution tests in `LauncherDatabaseTests`, and the backup test that asserts the
engine copy is not in a set.

### 19. The patch server is asked about before Home, instead of assumed - *done*

**What was asked for.** Running a fresh install downloads the server and says nothing at all about the patch
server - which Imlight cannot boot without, so a session always has one and the only thing to decide is whose.
Two things follow from leaving that unasked. The launcher's own default is a patch server on this machine and
a fresh one has none, so every session lands on the public instance without anybody choosing it. And the public
instance is exactly what a session played offline cannot rely on: a new zone, creature, item or single asset
that this machine has never cached cannot be fetched, and the game is stuck there rather than merely slower.
The question belongs in the first run, where the other two decisions already are, and the local answer is the
one worth recommending - it is the only arrangement that plays with no connection at all.

**What came out of it.** A third first-run screen, between the Imlight download and Home, asking it once: run
one on this machine (the default, with Aurorium downloaded there and then) or use the public patch server
(nothing downloaded, the address Imlight already has). `PatchServerChoiceAnswered` remembers that the question
was put, the gate is those three screens in order, and a machine that already has a patch server is never
stopped by it. The point of asking rather than defaulting is that the two answers are not equivalent, and the
consequence of the wrong one arrives hours later, in a game that cannot be played - which is not a thing to
find out from a softlock. Written up under [the patch server](#the-patch-server).

**One thing deliberately left where it was.** The screen does not start the patch server, or fetch anything
with it. Its first start is where the revision's file list is written, where the fetch begins, and where the
offer to hand it the game's files instead is made - and all three of those belong to Play, next to the thing
they are for, rather than to a first-run screen that would then be a download with a question attached. The offer it names was removed in
[22](#22-the-launcher-watches-the-patch-server-fetch-and-stops-copying-files-into-it---done); the fetch itself is
what Play now reports on.

### 20. A finished copy is noticed, and the card stops asking - *done*

**What was asked for.** In portable mode, after the game is copied in, two things went on being wrong on
screen. Home kept offering to copy the game in, with a card headed *copy your game into this folder*, about a
copy that had just finished. And Settings → the game folder went on saying *root.wad was not found* and
*bin\WizardGraphicalClient.exe was not found* about the folder the copy had just been written into - until
*Detect* was pressed again by hand, which then found both. The launcher had done the work and not looked at
it.

**What came out of it.** Both were the same missing read. A portable launcher starts out pointed at its own
`game` folder, so `ClientPath = result.Destination` was writing back the value that was already there: no
change, no `OnClientPathChanged`, and therefore no re-read of the very folder the copy had filled. The copy
now calls `RefreshClientState()` on the way out, as the update path already did - that contrast is what made
it obvious - so the executable and the archive are found the moment the copy finishes. The card's ask was
separated from its report for the same reason: it is the ask that has to end, so `ShowPortableClientPrompt`
decides the folder box and the buttons, the heading changes with it, and the message that proves the copy ran
is left on screen where it belongs. A copy that is behind the machine's install is still the other card -
[15](#15-a-copy-of-the-game-is-a-build-and-builds-go-stale---done) - because that is a different situation
with a different fix. Written up under [copying the game in](#copying-the-game-in).

### 21. A first sync never writes over the cloud - *done*

**What was asked for.** A cloud sync that is dangerous in a way nobody expects, on the one day it matters
most. A launcher started fresh creates its own database, and may already have a server database from one run;
the cloud holds the real thing. Say yes to the backup and that machine's copy is what the sync weighs as
current, so signing in on a new install can wipe the profile data that was the entire reason to sign in -
destroyed before the sync that was supposed to restore it could run. The rule asked for is blunt and right:
**when the cloud is being set up, the remote always wins.** A machine with nothing up there wins by default
and syncs ordinarily; a machine pointing at a cloud that *does* hold something takes that copy first, and
only then does the normal logic apply.

**What came out of it.** `BackupSync.Plan` no longer stops to ask when both sides are present, they differ,
and there is no ancestor: that state is a launcher which has never synced with this cloud, and the answer is
a restore. The restore settles the ancestor as it already did, so the next sync
is judged the ordinary way and this is never the everyday case. The conflict path is untouched and still
reached where it belongs - both sides changed since they last agreed - and the two manual buttons stay as
they were, because a person pressing **Back up now** is a decision rather than a guess.

**The second route to the same loss, found while closing the first.** An upload writes the archive and then
its manifest, so a run that stops between the two leaves a real backup with nothing beside it saying what it
holds - and a manifest that is not there reads exactly like a Drive with no backup on it. That planned an
upload: a fresh machine writing its empty database over a backup it could not see. `ICloudBackupStore`
gained `ExistsAsync`, asked once about the archive - a listing rather than a tens-of-gigabytes download - and
a cloud holding an archive with no manifest is now brought down (it carries its own manifest) instead of
written over. A search that fails is a third answer rather than a second, and it stops the sync rather than
being read as an empty drive. Written up under [a cloud backup of the few databases that
matter](#5-a-cloud-backup-of-the-few-databases-that-matter---done) and in the readme's table of what a sync
does.


### 22. The launcher watches the patch server fetch, and stops copying files into it - *done*

**What was asked for.** Two things that turned out to be one. The first is that a first fetch is invisible:
the patch server is up, it is downloading eighteen gigabytes, and nothing on screen says how far along it
is or whether it is moving at all - so the answer to "how long will this take" was a shrug. The patch
server's own software does not obviously expose that, so the request was to find out whether it can, and to
show the progress and the finish time if it can. The second is that the launcher filling the patch server
in from the local game is a shortcut around the way the software is meant to work: the patch server never
downloaded those files, may never have indexed them, and "the bytes matched when we checked" is not the same
as "this is the state the patch server expects to be in". The two together point one way - stop standing in
for the patch server's job, and start reporting it.

**What the patch server actually exposes.** Aurorium's HTTP API is `/revisions`, `/latest` and
`/{revision}/{file_path}`; there is no progress route, and its `indicatif` bars are drawn on its own console.
Its database knows which assets a revision *should* have rather than which have arrived, and its log lines
are `info`-level events rather than a percentage - `Revision {name} has {n} updated or new assets`, `All
downloads completed` - which are worth having but are not a figure to put a bar under. What it does leave on
disk is exact: the revision's `LatestFileList.xml`, written before the first asset is fetched, and the files
themselves. So the launcher measures instead of asking, and the request's own condition - *if the patch
server exposes that* - is answered as: not over its API, but the disk it is writing to is enough.

**What came out of it.** `PatchServerFetch` counts a revision's file list against the disk, with four rules
that make the answer honest: a file counts as held only at the length the revision gives it; a file missing
from the revision's own folder is looked for in the patch server's other revisions, because that is where an
unchanged asset is served from and counting only the newest folder would report thousands of files missing
that are being served; `.part` bytes count as progress but never as something the patch server can serve;
and a preload's `.preloadpart` counts as neither until it is moved into place. `PatchServerFetchPace` turns
a series of those readings into a rate - a window of at least fifteen seconds, each blended into the last -
and drops the estimate with a plain sentence when a whole window carries nothing, because Aurorium retries
what it could not get on its own `fetch_interval` schedule and a countdown through that would be a lie. The
watch is started and stopped with the patch server process, so a patch server started by hand from Settings
is watched from there, and the line and bar appear under Home's patch server row and in the patch server
card. Written up under [watching it fetch](#watching-it-fetch).

**And the copy stops being something Play does.** The offer before a session, the setting that turned it
into something automated, the dialog it asked in, `LauncherPhase.CopyingGameFiles`, and the two places the
answer was remembered are all gone. What is left is the button in Settings and the plan behind it, so the
copy is something asked for about a known obstacle - the files Aurorium's own two-minute request limit means
it can never finish - rather than the shape of a normal first run. This supersedes the *Play asks* paragraph
and the *offer before a session* wording in [the patch server](#the-patch-server), and the copy's own row in
entry 1 above.

### 23. The server's revision is the one that is kept, and the revisions nobody wants are removed - *done*

**Superseded in part by [24](#24-the-two-revisions-are-kept-in-step-and-play-waits-for-the-one-file-the-boot-needs---done).**
Which way round the two revisions are made to agree was turned around a day later, and the cleanup rule lost
its third "kept" case with it. Everything else here - the file hand-off and how exact it is, the first-run
offer to prepare the files, the two cleanup modes and the guards on them - is still what the launcher does.

**What was asked for.** Four things that are one arrangement. Downloading is the default, and copying the
game's files in is allowed only when the client version matches what the patch server actually wants *and*
the files it wants are exactly the files the client has. As a safety net the launcher should make the patch
server keep the revision **the server wants** rather than moving the server to the revision the client is -
which is what makes a server able to retrieve its own files - with the old way round kept behind a toggle
that warns first. The first-run screen should offer to prepare the files there and then, with a progress bar.
And the revisions on disk should be looked after: automatic cleanup that keeps the two in use and removes the
rest, or a manual list where each one can be kept, deleted, or asked for.

**Why "the server's revision" is the whole of it.** `GameRevision` does two jobs at once: it is the
`V_{GameRevision}` segment Imlight asks its patch server for, and it is the revision the server accepts
clients on. So the two halves of this only work together when the patch server holds *that* revision. The
old arrangement moved the other value - the launcher read the revision the patch server held (whatever
Wizard101 was serving) and wrote it into `GameRevision`, so the server accepted the newest client instead.
That is a decision made on the say-so of a file server about a server build, and the launcher cannot check
whether the build still fits: on this machine the server is `r806919` pinned to `r803238`, and the mirror had
rewritten `GameRevision` to `r806919`. Sessions where the pair does not fit show up as anomalies - refused
logins, missing content - so it is now off by default and behind a warning, and the default is the direction
that has nothing to guess about: the patch server is made to keep the server's revision.

**How little the launcher can do about that, and why it says so.** Aurorium decides which revision to fetch
from one place only - the patch host in its config, which it asks `MSG_LATEST_FILE_LIST_V2` for and from
whose answer the revision name falls out. There is no way to ask it for a *particular* revision, old or new,
and the launcher will not invent one by writing files under a revision name the patch server has never
heard of. So "make the patch server keep the server's revision" has exactly one mechanism behind it: the
exact file hand-off above, aimed at the server's revision, and only when this machine's client *is* that
revision. Where it cannot be done it is reported - a line in the Log, and the same refusal the copy card
shows - because a session that fails on a 404 and a session that is told why are very different things.

**"Exactly" was measured, and it is the strictest form of it that this machine's data supports.** Against the
real fetched revision (`r806919`, 3,825 files) and the real Steam install (7,885 files): every file the two
had in common matched in length, none differed - the whole of `Data\` and `Bin\`, 3,725 files - and the 100
revision files the install does not have are all `PatchClient\` and `Windows\` launcher files, which a Steam
install does not ship. Set-for-set equality therefore holds for nothing on a real machine, and a rule written
that way would have silently disabled the feature. What is enforced instead is: the revision is the server's,
the source build is that revision, and **no file may be at a wrong length** - which is the condition that
actually proves whose build this is, since a size mismatch is exactly what a patched-past install looks like.
Missing files are left to the patch server; extra files in the install (logs, launcher files) are none of the
revision's business and are ignored.

**Preparing the files now, on the first-run screen.** The screen that asks whose patch server now also asks
whether to prepare its files immediately: pressing it starts the patch server there and shows the same line
and bar Home and Settings show, and leaving the screen does not stop the fetch - a fetch only advances while
the process is alive, and the launcher keeps it alive. "Not now" is an answer and is remembered for the
screen's lifetime. This is the one place the patch server is started before Play, and it is deliberate: the
first fetch is the whole of what a patch server costs, and a machine that has just chosen one may as well get
it started while it is looking at the screen. What is *not* offered is a launcher that prepares files on its
own - that was removed in entry 22 and has not come back.

**The revisions on disk.** `PatchServerRevisionCatalogue` reads the patch server's data folder (as its own
config names it, not as its default has it) and gives every `V_*` folder a role. This entry gave it three:
the revision the server asks for, the revision this machine's game is, or neither - and the middle one is
the case [entry 24](#24-the-two-revisions-are-kept-in-step-and-play-waits-for-the-one-file-the-boot-needs---done)
took away, because a client's own revision is a folder in the *game's* install rather than anything a session
asks a patch server for. Nothing outside that folder, and nothing that is not
named the way Aurorium names a revision, is ever considered - a folder somebody keeps beside the revisions is
theirs. Removal is guarded twice over, because the failure is a download that cannot be repeated: a revision
is only spare if its role says so, **and** the patch server is not writing into it - no `.part` file in it and
nothing under it written within the last twenty minutes, since a revision Aurorium has just started on has
its file list on disk before its first asset and otherwise looks exactly like an unused one. The guards are
re-checked per revision at the moment of removal, so a fetch starting between two deletions is not something
the batch can get wrong.

**Automatic is the default, and choosing it is the permission.** Each revision is around 18 GB, and the patch
server fetches a new one whenever Wizard101 publishes one, so the folder only ever grows - and it is the
patch server's folder, so nobody else will ever tidy it. The card in Settings says all of that before the
choice is made, the choice can be changed at any time, and every removal is written into the Log with what it
was and why. The pass runs on the way in, when a patch server stops, and from the button; the pass the
launcher makes on its own account is deliberately **silent** about it - it runs on a thread of its own while
the launcher is still coming up, and the Log is a collection the UI thread owns, so the outcome goes on the
card instead. That was found by a test rather than by reasoning: the first version logged it, and a
background append racing the constructor's own log lines corrupted the log document.

**Manual is the same cleanup with a person in the middle.** The list shows every revision with what it is and
what it weighs, a tick that means keep, and a Delete button that removes the unticked ones; the two in use
start ticked, so the list opens on the answer automatic mode reaches by itself. Manual may delete a revision
that is in use - that is the point of it, and the user's list *is* the permission - but it may never delete
one the patch server is writing into, which is the one rule it does not get to overrule. Each row also offers
**Download**, which is how the honest half of that request is answered: Aurorium fetches what Wizard101 is
serving when it looks, so an older revision cannot be asked for and the row says so rather than looking like
something that might work.

| Piece | What it does |
| --- | --- |
| `PatchServerPreload.Plan` | the gate, restated as three conditions on the server's revision rather than the client's |
| `MainWindowViewModel.ServerWantedRevision` | `GameRevision`, read from the config the session will use - the one revision worth having on disk |
| `PatchServerRevisionCatalogue.List` | every revision on disk with its role, its size, its file count, and whether the patch server is writing into it |
| `PatchServerRevisionCatalogue.RoleOf` | server's revision, the patch server's newest, or spare - as a rule, so it is testable without a disk (the client's revision was the third case until entry 24) |
| `PatchServerRevisionCatalogue.Delete` | per-revision re-check, never throws, reports what it kept and why |
| `PatchServerRevisionSynced` | the toggle, on by default; off pins the server to its own config and leaves the hand-off as the only way its files arrive |
| `PreparePatchServerFilesNowCommand` / `SkipPreparingPatchServerFilesCommand` | the first-run offer, and taking it or leaving it |
| `PatchServerRevisionCleanup` | `Automatic` or `Manual`, stored as text so a value from a later build falls back rather than failing to load |

### 24. The two revisions are kept in step, and Play waits for the one file the boot needs - *done*

**What was asked for.** Two faults, reported from playing. The server starts warming up *before* the patch
server has the files it is going to ask for, so it asks, gets a 404 and sits there. And the Log does not say
the things worth knowing about a fetch: which file it is downloading, how big that file is, which revision
is being fetched, and whether that revision is the one the server asks for. Then, from working out what to do
about the first one: the patch server cannot be told which revision to fetch, so the server should be kept on
the patch server's instead - the same sync this file had described as the off-by-default fallback, but on by
default and with no dialog in front of it - and automatic cleanup should stop keeping a revision for the
client's sake.

**What came out of it.** The toggle is `PatchServerRevisionSynced` and is **on by default**: the launcher
reads `GET /revisions` and writes the patch server's revision into `GameRevision`, so a server always asks
for files the patch server has *and* accepts the client that has just updated. It is applied whenever a patch
server starts, again on every pass while Play waits for its files, and turning it off restores the value the
config had before the launcher's first write - and only that value, as before. The dialog that used to guard
the other direction is gone rather than reversed: there is nothing left to warn about, because the direction
it warned about (a server build running with a revision it was not published with) is now what the launcher
does by default and cannot check either way. `UndoAMirroredRevisionLeftBehind` went with it - a
`GameRevision` an older launcher mirrored is now exactly what this build wants, so undoing it on the way in
would be undoing the arrangement.

**Play waits, for one file, and never without a way out.** `WaitForPatchServerFilesAsync` sits at the top of
`StartServerAsync`, after Imlight is pointed at the patch server and before anything is spawned: it asks for
the boot file with a `HEAD`, and if it is not there it waits - re-asking the sync each pass, showing the fetch
line on Home, and offering **Play anyway** beside it. That button is the part worth being careful about: a
wait can be a long time on a first run, so it is escape-able, and taking the escape is written into the Log
with what it is about to cost. The wait ends by itself when the patch server goes away, and the time spent in
it is added back onto Play's own readiness deadline, because a server that has not been started yet is not a
server that is being slow.

**The fetch says what it is doing.** `PatchServerDownloadInFlight` is one `.part` file Aurorium is writing,
named after the file it will become and sized from the revision's own file list - so the line reads
`Downloading Equipment-WorldData.wad (12.3 MB of 281.4 MB) now (4 files in flight)`. Several are in flight at
once and the one named is the most recently written, which is why the count goes with it. The same sentence
is written into the Log when the file changes and once a minute besides. The revision is checked against the
one the server asks for on every sample, and a mismatch is said by name in the line and written once as a
warning - that one is not progress, it is the reason a session will fail.

**The server's own log is where that failure exists, so it is read there too.**
`ServerLogFacts.PatchServerRefusedAFile` reads the server's `Error while downloading file
http://127.0.0.1:12369/V_r803238.Wizard_1_610/Data/GameData/Root.wad ... 404 (Not Found)` line, pulls the
revision and the file out of the address, and Home says which two revisions disagree and which of them the
launcher moves. It is reported once per address - the server retries and restarts its patch actor, so the
same address comes round again and again - and the notice is cleared at the start of the next attempt. Nothing
is acted on: the launcher does not restart the server under the player, which is why the sentence it prints
ends in "stop the server and press Play".

**Cleanup keeps one revision, not two.** With the sync on, the revision the server asks for and the newest the
patch server has are the same folder - so the third case the catalogue used to keep, *this machine's game is
this one*, went away. A client's revision is a folder in the game's own install; what a session asks a patch
server for is the revision the server names, so an older build's folder on the patch server was 18 GB held
for nothing. `RoleOf` now answers *the server's revision*, *the newest*, or *spare*, and the newest is one of
the two kept answers because deleting it would be deleting the fetch the folder exists for. Below the newest,
an older folder **the server asks for** is still kept, which is what makes the sync-off arrangement work at
all: that revision cannot be fetched again and the server cannot serve its clients without it.

| Piece | What it does |
| --- | --- |
| `PatchServerRevisionSynced` | the toggle, on by default; applied at every patch server start, and again on every pass of the wait |
| `MainWindowViewModel.ShouldWaitForPatchServerFiles` | the rule: a patch server is up, it is the one Imlight will ask, and it cannot serve the boot file |
| `WaitForPatchServerFilesAsync` / `PlayAnyway` | the wait and its way out, with what it costs written into the Log |
| `PatchServerFetch.DescribeCurrentFile` | which file it is on, how big it is, and how many are in flight |
| `PatchServerFetch.DescribeRevisionMismatch` | the two revisions that disagree, named, and what that means for a session |
| `ServerLogFacts.PatchServerRefusedAFile` | the server's own account of it, read out of its log |
| `MainWindowViewModel.ReportPatchServerRefusal` | the notice on Home, once per address, with the way out |
| `PatchServerRevisionCatalogue.RoleOf` | server's revision, the newest, or spare - the client's case removed |