using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Turns what the running server says into what profiles know about themselves.
/// </summary>
/// <remarks>
/// <para>
/// The launcher's own server is the only thing that feeds this, which is deliberate: it is the only server
/// whose log and database this launcher can read, so achievements and playtime are earned on your own
/// machine against your own server and simply do not follow you anywhere else. Joining someone else's
/// server earns nothing here, because ViewLight learns nothing about what you did there - the launchers do
/// not talk to each other.
/// </para>
/// <para>
/// <b>Attribution is the hard part, and it is done by saying no wherever it cannot be done.</b> The player
/// database names accounts and characters, so anything read there is attributed exactly. The server log
/// names a wizard, a zone and a duel's winner, but never says which account a duel was fought by - so a
/// duel is credited only when exactly one profile was signed in when it ended, and left uncredited when
/// two were. Nothing is split, guessed or shared out.
/// </para>
/// </remarks>
public sealed class ProfileLedger {

    /// <summary>
    /// The longest gap between two looks at a running session that may be credited as playtime.
    /// </summary>
    /// <remarks>
    /// The launcher only ever counts time between two looks it made itself while the game was running under
    /// it, so this is the ceiling on one look rather than the shape of a normal one - a poll is ten seconds
    /// apart, and anything past half a minute means the launcher was not watching and the time in between
    /// was not observed. Crediting it would be inventing time.
    /// </remarks>
    public static readonly TimeSpan MaxCreditedGap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a wizard name from the log waits to be matched to a character in the online list.
    /// </summary>
    /// <remarks>
    /// The two arrive by different routes - a log line, then a database document - so a name is held until
    /// the account it belongs to turns up online. After this long, nobody signed in with it and it is let
    /// go rather than attached to whoever appears next.
    /// </remarks>
    public static readonly TimeSpan NameMatchWindow = TimeSpan.FromMinutes(2);

    /// <summary>How many zone entries may be held waiting for a name. Old ones are dropped.</summary>
    private const int MaxPendingZones = 64;

    /// <summary>
    /// How many duels' creature lists are held at once.
    /// </summary>
    /// <remarks>
    /// A duel that never ends would otherwise leave its list behind for as long as the launcher runs, so
    /// the oldest are dropped rather than kept forever. Nothing is concluded from a duel that is gone.
    /// </remarks>
    private const int MaxTrackedDuels = 256;

    private readonly ProfileStore _store;
    private readonly List<PendingSignIn> _pending = [];
    private readonly List<ZoneEntered> _pendingZones = [];

    /// <summary>
    /// The duels that have been seen to start and have not ended yet, by the id the server gives them.
    /// </summary>
    /// <remarks>
    /// Held until the ending arrives, because the parts are separate lines: who took a slot, what a spell
    /// hit, and who won. A win is credited to every creature of the duel, which is what the log actually
    /// says - it lists one winner for the whole duel, not a separate result per participant.
    /// </remarks>
    private readonly Dictionary<string, WatchedDuel> _duels = new(StringComparer.Ordinal);

    /// <summary>What has been read about one duel that has not ended yet.</summary>
    /// </summary>
    private sealed class WatchedDuel {

        /// <summary>
        /// The slots a creature planned a turn on, which is the server saying that a creature is on them.
        /// </summary>
        /// <remarks>
        /// This is what tells a creature from a player in a real duel, and it is the only thing that does:
        /// the participant line carries a debug name (<c>140DD</c>) rather than a template ID or a name a
        /// creature list could carry, and no line gives a creature's template at all. A creature that
        /// planned a turn was in the fight; one that joined it and died before ever planning is not counted,
        /// which is an under-count rather than a guess. See <see cref="DuelCreatureTurn"/>.
        /// </remarks>
        public HashSet<int> CreatureSlots { get; } = [];

        /// <summary>What each spell reached.</summary>
        public List<SpellCast> Hits { get; } = [];

        /// <summary>
        /// The creatures the line the server does not write yet would name outright.
        /// </summary>
        /// <remarks>
        /// Kept apart from the counted kills because it needs no list to be believed: a template id is the
        /// creature. See <see cref="DuelCreature"/>.
        /// </remarks>
        public List<uint> Creatures { get; } = [];

        /// <summary>
        /// The creature slots a spell of the players' side reached in this duel, once each.
        /// </summary>
        /// <remarks>
        /// Both halves come off the server's own lines: a spell's line says which slot it was cast from and
        /// which slots it reached, and a creature's attitude line says which slots held creatures. Casting
        /// from a player's circle at a creature's is the player's side reaching it - and a creature casting
        /// at a creature, or a spell that landed on the players' own circles, is neither.
        /// </remarks>
        public IEnumerable<int> CreatureSlotsHitByPlayers()
            => Hits.Where(hit => IsPlayersSide(hit.CasterSlot))
                .SelectMany(hit => hit.Slots)
                .Where(slot => CreatureSlots.Contains(slot) && !IsPlayersSide(slot))
                .Distinct();
    }

