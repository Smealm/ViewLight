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
    /// The longest gap between two looks at the server's online list that may be credited as playtime.
    /// </summary>
    /// <remarks>
    /// A launcher that was closed and opened again finds the account still signed in, and the gap between
    /// its last look and its next one is not something it observed. Crediting it would be inventing time;
    /// crediting one poll's worth is the honest maximum, and what the user was actually playing for in that
    /// gap is simply not counted.
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
    /// The creatures each duel has been seen to involve, by the template they were spawned from.
    /// </summary>
    /// <remarks>
    /// Held until the duel's ending arrives, because the two are separate lines: the creatures are named
    /// when the duel starts, and who won is only known when it ends. A win is credited to every creature
    /// in the duel, which is what the log actually says - it lists one winner for the whole duel, not a
    /// separate result per participant.
    /// </remarks>
    private readonly Dictionary<string, List<uint>> _duelCreatures = new(StringComparer.Ordinal);

    /// <summary>
    /// The server's own creature list, once it has been read.
    /// </summary>
    /// <remarks>
    /// A duel says who won it and names the creatures it was against, but nothing in either says which of
    /// them was a boss - that is a fact about the creature, and the server's creature list is where it
    /// lives. Until the list has been read, a beaten creature is counted as beaten and nothing more, which
    /// can only ever under-count.
    /// </remarks>
    private (IReadOnlySet<uint> Bosses, IReadOnlySet<uint> Creatures)? _roster;

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
    private IReadOnlyList<ulong> _online = [];

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
    /// Folds in what the player database says: which wizards an account has, who is signed in, and every
    /// command the server has logged. Also where playtime is counted from, because presence in that online
    /// list <i>is</i> being signed in.
    /// </summary>
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

        foreach (var profile in _store.Profiles) {
            var online = _online.Contains(profile.AccountId);
            ApplyPresence(profile, online, at);
        }

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
    /// Ends a session at the moment the server says the game connection went, which is more exact than the
    /// next look at the online list.
    /// </summary>
    private void EndSession(DateTimeOffset at) {
        // Only when it can only be one profile: the line names the address the session came from, not the
        // account, so two people playing at once is not something this can be told apart.
        if (_online.Count != 1) {
            return;
        }

        var profile = _store.Profiles.FirstOrDefault(p => p.AccountId == _online[0]);
        if (profile is null || profile.LastSeenOnlineAt is null) {
            return;
        }

        Credit(profile, at);
        profile.LastSeenOnlineAt = null;
        profile.LastPlayedAt = at;
    }

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

            // -------------------------------------------------------- a game session ended
            case ServerConnectionLost { Game: true } connectionLost:
                EndSession(connectionLost.At);
                return true;

            // -------------------------------------------------------- a zone began loading
            case ZoneLoadStarted started:
                _loadingZone = started.Path;
                return false;

            // -------------------------------------------------------- a loaded zone was named
            case ZoneNamed named:
                return LearnZoneWorld(named, earned);

            // -------------------------------------------------------- a creature joined a duel
            case DuelCreature creature:
                NoteDuelCreature(creature);
                return true;

            // -------------------------------------------------------- a duel finished
            case DuelFinished duel: {
                var creatures = ForgetDuel(duel.DuelId);
                var profile = SingleOnlineProfile();

                if (profile is null) {
                    UncreditedDuels++;
                    return true;
                }

                if (duel.PlayersWon) {
                    profile.DuelsWon++;
                    CreditCreatures(profile, creatures);

                    // Which of them were bosses is the creature list's business, so it is re-counted here
                    // once that list is known - a duel won before it was read still counts.
                    if (_roster is { } roster) {
                        profile.ClassifyBeatenCreatures(roster.Bosses, roster.Creatures);
                    }
                }
                else {
                    profile.DuelsLost++;
                }

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
    /// Counts the time a profile has been signed in, from whether the server's online list holds it now.
    /// </summary>
    private void ApplyPresence(LauncherProfile profile, bool online, DateTimeOffset at) {
        if (!online) {
            if (profile.LastSeenOnlineAt is not null) {
                // Signed out between two looks. The time since the last look is not credited: the account
                // was there then, and signing out could have been a second later - so playtime is measured
                // between the looks that saw the account online at both ends, and each session is accurate
                // to within one look rather than padded to it.
                profile.LastPlayedAt = at;
                profile.LastSeenOnlineAt = null;
            }

            return;
        }

        if (profile.LastSeenOnlineAt is not null) {
            // A gap longer than the poll interval means this launcher was not watching for a while, so it
            // counts as a new session rather than as a continuation nobody saw.
            if (at - profile.LastSeenOnlineAt.Value > MaxCreditedGap) {
                profile.SessionCount++;
            }

            Credit(profile, at);
        }
        else {
            profile.SessionCount++;
            profile.FirstPlayedAt ??= at;
        }

        profile.LastSeenOnlineAt = at;
        profile.LastPlayedAt = at;
    }

    /// <summary>Adds the time since the last sighting, capped at one poll's worth.</summary>
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

    /// <summary>Records that a creature took part in a duel, ready for the duel's ending.</summary>
    private void NoteDuelCreature(DuelCreature creature) {
        if (!_duelCreatures.TryGetValue(creature.DuelId, out var creatures)) {
            // Oldest first, so a duel that never ended cannot crowd out the ones that still might.
            while (_duelCreatures.Count >= MaxTrackedDuels) {
                _duelCreatures.Remove(_duelCreatures.Keys.First());
            }

            creatures = [];
            _duelCreatures[creature.DuelId] = creatures;
        }

        if (!creatures.Contains(creature.TemplateId)) {
            creatures.Add(creature.TemplateId);
        }
    }

    /// <summary>Takes a duel's creature list, once its ending has been read.</summary>
    private IReadOnlyList<uint> ForgetDuel(string duelId)
        => _duelCreatures.Remove(duelId, out var creatures) ? creatures : [];

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
    /// <param name="bosses">The creatures the list names as bosses.</param>
    /// <param name="creatures">Every creature the list has.</param>
    /// <returns>The achievements this made true, so the caller can say so.</returns>
    public IReadOnlyList<ProfileAward> UseCreatureRoster(
        IReadOnlySet<uint> bosses, IReadOnlySet<uint> creatures, DateTimeOffset at) {
        _roster = (bosses, creatures);

        var earned = new List<ProfileAward>();

        foreach (var profile in _store.Profiles) {
            profile.ClassifyBeatenCreatures(bosses, creatures);

            // Nothing is quoted, for the same reason as the database pass: reading a creature list is not
            // something that happened to this badge, and the badges it turns on say what they take.
            Award(profile, at, string.Empty, earned);
        }

        _store.Save();
        return earned;
    }

    /// <summary>
    /// Marks every creature of a won duel as beaten by the profile.
    /// </summary>
    /// <remarks>
    /// Already-beaten creatures are left alone so the list stays a list of what was beaten rather than a
    /// count of how many times, and nothing is ever removed from it.
    /// </remarks>
    private static void CreditCreatures(LauncherProfile profile, IReadOnlyList<uint> creatures) {
        foreach (var creature in creatures) {
            if (!profile.CreaturesDefeated.Contains(creature)) {
                profile.CreaturesDefeated.Add(creature);
            }
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
