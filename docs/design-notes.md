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
- **A machine .NET runtime with ASP.NET Core**, which is what the launcher's own database runs in: the
  RavenDB the server ships is framework-dependent even though Imlight's own build is not. Checked on the
  first screen rather than discovered at a database that will not start, so a machine without one is told
  what it needs instead of being left with an empty profile screen
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
| The .NET runtime that database asks for | Because that database is framework-dependent (below). |

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

**The .NET runtime is part of the requirement, which is not obvious.** Measured on a real install:
Imlight's Windows build is *self-contained* (`Imlight.Director.runtimeconfig.json` has
`includedFrameworks`, with `coreclr.dll` and `hostfxr.dll` beside it), so Imlight itself needs nothing from
the machine. The RavenDB inside it is the other way round: `RavenDBServer\Raven.Server.runtimeconfig.json`
names `Microsoft.NETCore.App` **and** `Microsoft.AspNetCore.App` as shared frameworks, and that folder
holds 198 DLLs with no ASP.NET Core among them. `EmbeddedRaven` starts it with a machine `dotnet`, so a
machine-wide runtime has to exist and has to have the frameworks the database asks for. What it asks for is
read from that file rather than hard-coded, and the rule is the runtime's own roll-forward: the same
framework, the same major, a version at least as high. An Imlight that ever shipped the database
self-contained would need nothing here and the screen would not ask for anything.

What that means for a user is the point of the screen rather than a consequence to soften. The launcher's
own release is self-contained, so a machine with no .NET at all still **starts** - the launcher, the
folders question, and this screen all work - and only the server's database is what cannot run. The screen
uses that shape deliberately: it says the launcher itself starts without .NET, names the frameworks the
database asked for, and links to the version's page, so a machine missing one dependency is walked through
it instead of meeting a launcher that never appears, does nothing, or dies with an error nobody can read.
Nothing starts a database before that screen is satisfied: holding one open for a launcher nobody can use
is work for nothing.

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
| A copy is only ever planned for an install whose build **is** the revision the patch server is holding (`PatchServerPreload.IsTheBuild`) | the revision folder is named after the build, and the folder plus its file list only exist once Aurorium has fetched that revision - so a build of any other revision has nowhere to go and is refused instead |
| A file goes in only when its length equals the length the revision's own file list gives for it | the right name at the wrong length is a different build's file, and a wrong file cannot be repaired later: the patch server only ever fetches what is missing, so it would serve that file forever |
| Nothing is overwritten, and every file is written as `<name>.preloadpart` and then moved into place | what a patch server already has is what it is prepared to serve, and Aurorium skips by name alone - so a copy interrupted halfway must not leave something that looks downloaded. The suffix is deliberately not `.part`, which is Aurorium's own name for a download in flight |

**The plan comes from the patch server's own file list**, `LatestFileList.xml` in the revision folder,
written by Aurorium on a first pass before any download - not from a list the launcher fetches itself. That
keeps the two views of the revision identical by construction, and it is why the copy is offered from Play
*after* the patch server is up rather than before: an unstarted patch server has no revision folder and
nothing to plan against. The endpoint binds before the file list is written, so Play waits a bounded twenty
seconds for it - without that wait, the first Play after installing a patch server would never be offered
the copy at all.

| Piece | What it does |
| --- | --- |
| `PatchServerManifest` | reads a revision's `LatestFileList.xml` for paths and sizes (`TryRead` returns nothing rather than throwing, since a folder being written to is read as half an XML document) |
| `PatchServerPreload.Plan` | what would be copied and what would not, counted out of that file list: already held, held at another size, not in the build, in the build at another size. Nothing here writes |
| `PatchServerPreload.RunAsync` | the copy itself: off the UI thread, cancellable, resumable by being re-planned, never throwing - a failure is a line in the report |
| `MainWindowViewModel.DescribePreload` | the sentence under the folder box: how many files of the revision it holds, how many of the rest are here, and how much they weigh |
| `ShouldOfferPreload` | asked once per revision, and only when there is something to copy |
| `ShouldPreloadWithoutAsking` | the setting above, and the same "is there anything to do" test - a silent copy of nothing is still nothing |

