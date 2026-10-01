using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SentisModels;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.TestTools;

namespace VoiceAgent.Tests
{
    /// <summary>
    /// Gemma3 against reference token ids and hidden states from Hugging Face and PyTorch (Golden/gemma_golden.json),
    /// batched and per-layer encoding, the jevlike head on everyday phrases, and a mic-free TTS → VAD → STT → jevlike loop.
    /// </summary>
    public class ModelTests
    {
        [Serializable]
        sealed class Golden
        {
            public string text;
            public int[] ids;
            public float[] hidden_row1;
        }

        [Serializable]
        sealed class GoldenList
        {
            public Golden[] items;
        }

        static CommandCatalog s_Catalog;
        static Gemma3Model s_Gemma;
        static JevlikeRanker s_Ranker;
        static Golden[] s_Golden;

        [OneTimeSetUp]
        public void Load()
        {
            if (UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{ModelRoots.GemmaPackage}") == null ||
                !File.Exists(Path.Combine(ModelRoots.Gemma, Gemma3Model.DefaultModelFile)))
                Assert.Ignore($"{ModelRoots.GemmaPackage} is not installed");
            s_Catalog = CommandCatalog.Load();
            s_Gemma = ModelRoots.LoadGemma(null);
            s_Ranker = new JevlikeRanker(s_Gemma, ModelRoots.IntentHead, s_Catalog);
            var json = File.ReadAllText("Assets/VoiceAgent/Tests/Editor/Golden/gemma_golden.json");
            s_Golden = JsonUtility.FromJson<GoldenList>("{\"items\":" + json + "}").items;
        }

        [OneTimeTearDown]
        public void Unload() => s_Gemma?.Dispose();

        [Test]
        public void Tokenizer_MatchesHuggingFace()
        {
            foreach (var g in s_Golden)
                CollectionAssert.AreEqual(g.ids, s_Gemma.Tokenize(JevlikeRanker.Normalize(g.text)), g.text);
        }

        [Test]
        public void HiddenStates_MatchTorch()
        {
            foreach (var g in s_Golden)
            {
                var states = s_Gemma.Encode(g.ids);
                double err = 0, norm = 0;
                for (var i = 0; i < g.hidden_row1.Length; i++)
                {
                    var d = states[Gemma3Model.Width + i] - g.hidden_row1[i];
                    err += d * d;
                    norm += g.hidden_row1[i] * g.hidden_row1[i];
                }
                var relative = Math.Sqrt(err / Math.Max(norm, 1e-6));
                Assert.Less(relative, 0.05, $"{g.text}: relative error {relative:F4}");
            }
        }

        [TestCase(Gemma3Model.FinalNorm)]
        [TestCase(12)]
        public void EncodeBatch_MatchesEncode(int layer)
        {
            var sentences = new List<int[]>();
            foreach (var g in s_Golden) sentences.Add(g.ids);
            foreach (var text in new[] { "hi", "소리 좀 키워 줘", "What is the capital of South Korea, and how many people live in its largest city today?" })
                sentences.Add(s_Gemma.Tokenize(text, 48));

            s_Gemma.Encode(sentences[0], layer);
            s_Gemma.EncodeBatch(sentences, layer);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var single = sentences.ConvertAll(ids => s_Gemma.Encode(ids, layer));
            var singleMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var batched = s_Gemma.EncodeBatch(sentences, layer);
            var batchMs = clock.Elapsed.TotalMilliseconds;
            var packs = s_Gemma.BatchTokens;
            s_Gemma.BatchTokens = 16;
            float[][] small;
            try { small = s_Gemma.EncodeBatch(sentences, layer); }
            finally { s_Gemma.BatchTokens = packs; }

            var worst = 0.0;
            for (var i = 0; i < sentences.Count; i++)
                foreach (var candidate in new[] { batched[i], small[i] })
                {
                    Assert.AreEqual(single[i].Length, candidate.Length, $"sentence {i}");
                    double err = 0, norm = 0;
                    for (var j = 0; j < candidate.Length; j++)
                    {
                        var d = candidate[j] - single[i][j];
                        err += d * d;
                        norm += single[i][j] * (double)single[i][j];
                    }
                    worst = Math.Max(worst, Math.Sqrt(err / Math.Max(norm, 1e-12)));
                }
            Debug.Log($"[EncodeBatch layer {layer}] {sentences.Count} sentences, {sentences.Sum(s => s.Length)} tokens: one by one {singleMs:F0} ms, batched {batchMs:F0} ms; worst relative error {worst:E2}");
            Assert.Less(worst, 1e-3);
        }

