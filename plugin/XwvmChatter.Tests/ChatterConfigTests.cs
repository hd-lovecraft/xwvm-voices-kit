using System.Linq;
using Xunit;

namespace XwvmChatter.Tests
{
    public class SlugTests
    {
        [Theory]
        [InlineData("Jon Marek", "jon-marek")]
        [InlineData("Dana Vale - Deep Space IX", "dana-vale-deep-space-ix")]
        [InlineData("  Mira   Kessel!  ", "mira-kessel")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void VoiceNameBecomesItsFolderName(string voice, string folder) => Assert.Equal(folder, ChatterConfig.Slug(voice));
    }

    public class MissionNameTests
    {
        [Theory]
        [InlineData("T1M01XW", "T1M01XW", true)]
        [InlineData("t1m01xw", "T1M01XW", true)]     // case does not matter
        [InlineData("*", "ANYTHING", true)]
        [InlineData("T1M*", "T1M01XW", true)]
        [InlineData("T1M*", "T2M01XW", false)]
        [InlineData("T1M0?XW", "T1M01XW", true)]
        [InlineData("T1M0?XW", "T1M011XW", false)]
        [InlineData("T1M01XW|T1M02XW", "T1M02XW", true)]
        [InlineData("T1M01XW", "T1M01XWB", false)]
        [InlineData("T1.M1", "T1XM1", false)]        // a dot is a dot, not "any character"
        [InlineData("", "T1M01XW", false)]
        [InlineData(null, "T1M01XW", false)]
        [InlineData("*", null, false)]
        public void TargetPattern(string pattern, string mission, bool matches) =>
            Assert.Equal(matches, ChatterConfig.MatchesMissionName(pattern, mission));

        /// <summary>With alternatives the pattern becomes "^(A)|(B)$": only the first is tied to the start and only the
        /// last to the end. The game's own SidecarLoader.MatchesMissionName builds the same expression, and the plugin
        /// follows the game. No shipped casting file uses '|'; this test records the behaviour so a change is noticed.</summary>
        [Fact]
        public void AlternativesAnchorLooselyAsInTheGame()
        {
            Assert.True(ChatterConfig.MatchesMissionName("RESCUE1|WYRESC3", "RESCUE1-LONGER"));
            Assert.True(ChatterConfig.MatchesMissionName("RESCUE1|WYRESC3", "PREFIX-WYRESC3"));
        }
    }

    public class LoadTests
    {
        private const string TwoLines =
            "<line situation='UnderAttack' audio='UnderAttack-1-t1'>Break off!</line>" +
            "<line situation='KillReport' audio='KillReport-1-t1'>Got him.</line>";

        [Fact]
        public void MissingFolderGivesAnEmptyConfigAndSaysSo()
        {
            var log = new System.Collections.Generic.List<string>();
            var cfg = ChatterConfig.Load(@"Z:\no\such\folder", "T1M01XW", log.Add);
            Assert.True(cfg.IsEmpty);
            Assert.Contains(log, l => l.Contains("sidecar dir not found"));
        }

