using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NVorbis;
using UnityEngine;

namespace XwvmChatter
{
    /// <summary>Builds our clips without UnityWebRequest.
    ///
    /// Unity finishing a UnityWebRequestMultimedia request costs 8-9 ms of main-thread time per clip however many are in
    /// flight, which is most of a minute for a mission with several thousand clips. Here the file is read and its Vorbis
    /// header parsed on a worker thread; the main thread only calls AudioClip.Create. That follows the game's own pattern
    /// for audio it decodes itself (GameResourceLibrary.LoadSFXAudioClip: AudioClip.Create + SetData) - a stated
    /// deviation from how the game loads EXTERNAL files (AudioClipHelper.LoadAudioClipFromFile, UnityWebRequest), which is
    /// the cost we are removing. The clip is created streamed (PCMReaderCallback): Unity pulls PCM as it plays, decoded
    /// from the compressed bytes we keep, so memory stays near the .ogg size (a fully decoded set would be several
    /// hundred MB). The game's radio plays it as it plays any clip (audioSource.clip = clip; Play(); it may set
    /// audioSource.time on resume, which arrives as a set-position callback and seeks the decoder).
    ///
    /// NVorbis (MIT) is embedded in XwvmChatter.dll as a resource and resolved from there, so the release stays one DLL.</summary>
    internal static partial class OggDirect
    {
        // ------------------------------------------------------------------ the embedded decoder

        private static bool _resolverInstalled;

        /// <summary>Call from Plugin.Awake, before anything touches an NVorbis type.</summary>
        internal static void InstallResolver()
        {
            if (_resolverInstalled) return;
            _resolverInstalled = true;
            AppDomain.CurrentDomain.AssemblyResolve += (sender, request) =>
            {
                if (!request.Name.StartsWith("NVorbis", StringComparison.OrdinalIgnoreCase)) return null;
                using (var resource = typeof(OggDirect).Assembly.GetManifestResourceStream("NVorbis.dll"))
                {
                    if (resource == null) return null;
                    var assemblyBytes = new byte[resource.Length];
                    int filled = 0;
                    while (filled < assemblyBytes.Length)
                    {
                        int read = resource.Read(assemblyBytes, filled, assemblyBytes.Length - filled);
                        if (read <= 0) break;
                        filled += read;
                    }
                    return Assembly.Load(assemblyBytes);
                }
            };
        }

        // ------------------------------------------------------------------ worker thread: read and parse

        /// <summary>A clip file read and its header parsed, ready for <see cref="Build"/>.</summary>
        internal sealed class Prepared
        {
            internal byte[] Bytes;
            internal int Channels, Rate;
            internal long Samples;
            /// <summary>Why it cannot be built; null when it can.</summary>
            internal string Error;
            /// <summary>The first decoded samples, which AudioClip.Create asks for at once (see Prepare).</summary>
            internal float[] Prefix;
            internal int PrefixLength;
        }

        /// <summary>How many floats AudioClip.Create pulls up front. Learned: raised when Unity asks for more.</summary>
        private static volatile int _prefillFloats = 38400;

        /// <summary>Worker thread: read the file and its Vorbis header (channels, rate, length).</summary>
        internal static Task<Prepared> PrepareAsync(string path) => Task.Run(() => Prepare(path));

