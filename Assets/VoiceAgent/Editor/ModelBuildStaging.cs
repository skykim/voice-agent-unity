using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VoiceAgent.Editor
{
    /// <summary>
    /// Player builds read the models from StreamingAssets (see <see cref="ModelRoots"/>): before a build this copies each
    /// package's Models~ folder to StreamingAssets/Models/&lt;package&gt; and writes the file manifest Android copies out
    /// on first launch. The staged copy is removed after the build so the Editor keeps reading the package cache.
    /// </summary>
    sealed class ModelBuildStaging : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        static string StreamingAssets => Application.streamingAssetsPath;
        static string StagedModels => Path.Combine(StreamingAssets, ModelRoots.ModelsFolder);
        static string Manifest => Path.Combine(StreamingAssets, ModelRoots.ManifestFile);

        public void OnPreprocessBuild(BuildReport report)
        {
            Clean();
            foreach (var package in ModelRoots.Packages)
                CopyFolder(ModelRoots.Package(package), Path.Combine(StagedModels, package));

            var manifest = new StringBuilder();
            foreach (var file in Directory.GetFiles(StreamingAssets, "*", SearchOption.AllDirectories).OrderBy(f => f))
            {
                if (file.EndsWith(".meta") || Path.GetFileName(file).StartsWith(".") || file == Manifest) continue;
                var relative = Path.GetRelativePath(StreamingAssets, file).Replace('\\', '/');
                manifest.Append(relative).Append('\t').Append(new FileInfo(file).Length).Append('\n');
            }
            File.WriteAllText(Manifest, manifest.ToString());
        }

        public void OnPostprocessBuild(BuildReport report) => Clean();

        /// <summary>Removes a staged copy left behind by a build that failed before its postprocess step.</summary>
        [InitializeOnLoadMethod]
        static void CleanAfterFailedBuild()
        {
            if (!BuildPipeline.isBuildingPlayer) Clean();
        }

        static void Clean()
        {
            if (Directory.Exists(StagedModels)) Directory.Delete(StagedModels, true);
            File.Delete(StagedModels + ".meta");
            File.Delete(Manifest);
            File.Delete(Manifest + ".meta");
        }

        static void CopyFolder(string source, string target)
        {
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).StartsWith(".")) continue;
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }
    }
}
