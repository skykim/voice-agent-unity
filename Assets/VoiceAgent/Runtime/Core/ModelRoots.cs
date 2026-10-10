using System.IO;
using SentisModels;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Networking;

namespace VoiceAgent
{
    /// <summary>
    /// Model folders. In the Editor, the Models~ folder of each Hugging Face package, read straight from the package cache.
    /// In a player, the copy the build puts in StreamingAssets/Models/&lt;package&gt; (see ModelBuildStaging). Android keeps
    /// StreamingAssets inside the APK, where files can't be opened, so <see cref="PrepareAsync"/> first copies them out to
    /// persistentDataPath; call it before loading any model.
    /// </summary>
    public static class ModelRoots
    {
        public const string VadPackage = "com.sky.sentis.silero-vad";
        public const string SttPackage = "com.sky.sentis.sensevoice";
        public const string TtsPackage = "com.sky.sentis.supertonic";
        public const string GemmaPackage = "com.sky.sentis.gemma3-270m-it";
        public static readonly string[] Packages = { VadPackage, SttPackage, TtsPackage, GemmaPackage };

        /// <summary>StreamingAssets folder the build copies the package models into.</summary>
        public const string ModelsFolder = "Models";
        /// <summary>StreamingAssets-relative list of the files a player copies out on Android, one "path\tbytes" per line.</summary>
        public const string ManifestFile = "voiceagent_files.txt";
        const string IntentHeadFile = "Intent/decision_ai_head.sentis";

        public static string Vad => Package(VadPackage);
        public static string Stt => Package(SttPackage);
        public static string Tts => Package(TtsPackage);
        public static string Gemma => Package(GemmaPackage);
        /// <summary>The trained Decision AI head graph (VoiceAgent/Train Command Head).</summary>
        public static string IntentHead => Path.Combine(Application.isEditor ? Application.streamingAssetsPath : RuntimeRoot, IntentHeadFile);

        /// <summary>True where StreamingAssets is a URL inside the app package (Android) instead of a folder.</summary>
        static bool StreamingAssetsInPackage => Application.streamingAssetsPath.Contains("://");

        /// <summary>Folder the player reads StreamingAssets files from: the folder itself, or the Android copy.</summary>
        static string RuntimeRoot => StreamingAssetsInPackage ? Path.Combine(Application.persistentDataPath, "StreamingAssets") : Application.streamingAssetsPath;

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
            return Path.Combine(RuntimeRoot, ModelsFolder, name);
#endif
        }

        /// <summary>
        /// Android: copies every file in the build's manifest from the APK to persistentDataPath, skipping files already
        /// copied at the right size (the first launch copies about 1.4 GB). Other platforms read StreamingAssets in place.
        /// </summary>
        public static async Awaitable PrepareAsync(System.Action<string> progress = null)
        {
            if (Application.isEditor || !StreamingAssetsInPackage) return;
            var manifest = await ReadStreamingText(ManifestFile);
            foreach (var line in manifest.Split('\n'))
            {
                var fields = line.Trim().Split('\t');
                if (fields.Length != 2 || !long.TryParse(fields[1], out var bytes)) continue;
                var target = Path.Combine(RuntimeRoot, fields[0]);
                if (File.Exists(target) && new FileInfo(target).Length == bytes) continue;

                progress?.Invoke($"Copying {fields[0]} ({bytes / (1024 * 1024)} MB)…");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var part = target + ".part";
                using (var request = UnityWebRequest.Get($"{Application.streamingAssetsPath}/{fields[0]}"))
                {
                    request.downloadHandler = new DownloadHandlerFile(part) { removeFileOnAbort = true };
                    await request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                        throw new IOException($"Couldn't copy {fields[0]} out of the APK: {request.error}");
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(part, target);
            }
        }

        static async Awaitable<string> ReadStreamingText(string relative)
        {
            using var request = UnityWebRequest.Get($"{Application.streamingAssetsPath}/{relative}");
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
                throw new FileNotFoundException($"{relative} is missing from StreamingAssets: {request.error}");
            return request.downloadHandler.text;
        }
    }
}
