using System.Runtime.InteropServices;

namespace ViewLight.Services;

/// <summary>
/// The chime that plays when an achievement is earned.
/// </summary>
/// <remarks>
/// <para>
/// The sound is <b>generated</b> rather than shipped as a file, so the launcher stays a single executable
/// with no assets to lose, and the same three-note rise is heard on every machine regardless of which
/// system sounds happen to be installed. It is a plain 16-bit PCM wave held in memory and handed to
/// Windows' own <c>PlaySound</c>.
/// </para>
/// <para>
/// <b>Sound is a nicety, never a failure.</b> Everything here is best-effort and wrapped: a machine with no
/// audio device, a locked-down session that refuses the call, or a platform that is not Windows at all
/// simply gets no chime, and nothing anywhere else changes. Nothing about earning a badge depends on it.
/// </para>
/// </remarks>
public static class AchievementSound {

    // winmm.dll's flags. SND_MEMORY says the pointer is the wave itself; SND_ASYNC says do not block the
    // caller while it plays, which matters because this is called from the UI thread.
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_MEMORY = 0x0004;

    // The fallback when the generated chime cannot be played: the standard Windows information sound.
    private const uint MB_ICONASTERISK = 0x00000040;

    /// <summary>
    /// Whether a chime is played at all. On by default; the launcher's own tests turn it off so a test run
    /// does not depend on - or make noise through - the machine's audio.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    // Held for the lifetime of the process because SND_ASYNC reads it after this call returns. A static
    // field is what keeps it alive; without it the pointer would dangle mid-playback.
    private static readonly byte[] s_chime = BuildChime();

    /// <summary>Plays the chime, or does nothing when it cannot be played.</summary>
    public static void Play() {
        if (!Enabled || !OperatingSystem.IsWindows()) {
            return;
        }

        try {
            if (PlaySound(s_chime, IntPtr.Zero, SND_MEMORY | SND_ASYNC)) {
                return;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                      or BadImageFormatException) {
            // Falls through to the simpler sound below.
        }

        try {
            _ = MessageBeep(MB_ICONASTERISK);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) {
            // No audio at all. That is fine; the toast is the notification, the sound is a garnish.
        }
    }

    /// <summary>
    /// A short rising three-note chime, as a complete RIFF wave in memory.
    /// </summary>
    /// <remarks>
    /// Written out by hand rather than decoded, so there is nothing to load and nothing to go missing. The
    /// notes are a major triad an octave above middle C - bright, unmistakably an announcement, and under
    /// half a second so it does not talk over anything.
    /// </remarks>
    public static byte[] BuildChime() {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;

        int[] notes = [1046, 1318, 1568]; // C6, E6, G6
        const int noteMs = 110;

        var samples = new List<short>(sampleRate * noteMs * notes.Length / 1000);

        foreach (var frequency in notes) {
            var count = sampleRate * noteMs / 1000;

            for (var i = 0; i < count; i++) {
                var t = (double) i / sampleRate;

                // A short attack and a longer decay, so each note speaks and fades rather than clicking at
                // either end. The last note is left to ring a touch longer than the others.
                var progress = (double) i / count;
                var envelope = Math.Min(1.0, progress / 0.08) * (1.0 - progress);

                var value = Math.Sin(2 * Math.PI * frequency * t) * envelope * 0.35;
                samples.Add((short) (value * short.MaxValue));
            }
        }

        // Let the final note fade rather than cut off.
        for (var i = 0; i < sampleRate * 120 / 1000; i++) {
            samples.Add(0);
        }

        var dataBytes = samples.Count * sizeof(short);

        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                                             // PCM header size
        writer.Write((short) 1);                                      // PCM
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);      // byte rate
        writer.Write((short) (channels * bitsPerSample / 8));         // block align
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples) {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(byte[] wave, IntPtr module, uint flags);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);
}