        private static Prepared Prepare(string path)
        {
            var prepared = new Prepared();
            try
            {
                var timer = Stopwatch.StartNew();
                prepared.Bytes = ClipCache.Bytes.TryGetValue(path, out var preloaded) ? preloaded : File.ReadAllBytes(path);
                long afterRead = timer.ElapsedTicks;
                using (var reader = new VorbisReader(new MemoryStream(prepared.Bytes, false), true))
                {
                    long afterOpen = timer.ElapsedTicks;
                    prepared.Channels = reader.Channels; prepared.Rate = reader.SampleRate;
                    prepared.Samples = (long)Math.Round(reader.TotalTime.TotalSeconds * reader.SampleRate);
                    long afterLength = timer.ElapsedTicks;
                    // AudioClip.Create(stream: true) pre-fills its stream buffer by calling the reader right there, on the
                    // main thread (several ms of decode per clip). Decode that prefix here instead; the clip takes it
                    // during Create and drops it, so it never stays in memory.
                    int wanted = (int)Math.Min(prepared.Samples * prepared.Channels, _prefillFloats);
                    wanted -= wanted % Math.Max(1, prepared.Channels);
                    prepared.Prefix = new float[wanted];
                    while (prepared.PrefixLength < wanted)
                    {
                        int read = reader.ReadSamples(prepared.Prefix, prepared.PrefixLength, wanted - prepared.PrefixLength);
                        if (read <= 0) break;
                        prepared.PrefixLength += read;
                    }
                    long afterPrefill = timer.ElapsedTicks;
                    Interlocked.Add(ref _workerReadTicks, afterRead); Interlocked.Add(ref _workerOpenTicks, afterOpen - afterRead);
                    Interlocked.Add(ref _workerLengthTicks, afterLength - afterOpen); Interlocked.Add(ref _workerPrefillTicks, afterPrefill - afterLength);
                    Interlocked.Increment(ref _workerClips);
                }
                if (prepared.Channels <= 0 || prepared.Rate <= 0 || prepared.Samples <= 0)
                    prepared.Error = $"bad header ({prepared.Channels} ch, {prepared.Rate} Hz, {prepared.Samples} samples)";
            }
            catch (Exception e) { prepared.Error = e.Message; }
            return prepared;
        }

        // ------------------------------------------------------------------ main thread: the clip

        /// <summary>Main thread: the streamed clip.</summary>
        internal static AudioClip Build(string name, Prepared prepared)
        {
            var source = new Source(name, prepared.Bytes, prepared.Channels, prepared.Prefix, prepared.PrefixLength);
            prepared.Prefix = null;
            _inCreate = true; _createReads = 0; _createFloats = 0; _createReadMs = 0;
            try { return AudioClip.Create(name, (int)Math.Min(int.MaxValue, prepared.Samples), prepared.Channels, prepared.Rate, true, source.Read, source.SetPosition); }
            finally
            {
                _inCreate = false; source.ReleasePrefix(); CreateProbe.Add(_createReads, _createFloats, _createReadMs);
                if (_createFloats > _prefillFloats) _prefillFloats = _createFloats;   // Unity asked for more: decode more next time
            }
        }

        // ------------------------------------------------------------------ measurements (the loading screen's [load] line)

        // Per-clip time on the worker thread, summed in Stopwatch ticks.
        private static long _workerReadTicks, _workerOpenTicks, _workerLengthTicks, _workerPrefillTicks;
        private static int _workerClips;

        internal static void ResetWorkerStats() { _workerReadTicks = _workerOpenTicks = _workerLengthTicks = _workerPrefillTicks = 0; _workerClips = 0; }

        internal static string WorkerSummary()
        {
            if (_workerClips == 0) return "";
            double msPerClip = 1000.0 / Stopwatch.Frequency / _workerClips;
            return $"; worker per clip: read {_workerReadTicks * msPerClip:0.00} ms, reader {_workerOpenTicks * msPerClip:0.00} ms, length {_workerLengthTicks * msPerClip:0.00} ms, prefill {_workerPrefillTicks * msPerClip:0.00} ms" +
                   $" (threadpool min {ThreadPoolLimits()})";
        }

        private static string ThreadPoolLimits()
        {
            ThreadPool.GetMinThreads(out int minWorkers, out _);
            ThreadPool.GetMaxThreads(out int maxWorkers, out _);
            return $"{minWorkers}, max {maxWorkers}";
        }

        // What Unity asks of the reader callback during the AudioClip.Create in progress (main thread).
        [ThreadStatic] private static bool _inCreate;
        private static int _createReads, _createFloats;
        private static double _createReadMs;

