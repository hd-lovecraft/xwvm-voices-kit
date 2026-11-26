using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XwvmChatter.Tests
{
    public class PoolDealTests
    {
        private static Speaker Pool(string id, string voice) => new Speaker { Id = id, Role = "generic", Voice = voice };
        private static Speaker Cast(string seat, string voice) => new Speaker { Id = seat, Designation = seat, Voice = voice };
        private static ChatterConfig Config(params Speaker[] speakers)
        {
            var cfg = new ChatterConfig { MissionName = "TEST" };
            foreach (var s in speakers) cfg.Speakers[s.Id] = s;
            return cfg;
        }
        private static readonly Func<Speaker, string, bool> NobodyCovers = (sp, cs) => false;

        [Fact]
        public void AVoiceCastInTheMissionLeavesThePool()
        {
            var cfg = Config(Pool("g1", "Anna"), Pool("g0", "Boris"), Cast("Red 2", "Anna"));
            Assert.Equal(new[] { "Boris" }, PoolDeal.PoolOf(cfg).Select(s => s.Voice));
        }

        [Fact]
        public void IfEveryPoolVoiceIsCastTheWholePoolIsUsedRatherThanNobody()
        {
            var cfg = Config(Pool("g0", "Anna"), Cast("Red 2", "Anna"));
            Assert.Equal(new[] { "Anna" }, PoolDeal.PoolOf(cfg).Select(s => s.Voice));
        }

        [Fact]
        public void ThePoolIsOrderedById()
        {
            var cfg = Config(Pool("g2", "C"), Pool("g0", "A"), Pool("g1", "B"));
            Assert.Equal(new[] { "g0", "g1", "g2" }, PoolDeal.PoolOf(cfg).Select(s => s.Id));
        }

        [Fact]
        public void TheRosterIsTheUncastSeatsInNaturalOrder()
        {
            var cfg = Config(Cast("Red Leader", "Anna"));
            var roster = PoolDeal.UncastRoster(new List<string> { "Red 10", "Red 2", "Red 1", "Blue 3" }, cfg);
            Assert.Equal(new[] { "Blue 3", "Red 2", "Red 10" }, roster);      // "Red 1" is Red Leader's seat: cast
        }

        [Theory]
        [InlineData("Red 2", "Red 10", -1)]
        [InlineData("Red 10", "Red 2", 1)]
        [InlineData("Blue 9", "Red 1", -1)]
        [InlineData("red 2", "RED 2", 0)]
        public void NaturalOrder(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(PoolDeal.NaturalCompare(a, b)));

        [Fact]
        public void NobodySharesAVoiceWhileAnotherSitsIdle()
        {
            var pool = new List<Speaker> { Pool("g0", "A"), Pool("g1", "B"), Pool("g2", "C") };
            var roster = new List<string> { "Red 2", "Red 3", "Red 4" };
            var map = PoolDeal.Deal(pool, roster, "T1M01XW", NobodyCovers, out _, out var covered);
            Assert.Equal(3, map.Values.Distinct().Count());
            Assert.Equal(0, covered);
        }

        [Fact]
        public void TheSameMissionAlwaysDealsTheSameVoices()
        {
            var pool = new List<Speaker> { Pool("g0", "A"), Pool("g1", "B"), Pool("g2", "C") };
            var roster = new List<string> { "Red 2", "Red 3", "Red 4", "Red 5" };
            var first = PoolDeal.Deal(pool, roster, "T1M01XW", NobodyCovers, out var offset1, out _);
            var again = PoolDeal.Deal(pool, roster, "T1M01XW", NobodyCovers, out var offset2, out _);
            Assert.Equal(offset1, offset2);
            Assert.All(roster, seat => Assert.Same(first[seat], again[seat]));
        }

        [Fact]
        public void RotationStartsAtTheOffsetAndWrapsRound()
        {
            var pool = new List<Speaker> { Pool("g0", "A"), Pool("g1", "B") };
            var roster = new List<string> { "Red 2", "Red 3", "Red 4" };
            var map = PoolDeal.Deal(pool, roster, "ANY", NobodyCovers, out var offset, out _);
            Assert.Same(pool[offset % 2], map["Red 2"]);
            Assert.Same(pool[(offset + 1) % 2], map["Red 3"]);
            Assert.Same(pool[(offset + 2) % 2], map["Red 4"]);
        }

        [Fact]
        public void ASeatGoesToAVoiceThatHasItsSelfIdLines()
        {
            var a = Pool("g0", "A"); var b = Pool("g1", "B");
            var pool = new List<Speaker> { a, b };
            // only B recorded "This is Red Two / Red Three"
            var map = PoolDeal.Deal(pool, new List<string> { "Red 2", "Red 3" }, "ANY", (sp, cs) => ReferenceEquals(sp, b), out _, out var covered);
            Assert.Same(b, map["Red 2"]);
            Assert.Same(b, map["Red 3"]);
            Assert.Equal(2, covered);
        }

        [Fact]
        public void AmongVoicesThatCoverASeatTheLeastUsedGoesFirst()
        {
            var a = Pool("g0", "A"); var b = Pool("g1", "B");
            var map = PoolDeal.Deal(new List<Speaker> { a, b }, new List<string> { "Red 2", "Red 3" }, "ANY", (sp, cs) => true, out _, out _);
            Assert.Same(a, map["Red 2"]);
            Assert.Same(b, map["Red 3"]);
        }

        [Fact]
        public void CoverageIsReadFromTheVoicesOwnFolder()
        {
            var manifest = new VoiceManifest();
            manifest.Versions.Add(new CallsignVersion { Kind = "selfid", Callsign = "Blue Leader", AudioFile = "x" });
            manifest.Versions.Add(new CallsignVersion { Kind = "address", Callsign = "Red 2", AudioFile = "x" });
            var manifests = new Dictionary<string, VoiceManifest> { [Path.Combine("voices", "jon-marek")] = manifest };
            var jon = Pool("g0", "Jon Marek");
            Assert.True(PoolDeal.CoversSelfId(manifests, jon, "Blue 1"));        // Blue 1 is Blue Leader
            Assert.False(PoolDeal.CoversSelfId(manifests, jon, "Red 2"));        // that one only addresses Red 2
            Assert.False(PoolDeal.CoversSelfId(manifests, Pool("g1", "Someone Else"), "Blue 1"));
        }

        [Fact]
        public void TheHashIsTheStandardFnv1a()
        {
            Assert.Equal(2166136261u, PoolDeal.Fnv1a(""));
            Assert.Equal(0xE40C292Cu, PoolDeal.Fnv1a("a"));
        }
    }

    public class LineHistoryTests
    {
        private static Line L(string text, string take = null) => new Line { Situation = "UnderAttack", Text = text, AudioFile = take ?? text + "-t1.ogg" };
        private const string Sit = "UnderAttack";

        [Fact]
        public void NothingHeardYetMeansEveryLineIsEligible()
        {
            var pool = new List<Line> { L("a"), L("b") };
            Assert.Equal(pool, new LineHistory().Fresh(pool, Sit));
        }

        [Fact]
        public void ALineNotYetHeardBeatsOneThatWas()
        {
            var a = L("a"); var b = L("b"); var c = L("c");
            var h = new LineHistory();
            h.Remember(Sit, a);
            Assert.Equal(new[] { b, c }, h.Fresh(new List<Line> { a, b, c }, Sit));
        }

        [Fact]
        public void TheWholeSetPlaysBeforeAnythingRepeats()
        {
            var pool = new List<Line> { L("a"), L("b"), L("c"), L("d"), L("e") };
            var h = new LineHistory(); var heard = new List<string>();
            for (int i = 0; i < pool.Count; i++)
            {
                var pick = h.Fresh(pool, Sit).Last();          // whichever the caller picks among the eligible
                heard.Add(pick.Text); h.Remember(Sit, pick);
            }
            Assert.Equal(5, heard.Distinct().Count());
        }

        [Fact]
        public void OnceAllWereHeardOnlyTheHalfHeardLongestAgoIsEligible()
        {
            var a = L("a"); var b = L("b"); var c = L("c"); var d = L("d");
            var h = new LineHistory();
            foreach (var l in new[] { a, b, c, d }) h.Remember(Sit, l);
            Assert.Equal(new[] { a, b }, h.Fresh(new List<Line> { d, c, b, a }, Sit).OrderBy(l => l.Text));
        }

        [Fact]
        public void TakesOfOneScriptLineCountAsTheSameLine()
        {
            var a1 = L("a", "a-t1.ogg"); var a2 = L("a", "a-t2.ogg"); var b = L("b");
            var h = new LineHistory();
            h.Remember(Sit, a1);
            Assert.Equal(new[] { b }, h.Fresh(new List<Line> { a1, a2, b }, Sit));
        }

        [Fact]
        public void WhenEveryLineHasTheSameWordsAtLeastTheTakeChanges()
        {
            var a1 = L("a", "a-t1.ogg"); var a2 = L("a", "a-t2.ogg");
            var h = new LineHistory();
            h.Remember(Sit, a1);
            Assert.Equal(new[] { a2 }, h.Fresh(new List<Line> { a1, a2 }, Sit));
        }

        [Fact]
        public void ASingleLineIsAlwaysEligible()
        {
            var a = L("a"); var h = new LineHistory();
            h.Remember(Sit, a);
            Assert.Equal(new[] { a }, h.Fresh(new List<Line> { a }, Sit));
        }

        [Fact]
        public void ACallsignVersionCountsAsTheScriptLineItStandsFor()
        {
            var plain = L("Good shooting."); var other = L("Nice shot.");
            var version = new Line { Situation = Sit, Text = "Good shooting, Red One.", AudioFile = "v.ogg", BaseLine = plain };
            var h = new LineHistory();
            h.Remember(Sit, version);
            Assert.Equal(new[] { other }, h.Fresh(new List<Line> { plain, other }, Sit));
        }

        [Fact]
        public void EachSituationHasItsOwnMemory()
        {
            var a = L("a"); var b = L("b"); var h = new LineHistory();
            h.Remember("KillReport", a);
            Assert.Equal(2, h.Fresh(new List<Line> { a, b }, Sit).Count);
        }
    }

    public class LineTagsTests
    {
        [Theory]
        [InlineData(4, false, "tie fighter")]
        [InlineData(5, false, "tie interceptor")]
        [InlineData(7, false, "gunboat")]
        [InlineData(16, true, "stardestroyer capital")]
        [InlineData(999, false, "other")]
        [InlineData(999, true, "capital")]
        public void CraftTypeToTagWords(int typeId, bool big, string tags) =>
            Assert.Equal(tags.Split(' '), LineTags.CraftClasses(typeId, big));

        private static Line Tagged(string attacker = null, string threat = null, string arrival = null)
        {
            var l = new Line();
            if (attacker != null) l.Attacker.AddRange(attacker.Split(' '));
            if (threat != null) l.Threat.AddRange(threat.Split(' '));
            if (arrival != null) l.Arrival.AddRange(arrival.Split(' '));
            return l;
        }
        private static readonly List<string> Interceptor = new List<string> { "tie", "interceptor" };

        [Fact] public void AnUntaggedLineFitsAnyMoment() => Assert.True(LineTags.Fit(new Line(), null, null, null));
        [Fact] public void AnyOneAttackerTagIsEnough() => Assert.True(LineTags.Fit(Tagged(attacker: "bomber interceptor"), Interceptor, null, null));
        [Fact] public void TheWrongAttackerDoesNotFit() => Assert.False(LineTags.Fit(Tagged(attacker: "bomber"), Interceptor, null, null));
        [Fact] public void ATaggedLineDoesNotFitAnUnknownAttacker() => Assert.False(LineTags.Fit(Tagged(attacker: "tie"), null, null, null));
        [Fact] public void ThreatMustMatch() => Assert.False(LineTags.Fit(Tagged(threat: "multiple"), Interceptor, "new", null));
        [Fact] public void EveryTagListTheLineCarriesMustMatch() => Assert.False(LineTags.Fit(Tagged(attacker: "tie", threat: "multiple"), Interceptor, "new", null));
        [Fact] public void AnArrivalMayCarrySeveralWords() => Assert.True(LineTags.Fit(Tagged(arrival: "launched-stardestroyer"), null, null, "launched launched-stardestroyer launched-capital"));
        [Fact] public void APlainLaunchLineFitsEveryLaunch() => Assert.True(LineTags.Fit(Tagged(arrival: "launched"), null, null, "launched launched-deathstar"));
        [Fact] public void AHyperspaceLineDoesNotFitALaunch() => Assert.False(LineTags.Fit(Tagged(arrival: "hyper"), null, null, "launched"));
    }

    public class CallsignVersionsTests
    {
        private static readonly string Jon = Path.Combine("voices", "jon-marek");
        private static readonly string Mira = Path.Combine("voices", "mira-kessel");
        private static CallsignVersion V(string dir, string file, string kind = "address", string callsign = "Red 2") =>
            new CallsignVersion { Kind = kind, Callsign = callsign, AudioFile = Path.Combine(dir, file) };
        private static VoiceManifest Manifest(params CallsignVersion[] versions)
        {
            var manifest = new VoiceManifest(); manifest.Versions.AddRange(versions); return manifest;
        }

        [Theory]
        [InlineData("UnderAttack-3-t2.ogg", "UnderAttack-3-")]
        [InlineData("KillStreak2-1-t10.ogg", "KillStreak2-1-")]
        [InlineData("NiceShot-12-v4.ogg", "NiceShot-12-")]
        [InlineData("plain.ogg", "plain-")]
        public void ALinesPrefixIsItsClipNameWithoutTheLastPart(string clip, string prefix) =>
            Assert.Equal(prefix, CallsignVersions.LinePrefix(Path.Combine(Jon, clip)));

        [Fact]
        public void AVersionBelongsToTheLineNotToOneTake()
        {
            var v = V(Jon, "UnderAttack-3-v4--address--red-2.ogg");
            var idx = new CallsignVersions(); idx.Add(Manifest(v));
            Assert.Same(v, Assert.Single(idx.Of(Path.Combine(Jon, "UnderAttack-3-t1.ogg"))));
            Assert.Same(v, Assert.Single(idx.Of(Path.Combine(Jon, "UnderAttack-3-t7.ogg"))));
        }

        [Fact]
        public void Line1DoesNotPickUpLine10sVersions()
        {
            var idx = new CallsignVersions();
            idx.Add(Manifest(V(Jon, "UnderAttack-10-v4--address--red-2.ogg")));
            Assert.Empty(idx.Of(Path.Combine(Jon, "UnderAttack-1-t1.ogg")));
        }

        [Fact]
        public void AnotherVoicesVersionsAreNotThisVoices()
        {
            var idx = new CallsignVersions();
            idx.Add(Manifest(V(Mira, "UnderAttack-3-v4--address--red-2.ogg")));
            Assert.Empty(idx.Of(Path.Combine(Jon, "UnderAttack-3-t1.ogg")));
        }

        [Fact]
        public void FolderAndFileNamesMatchWhateverTheirCase()
        {
            var idx = new CallsignVersions();
            idx.Add(Manifest(V(Jon, "underattack-3-v4--address--red-2.ogg")));
            Assert.Single(idx.Of(Path.Combine("VOICES", "Jon-Marek", "UnderAttack-3-t1.ogg")));
        }

        [Fact]
        public void ALineWithoutAClipHasNoVersions() => Assert.Empty(new CallsignVersions().Of(new Line()));

        [Fact]
        public void VersionsAddedLaterAreFound()
        {
            var idx = new CallsignVersions();
            var line = Path.Combine(Jon, "UnderAttack-3-t1.ogg");
            Assert.Empty(idx.Of(line));
            idx.Add(Manifest(V(Jon, "UnderAttack-3-v4--address--red-2.ogg")));
            Assert.Single(idx.Of(line));
        }

        /// <summary>The index replaced a scan of every version, written out at each place that needed it. For any
        /// line it must give exactly what that scan gave, in the same order (a random pick depends on the order).</summary>
        [Fact]
        public void TheIndexGivesWhatTheOldFullScanGave()
        {
            var rnd = new Random(12345);
            var dirs = new[] { Jon, Mira, Path.Combine("voices", "tobin-ash") };
            var sits = new[] { "UnderAttack", "KillReport", "NiceShot", "KillStreak2" };
            var all = new List<CallsignVersion>(); var idx = new CallsignVersions();
            foreach (var d in dirs)
            {
                var vs = new List<CallsignVersion>();
                for (int i = 0; i < 300; i++)
                    vs.Add(V(d, $"{sits[rnd.Next(sits.Length)]}-{rnd.Next(1, 15)}-v4--{(rnd.Next(2) == 0 ? "address" : "selfid")}--red-{rnd.Next(1, 6)}-{i}.ogg"));
                all.AddRange(vs); idx.Add(Manifest(vs.ToArray()));
            }
            Assert.Equal(900, idx.Count);
            foreach (var d in dirs)
                foreach (var s in sits)
                    for (int n = 1; n < 15; n++)
                    {
                        var line = Path.Combine(d, $"{s}-{n}-t{rnd.Next(1, 9)}.ogg");
                        var take = Path.GetFileNameWithoutExtension(line);
                        var prefix = take.Substring(0, take.LastIndexOf('-')) + "-";
                        var scan = all.Where(c => string.Equals(Path.GetDirectoryName(c.AudioFile), Path.GetDirectoryName(line), StringComparison.OrdinalIgnoreCase)
                                                  && Path.GetFileName(c.AudioFile).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                        Assert.Equal(scan, idx.Of(line));
                    }
        }
    }

    public class SettingTextTests
    {
        [Theory]
        [InlineData("2.5-4.5", true, 2.5f, 4.5f)]
        [InlineData("3", true, 3f, 3f)]
        [InlineData("x", false, 0f, 0f)]
        [InlineData("", false, 0f, 0f)]
        [InlineData(null, false, 0f, 0f)]
        public void Range(string text, bool ok, float min, float max)
        {
            Assert.Equal(ok, SettingText.TryRange(text, out var a, out var b));
            if (ok) { Assert.Equal(min, a); Assert.Equal(max, b); }
        }

        private const string Delays = "KillReport=1.0-2.0,NiceShot=0.8-1.8, SaveThanks = 2.2-1.0";

        [Fact] public void ADelayIsFoundByItsSituation() => Assert.Equal((0.8f, 1.8f), SettingText.DelayFor(Delays, "NiceShot"));
        [Fact] public void SituationNamesMatchWhateverTheirCase() => Assert.Equal((1.0f, 2.0f), SettingText.DelayFor(Delays, "killreport"));
        [Fact] public void TheTwoNumbersMayComeInEitherOrder() => Assert.Equal((1.0f, 2.2f), SettingText.DelayFor(Delays, "SaveThanks"));
        [Fact] public void ASituationNotListedHasNoDelay() => Assert.Null(SettingText.DelayFor(Delays, "UnderAttack"));
        [Fact] public void AnEmptySettingMeansNoDelays() => Assert.Null(SettingText.DelayFor("", "KillReport"));
        [Fact] public void ASingleNumberIsBothEnds() => Assert.Equal((1.5f, 1.5f), SettingText.DelayFor("KillReport=1.5", "KillReport"));
    }

    public class PreloadRulesTests
    {
        [Theory]
        [InlineData("C:/voices/VOICE/_chatter/jon-marek/UnderAttack-3-t1.ogg", "jon-marek")]
        [InlineData(@"C:\voices\VOICE\_chatter\jon-marek\UnderAttack-3-t1.ogg", "jon-marek")]
        [InlineData("jon-marek/x.ogg", "jon-marek")]
        [InlineData("x.ogg", "")]
        [InlineData("", "")]
        public void AClipsVoiceFolderIsTheFolderItSitsIn(string path, string folder) => Assert.Equal(folder, PreloadRules.VoiceDirOf(path));

        [Theory]
        [InlineData("Red 2", "Red")]
        [InlineData("  Gold Leader ", "Gold")]
        [InlineData("Qiluiy", "Qiluiy")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void TheFlightWordIsTheFirstWord(string designation, string word) => Assert.Equal(word, PreloadRules.FlightWord(designation));

        [Fact]
        public void SituationsNeededFirstAreReadFirst()
        {
            Assert.Equal(0, PreloadRules.SituationRank("EnemyArrival"));
            Assert.True(PreloadRules.SituationRank("UnderAttack") < PreloadRules.SituationRank("KillReport"));
        }

        [Theory]
        [InlineData("WingmanLost")]
        [InlineData("underattack")]   // ids are matched as written
        [InlineData("")]
        [InlineData(null)]
        public void SituationsNotListedComeLast(string situation) =>
            Assert.True(PreloadRules.SituationRank(situation) > PreloadRules.SituationRank("SaveThanks"));
    }
}
