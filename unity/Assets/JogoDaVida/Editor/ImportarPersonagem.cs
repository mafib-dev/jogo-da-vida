using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace JogoDaVida.EditorTools
{
    /// <summary>
    /// Configura a importação do personagem gerado no Blender (arte/blender/gerar_personagem.py).
    /// Vale para tudo em Assets/JogoDaVida/Arte/Personagem/:
    ///  - modelo: rig Humanoid (avatar criado do próprio modelo), shape keys importadas,
    ///    escala do arquivo (o FBX já vem em metros), sem animações;
    ///  - materiais: extraídos para a subpasta Materials, usando as texturas da mesma pasta;
    ///  - texturas: mapa normal marcado como normal, transparência no cabelo/barba/cílios/sobrancelhas;
    ///  - cabelo e barba são cascas (shell fur): cada material corta o alfa num limite diferente;
    ///  - óculos: armação na cor do FBX (a v3 não tem lentes; se tiver, ficam no modo Fade).
    /// No fim de cada importação escreve no console se o Avatar ficou válido e a altura do personagem.
    /// </summary>
    public class ImportarPersonagem : AssetPostprocessor
    {
        const string Pasta = "Assets/JogoDaVida/Arte/Personagem/";
        const string Prefixo = "[ImportarPersonagem] ";

        // Materiais que usam recorte por alpha, com o limite (_Cutoff) de cada um.
        // Cabelo e barba: o alfa da textura é a "altura" do fio; cada casca mostra só o que passa
        // do seu limite (mesmos valores de CABELO_CASCAS / BARBA_CASCAS em gerar_personagem.py).
        static readonly Dictionary<string, float> Recorte = new Dictionary<string, float>
        {
            { "M_Cabelo", 0.14f }, { "M_Cabelo_Meio", 0.42f }, { "M_Cabelo_Topo", 0.68f },
            { "M_Barba", 0.30f }, { "M_Barba_Externa", 0.62f },
            { "M_Cilios", 0.4f }, { "M_Sobrancelhas", 0.4f },
        };

        // Materiais semitransparentes (modo Fade do Standard).
        static readonly string[] Transparentes = { "M_Oculos_Lente" };

        // Texturas cujo alfa é recorte (fios).
        static readonly string[] TexturasComAlfa = { "cabelo", "barba", "cilios", "sobrancelhas" };

        static bool EhDoPersonagem(string caminho) =>
            caminho.Replace('\\', '/').StartsWith(Pasta);

        // ---------- Modelo ----------

        void OnPreprocessModel()
        {
            if (!EhDoPersonagem(assetPath)) return;
            var mi = (ModelImporter)assetImporter;

            // Escala: o Blender exporta em metros com a escala aplicada; usa a do arquivo.
            mi.useFileScale = true;
            mi.globalScale = 1f;

            // Rig Humanoid com avatar criado a partir deste modelo (ossos mixamorig:*).
            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.importAnimation = false;

            // Shape keys (corpo e expressões) viram blend shapes.
            mi.importBlendShapes = true;
            mi.importBlendShapeNormals = ModelImporterNormals.Calculate;

            // Materiais extraídos para Materials/, procurando as texturas nesta pasta.
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.materialLocation = ModelImporterMaterialLocation.External;
            mi.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            mi.materialSearch = ModelImporterMaterialSearch.Local;
        }

        /// <summary>Monta o material Standard a partir da descrição que vem no FBX.</summary>
        void OnPreprocessMaterialDescription(MaterialDescription descricao, Material material, AnimationClip[] clipes)
        {
            if (!EhDoPersonagem(assetPath)) return;

            material.shader = Shader.Find("Standard");
            material.SetColor("_Color", Color.white);
            material.SetFloat("_Glossiness", 0.15f);
            material.SetFloat("_Metallic", 0f);

            var cor = AcharTextura(descricao, "DiffuseColor");
            if (cor != null) material.SetTexture("_MainTex", cor);
            else if (descricao.TryGetProperty("DiffuseColor", out Vector4 rgb))
                material.SetColor("_Color", new Color(rgb.x, rgb.y, rgb.z, 1f));   // ex.: armação dos óculos

            var normal = AcharTextura(descricao, "NormalMap") ?? AcharTextura(descricao, "Bump");
            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
            }

            if (Recorte.TryGetValue(material.name, out float limite))
            {
                // Modo "Cutout" do Standard
                material.SetFloat("_Mode", 1f);
                material.SetOverrideTag("RenderType", "TransparentCutout");
                material.SetFloat("_Cutoff", limite);
                material.EnableKeyword("_ALPHATEST_ON");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }

            if (Transparentes.Contains(material.name))
            {
                // Modo "Fade" do Standard: lente quase invisível, só um reflexo leve.
                float alfa = descricao.TryGetProperty("Opacity", out float op) && op < 1f ? op : 0.08f;
                var c = material.GetColor("_Color");
                material.SetColor("_Color", new Color(c.r, c.g, c.b, Mathf.Clamp(alfa, 0.04f, 0.3f)));
                material.SetFloat("_Glossiness", 0.9f);
                material.SetFloat("_Mode", 2f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.EnableKeyword("_ALPHABLEND_ON");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
        }

        /// <summary>Textura de uma propriedade do FBX; se o Unity não resolveu, procura pelo nome do arquivo nesta pasta.</summary>
        Texture AcharTextura(MaterialDescription descricao, string propriedade)
        {
            if (!descricao.TryGetProperty(propriedade, out TexturePropertyDescription tex)) return null;
            if (tex.texture != null) return tex.texture;
            if (string.IsNullOrEmpty(tex.path)) return null;
            var caminho = Pasta + Path.GetFileName(tex.path);
            return AssetDatabase.LoadAssetAtPath<Texture>(caminho);
        }

        // ---------- Texturas ----------

        void OnPreprocessTexture()
        {
            if (!EhDoPersonagem(assetPath)) return;
            var ti = (TextureImporter)assetImporter;
            var nome = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();

            ti.maxTextureSize = 2048;
            ti.textureType = nome.EndsWith("_normal") ? TextureImporterType.NormalMap : TextureImporterType.Default;
            ti.alphaIsTransparency = TexturasComAlfa.Any(p => nome.StartsWith(p));
            // Cabelo/barba: os mipmaps mantêm a cobertura do recorte (senão os fios somem de longe).
            ti.mipMapsPreserveCoverage = nome.StartsWith("cabelo") || nome.StartsWith("barba");
            ti.alphaTestReferenceValue = 0.3f;
        }

        // ---------- Conferência ----------

        static void OnPostprocessAllAssets(string[] importados, string[] apagados, string[] movidos, string[] origens)
        {
            foreach (var caminho in importados)
            {
                if (!EhDoPersonagem(caminho) || !caminho.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                Conferir(caminho);
            }
        }

        static void Conferir(string caminho)
        {
            var avatar = AssetDatabase.LoadAllAssetsAtPath(caminho).OfType<Avatar>().FirstOrDefault();
            if (avatar == null)
                Debug.LogError(Prefixo + caminho + ": nenhum Avatar foi criado.");
            else if (!avatar.isValid || !avatar.isHuman)
                Debug.LogError(Prefixo + caminho + ": Avatar inválido (isValid=" + avatar.isValid + ", isHuman=" + avatar.isHuman + ").");
            else
                Debug.Log(Prefixo + caminho + ": Avatar Humanoid válido.");

            var modelo = AssetDatabase.LoadAssetAtPath<GameObject>(caminho);
            if (modelo == null) return;
            var copia = Object.Instantiate(modelo);
            try
            {
                var rends = copia.GetComponentsInChildren<Renderer>();
                if (rends.Length == 0) return;
                // Os bounds do SkinnedMeshRenderer têm folga; mede pelos vértices da malha na pose de importação.
                float min = float.MaxValue, max = float.MinValue;
                var assada = new Mesh();
                foreach (var s in rends.OfType<SkinnedMeshRenderer>())
                {
                    s.BakeMesh(assada, true);
                    foreach (var v in assada.vertices)
                    {
                        float y = s.transform.TransformPoint(v).y;
                        if (y < min) min = y;
                        if (y > max) max = y;
                    }
                }
                Object.DestroyImmediate(assada);
                var caixa = new Bounds(Vector3.zero, new Vector3(0, max - min, 0));
                var formas = rends.OfType<SkinnedMeshRenderer>().Sum(s => s.sharedMesh ? s.sharedMesh.blendShapeCount : 0);
                Debug.Log(Prefixo + string.Format("altura {0:0.00} m, {1} malhas, {2} blend shapes no total.",
                    caixa.size.y, rends.Length, formas));
                if (caixa.size.y < 1.5f || caixa.size.y > 2.0f)
                    Debug.LogWarning(Prefixo + "altura fora do esperado (~1,75 m); confira a escala do FBX.");
            }
            finally
            {
                Object.DestroyImmediate(copia);
            }
        }
    }
}