        /// <summary>
        /// The last layer's output, RMS-normalized, times one scale per channel must give the final-norm states: then the
        /// layer outputs really are the residual stream the final norm reads.
        /// </summary>
        [Test]
        public void LastLayer_IsWhatTheFinalNormReads()
        {
            var ids = s_Gemma.Tokenize("turn on the living room lights and play some music");
            var last = s_Gemma.Encode(ids, Gemma3Model.NumLayers);
            var hidden = s_Gemma.Encode(ids);
            const int width = Gemma3Model.Width;
            var normalized = new double[last.Length];
            for (var t = 0; t < ids.Length; t++)
            {
                double square = 0;
                for (var i = 0; i < width; i++) square += last[t * width + i] * (double)last[t * width + i];
                var rms = Math.Sqrt(square / width + 1e-6);
                for (var i = 0; i < width; i++) normalized[t * width + i] = last[t * width + i] / rms;
            }
            double err = 0, norm = 0;
            for (var i = 0; i < width; i++)
            {
                double xy = 0, xx = 0;
                for (var t = 0; t < ids.Length; t++)
                {
                    xy += normalized[t * width + i] * hidden[t * width + i];
                    xx += normalized[t * width + i] * normalized[t * width + i];
                }
                var scale = xy / Math.Max(xx, 1e-12);
                for (var t = 0; t < ids.Length; t++)
                {
                    var d = scale * normalized[t * width + i] - hidden[t * width + i];
                    err += d * d;
                    norm += hidden[t * width + i] * (double)hidden[t * width + i];
                }
            }
            var relative = Math.Sqrt(err / norm);
            Debug.Log($"[Layers] final norm from layer {Gemma3Model.NumLayers}: relative error {relative:E2}");
            Assert.Less(relative, 1e-2);
            Assert.Throws<ArgumentOutOfRangeException>(() => s_Gemma.Encode(ids, Gemma3Model.NumLayers + 1));
        }

        [TestCase("")]
        [TestCase(".")]
        [TestCase(" ?! ")]
        public void Jevlike_TextWithoutWordsIsUniform(string text)
        {
            var probs = s_Ranker.Score(text);
            Assert.AreEqual(s_Catalog.Count, probs.Length);
            foreach (var p in probs) Assert.AreEqual(1f / s_Catalog.Count, p, 1e-6f, $"'{text}'");
        }

        [TestCase("Turn on the music.", "play_music")]
        [TestCase("Could you turn the music on?", "play_music")]
        [TestCase("Switch the music off.", "stop_music")]
        [TestCase("TV off now.", "turn_off_tv")]
        [TestCase("PC on.", "turn_on_computer")]
        [TestCase("소리 내려 봐", "volume_down")]
        [TestCase("소리 올려 봐", "volume_up")]
        [TestCase("음악 좀 꺼 줄래", "stop_music")]
        [TestCase("한국의 수도는 어디야?", "web_search")]
        [TestCase("아이유가 누구야", "web_search")]
        [TestCase("부엌 불 꺼 줘", "turn_off_light")]
        [TestCase("텔레비전 좀 틀어 줄래", "turn_on_tv")]
        [TestCase("오늘 부산 날씨 어때", "get_weather")]
        [TestCase("노래 틀어줘", "play_music")]
        [TestCase("소리 좀 키워줘", "volume_up")]
        [TestCase("너 이름이 뭐야", "introduce_self")]
        public void Jevlike_UnderstandsEverydayPhrases(string text, string expected)
        {
            var top = Ranking.Top(s_Catalog, s_Ranker.Score(text), 1)[0];
            Debug.Log($"[Jevlike] '{text}' → {top.Command.id} {top.Probability:P0}");
            Assert.AreEqual(expected, top.Command.id, text);
        }

