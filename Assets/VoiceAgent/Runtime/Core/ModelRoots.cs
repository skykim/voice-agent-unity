using System.IO;
using SentisModels;
using Unity.InferenceEngine;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>
    /// Model folders: the Models~ folder of each Hugging Face package, read straight from the package cache. The project
    /// runs in the Editor only; a player has no Models~ folders.
    /// </summary>
    public static class ModelRoots
    {
        public const string VadPackage = "com.sky.sentis.silero-vad";
        public const string SttPackage = "com.sky.sentis.sensevoice";
        public const string TtsPackage = "com.sky.sentis.supertonic";
        public const string GemmaPackage = "com.sky.sentis.gemma3-270m-it";

        public static string Vad => Package(VadPackage);
        public static string Stt => Package(SttPackage);
        public static string Tts => Package(TtsPackage);
        public static string Gemma => Package(GemmaPackage);
        /// <summary>The trained jevlike head graph (VoiceAgent/Train Command Head).</summary>
        public static string IntentHead => Path.Combine(Application.streamingAssetsPath, "Intent", "jevlike_head.sentis");

        public static Gemma3Model LoadGemma(string systemPrompt)
        {
            var gemma = new Gemma3Model(BackendType.GPUCompute) { SystemPrompt = systemPrompt };
            gemma.Load(Gemma);
            return gemma;
        }

        public static string Package(string name)
        {
#if UNITY_EDITOR
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{name}");
            if (info != null) return Path.Combine(info.resolvedPath, "Models~");
            throw new FileNotFoundException($"{name} is not installed (see Packages/manifest.json)");
#else
            throw new System.PlatformNotSupportedException("The models are read from the Editor's package cache: run the scenes in the Editor.");
#endif
        }
    }
}
