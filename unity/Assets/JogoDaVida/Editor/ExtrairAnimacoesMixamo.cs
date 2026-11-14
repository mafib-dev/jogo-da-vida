using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace JogoDaVida.EditorTools
{
    /// <summary>
    /// Traz as animações do Mixamo para o personagem.
    ///
    /// 1. Copia os FBX de &lt;repositório&gt;/mixamo/ para Assets/MixamoBruto/ (fora do Git: o FBX com
    ///    skin tem ~87 MB). Importa todos como Humanoid: o "Happy Idle" (com skin) cria o avatar
    ///    e os demais copiam esse avatar.
    /// 2. Extrai cada AnimationClip para um .anim próprio em Arte/Personagem/Animacoes/ (esses vão
    ///    para o Git): loop nos idles; rotação, altura e posição XZ da raiz "bake into pose",
    ///    para o personagem não sair do lugar (altura pelos pés nos clipes em pé; a altura do pulo
    ///    fica na própria pose).
    ///    Os clipes de sentar (Stand To Sit, Sitting Idle) são extraídos mas não entram no
    ///    controller: o "Sitting Idle" baixado é sentado no chão e o "Stand To Sit" termina numa
    ///    cadeira, então não encaixam na beira da cama. O Abatido usa o Sad Idle em pé, misturado
    ///    com o Breathing Idle num blend tree (parâmetro "Tristeza").
    /// 3. Gera o AnimatorController (Arte/Personagem/Animacoes/Personagem.controller) e o liga ao
    ///    prefab Resources/PersonagemHumano.prefab, para entrar no APK.
    ///
    /// Menu: Jogo da Vida → Extrair animações do Mixamo. Linha de comando:
    /// Unity.exe -batchmode -quit -projectPath unity -executeMethod JogoDaVida.EditorTools.ExtrairAnimacoesMixamo.Extrair
    /// </summary>
    public static class ExtrairAnimacoesMixamo
    {
        const string Prefixo = "[ExtrairAnimacoesMixamo] ";
        const string PastaBruta = "Assets/MixamoBruto/";
        const string PastaAnim = "Assets/JogoDaVida/Arte/Personagem/Animacoes/";
        public const string Controlador = PastaAnim + "Personagem.controller";
        const string Prefab = "Assets/JogoDaVida/Resources/PersonagemHumano.prefab";
        const string ComSkin = "Ch08_nonPBR@Happy Idle.fbx";
        /// <summary>Parâmetro do blend tree do Abatido (0 = Breathing Idle, 1 = Sad Idle).</summary>
        public const string ParametroTristeza = "Tristeza";

        /// <summary>Clipe extraído: arquivo do Mixamo, nome do .anim, se faz loop.</summary>
        static readonly (string fbx, string anim, bool loop)[] Clipes =
        {
            ("Ch08_nonPBR@Happy Idle.fbx", "HappyIdle", true),
            ("Ch08_nonPBR@Breathing Idle.fbx", "BreathingIdle", true),
            ("Ch08_nonPBR@Sad Idle.fbx", "SadIdle", true),
            ("Ch08_nonPBR@Sitting Idle.fbx", "SittingIdle", true),
            ("Ch08_nonPBR@Yawn.fbx", "Yawn", false),
            ("Ch08_nonPBR@Stand To Sit.fbx", "StandToSit", false),
            ("Ch08_nonPBR@Victory.fbx", "Victory", false),
            ("Ch08_nonPBR@Joyful Jump.fbx", "JoyfulJump", false),
        };

        /// <summary>Estados do Animator (nomes usados pelo PersonagemHumano) e o clipe de cada um.</summary>
        static readonly (string estado, string anim)[] Estados =
        {
            ("Normal", "BreathingIdle"),     // estado padrão
            ("Bem", "HappyIdle"),
            ("Sinais", "BreathingIdle"),
            ("Bocejo", "Yawn"),
            ("Abatido", "SadIdle"),
            ("Comemorar", "Victory"),
            ("SubiuDeNivel", "JoyfulJump"),
        };

        [MenuItem("Jogo da Vida/Extrair animações do Mixamo")]
        public static void Extrair()
        {
            if (!CopiarBrutos()) return;
            if (!ImportarBrutos()) return;
            ExtrairClipes();
            GerarControlador();
            LigarAoPrefab();
            AssetDatabase.SaveAssets();
            Debug.Log(Prefixo + "pronto.");
        }

        // ---------- 1. FBX brutos ----------

        static bool CopiarBrutos()
        {
            var origem = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "mixamo"));
            var destino = Path.GetFullPath(Path.Combine(Application.dataPath, "MixamoBruto"));
            Directory.CreateDirectory(destino);
            foreach (var (fbx, _, _) in Clipes)
            {
                var d = Path.Combine(destino, fbx);
                if (File.Exists(d)) continue;
                var o = Path.Combine(origem, fbx);
                if (!File.Exists(o))
                {
                    Debug.LogError(Prefixo + "não achei " + o + " (baixe do Mixamo para a pasta mixamo/ na raiz do repositório).");
                    return false;
                }
                File.Copy(o, d);
            }
            AssetDatabase.Refresh();
            return true;
        }

        static bool ImportarBrutos()
        {
            // Primeiro o FBX com skin, que cria o avatar.
            var comSkin = PastaBruta + ComSkin;
            Configurar(comSkin, null);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(comSkin).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                Debug.LogError(Prefixo + "o avatar do " + ComSkin + " não ficou Humanoid válido.");
                return false;
            }
            foreach (var (fbx, _, _) in Clipes)
                if (fbx != ComSkin) Configurar(PastaBruta + fbx, avatar);
            return true;
        }

        static void Configurar(string caminho, Avatar origem)
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(caminho);
            mi.animationType = ModelImporterAnimationType.Human;
            if (origem == null)
                mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            else
            {
                mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                mi.sourceAvatar = origem;
            }
            mi.importAnimation = true;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.importBlendShapes = false;
            mi.SaveAndReimport();

            bool loop = Clipes.First(c => PastaBruta + c.fbx == caminho).loop;
            var clipes = mi.defaultClipAnimations;
            foreach (var c in clipes)
            {
                c.loopTime = loop;
                c.loopPose = loop;
                // Raiz parada: tudo "bake into pose", baseado no original do clipe.
                c.lockRootRotation = true; c.keepOriginalOrientation = true;
                c.lockRootHeightY = true; c.keepOriginalPositionY = !PeloPe(caminho); c.heightFromFeet = PeloPe(caminho);
                c.lockRootPositionXZ = true; c.keepOriginalPositionXZ = true;
            }
            mi.clipAnimations = clipes;
            mi.SaveAndReimport();
        }

        /// <summary>
        /// Altura da raiz baseada nos pés (pés sempre no chão) nos clipes em pé. No pulo e nos de
        /// sentar fica a altura original, para o pulo subir de verdade e o quadril descer ao sentar.
        /// </summary>
        static bool PeloPe(string fbx) =>
            !fbx.Contains("Joyful Jump") && !fbx.Contains("Sit");

        // ---------- 2. .anim ----------

        static void ExtrairClipes()
        {
            Directory.CreateDirectory(Path.GetFullPath(PastaAnim));
            foreach (var (fbx, nome, loop) in Clipes)
            {
                var fonte = AssetDatabase.LoadAllAssetsAtPath(PastaBruta + fbx).OfType<AnimationClip>()
                    .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
                if (fonte == null)
                {
                    Debug.LogError(Prefixo + "sem clipe em " + fbx);
                    continue;
                }
                var copia = new AnimationClip();
                EditorUtility.CopySerialized(fonte, copia);
                copia.name = nome;
                var cfg = AnimationUtility.GetAnimationClipSettings(copia);
                cfg.loopTime = loop; cfg.loopBlend = loop;
                cfg.loopBlendOrientation = true; cfg.keepOriginalOrientation = true;
                cfg.loopBlendPositionY = true; cfg.keepOriginalPositionY = !PeloPe(fbx); cfg.heightFromFeet = PeloPe(fbx);
                cfg.loopBlendPositionXZ = true; cfg.keepOriginalPositionXZ = true;
                AnimationUtility.SetAnimationClipSettings(copia, cfg);

                var destino = PastaAnim + nome + ".anim";
                var existente = AssetDatabase.LoadAssetAtPath<AnimationClip>(destino);
                if (existente != null)
                {
                    EditorUtility.CopySerialized(copia, existente);   // mantém o GUID (o controller continua apontando)
                    EditorUtility.SetDirty(existente);
                }
                else
                    AssetDatabase.CreateAsset(copia, destino);
                Debug.Log(Prefixo + string.Format("{0}: {1:0.00} s, loop={2}", nome, fonte.length, loop));
            }
            AssetDatabase.SaveAssets();
        }

        // ---------- 3. Animator ----------

        static void GerarControlador()
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(Controlador) != null)
                AssetDatabase.DeleteAsset(Controlador);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(Controlador);
            var maquina = ctrl.layers[0].stateMachine;
            ctrl.AddParameter(ParametroTristeza, AnimatorControllerParameterType.Float);
            var parametros = ctrl.parameters;
            parametros[0].defaultFloat = 0.3f;   // o PersonagemHumano define o valor de verdade (PesoTristeza)
            ctrl.parameters = parametros;

            float y = 0;
            foreach (var (estado, anim) in Estados)
            {
                var clipe = AssetDatabase.LoadAssetAtPath<AnimationClip>(PastaAnim + anim + ".anim");
                var s = maquina.AddState(estado, new Vector3(300, y += 60, 0));
                s.motion = estado == "Abatido" ? ArvoreAbatido(ctrl) : clipe;
                s.writeDefaultValues = true;
            }
            maquina.defaultState = maquina.states.First(s => s.state.name == "Normal").state;
            // As trocas (crossfade de 0,4 s) são feitas pelo PersonagemHumano, que sabe o humor atual.
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Abatido: blend tree 1D entre Breathing Idle (0) e Sad Idle (1). O Sad Idle puro deixa os
        /// ombros caídos demais e o corpo corcunda; misturado, fica só desanimado.
        /// </summary>
        static BlendTree ArvoreAbatido(AnimatorController ctrl)
        {
            var arvore = new BlendTree
            {
                name = "Abatido",
                blendType = BlendTreeType.Simple1D,
                blendParameter = ParametroTristeza,
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(arvore, ctrl);
            arvore.AddChild(AssetDatabase.LoadAssetAtPath<AnimationClip>(PastaAnim + "BreathingIdle.anim"), 0f);
            arvore.AddChild(AssetDatabase.LoadAssetAtPath<AnimationClip>(PastaAnim + "SadIdle.anim"), 1f);
            return arvore;
        }

        static void LigarAoPrefab()
        {
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Controlador);
            var raiz = PrefabUtility.LoadPrefabContents(Prefab);
            try
            {
                LigarControlador(raiz, ctrl);
                PrefabUtility.SaveAsPrefabAsset(raiz, Prefab);
                Debug.Log(Prefixo + "controller ligado ao prefab " + Prefab);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(raiz);
            }
        }

        /// <summary>Coloca o controller no Animator do personagem (também usado ao gerar o prefab).</summary>
        public static void LigarControlador(GameObject raiz, RuntimeAnimatorController ctrl)
        {
            var anim = raiz.GetComponentInChildren<Animator>(true);
            if (anim == null || ctrl == null) return;
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.updateMode = AnimatorUpdateMode.Normal;
        }
    }
}
