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
    <td width="50%">
      <img src="docs/screenshots/home.png" alt="Home" width="100%">
    </td>
    <td width="50%">
      <img src="https://github.com/user-attachments/assets/93b03fd8-7bf7-44e2-a539-81008a5d841d" alt="Profile" width="100%">
    </td>
  </tr>
  <tr>
    <td><b>Home.</b> The game's banner, the Play button, and one quiet strip for the server underneath it.</td>
    <td><b>Profile.</b> The account Play signs in as, its playtime and standing, and the score circle its achievements open from.</td>
  </tr>
  <tr>
    <td>
      <img src="docs/screenshots/achievements.png" alt="Achievements" width="100%">
    </td>
    <td>
      <img src="docs/screenshots/achievement-toast.png" alt="Achievement toast" width="100%">
    </td>
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
  played, the wizards on the account on a shelf, and the achievements you have earned. All of it stays on
  your PC.
- **Lets you browse for a picture rather than only picking a file.** A profile picture can come from a
  backup of Xbox 360 gamerpics - square, which is what a picture wants - and a banner from Wallhaven's
  weekly toplist, which is wide. Both are searched, paged and cached, so looking at a page twice costs
  nothing the second time.
- **Puts its settings in pages.** *Server*, *Client*, *ViewLight* and *Data*, with the server's own settings
  and the patch server's on a page each. No more scrolling past three groups of cards to reach the fourth.
- **Plays anywhere.** The caret beside Play joins a server somebody else is running, scans your network for
  one, or connects to Wizard101's own servers.
