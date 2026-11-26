using System;
using System.Collections.Generic;
using System.IO;

namespace XwvmChatter.Tests
{
    /// <summary>A throwaway voices folder laid out like the real one:
    ///   _chatter/*.xml                       who flies where, with which voice
    ///   VOICE/_chatter/&lt;voice&gt;/bank.xml     that voice's lines
    ///   VOICE/_chatter/&lt;voice&gt;/*.ogg        its clips (empty files here - nothing decodes them)</summary>
    public sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "xwvm-chatter-tests-" + Guid.NewGuid().ToString("N"));
        public readonly List<string> Log = new List<string>();

        public Fixture() { Directory.CreateDirectory(Path.Combine(Root, "_chatter")); }

        public void Casting(string fileName, string xml) => File.WriteAllText(Path.Combine(Root, "_chatter", fileName), xml);

        public string VoiceDir(string slug)
        {
            var d = Path.Combine(Root, "VOICE", "_chatter", slug);
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Write a voice's bank.xml and an empty clip for every name in <paramref name="clips"/>.</summary>
        public void Bank(string slug, string linesXml, params string[] clips)
        {
            var d = VoiceDir(slug);
            File.WriteAllText(Path.Combine(d, "bank.xml"), "<bank>" + linesXml + "</bank>");
            foreach (var c in clips) File.WriteAllBytes(Path.Combine(d, c + ".ogg"), Array.Empty<byte>());
        }

        public ChatterConfig Load(string mission) => ChatterConfig.Load(Root, mission, Log.Add);

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
