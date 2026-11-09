using MediaBrowser.Controller.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomeScreenCompanion
{
    /// <summary>
    /// Gentle mode: while someone is watching, a run does its many-item writes (item saves,
    /// merges, collection members, playlist changes, home rows for many users) in small slices
    /// with a pause between them, so Emby's database is never busy long enough for a stream to
    /// stall. With nobody watching every slice runs straight on, as fast as before.
    ///
    /// Heavy loops call <see cref="Wrote"/> (or <see cref="WroteAsync"/>) after each write that
    /// really changed something. Every <see cref="SliceSize"/> writes the streams are counted
    /// again: none means go on, any means pause <see cref="Pause"/>. A batch call (one Emby call
    /// that saves many items) is split with <see cref="Slices"/> first.
    ///
    /// The numbers come from the test server: 4 saves then 3 s kept segment times at their
    /// idle level with 4 transcoding streams (see the PR for the measurements).
    /// </summary>
    internal static class GentleMode
    {
        internal const int SliceSize = 4;
        internal static readonly TimeSpan Pause = TimeSpan.FromSeconds(3);

        private static ISessionManager? _sessions;
        private static readonly object Sync = new object();
        private static int _pending, _pauses, _streamsAtStart, _maxStreams;
        private static TimeSpan _paused = TimeSpan.Zero;

        internal static void Initialize(ISessionManager sessionManager) => _sessions = sessionManager;

        /// <summary>Sessions playing something right now (paused ones do not count: they fetch nothing).</summary>
        internal static int ActiveStreams()
        {
            var sessions = _sessions;
            if (sessions == null) return 0;
            try
            {
                return sessions.Sessions.Count(s => s != null && s.NowPlayingItem != null && !(s.PlayState?.IsPaused ?? false));
            }
            catch (Exception swallowed) { PluginLog.Swallowed(swallowed); return 0; }
        }

        internal static bool Streaming => ActiveStreams() > 0;

        /// <summary>A run starts (RunGate): counters back to zero.</summary>
        internal static void Begin()
        {
            lock (Sync)
            {
                _pending = 0; _pauses = 0; _paused = TimeSpan.Zero;
                _streamsAtStart = ActiveStreams();
                _maxStreams = _streamsAtStart;
            }
        }

        /// <summary>The run's header: one plain line when people are watching as it starts.</summary>
        internal static void Announce(RunLog? log)
        {
            int n;
            lock (Sync) n = _streamsAtStart;
            if (n > 0 && log != null)
                log.Info($"  {(n == 1 ? "1 person is" : $"{n} people are")} streaming: the sync runs in gentle mode and takes longer.");
        }

        /// <summary>For the run's summary: how much gentle mode slowed it down; null when it never paused.</summary>
        internal static string? SummaryLine()
        {
            lock (Sync)
            {
                if (_pauses == 0) return null;
                return $"Gentle mode:   {_pauses + 1} slices, {RunLog.Elapsed(_paused)} of pauses ({RunLog.Plural(_pauses, "pause")} of {Pause.TotalSeconds:0}s) while up to {(_maxStreams == 1 ? "1 person was" : $"{_maxStreams} people were")} streaming";
            }
        }

        /// <summary>
        /// Call after a write (or <paramref name="count"/> writes) that changed something. Pauses
        /// when a slice is full and someone is streaming. Throws when cancelled.
        /// </summary>
        internal static void Wrote(CancellationToken cancellationToken, int count = 1)
        {
            var wait = Due(count);
            if (wait > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(wait))
                cancellationToken.ThrowIfCancellationRequested();
        }

        internal static async Task WroteAsync(CancellationToken cancellationToken, int count = 1)
        {
            var wait = Due(count);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The ids of one batch call in slices: one slice when nobody is watching.</summary>
        internal static IEnumerable<T[]> Slices<T>(IReadOnlyList<T> items)
        {
            int i = 0;
            while (i < items.Count)
            {
                int size = Streaming ? SliceSize : items.Count - i;
                var slice = new T[Math.Min(size, items.Count - i)];
                for (int k = 0; k < slice.Length; k++) slice[k] = items[i + k];
                i += slice.Length;
                yield return slice;
            }
        }

        // How long to pause now: zero until a slice is full; then the streams decide.
        private static TimeSpan Due(int count)
        {
            if (count <= 0) return TimeSpan.Zero;
            lock (Sync)
            {
                _pending += count;
                if (_pending < SliceSize) return TimeSpan.Zero;
                _pending = 0;
            }
            int streams = ActiveStreams();
            if (streams == 0) return TimeSpan.Zero;
            lock (Sync)
            {
                _pauses++;
                _paused += Pause;
                if (streams > _maxStreams) _maxStreams = streams;
            }
            return Pause;
        }
    }
}
