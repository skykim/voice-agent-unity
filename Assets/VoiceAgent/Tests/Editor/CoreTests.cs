using System;
using System.Linq;
using NUnit.Framework;

namespace VoiceAgent.Tests
{
    public class CoreTests
    {
        static readonly CommandCatalog Catalog = CommandCatalog.Load();

        [Test]
        public void Catalog_HasNineteenEnglishCommands()
        {
            Assert.AreEqual(19, Catalog.Count);
            Assert.AreEqual(-1, Catalog.IndexOf("set_volume"));
            foreach (var id in new[] { CommandCatalog.Chat, CommandCatalog.WebSearch, CommandCatalog.IntroduceSelf, "volume_up", "volume_down" })
                Assert.GreaterOrEqual(Catalog.IndexOf(id), 0, id);
            foreach (var c in Catalog.Commands)
            {
                Assert.IsNotEmpty(c.label, c.id);
                Assert.AreEqual(Lang.En, LangDetect.Of(c.label + c.reply), c.id);
                Assert.IsTrue(c.options.Any(o => LangDetect.Of(o) == Lang.Ko) && c.options.Any(o => LangDetect.Of(o) == Lang.En), c.id);
                Assert.GreaterOrEqual(c.options.Length, 2, c.id);
            }
        }

        [Test]
        public void Persona_IsComplete()
        {
            var p = Persona.Load();
            Assert.AreEqual("Nova", p.name);
            StringAssert.Contains("Nova", p.introduction);
            StringAssert.Contains("Nova", p.system_prompt);
            Assert.IsNotEmpty(p.greeting);
            Assert.IsNotEmpty(p.voice);
        }

        [Test]
        public void Persona_SystemPromptNamesTheLanguageAndTheSearchLimit()
        {
            var p = Persona.Load();
            StringAssert.EndsWith(p.reply_language_ko, p.SystemPrompt(Lang.Ko));
            StringAssert.EndsWith(p.reply_language, p.SystemPrompt(Lang.En));
            StringAssert.DoesNotContain(p.search_reply, p.SystemPrompt(Lang.En), "a chat turn has no search line");
            StringAssert.Contains(p.search_reply, p.SystemPrompt(Lang.En, search: true));
            StringAssert.Contains("two short sentences", p.search_reply);
        }

        [Test]
        public void TextMatch_NormalizesAndComparesBigrams()
        {
            Assert.AreEqual("lightson", Info.TextMatch.Normalize("Lights, on!"));
            Assert.AreEqual(1f, Info.TextMatch.Dice("애플워치", "애플워치"));
            Assert.AreEqual(0f, Info.TextMatch.Dice("apple", "zzzz"));
            Assert.Greater(Info.TextMatch.Dice("alanturing", "allanturing"), 0.8f, "a misheard name is still close");
        }

        [Test]
        public void SmartHome_VolumeStepsByTenAndClamps()
        {
            var home = new SmartHome();
            Assert.AreEqual(50, home.Volume);
            Assert.AreEqual("Volume up to 60%.", home.Execute(Catalog["volume_up"]));
            Assert.AreEqual("Volume down to 50%.", home.Execute(Catalog["volume_down"]));
            for (var i = 0; i < 8; i++) home.Execute(Catalog["volume_up"]);
            Assert.AreEqual(100, home.Volume);
            for (var i = 0; i < 12; i++) home.Execute(Catalog["volume_down"]);
            Assert.AreEqual(0, home.Volume);
        }

        [Test]
        public void SmartHome_LightUsesRainbowPresets()
        {
            var home = new SmartHome();
            Assert.AreEqual("yellow", home.CurrentColor.Name, "default color");
            Assert.AreEqual("The light is blue now.", home.Execute(Catalog["set_light_color"], "change the light color to blue"));
            Assert.IsTrue(home.LightOn);
            Assert.AreEqual("The light is indigo now.", home.Execute(Catalog["set_light_color"], "change the light color"), "no color named → next in the rainbow");
            Assert.AreEqual("The light is violet now.", home.Execute(Catalog["set_light_color"], "make it purple"));
            Assert.AreEqual("The light is red now.", home.Execute(Catalog["set_light_color"]), "wraps around");
            Assert.AreEqual(7, SmartHome.Colors.Length);
        }

        [Test]
        public void SmartHome_RepliesInTheLanguageOfTheRequest()
        {
            var home = new SmartHome();
            Assert.AreEqual("불을 켰어요.", home.Execute(Catalog["turn_on_light"], "거실 불 켜 줘"));
            Assert.AreEqual("조명을 파란색으로 바꿨어요.", home.Execute(Catalog["set_light_color"], "조명 색깔 파란색으로 바꿔 줘"));
            Assert.AreEqual("blue", home.CurrentColor.Name);
            Assert.AreEqual("조명을 보라색으로 바꿨어요.", home.Execute(Catalog["set_light_color"], "보라색으로 해 줘"));
            Assert.AreEqual("볼륨을 60%로 올렸어요.", home.Execute(Catalog["volume_up"], "볼륨 좀 올려 줘"));
            StringAssert.StartsWith("지금은 오", home.Execute(Catalog["get_time"], "지금 몇 시야"));
            Assert.AreEqual("Lights are off.", home.Execute(Catalog["turn_off_light"], "turn off the lights"));
            foreach (var c in Catalog.Commands)
            {
                Assert.AreEqual(Lang.Ko, LangDetect.Of(c.label_ko), c.id);
                Assert.AreEqual(string.IsNullOrEmpty(c.reply), string.IsNullOrEmpty(c.reply_ko), c.id);
            }
            var p = Persona.Load();
            foreach (var line in new[] { p.introduction_ko, p.not_sure_ko, p.search_miss_prefix_ko, p.low_confidence_ko })
                Assert.AreEqual(Lang.Ko, LangDetect.Of(line));
        }