**Play asks, and the answer is remembered per revision - or the asking is turned off.** The offer is a
dialog before the server starts, because that is the moment it is worth having: Imlight reads `Root.wad`
from the patch server during its own boot, so a copy that arrives later serves the next session rather than
this one. `PatchServerPreloadAutomatically` is the other way round and is off by default: on, Play stops
asking and just makes the copy for a patch server that holds nothing of the session's revision, saying in
the Log that it was a setting rather than an answer. The three conditions below are still what decides
whether there is anything to do - the setting only decides whether it is asked about. While it copies, Home says
so under a phase of its own (`LauncherPhase.CopyingGameFiles`) rather than sitting on *Getting ready…* for
ten minutes with nothing to explain it. Turning the offer down with *don't ask again* stores the revision it
was about, so the question comes back for the next revision rather than never - the files that cannot be
downloaded are per-revision, and somebody who said no to one has said nothing about the next. The same copy
is a button in Settings, for doing it on purpose, and it is safe to press while the patch server is fetching:
both are writing the same files, and whichever arrives first is the one that stays.

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
`GET /revisions` (`PatchServerRevisions`), and it is made for one reason: the revision mirror below needs
to know which revision it holds.

**The fetch is reported and kept alive, and Play still neither waits nor is refused.** Three things,
and no fourth:

| Rule | Where |
| --- | --- |
| Ask once, before the server starts, whether the patch server can serve the session's revision - `GET {address}/V_{GameRevision}/Data/GameData/Root.wad`, headers only, the same request Imlight makes first | `PatchServerServing` |
| Say so in the Log when it cannot, naming the file, and saying the fetch keeps running while ViewLight is open | `WarnIfPatchServerIsStillFetchingAsync` |
| Leave the patch server running when the server stops **if** that is the answer, because stopping it is stopping the download | `PatchServerStopsWithTheServer` |

The answer is remembered only so the third rule can be stated, and the Stop button says *Stop the server*
rather than *Stop the servers* while it applies - a button that promises to stop something it leaves
running is worse than one that says which of the two it means. `Stop` in the patch server card stops it
outright, because that is a person asking. Nothing is repointed on any of this: a session started inside a
first fetch still loads `Root.wad` from Imlight's own cache, or stops on the 404 - but it now stops next to
a Log line that says why, and the patch server is still fetching while the user reads it. Blocking Play
instead would be a spinner on a fifteen-minute job; refusing it would be the launcher deciding the session
is not worth having.

**Order: patch server, then server, then client.** The patch server is started and waited for *before*
the Director, because the Director reads it during its own boot - starting it afterwards would only serve
the next session. That is why the wait lives at the top of `StartServerAsync` instead of in `PlayAsync`:
Play goes through the same door, and a server started by hand from Settings gets the same order.

**A patch server failing is not a failed session.** Imlight keeps its own cached copy of `Root.wad`, so
the launcher reports the failure - Log, Home's row, Settings' state - and starts the server anyway. The
same reasoning is why a patch server that cannot serve *yet* does not hold a session back either: it is
reported, it is left fetching, and the session goes ahead.
Refusing to play because an optional convenience did not start would be the worse trade, and the
alternative (silently continuing) is what the row and the log lines exist to prevent.

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

**The mirror only ever asks this machine's patch server**, because that is the one it has an address for:
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

**A patch server can only hold what upstream currently offers, so the revision the server asks for can be
made to follow it.** This is the other half of a local stack, and it is a config value rather than a
process: Imlight asks a patch server for `{PatchServerInternalUrl}/V_{GameRevision}` and accepts clients
whose revision matches the same value, while a server build pins the revision it was published with. The
moment the game has been patched past that build the two disagree - Imlight asks for a revision the patch
server does not hold, which is the 404 above, and with `EnforceRevision` on the same client is turned away
at login. Updating Imlight is the fix when a build exists; when none does, *Make the server accept the
patch server's revision* is the deliberate act of saying the client that is here now is the one to accept.

Three rules, all pure and all tested, carry it:

| Rule | What it says |
| --- | --- |
| `PatchServerConfig.GameRevisionFrom` | The game revision a patch server's name stands for, and `null` for a name that is not one of its own |
| `PatchServerConfig.NewestGameRevision` | Which of several revisions is newest: ordered by the number each name carries, not by the order they were listed in, because a server that was handed a revision out of turn would otherwise have its first entry read as its newest |
| `MainWindowViewModel.RevisionToMirror` | The value to write into `GameRevision`, or `null` when there is nothing to write because the config already asks for it |

The mirror has no timer and no button of its own: it rides on `GET /revisions`
(`Services/PatchServerRevisions.cs`), which is the only request the launcher makes to a patch server and
is made for the mirror's sake alone. So it is applied whenever the launcher starts a patch server - Play's
own start, or the Start button in Settings - and the moment the toggle is turned on while one is already
running, which is what fills its box in without waiting for Play. A patch server that is not up is not
asked and nothing is remembered about it: the revision a session asks for is the config's until a patch
server answers.

Turning it off puts back the value the config had when it was turned on, **and only that value**
(`RevisionToRestore`): if the config no longer holds what the launcher wrote - a newer server build, an
edit by hand - the value in it belongs to whoever put it there, and the rule that keeps a patch address the
launcher did not write from being rewritten keeps this one too. The `GameRevision` box is greyed out
while the mirror is on rather than taking edits it is about to overwrite, and the line under it says which
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

669 tests covering the parts most likely to break quietly: INI round-tripping and merging (including
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
nothing a user chose is ever rewritten. The revision mirror is covered
the same way and from both ends: what a patch server's revision name is worth as a `GameRevision` (and
that a name which is not one is worth `null`), which of several revisions is newest when the list is not
in order, that nothing is written when the config already asks for it, that turning the mirror off puts
back only a value the launcher itself wrote, and - through the launcher's own settings and config files -
that the toggle remembers the value it replaced and restores it on disk.

The profile system is covered the same way: the log parser driven by the exact lines Imlight writes,
playtime and session counting across polls (including a gap between two of them not being credited), duel
attribution refusing to hand a win to nobody in particular, badges never granted twice and never taken back
by a later read that has not caught up, wizards, quests and the command log read from the database, which
remembered accounts may be shown and which may not, and the words the screen uses when there is no profile
to show.

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

The Imlight requirement - the launcher's first screen, and the rule behind it - is covered by sixteen tests
of its own: which piece each gap reports, that a database carrying its own runtime asks a machine for
nothing at all, that a framework-dependent one asks for exactly what its own file names, the roll-forward
rule that decides whether a machine can run it, the shape of a `--list-runtimes` listing, that the .NET
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
session pointed at it inside that window gets `404` for `Data/GameData/Root.wad` and stops, which is the
failure the launcher reports rather than works around. And the revision it fetches is upstream's rather
than the server build's, so a server pinned by `GameRevision` to an older build needs the mirror below to
be served by it at all.

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
is exactly what the mirror writes, with nothing written when the config already asks for it. Which is what
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