    /// <summary>
    /// The first slot that belongs to the players, from the server's own circle layout.
    /// </summary>
    /// <remarks>
    /// Circles 0-3 are the creatures' and 4-7 the players', in the order the sigil template lists them:
    /// Imlight says so in <c>CombatGroupReservations</c> and its own circle-choosing code walks the same
    /// split, so no player is ever put on a creature's circle. That is what makes "cast from 4 or above"
    /// mean the players' side - and the split is corroborated per duel rather than believed on its own: a
    /// duel in which no creature ever planned a turn credits nothing at all.
    /// </remarks>
    public const int FirstPlayerCircle = 4;

    private static bool IsPlayersSide(int slot) => slot >= FirstPlayerCircle;

    /// <summary>
    /// One spell's cast, as the duel it belongs to saw it.
    /// </summary>
    /// <remarks>
    /// The line itself is not kept: a badge earned by a count of creatures quotes the line that ended the
    /// duel rather than one of the spells in it, because the ending is what made the count true. See the
    /// duel's own pass in <see cref="Apply"/>.
    /// </remarks>
    private sealed record SpellCast(int CasterSlot, IReadOnlyList<int> Slots);

    /// <summary>
    /// The creatures the server's own list calls bosses, once it has been read.
    /// </summary>
    /// <remarks>
    /// A duel names the creatures it was against and never says which of them was a boss - that is a fact
    /// about the creature, and the server's creature list is where it lives. The list no longer has to say
    /// who was a creature: a duel's own lines do that (see <see cref="DuelCreatureTurn"/>), which leaves the
    /// bosses as the only thing still waiting on it.
    /// </remarks>
    private IReadOnlySet<uint>? _bosses;

    /// <summary>
    /// The game's own name tables, once they have been handed over.
    /// </summary>
    /// <remarks>
    /// Nothing to read until a game folder is known, and resolving a name means reading the client's
    /// archive - so it stays <see cref="WizardNameTable.Unavailable"/> until it is needed, and a launcher
    /// with no game folder simply shows characters by their id.
    /// </remarks>
    private WizardNameTable _names = WizardNameTable.Unavailable;

    /// <summary>Sign-ins seen in the log that no character has been matched to yet.</summary>
    private sealed record PendingSignIn(string Wizard, DateTimeOffset At);

    /// <summary>
    /// Which world each zone belongs to, as learned from the server's own log.
    /// </summary>
    /// <remarks>
    /// Seeded from the store, so what was learned in an earlier run is still known here: the log only ever
    /// goes forward, and a zone the server loaded last week is not going to load again just to be explained.
    /// </remarks>
    private readonly Dictionary<string, string> _zoneWorlds;

    /// <summary>
    /// The zone path the server is loading, waiting for the zone it belongs to to be named.
    /// </summary>
    /// <remarks>
    /// The two arrive as separate lines in the same block of the log - the path first, then the zone's own
    /// name - and the one that names the load comes last, so the path is held until it does. A path whose
    /// zone never turns up is simply replaced by the next one rather than being held forever.
    /// </remarks>
    private string _loadingZone = string.Empty;

    /// <summary>The accounts seen online at the last look, in the order the database listed them.</summary>
    /// <remarks>
    /// Read for one thing only: telling one player from two. A duel's own lines say who won but never which
    /// account fought it, so it is credited when exactly one account is in the game - and that question is
    /// answered by the server's online list, which is the only place that knows.
    /// </remarks>
    private IReadOnlyList<ulong> _online = [];

    /// <summary>
    /// The account the launcher's current session is being played as, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session is something the launcher starts and stops, and that is the whole point of it being here:
    /// it begins when Play starts the game on an account and ends when that game goes away. Nothing about
    /// switching profiles, opening the launcher or reading the server's database begins one, because none
    /// of those is anybody playing.
    /// </para>
    /// <para>
    /// It used to be worked out from the server's online list instead - being in that list was being signed
    /// in - which was wrong in a way that showed: the server's database keeps saying an account is online
    /// from the moment the launcher reads it, so an idle launcher counted playtime, and one session counted
    /// a sign-in for every look that found the account there.
    /// </para>
    /// </remarks>
    private ulong? _playingAccountId;

    public ProfileLedger(ProfileStore store) {
        _store = store;
        _zoneWorlds = new Dictionary<string, string>(store.ZoneWorlds, StringComparer.OrdinalIgnoreCase);

        // The catalog is told what this server's worlds are, here and wherever a new one is learned: it is
        // where the badges that name them come from, and this is the only place that knows them. Doing it at
        // the moment of learning rather than at the next poll is what lets the account that discovered a
        // world be given its badge by the same pass that found it.
        AchievementCatalog.UseWorlds(LearnedWorlds);
    }

    /// <summary>Which world each zone belongs to, as far as the launcher has learned.</summary>
    public IReadOnlyDictionary<string, string> ZoneWorlds => _zoneWorlds;

