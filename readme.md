# ViewLight

A launcher for **Imlight**, the [Revive101](https://revive101.de/) private Wizard101 server.

Point it at your Wizard101 folder and press **Play**. ViewLight gets the server for you, sets it up,
starts it, waits until it is genuinely ready, and then starts the game connected to it. Closing the
launcher shuts the server down again, so nothing is left running behind your back.

- Imlight: <https://github.com/Revive101/Imlight>
- Imlight documentation: <https://revive101.github.io/Imlight/imlight/>
- Revive101: <https://revive101.de/>
- ViewLight: <https://github.com/Smealm/ViewLight> - source, and the newest build as a prerelease

## Why ViewLight

**A private Wizard101 server, on your own account, for one player.** Running Imlight by hand means
finding a server build, unpacking it, editing `Imlight.ini`, starting the server and *then* starting the
game pointed at it. ViewLight does all of that from one button. The point of it is to make a private
server feel like a singleplayer game: your own server on your own machine, running offline, with the
worlds, the quests and the battles all where they should be, and no server to find and no login to type.

What it adds on top is a **profile worth having**. Achievements are earned as you play and they are
scored, and a launcher profile is tied to the server profile that already exists - so the character
levelling up in the game is the same one collecting points outside it. The launcher can also tell whether
an account is **clean**, meaning no server commands have ever been run against it, and a clean account is
worth **double** on everything it earns. That is not a score handed to anybody: it happened on your own
server, and the launcher watched it happen.

All three of the things that follow come from the server and the launcher being on the same machine:

- **It signs you in.** The launcher has direct access to the server it started - its database, its logs,
  its debug output - so it can skip the login screen, watch playtime, and notice accounts and characters
  as they appear.
- **It can point anywhere else.** The caret beside Play launches any other server you name, by address,
  and Wizard101's own servers are two entries in the same menu. That part is not special to ViewLight -
  the client does it by itself with `-L` - it is just easier than typing arguments.
- **It cannot get you banned**, for the reason set out below.

## What it does not do

**ViewLight modifies nothing.** It does not patch, hook, inject or otherwise touch a single byte of
Wizard101's data or memory, and it does not modify the server either. There is no client-side DLL, no
memory editing, no packet rewriting - from the game's point of view the launcher is not there.

So where do the profiles and the achievements come from? From the server, while the launcher is running
it. Accounts and characters are read from the server's own player database, and sign-ins, zone entries,
duels and creature kills are read from the server's own log. Auto sign-in works the same way: the launcher
asks the server for the session key it already holds for this machine and hands it to the client, exactly
as the client's `-U` argument is designed to receive it. Reading what a server writes down is the whole
technique, which is why the launcher can do this without touching anything it does not own.

## Screenshots

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/home.png" alt="Home"></td>
    <td width="50%"><img src="docs/screenshots/profile.png" alt="Profile"></td>
  </tr>
  <tr>
    <td><b>Home.</b> The game's banner, the Play button, and one quiet strip for the server underneath it.</td>
    <td><b>Profile.</b> The account Play signs in as, its playtime and standing, and the score circle its achievements open from.</td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/achievements.png" alt="Achievements"></td>
    <td><img src="docs/screenshots/achievement-toast.png" alt="Achievement toast"></td>
  </tr>
  <tr>
    <td><b>Achievements.</b> Grouped by category and scored, with one milestone card per ladder.</td>
    <td><b>Earned.</b> A badge announced over the game as it happens.</td>
  </tr>
</table>

## What it does

- **Finds your game.** *Detect* looks for a Wizard101 install, including ones on a second drive.
- **Downloads the server.** One button fetches the newest Windows build of Imlight and unpacks it into
  a folder you choose.
- **Sets the server up.** Your admin account, the tutorial, the items a new character starts with, the
  realms and ports are all fields that write themselves into the server's own config file.
- **Press Play.** The server starts, warms up, and Wizard101 opens connected to it. You never start the
  server yourself.
- **Signs you in.** Pick an account on **Profile** and Play skips Wizard101's login screen: the launcher
  hands the client the session key the server already holds for this machine, and issues one when it has
  to. Characters are still chosen on the game's own character screen. Your choice is remembered, and so are
  the account names, so the picker is filled in the moment the launcher opens rather than only after the
  server has run once - for the server those accounts belong to. Point the launcher at a different server,
  or open a portable copy that has never run a server, and you get **No profile** rather than somebody
  else's account.
- **Keeps a profile.** **Profile** is who you are on this server: a picture and a banner, how long you have
  played, the wizards on the account, and achievements earned in the game - all kept on this machine, and
  sent nowhere.
- **Plays where you want.** The caret beside Play joins a server somebody else is running - by address,
  from a scan of your own network, or Wizard101's own server - without starting anything locally.
- **Runs from a folder, not just an install.** Put a `portable.txt` marker beside the launcher's
  executable and it keeps everything - settings, server, cached art, and the game if you copy it in -
  inside its own folder instead of `%LOCALAPPDATA%`, so the whole thing can be moved to another drive or
  carried on a USB stick. One caveat comes with it: a **Steam** copy of Wizard101 still needs the Steam
  client installed wherever it is run. A standalone install from KingsIsle's website does not.
- **Keeps itself tidy.** Closing ViewLight stops the server it started, and asks first while it is
  running. The server is tied to the launcher, so it stops even if the launcher is killed outright.
- **Updates without losing anything.** Updating replaces the server's program files and keeps your
  settings.

Home shows two builds: the **server build** installed in your server folder, and the **client build**
the game itself reports. When they do not match, ViewLight says so - that mismatch is the usual cause
of a login that is refused.

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source
- Your own Wizard101 install. Imlight ships no game files and neither does ViewLight.
- An internet connection for the first install, and to update later

## Getting started

```bash
dotnet build ViewLight.slnx
dotnet run --project src/ViewLight
```

**Or take a build.** Every ViewLight build is published as a **prerelease** under
[Releases](https://github.com/Smealm/ViewLight/releases); download the `ViewLight-win-x64.zip`, unpack
it anywhere and run `ViewLight.exe`. It is self-contained: no .NET install and no admin rights.

**The first run asks for two folders.**

| What | Which folder | Example |
| --- | --- | --- |
| Game folder | The Wizard101 install itself - the one holding `bin\` and `Data\` | `C:\Program Files (x86)\Steam\steamapps\common\Wizard101` |
| Server folder | Where the server should live. It does not have to exist yet, and it can be anywhere | `C:\Imlight` |

The game folder is **not** the `bin` folder inside it. That is the easy mistake to make, and ViewLight
notices and corrects it when it can.

Then press **Play**. On a first run that means a download of around 260 MB, so give it a few minutes: the
game starts by itself once the server is up.

## The tabs

| Tab | What is there |
| --- | --- |
| **Home** | The game's banner and the Play button, plus what Play is doing and the two builds. |
| **Home → Play ▾** | Join a server somebody else runs, scan your network for one, or connect to Wizard101's own server. |
| **Profile** | What this machine knows about the account Play signs in as: picture and banner, playtime, standing, the score circle, and the wizards on it. |
| **Profile → Achievements** | Every badge that account has earned on this server, grouped by category, with a milestone card for each ladder. |
| **Settings** | Your folders, everything about the server, and every value ViewLight writes into its config. |
| **Log** | Everything the launcher and the server have said, with the server's own output as it arrives. |
| **About** | Versions, links, and what is still missing. |

### What Home tells you while you play

Home says nothing at all until something is happening. Press Play and a status appears: *Getting ready…*,
then *Warming up…*, then *Starting Wizard101…*, then *Wizard101 is running* - with the server's own
progress, such as loading world data, named in the line underneath. Below that, two rows say where the
server and the game each are, and they keep saying it once you are in, so you can see at a glance that
the server is still up. If something goes wrong, that same panel is where it is said, rather than only
in the Log.

### What Profile shows

**Profile is who Play signs in as, and it starts with the profile itself**: a picture and a banner you
choose (both are copied into the launcher's own folder, so they survive the originals being moved or
deleted), the time that account has spent on this server, how many times it has signed in, and whether it
still counts as clean. Beside them is the **score circle**: what the account is worth on this server, and
the one button that opens its achievements. Under it is **Switch account** - a button rather than a box
across the card, because a launcher with one account is not making a choice, and the list it opens is the
same one Play signs in from - then the account's wizards.

**The account is drawn in the corner of the window**, on every page rather than only on Home, because who
is signed in is a state the launcher is in rather than a step on the way to pressing Play. Closed, it is
the picture and the name. Clicking it opens it into the banner, the name and the score; clicking that
takes you to the account section; and the pointer leaving it folds it back to the picture and the name
after a moment.

**Each wizard is shown the way the game shows it**: its name, its school, and the level it has reached,
with its gold and finished quests alongside. Level, gold, quests and school are read from your server's
player database, which only answers while the server is running. A name is the one thing the database does
not hold as text - Imlight keeps the table indices the name was built from - so it comes from the server's
log when that names the wizard signing in, and otherwise from the game's own name tables in `Root.wad` when
the launcher can find them. A character neither source can name is shown by its id, which is ugly and true
rather than a name that may be somebody else's.

**Achievements are a screen of their own, grouped, scored, and start shut.** They open from the score
circle on the profile, under the account they belong to, and they are read rather than glanced at: every
badge sits under its category - time on the server, battles, your wizards, and so on - and each heading
carries how many of them are unlocked and what they have scored, so the screen opens as a list of
categories rather than a wall of badges. The long ladders
are **milestone cards rather than rows of badges**: one per school, saying which level that wizard has
reached, what the next milestone is, how far away it is and what it is worth - a rung every five levels up
to your server's own level cap (`Character.MaxLevel` in `Config/Imlight.ini`, 200 by default) - plus a card
for **every ten duels won** and one for **every twenty-fifth creature beaten**, which run as far as your
server's own creature list does. Each rung is worth more the further along it sits. A profile the server has
never logged a command against is **clean**, and its badges count **double** - how much, and why, is on the
card beside the score. Milestones arrive in runs while you are levelling or farming, so **Settings →
ViewLight Settings** has a switch to stop announcing them; the badge is still earned, still counted, and
still on the profile when you turn it off.

**Discovery is what you have been to.** The zones a wizard has entered come from the server's log, and the
world each one belongs to is learned from the server's own account of loading it: the log names a zone
twice over, once by the path it came from - which begins with the world, `WizardCity/WC_Streets/WC_Unicorn` -
and once by the name the game calls it, `WizardZone_UnicornWay`. So **Discover Wizard City** and its like are
earned against your server's real worlds rather than a list written down in the launcher, and the Discovery
group shows each world you have been in with the zones you have found inside it. Nothing is guessed: a zone
the server only numbers - a house, an instance, `WizardZone_00000878` - is recorded as somewhere you were,
and never as a place with a name.

**A brand new launcher has no profile, and says so.** An account belongs to a server, so until a server
has been downloaded *and* its `Config/Imlight.ini` names an admin username and password, there is no
account at all and the screen reads **No profile** rather than showing an empty box. Once there is one, the
screen names it - that admin account is the only one a server has before anybody has signed in - and tells
you what to do with it: **make another under Settings**, or sign in as it and say
`.account create $username $password` in the game. Every account appears on your next launcher start as a
profile of its own.

**Playtime and achievements are local, and only as good as what the launcher saw.** They come from your
own server while ViewLight is running it - the player database's list of who is signed in, and the server's
log - so joining somebody else's server, or somebody joining yours, earns nothing here. A profile that was
played while the launcher was closed simply has no time recorded for that stretch, and the zone and duel
achievements need the server's log level turned up to `DEBUG`, which the Profile tab offers to do for you.

### Playing on another server

Press the caret beside **Play** to choose somewhere else to play. The left half of the button still
means "start my server and the game", exactly as before.

| Choice | What happens |
| --- | --- |
| **Play (start the server here)** | The usual: the Imlight server starts, waits until it is ready, and Wizard101 opens connected to it. |
| **Join a server…** | Points Wizard101 straight at an address you type. Nothing is started here. |
| **Find servers on my network** | Asks every address on your network whether anything is listening on port 12000, and lists what answers. |
| **Wizard101 official — standard account** | Connects to `login.us.wizard101.com:12000` with a standalone Wizard101 account. |
| **Wizard101 official — Steam account** | The same, for a Steam-linked Wizard101 account. |

Three things are worth knowing about joining:

- **You are not signed in automatically.** Automatic sign-in uses a session key the server itself
  issued, so it only works on the server this launcher runs. Joining anywhere else shows Wizard101's own
  login screen, and the account picker on Profile says so.
- **Steam or standalone matters.** A Steam-linked Wizard101 account cannot sign in to Wizard101's own
  server without a launch argument a standalone account must not be given, so the two are separate
  choices rather than a guess. Picking the wrong one is refused at the login screen.
- **A server on another network cannot be found for you.** Nothing publishes a list of Imlight servers,
  so it has to be typed in by address, the way a Minecraft server works. A device that answers a scan is
  only *something listening on 12000* - a good guess, not a promise.

## Your settings

There are two kinds, and they are kept separately.

**ViewLight's own settings** live under **Settings → ViewLight Settings**: the theme (*System*, *Light*
or *Dark*), the page the launcher opens on, whether closing it asks first while the server is running, and
whether milestone achievements are announced as toasts.

**The server's settings** are fields in Settings that write straight into the server's own
`Config\Imlight.ini`. That file is the server's, not the launcher's: ViewLight only changes the values
you can see and leaves the rest of the file - comments included - exactly as it was.

| What | Config key |
| --- | --- |
| Administrator account the server creates for you | `AdminAccountUsername`, `AdminAccountPassword` |
| Skipping the tutorial, and what new characters start with | `TutorialDisabled`, `DefaultItems`, `StartingLevel`, `StartingZone`, `BaseGoldPouch` |
| The realms you offer, and the ports they use | `GameServerIP`, `GameServerPort`, `GameServerCount`, `RealmNames`, `LoginServerPort` |
| The server's own database port | `EmbeddedDatabasePort` |
| Where `Root.wad` is downloaded from | `PatchServerInternalUrl` |
| World data and logging | `SpiralDBAutoFetch`, `LogLevel` |
| Which client revision is accepted | `GameRevision`, `EnforceRevision` |

**Accounts are made in Settings, with the rest of the server's own account management.** With a server
downloaded, **Create an account** writes a username and a password straight into the server's player
database in the shape Imlight writes its own, so it works in the game with no restart - the same thing the
admin account does in the game with `.account create $username $password`, without having to be signed in
to type it.

**Starter items are fussy on purpose.** A character needs at least two items and the server can grant at
most sixteen, so ViewLight refuses to save a list outside that range and explains why. *Apply starter
settings* fills in the usual pair - a Tier 1 wand and the tutorial deck - for a fresh server.

**Two values move themselves.** If the server's database port is still on the default 8080 and something
else already has it, ViewLight moves it to a free port, because the server will not start otherwise. If
the patch server address is blank or points at a local address with nothing answering it, ViewLight
points it at the public one the Revive101 team runs. Anything you have set yourself is left alone.

## Known issues

Rough edges, not faults, and every one of them has a way through.

**Do not create accounts in Settings until the server has run once.** A server that has never been started
has no player database yet, so there is nothing to write an account into and the button has nothing to
do. Press **Play** once - or start the server from Home - and accounts can be made from then on.

**The first server run is slow, and it can crash once.** It downloads its assets, which is a few hundred
megabytes, so the first boot takes minutes rather than seconds. Once the download finishes the server can
still stop by itself for a reason nobody has pinned down yet. **Press Play again** and it starts normally.
After that first successful boot it has not been seen to happen again.

**Loading into a new area can be slow on the public patch server.** Zones are fetched from the patch
server, and the Revive101 one is someone else's machine: it is not on your PC and it does not have
KingsIsle's infrastructure, so a busy day there is felt as a slow zone load. The server is local; the
assets are not.

## When something goes wrong

**Profile says "No profile".** That is the honest answer rather than a fault: an account exists only once
a server here has made one, and a name remembered from another server is not one. Download the server
under **Settings** if it is missing, and check that **Settings → Administrator account** names both a
username and a password - Imlight creates that account the next time the server boots, and it is what the
screen shows until your own accounts turn up in the database. New accounts are made in the game, signed in
as the admin one: `.account create $username $password`.

**Play says Wizard101 was not found.** The game folder is wrong. Check it under **Settings → Game
folder**; the status line underneath says what is and is not there.

**The server starts and then stops by itself.** Read the failure line on Home, then the **Log**. The most
common cause is the patch server: the server downloads `Root.wad` from it, and it cannot start without
that. The Log quotes the server's own last error lines, and ViewLight will have already repaired a blank
or unreachable patch address on its own.

**A login is refused.** Compare the two builds on Home. The server accepts one client revision, and if
your game has been updated past it the client is turned away. Update the server, or update to a client
the server expects.

**Wizard101 will not start from the launcher's folder.** If the game came from Steam, it still needs the
Steam client installed and signed in - copying the files does not remove that. Copy a standalone install
from KingsIsle's website instead, or point the launcher back at the Steam install where Steam put it.
Home says so as soon as you choose a Steam folder.

**Anything else.** The **Log** tab has everything both processes said, in order.

## Where ViewLight keeps its files

Everything it stores about itself - which folders you chose, which build is installed, the art it has
cached, and the profiles with their playtime and achievements - is under `%LOCALAPPDATA%\ViewLight`.
Deleting that folder resets the launcher, profiles included; it does not touch your game or your server.

### Portable mode

Put a file called `portable.txt` next to `ViewLight.exe` (an empty one is enough, and **Settings →
Portable mode** creates it for you) and the launcher keeps everything in its own folder instead: its settings, its server, and the game if you copy it in. Folders *inside* the
launcher's folder are written down relative to it, so the folder can be moved to another drive or
another machine - or carried on a USB stick - and still find everything; a folder somewhere else is
written down as the full path, because that is genuinely where it is.

**Settings → Portable mode** switches it on or off and offers to copy your Wizard101 install into the
launcher's folder, which is what makes the launcher self-contained rather than merely self-locating. Copying
takes a few minutes and several gigabytes, and it is a copy: your original install is left exactly as it
was. Turning portable mode on or off needs a restart, and the launcher says so.

**The Steam caveat.** If that install came from Steam, copying it does not remove its need for Steam. A
Steam copy of Wizard101 loads its own `bin\steam_api.dll` to talk to the Steam client, so with Steam not
installed and signed in the game will not start - wherever the files happen to be. A standalone install
from KingsIsle's website has no such dependency and is the one to copy. ViewLight says this before thecopy starts, on Home whenever a Steam folder is chosen, and again in the report afterwards.



## Fork it

Feel free to fork the launcher and open a PR. This is an example project, written over a day out of a
long-standing love of the game and the novelty of Imlight finally existing - private-server software that
lets Wizard101 be played offline. The server is the hard part and it is not mine; this launcher is a
nicer way to set it up and configure it, plus the things you can only do because you are running your own
backend on the same machine as the client.

## Building and testing

```bash
dotnet build ViewLight.slnx
dotnet test ViewLight.slnx
```

The launcher is a .NET 10 application using Avalonia, so the window is drawn by the launcher itself and
looks the same on any platform that can run it.

## Not done yet

- **Item names in the starter-item picker** come from Imlight's own list rather than from the game.
  Reading the real names out of the game's `Root.wad` is a separate piece of work.
- **Imlight's own first-boot crash** is not worked around by the launcher; pressing Play again is the way
  through it (see [Known issues](#known-issues)).
- **Choosing an older server build** - ViewLight always takes the newest release.
- **Profiles that travel.** They are this machine's, by design. There is no sync and nowhere to sync to.
- **A Linux build.** The server has one, but the game client is a Windows program, so this is a bigger
  question than it looks.

Why things are the way they are, including what has been measured on a real install, is written up in
[docs/design-notes.md](docs/design-notes.md).