        [Test]
        public void SmartHome_AppliesDefaultsAndFillsReplies()
        {
            var home = new SmartHome();
            Assert.AreEqual("Lights are on.", home.Execute(Catalog["turn_on_light"]));
            Assert.IsTrue(home.LightOn);
            Assert.AreEqual("yellow", home.CurrentColor.Name);
            home.Execute(Catalog["play_music"]);
            Assert.IsTrue(home.MusicOn);
            StringAssert.StartsWith("It's ", home.Execute(Catalog["get_time"]));
            Assert.IsNull(home.Execute(Catalog["chat"]));
            Assert.IsNull(home.Execute(Catalog["web_search"]));
            Assert.IsNull(home.Execute(Catalog["introduce_self"]));
        }

        [Test]
        public void PolarityGuard_FixesDirectionOnlyForTheTopPair()
        {
            var ids = Catalog.Commands.Select(c => c.id).ToArray();
            var guard = PolarityGuard.Load(ids);
            float[] Probs(params (string Id, float P)[] entries)
            {
                var p = new float[ids.Length];
                foreach (var (id, value) in entries) p[Array.IndexOf(ids, id)] = value;
                return p;
            }
            float P(float[] p, string id) => p[Array.IndexOf(ids, id)];

            var fixedDown = guard.Apply("소리 내려 봐", Probs(("volume_up", 0.25f), ("volume_down", 0.20f), ("chat", 0.1f)));
            Assert.AreEqual(0.45f, P(fixedDown, "volume_down"), 1e-5f);
            Assert.AreEqual(0f, P(fixedDown, "volume_up"));

            var tvOff = guard.Apply("switch the tv off", Probs(("turn_on_tv", 0.5f), ("turn_off_tv", 0.3f)));
            Assert.AreEqual(0.8f, P(tvOff, "turn_off_tv"), 1e-5f);

            var fan = Probs(("chat", 0.3f), ("turn_on_light", 0.2f), ("turn_off_light", 0.1f));
            CollectionAssert.AreEqual(fan, guard.Apply("turn on the fan in the study", fan), "the head's top pick isn't a pair: untouched");

            var complaint = Probs(("volume_up", 0.5f), ("volume_down", 0.3f));
            CollectionAssert.AreEqual(complaint, guard.Apply("소리가 너무 크게 들려", complaint), "'너무 크게 들려' means too loud, not 'louder'");

            var shutDown = Probs(("turn_off_computer", 0.5f), ("turn_on_computer", 0.3f));
            CollectionAssert.AreEqual(shutDown, guard.Apply("컴퓨터 종료시켜 줘", shutDown), "'시켜' contains the on word '켜': the guard stays out");

            var both = Probs(("turn_on_light", 0.5f), ("turn_off_light", 0.3f));
            CollectionAssert.AreEqual(both, guard.Apply("turn off the tv and turn on the light", both), "both directions named: untouched");
        }

        [Test]
        public void Routing_FollowsConfidenceAndCommandKind()
        {
            RankedCommand R(string id, float p) => new(Catalog[id], p);
            TurnRoute Route(string id, float p, float runnerUp = 0.05f, bool info = true) =>
                TurnRouting.Decide(new[] { R(id, p), R("chat", runnerUp) }, info, 0.5f);
            Assert.AreEqual(TurnRoute.Execute, Route("volume_up", 0.9f));
            Assert.AreEqual(TurnRoute.LowConfidence, Route("volume_up", 0.3f));
            Assert.AreEqual(TurnRoute.Execute, Route("play_music", 0.49f, 0.20f), "a clear leader just under the threshold still runs");
            Assert.AreEqual(TurnRoute.LowConfidence, Route("play_music", 0.38f, 0.38f), "a tie asks");
            Assert.AreEqual(TurnRoute.Execute, Route("web_search", 0.3f));
            Assert.AreEqual(TurnRoute.Chat, Route("web_search", 0.9f, info: false));
            Assert.AreEqual(TurnRoute.Chat, Route("chat", 0.9f));
            Assert.AreEqual(TurnRoute.Execute, Route("introduce_self", 0.9f));

            var unsureChat = new[] { R("chat", 0.16f), R("volume_down", 0.14f), R("volume_up", 0.11f) };
            Assert.AreEqual(TurnRoute.LowConfidence, TurnRouting.Decide(unsureChat, true, 0.5f), "an unsure chat pick with a command close behind asks");
            Assert.AreEqual("volume_down", TurnRouting.Suggestion(unsureChat).id, "and asks about the command, not chat");
            Assert.AreEqual(TurnRoute.Chat, TurnRouting.Decide(new[] { R("chat", 0.25f), R("get_location", 0.14f) }, true, 0.5f), "the command is too far behind");
            Assert.AreEqual(TurnRoute.Chat, TurnRouting.Decide(new[] { R("chat", 0.49f), R("volume_down", 0.45f) }, true, 0.5f), "chat is confident enough");
            Assert.AreEqual("play_music", TurnRouting.Suggestion(new[] { R("play_music", 0.3f), R("chat", 0.2f) }).id);
            Assert.AreEqual("I'm not sure I got that. Did you mean \"Play music\"? Tap it on the left, or say it again.", Persona.Load().Clarify(Catalog["play_music"]));
            Assert.AreEqual("잘 알아듣지 못했어요. 혹시 \"음악 틀기\" 말씀이세요? 왼쪽에서 누르거나 다시 말해 주세요.", Persona.Load().Clarify(Catalog["play_music"], Lang.Ko));
        }
    }
}
