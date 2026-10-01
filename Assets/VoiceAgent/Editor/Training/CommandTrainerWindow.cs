using System;
using System.IO;
using UnityEditor;
using SentisModels;
using UnityEngine;

namespace VoiceAgent.Editor
{
    /// <summary>
    /// VoiceAgent/Train Command Head…: every <see cref="CommandTrainer.Settings"/> field, a Train button, and the last run's
    /// results and log. The settings are remembered per project (EditorPrefs).
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
            window.minSize = new Vector2(440, 520);
        }

        void OnEnable() => m_Settings = LoadSettings();

        void OnDisable() => SaveSettings();

        static CommandTrainer.Settings LoadSettings()
        {
            var settings = new CommandTrainer.Settings();
            var json = EditorPrefs.GetString(PrefsKey, string.Empty);
            if (json.Length > 0) JsonUtility.FromJsonOverwrite(json, settings);
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
            EditorGUILayout.HelpBox("Encodes every training sentence once with the frozen Gemma3 encoder, then trains the jevlike head " +
                                    "on the cached states. About a minute and a half with the defaults.", MessageType.None);

            Section("Data");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Languages", "Dataset rows and extra phrases to train on."));
                var english = GUILayout.Toggle(s.Languages.Contains("en"), "English", GUILayout.Width(80));
                var korean = GUILayout.Toggle(s.Languages.Contains("ko"), "Korean", GUILayout.Width(80));
                s.Languages = english && korean ? "en,ko" : english ? "en" : korean ? "ko" : string.Empty;
            }
            s.RepeatExtra = Int("Repeat extra phrases", "How often the extra phrases repeat, so about 30 phrasings aren't drowned out by about 100 dataset rows.", s.RepeatExtra, 1);
            s.MaxTokens = Int("Max tokens", "Longest sentence in tokens, including <bos>.", s.MaxTokens, 2);
            PathField("Dataset folder", "Folder with train.jsonl and val.jsonl.", s.Dataset, true, "json", v => m_Settings.Dataset = v);
            if (string.IsNullOrWhiteSpace(s.Dataset))
                EditorGUILayout.LabelField(" ", "Empty: the local checkout next to the project, else a one-time download into Library/VoiceAgent/functiongemma.", EditorStyles.wordWrappedMiniLabel);

            Section("Head");
            s.Layer = EditorGUILayout.IntSlider(new GUIContent("Gemma3 layer",
                "Layer whose token states the head reads: 1 to 18, or 0 for the final norm. The runtime reads the layer from the head file."),
                s.Layer, Gemma3Model.FinalNorm, Gemma3Model.NumLayers);
            s.Heads = Int("Heads", "Heads trained from different random starts and averaged. Steadier, but each head takes as long again.", s.Heads, 1);
            s.Rank = Int("Rank", "Width of the query/key/value projections. Bigger means more capacity and a bigger head file.", s.Rank, 1);
            s.Epochs = Int("Epochs", "Passes over the training sentences; the epoch with the lowest validation loss is kept.", s.Epochs, 1);
            s.BatchSize = Int("Batch size", "Sentences per optimizer step.", s.BatchSize, 1);
            s.Seed = EditorGUILayout.IntField(new GUIContent("Seed", "Random seed for the split, the augmentation and the initialization."), s.Seed);
            s.FinalOnAllSentences = EditorGUILayout.Toggle(new GUIContent("Final head on all sentences",
                "After the first run finds the best epoch, train the written head again on the training and validation sentences " +
                "together, so it also learns the held-out phrases. The validation numbers still come from the first run."), s.FinalOnAllSentences);

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

            Section("Evaluation");
            s.Guard = EditorGUILayout.Toggle(new GUIContent("On/off guard", "Score validation with the direction-word guard (Resources/Polarity.json), as the runtime does."), s.Guard);

            Section("Output");
            PathField("Head file", "Where the trained head is written.", s.HeadOut, false, "json", v => m_Settings.HeadOut = v);
            if (SamePath(s.HeadOut, ModelRoots.IntentHead))
                EditorGUILayout.HelpBox("This is the head the runtime loads: training overwrites it.", MessageType.Info);
            PathField("Report file", "Accuracy, on/off check, timings and machine, as JSON.", s.Report, false, "json", v => m_Settings.Report = v);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to defaults", GUILayout.Height(28)))
                {
                    m_Settings = new CommandTrainer.Settings();
                    GUI.FocusControl(null);
                }
                using (new EditorGUI.DisabledScope(s.Languages.Length == 0))
                    if (GUILayout.Button("Train", GUILayout.Height(28)))
                        EditorApplication.delayCall += Train;
            }
            if (s.Languages.Length == 0) EditorGUILayout.HelpBox("Choose at least one language.", MessageType.Warning);

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
            EditorGUILayout.LabelField("Written head", m_Last.FinalTrainSentences > 0
                ? $"retrained on all {m_Last.FinalTrainSentences} sentences for {string.Join(", ", m_Last.KeptEpochs)} epoch(s)"
                : $"first run, epoch {string.Join(", ", m_Last.KeptEpochs)}");
            EditorGUILayout.LabelField("Time", $"{m_Last.TotalSeconds:F1} s");
            m_LogScroll = EditorGUILayout.BeginScrollView(m_LogScroll, GUILayout.Height(220));
            EditorGUILayout.TextArea(m_Last.Log ?? string.Empty, EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        void Train()
        {
            var settings = m_Settings;
            if (SamePath(settings.HeadOut, ModelRoots.IntentHead) &&
                !EditorUtility.DisplayDialog("Train the command head", $"Overwrites {Relative(settings.HeadOut)}, the head the runtime loads.", "Train", "Cancel"))
                return;
            SaveSettings();
            try
            {
                m_Last = CommandTrainer.Train(settings, (text, progress) => EditorUtility.DisplayCancelableProgressBar("Training the command head", text, progress));
                if (Path.GetFullPath(settings.HeadOut).StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase)) AssetDatabase.Refresh();
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

        /// <summary>A text field plus a browse button; the panel opens after this repaint so it doesn't break the layout.</summary>
        void PathField(string label, string tooltip, string value, bool folder, string extension, Action<string> set)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var edited = EditorGUILayout.TextField(new GUIContent(label, tooltip), value ?? string.Empty);
                if (edited != (value ?? string.Empty)) set(edited);
                if (!GUILayout.Button("…", GUILayout.Width(28))) return;
                EditorApplication.delayCall += () =>
                {
                    var start = string.IsNullOrWhiteSpace(value) ? ProjectRoot : folder ? value : Path.GetDirectoryName(value);
                    var picked = folder
                        ? EditorUtility.OpenFolderPanel(label, start, string.Empty)
                        : EditorUtility.SaveFilePanel(label, start, Path.GetFileName(value ?? string.Empty), extension);
                    if (string.IsNullOrEmpty(picked)) return;
                    set(picked);
                    SaveSettings();
                    Repaint();
                };
            }
        }

        static bool SamePath(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        static string Relative(string path) => Path.GetRelativePath(ProjectRoot, path);
    }
}