        [Fact]
        public void ASpeakerTakesItsLinesFromItsVoicesBank()
        {
            using var f = new Fixture();
            f.Bank("test-voice", TwoLines, "UnderAttack-1-t1", "KillReport-1-t1");
            f.Casting("casting.xml", "<sidecar><mission target='T1M01XW'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Test Voice'/></chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.Equal(2, cfg.Lines.Count);
            Assert.All(cfg.Lines, l => Assert.Equal(new[] { "red2" }, l.SpeakerIds));
            Assert.All(cfg.Lines, l => Assert.True(l.FromMissionBlock));
            Assert.Equal("Break off!", cfg.Lines[0].Text);
            Assert.EndsWith("UnderAttack-1-t1.ogg", cfg.Lines[0].AudioFile);
        }

        [Fact]
        public void AnotherMissionsCastingDoesNotApply()
        {
            using var f = new Fixture();
            f.Bank("test-voice", TwoLines, "UnderAttack-1-t1", "KillReport-1-t1");
            f.Casting("casting.xml", "<sidecar><mission target='T1M01XW'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Test Voice'/></chatter></mission></sidecar>");
            Assert.True(f.Load("T1M02XW").IsEmpty);
        }

        [Fact]
        public void ALineWhoseClipIsMissingIsLeftOutAndLogged()
        {
            using var f = new Fixture();
            f.Bank("test-voice", TwoLines, "UnderAttack-1-t1");      // no clip for the kill report
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Test Voice'/></chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.Equal("UnderAttack", Assert.Single(cfg.Lines).Situation);
            Assert.Contains(f.Log, l => l.Contains("audio missing") && l.Contains("KillReport-1-t1"));
        }

        [Fact]
        public void AVoiceWithoutABankIsLoggedAndGetsNoLines()
        {
            using var f = new Fixture();
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Nobody Recorded'/></chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.Empty(cfg.Lines);
            Assert.Contains(f.Log, l => l.Contains("no bank.xml for voice Nobody Recorded"));
        }

        [Fact]
        public void ACallsignOnlyLineNeedsNoPlainClip()
        {
            using var f = new Fixture();
            f.Bank("test-voice", "<line situation='NiceShot' audio='NiceShot-12-v4' callsignOnly='true'>Good shooting, {player}.</line>");
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Test Voice'/></chatter></mission></sidecar>");
            var line = Assert.Single(f.Load("T1M01XW").Lines);
            Assert.True(line.CallsignOnly);
        }

        [Fact]
        public void AWingmanNeverGetsCapitalShipLinesAndAShipOnlyGetsThose()
        {
            using var f = new Fixture();
            f.Bank("test-voice", TwoLines + "<line situation='KillReport' role='capital' audio='KillReport-c1-t1'>Target destroyed.</line>",
                   "UnderAttack-1-t1", "KillReport-1-t1", "KillReport-c1-t1");
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Test Voice'/>" +
                                     "<speaker id='ship1' designation='Liberty' role='ship' lines='capital' voice='Test Voice'/></chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.Equal(2, cfg.Lines.Count(l => l.SpeakerIds.Contains("red2")));
            var shipLine = Assert.Single(cfg.Lines, l => l.SpeakerIds.Contains("ship1"));
            Assert.Equal("Target destroyed.", shipLine.Text);
        }

        [Fact]
        public void ShipsOfOneFlightGroupShareOneSetOfLines()
        {
            using var f = new Fixture();
            f.Bank("test-voice", "<line situation='KillReport' role='capital' audio='KillReport-c1-t1'>Target destroyed.</line>", "KillReport-c1-t1");
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='q1' designation='Qiluiy 1' role='ship' lines='capital' group='qiluiy' callsign='Qiluiy' voice='Test Voice'/>" +
                                     "<speaker id='q2' designation='Qiluiy 2' role='ship' lines='capital' group='qiluiy' callsign='Qiluiy' voice='Test Voice'/>" +
                                     "</chatter></mission></sidecar>");
            var line = Assert.Single(f.Load("T1M01XW").Lines);
            Assert.Equal(new[] { "q1", "q2" }, line.SpeakerIds);
        }

        [Fact]
        public void SituationsAttributeLimitsWhatASpeakerTakes()
        {
            using var f = new Fixture();
            f.Bank("test-voice", TwoLines, "UnderAttack-1-t1", "KillReport-1-t1");
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='cmd' role='commander' voice='Test Voice' situations='UnderAttack'/></chatter></mission></sidecar>");
            Assert.Equal("UnderAttack", Assert.Single(f.Load("T1M01XW").Lines).Situation);
        }

        [Fact]
        public void ASilentSeatIsHeldButHasNoLines()
        {
            using var f = new Fixture();
            f.Bank("test-voice", TwoLines, "UnderAttack-1-t1", "KillReport-1-t1");
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='red2' designation='Red 2' role='silent' voice='Test Voice'/></chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.True(cfg.Speakers["red2"].IsSilent);
            Assert.Empty(cfg.Lines);
        }

        [Fact]
        public void TagsAreReadLowerCase()
        {
            using var f = new Fixture();
            f.Bank("test-voice", "<line situation='UnderAttack' audio='UnderAttack-1-t1' attacker='TIE Interceptor' threat='multiple' arrival='Hyper' context='combat'>x</line>", "UnderAttack-1-t1");
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Test Voice'/></chatter></mission></sidecar>");
            var line = Assert.Single(f.Load("T1M01XW").Lines);
            Assert.Equal(new[] { "tie", "interceptor" }, line.Attacker);
            Assert.Equal(new[] { "multiple" }, line.Threat);
            Assert.Equal(new[] { "hyper" }, line.Arrival);
            Assert.Equal(new[] { "combat" }, line.Context);
            Assert.True(line.Tagged);
        }