- **Runs from a folder, not just an install.** A `portable.txt` marker beside `ViewLight.exe` keeps
  everything - settings, server, the game if you copy it in - inside its own folder, so you can carry it on
  a USB stick. See [Portable mode](#portable-mode).
- **Keeps itself tidy.** Closing ViewLight stops everything it started, and asks first while the server is
  running. Both stop even if the launcher is killed outright, so nothing is left holding a port.
- **Can start over from nothing.** **Settings → Data → Start over completely** deletes both databases and
  lets the launcher and the server make themselves new ones - with the copy in your Google Drive as well, if
  you ask for it, because otherwise your backup would simply put the old data back. It asks twice, and the
  first of the two has to be read before it can be gone past.
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
The **Build channel** above that button chooses whether that is the newest published build or one made from
Imlight's current source on your machine; see [Choosing a server build](#choosing-a-server-build).

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
collect the game's files from Wizard101, thousands of them, around 18 GB - so picking it then offers to
**prepare those files right there**, while you are still on that screen: it starts the patch server and the
bar underneath says how far along it is. You can also wave it through and let Play get it started. Either
way it downloads the files itself, which is the workflow Aurorium was built around; ViewLight's part is to
say how far along it is. See [the patch server](#the-patch-server-in-plain-words).

## Choosing a server build

Imlight is a project that is still moving, and what it publishes on GitHub is marked as a *prerelease*.
ViewLight can install three different things, chosen with **Build channel** on the first screen and in
**Settings → Server**:

| Channel | What it installs | When it is for |
| --- | --- | --- |
| **Stable release** | Only builds Imlight publishes as a full release, not a prerelease. | The one to pick when you want what the project considers finished. There are **none yet**, and choosing this says so rather than quietly installing a prerelease. |
| **Prerelease** - the default | The newest build on GitHub, prerelease or not. | The ordinary case today: it is what Imlight publishes, and it is what the download button has always installed. |
| **Bleeding edge** | Imlight's **current source**, cloned from GitHub, built on this machine, and installed from the result. | A very specific bug or feature that has been committed and is not in any published build yet. |

**Bleeding edge is not a build anybody tested or published.** It is the source as it is this minute, and it
can be broken. If you report a bug to Imlight's developers while running it, say first that you built it from
their source rather than installing a release - otherwise they will look for a fault in code that was never
released.

It is also the only channel that can fail on the machine rather than at GitHub. It needs:

- **git**, to clone and update the checkout;
- **the .NET 10 SDK**, not just the runtime - ViewLight itself ships only the runtime;
- **Python 3.12 or newer**, because the type dump Imlight's `Imcodec` submodule needs is made from your own
  game with [wiztype](https://github.com/wizspoil/wiztype). ViewLight creates a private Python environment
  under its data folder and installs wiztype into that - nothing goes into your own Python. It then starts
  Wizard101 once (no account, just the login screen), reads the type registry, and closes the game again
  **only if ViewLight started it**. The dump is kept at
  `%LOCALAPPDATA%\ViewLight\source\type-dump.json` (or `<your portable folder>\source\type-dump.json`) and
  reused until your client's revision changes, so only the first build and the build after a game update do
  this;
- **your Wizard101 install** - the same folder the rest of the launcher uses. The message definitions Imlight
  needs are files inside `Root.wad`, and ViewLight extracts them into the checkout itself.

If you already have a type dump, put it at the `type-dump.json` path above and ViewLight uses it as it is,
with no Python involved. Deleting that file and its `.revision` companion asks ViewLight to make a fresh one.

**Updating is git's job, not a download's.** The checkout lives in ViewLight's own data folder, so the next
build is a `git fetch` and a merge, then a rebuild. A checkout with uncommitted edits is never merged over:
it is built exactly as it is, and the Log says so. Local commits are merged with GitHub's normally; a conflict
aborts the merge and leaves the tree alone. Every install is still an **overlay** - your `Imlight.ini`, logs,
caches and database are kept, exactly as with a published build. If the server is running, ViewLight lets it
keep running through the download or build and stops it only when the new files are about to be written, so
close Wizard101 first - an install is refused while the game itself is open.

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
| **Profile** | The account Play signs in as: picture, banner, playtime, standing, score, its wizards, and what it has earned lately. |
| **Profile → Achievements** | Every badge that account has earned, grouped by category, with a milestone card per ladder. |
| **Settings** | Four pages - *Server*, *Client*, *ViewLight* and *Data* - covering your folders, both configs, your backup and the reset. |
| **Log** | Everything the launcher, the patch server and the server said, in one place. |
| **About** | Versions, links, and what is still missing. |

**The Log reads like a terminal.** You can drag across it and copy as much as you like at once. The boxes
above it choose whether to show the launcher's lines, the server's, or the patch server's. A line that
repeats thirty times is folded into one line with a count next to it; press it to see every one of them, and
press it again to fold it back. That includes a warning the server repeats with a later timestamp in front of
it - the timestamp is not a difference worth a line of its own.

The fourth box, **Launcher's own notes**, is the odd one out: with it off, the lines ViewLight wrote *about*
the server and the patch server go, and those programs' own lines stay. It is there for pasting one program's
log to the people who wrote it without ViewLight's remarks on it coming too.

**Home shows two versions**: the server build that is installed, and the build the game itself reports. When
they do not match, ViewLight says so - that mismatch is the usual reason a login gets refused.

## Your profile and achievements

**Profile** starts with the profile itself: a picture and a banner you choose, the time that account has
spent on your server, how often it has signed in, and whether it still counts as **clean**. Choosing a new
picture or banner shows it straight away, and the buttons under the score go to the achievements and sign
this profile out.

**The profiles database comes up with the launcher.** Your profiles, playtime and badges live in the server's
own RavenDB, which ViewLight starts when it opens - a few seconds the first time. While it is starting, a
notification in the top-left corner says **Launching the profiles database…** rather than the launcher looking
like an account with nothing in it. The Google Drive sync notice appears in the same corner, stacked instead of
overlapping. Nothing is blocked while either runs: Play works, and pressing it puts the database down so the
server can have it. A notice reads for a few seconds and then folds itself into a small tray card in that
corner; the arrow on it opens the messages again and closes them again, and opening them by hand keeps them
open until you put them away.

**Logging out is how you change account.** **Log out** puts the profile down and the launcher covers itself
with the **sign-in screen**: the accounts as a row of cards you scroll across, each with its picture, its
name, its gamerscore and the first few wizards behind it - their school's element logo and level. Click one
to sign in as it, and ViewLight comes back to its profile. The row slides in, a card lifts under the
pointer, and the arrows beside it move a page at a time. It is the console's account picker rather than a
menu with one row in it, and the accounts it shows are this machine's own profiles - the ones your server
holds an account for.

**To change your picture or your banner, tick Edit profile** in the corner of the banner and click the one
you want to change. Unticked, they are just pictures; ticked, each wears a dashed edge and opens its two
choices. Nothing is downloaded until you pick something, and whatever you settle on is kept in your profile,
so it is backed up with the rest.

**A picture or a banner comes from your computer or from somewhere else.** *From my computer…* opens the
usual file picker. *Browse gamerpics…* opens a backup of Xbox 360 gamerpics - around 32,500 of them, square,
which is the shape a profile picture wants - and *Browse wallpapers…* opens Wallhaven's weekly toplist,
which is wide enough to be a banner and changes every week. Both can be searched, both are shown a page of
pictures at a time, and both are kept on your PC as they are looked at, so paging back through a grid costs
nothing the second time.

**Everything on this screen updates as you play** - badges light up as they are earned, the score follows,
a heading's "unlocked 3 of 40" moves with them, a new wizard appears, and a level, a school or a gold total
that changes moves too. You should not have to restart the launcher to see any of it.

**Your wizards stand under the account on a shelf**, under the heading **Wizards**: a cup wearing the game's
own element logo for that wizard's school, the name under it, and the level. Hover one for the rest - gold,
spells learned, quests finished, and when it was last seen. All of it is read from your own server while it
is running.

**Under them is a history** of what the account has earned lately, newest first: the badges and the
milestones together, each with when it happened, what it was worth and the server's own line about it. It is
the same badges the achievements screen shows rather than a second list, so the two can never disagree, and
the oldest of them stay on the achievements screen.

**The element logos are the game's own**, not something drawn to stand in for them: ViewLight ships none of
KingsIsle's art, so the seven - Fire, Ice, Storm, Myth, Life, Death and Balance - are fetched once from the
Wizard101 wiki and kept beside the launcher's other downloaded art. Each one is the school's own emblem, the
element inside the shield KingsIsle draws it in - the flame, the snowflake, the bolt, the scales, the skull -
rather than the few-pixel marker the wiki uses in its tables, which is unreadable at card size. A wizard whose
school was never read is shown without a logo rather than with one that belongs to a different school.

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

**Where this all comes from.** Pressing Play on a profile is one sign-in, and it is the account ViewLight
signs you in as - so a session is counted when you actually start playing, not when the launcher opens or
when you look at a profile. Playtime is counted while ViewLight's own server and its own game are both
running, and only for that profile. Zones, duels and creature kills are read from your own server while
ViewLight is running it; join somebody else's server and nothing is earned here. Play with the launcher
closed and that time is not recorded. Creature counts need the server's logging turned up to `DEBUG`, which
the Profile tab offers to do for you.

## Your settings

Settings is four pages, chosen from the row at the top of it, so nothing is found by scrolling past
something else.

| Page | What is on it |
| --- | --- |
| **Server → ImLight** | The server folder, its build and its updates, the admin account, realms and ports, logging and the client version it accepts, and what a new character starts with. |
| **Server → Aurorium** | The patch server: whose it is, the folder and port for one on this PC, what it has fetched, and which of those versions are kept. |
| **Client → Game folder** | Where Wizard101 is installed. |
| **Client → New characters** | Skipping the tutorial, the starting level, gold and zone, and the starter items. |
| **ViewLight** | The launcher's own settings: theme, the page it opens on, the close prompt, milestone notes, verbose mode, and portable mode. |
| **Data** | The cloud backup, and starting over completely. |

There are two kinds of setting, and they are kept separately.

**ViewLight's own settings**, under **Settings → ViewLight**: the theme (*System*, *Light* or *Dark*), the
page it opens on, whether closing it asks first while the server is running, whether milestone achievements
are announced as toasts, verbose mode, and portable mode.

**The launcher explains itself only when asked.** Most controls have a sentence under them saying what they
are for. That sentence is a comment on the screen rather than part of it, so it is hidden by default and the
cards close up around it. **Settings → ViewLight → Verbose mode** shows every one of them at once, and the
choice is remembered; off again, they all go away.

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
Ravenwood, learn your spell from the trainer, and equip the starter items out of your inventory. The
starting level, gold, zone and items are under **Settings → Client → New characters**.

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
simply has nothing to hand over. Four things are worth knowing:

- **Leave it running.** It only makes progress while it is alive, so ViewLight deliberately does **not** stop
  a patch server that has not finished. It stays until you close the launcher or stop it under Settings.
- **ViewLight shows you how far along it is.** While the patch server is up, a line and a bar under Home's
  patch server row - and in the patch server card in Settings - say how many of the version's files are
  here, how much of it that is, how much is left, and roughly how long the rest should take at the rate it
  is going. It says so plainly when nothing is arriving, too, which is what a retry after a failed download
  looks like from outside. The figure is measured, not guessed: the patch server does not publish its
  progress, so ViewLight counts the version's own file list against the files that are on disk - the folder
  the patch server is writing to. Nothing is put into that folder to read it, and the patch server is not
  asked for anything it does not already serve.
- **It tells you which file it is on, and which version.** The line names the version the patch server is
  fetching and the file it is downloading right now with how far into it it is - `Equipment-WorldData.wad
  (12.3 MB of 281.4 MB)` - and how many other files are arriving alongside it. When it is fetching a
  version other than the one your server asks for, it says that too, by name, because that is the one
  thing about a download that a session cannot work through. The Log keeps the same line as it moves from
  file to file, so you can see what a fetch that ran for hours actually did.
- **Play waits for the file the server's boot needs, rather than starting into a 404.** The first thing
  Imlight asks its patch server for is `Root.wad`, and a patch server that has not fetched it answers 404 -
  which leaves the server at "warming up" forever with nothing to show for it. So when ViewLight is about to
  start the server and the patch server cannot serve that one file yet, it waits: Home says what it is
  waiting for and where the download has got to, and starting the server anyway is one click away on the
  **Play anyway** button. Nothing is blocked and nothing is repointed - a patch server that has the file
  starts the server as it always did.
- **Your own game can still hand it the files, if you ask it to.** A download is not always enough on its
  own: the patch server gives up on any single file that takes more than two minutes, and the largest ones
  take longer than that on most connections, so it can fetch for hours and never finish. A Wizard101 install
  that is **exactly the version your server asks the patch server for** can supply those files directly -
  **Copy the game's files in**, under the patch server card in Settings. Nothing is overwritten and nothing
  is invented: only files of the right name and the right size go in, each one is written aside and moved
  into place, and running it again copies only what is still missing. It is a button rather than something
  Play does, because downloading is the patch server's own job.

**The copy knows exactly what "exactly" means.** It is a hand-off into a workflow that is not yours to
change, so it happens only when three things hold, and it says which one failed when they do not:

1. the revision being filled is the one **your server asks for** - not the one your game happens to be;
2. the copy it comes from **is that same revision**, read from the game itself;
3. **not one file of it is a different length** from what the revision's own file list says.

**Which version that first one is follows the switch further down the card**, and this matters: with the two
kept in step it is the version the patch server holds, because ViewLight writes that into the server's own
setting whenever it starts a patch server. A machine whose config still names the version the server build
was published with is therefore not refusing the copy over a version the server is not going to ask for.

That third one is what makes it a hand-off rather than a guess: a file of the right name at the wrong size
is another build's file, and the patch server never looks at a file it already has, so a wrong one in there
would be wrong for good. Measured on a real install against a real fetched revision, every file the two had
in common - the whole of `Data\` and `Bin\`, 3,725 of them - matched in length exactly, so where the three
conditions hold, what goes in is what would have come down. Files the revision wants that your install does
not have are left for the patch server to fetch, and downloading stays the default: this is a shortcut for
one machine with the right game on it, not a second way to collect a revision.

**Your server and your patch server are kept on the same version - that is the switch at the bottom of the
card, on by default: Sync the patch server's revision with the server's accepted client revision.** A patch
server fetches whatever version Wizard101 is serving, and there is no way to ask it for a different one - so
the value that has to move is the server's. Imlight asks its patch server for `V_{GameRevision}` and accepts
clients on that same revision, so every time ViewLight starts a patch server it writes the version that
patch server actually holds into `GameRevision`. Both then say the same thing: the server asks for files
the patch server has, and the client that has just been updated is the version the server accepts. The
`GameRevision` box under **Settings → Network** is greyed out while this is on, because the value is the
patch server's rather than yours.

**Switching it off is for a server deliberately held at one version.** Your server build pins the version
it was published with, and the patch server is then the side left needing something it cannot fetch: the
hand-off above is the only way it gets those files, and it needs your game to be exactly that version.
Turning the sync off puts the value your config had before the launcher wrote one back, and
only that value: a newer server build, or an edit by hand, is left alone.

**If your server cannot load its own files, ViewLight says so instead of waiting forever.** The one failure
with no other symptom: the server is running, its ports are open, and it never finishes warming up. What it
is doing is asking the patch server for a version that is not there, over and over - and it writes that in
its own log. ViewLight reads it and says which two versions disagree and which of the two it is that moves,
rather than leaving you to wait for a server that will never arrive.

**Old versions pile up unless somebody tidies them.** Each revision is around 18 GB, and the patch server
fetches a new one whenever Wizard101 publishes one - it writes it beside the ones it already has and never
removes anything, which is the right behaviour for it and the reason a long-running patch server ends up
holding every version it has ever seen. **The revisions it has fetched**, in the same card, is where that is
handled:

- **Clean up automatically** is the default and what ViewLight recommends: the newest version your patch
  server has - which, with the sync on, is the one your server asks for and the one your game is - is kept,
  and the older ones are removed. Choosing it *is* the permission it needs to do that on your behalf: it is
  stated before anything is downloaded, it can be changed here at any time, and every removal is written
  into the Log with what it was and why. Nothing that is being downloaded, and nothing your server asks for
  by name, can ever be removed by it. Keeping several versions is useful to a data hoarder, or to a server
  arrangement where a second version is the point - that is what **Manage them myself** is for.
- **Manage them myself** removes nothing on its own and gives you the list instead: each revision with what
  it is and how much of your disk it is using, a tick to keep it, **Look again**, and **Delete the unticked
  ones**. A revision can also be **Download**ed by request - which cannot be done, and says so: Aurorium
  fetches whatever version Wizard101 is serving when it looks, and there is no way to ask it for an older
  one. So an old version can be kept or removed, and once removed it cannot be fetched back.

That list is also the honest answer to "why is my disk full": it is the one place the launcher tells you
what the patch server is holding.

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

**A profile will not load on a machine with no internet connection.** The databases are this machine's own
and are read at `127.0.0.1` - ViewLight uses the loopback address and the loopback client, with the system
proxy switched off, so no part of reading a database can reach the network, the network's proxy, or anything
that has to be looked up. If a database still cannot be read, the **Log** now carries what the database
itself printed before it stopped - a port something else had taken, a data directory it could not open, a
runtime it was not given - which is the thing to paste when asking about it. What is worth checking first is
whether the database is up at all: the line under the profile says "the server's own RavenDB is running on
port …" when the launcher is holding it, and which port the server uses is `EmbeddedDatabasePort` in `Config/Imlight.ini`.

**Play says Wizard101 was not found.** Your game folder is wrong. Check it under **Settings → Game folder**;
the line underneath says what is and is not there.

**The server starts and then stops by itself.** Read the failure line on Home, then the **Log**. The most
common cause is the patch server - the server needs a file from it before it can boot - and ViewLight will
have already repaired an address that was blank or pointing at nothing.

**A download of the first file fails with "404" from your own machine.** Your patch server has not collected
the files yet. Leave it running and try again later, or hand it the files your game already has - see
[the patch server](#the-patch-server-in-plain-words).

**A download of a specific file fails with "404", and the file is one your patch server should be serving.**
Your patch server has not collected that version yet. The line under its row on Home says which file it is
on and how far along it is, and leaving it running is all that is needed - see
[the patch server](#the-patch-server-in-plain-words).

**The server says something like `Error while downloading file http://127.0.0.1:12369/V_r803238.../Root.wad`
and then sits at "warming up" forever.** Your server is asking the patch server for a version it does not
have - a build pinned to an older version than the one Wizard101 is serving. ViewLight reads that line out
of the server's own log and says so on Home, with both versions named. Normally this fixes itself: the
launcher writes the patch server's version into `GameRevision` every time it starts one, so stopping the
server and pressing Play again is enough. If the sync at the bottom of the patch server card is off, that is
the reason - turning it back on is the fix.

**You run a patch server on this machine and the server will not start.** Then the two have drifted apart,
and the honest answer is that one of the builds is behind: update the server, or wait for whichever one it is.
What ViewLight does about it is the sync at the bottom of the patch server card, and it is on by default -
see [the patch server](#the-patch-server-in-plain-words).

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
server accepts; the switch above is the same one, and the same rule - turn it back off once one of the two
has been updated.

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

**ViewLight 2.0 is where this launcher stops growing.** It does what it set out to do - find the game,
install and update the server, run the patch server, start a session, keep a profile and its achievements,
and back all of it up - and what is left is another pass over the whole thing rather than another feature: a
hand rewrite that keeps the same concept with nothing incidental carried over, an achievements system that
is finished and worth chasing rather than a ladder with gaps in it, some tidying of the interface, and the
optimisations that come from one person reading the whole program at once.

It is an idea and a reference as much as a program. Fork it, change it, take the parts you like and build
your own launcher - pull requests are welcome, and so is a fork that goes somewhere else entirely. This is
an example project, written out of a long-standing love of the game and the novelty of Imlight finally
existing - private-server software that lets Wizard101 be played offline. The server is the hard part and
it is not mine; this launcher is a nicer way to set it up and configure it, plus the things you can only do
because your own backend is running on the same machine as the client.

## Known limits

- **Item names in the starter-item picker** come from Imlight's own list rather than from the game. Reading
  the real names out of the game's data is separate work.
- **Imlight's own first-boot crash** is not worked around; pressing Play again is the way through it.
- **The file hand-off to a local patch server is the narrowest thing in the launcher.** It spares the patch
  server a download it would otherwise do itself, and it is deliberately the only way the launcher touches
  the patch server's storage: nothing else is ever written there, and nothing is ever replaced. It happens
  only when the revision, the source build and every file's length can all be checked, so what goes in is
  what would have come down - but content delivery is not something this launcher knows deeply, and files
  that do not match are refused rather than repaired. That is why it is a button under Settings rather than
  something Play does on the way past: a patch server left to collect its own revision is the path its
  authors designed, and it is the one a session takes unless somebody decides otherwise.
- **Removing a revision cannot be undone.** Aurorium fetches the version Wizard101 is serving when it looks
  and nothing can ask it for an older one, so a version that has been removed is gone until it comes round
  again - which it will not, once the game has moved on. Automatic cleanup is on by default, it only ever
  removes an older revision nothing is asking for and nothing is writing into, and it says what it removed
  in the Log - and the list in the patch server card is where to see what is there before that happens.
- **A local patch server still has to collect its first version from Wizard101.** That is where the game's
  files live. Once they are on your machine, everything is served locally - and while it is collecting them,
  the line under its row on Home says which file it is on, how far along it is and roughly how long the rest
  will take.
- **Waiting for the patch server is one file deep, not a whole version.** Play waits for the `Root.wad` the
  server's own boot asks for, and nothing more: the rest of the version arrives while you play, so a zone or
  an asset the patch server has not fetched yet is a 404 in the game rather than something the launcher
  holds the session back for. Waiting for all 18 GB before the first session would be the honest version of
  "finished", and it is not what a first run can afford.
- **The first Play after a fresh patch server can wait a long time.** The wait ends the moment `Root.wad` is
  on disk - which for a patch server that has just started fetching a whole version can be a while - and
  **Play anyway** on Home starts the session immediately if you would rather not wait.
- **ViewLight always takes the newest server build**; choosing an older one is not offered.
- **The revision sync only applies to your own patch server.** With a remote one chosen there is nothing to
  read or hand files to, and it does nothing; `GameRevision` is then whatever your config says.
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
