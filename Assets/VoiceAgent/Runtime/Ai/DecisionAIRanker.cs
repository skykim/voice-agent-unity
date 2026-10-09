using System;
using System.Diagnostics;
using System.IO;
using SentisModels;
using Unity.InferenceEngine;

namespace VoiceAgent
{
    /// <summary>
    /// Decision AI option scorer (based on jevlike, github.com/vinnylarouge/jevlike, MIT) over frozen Gemma3 token states from the layer the
    /// head was trained on:
    /// each command's option vector queries the context tokens, a shared dot product gives one logit per
    /// command, and a softmax runs across commands: one encoder pass, no decoding. With several heads the
    /// probabilities are averaged; <see cref="PolarityGuard"/> then settles on/off pairs by their direction words.
    /// The head is one Sentis graph (StreamingAssets/Intent/decision_ai_head.sentis from VoiceAgent/Train Command Head): token
    /// states [length × width] → command probabilities, run on the CPU, with the Gemma3 layer, token limit, command order
    /// and validation numbers as constant outputs.
    /// </summary>
    public sealed class DecisionAIRanker : IDisposable
    {
        /// <summary>The graph's input: token states [length × width] without the &lt;bos&gt; row.</summary>
        public const string InputName = "states";
        /// <summary>The graph's outputs: command probabilities, then the constants the trainer writes.</summary>
        public const string ProbsName = "probs", LayerName = "layer", MaxTokensName = "max_tokens", IntentsName = "intents", ValidationName = "validation";

        readonly Gemma3Model m_Gemma;
        readonly Worker m_Worker;
        readonly PolarityGuard m_Guard;
        readonly int m_Layer, m_MaxTokens, m_Count;

        public double LastEncoderMs { get; private set; }
        public double LastHeadMs { get; private set; }
        public float ValidationAccuracy { get; }
        public int Layer => m_Layer;

        public DecisionAIRanker(Gemma3Model gemma, string headPath, CommandCatalog catalog)
        {
            m_Gemma = gemma;
            var name = Path.GetFileName(headPath);
            if (!File.Exists(headPath)) throw new FileNotFoundException($"{name} is missing; train the head with VoiceAgent/Train Command Head", headPath);
            m_Worker = new Worker(ModelLoader.Load(headPath), BackendType.CPU);
            string[] intents;
            try
            {
                // One pass on a single zero token reads the constant outputs (and warms the worker up).
                using (var probe = new Tensor<float>(new TensorShape(1, Gemma3Model.Width), new float[Gemma3Model.Width])) m_Worker.Schedule(probe);
                m_Layer = Read<int>(LayerName)[0];
                m_MaxTokens = Read<int>(MaxTokensName)[0];
                ValidationAccuracy = Read<float>(ValidationName)[0];
                intents = new string(Array.ConvertAll(Read<int>(IntentsName), c => (char)c)).Split('\n');
                if (m_Layer < Gemma3Model.FinalNorm || m_Layer > Gemma3Model.NumLayers) throw new InvalidDataException($"head layer {m_Layer} is not a Gemma3 layer");
                var upToDate = intents.Length == catalog.Count;
                for (var i = 0; upToDate && i < catalog.Count; i++) upToDate = intents[i] == catalog.Commands[i].id;
                if (!upToDate) throw new InvalidDataException($"{name} is out of date with Commands.json; retrain it with VoiceAgent/Train Command Head");
            }
            catch
            {
                m_Worker.Dispose();
                throw;
            }
            m_Count = intents.Length;
            m_Guard = PolarityGuard.Load(intents);
        }

        T[] Read<T>(string output) where T : unmanaged
        {
            using var tensor = (m_Worker.PeekOutput(output) as Tensor<T>).ReadbackAndClone();
            return tensor.DownloadToArray();
        }

        /// <summary>
        /// Lowercase without punctuation, as every training sentence is (TrainingData), so STT's
        /// "Switch the music off." and a typed "switch the music off" score the same.
        /// </summary>
        public static string Normalize(string text) => TextScan.CollapseSpaces(TextScan.ReplaceRuns(text.ToLowerInvariant(), TextScan.Punctuation, " "));

        public float[] Score(string text)
        {
            var clock = Stopwatch.StartNew();
            var normalized = Normalize(text ?? string.Empty);
            // A partial transcript can be only punctuation ("." or "?"): nothing is left to encode, and the tokenizer
            // throws on an empty string. Every command is then equally likely.
            if (normalized.Length == 0)
            {
                LastEncoderMs = LastHeadMs = 0;
                return ScoreStates(ReadOnlySpan<float>.Empty, 0);
            }
            var ids = m_Gemma.Tokenize(normalized, m_MaxTokens);
            var states = m_Gemma.Encode(ids, m_Layer);
            LastEncoderMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            // Row 0 is <bos>, an attention sink with huge activations; training drops it too.
            var probs = m_Guard.Apply(normalized, ScoreStates(states.AsSpan(Gemma3Model.Width), ids.Length - 1));
            LastHeadMs = clock.Elapsed.TotalMilliseconds;
            return probs;
        }

        /// <summary>Heads only: token states [length × width] → command probabilities, averaged over the heads.</summary>
        public float[] ScoreStates(ReadOnlySpan<float> states, int length)
        {
            var n = m_Count;
            if (length <= 0)
            {
                var uniform = new float[n];
                Array.Fill(uniform, 1f / n);
                return uniform;
            }
            using var input = new Tensor<float>(new TensorShape(length, Gemma3Model.Width), states[..(length * Gemma3Model.Width)].ToArray());
            m_Worker.Schedule(input);
            return Read<float>(ProbsName);
        }

        public void Dispose() => m_Worker?.Dispose();
    }
}