        [UnityTest]
        public IEnumerator Gemma_RepliesInPersona()
        {
            var persona = Persona.Load();
            foreach (var text in new[] { "Hi there!", "Tell me a fun fact about cats.", "How are you today?", "Thanks a lot", "안녕하세요", "심심해", "잘 자" })
            {
                s_Gemma.SystemPrompt = persona.SystemPrompt(LangDetect.Of(text));
                var start = Time.realtimeSinceStartup;
                var task = s_Gemma.GenerateAsync(text, 40);
                while (!task.GetAwaiter().IsCompleted) yield return null;
                var answer = task.GetAwaiter().GetResult();
                Debug.Log($"[Gemma] {text} → {answer} ({Time.realtimeSinceStartup - start:F2} s)");
                Assert.IsNotEmpty(answer);
                Assert.AreEqual(LangDetect.Of(text), LangDetect.Of(answer), $"'{text}' was answered in the other language: {answer}");
            }
        }

        [UnityTest]
        public IEnumerator TtsVadSttJevlike_EndToEnd()
        {
            using var tts = new SupertonicTts(BackendType.GPUCompute);
            tts.Load(ModelRoots.Tts);
            using var stt = new SenseVoiceRecognizer(BackendType.GPUCompute, SenseVoiceLanguage.Auto);
            stt.Load(ModelRoots.Stt);

            var cases = new[]
            {
                ("Turn on the lights in the kitchen.", "turn_on_light"), ("Please turn off the TV.", "turn_off_tv"), ("Turn the volume down.", "volume_down"),
                ("한국의 수도는 어디야?", "web_search"), ("거실 불 좀 켜 줄래?", "turn_on_light"), ("소리 좀 키워 줘.", "volume_up"),
            };
            foreach (var (sentence, expected) in cases)
            {
                var korean = LangDetect.Of(sentence) == Lang.Ko;
                var synth = tts.Synthesize(sentence, korean ? SupertonicLanguage.ko : SupertonicLanguage.en, "M1");
                while (!synth.GetAwaiter().IsCompleted) yield return null;
                var audio = Resample(synth.GetAwaiter().GetResult(), tts.SampleRate, MicrophoneStream.SampleRate);

                var (started, ended) = RunVad(audio);
                Assert.IsTrue(started && ended, $"VAD missed speech in '{sentence}'");

                var result = stt.Transcribe(audio);
                if (SpeechLanguage.Retry(result.Language) is { } retry) result = stt.Transcribe(audio, retry);
                Assert.AreEqual(korean ? SenseVoiceLanguage.Ko : SenseVoiceLanguage.En, result.Language, sentence);
                var probs = s_Ranker.Score(result.Text);
                var top = Ranking.Top(s_Catalog, probs, 1)[0];
                Debug.Log($"[E2E] '{sentence}' → STT [{result.Language}] '{result.Text}' ({result.InferenceMs:F0} ms) → {top.Command.id} {top.Probability:P0}");
                Assert.AreEqual(expected, top.Command.id, result.Text);
            }
        }

        /// <summary>Linear resampling, enough to feed TTS audio to VAD and STT at 16 kHz.</summary>
        static float[] Resample(float[] input, int from, int to)
        {
            var output = new float[(int)((long)input.Length * to / from)];
            var step = (double)from / to;
            for (var i = 0; i < output.Length; i++)
            {
                var j = (int)(i * step);
                var frac = (float)(i * step - j);
                output[i] = j + 1 < input.Length ? input[j] + (input[j + 1] - input[j]) * frac : input[^1];
            }
            return output;
        }

        static (bool started, bool ended) RunVad(float[] speech)
        {
            using var vad = new SileroVad(BackendType.CPU, 0.5f, 0.35f, 0.5f);
            vad.Load(ModelRoots.Vad);
            bool started = false, ended = false;
            vad.SpeechStarted += () => started = true;
            vad.SpeechEnded += () => ended = true;
            vad.StartListening();
            var padded = new List<float>(new float[8000]);
            padded.AddRange(speech);
            padded.AddRange(new float[16000]);
            var buffer = new float[512];
            for (var i = 0; i + 512 <= padded.Count; i += 512)
            {
                padded.CopyTo(i, buffer, 0, 512);
                vad.PushSamples(buffer, 512);
                vad.Pump();
            }
            return (started, ended);
        }
    }
}
