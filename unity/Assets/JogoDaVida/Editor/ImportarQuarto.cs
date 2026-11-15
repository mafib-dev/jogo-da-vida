using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace JogoDaVida.EditorTools
{
    /// <summary>
    /// Importação dos assets do quarto (móveis do Poly Haven/Sketchfab e texturas de piso, parede e tecido).
    /// Vale para tudo em Assets/JogoDaVida/Arte/Quarto/:
    ///
    ///  - Modelos (Arte/Quarto/&lt;Tipo&gt;/&lt;id&gt;/*.fbx): sem rig nem animação, escala do arquivo, materiais
    ///    extraídos para a subpasta Materials com o shader Standard (cor = *_diff*, relevo = *_nor_gl*;
    ///    os mapas de rugosidade são ignorados: o brilho é fixo no material).
    ///    Cada FBX vira um prefab em Resources/Quarto/&lt;Tipo&gt;/&lt;id&gt;.prefab, com o pivô no centro da base
    ///    (o móvel "apoia" no chão em y = 0) e sem colisores. O quarto carrega por Resources.Load("Quarto/&lt;Tipo&gt;/&lt;id&gt;").
    ///
    ///  - Texturas (Arte/Quarto/Texturas/&lt;Superfície&gt;/&lt;id&gt;/*_diff*, *_nor_gl*): limitadas a 1024 px, o normal GL
    ///    marcado como Normal map. Cada pasta vira um material em Resources/Quarto/Texturas/&lt;Superfície&gt;/&lt;id&gt;.mat.
    ///
    /// Os prefabs e materiais em Resources são o que entra no APK; a pasta Arte/Quarto guarda os arquivos originais.
    /// Menu: Jogo da Vida → Gerar prefabs do quarto (regenera tudo). Linha de comando:
    /// Unity.exe -batchmode -quit -projectPath unity -executeMethod JogoDaVida.EditorTools.ImportarQuarto.Gerar
    /// </summary>
    public class ImportarQuarto : AssetPostprocessor
    {
        const string Pasta = "Assets/JogoDaVida/Arte/Quarto/";
        const string PastaTexturas = Pasta + "Texturas/";
        const string Saida = "Assets/JogoDaVida/Resources/Quarto/";
        const string Prefixo = "[ImportarQuarto] ";

        static readonly string[] ExtensoesDeImagem = { ".jpg", ".jpeg", ".png", ".tga", ".exr", ".tif", ".tiff" };

        static string Normalizar(string caminho) => caminho.Replace('\\', '/');
        static bool EhDoQuarto(string caminho) => Normalizar(caminho).StartsWith(Pasta);
        static bool EhTexturaAvulsa(string caminho) => Normalizar(caminho).StartsWith(PastaTexturas);
        static bool EhModelo(string caminho) =>
            caminho.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase) ||
            caminho.EndsWith(".obj", System.StringComparison.OrdinalIgnoreCase);
        static bool EhImagem(string caminho) =>
            ExtensoesDeImagem.Contains(Path.GetExtension(caminho).ToLowerInvariant());

        static bool EhMapaNormal(string nome) =>
            nome.Contains("_nor_gl") || nome.Contains("_normal") || nome.EndsWith("_nor") || nome.Contains("_nrm");
        static bool EhMapaTecnico(string nome) =>
            nome.Contains("_rough") || nome.Contains("_arm") || nome.Contains("_disp") || nome.Contains("_ao") ||
            nome.Contains("_metal") || nome.Contains("_spec") || nome.Contains("_nor_dx") || nome.Contains("_bump");

        // ---------- Modelo ----------

        void OnPreprocessModel()
        {
            if (!EhDoQuarto(assetPath)) return;
            var mi = (ModelImporter)assetImporter;

            mi.useFileScale = true;
            mi.globalScale = 1f;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importAnimation = false;
            mi.importBlendShapes = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.addCollider = false;
            mi.meshCompression = ModelImporterMeshCompression.Medium;

            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.materialLocation = ModelImporterMaterialLocation.External;
            mi.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            mi.materialSearch = ModelImporterMaterialSearch.Local;
        }

        /// <summary>Monta o material Standard do móvel: cor + relevo, brilho fixo.</summary>
        void OnPreprocessMaterialDescription(MaterialDescription descricao, Material material, AnimationClip[] clipes)
        {
            if (!EhDoQuarto(assetPath)) return;

            material.shader = Shader.Find("Standard");
            material.SetColor("_Color", Color.white);
            material.SetFloat("_Glossiness", 0.2f);
            material.SetFloat("_Metallic", 0f);

            var pasta = Path.GetDirectoryName(assetPath);
            var cor = AcharTextura(descricao, "DiffuseColor", pasta) ?? AcharNaPasta(pasta, "_diff");
            if (cor != null) material.SetTexture("_MainTex", cor);
            else if (descricao.TryGetProperty("DiffuseColor", out Vector4 rgb))
                material.SetColor("_Color", new Color(rgb.x, rgb.y, rgb.z, 1f));

            var normal = AcharTextura(descricao, "NormalMap", pasta) ?? AcharTextura(descricao, "Bump", pasta)
                ?? AcharNaPasta(pasta, "_nor_gl");
            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
            }
        }

        /// <summary>Textura de uma propriedade do FBX; se o Unity não resolveu o caminho, procura pelo nome do arquivo na pasta do modelo.</summary>
        static Texture AcharTextura(MaterialDescription descricao, string propriedade, string pasta)
        {
            if (!descricao.TryGetProperty(propriedade, out TexturePropertyDescription tex)) return null;
            if (tex.texture != null) return tex.texture;
            if (string.IsNullOrEmpty(tex.path)) return null;
            var nome = Path.GetFileName(tex.path);
            if (EhMapaTecnico(nome.ToLowerInvariant()) && propriedade == "DiffuseColor") return null;
            return Imagens(pasta).Where(c => Path.GetFileName(c) == nome)
                .Select(AssetDatabase.LoadAssetAtPath<Texture>).FirstOrDefault(t => t != null);
        }

        /// <summary>Primeira imagem da pasta (e subpastas) cujo nome contém o marcador; JPG/PNG antes de EXR.</summary>
        static Texture AcharNaPasta(string pasta, string marcador)
        {
            return Imagens(pasta)
                .Where(c => Path.GetFileNameWithoutExtension(c).ToLowerInvariant().Contains(marcador))
                .Select(AssetDatabase.LoadAssetAtPath<Texture>)
                .FirstOrDefault(t => t != null);
        }

        static string[] Imagens(string pasta)
        {
            if (!Directory.Exists(pasta)) return new string[0];
            return Directory.GetFiles(pasta, "*.*", SearchOption.AllDirectories)
                .Select(Normalizar)
                .Where(EhImagem)
                .OrderBy(c => Path.GetExtension(c).ToLowerInvariant() == ".exr" ? 1 : 0)
                .ThenBy(c => c)
                .ToArray();
        }

        // ---------- Texturas ----------

        void OnPreprocessTexture()
        {
            if (!EhDoQuarto(assetPath)) return;
            var ti = (TextureImporter)assetImporter;
            var nome = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();

            ti.maxTextureSize = 1024;
            ti.mipmapEnabled = true;
            if (EhMapaNormal(nome))
            {
                ti.textureType = TextureImporterType.NormalMap;
            }
            else
            {
                ti.textureType = TextureImporterType.Default;
                // Rugosidade, AO etc. não são usados; ficam pequenos e lineares caso alguém queira ligar depois.
                if (EhMapaTecnico(nome)) { ti.sRGBTexture = false; ti.maxTextureSize = 512; }
                else ti.sRGBTexture = true;
            }
        }

        // ---------- Depois da importação: prefabs e materiais ----------

        static void OnPostprocessAllAssets(string[] importados, string[] apagados, string[] movidos, string[] origens)
        {
            var modelos = importados.Where(c => EhDoQuarto(c) && EhModelo(c)).ToArray();
            var pastasDeTextura = importados.Where(c => EhTexturaAvulsa(c) && EhImagem(c))
                .Select(c => Normalizar(Path.GetDirectoryName(c))).Distinct().ToArray();
            if (modelos.Length == 0 && pastasDeTextura.Length == 0) return;

            // Fora da importação, para poder criar assets com segurança.
            EditorApplication.delayCall += () =>
            {
                foreach (var m in modelos) GerarPrefab(m);
                foreach (var p in pastasDeTextura) GerarMaterialDeTextura(p);
                AssetDatabase.SaveAssets();
            };
        }

        [MenuItem("Jogo da Vida/Gerar prefabs do quarto")]
        public static void Gerar()
        {
            if (!Directory.Exists(Pasta))
            {
                Debug.LogWarning(Prefixo + "pasta não encontrada: " + Pasta);
                return;
            }
            int n = 0;
            foreach (var fbx in Directory.GetFiles(Pasta, "*.*", SearchOption.AllDirectories).Select(Normalizar).Where(EhModelo))
            {
                if (EhTexturaAvulsa(fbx)) continue;
                GerarPrefab(fbx);
                n++;
            }
            if (Directory.Exists(PastaTexturas))
            {
                foreach (var pasta in Directory.GetDirectories(PastaTexturas, "*", SearchOption.AllDirectories).Select(Normalizar))
                {
                    if (!Imagens(pasta).Any(c => Normalizar(Path.GetDirectoryName(c)) == pasta)) continue;
                    GerarMaterialDeTextura(pasta);
                    n++;
                }
            }
            AssetDatabase.SaveAssets();
            Debug.Log(Prefixo + n + " item(ns) gerado(s) em " + Saida);
        }

        /// <summary>Arte/Quarto/&lt;Tipo&gt;/&lt;id&gt;/x.fbx → Resources/Quarto/&lt;Tipo&gt;/&lt;id&gt;.prefab, com o pivô no centro da base.</summary>
        static void GerarPrefab(string fbx)
        {
            var modelo = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (modelo == null)
            {
                Debug.LogError(Prefixo + "modelo não carregou: " + fbx);
                return;
            }

            // Tipo e id vêm das pastas: Arte/Quarto/Cama/vintage_day_bed/vintage_day_bed_1k.fbx
            var relativo = Normalizar(fbx).Substring(Pasta.Length);
            var partes = relativo.Split('/');
            if (partes.Length < 3)
            {
                Debug.LogWarning(Prefixo + "esperava Arte/Quarto/<Tipo>/<id>/arquivo.fbx, ignorando: " + fbx);
                return;
            }
            string tipo = partes[0], id = partes[1];
            var destino = Saida + tipo + "/" + id + ".prefab";
            Directory.CreateDirectory(Path.GetDirectoryName(destino));

            var raiz = new GameObject(id);
            try
            {
                var instancia = (GameObject)PrefabUtility.InstantiatePrefab(modelo);
                instancia.transform.SetParent(raiz.transform, false);

                var rends = instancia.GetComponentsInChildren<Renderer>(true);
                if (rends.Length == 0)
                {
                    Debug.LogError(Prefixo + "modelo sem malhas: " + fbx);
                    return;
                }
                var caixa = rends[0].bounds;
                foreach (var r in rends)
                {
                    caixa.Encapsulate(r.bounds);
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    r.receiveShadows = true;
                }
                // Pivô no centro da base: o quarto só precisa dizer onde o móvel apoia no chão.
                instancia.transform.localPosition = -new Vector3(caixa.center.x, caixa.min.y, caixa.center.z);

                foreach (var col in raiz.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);

                PrefabUtility.SaveAsPrefabAsset(raiz, destino, out bool ok);
                var t = caixa.size;
                int tris = rends.Select(r => r.GetComponent<MeshFilter>()).Where(f => f && f.sharedMesh)
                    .Sum(f => f.sharedMesh.triangles.Length / 3);
                Debug.Log(Prefixo + (ok ? "prefab " : "FALHA no prefab ") + destino +
                    string.Format(" ({0:0.00} × {1:0.00} × {2:0.00} m, L×A×P, {3} triângulos)", t.x, t.y, t.z, tris));
                if (t.magnitude > 8f || t.magnitude < 0.05f)
                    Debug.LogWarning(Prefixo + id + ": tamanho estranho; confira a escala do FBX (o Unity deveria ler em metros).");
            }
            finally
            {
                Object.DestroyImmediate(raiz);
            }
        }

        /// <summary>Arte/Quarto/Texturas/&lt;Superfície&gt;/&lt;id&gt;/ → Resources/Quarto/Texturas/&lt;Superfície&gt;/&lt;id&gt;.mat.</summary>
        static void GerarMaterialDeTextura(string pasta)
        {
            pasta = Normalizar(pasta).TrimEnd('/');
            var relativo = pasta.Substring(PastaTexturas.Length);
            var partes = relativo.Split('/');
            if (partes.Length < 2)
            {
                Debug.LogWarning(Prefixo + "esperava Arte/Quarto/Texturas/<Superfície>/<id>/, ignorando: " + pasta);
                return;
            }
            string superficie = partes[0], id = partes[1];

            var imagens = Imagens(pasta).Where(c => Normalizar(Path.GetDirectoryName(c)) == pasta).ToArray();
            var cor = imagens.Where(c => Path.GetFileNameWithoutExtension(c).ToLowerInvariant().Contains("_diff") ||
                                         Path.GetFileNameWithoutExtension(c).ToLowerInvariant().Contains("_col"))
                .Select(AssetDatabase.LoadAssetAtPath<Texture>).FirstOrDefault(t => t != null);
            var normal = imagens.Where(c => EhMapaNormal(Path.GetFileNameWithoutExtension(c).ToLowerInvariant()))
                .Select(AssetDatabase.LoadAssetAtPath<Texture>).FirstOrDefault(t => t != null);
            if (cor == null && normal == null)
            {
                Debug.LogWarning(Prefixo + "nenhuma textura _diff/_nor_gl em " + pasta);
                return;
            }

            var destino = Saida + "Texturas/" + superficie + "/" + id + ".mat";
            Directory.CreateDirectory(Path.GetDirectoryName(destino));
            var material = AssetDatabase.LoadAssetAtPath<Material>(destino);
            bool novo = material == null;
            if (novo) material = new Material(Shader.Find("Standard"));
            else material.shader = Shader.Find("Standard");

            material.SetColor("_Color", Color.white);
            material.SetFloat("_Metallic", 0f);
            // Piso de madeira tem um brilho leve; parede e tecido são foscos.
            material.SetFloat("_Glossiness", superficie.ToLowerInvariant() == "piso" ? 0.3f : 0.08f);
            material.SetTexture("_MainTex", cor);
            material.SetTexture("_BumpMap", normal);
            if (normal != null) material.EnableKeyword("_NORMALMAP"); else material.DisableKeyword("_NORMALMAP");

            if (novo) AssetDatabase.CreateAsset(material, destino);
            else EditorUtility.SetDirty(material);
            Debug.Log(Prefixo + "material " + destino + (cor ? " (cor" : " (sem cor") + (normal ? " + relevo)" : ")"));
        }
    }
}