        /// <summary>What AudioClip.Create pulled through the reader callback, averaged over the clips built.</summary>
        internal static class CreateProbe
        {
            private static int _clips, _reads, _floats;
            private static double _readMs;

            internal static void Add(int reads, int floats, double readMs) { _clips++; _reads += reads; _floats += floats; _readMs += readMs; }
            internal static string Summary() => _clips == 0 ? "" : $"; Create: {_reads / (double)_clips:0.0} read(s) / {_floats / (double)_clips:0} floats / reader+decode {_readMs / _clips:0.00} ms avg per clip";
            internal static void Reset() { _clips = _reads = _floats = 0; _readMs = 0; }
        }

        // ------------------------------------------------------------------ one clip's decoder

        /// <summary>One clip's decoder. Unity calls these on its audio thread; the reader exists only while the clip plays
        /// (made on first use, dropped at the end) so thousands of idle clips hold just their compressed bytes.</summary>
        private sealed partial class Source
        {
            private readonly string _name;
            private readonly byte[] _bytes;
            private readonly int _channels;
            private readonly object _lock = new object();
            private VorbisReader _reader;
            private long _position;          // in sample frames
            private float[] _prefix;
            private int _prefixLength;

            // Extension points. Each is a no-op unless extra source files implement it.
            partial void TraceCreated();     // AudioClip.Create has returned: callbacks from here on are playback
            partial void TraceSetPosition(int frame);
            partial void TraceRead(int length);

            internal Source(string name, byte[] bytes, int channels, float[] prefix, int prefixLength)
            {
                _name = name; _bytes = bytes; _channels = channels; _prefix = prefix; _prefixLength = prefixLength;
            }

            internal void ReleasePrefix() { lock (_lock) { _prefix = null; _prefixLength = 0; TraceCreated(); } }

            private VorbisReader Reader()
            {
                if (_reader == null)
                {
                    _reader = new VorbisReader(new MemoryStream(_bytes, false), true);
                    if (_position > 0) _reader.DecodedPosition = _position;
                }
                return _reader;
            }

            internal void SetPosition(int frame)
            {
                lock (_lock)
                {
                    TraceSetPosition(frame);
                    try
                    {
                        _position = Math.Max(0, frame);
                        if (_reader != null && _reader.DecodedPosition != _position) _reader.DecodedPosition = _position;
                    }
                    catch { DropReader(); }
                }
            }

            internal void Read(float[] data)
            {
                lock (_lock)
                {
                    TraceRead(data.Length);
                    int filled = 0;
                    var createTimer = _inCreate ? Stopwatch.StartNew() : null;
                    try
                    {
                        // the prefix decoded on the worker thread answers AudioClip.Create's first reads
                        if (_prefix != null && _reader == null)
                        {
                            long prefixOffset = _position * _channels;
                            if (prefixOffset < _prefixLength)
                            {
                                int copied = (int)Math.Min(data.Length, _prefixLength - prefixOffset);
                                Array.Copy(_prefix, prefixOffset, data, 0, copied);
                                filled = copied; _position += copied / Math.Max(1, _channels);
                            }
                        }
                        var reader = filled < data.Length ? Reader() : null;

                        int filledFromPrefix = filled;
                        while (reader != null && filled < data.Length)
                        {
                            int read = reader.ReadSamples(data, filled, data.Length - filled);
                            if (read <= 0) break;
                            filled += read;
                        }
                        _position += (filled - filledFromPrefix) / Math.Max(1, _channels);
                    }
                    catch { filled = 0; }
                    if (createTimer != null) { _createReads++; _createFloats += data.Length; _createReadMs += createTimer.Elapsed.TotalMilliseconds; }
                    // the end of the clip (or a decode error): silence for the rest, and the reader goes until the next play
                    if (filled < data.Length) { Array.Clear(data, filled, data.Length - filled); DropReader(); _position = 0; }
                }
            }

            private void DropReader() { try { _reader?.Dispose(); } catch { } _reader = null; }
        }
    }
}
