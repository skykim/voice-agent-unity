using System;
using System.IO;
using UnityEditor;
using SentisModels;
using UnityEngine;

namespace VoiceAgent.Editor
{
    /// <summary>
    /// VoiceAgent/Train Command Head…: the head's training settings (layer, size, epochs, optimizer), a Train button, and
    /// the last run's results and log. The settings are remembered per project (EditorPrefs). The rest of
    /// <see cref="CommandTrainer.Settings"/> always keeps its default: English and Korean, the on/off guard, the final head
    /// on all sentences, and the head written to <see cref="ModelRoots.IntentHead"/>.
    /// </summary>
    public sealed class CommandTrainerWindow : EditorWindow
    {
        CommandTrainer.Settings m_Settings;
        CommandTrainer.Result m_Last;
        Vector2 m_Scroll, m_LogScroll;
        bool m_ShowOptimizer;

        static string PrefsKey => "VoiceAgent.CommandTrainer.Settings." + Application.dataPath;
        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        [MenuItem("VoiceAgent/Train Command Head…")]
        static void Open()
        {
            var window = GetWindow<CommandTrainerWindow>("Command Head Trainer");
            window.minSize = new Vector2(440, 480);
        }

        void OnEnable() => m_Settings = LoadSettings();

        void OnDisable() => SaveSettings();

        /// <summary>The remembered settings, with the fields the window doesn't show put back to their defaults.</summary>
        static CommandTrainer.Settings LoadSettings()
        {
            var settings = new CommandTrainer.Settings();
            var json = EditorPrefs.GetString(PrefsKey, string.Empty);
            if (json.Length > 0) JsonUtility.FromJsonOverwrite(json, settings);
            var defaults = new CommandTrainer.Settings();
            settings.Languages = defaults.Languages;
            settings.Guard = defaults.Guard;
            settings.FinalOnAllSentences = defaults.FinalOnAllSentences;
            settings.HeadOut = defaults.HeadOut;
            settings.Report = defaults.Report;
            settings.Dataset = defaults.Dataset;
            return settings;
        }

        void SaveSettings()
        {
            if (m_Settings != null) EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(m_Settings));
        }

        void OnGUI()
        {
            var s = m_Settings;
            EditorGUI.BeginChangeCheck();
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            EditorGUILayout.HelpBox("Encodes every training sentence once with the frozen Gemma3 encoder, then trains the Decision AI head " +
                                    $"on the cached states and writes it to {Relative(ModelRoots.IntentHead)}. About a minute with the defaults.", MessageType.None);

            Section("Head");
            s.Layer = EditorGUILayout.IntSlider(new GUIContent("Gemma3 layer",
                "Layer whose token states the head reads: 1 to 18, or 0 for the final norm. The head file records it for the runtime."),
                s.Layer, Gemma3Model.FinalNorm, Gemma3Model.NumLayers);
            s.Heads = Int("Heads", "Heads trained from different random starts and averaged. Steadier, but each head takes as long again.", s.Heads, 1);
            s.Rank = Int("Rank", "Width of the query/key/value projections. Bigger means more capacity (the head file stays small).", s.Rank, 1);
            s.MaxTokens = Int("Max tokens", "Longest sentence in tokens, including <bos>.", s.MaxTokens, 2);

            Section("Training");
            s.Epochs = Int("Epochs", "Passes over the training sentences; the epoch with the lowest validation loss is kept.", s.Epochs, 1);
            s.BatchSize = Int("Batch size", "Sentences per optimizer step.", s.BatchSize, 1);
            s.RepeatExtra = Int("Repeat extra phrases", "How often the extra phrases repeat, so about 30 phrasings aren't drowned out by about 100 dataset rows.", s.RepeatExtra, 1);
            s.Seed = EditorGUILayout.IntField(new GUIContent("Seed", "Random seed for the split, the augmentation and the initialization."), s.Seed);

            m_ShowOptimizer = EditorGUILayout.Foldout(m_ShowOptimizer, "Optimizer (AdamW)", true);
            if (m_ShowOptimizer)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    s.LearningRate = Mathf.Max(1e-6f, EditorGUILayout.FloatField(new GUIContent("Learning rate"), s.LearningRate));
                    s.WeightDecay = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("Weight decay", "Decoupled weight decay on every parameter."), s.WeightDecay));
                    s.Dropout = EditorGUILayout.Slider(new GUIContent("Dropout", "Dropout on the token states during training."), s.Dropout, 0f, 0.9f);
                    s.LabelSmoothing = EditorGUILayout.Slider(new GUIContent("Label smoothing"), s.LabelSmoothing, 0f, 0.5f);
                    s.ClipNorm = Mathf.Max(1e-3f, EditorGUILayout.FloatField(new GUIContent("Gradient clip", "Largest gradient norm per step."), s.ClipNorm));
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to defaults", GUILayout.Height(28)))
                {
                    m_Settings = new CommandTrainer.Settings();
                    GUI.FocusControl(null);
                }
                if (GUILayout.Button("Train", GUILayout.Height(28)))
                    EditorApplication.delayCall += Train;
            }

            if (m_Last != null) LastRun();
            EditorGUILayout.EndScrollView();
            if (EditorGUI.EndChangeCheck()) SaveSettings();
        }

        void LastRun()
        {
            Section("Last run");
            EditorGUILayout.LabelField("Validation", $"loss {m_Last.ValLoss:F3} / accuracy {m_Last.ValAccuracy:F3} / calibration error {m_Last.ValEce:F3}");
            EditorGUILayout.LabelField("On/off check", $"{m_Last.PolarityCorrect} / {m_Last.PolarityTotal}");
            if (m_Last.BenchmarkTotal > 0)
                EditorGUILayout.LabelField("Benchmark", $"{m_Last.BenchmarkRight} right / {m_Last.BenchmarkAsks} ask / {m_Last.BenchmarkWrong} wrong (of {m_Last.BenchmarkTotal})");
            EditorGUILayout.LabelField("Gemma3 layer", m_Last.Layer == Gemma3Model.FinalNorm ? "final norm" : m_Last.Layer.ToString());
            EditorGUILayout.LabelField("Sentences", $"{m_Last.TrainSentences} training / {m_Last.ValSentences} validation");
            EditorGUILayout.LabelField("Time", $"{m_Last.TotalSeconds:F1} s");
            m_LogScroll = EditorGUILayout.BeginScrollView(m_LogScroll, GUILayout.Height(220));
            EditorGUILayout.TextArea(m_Last.Log ?? string.Empty, EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        void Train()
        {
            if (!EditorUtility.DisplayDialog("Train the command head", $"Overwrites {Relative(ModelRoots.IntentHead)}, the head the runtime loads.", "Train", "Cancel"))
                return;
            SaveSettings();
            var settings = m_Settings;
            try
            {
                m_Last = CommandTrainer.Train(settings, (text, progress) => EditorUtility.DisplayCancelableProgressBar("Training the command head", text, progress));
                AssetDatabase.Refresh();
                ShowNotification(new GUIContent($"Validation accuracy {m_Last.ValAccuracy:F3}"));
            }
            catch (OperationCanceledException)
            {
                ShowNotification(new GUIContent("Training cancelled"));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Training failed", e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        static void Section(string title)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        static int Int(string label, string tooltip, int value, int min) =>
            Mathf.Max(min, EditorGUILayout.IntField(new GUIContent(label, tooltip), value));

        static string Relative(string path) => Path.GetRelativePath(ProjectRoot, path);
    }
}
