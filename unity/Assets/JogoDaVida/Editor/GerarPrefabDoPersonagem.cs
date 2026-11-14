using UnityEditor;
using UnityEngine;

namespace JogoDaVida.EditorTools
{
    /// <summary>
    /// Gera Assets/JogoDaVida/Resources/PersonagemHumano.prefab a partir do Personagem.fbx,
    /// para o jogo carregar o personagem em tempo de execução (Resources.Load).
    /// O prefab referencia os materiais .mat de Arte/Personagem/Materials, então materiais,
    /// texturas e as variantes do shader Standard entram no build do Android.
    /// Menu: Jogo da Vida → Gerar prefab do personagem. Linha de comando:
    /// Unity.exe -batchmode -quit -projectPath unity -executeMethod JogoDaVida.EditorTools.GerarPrefabDoPersonagem.Gerar
    /// Rode de novo sempre que o FBX for reexportado.
    /// </summary>
    public static class GerarPrefabDoPersonagem
    {
        const string Fbx = "Assets/JogoDaVida/Arte/Personagem/Personagem.fbx";
        const string Prefab = "Assets/JogoDaVida/Resources/PersonagemHumano.prefab";

        [MenuItem("Jogo da Vida/Gerar prefab do personagem")]
        public static void Gerar()
        {
            var modelo = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            if (modelo == null)
            {
                Debug.LogError("[GerarPrefabDoPersonagem] FBX não encontrado: " + Fbx);
                return;
            }

            var instancia = (GameObject)PrefabUtility.InstantiatePrefab(modelo);
            try
            {
                instancia.name = "PersonagemHumano";
                foreach (var r in instancia.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    r.receiveShadows = true;
                }
                PrefabUtility.SaveAsPrefabAsset(instancia, Prefab, out bool ok);
                Debug.Log("[GerarPrefabDoPersonagem] " + (ok ? "prefab salvo em " : "falha ao salvar ") + Prefab);
            }
            finally
            {
                Object.DestroyImmediate(instancia);
            }
        }
    }
}
