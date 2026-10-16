# ViewLight

**A launcher that makes playing Wizard101 on your own private server as easy as pressing Play.**

Wizard101 can be played on a private server that runs on your own PC, using the Revive101 project
**Imlight**. Setting that up by hand is a chore: find a server build, unpack it, edit its configuration
file, start the server, and only then start the game pointed at it.

ViewLight does the whole thing from one window. Point it at your Wizard101 folder, press **Play**, and it
downloads the server, sets it up, starts it, waits until it is genuinely ready, and opens the game already
connected and signed in. It can run the game's **patch server** on your PC as well - the machine the game
gets its files from - which is what lets you play with no internet connection at all. Close the launcher
and everything it started shuts down with it.

- ViewLight: <https://github.com/Smealm/ViewLight>
- Imlight, the server it runs: <https://github.com/Revive101/Imlight>
- Imlight's own documentation: <https://revive101.github.io/Imlight/imlight/>
- Revive101: <https://revive101.de/>

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

- **Finds your game.** *Detect* looks for a Wizard101 install, including one on a second drive.
- **Gets and sets up the server.** It downloads the newest Imlight build, unpacks it where you choose, and
  fills in the admin account, the tutorial setting, the starter items, the realms and the ports for you.
- **Press Play.** The server starts, warms up, and Wizard101 opens connected to it, with the login screen
  skipped. You never start the server yourself.
- **Keeps a profile.** **Profile** is who you are on your server: a picture and a banner, how long you have
  played, the wizards on the account, and the achievements you have earned. All of it stays on your PC.
- **Plays anywhere.** The caret beside Play joins a server somebody else is running, scans your network for
  one, or connects to Wizard101's own servers.
