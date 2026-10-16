# Changelog

Every published build of ViewLight, newest first, in plain language: what changed for the person using it.
The reason *why* each thing is the way it is lives in [docs/design-notes.md](docs/design-notes.md), and the
downloads are under [Releases](https://github.com/Smealm/ViewLight/releases).

The version number in the window, on the **About** tab, matches the tag and the release.

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
