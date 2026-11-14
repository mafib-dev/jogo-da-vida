using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace JogoDaVida.EditorTools
{
    /// <summary>
    /// Prepara a cena principal e as Player Settings do Android e gera o APK.
    /// Menu: Jogo da Vida → Gerar APK. Linha de comando:
    /// Unity.exe -batchmode -quit -projectPath unity -buildTarget Android -executeMethod JogoDaVida.EditorTools.BuildAndroid.GerarApk
    /// </summary>
    public static class BuildAndroid
    {
        const string Cena = "Assets/JogoDaVida/Cenas/Principal.unity";
        const string Apk = "../Builds/JogoDaVida.apk"; // relativo à pasta unity/

        [MenuItem("Jogo da Vida/Preparar projeto")]
        public static void Preparar()
        {
            // Cena vazia: o JogoApp sobe sozinho e cria a própria câmera.
            if (!File.Exists(Cena))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Cena));
                var cena = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(cena, Cena);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(Cena, true) };

            // Material do boneco em Resources: garante que o shader Standard vá para o APK.
            const string mat = "Assets/JogoDaVida/Resources/BonecoMaterial.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(mat) == null)
                AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), mat);

            PlayerSettings.companyName = "mafib";
            PlayerSettings.productName = "Jogo da Vida";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.mafib.jogodavida");
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            if (PlayerSettings.Android.minSdkVersion < AndroidSdkVersions.AndroidApiLevel25)
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Jogo da Vida/Gerar APK")]
        public static void GerarApk()
        {
            Preparar();
            EditorUserBuildSettings.buildAppBundle = false;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Apk)));
            var relatorio = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Cena },
                locationPathName = Apk,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            var r = relatorio.summary;
            Debug.Log($"[BuildAndroid] {r.result}: {r.outputPath} ({r.totalSize} bytes, {r.totalErrors} erros)");
            if (Application.isBatchMode) EditorApplication.Exit(r.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