        [Fact]
        public void SilenceListsTheSituationsTheStoryScriptVoices()
        {
            using var f = new Fixture();
            f.Bank("test-voice", TwoLines, "UnderAttack-1-t1", "KillReport-1-t1");
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter' silence='MCCAttacked, EnemyArrival'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Test Voice'/></chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.Contains("mccattacked", cfg.Silenced);     // case does not matter
            Assert.Contains("EnemyArrival", cfg.Silenced);
        }

        [Fact]
        public void SubtitleTextIsJoinedOntoOneLine()
        {
            using var f = new Fixture();
            f.Bank("test-voice", "<line situation='UnderAttack' audio='UnderAttack-1-t1'>\n   Break off!\n   You're taking fire!\n</line>", "UnderAttack-1-t1");
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Test Voice'/></chatter></mission></sidecar>");
            Assert.Equal("Break off! You're taking fire!", Assert.Single(f.Load("T1M01XW").Lines).Text);
        }

        [Fact]
        public void ABlocksOwnLineNamesItsSpeakersAndFindsItsClipUnderAudioPath()
        {
            using var f = new Fixture();
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(f.VoiceDir("test-voice"), "UnderAttack-1-t1.ogg"), new byte[0]);
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter audioPath='VOICE/_chatter/test-voice'>" +
                                     "<speaker id='red2' designation='Red 2' voice='Test Voice'/>" +
                                     "<speaker id='red3' designation='Red 3' voice='Test Voice'/>" +
                                     "<line situation='UnderAttack' audio='UnderAttack-1-t1' speaker='red2, red3'>Break off!</line>" +
                                     "<line situation='KillReport'>Got him.</line>" +
                                     "</chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.Equal(2, cfg.Lines.Count);
            Assert.Equal(new[] { "red2", "red3" }, cfg.Lines[0].SpeakerIds);
            Assert.EndsWith("UnderAttack-1-t1.ogg", cfg.Lines[0].AudioFile);
            Assert.Empty(cfg.Lines[1].SpeakerIds);            // names nobody: whoever the game names as sender
            Assert.Null(cfg.Lines[1].AudioFile);              // words only
        }

        [Fact]
        public void ASpeakerWithoutAnIdIsKnownByItsDesignationThenItsRole()
        {
            using var f = new Fixture();
            f.Casting("casting.xml", "<sidecar><mission target='*'><chatter>" +
                                     "<speaker designation='Red 2' role='generic' voice='A'/>" +
                                     "<speaker role='commander' voice='B'/>" +
                                     "<speaker voice='C'/>" +
                                     "</chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.Equal(new[] { "commander", "Red 2" }, cfg.Speakers.Keys.OrderBy(k => k));
            Assert.Contains(f.Log, l => l.Contains("speaker without id/designation/role"));
        }
    }

    public class LineTests
    {
        private static Line LineFor(string situation, params string[] speakerIds)
        {
            var line = new Line { Situation = situation };
            line.SpeakerIds.AddRange(speakerIds);
            return line;
        }

        [Fact]
        public void ALineIsForItsSituationWhateverTheCase()
        {
            Assert.True(LineFor("UnderAttack").IsFor("underattack"));
            Assert.False(LineFor("UnderAttack").IsFor("KillReport"));
            Assert.False(LineFor("UnderAttack").IsFor(null));
        }

        [Fact]
        public void ALineNamesTheSpeakersOnItsListWhateverTheCase()
        {
            var line = LineFor("UnderAttack", "red2", "red3");
            Assert.True(line.NamesSpeaker(new Speaker { Id = "RED3" }));
            Assert.False(line.NamesSpeaker(new Speaker { Id = "red4" }));
        }

        [Fact]
        public void ALineNamesNoSpeakerWhenThereIsNoneOrItsListIsEmpty()
        {
            Assert.False(LineFor("UnderAttack", "red2").NamesSpeaker(null));
            Assert.False(LineFor("UnderAttack").NamesSpeaker(new Speaker { Id = "red2" }));
        }
    }

    public class DefaultOfficerTests
    {
        private const string OfficerLine = "<line situation='UnderAttack' audio='UnderAttack-1-t1'>Break off!</line>";
        private const string Defaults = "<sidecar><chatter-defaults target='*' banks='VOICE/_chatter'>" +
                                        "<speaker id='g0' role='generic' voice='Pool Voice'/>" +
                                        "<speaker id='cmd0' role='commander' voice='Default Officer'/></chatter-defaults></sidecar>";