    /// <summary>
    /// Re-reads what the store holds, for a store that was given its notes after this was built.
    /// </summary>
    /// <remarks>
    /// The launcher's own database takes tens of seconds to come up, so the store is empty while this is
    /// being made. Without this, every zone learned in an earlier run would look like one nobody has ever
    /// been to - and the badges that name worlds would be missing from the catalog until the server happened
    /// to load one of them again.
    /// </remarks>
    public void ReloadFromStore() {
        _zoneWorlds.Clear();

        foreach (var (zone, world) in _store.ZoneWorlds) {
            _zoneWorlds[zone] = world;
        }

        AchievementCatalog.UseWorlds(LearnedWorlds);
    }

    /// <summary>
    /// The worlds the launcher knows of, in a stable order, for the badges that name them.
    /// </summary>
    /// <remarks>
    /// Ordered rather than left in dictionary order, because this is what the catalog's world badges are
    /// built from: a list that reshuffled itself would reshuffle the profile screen under the reader.
    /// </remarks>
    public IReadOnlyList<string> LearnedWorlds
        => [.. _zoneWorlds.Values.Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal)];

    /// <summary>
    /// Duels that ended while the credit was ambiguous - two profiles signed in at once - and so were
    /// given to nobody. Kept so the count can be shown rather than silently dropped.
    /// </summary>
    public int UncreditedDuels { get; private set; }

    /// <summary>Sign-ins in the log that are still waiting for a character to be matched to.</summary>
    public int UnmatchedSignIns => _pending.Count;

    public ProfileStore Store => _store;

    /// <summary>
    /// Makes sure there is a profile for every account the launcher knows about, so a profile exists when
    /// the player picks it rather than only once they have played.
    /// </summary>
    public void NoteAccounts(IEnumerable<PlayerAccount> accounts) {
        var changed = false;

        foreach (var account in accounts) {
            var known = _store.Has(account.AccountId);
            _store.GetOrCreate(account.AccountId, account.Username);
            changed |= !known;
        }

        if (changed) {
            _store.Save();
        }
    }

    /// <summary>
    /// Reads a chunk of server log and folds what it says into the profiles it is about.
    /// </summary>
    /// <returns>The achievements this made true, so the caller can say so.</returns>
    public IReadOnlyList<ProfileAward> ApplyLogLines(IEnumerable<string> lines) {
        var earned = new List<ProfileAward>();
        var changed = false;

        foreach (var line in lines) {
            foreach (var fact in ServerLogFacts.Parse(line)) {
                changed |= Apply(fact, earned);
            }
        }

        if (changed) {
            _store.Save();
        }

        return earned;
    }

    /// <summary>
    /// Folds in what the player database says: which wizards an account has, who is in the game, and every
    /// command the server has logged.
    /// </summary>
    /// <remarks>
    /// Playtime is deliberately not counted from here any more - see <see cref="BeginSession"/>. The online
    /// list this reads is who the server has in the game, which is worth knowing for telling one player from
    /// two, and is not evidence that anybody is playing this machine's server right now: the database says
    /// the same thing whether the server is running or the launcher is idling on the profile screen.
    /// </remarks>
    public IReadOnlyList<ProfileAward> ApplyDatabase(PlayerProfileData data, DateTimeOffset at) {
        if (!data.IsUsable) {
            // Nothing may be concluded from a read that did not look right - including that someone who
            // was online a moment ago has signed out.
            return [];
        }

        var earned = new List<ProfileAward>();

        MergeWizards(data.Wizards);
        MergeCommands(data.Commands);

        _online = data.Online.Select(o => o.AccountId).Distinct().ToList();

        MatchWizardNames(data.Online, at, earned);

        // Every badge this pass earns is judged from the profile as a whole, so there is no one line to
        // quote for any of them - and stamping a summary of the whole account onto each of them is what
        // made earned badges unreadable. Nothing is quoted: a badge with no line of its own says what it
        // takes instead, and a badge earned by a specific thing keeps that thing's line, which is written
        // where the thing is read rather than here.
        foreach (var profile in _store.Profiles) {
            Award(profile, at, string.Empty, earned);
        }

        _store.Save();
        return earned;
    }

    /// <summary>
    /// Play has started the game on <paramref name="accountId"/>: one sign-in for that profile, and the one
    /// profile playtime is credited to while the session lasts.
    /// </summary>
    /// <remarks>
    /// One sign-in per session is the whole of it. The game's own log says a wizard arrived, and the server's
    /// database says an account is in the game, but neither says a session began: a reconnect says the first
    /// again and the second is still true of a server nobody is playing. Pressing Play on a profile is the
    /// one unambiguous "I am playing as this account", which is why it is the one counted.
    /// </remarks>
    /// <returns>The achievements this made true, so the caller can say so.</returns>
    public IReadOnlyList<ProfileAward> BeginSession(ulong accountId, DateTimeOffset at) {
        var profile = _store.GetOrCreate(accountId, string.Empty);

        _playingAccountId = accountId;

        profile.SessionCount++;
        profile.FirstPlayedAt ??= at;
        profile.LastPlayedAt = at;
        profile.LastSeenOnlineAt = at;

        var earned = new List<ProfileAward>();
        Award(profile, at, string.Empty, earned);

        _store.Save();
        return earned;
    }

    /// <summary>
    /// Adds the time since the last look to the profile being played, and counts nothing at all when no
    /// session is on.
    /// </summary>
    /// <remarks>
    /// Called on the same poll that reads the server, so a session is measured in looks rather than by what
    /// a clock says afterwards - and a session the launcher stopped watching is never credited past the last
    /// look it made, because <see cref="MaxCreditedGap"/> caps a single one.
    /// </remarks>
    public void CreditSessionTime(DateTimeOffset at) {
        if (PlayingProfile() is not { } profile) {
            return;
        }

        // No marker means time is not being credited from anywhere - the session was closed off, by the game
        // connection ending or by a stop - so the count starts again from this look rather than claiming the
        // time since. A player who reconnects inside one session therefore loses the seconds they were out.
        if (profile.LastSeenOnlineAt is null) {
            profile.LastSeenOnlineAt = at;
            profile.LastPlayedAt = at;
            return;
        }

        Credit(profile, at);
        profile.LastSeenOnlineAt = at;
        profile.LastPlayedAt = at;
    }

    /// <summary>
    /// The server's own log says the game connection went, so nothing is counted through the gap.
    /// </summary>
    /// <remarks>
    /// More exact than waiting for the next look, and deliberately not the end of the session: the game's
    /// client keeps running while somebody sits at the character screen or comes back in, and a session is
    /// the launcher's own game process rather than one connection inside it. So what has been seen of the
    /// session is added up to the second the server named, and counting starts again at the next look -
    /// which means the seconds between the two are not credited, because nobody was in the game for them.
    /// </remarks>
    public void NoteGameConnectionEnded(DateTimeOffset at) {
        if (PlayingProfile() is not { } profile || profile.LastSeenOnlineAt is null) {
            return;
        }

        Credit(profile, at);
        profile.LastPlayedAt = at;
        profile.LastSeenOnlineAt = null;
    }

    /// <summary>
    /// The session is over: credit what has been seen of it and stop counting.
    /// </summary>
    /// <remarks>
    /// Called from every way a session can end - the game's client exiting, the server stopping, the window
    /// closing - and idempotent, because more than one of those usually happens. Nothing more is credited to
    /// that profile until Play starts another session. The game connection ending early is not one of these;
    /// see <see cref="NoteGameConnectionEnded"/>, which stops the clock without ending the session.
    /// </remarks>
    public void EndSession(DateTimeOffset at) {
        if (PlayingProfile() is not { } profile) {
            _playingAccountId = null;
            return;
        }

        if (profile.LastSeenOnlineAt is not null) {
            Credit(profile, at);
            profile.LastPlayedAt = at;
        }

        profile.LastSeenOnlineAt = null;
        _playingAccountId = null;
        _store.Save();
    }

    /// <summary>Whether a session is on, so the launcher can tell an ending from nothing happening.</summary>
    public bool SessionIsOn => _playingAccountId is not null;

    /// <summary>The profile the launcher's current session is being played as, or <c>null</c>.</summary>
    private LauncherProfile? PlayingProfile()
        => _playingAccountId is { } accountId
            ? _store.Profiles.FirstOrDefault(p => p.AccountId == accountId)
            : null;

    private bool Apply(ServerLogFact fact, List<ProfileAward> earned) {
        switch (fact) {
            // -------------------------------------------------------- a wizard went into the game
            case WizardSignedIn signIn: {
                var profile = FindByWizardName(signIn.Wizard);

                if (profile is null) {
                    // Nobody has this wizard's name on record yet. The name is held rather than dropped:
                    // the online list, which does know the account, arrives on the next poll and is what
                    // the name is matched to.
                    if (!_pending.Any(p => p.Wizard == signIn.Wizard && p.At == signIn.At)) {
                        _pending.Add(new PendingSignIn(signIn.Wizard, signIn.At));
                    }

                    return true;
                }

                var wizard = profile.Wizards.First(w => NameIs(w, signIn.Wizard));
                wizard.LastSeenAt = signIn.At;
                profile.LastPlayedAt = signIn.At;

                return true;
            }

            // -------------------------------------------------------- the game connection went
            case ServerConnectionLost { Game: true } connectionLost:
                NoteGameConnectionEnded(connectionLost.At);
                return true;

            // -------------------------------------------------------- a zone began loading
            case ZoneLoadStarted started:
                _loadingZone = started.Path;
                return false;

            // -------------------------------------------------------- a loaded zone was named
            case ZoneNamed named:
                return LearnZoneWorld(named, earned);

            // -------------------------------------------------------- a duel began
            case DuelStarted started:
                RestartDuel(started.DuelId);
                return true;

            // -------------------------------------------------------- a creature joined a duel
            case DuelCreature creature:
                NoteDuelCreature(creature);
                return true;

            // -------------------------------------------------------- a creature planned a turn
            case DuelCreatureTurn turn:
                NoteDuelCreatureTurn(turn);
                return true;

            // -------------------------------------------------------- a spell reached some slots
            case DuelSpellHit hit:
                NoteDuelSpellHit(hit);
                return true;

            // -------------------------------------------------------- a duel finished
            case DuelFinished duel: {
                var watched = ForgetDuel(duel.DuelId);
                var profile = SingleOnlineProfile();

                if (profile is null) {
                    UncreditedDuels++;
                    return true;
                }

                if (duel.PlayersWon) {
                    profile.DuelsWon++;

                    // What a won duel leaves behind: the creatures it identified outright, and a count of
                    // the creature slots the players' spells reached. The count needs no creature list -
                    // the duel's own lines carry it - while which of them were bosses still does.
                    CreditCreatures(profile, watched);

                    // What was just beaten is re-counted against the list here rather than only when the
                    // list is read: a boss beaten in this duel is a boss beaten now, not at the next
                    // launcher start.
                    if (_bosses is { } bosses) {
                        profile.ClassifyBeatenCreatures(bosses);
                    }
                }
                else {
                    profile.DuelsLost++;
                }

                // One pass, quoting the line that ended the duel: the duel's own badges are what that line
                // is about, and a count reached in the duel is not shown as if a spell line proved it. See
                // Award for why a pass quotes one line rather than a line each.
                Award(profile, duel.At, duel.Evidence, earned);
                return true;
            }

            // -------------------------------------------------------- a wizard entered a zone
            case ZoneEntered zone:
                return NoteZone(zone, earned);

            // -------------------------------------------------------- a command was used
            case CommandUsed command: {
                var profile = _store.Profiles.FirstOrDefault(
                    p => string.Equals(p.Username, command.Username, StringComparison.OrdinalIgnoreCase));

                if (profile is null) {
                    return false;
                }

                profile.CommandsSeenHere++;
                NoteCommand(profile, command.Command, command.At);
                Award(profile, command.At, command.Evidence, earned);

                return true;
            }

            default:
                return false;
        }
    }

    // -------------------------------------------------------------------- the database

    private void MergeWizards(IReadOnlyList<PlayerWizardRecord> wizards) {
        foreach (var record in wizards) {
            var profile = _store.GetOrCreate(record.AccountId, string.Empty);
            var wizard = profile.Wizards.FirstOrDefault(w => w.CharId == record.CharId);

            if (wizard is null) {
                wizard = new ProfileWizard { CharId = record.CharId };
                profile.Wizards.Add(wizard);
            }

            wizard.Level = Math.Max(wizard.Level, record.Level);

            // Gold is stored as the most ever held rather than as it stands: gold gets spent, and an
            // achievement that could be taken away again by a shopping trip would be no achievement.
            wizard.Gold = Math.Max(wizard.Gold, record.Gold);

            wizard.QuestsCompleted = Math.Max(wizard.QuestsCompleted, record.CompletedQuests.Count);

            // A wizard's school never changes, so the record's answer is kept whenever it has one.
            if (record.School.Length > 0) {
                wizard.School = record.School;
            }

            // Spells are added and never taken away, for the same reason as gold: the list is what the
            // wizard knows, and a spell removed from a deck is still known.
            foreach (var spell in record.Spells) {
                if (!wizard.LearnedSpells.Contains(spell)) {
                    wizard.LearnedSpells.Add(spell);
                }
            }

            // A name already known is kept, whoever gave it: the server's log naming a wizard is direct
            // evidence, where resolving the stored key is the launcher reconstructing the same name from the
            // game's tables. Only one with no name at all is filled in here.
            if (string.IsNullOrWhiteSpace(wizard.Name)) {
                wizard.Name = RecordedName(record);
            }
        }
    }

    /// <summary>
    /// The name a character document carries for a wizard, or nothing when it carries none that can be read.
    /// </summary>
    /// <remarks>
    /// An override is a name already spelled out and is taken as it stands. Otherwise the stored key is
    /// resolved through the game's tables, which needs the gender as well as the key: the two first-name
    /// tables are different lists, so without it there is no honest answer and the character keeps its id.
    /// </remarks>
    private string RecordedName(PlayerWizardRecord record) {
        if (record.Name is { Length: > 0 }) {
            return record.Name;
        }

        return record.NameKeys is { } keys && WizardNameTable.IsFemale(record.Gender) is { } female
            ? _names.Resolve(keys, female) ?? string.Empty
            : string.Empty;
    }

    private void MergeCommands(IReadOnlyList<PlayerCommandRecord> commands) {
        foreach (var group in commands.GroupBy(c => c.AccountId)) {
            var profile = _store.Profiles.FirstOrDefault(p => p.AccountId == group.Key);
            if (profile is null) {
                continue;
            }

            var ordered = group.OrderBy(c => c.At).ToList();
            var latest = ordered[^1];

            // The server's own count, kept apart from what this launcher watched go by: they are the same
            // command seen twice when both are available, so they are never added together.
            profile.CommandsOnRecord = Math.Max(profile.CommandsOnRecord, ordered.Count);
            NoteCommand(profile, latest.Text, latest.At);
        }
    }

    /// <summary>
    /// Adds the time since the last look at the running session, capped at one poll's worth.
    /// </summary>
    /// <remarks>
    /// The cap is what keeps a launcher that stopped watching from inventing the gap: the account was in the
    /// game at both ends, and how much of the time in between it was there for is not something this saw.
    /// </remarks>
    private static void Credit(LauncherProfile profile, DateTimeOffset at) {
        if (profile.LastSeenOnlineAt is not { } last) {
            return;
        }

        var seconds = (at - last).TotalSeconds;
        if (seconds <= 0) {
            return;
        }

        profile.PlaytimeSeconds += (long) Math.Min(seconds, MaxCreditedGap.TotalSeconds);
    }

    /// <summary>
    /// Attaches the names wizards signed in under to the characters they signed in as.
    /// </summary>
    /// <remarks>
    /// The log names a wizard and the database knows which character an account is on right now, and
    /// neither side has the other's half: Imlight stores a wizard's name as indices into its own name
    /// tables, so the database cannot be asked for it. The two are matched by signing in, which is what
    /// makes them the same event. One sign-in and one unnamed character is a match; more than one is left
    /// alone, and the character is then shown by its id rather than under a name that might be someone
    /// else's.
    /// </remarks>
    private void MatchWizardNames(
        IReadOnlyList<PlayerOnlineRecord> online, DateTimeOffset at, List<ProfileAward> earned) {
        _pending.RemoveAll(p => at - p.At > NameMatchWindow);
        _pendingZones.RemoveAll(z => at - z.At > NameMatchWindow);

        if (_pending.Count == 0) {
            return;
        }

        var unnamed = new List<(LauncherProfile Profile, PlayerOnlineRecord Record)>();

        foreach (var record in online.OrderBy(o => o.CharId)) {
            var profile = _store.Profiles.FirstOrDefault(p => p.AccountId == record.AccountId);
            var wizard = profile?.Wizards.FirstOrDefault(w => w.CharId == record.CharId);

            if (profile is not null && wizard is not null && string.IsNullOrWhiteSpace(wizard.Name)) {
                unnamed.Add((profile, record));
            }
        }

        var candidates = _pending.OrderBy(p => p.At).ToList();

        // A name is attached only when there is exactly one sign-in and one character waiting for it.
        // Two accounts signing in within one poll is two names and two characters, and which is which is
        // written down nowhere - so neither is attached. Getting this wrong would put one player's name on
        // another player's achievements, which is the one thing this must never do.
        if (unnamed.Count != 1 || candidates.Count != 1) {
            return;
        }

        var (signedInAs, onlineNow) = unnamed[0];
        var matched = signedInAs.Wizards.First(w => w.CharId == onlineNow.CharId);

        matched.Name = candidates[0].Wizard;
        matched.LastSeenAt = at;
        _pending.Remove(candidates[0]);

        // The zones visited before the name was known are folded in now rather than thrown away: a wizard
        // is through a few doors in the first seconds of a session, which is exactly the part the badge
        // about starting out is about.
        foreach (var pending in _pendingZones.Where(z => z.Wizard == matched.Name).ToList()) {
            NoteZone(pending, earned);
        }
    }

    /// <summary>
    /// Records a zone visit, holding it if the wizard it names has not been matched to an account yet.
    /// </summary>
    private bool NoteZone(ZoneEntered zone, List<ProfileAward> earned) {
        var profile = FindByWizardName(zone.Wizard);

        if (profile is null) {
            _pendingZones.Add(zone);

            if (_pendingZones.Count > MaxPendingZones) {
                _pendingZones.RemoveAt(0);
            }

            return false;
        }

        // Kept as a set of what was visited, so a wizard pacing between two zones does not count as
        // having explored a hundred of them.
        if (!profile.ZonesVisited.Any(z => string.Equals(z, zone.Zone, StringComparison.OrdinalIgnoreCase))) {
            profile.ZonesVisited.Add(zone.Zone);
        }

        // And if the launcher knows which world that zone is in, this is a world discovered too. Learning
        // which world a zone is in does not depend on this, so the visit is credited either way - the world
        // simply follows when it can.
        AttributeWorlds(profile);

        Award(profile, zone.At, zone.Evidence, earned);
        return true;
    }

    /// <summary>
    /// Learns which world a zone belongs to, from the server's own account of loading it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The log names a zone twice over as it loads: once by the path it came from, which begins with the
    /// world (<c>WizardCity/WC_Streets/WC_Unicorn</c>), and once by the name the game calls it
    /// (<c>WizardZone_UnicornWay</c>). Nothing else in anything the launcher can read joins the two - the
    /// client's own zone table is inside an archive it cannot open - so this is learned from the server's own
    /// lines rather than assumed from a list written down here, which would be a guess that goes stale.
    /// </para>
    /// <para>
    /// A zone the server names by an id is an instance rather than a place, and is left out: there is no
    /// world to learn from "instance 2168", and a house being in Wizard City is not somewhere anybody
    /// discovered.
    /// </para>
    /// </remarks>
    /// <returns>Whether anything was learned, which is what the caller saves on.</returns>
    private bool LearnZoneWorld(ZoneNamed named, List<ProfileAward> earned) {
        var path = _loadingZone;
        _loadingZone = string.Empty;

        if (ZoneLoadStarted.WorldOf(path) is not { Length: > 0 } world || !ZoneNames.IsNamed(named.Zone)) {
            return false;
        }

        if (_zoneWorlds.TryGetValue(named.Zone, out var known)
            && string.Equals(known, world, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        _zoneWorlds[named.Zone] = world;
        _store.ZoneWorlds[named.Zone] = world;
        AchievementCatalog.UseWorlds(LearnedWorlds);

        // Every profile that has already been in that zone has been in the world it turns out to belong to,
        // so they are told now rather than only on their next visit - the pairing arrived late, the visit did
        // not, and a badge that hung on which of the two was seen first would be no badge at all.
        foreach (var profile in _store.Profiles) {
            if (AttributeWorlds(profile)) {
                Award(profile, named.At, named.Evidence, earned);
            }
        }

        return true;
    }

    /// <summary>
    /// Marks the worlds a profile's own zone list says it has been in.
    /// </summary>
    /// <remarks>
    /// Worked out from what the profile has visited rather than from what just happened, so it is the same
    /// answer however the launcher came to know: a zone entered while the pairing was unknown is credited the
    /// moment it is known, and a zone learned about in an earlier run stays credited.
    /// </remarks>
    /// <returns>Whether a world was added.</returns>
    private bool AttributeWorlds(LauncherProfile profile) {
        var added = false;

        foreach (var zone in profile.ZonesVisited) {
            if (!_zoneWorlds.TryGetValue(zone, out var world) || profile.HasVisitedWorld(world)) {
                continue;
            }

            profile.WorldsVisited.Add(world);
            added = true;
        }

        return added;
    }

    /// <summary>
    /// The duel a line is about, started here if it is the first thing seen about it.
    /// </summary>
    /// <remarks>
    /// The oldest is dropped once too many are being followed, so a duel that never ends cannot leave its
    /// parts behind for as long as the launcher runs. Nothing is ever concluded from a duel that is gone.
    /// </remarks>
    private WatchedDuel DuelOf(string duelId) {
        if (_duels.TryGetValue(duelId, out var duel)) {
            return duel;
        }

        while (_duels.Count >= MaxTrackedDuels) {
            _duels.Remove(_duels.Keys.First());
        }

        duel = new WatchedDuel();
        _duels[duelId] = duel;
        return duel;
    }

    /// <summary>
    /// Starts a duel over, under the id the server gave it.
    /// </summary>
    /// <remarks>
    /// The id is the sigil's rather than the fight's, and a sigil is fought at more than once: the same
    /// number comes round again with the same lines about slots underneath it. Throwing away what the
    /// earlier fight said is what keeps a creature that was in that one out of this one's count.
    /// </remarks>
    private void RestartDuel(string duelId) {
        _duels.Remove(duelId);
        DuelOf(duelId);
    }

    /// <summary>
    /// Records that a creature took part in a duel, by the template the server named it with.
    /// </summary>
    /// <remarks>
    /// Imlight does not write this line yet, and the day it does, this is what turns a duel's creatures into
    /// creatures the launcher can name - which is what the boss badges are waiting on. See
    /// <see cref="DuelCreature"/>.
    /// </remarks>
    private void NoteDuelCreature(DuelCreature creature) {
        var duel = DuelOf(creature.DuelId);

        if (!duel.Creatures.Contains(creature.TemplateId)) {
            duel.Creatures.Add(creature.TemplateId);
        }
    }

    /// <summary>Records that a creature planned a turn on a slot, which makes it a creature's slot.</summary>
    private void NoteDuelCreatureTurn(DuelCreatureTurn turn)
        => DuelOf(turn.DuelId).CreatureSlots.Add(turn.Slot);

    /// <summary>Records what a spell reached, ready for the duel's ending.</summary>
    private void NoteDuelSpellHit(DuelSpellHit hit)
        => DuelOf(hit.DuelId).Hits.Add(new SpellCast(hit.CasterSlot, hit.Slots));

    /// <summary>Takes what was read about a duel, once its ending has arrived.</summary>
    private WatchedDuel ForgetDuel(string duelId)
        => _duels.Remove(duelId, out var duel) ? duel : new WatchedDuel();

    /// <summary>
    /// Takes the game's own name tables, so a wizard can be shown by name rather than by id.
    /// </summary>
    public void UseNameTable(WizardNameTable table) => _names = table;

    /// <summary>
    /// Takes the server's creature list, so a beaten creature can be told apart from a beaten boss.
    /// </summary>
    /// <remarks>
    /// The list is a file on disk rather than something the server reports, so it can turn up after
    /// creatures have already been beaten - which is why the beaten creatures are re-counted against it
    /// here rather than only as duels are won. Every profile is re-counted, because a boss beaten on an
    /// account the launcher was not watching is still a boss beaten that this list can now explain.
    /// </remarks>
    /// <param name="creatures">Every creature the list has.</param>
    /// <returns>The achievements this made true, so the caller can say so.</returns>
    public IReadOnlyList<ProfileAward> UseCreatureRoster(
        IReadOnlyList<CreatureManifestEntry> creatures, DateTimeOffset at) {
        var bosses = creatures.Where(c => c.IsBoss).Select(c => c.TemplateId).ToHashSet();

        _bosses = bosses;

        var earned = new List<ProfileAward>();

        foreach (var profile in _store.Profiles) {
            profile.ClassifyBeatenCreatures(bosses);

            // Nothing is quoted, for the same reason as the database pass: reading a creature list is not
            // something that happened to this badge, and the badges it turns on say what they take.
            Award(profile, at, string.Empty, earned);
        }

        _store.Save();
        return earned;
    }

    /// <summary>
    /// Marks the creatures of a won duel as beaten by the profile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two ways of knowing, and both are the server's own lines. A line naming a creature outright
    /// (<c>| Creature 35166 Wraith-WCBoss-L05 joined</c>) is taken as it stands - the template id is the
    /// creature, and no list is needed to believe it - but Imlight does not write that line yet, so the way
    /// that works today is the duel's other lines read together: a spell cast from a player's circle that
    /// reached a slot a creature was seen planning on is the players' side beating that creature, and the
    /// duel having been won is what makes it beaten rather than merely hit.
    /// </para>
    /// <para>
    /// The two leave different things behind on purpose. A named creature goes into the list of what was
    /// beaten, once each, because the id is known; a slot does not, because nothing says which creature was
    /// on it - so it adds one to the count of creatures beaten instead. Farming one creature therefore
    /// counts every time, which is what the log supports and what the screen says.
    /// </para>
    /// </remarks>
    private static void CreditCreatures(LauncherProfile profile, WatchedDuel duel) {
        foreach (var creature in duel.Creatures) {
            Beat(profile, creature);
        }

        profile.CreaturesKilled += duel.CreatureSlotsHitByPlayers().Count();
    }

    /// <summary>Marks one creature as beaten by a profile, once.</summary>
    private static void Beat(LauncherProfile profile, uint templateId) {
        if (!profile.CreaturesDefeated.Contains(templateId)) {
            profile.CreaturesDefeated.Add(templateId);
        }
    }

    private void NoteCommand(LauncherProfile profile, string command, DateTimeOffset at) {
        if (profile.LastCommandAt is { } last && last > at) {
            return;
        }

        profile.LastCommand = command;
        profile.LastCommandAt = at;
    }

    // -------------------------------------------------------------------- awarding

    /// <summary>
    /// Gives a profile every achievement it has come to deserve, and says what made each one true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Judged against the profile every time rather than against the event that triggered it, so the rules
    /// stay in one place and a threshold reached by any route counts - a level 25 reached on the last poll
    /// is the same as one reached while the launcher was watching. Earned badges are never taken away.
    /// </para>
    /// <para>
    /// <paramref name="evidence"/> is the line the server wrote when the trigger <i>is</i> what the badges
    /// are about - a duel that was won, a zone that was entered - and empty when the trigger is a pass over
    /// what the profile holds. One line for many badges is not evidence of any of them, so a pass quotes
    /// nothing and the badges say what they take.
    /// </para>
    /// </remarks>
    private static void Award(
        LauncherProfile profile, DateTimeOffset at, string evidence, List<ProfileAward> earned) {
        foreach (var achievement in AchievementCatalog.All) {
            if (profile.Has(achievement.Id) || !achievement.Earned(profile)) {
                continue;
            }

            var given = new EarnedAchievement(achievement.Id, at, evidence);
            profile.Achievements.Add(given);
            earned.Add(new ProfileAward(profile, given));
        }
    }

    private LauncherProfile? FindByWizardName(string wizardName)
        => _store.Profiles.FirstOrDefault(p => p.Wizards.Any(w => NameIs(w, wizardName)));

    private static bool NameIs(ProfileWizard wizard, string name)
        => !string.IsNullOrWhiteSpace(wizard.Name)
           && string.Equals(wizard.Name, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The profile to credit something to when there is no way to know which of two it was.</summary>
    private LauncherProfile? SingleOnlineProfile()
        => _online.Count == 1
            ? _store.Profiles.FirstOrDefault(p => p.AccountId == _online[0])
            : null;


}