**The next round of work is [To be added](#to-be-added), at the end of this file** - five changes that came
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
  game's files live. The launcher watches for it to arrive and says so (see
  [running one instead of visiting one](#running-one-instead-of-visiting-one)), and makes the server ask
  for that revision once it is there. What it can also do is hand it the files a copy of the game already
  has - most of the revision, and all of the files too large for the server's own download limit (see
  [filling it in from a copy of the game](#filling-it-in-from-a-copy-of-the-game)) - and what is left after
  that is fetched as before. That fetch is still not shown while it happens: there is no per-file progress
  to show, only Aurorium's own two lines in the Log, so the card says what is missing rather than how far
  along the fetch is.
- **Switching a session over mid-flight.** Once the patch server can serve, the *next* Play uses it; the
  running Imlight read its config at boot and is not repointed. Doing so would mean restarting the server
  under the player.
- **Updating Aurorium while it runs.** The install stops it first, because Windows will not replace a
  running executable.

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
| A new zone is slow to load | The zone's assets come from the patch server, and the public one is a community machine rather than KingsIsle's infrastructure. The server is local; the assets are not - running the launcher's own patch server is what fixes this, once it has finished its first fetch |
| `404 (Not Found)` downloading `Root.wad` from a `127.0.0.1` patch server | One of two things. Either the local patch server has not finished fetching that revision yet - its first minutes, and the honest answer is to leave it running - or it holds the revision Wizard101 currently offers while `GameRevision` pins the server to an older build, which has a fix rather than a wait: **Make the server accept the patch server's revision** - see [running one instead of visiting one](#running-one-instead-of-visiting-one). The launcher does not redirect the session for either of them: the address it was given is the address Imlight keeps |
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
on the release and the commit that produced both agree. `v1.0.0` was the first build; `v1.1.0` is the one
that adds the patch server, the launcher's own database, and the screen that stands down when Imlight or
the runtime it needs is missing.

The build shipped in a release is produced by exactly two commands:

```bash
dotnet publish src/ViewLight/ViewLight.csproj -c Release -r win-x64 --self-contained true -o publish
7z a ViewLight-win-x64.zip ./publish/*
```

**Self-contained on purpose.** The launcher's audience is people who want to play a private Wizard101
server without first installing a development toolchain, so the zip carries its own runtime and needs
neither .NET nor administrator rights *for the launcher itself*. The one thing it does ask a machine for is
the runtime its own database runs in - a consequence of keeping the notes in the RavenDB the server already
ships, and the reason the first screen checks for it rather than the profile screen failing later. The cost is size, and it is worth paying here: every alternative
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

## To be added

Five changes that came out of *using* the launcher rather than out of reading its code, written down here
in the order they will be done. Each one records the reason as much as the job - they came out of a
conversation, and a later reader should be able to finish any of them without having it again. The finished
ones stay in the list and point at the section above that now describes the rule properly, which is the same
habit [Not done yet](#not-done-yet) keeps.

### 1. The patch server is part of the stack, and Settings asks one question about it - *done*

**What was asked for.** The patch server was presented as an optional extra with a checkbox to turn it off.
It is not optional - Imlight has one or it does not boot - so what is actually being chosen is *whose*: this
machine's, or somebody else's. Settings asks that one question, and everything that follows from the answer
sits underneath it: the remote address (the public instance, or an address of your own) in one branch, and
the folder, the port, *start it with the server, stop it with the server*, *fill it in from the game folder
if it has nothing yet*, and the revision mirror in the other. The patch server address moved out of the
**Network** card, because it is a patch server setting.

**What came out of it.** Two new launcher settings and one rule change. `UseLocalPatchServer` (on by
default) says which branch is in play, and `PatchServerPreloadAutomatically` (off by default) turns Play's
offer into something that just happens. The rule for pointing Imlight at this machine's patch server used to
be *the launcher is running it* - `PatchServerManaged`, which is really "start it with the server" - and is
now *this machine's patch server is the one that was chosen*. A patch server started by hand from Settings
is therefore used by the next Play instead of the address being left as it was, and Home's row, the fetch
report, the copy offer and the mirror all hang off the one answer rather than off three separate switches.
Written up under [the patch server](#the-patch-server) and
[filling it in from a copy of the game](#filling-it-in-from-a-copy-of-the-game).

**One limit carried forward from this.** The revision mirror asks *this machine's* patch server and only
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
install it comes out of is self-contained, so what the launcher actually depends on is a machine-wide .NET
runtime with the frameworks the database names. That was measured against a real install and written up
under [Imlight is a requirement](#imlight-is-a-requirement-and-the-launcher-stops-and-says-so). The user
chose to keep the engine and make the runtime part of the requirement, which is why that screen exists and
why it checks the runtime rather than discovering it later at a database that will not start.

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

**The pictures are attachments on the profile document.** `ProfileStore.ReadImageAsync` hands back bytes for
the screen to decode, replacing the path it used to resolve and the profile screen used to read off disk; a
picture now exists for exactly one profile, travels with it, and is part of a backup of the directory. The
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

### 5. A cloud backup of the few databases that matter - *not started*

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

**The constraint.** RavenDB holds its data directory under an exclusive lock, so nothing is copied *or*
restored while anything has it open. The sync therefore runs while the launcher is idle and nothing is
running - which is exactly the window `EmbeddedRaven` already manages, so the taking-turns code is there and
this rides on it. A restore is applied before the next server start, never under a running server.

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