        private static Fixture Voices()
        {
            var f = new Fixture();
            f.Bank("pool-voice", OfficerLine, "UnderAttack-1-t1");
            f.Bank("default-officer", OfficerLine, "UnderAttack-1-t1");
            f.Bank("tour-officer", OfficerLine, "UnderAttack-1-t1");
            f.Casting("generic.xml", Defaults);
            return f;
        }

        [Fact]
        public void AMissionWithNoOfficerOfItsOwnGetsTheDefaultOne()
        {
            using var f = Voices();
            var cfg = f.Load("SOMECUSTOM");
            Assert.True(cfg.Speakers.ContainsKey("cmd0"));
            Assert.Contains(cfg.Lines, l => l.SpeakerIds.Contains("cmd0"));
            Assert.Contains(f.Log, l => l.Contains("default officer Default Officer"));
        }

        [Fact]
        public void AMissionThatCastsItsOwnOfficerDropsTheDefaultOneAndHisLines()
        {
            using var f = Voices();
            f.Casting("casting.xml", "<sidecar><mission target='T1M01XW'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='cmd' role='commander' voice='Tour Officer'/></chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.False(cfg.Speakers.ContainsKey("cmd0"));
            Assert.DoesNotContain(cfg.Lines, l => l.SpeakerIds.Contains("cmd0"));
            Assert.Contains(cfg.Lines, l => l.SpeakerIds.Contains("cmd"));
            Assert.Contains(cfg.Lines, l => l.SpeakerIds.Contains("g0"));     // the pool stays
        }

        [Fact]
        public void AMissionSpeakerReplacesADefaultWithTheSameId()
        {
            using var f = Voices();
            f.Casting("casting.xml", "<sidecar><mission target='T1M01XW'><chatter banks='VOICE/_chatter'>" +
                                     "<speaker id='g0' designation='Red 2' voice='Tour Officer'/></chatter></mission></sidecar>");
            var cfg = f.Load("T1M01XW");
            Assert.Equal("Tour Officer", cfg.Speakers["g0"].Voice);
            Assert.True(cfg.Speakers["g0"].FromMissionBlock);
        }
    }

    public class CallsignMapTests
    {
        [Theory]
        [InlineData("Red 1", "Red Leader", true)]
        [InlineData("Red", "red 1", true)]
        [InlineData("RED LEADER", "Red", true)]
        [InlineData("Red 2", "Red 1", false)]
        [InlineData("Red 10", "Red 1", false)]
        [InlineData("Gold 2", "gold 2", true)]
        public void LeaderOneAndTheBareNameAreTheSameSeat(string a, string b, bool same) =>
            Assert.Equal(same, CallsignMap.CallsignsEqual(a, b));

        [Fact]
        public void ManifestListsTheVersionsWhoseClipsExist()
        {
            using var f = new Fixture();
            var dir = f.VoiceDir("test-voice");
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "UnderAttack-1-v4--address--red-2.ogg"), new byte[0]);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "callsigns.xml"),
                "<callsigns>" +
                "<composed callsign='Red 2' situation='UnderAttack' audio='UnderAttack-1-v4--address--red-2.ogg'> Red Two, break off! </composed>" +
                "<composed kind='SelfId' callsign='Red 3' situation='KillReport' audio='not-there.ogg'>x</composed>" +
                "</callsigns>");
            var m = CallsignMap.LoadManifest(dir, f.Log.Add);
            var v = Assert.Single(m.Versions);
            Assert.Equal("address", v.Kind);                   // the default kind
            Assert.Equal("Red 2", v.Callsign);
            Assert.Equal("Red Two, break off!", v.Text);
            Assert.Contains(f.Log, l => l.Contains("missing not-there.ogg"));
        }

        [Fact]
        public void AVersionsKindIsReadLowerCase()
        {
            using var f = new Fixture();
            var dir = f.VoiceDir("test-voice");
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "KillReport-1-v4--selfid--red-3.ogg"), new byte[0]);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "callsigns.xml"),
                "<callsigns><composed kind=' SelfId ' callsign='Red 3' situation='KillReport' audio='KillReport-1-v4--selfid--red-3.ogg'>x</composed></callsigns>");
            Assert.Equal(CallsignVersion.SelfId, Assert.Single(CallsignMap.LoadManifest(dir, f.Log.Add).Versions).Kind);
        }

        [Fact]
        public void AVoiceWithoutAManifestHasNoVersions()
        {
            using var f = new Fixture();
            Assert.Empty(CallsignMap.LoadManifest(f.VoiceDir("plain-voice"), f.Log.Add).Versions);
        }
    }
}
