using System.Collections.Generic;
using System.Diagnostics;
using SentisModels;
using VoiceAgent.Info;
using VoiceAgent.UI;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>What the voice input measured before the text reached the controller.</summary>
    public readonly struct VoiceTiming
    {
        /// <summary>From the end of speech (VAD) until the final transcript arrived.</summary>
        public readonly double SttMs;
        public readonly float SpeechSeconds;
        public readonly int Partials;

        public VoiceTiming(double sttMs, float speechSeconds, int partials)
        {
            SttMs = sttMs;
            SpeechSeconds = speechSeconds;
            Partials = partials;
        }
    }

    /// <summary>
    /// Turn logic of the voice agent. Typed or spoken text is ranked into command chips;
    /// on submit the best command runs when it is confident enough. "Who are you" gets the persona's introduction,
    /// weather/time/location/search are answered from the internet, and chat or low-confidence turns get a reply
    /// from Gemma3 in the persona's voice.
    /// </summary>
    public sealed class AssistantController
    {
        const float TypingDebounce = 0.12f;

        readonly CommandCatalog m_Catalog;
        readonly SmartHome m_Home;
        readonly JevlikeRanker m_Ranker;
        readonly AssistantView m_View;
        readonly Gemma3Model m_Gemma;
        readonly SpeechOutput m_Speech;
        readonly ProceduralMusic m_Music;
        readonly InfoAgent m_Info;
        readonly Persona m_Persona;
        readonly TimingPanel m_Timing;

        // Stage timings of the current turn (seconds after the end of speech), shown in the timing panel.
        readonly Stopwatch m_ActionClock = new();
        double m_SttSeconds, m_IntentSeconds, m_ActionSeconds;
        bool m_TimingTurn;
        int m_TurnNumber;

        string m_PendingText;
        float m_PendingAt = -1f;
        bool m_Busy;

        public bool IsBusy => m_Busy;

        public AssistantController(CommandCatalog catalog, Persona persona, SmartHome home, JevlikeRanker ranker, Gemma3Model gemma, AssistantView view,
            ProceduralMusic music, SpeechOutput speech = null, InfoAgent info = null, TimingPanel timing = null)
        {
            m_Timing = timing;
            m_Catalog = catalog;
            m_Persona = persona;
            m_Home = home;
            m_Ranker = ranker;
            m_View = view;
            m_Music = music;
            m_Gemma = gemma;
            m_Speech = speech;
            m_Info = info;

            view.TextChanged += QueueRank;
            view.CanSubmit = () => !m_Busy;
            // async void handlers so exceptions reach the Unity log instead of vanishing with a discarded Awaitable.
            view.Submitted += async text => await Submit(text);
            view.ChipClicked += async command => await RunChip(command);
            home.Changed += SyncMusic;
            SyncMusic();
        }

        /// <summary>Call from Update: runs the debounced chip ranking for typed text.</summary>
        public void Tick()
        {
            if (m_PendingAt >= 0f && Time.time >= m_PendingAt)
            {
                m_PendingAt = -1f;
                ShowChips(m_PendingText);
            }
        }

        public void QueueRank(string text)
        {
            m_PendingText = text;
            m_PendingAt = Time.time + TypingDebounce;
        }

        public List<RankedCommand> ShowChips(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                m_View.SetChips(null);
                return null;
            }
            var top = Ranking.Top(m_Catalog, m_Ranker.Score(text), m_View.MaxChips);
            m_View.SetChips(top);
            return top;
        }

        /// <summary>A spoken turn has started (the timing panel shows it as listening).</summary>
        public void BeginVoiceTurn()
        {
            if (m_Timing == null || m_Busy) return;
            m_Timing.Begin($"Turn {++m_TurnNumber} / voice", "listening…", "latency after the end of speech");
        }

        public void ShowPartials(int partials) => m_Timing?.Listening(partials, 0);

        public async Awaitable Submit(string text, VoiceTiming? voice = null)
        {
            if (m_Busy || string.IsNullOrWhiteSpace(text)) return;
            m_PendingAt = -1f;
            m_View.AddMessage(text, true);
            if (m_Timing != null)
            {
                if (voice == null) m_Timing.Begin($"Turn {++m_TurnNumber} / typed", $"\"{text}\"", "latency after pressing Enter");
                else m_Timing.SetUtterance(text);
                m_TimingTurn = true;
                m_SttSeconds = (voice?.SttMs ?? 0) / 1000;
                if (voice is { } v)
                {
                    m_Timing.Listening(v.Partials, v.SpeechSeconds);
                    m_Timing.Stage(0, "Speech to text / SenseVoice", v.SttMs, 0);
                }
                else m_Timing.Stage(0, "Speech to text / typed", 0, 0);
            }
            var intentClock = Stopwatch.StartNew();
            var top = ShowChips(text);
            var best = top[0];
            m_IntentSeconds = intentClock.Elapsed.TotalSeconds;
            if (m_Timing != null)
            {
                m_Timing.Stage(1, "Intent / Gemma3 + jevlike", m_IntentSeconds * 1000, m_SttSeconds);
                m_Timing.Verdict(best.Command.id, best.Probability);
            }
            m_ActionClock.Restart();
            var route = TurnRouting.Decide(top, m_Info != null, TurnRouting.AutoRunThreshold);
            UnityEngine.Debug.Log($"[Turn] '{text}' → {route}: " + string.Join(", ", top.GetRange(0, System.Math.Min(3, top.Count)).ConvertAll(r => $"{r.Command.id} {r.Probability:F2}")));
            if (route == TurnRoute.Execute)
            {
                await Execute(best.Command, text);
                m_View.SetChips(null);
                return;
            }
            if (route == TurnRoute.LowConfidence)
            {
                // Ask instead of letting Gemma improvise; the chips stay up so the user can tap the right command.
                var line = m_Persona.Clarify(TurnRouting.Suggestion(top), LangDetect.Of(text));
                ActionDone("Action / asked to confirm");
                m_View.AddMessage(line, false);
                await Speak(line);
                return;
            }
            await Reply(text);
        }

        async Awaitable RunChip(Command command)
        {
            if (m_Busy) return;
            var typed = m_View.InputText.Trim();
            var text = typed.Length > 0 ? typed : command.label;
            m_View.AddMessage(text, true);
            m_View.SetInputText(string.Empty);
            m_View.SetChips(null);
            if (m_Timing != null)
            {
                m_Timing.Begin($"Turn {++m_TurnNumber} / tapped", $"\"{command.label}\"", "latency after the tap");
                m_Timing.Stage(0, "Speech to text / none", 0, 0);
                m_Timing.Stage(1, "Intent / tapped suggestion", 0, 0);
                m_Timing.Verdict(command.id, 1f);
                m_TimingTurn = true;
                m_SttSeconds = m_IntentSeconds = 0;
            }
            m_ActionClock.Restart();
            await Execute(command, text);
        }

        async Awaitable Execute(Command command, string userText)
        {
            if (command.id == CommandCatalog.IntroduceSelf)
            {
                ActionDone("Action / Nova's introduction");
                var introduction = m_Persona.Introduction(LangDetect.Of(userText));
                m_View.AddMessage(introduction, false);
                await Speak(introduction);
                return;
            }

            if (m_Info != null && InfoAgent.Supports(command.id))
            {
                // Internet answers follow the question's language: a Korean question searches Korean sources and is answered in Korean.
                var lang = LangDetect.Of(userText);
                m_Busy = true;
                m_View.SetMicState(MicState.Thinking);
                var bubble = m_View.AddMessage(InfoAgent.LookingUp(lang), false);
                InfoAnswer answer;
                try
                {
                    answer = await m_Info.AnswerAsync(command.id, userText, lang, step => m_View.UpdateMessage(bubble, step));
                }
                finally
                {
                    m_Busy = false;
                }
                if (answer == null)
                {
                    var local = m_Home.Execute(command, userText);
                    ActionDone("Action / local clock");
                    m_View.UpdateMessage(bubble, local);
                    await Speak(local);
                    return;
                }
                foreach (var step in answer.Steps) UnityEngine.Debug.Log($"[Info] {step}");
                if (answer.Found || command.id != CommandCatalog.WebSearch)
                {
                    ActionDone(InfoAgent.ActionLabel(command.id, answer.Sources));
                    m_View.UpdateMessage(bubble, WithSources(answer.Speech, answer.Sources));
                    await Speak(answer.Speech);
                    return;
                }
                await Reply(userText, bubble, m_Persona.SearchMissPrefix(lang));
                return;
            }

            var reply = m_Home.Execute(command, userText);
            if (reply == null)
            {
                await Reply(userText);
                return;
            }
            ActionDone("Action / home control");
            m_View.AddMessage(reply, false);
            await Speak(reply);
        }

        static string WithSources(string text, string sources) =>
            string.IsNullOrEmpty(sources) ? text : $"{text}\n<size=15><color=#6B6780>Source / {sources}</color></size>";

        async Awaitable Reply(string text, MessageView bubble = null, string prefix = null)
        {
            m_Busy = true;
            m_View.SetMicState(MicState.Thinking);
            bubble ??= m_View.AddMessage("…", false);
            // A prefix means the web search found nothing and Gemma answers instead: two sentences at most, like the web answers.
            var search = !string.IsNullOrEmpty(prefix);
            prefix ??= string.Empty;
            string answer;
            try
            {
                m_Gemma.SystemPrompt = m_Persona.SystemPrompt(LangDetect.Of(text), search);
                answer = ReplyText.Clean(await m_Gemma.GenerateAsync(text, 48, partial => m_View.UpdateMessage(bubble, prefix + ReplyText.Clean(partial))));
            }
            catch (System.Exception e)
            {
                // The turn still ends with a reply, so the mic and the bubble don't stay on "thinking".
                UnityEngine.Debug.LogException(e);
                answer = null;
            }
            finally
            {
                m_Busy = false;
            }
            // The small model doesn't always keep to the prompt's length, so the cut is also made here.
            if (search && answer != null) answer = InfoText.FirstSentences(answer, 2);
            if (string.IsNullOrWhiteSpace(answer)) answer = m_Persona.NotSure(LangDetect.Of(text));
            answer = prefix + answer;
            ActionDone(prefix.Length > 0 ? "Action / search + Gemma3" : "Action / Gemma3 reply");
            m_View.UpdateMessage(bubble, answer);
            await Speak(answer);
        }

        /// <summary>The reply text is ready: records the action stage.</summary>
        void ActionDone(string label)
        {
            if (m_Timing == null || !m_TimingTurn) return;
            m_ActionSeconds = m_ActionClock.Elapsed.TotalSeconds;
            m_Timing.Stage(2, label, m_ActionSeconds * 1000, m_SttSeconds + m_IntentSeconds);
        }

        async Awaitable Speak(string text)
        {
            var start = m_SttSeconds + m_IntentSeconds + m_ActionSeconds;
            if (m_Speech == null || !m_Speech.IsLoaded)
            {
                if (m_Timing != null && m_TimingTurn)
                {
                    m_Timing.Stage(3, "Voice / none (text only)", 0, start);
                    m_Timing.Total(start);
                    m_TimingTurn = false;
                }
                m_View.SetMicState(MicState.Idle);
                return;
            }
            m_View.SetMicState(MicState.Speaking);
            var clock = Stopwatch.StartNew();
            await m_Speech.Speak(text);
            if (m_Timing != null && m_TimingTurn)
            {
                // Speak returns when playback starts, so this is synthesis until the first sound.
                var ms = clock.Elapsed.TotalMilliseconds;
                m_Timing.Stage(3, "Voice / Supertonic 3", ms, start);
                m_Timing.Total(start + ms / 1000);
                m_TimingTurn = false;
            }
            while (m_Speech.IsSpeaking) await Awaitable.NextFrameAsync();
            m_View.SetMicState(MicState.Idle);
        }

        void SyncMusic()
        {
            if (m_Music == null) return;
            m_Music.SetPlaying(m_Home.MusicOn);
            m_Music.Volume = m_Home.Volume / 100f;
        }
    }
}