- **Runs from a folder, not just an install.** A `portable.txt` marker beside `ViewLight.exe` keeps
  everything - settings, server, the game if you copy it in - inside its own folder, so you can carry it on
  a USB stick. See [Portable mode](#portable-mode).
- **Keeps itself tidy.** Closing ViewLight stops everything it started, and asks first while the server is
  running. Both stop even if the launcher is killed outright, so nothing is left holding a port.
- **Picks up where it left off.** Update the server and your settings and data are kept.

## What you need

- **Windows.**
- **Your own copy of Wizard101** - from Steam or from KingsIsle's website. Imlight does not include game
  files, and neither does ViewLight. They are BYOD: bring your own data.
- **An internet connection** for the first setup, and for updates later.

That is the whole list. The build you download carries everything else it needs - including a small copy
of .NET for its own bookkeeping - so there is **nothing to install first and no administrator rights
required**. Unpack the zip anywhere and run `ViewLight.exe`.

## Your first run

**Download the latest build** from [Releases](https://github.com/Smealm/ViewLight/releases), unpack
`ViewLight-win-x64.zip` anywhere, and run `ViewLight.exe`. Every build is published as a *prerelease*, which
is what this launcher is: a proof of concept that gets played with rather than depended on.

Then it asks you **three questions**, in this order:

**1. Two folders.**

| What | Which folder | Example |
| --- | --- | --- |
| Game folder | Your Wizard101 install - the one holding `bin\` and `Data\` | `C:\Program Files (x86)\Steam\steamapps\common\Wizard101` |
| Server folder | Where the server should live. It does not have to exist yet, and it can be anywhere | `C:\Imlight` |

The game folder is **not** the `bin` folder inside it. That is the easy mistake to make, and ViewLight
notices and corrects it when it can.

**2. Imlight itself.** Press **Download Imlight** (around 260 MB) or point ViewLight at a folder you already
have. Until there is an install, this is the only screen you get - nothing else can work without a server.

**3. Whose patch server.** This is the one worth reading twice.

The game is a *thin client*: it does not carry the whole world with it, and it asks a **patch server** for
the files it needs as you play - a new zone, a creature, an item, or just the `Root.wad` the server needs
before it will even start. So a patch server is never optional. The only question is whose it is.

| Answer | What it means |
| --- | --- |
| **Run one on this machine** - the default, and the one worth picking | ViewLight downloads Aurorium (about 9 MB, one file) into its own folder and runs it for you. Every file the game asks for is then served from your own PC, so it works with **no internet at all** - a zone or creature you have never seen included. |
| **Use the public patch server** | Nothing is downloaded and nothing extra runs. The catch is that a file you have not already got cannot be fetched: play a while offline, walk into somewhere new, and the game has nowhere to get it from and stops there. |

The question is asked once, on a machine that does not already have a patch server, and you can change your
answer later under **Settings**. A local patch server does have one drawback - its very first run has to
collect the game's files from Wizard101, thousands of them, around 18 GB - and ViewLight can fill it in from
the copy of the game you already have, which is most of that and the only way the largest files ever arrive
at all. It offers to do that before your first session. See
[the patch server](#the-patch-server-in-plain-words).

## Playing

Press **Play**. In order, ViewLight starts your patch server (if you have one), starts the Imlight server,
waits until it is properly up, and then opens Wizard101.

While it works, Home tells you where it is - *Getting ready…*, *Warming up…*, *Starting Wizard101…*, then
*Wizard101 is running* - and a row under it shows each of the three pieces and what it is doing. Those rows
stay up while you play, so you can see at a glance that the server is still there. If something goes wrong,
that is where it is said.

One button stops both servers. The only exception is a patch server that is still collecting its first batch
of files: stopping it would cancel the download, so ViewLight stops the server, leaves it running, and the
button says *Stop the server* rather than *Stop the servers*.

### The tabs

| Tab | What is there |
| --- | --- |
| **Home** | The game's banner and the Play button, what Play is doing, and the two builds. |
| **Home → Play ▾** | Join a server somebody else runs, scan your network for one, or connect to Wizard101's own. |
| **Profile** | The account Play signs in as: picture, banner, playtime, standing, score, and its wizards. |
| **Profile → Achievements** | Every badge that account has earned, grouped by category, with a milestone card per ladder. |
| **Settings** | Your folders, the server, the patch server, and everything ViewLight writes into either config. |
| **Log** | Everything the launcher, the patch server and the server said, in one place. |
| **About** | Versions, links, and what is still missing. |

**The Log reads like a terminal.** You can drag across it and copy as much as you like at once. The boxes
above it choose whether to show the launcher's lines, the server's, or the patch server's. A line that
repeats thirty times is folded into one line with a count next to it; press it to see every one of them, and
press it again to fold it back.

**Home shows two versions**: the server build that is installed, and the build the game itself reports. When
they do not match, ViewLight says so - that mismatch is the usual reason a login gets refused.

## Your profile and achievements

**Profile** starts with the profile itself: a picture and a banner you choose, the time that account has
spent on your server, how often it has signed in, and whether it still counts as **clean**. Choosing a new
picture or banner shows it straight away.

**Everything on this screen updates as you play** - badges light up as they are earned, the score follows,
a heading's "unlocked 3 of 40" moves with them, a new wizard appears, and a level, a school or a gold total
that changes moves too. You should not have to restart the launcher to see any of it.

**Wizards are shown the way the game shows them**: name, school, level, gold and finished quests, all read
from your own server.

**Achievements** are a screen of their own, opened from the score circle. Every badge sits under its
category, and each heading says how many are unlocked and what they scored. The long ladders are shown as one
**milestone card** each rather than forty rows of badges:

- one per school, a rung every five levels up to your server's level cap;
- one for **every ten duels won**;
- one for **every twenty-five creatures beaten** - a count of kills, so the same creature twice counts twice;
- one for **time played**: half an hour, an hour, then five hours at a time up to a thousand. The last rung
  is a badge of its own, **Wizard of a Thousand Hours**.

A profile that has never had a command run against it is **clean**, and its badges are worth
**double**. That is not a prize handed out: it happened on your own server, and ViewLight watched it happen.

**Discovery is what you have actually been to.** The zones a wizard has entered come from your server, and
the world each one belongs to is learned from the server's own record of loading it - so *Discover Wizard
City* means the real Wizard City on your server, not a list hard-coded into the launcher.

Milestones can arrive in runs while you are levelling. **Settings → ViewLight Settings** has a switch to stop
announcing them; the badge is still earned and still counted.

**Where this all comes from.** Playtime, zones, duels and creature kills are read from your own server while
ViewLight is running it. Join somebody else's server and nothing is earned here. Play with the launcher
closed and that time is not recorded. Creature counts need the server's logging turned up to `DEBUG`, which
the Profile tab offers to do for you.

## Your settings

There are two kinds, and they are kept separately.

**ViewLight's own settings**, under **Settings → ViewLight Settings**: the theme (*System*, *Light* or
*Dark*), the page it opens on, whether closing it asks first while the server is running, and whether
milestone achievements are announced as toasts.

**The server's settings** are the fields in Settings that write into the server's own configuration file.
ViewLight only changes the values you can see and leaves the rest of that file - comments included - exactly
as it was. They cover the admin account, skipping the tutorial and what new characters start with, your
realms and ports, the logging level, and which client version the server accepts.

**Accounts are made by the server, inside the game.** ViewLight does not write into your server's player
data: the server owns that, and its own way of making an account is its own command. Sign in as the admin
account in the game, type `.account create $username $password` in the chat, close the client, and the new
account shows up on **Profile** as a profile of its own. The two steps are also written out under
**Settings → Making another account**.

One thing worth knowing: **a brand new server has no accounts at all** until it has run once, because the
admin account is created on its first start. Until then **Profile** says **No profile** - press **Play**
once and it appears.

**Two values fix themselves.** If the server's database port is taken by something else, ViewLight moves it,
because otherwise the server will not start. If the patch server address is blank or points at something on
your machine that is not answering, ViewLight points it at the public one. Anything you set yourself is left
alone.

**Skipping the tutorial is a shortcut with a catch.** New characters go straight to the starting zone, but
the things the *tutorial* would have given them - the school's first spell, and the starter items being
equipped - do not happen. The setting says so where you turn it on. The fix is in the game: walk to
Ravenwood, learn your spell from the trainer, and equip the starter items out of your inventory.

## The patch server, in plain words

**Settings → Patch server** asks the one question from your first run again: whose patch server. Everything
else follows from the answer.

- **Run one on this machine** gives it a folder, a port, and the switch **Start it with the server, and stop
  it with the server**. While that is on, Play starts it first, stops it with everything else, and points the
  server at it, so the game's files come from your PC.
- **Use a remote patch server** is an address and nothing else - the public one by default, or one of your
  own. ViewLight never rewrites an address you typed, and never starts or stops somebody else's server.

**Its first run is the long one.** Aurorium starts answering within seconds, but it then has to download the
game's current version from Wizard101 - thousands of files, around 18 GB - and until a file arrives it
simply has nothing to hand over. Three things are worth knowing:

- **Leave it running.** It only makes progress while it is alive, so ViewLight deliberately does **not** stop
  a patch server that has not finished. It stays until you close the launcher or stop it under Settings.
- **ViewLight tells you where it stands.** Before the server starts, it asks the patch server for the first
  file the game needs and writes a line in the Log when the answer is "not yet", so a patch server that is
  merely waiting does not look like one that is broken.
- **Your own game can hand it the files instead.** The version a patch server collects is the same version
  installed on your PC, so ViewLight can copy those files straight in and skip most of the wait - and the
  largest files, which the download cannot finish at all, arrive only that way. **Copy the game's files in**
  is under the patch server card, and Play offers it once per version just before the server starts. Nothing
  is overwritten and nothing is invented: only files of the right name and the right size go in, each one is
  written aside and moved into place, and running it again copies only what is still missing.

**It is also the one thing here that does something the patch server did not ask for.** A patch server
normally collects a revision itself, and this hands it the content without it ever having downloaded that
revision - so it may not be expecting those files to be there yet. When the feature was written, the files
the game client had cached and the patch server's own copy were compared and looked identical, which is why
it should be safe; but content delivery is not something this launcher knows deeply, so treat "should" as
the strong word it is. If the game misbehaves around a file, or the patch server reports something
unexpected, the safe retreat is to let it download that revision itself rather than trust the copy.

**If a login is refused, these two are usually why.** Your game gets updated by KingsIsle, and your server
accepts one specific version. **Make the server accept the patch server's revision** - one switch in the
patch server card - tells the server to accept whatever version your patch server actually holds, which is
the version your game was just updated to. It is off by default, and turning it off puts the old value back.

## Playing on another server

Press the caret beside **Play** to go somewhere else. The main button still means "start my server and the
game".

| Choice | What happens |
| --- | --- |
| **Play** | The usual: patch server if you have one, then your server, then the game. |
| **Join a server…** | Points the game straight at an address you type. Nothing is started here. |
| **Find servers on my network** | Asks every address on your network whether anything is listening, and lists what answers. |
| **Wizard101 official** | Connects to Wizard101's own servers - one entry for a standalone account, one for a Steam-linked account. |

Three things to know:

- **You are not signed in automatically.** Skipping the login screen only works on the server ViewLight
  itself runs, because that is the server that gave your machine a sign-in key. Anywhere else, you get
  Wizard101's own login screen.
- **Steam or standalone matters** for Wizard101's official servers. Picking the wrong one is refused at the
  login screen.
- **A server on somebody else's network has to be typed in by address.** Nothing publishes a list of Imlight
  servers, and a device that answers a scan is only *something listening* - a good guess, not a promise.

## Portable mode

Put a file called `portable.txt` next to `ViewLight.exe` - an empty one is enough, and **Settings →
Portable mode** creates it for you - and ViewLight keeps everything inside its own folder instead of in your
user folder. Move the folder to another drive or another machine and it still finds everything.

**Settings → Portable mode** also offers to copy your Wizard101 install into the launcher's folder, which is
what makes it properly self-contained rather than just self-locating. That is a copy, so your original
install is left exactly as it was. Switching portable mode on or off needs a restart, and ViewLight says so.

**A copy goes stale.** Portable mode plays the game out of its own folder, so if Steam or KingsIsle updates
the game on your machine, your copy is now an older version and nothing in the folder would tell you. Every
start compares the copy against the install it came from, and Home offers to close the gap - either by
writing only the files that changed, or by copying the whole thing in fresh. They end up the same; they
differ only in what they spend. Update runs when you ask for it, never on its own.

**If your copy came from Steam, read this.** It plays without the Steam client running, today, and it comes
with the game's own cached patch files - a head start nothing else offers. But it also carries a Steam
library file, and what that file does is KingsIsle's decision, not this launcher's: a future build of the
game could start requiring Steam to be installed and signed in, and this copy would stop working the day it
does. A standalone install from KingsIsle's website has no such file, so it is the copy that stands on its
own for certain.

## Cloud backup

Your private server runs on your own PC, which means it has the one kind of data nothing can re-download:
your accounts, your wizards, your profiles, your playtime and your badges. Lose those and no amount of
reinstalling brings them back.

**Settings → Cloud backup** keeps a copy of them in your own Google Drive, inside a folder that belongs to
ViewLight. You cannot see that folder in Drive, and it cannot reach a single other file in there.

**What is saved:** your accounts, wizards and everything they own, plus your profiles, playtime and badges.

**What is not:** the game's files (around 18 GB), the game client, the server and patch server builds, and
cached downloads and art. All of those can be fetched again. Your server's own configuration file is left
out on purpose too, because it is full of folder paths that would be wrong on a different machine.

**What it does, on its own:**

| Your copy | The cloud's copy | What happens |
| --- | --- | --- |
| unchanged | unchanged | Nothing, and nothing is said. |
| changed | unchanged | Your copy is uploaded. |
| unchanged | changed | The cloud's copy is restored. |
| changed | changed | ViewLight stops and asks: **Back up now**, or **Restore from the cloud**. |

The ordinary case is guessed right and stays quiet, like Steam Cloud. Only a real conflict - both sides
changed since they last agreed - asks, because there is no way to tell which one you meant. Your data is
never copied while something is using it, so a sync runs when the launcher has a moment to itself: when it
opens, just before the server starts, and after the server stops. While it runs, editing is paused for a
moment and the corner of the window says so.

**A new machine takes the cloud's copy, and does not ask.** The first time you sign in on a fresh install,
what is in the cloud is the copy you put there on purpose; what is on the new machine is an empty database
it made a moment ago. So the cloud wins, the backup comes down first, and only then does this machine start
uploading its own changes. That is the difference between a new install picking up where you left off and a
new install wiping the only backup you had.

**Setting it up.** Sign in with Google. It takes about two minutes:

1. Open the [Google Cloud credentials page](https://console.cloud.google.com/apis/credentials), create a
   project if you do not have one, and choose **Create credentials → OAuth client ID**.
2. Pick application type **Desktop app** and copy the **client ID** it shows you. Desktop app is the one
   type that needs no client secret. A **Web application** client works too, but Google will not refresh it
   without the **client secret**, so paste that in as well if you pick that type.
3. Paste it into **Settings → Cloud backup**, tick **Keep a backup in my cloud drive**, and press **Sign in
   to Google Drive**. Spaces and line breaks around what you paste are stripped for you.

Either way the browser opens Google's own page and ViewLight never sees your password. A built-in sign-in
that needs no setup at all is written and waiting, but it is greyed out until ViewLight has an app of its own
registered, which it does not yet.

## If something goes wrong

**Profile says "No profile".** That is the honest answer rather than a fault: an account only exists once
your server has made one. A brand new server has none until it has run once, so press **Play** first. Then
make your own accounts in the game with `.account create $username $password`, signed in as the admin
account.

**The first screen says Imlight is missing.** Point it at a folder you already have, or press **Download
Imlight**. That screen is deliberate: nothing else in the launcher works without a server.

**Play says Wizard101 was not found.** Your game folder is wrong. Check it under **Settings → Game folder**;
the line underneath says what is and is not there.

**The server starts and then stops by itself.** Read the failure line on Home, then the **Log**. The most
common cause is the patch server - the server needs a file from it before it can boot - and ViewLight will
have already repaired an address that was blank or pointing at nothing.

**A download of the first file fails with "404" from your own machine.** Your patch server has not collected
the files yet. Leave it running and try again later, or hand it the files your game already has - see
[the patch server](#the-patch-server-in-plain-words).

**You run a patch server on this machine and the server will not start.** Turn on **Make the server accept the
patch server's revision** in the patch server card. Imlight and the patch server are separate projects on
their own schedules, and Imlight can be a few updates behind the patch server - which is usually fine, but it
means the server can be asked for a revision it was not built expecting and refuse to boot instead of failing
in a way that reads clearly.

That switch is **off by default for a reason: it should not be needed.** Run both from their latest builds and
they are the same version, which is the state they are meant to be in, and leaving the switch alone is the way
to keep them there. Turn it on when they have drifted apart, and turn it **back off once either one has been
updated** - it covers a gap rather than being a setting to live with. If you would rather not take the chance
at all, the other way through is to wait for whichever one is behind to catch up, because an out-of-date patch
server - or an up-to-date one serving an out-of-date server - is exactly the combination the switch is willing
to accept the consequences of.

**Wizard101 cannot connect to the server.** This is the one fault the launcher cannot see or fix, because
nothing is actually broken: the patch server, the Imlight server and the game are all running, all on this
machine, and all talking to each other over a local IP address - and something in between is stopping them.
Two things cause it almost every time.

- **A VPN.** The game reads the server's address from your own machine's network, and a VPN replaces that
  network with its own. Disconnect it and try again.
- **A firewall.** The rule that has to exist is **Imlight's Director allowed to talk to Wizard101**, and the
  same for your patch server. Windows' own firewall asks you the first time and offers to make the rule; say
  **Allow**. Third-party firewalls count too - this includes software like **PortMaster** - and a firewall
  that is installed is still filtering when its window is closed: if it runs a background service, that
  service goes on blocking traffic with nothing in the tray to remind you. So launch it, then either shut it
  down properly (check its process is really gone) or exempt the Director, the patch server and Wizard101 so
  they can reach each other.

**The rule of thumb:** a patch server, an Imlight server and a game client all on one machine are expected
to be outside any VPN and outside any firewall restriction, and to communicate freely over a local address.
Anything in the way of that shows up as a connection error rather than as a fault, so check what is
intercepting the traffic before you suspect what is running it.

**A login is refused.** Compare the two versions on Home. Your game has been updated past the version your
server accepts; turn on **Make the server accept the patch server's revision** in the patch server card -
the same switch as above, and the same rule: turn it back off once one of the two has been updated.

**An update says a file in the server folder is in use.** Something is running the server's files. Stop the
server under **Settings → Stop the server** and try again. The message names the file, and nothing is
half-written by the attempt.

**The first server run is slow, and it can crash once.** It downloads its own assets - a few hundred
megabytes - so the first boot takes minutes. After that it can stop by itself once, for a reason nobody has
pinned down: press **Play** again and it starts normally. It has not been seen to happen a second time.

**A new area loads slowly.** That is the public patch server rather than your PC: your server is local, but
the assets come over the internet from a community machine. Running your own patch server is the fix.

**Something in the game is not right.** The launcher is not the game's author and modifies neither the game
nor the server - see [What ViewLight never does](#what-viewlight-never-does) - so where the fault sits decides
who can actually fix it:

- **A bug in Wizard101 itself** - a spell doing the wrong thing, a texture, an animation, a crash in the
  client - is **KingsIsle's**. They wrote the game, and a private server does not change that. This launcher
  deliberately leaves the game untouched, so it has no way to patch a game bug out.
- **Something the server gets wrong** - a quest that will not advance, a creature behaving oddly, a reward
  that never arrives - is **Imlight's developers'**. The server is where the world's rules live; ViewLight
  writes one configuration file into it and otherwise leaves it alone. That belongs on Imlight's own project
  page rather than here.
- **Files that will not come down, or arrive wrong** is the one of the three that can genuinely be this
  launcher's, and it is a known weak point of its own - the file hand-off described under
  [the patch server](#the-patch-server-in-plain-words).

None of that means a bug is not worth reporting - it means it is worth reporting in the right place, where
somebody can do something about it.

**Anything else.** The **Log** tab has everything all three of them said, in order, and you can copy any of
it straight out.

## Where ViewLight keeps its files

Everything ViewLight stores about itself - your folders, the cached art and downloads, your profiles with
their playtime and badges - is under `%LOCALAPPDATA%\ViewLight`. Deleting that folder resets the launcher,
profiles included, and leaves your game, your server and your patch server alone.

Your server keeps its own files in the server folder you chose. The only thing ViewLight writes in there is
the configuration file Imlight expects to be edited from outside.

In portable mode, all of the launcher's own files live in its folder instead, so a copied folder arrives
with its profiles in it.

**Upgrading from an older build:** a `profiles.json` left by a previous version is moved across the first
time the launcher starts. Nothing is deleted - the old file is kept beside it as `profiles.json.imported`.

## Building it yourself

```bash
dotnet build ViewLight.slnx
dotnet test ViewLight.slnx
dotnet run --project src/ViewLight
```

Building from source needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). A build from source
does not carry its own runtime the way a release does, so run it on a machine that already has .NET installed,
or use a release.

## Fork it

Feel free to fork the launcher and open a pull request. This is an example project, written out of a
long-standing love of the game and the novelty of Imlight finally existing - private-server software that
lets Wizard101 be played offline. The server is the hard part and it is not mine; this launcher is a nicer
way to set it up and configure it, plus the things you can only do because your own backend is running on the
same machine as the client.

## Known limits

- **Item names in the starter-item picker** come from Imlight's own list rather than from the game. Reading
  the real names out of the game's data is separate work.
- **Imlight's own first-boot crash** is not worked around; pressing Play again is the way through it.
- **The file hand-off to a local patch server is the weakest thing in the launcher.** It spares the patch
  server a download it would otherwise do itself and gives it content for a revision it has not collected,
  which it may not be written to expect. It was checked by comparing the game client's cached files against
  the patch server's own and finding them identical, so it is expected to be safe - but expected is not
  proven, and content delivery is not something this launcher knows deeply. A patch server left to collect
  its own revision is the path its authors designed, and the one to fall back on if files misbehave.
- **A local patch server still has to collect its first version from Wizard101.** That is where the game's
  files live. Once they are on your machine, everything is served locally.
- **ViewLight always takes the newest server build**; choosing an older one is not offered.
- **The revision switch only reads your own patch server.** With a remote one chosen it has nothing to read
  and does nothing.
- **A Linux build.** The server has one, but the game client is a Windows program, so this is a bigger
  question than it looks.

Why things are the way they are - what was measured, what was tried, what was deliberately left out - is
written up in [docs/design-notes.md](docs/design-notes.md).

## What ViewLight never does

**It modifies nothing.** No patch, no hook, no injection, no memory editing, and your server is not modified
either. Accounts, wizards, sign-ins, zones, duels and creature kills are all *read* from your own server's
data and its log, and automatic sign-in uses the sign-in key the server already holds for this machine -
which is exactly what the game client's own `-U` argument is designed to receive. Reading what a server
writes down is the whole technique, so there is nothing here that could get an account banned.
