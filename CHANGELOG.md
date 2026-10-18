# Changelog

Every published build of ViewLight, newest first, in plain language: what changed for the person using it.
The reason *why* each thing is the way it is lives in [docs/design-notes.md](docs/design-notes.md), and the
downloads are under [Releases](https://github.com/Smealm/ViewLight/releases).

The version number in the window, on the **About** tab, matches the tag and the release.

---

## v2.0.0 - 2026-10-17

### Added

- **Signing in is a screen of accounts instead of a menu with one row in it.** **Log out** on the profile
  puts the profile down and covers the launcher with the sign-in screen: this machine's profiles as a row of
  cards to scroll across, each with its picture, name, achievement score and the first few wizards on the
  account - their school's element logo and level. One click signs in as that account and puts the launcher
  back. The row slides in, a card lifts under the pointer, and the arrows beside it move a page at a time.
  The accounts on it are the ones your server actually holds an account for.
- **A profile picture or banner can come from somewhere other than your computer.** Tick **Edit profile** in
  the corner of the banner and click the one you want to change. *From my computer…* opens the usual file
  picker; *Browse gamerpics…* opens a backup of around 32,500 Xbox 360 gamerpics, square, which is the shape
  a profile picture wants; and *Browse wallpapers…* opens Wallhaven's weekly toplist, which is wide enough to
  be a banner and changes every week. Both browsers can be searched and are shown a page of pictures at a
  time, and what is looked at is kept on your PC, so paging back through a grid costs nothing the second
  time. Nothing is downloaded until you pick something, and whatever you settle on is kept in your profile,
  so it is backed up with the rest.
- **A profile's wizards are a shelf of trophies rather than a long rectangle.** Under the account, each
  wizard is a cup wearing the game's own element logo for its school, with its name, level and school
  beneath it; hover one for its gold, spells learned, quests finished and when it was last seen. Below them,
  **History** lists what the account has earned lately, newest first - the achievements page's own badges
  read as a list - so the profile and the achievements screen cannot disagree about when something happened.
- **Settings is pages instead of one long scroll.** *Server*, *Client*, *ViewLight* and *Data*, with the
  server's own settings split into *ImLight* and *Aurorium* and the client's into *Game folder* and
  *Characters*. Every page is one click from every other, and the group you are looking for says which page
  it is on rather than being found by scrolling past the rest. **Default page**, on the ViewLight page,
  chooses where the launcher opens - Home unless you say otherwise.
- **A way to start over completely.** **Settings → Data → Wipe everything…** deletes both of the launcher's
  databases - the accounts, wizards, profiles, playtime, badges and pictures - and, if you tick it, the copy
  of them in your own Google Drive as well. It asks twice, and both steps are deliberately hard to do by
  accident: the first screen lists exactly what will go and holds its **Next** button for fifteen seconds
  while you read it, and the second asks you to type a sentence saying what is about to happen. The cloud
  copy is deleted *before* the new database is made, because a fresh database next to an old backup is a
  machine that takes the backup on its next sync - which is how the old data would come back. Your Google
  sign-in survives the reset so that delete-and-back-up can happen at all, and the launcher makes itself a
  new database and backs that up. Your server, patch server, game and settings are not touched; your server
  makes its own new, empty database the next time it starts, and the screen says so.
- **You can choose which Imlight builds ViewLight installs.** **Settings → Server → Build channel** (and the
  same choice on the first screen) offers **Stable release**, **Prerelease** and **Bleeding edge**. Stable
  installs only builds Imlight publishes as a full release; there are none yet, and choosing it says so
  instead of quietly installing a prerelease. Prerelease is the newest published build and remains the
  default. **Bleeding edge** is different in kind: it clones Imlight's current source with git, builds it on
  your machine with the .NET 10 SDK, and installs the result over your server - keeping your settings and
  data, exactly as a published update does. Its later updates are a `git fetch` and a merge followed by a
  rebuild, and it never merges over uncommitted edits. It is not a tested build; the screen says so, and says
  to tell Imlight's developers you built it from their source before reporting a bug. The two files Imlight
  does not publish - the WizWalker type dump and the game's message definitions - are no longer left for you
  to find: ViewLight installs [wiztype](https://github.com/wizspoil/wiztype) into a private Python
  environment, starts Wizard101 once to read its types, extracts the message XML from your `Root.wad`, and
  closes the game again if it was the one that opened it. The dump is kept under `<ViewLight data>\source`
  and reused until your client's revision changes; a dump you place there yourself is used as it is, with no
  Python needed.

### Changed

- **The launcher explains itself only when asked.** Most controls have a sentence under them saying what
  they are for; that sentence is a comment on the screen rather than part of it, so it is hidden by default.
  **Settings → ViewLight → Verbose mode** shows all of it at once - what a setting changes, which file it is
  written to and why it is there - and hides it again. A launcher should be used rather than read.
- **The launcher's own databases are read on this machine's own address.** They are read at `127.0.0.1` with
  the loopback client and the system proxy switched off, so no part of reading them can reach the network or
  anything that has to be looked up - which matters on a machine with no connection, where the profiles used
  to fail to load at all. If a database still cannot be read, the **Log** now carries what the database
  itself printed before it stopped: a port something else had taken, a data directory it could not open, or
  a runtime it was not given.
- **The window is on screen in about half the time it used to be.** Roughly 5.2 seconds instead of 10-11,
  measured on the same machine and release build: the saved settings are filled in with one read of the
  server's config instead of several, a folder with no install in it no longer waits as though a file were
  being held, and Home is built after the shell is already up rather than before it.
- **The profiles database notice is now a notification in the top-left corner instead of a dialogue over the
  whole window.** It shares one notification area with the Google Drive sync notice, so the two stack rather
  than cover each other - and both are visible over the sign-in screen, which is where the profiles database
  is usually coming up. Neither takes the window: nothing is blocked while either one is on screen.
- **Notifications fold themselves into a tray in the corner, and one arrow opens them again.** A notice stays
  on screen for a few seconds and then becomes a small card saying how many are waiting, with a down arrow to
  read them and an up arrow to put them away again. Opening it by hand keeps it open - the delay only folds
  something nobody has touched - and when there is nothing left to say, the tray goes too.

### Fixed

- **A build from source is now actually written into the server folder, and the launcher checks that it
  was.** The build's three output folders - Director's runnable tree and the CoreLib and Common folders
  beside it - are what gets deployed; a build without the executable or any of the three assemblies is
  refused before anything is written. The installed `Imlight.Director.dll`, `Imlight.CoreLib.dll` and
  `Imlight.Common.dll` are then compared with the built ones and a difference is reported as a failed
  install, instead of saying the build is installed while the folder still holds the previous server.
  Only files that differ are written, so rebuilding the same source a second time copies nothing. A
  server that is running no longer blocks the build either: it is stopped at the moment the files are
  written, and the card updates to the new build immediately afterwards. That now includes a server the
  launcher did not start itself - one left over from a previous session, or started from a shortcut or a
  terminal - which used to hold `Imlight.Director.exe` open and leave the server folder untouched while
  the build reported success.
- **A source build no longer needs a staging copy of its output.** The deploy reads the folders `dotnet
  build` put the files in and writes them straight into the server folder, so a build no longer spends
  time and disk copying itself to a mock server folder first.
- **Wizard101 is no longer started against a server whose database is not listening yet.** Imlight opens
  port 12000 about half a minute before its embedded RavenDB is ready, and the launcher used to take the
  open port as "server ready". Readiness now waits for the server's own `Imlight may now be connected to.`
  line whenever its log exists; the port is only a fallback after two minutes. The player-database read
  also waits briefly, so arriving a moment early no longer switches automatic sign-in off.

### Where this goes from here

ViewLight is, as of this release, the launcher it set out to be: it finds the game, installs and updates the
server, runs the patch server, starts a session, keeps a profile and its achievements, and backs all of it up.
What is left is not more features but another pass over the whole thing - most of all a hand rewrite that
keeps the concept and drops the accreted layers, an achievements system that is finished and worth chasing
rather than a ladder with gaps in it, some tidying of the interface, and the ordinary optimisations that come
from somebody reading the whole program at once. It is an idea and a reference as much as a program: fork it,
change it, use it for inspiration and build your own.

---

## v1.4.0 - 2026-10-16

### Fixed

- **Playtime and sign-ins are now your sessions, not the server's database.** Opening ViewLight, or looking at
  a profile, counted as signing in and started the playtime clock - so starting the launcher, playing once and
  closing it again came to three sign-ins, and idling on the launcher counted as playtime. **Play is now the
  sign-in**: one per session, on the profile it signs you in as, with playtime counted only while ViewLight's
  own server and its own game are both running. Signing back in after a disconnection inside one session is
  not a second sign-in.
- **Filling the patch server in from your game no longer refuses a game that is the right version.** The copy
  was aimed at the version in the server's config, which with the two kept in step is not the version the
  server is going to ask for - so it refused a client that *is* the version the patch server holds.
- **A warning the server repeats over and over now folds into one line.** It only folded when the whole line
  was identical, and the server puts its own timestamp inside every line - so the one warning that arrives a
  thousand times, each with a later millisecond in front of it, never folded at all.
- **The launcher's own lines about the patch server are now filed as the patch server's** - including the one
  that says which file a fetch is on, which the patch server's box did not show. A new **Launcher's own
  notes** box takes them out again, for pasting one program's log to the people who wrote it.

### Changed

- **Apply starter settings is now Apply recommended settings, and it leaves the tutorial alone.** It narrowed
  the starter kit *and* turned the tutorial off; the game has its own skip-tutorial button on the
  character-creation screen, so a server-wide default was never needed. Skipping the tutorial is the tick box
  directly below the button, and the Log says what the preset left as it was.

---

## v1.3.0 - 2026-10-16

### Added

- **The patch server's download is now something you can watch properly.** The line under its row on Home —
  and in the card in Settings — now names the file it is downloading at that moment and how big that file is
  (`Equipment-WorldData.wad (12.3 MB of 281.4 MB)`), how many files are arriving at once, which version it is
  fetching, and whether that version is the one your server asks it for. The Log keeps the same line as it
  moves from file to file.
- **A way past the wait: **Play anyway** on Home.** If the patch server has not got the file the server's
  boot needs yet, ViewLight waits for it rather than starting a server that will 404 — and that wait can be
  a long time on a first run. One button starts the session immediately, and the Log says what that means.
- **A server that cannot load its own files now says so instead of sitting at "warming up" forever.** The
  server asks the patch server for a version, and if the patch server does not have it the answer is a 404
  that the server writes in its own log and then retries until somebody notices. ViewLight reads it and puts
  it on Home, naming the version the server asked for and the version the patch server actually has.

### Changed

- **Your patch server and your server are now kept on the same game version by default.** Settings →
  Patch server → **Sync the patch server's revision with the server's accepted client revision** is on, and
  it means the server always asks for files the patch server actually has — and accepts the client that has
  just been updated. It is applied every time a patch server starts, and switching it off puts the version
  your config had back.
- **Play waits for the patch server's files before starting the server** — for the one file the server's own
  boot needs, not for a whole version. A first run used to press on regardless and leave the server unable
  to fetch its own files; it now says what it is waiting for, and starting anyway is one button away.
- **Automatic cleanup keeps the newest version and removes the older ones.** It used to also keep whichever
  version your game happened to be, which on a machine that has not been patched up to the current version
  was a second 18 GB held for nothing. Nothing your server asks for by name, and nothing being downloaded,
  can be removed.

### Removed

- **The warning dialog in front of the revision switch** is gone. It guarded the arrangement that is now
  the default, and the switch says what it does in plain words instead.

---

## v1.2.0 - 2026-10-15

### Added

- **A cloud backup of the things nothing can give back.** Your accounts, wizards, profiles, playtime and
  badges live in two folders that no amount of reinstalling can recreate. **Settings → Cloud backup** signs
  in to your own Google Drive and keeps a copy of them in a private folder nobody else can see. It backs up
  and restores by itself, and stays quiet unless both sides genuinely changed and it has to ask. Off until
  you turn it on, and it never touches your game files, your client or your builds — those can all be
  downloaded again.
- **Your first run now asks which patch server to use, instead of quietly picking one.** The game gets its
  files from a patch server while you play, and it cannot start without one, so a session always has one —
  the only question is whose. ViewLight recommends running one on your own PC, because that is the answer
  that lets you play with no internet at all; the public one is there too, with what it costs you spelled
  out. Asked once, changeable later in Settings.
- **Time played is a ladder now, not five fixed badges.** Half an hour, an hour, then five hours at a time
  up to a thousand, with every rung worth more than the last. The final rung is a badge of its own:
  **Wizard of a Thousand Hours**.
- **A portable launcher can bring its own copy of the game up to date.** Portable mode plays the game out of
  its own folder, so a Steam or KingsIsle update leaves that copy behind. Every start compares the two, and
  Home offers to close the gap — either by writing only the files that differ, or by copying the game in
  fresh.
- **Creature kills are counted**, from the duels your own server logs. That is what the creature milestones
  were waiting for; before this, beating a creature moved nothing.
- **Everything on the achievements screen updates as you play.** The badges and the score already did; the
  counts on each category, its progress bar and its tick mark now follow them instead of freezing at
  whatever they were when you opened the screen.
- **A wizard's school is detected.** It used to read "School not read" on a real server, which also stopped
  that school's level ladder from ever moving.
- **The wizards on your profile update live.** A wizard made while the launcher is open now appears, and a
  level, a school, a gold total or a finished quest moves as it happens rather than after a restart.

### Changed

- **Nothing has to be installed any more.** The download carries its own copy of the .NET runtime, so a
  machine that has never seen .NET can unpack the zip and run it. The previous build asked you to install
  that runtime first; now there is no step at all.
- **The launcher keeps its own files well away from the server's.** It now makes its own copy of the small
  database engine it needs instead of borrowing the server's, which sets your notes free from Imlight's
  folder — and means updating the server can no longer fail because the launcher happened to have the
  server's files open at the time. It costs about 180 MB in the launcher's own folder, inside the same
  install folder in portable mode.
- **A patch server that is still collecting its first files is no longer stopped** when you stop the server.
  Stopping it was cancelling an 18 GB download; it now stays until you close the launcher or stop it
  yourself.
- **The Steam note reads like what it is — a warning about the future, not about today.** A Steam copy of
  the game plays without the Steam client running, and it comes with cached patch files as a head start.
  What it also carries is a Steam library file, and whether that becomes required is KingsIsle's decision,
  not this launcher's. ViewLight says exactly that instead of telling you a game you are playing is broken.

### Fixed

- **A picture or banner you have just picked shows up straight away** rather than after a restart.
- **A profile's picture, banner and numbers are there as soon as the launcher opens**, instead of taking
  seconds to appear or waiting for another change to arrive.
- **An installed server is no longer reported as missing** when its config file is briefly busy because
  something else is reading or writing it.
- **A copy of the game that has just finished is noticed.** Home stops offering to copy it in, and
  Settings stops saying the game's files were not found, without pressing **Detect** by hand.
- **Updating the server no longer fails with "a file is in use"** because the launcher itself was the thing
  holding the server's files.
- **Signing in to Google is sturdier.** It survives a restart, it can be cancelled if you close the browser
  window it opens, and a stray space in a pasted client id — the usual cause of a 401 that looks like a
  sign-in that would not stick — is now stripped.
- **A first cloud sync can no longer write over the backup in the cloud.** On a fresh install the cloud's
  copy comes down first and the machine's own changes are uploaded afterwards, never the other way round.

### Removed

- **The five fixed playtime badges** are gone, replaced by the playtime ladder above. The hours themselves are
  not lost — they are what the ladder is measured in, so every rung you had already earned arrives with it.

---

## v1.1.0 - 2026-10-14

### Added

- **The patch server is part of the stack, and Settings asks one question about it.** Imlight loads the
  game's files from a patch server and cannot boot without one, so ViewLight installs Aurorium beside the
  server, writes its configuration, starts it first and points the server at it.
- **It can fill that patch server in from your own game.** An installed copy of the game *is* the version a
  patch server needs, so ViewLight can copy the files in rather than waiting for them — and the largest
  files, which the download cannot finish at all, arrive only that way.
- **The server can be told to accept the patch server's revision.** An update to the game can leave the
  server refusing your client; one switch makes the server accept whatever version your patch server holds.
- **The launcher's own notes moved into a database of their own**, so profiles, playtime and badges are no
  longer a single file rewritten whole on every change. An old `profiles.json` is imported once.
- **A first screen that stands down and says why** when there is no Imlight install, or no .NET runtime for
  the database the launcher's notes need — with the link, the steps, and a button that checks again.
- **The Log reads like a terminal**: one run of monospace text that is never wrapped, draggable and copyable
  as a whole, with a box for each of the launcher, the server and the patch server, and repeated lines
  folded into one with a count.

### Changed

- **Accounts are made by the server, in the game.** `.account create $username $password`, typed by the
  admin account inside the client, is the only way — the launcher reads account data, it does not write it.
- **Skipping the tutorial says what it costs you.** The school's first spell, the starter items being
  equipped and the tutorial's own items are all things the tutorial gives you, and the setting now says so
  before you turn it on.

### Removed

- **The Create an account button in Settings**, because making accounts is the server's job rather than the
  launcher's.

---

## v1.0.0 - 2026-10-14

The first build.

### Added

- Finds your Wizard101 install, including one on a second drive.
- Downloads the newest Imlight server build, unpacks it where you choose, and writes your admin account,
  tutorial setting, starter items, realms and ports into its configuration for you.
- **Press Play** starts the server, waits until it is genuinely ready, and opens Wizard101 connected to it
  and already signed in.
- A profile with a picture, a banner, playtime, sign-ins, standing, the account's wizards, and achievements
  earned while you play — all kept on your own machine and sent nowhere.
- Achievements that are scored, with long ladders shown as one milestone card each, and a profile with no
  commands ever run against it marked **clean** and worth double.
- Joining another server by address, scanning your network for one, or connecting to Wizard101's own.
- Portable mode: a `portable.txt` marker beside the launcher keeps everything in its own folder.
