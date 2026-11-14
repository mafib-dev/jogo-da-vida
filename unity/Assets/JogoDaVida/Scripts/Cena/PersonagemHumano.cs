using System;
using System.Collections.Generic;
using UnityEngine;

namespace JogoDaVida.Cena
{
    /// <summary>
    /// Personagem humano (modelo do Blender, rig mixamo) dentro do quarto.
    ///
    /// Com o AnimatorController no prefab (menu Jogo da Vida → Extrair animações do Mixamo), o corpo
    /// é animado pelos clipes do Mixamo: um idle por humor, bocejos de tempos em tempos nos Sinais,
    /// Victory ao comemorar e Joyful Jump ao subir de nível (os dois voltam ao idle do humor).
    /// Por cima da animação, no LateUpdate, ficam as piscadas (ossos das pálpebras) e as
    /// expressões (blend shapes de sorriso e olhos cansados).
    ///
    /// Sem o controller, cai na animação por código: a pose e todo o movimento são feitos
    /// girando os ossos no LateUpdate a partir da pose de repouso do FBX (pose A).
    ///
    /// As direções são dadas no espaço do próprio personagem (este objeto):
    /// +Z = para onde ele olha, +Y = para cima, +X = a direita dele.
    /// Os braços são apontados para direções fixas nesse espaço ("para baixo"),
    /// então pendem pela gravidade mesmo quando o tronco curva.
    /// </summary>
    public class PersonagemHumano : MonoBehaviour
    {
        /// <summary>Prefab em Resources gerado a partir do Personagem.fbx (menu Jogo da Vida → Gerar prefab do personagem).</summary>
        public const string NomeDoPrefab = "PersonagemHumano";
        const float AlturaDesejada = 1.75f;
        const float TempoDeTransicao = 0.5f;   // troca de humor
        const float GiroDaPalma = 40f;         // graus: palma virada para a coxa (medido nas capturas)
        const float SinalDosDedos = -1f;       // sentido em que os dedos fecham para a palma

        // ---------- Humores ----------

        /// <summary>Parâmetros de postura e rosto de cada humor (ângulos em graus, blend shapes de 0 a 100).</summary>
        struct Postura
        {
            public float tronco;      // + curva para a frente
            public float cabeca;      // + cabeça para baixo
            public float inclinacao;  // cabeça de lado
            public float ombroQueda;  // + ombros caídos
            public float ombroTras;   // + peito aberto (ombros para trás)
            public float sorriso, cansado;
            public float ritmo;       // velocidade da respiração (1 = normal)

            public static Postura Lerp(Postura a, Postura b, float k) => new Postura
            {
                tronco = Mathf.Lerp(a.tronco, b.tronco, k),
                cabeca = Mathf.Lerp(a.cabeca, b.cabeca, k),
                inclinacao = Mathf.Lerp(a.inclinacao, b.inclinacao, k),
                ombroQueda = Mathf.Lerp(a.ombroQueda, b.ombroQueda, k),
                ombroTras = Mathf.Lerp(a.ombroTras, b.ombroTras, k),
                sorriso = Mathf.Lerp(a.sorriso, b.sorriso, k),
                cansado = Mathf.Lerp(a.cansado, b.cansado, k),
                ritmo = Mathf.Lerp(a.ritmo, b.ritmo, k),
            };
        }

        static Postura PosturaDe(EstadoVisual e) => e switch
        {
            EstadoVisual.Bem => new Postura { tronco = -4, cabeca = -3, ombroTras = 7, ombroQueda = -2, sorriso = 75, ritmo = 1.1f },
            EstadoVisual.Normal => new Postura { sorriso = 15, ritmo = 1f },
            EstadoVisual.Sinais => new Postura { tronco = 5, cabeca = 8, inclinacao = 6, ombroQueda = 6, ombroTras = -4, cansado = 55, ritmo = 0.75f },
            _ => new Postura { tronco = 16, cabeca = 22, inclinacao = 3, ombroQueda = 11, ombroTras = -10, cansado = 100, ritmo = 0.6f },
        };

        // ---------- Ossos ----------

        class Lado
        {
            public float s;   // +1 = direita, -1 = esquerda (eixo X do personagem)
            public Transform ombro, braco, antebraco, mao, coxa, perna, pe;
            public Transform palpebraCima, palpebraBaixo;
            public Transform indicador, minimo;              // base dos dedos (para achar o eixo da palma)
            public readonly List<(Transform osso, float graus)> dedos = new List<(Transform, float)>();
            public Quaternion peDescanso;   // rotação do pé no espaço do personagem (pé apoiado)
        }

        readonly Dictionary<string, Transform> ossos = new Dictionary<string, Transform>();
        readonly List<(Transform osso, Quaternion rot)> descanso = new List<(Transform, Quaternion)>();
        Transform modelo, quadril, coluna, coluna1, coluna2, pescoco, cabecaOsso;
        Lado esq, dir;

        // Blend shapes do rosto em todas as malhas que as têm (corpo, barba, sobrancelhas, cílios, cabelo).
        readonly List<(SkinnedMeshRenderer r, int sorriso, int cansado)> rostos = new List<(SkinnedMeshRenderer, int, int)>();

        // ---------- Estado ----------

        Postura atual, alvo;
        float faseResp, proximaPiscada = 2f, inicioPiscada = -10f;
        float tempoComemorar = -10f, tempoNivel = -10f;
        Vector3 posicaoBase;
        Quaternion rotacaoBase;
        bool pronto;

        // ---------- Animator (clipes do Mixamo) ----------

        const float Crossfade = 0.4f;   // segundos
        /// <summary>
        /// Quanto de Sad Idle entra no Abatido (blend tree com o Breathing Idle): 0 = Breathing Idle,
        /// 1 = Sad Idle puro (ombros caídos demais, corcunda). Ajuste aqui.
        /// </summary>
        const float PesoTristeza = 0.3f;
        Animator anim;
        bool usaAnimator;
        EstadoVisual humor = EstadoVisual.Normal;
        float proximoBocejo = float.MaxValue;
        float sorrisoAtual, cansadoAtual;
        string acao;   // estado de uma só vez tocando agora (Comemorar, SubiuDeNivel, Bocejo) ou null

        static readonly int[] Acoes = { Animator.StringToHash("Comemorar"), Animator.StringToHash("SubiuDeNivel"), Animator.StringToHash("Bocejo") };

        static string IdleDe(EstadoVisual e) => e switch
        {
            EstadoVisual.Bem => "Bem",
            EstadoVisual.Normal => "Normal",
            EstadoVisual.Sinais => "Sinais",
            _ => "Abatido",
        };

        /// <summary>Usado só nas capturas de conferência: força as pálpebras fechadas (0 a 1); negativo = normal.</summary>
        [NonSerialized] public float forcarPiscada = -1f;

        /// <summary>
        /// Cria o personagem dentro de <paramref name="pai"/>, com os pés em y=0 no centro do tapete,
        /// virado para a câmera do quarto. Devolve null se o prefab não existir ou o rig não for o esperado.
        /// </summary>
        public static PersonagemHumano Criar(Transform pai)
        {
            var prefab = Resources.Load<GameObject>(NomeDoPrefab);
            if (prefab == null) return null;

            var raiz = new GameObject("Personagem humano");
            raiz.transform.SetParent(pai, false);
            raiz.transform.localPosition = new Vector3(0, 0, 0.1f);          // centro do tapete
            raiz.transform.localRotation = Quaternion.Euler(0, 180, 0);       // de frente para a câmera (que olha para +Z)
            var instancia = Instantiate(prefab, raiz.transform, false);
            instancia.name = "Modelo";

            var p = raiz.AddComponent<PersonagemHumano>();
            try
            {
                p.Montar(instancia.transform);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PersonagemHumano] não deu para montar o personagem, usando o boneco provisório: " + e.Message);
                Destroy(raiz);
                return null;
            }
            return p;
        }

        void Montar(Transform m)
        {
            modelo = m;
            foreach (var t in m.GetComponentsInChildren<Transform>(true))
            {
                var n = t.name;
                int i = n.LastIndexOf(':');
                if (i >= 0) n = n.Substring(i + 1);
                if (!ossos.ContainsKey(n)) ossos[n] = t;
            }

            quadril = Osso("Hips"); coluna = Osso("Spine"); coluna1 = Osso("Spine1"); coluna2 = Osso("Spine2");
            pescoco = Osso("Neck"); cabecaOsso = Osso("Head");
            esq = MontarLado("Left"); dir = MontarLado("Right");

            // Animator com os clipes do Mixamo, se o controller estiver no prefab; senão fica desligado
            // e o corpo é animado por código.
            anim = m.GetComponentInChildren<Animator>();
            usaAnimator = anim != null && anim.runtimeAnimatorController != null
                && anim.avatar != null && anim.avatar.isValid && anim.avatar.isHuman
                && anim.HasState(0, Animator.StringToHash("Normal"));
            if (anim != null && !usaAnimator) anim.enabled = false;

            // Vira o modelo para a frente deste objeto, medindo para onde os olhos apontam.
            Vector3 frente = ossos.TryGetValue("LeftEye", out var oe) && ossos.TryGetValue("RightEye", out var od)
                ? (oe.position + od.position) / 2f - cabecaOsso.position
                : Osso("LeftToeBase").position - esq.pe.position;
            frente.y = 0;
            modelo.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(frente, transform.forward, Vector3.up), Vector3.up) * modelo.rotation;

            // Lado de cada braço no espaço do personagem (+X = direita).
            esq.s = Mathf.Sign(transform.InverseTransformPoint(esq.braco.position).x);
            dir.s = Mathf.Sign(transform.InverseTransformPoint(dir.braco.position).x);
            if (esq.s == dir.s) throw new Exception("braços do mesmo lado");

            foreach (var o in new[] { quadril, coluna, coluna1, coluna2, pescoco, cabecaOsso })
                descanso.Add((o, o.localRotation));
            foreach (var l in new[] { esq, dir })
            {
                foreach (var o in new[] { l.ombro, l.braco, l.antebraco, l.mao, l.coxa, l.perna, l.pe, l.palpebraCima, l.palpebraBaixo })
                    if (o != null) descanso.Add((o, o.localRotation));
                foreach (var (o, _) in l.dedos) descanso.Add((o, o.localRotation));
                l.peDescanso = Quaternion.Inverse(transform.rotation) * l.pe.rotation;
            }

            foreach (var r in m.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
                r.quality = SkinQuality.Bone4;   // no Android a qualidade padrão usa só 2 ossos por vértice
                r.updateWhenOffscreen = false;
                var malha = r.sharedMesh;
                if (malha == null) continue;
                int s = IndiceDaForma(malha, "Rosto_Sorriso"), c = IndiceDaForma(malha, "Rosto_OlhosCansados");
                if (s >= 0 || c >= 0) rostos.Add((r, s, c));
            }

            atual = alvo = PosturaDe(EstadoVisual.Normal);
            posicaoBase = transform.localPosition;
            rotacaoBase = transform.localRotation;
            if (usaAnimator)
            {
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.Rebind();
                foreach (var par in anim.parameters)
                    if (par.name == "Tristeza") anim.SetFloat("Tristeza", PesoTristeza);
                anim.Play("Normal", 0, 0f);
                anim.Update(0f);
                AplicarRosto(0f, true);
            }
            else
                Posar(0f);
            Aterrar();
            pronto = true;
        }

        Lado MontarLado(string lado)
        {
            var l = NovoLado(lado);
            // Dedos levemente dobrados (mão relaxada): ângulo por falange, da base para a ponta.
            float[] curva = { 10f, 16f, 10f };
            foreach (var dedo in new[] { "Index", "Middle", "Ring", "Pinky" })
                for (int i = 1; i <= 3; i++)
                    if (ossos.TryGetValue(lado + "Hand" + dedo + i, out var f))
                        l.dedos.Add((f, curva[i - 1] * (dedo == "Index" ? 0.7f : 1f)));
            l.indicador = ossos.TryGetValue(lado + "HandIndex1", out var a) ? a : null;
            l.minimo = ossos.TryGetValue(lado + "HandPinky1", out var b) ? b : null;
            return l;
        }

        Lado NovoLado(string lado) => new Lado
        {
            ombro = Osso(lado + "Shoulder"), braco = Osso(lado + "Arm"), antebraco = Osso(lado + "ForeArm"), mao = Osso(lado + "Hand"),
            coxa = Osso(lado + "UpLeg"), perna = Osso(lado + "Leg"), pe = Osso(lado + "Foot"),
            palpebraCima = ossos.TryGetValue(lado + "OrbicularisTop", out var pc) ? pc : null,
            palpebraBaixo = ossos.TryGetValue(lado + "OrbicularisBottom", out var pb) ? pb : null,
        };

        Transform Osso(string nome) =>
            ossos.TryGetValue(nome, out var t) ? t : throw new Exception("osso não encontrado: " + nome);

        static int IndiceDaForma(Mesh malha, string nome)
        {
            int i = malha.GetBlendShapeIndex(nome);
            if (i >= 0) return i;
            for (int k = 0; k < malha.blendShapeCount; k++)
                if (malha.GetBlendShapeName(k).EndsWith(nome)) return k;
            return -1;
        }

        /// <summary>Ajusta a escala para ~1,75 m e apoia a sola dos sapatos em y = 0 (medido pelos vértices já na pose).</summary>
        void Aterrar()
        {
            if (!Medir(out float min, out float max)) return;
            float altura = max - min;
            if (altura > 0.5f && Mathf.Abs(altura - AlturaDesejada) > 0.03f)
            {
                modelo.localScale *= AlturaDesejada / altura;
                Medir(out min, out max);
            }
            float chao = transform.parent != null ? transform.parent.position.y : 0f;
            modelo.position += Vector3.up * (chao - min);
        }

        /// <summary>Menor e maior altura (y no mundo) dos vértices de todas as malhas, já deformadas pela pose.</summary>
        bool Medir(out float min, out float max)
        {
            min = float.MaxValue; max = float.MinValue;
            var assada = new Mesh();
            foreach (var r in modelo.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                r.BakeMesh(assada, false);
                var vs = assada.vertices;
                var tr = r.transform;
                for (int i = 0; i < vs.Length; i++)
                {
                    float y = tr.TransformPoint(vs[i]).y;
                    if (y < min) min = y;
                    if (y > max) max = y;
                }
            }
            Destroy(assada);
            return min <= max;
        }

        // ---------- API usada pelo BonecoProvisorio ----------

        public void DefinirEstado(EstadoVisual estado)
        {
            alvo = PosturaDe(estado);
            if (!usaAnimator) return;
            bool mudou = estado != humor;
            humor = estado;
            if (estado == EstadoVisual.Sinais)
            {
                if (mudou || proximoBocejo == float.MaxValue) proximoBocejo = Time.time + UnityEngine.Random.Range(8f, 15f);
            }
            else
                proximoBocejo = float.MaxValue;
            // Durante Victory / pulo a troca espera o fim; o bocejo é interrompido se o humor mudou.
            if (mudou && (acao == null || acao == "Bocejo"))
                Tocar(IdleDe(humor), Crossfade);
        }

        public void Comemorar()
        {
            tempoComemorar = Time.time;
            if (usaAnimator && acao != "SubiuDeNivel") Tocar("Comemorar", 0.3f, true);
        }

        public void SubiuDeNivel()
        {
            tempoNivel = Time.time;
            if (usaAnimator) Tocar("SubiuDeNivel", 0.3f, true);
        }

        // ---------- Animator ----------

        void Tocar(string estado, float transicao, bool doInicio = false)
        {
            acao = estado == "Comemorar" || estado == "SubiuDeNivel" || estado == "Bocejo" ? estado : null;
            if (doInicio) anim.CrossFadeInFixedTime(estado, transicao, 0, 0f);
            else anim.CrossFadeInFixedTime(estado, transicao);
        }

        void Update()
        {
            if (!pronto || !usaAnimator) return;
            if (acao != null)
            {
                // Fim da ação (Victory, pulo ou bocejo): volta ao idle do humor atual.
                if (anim.IsInTransition(0)) return;
                var info = anim.GetCurrentAnimatorStateInfo(0);
                if (Array.IndexOf(Acoes, info.shortNameHash) < 0) return;   // crossfade ainda não começou
                float fim = 1f - Crossfade / Mathf.Max(info.length, 0.5f);
                if (info.normalizedTime < fim) return;
                if (acao == "Bocejo") proximoBocejo = Time.time + UnityEngine.Random.Range(8f, 15f);
                Tocar(IdleDe(humor), Crossfade);
                return;
            }
            if (humor == EstadoVisual.Sinais && Time.time >= proximoBocejo && !anim.IsInTransition(0))
                Tocar("Bocejo", Crossfade, true);
        }

        /// <summary>Sorriso e olhos cansados do humor; comemorando, sorriso aberto.</summary>
        void AplicarRosto(float dt, bool imediato = false)
        {
            float sorriso = alvo.sorriso, cansado = alvo.cansado;
            if (acao == "Comemorar" || acao == "SubiuDeNivel") { sorriso = 80f; cansado = 0f; }
            else if (acao == "Bocejo") cansado = Mathf.Max(cansado, 80f);
            float k = imediato ? 1f : 1f - Mathf.Exp(-dt * 4.6f / TempoDeTransicao);
            sorrisoAtual = Mathf.Lerp(sorrisoAtual, sorriso, k);
            cansadoAtual = Mathf.Lerp(cansadoAtual, cansado, k);
            atual.cansado = cansadoAtual;   // a piscada fica mais lenta quando cansado
            foreach (var (r, s, c) in rostos)
            {
                if (s >= 0) r.SetBlendShapeWeight(s, sorrisoAtual);
                if (c >= 0) r.SetBlendShapeWeight(c, cansadoAtual);
            }
        }

        float abatimento;   // 0 a 1, suavizado

        /// <summary>
        /// Abatido: por cima do blend tree (que já tira a corcunda do Sad Idle), cabeça um pouco
        /// baixa e ombros levemente caídos. Some ao comemorar.
        /// </summary>
        void Abater(float dt)
        {
            float alvoAbat = humor == EstadoVisual.Abatido && acao == null ? 1f : 0f;
            abatimento = Mathf.Lerp(abatimento, alvoAbat, 1f - Mathf.Exp(-dt * 4.6f / TempoDeTransicao));
            if (abatimento < 0.001f) return;
            Girar(pescoco, Vector3.right, 8f * abatimento);
            Girar(cabecaOsso, Vector3.right, 12f * abatimento);
            foreach (var l in new[] { esq, dir })
                Girar(l.ombro, Vector3.forward, -l.s * 5f * abatimento);
        }

        // ---------- Animação por código ----------

        void LateUpdate()
        {
            if (!pronto) return;
            if (usaAnimator)
            {
                // O Animator não mexe nas pálpebras (não são ossos do Humanoid): parte do repouso e pisca.
                foreach (var l in new[] { esq, dir })
                {
                    if (l.palpebraCima != null) l.palpebraCima.localRotation = Descanso(l.palpebraCima);
                    if (l.palpebraBaixo != null) l.palpebraBaixo.localRotation = Descanso(l.palpebraBaixo);
                }
                AplicarRosto(Time.deltaTime);
                Abater(Time.deltaTime);
                Piscar(Time.time);
                return;
            }
            float dt = Time.deltaTime;
            atual = Postura.Lerp(atual, alvo, 1f - Mathf.Exp(-dt * 4.6f / TempoDeTransicao));   // ~99% em 0,5 s
            faseResp += dt * Mathf.PI * 2f * 0.24f * atual.ritmo;
            Posar(Time.time);
        }

        void Posar(float t)
        {
            // 1. Volta tudo para a pose do FBX; cada frame parte do zero.
            foreach (var (o, r) in descanso) o.localRotation = r;

            // 2. Pulos (comemorar / subiu de nível).
            float pulo = 0f, giro = 0f, bracosAcima = 0f, abertura = 0.85f;
            float dc = t - tempoComemorar, dn = t - tempoNivel;
            if (dn >= 0f && dn < 1f)
            {
                pulo = Arco(dn, 0.15f, 0.75f) * 0.22f;
                giro = Mathf.SmoothStep(0f, 360f, Mathf.InverseLerp(0.12f, 0.85f, dn));
                bracosAcima = Envelope(dn, 1f, 0.12f, 0.3f);
                abertura = 0.8f;
            }
            else if (dc >= 0f && dc < 0.8f)
            {
                pulo = Arco(dc, 0.12f, 0.6f) * 0.16f;
                bracosAcima = Envelope(dc, 0.8f, 0.12f, 0.3f);
            }
            transform.localPosition = posicaoBase + Vector3.up * pulo;
            transform.localRotation = rotacaoBase * Quaternion.Euler(0, giro, 0);

            var p = atual;
            float resp = Mathf.Sin(faseResp);
            float balanco = Mathf.Sin(t * 0.45f) * 1.4f + Mathf.Sin(t * 0.17f + 1f) * 0.6f;   // peso de uma perna para a outra

            // 3. Quadril e pernas: o quadril balança, as pernas ficam retas e os pés apoiados.
            Girar(quadril, Vector3.forward, balanco);
            foreach (var l in new[] { esq, dir })
            {
                Apontar(l.coxa, l.perna, new Vector3(l.s * 0.055f, -1f, 0.01f));
                Apontar(l.perna, l.pe, new Vector3(l.s * 0.02f, -1f, -0.02f));
                l.pe.rotation = transform.rotation * l.peDescanso;
            }

            // 4. Tronco: postura do humor, respiração e compensação do balanço do quadril.
            float[] partes = { 0.3f, 0.35f, 0.35f };
            var cadeia = new[] { coluna, coluna1, coluna2 };
            for (int i = 0; i < 3; i++)
            {
                Girar(cadeia[i], Vector3.right, p.tronco * partes[i] - (i == 2 ? resp * 1.6f : resp * 0.4f));
                Girar(cadeia[i], Vector3.forward, -balanco * 0.4f);
            }

            // 5. Pescoço e cabeça: humor + olhar em volta devagar.
            float olharLado = Mathf.Sin(t * 0.29f) * 14f + Mathf.Sin(t * 0.71f + 2f) * 4f;
            float olharCima = Mathf.Sin(t * 0.37f + 1f) * 3f + resp * 0.5f;
            Girar(pescoco, Vector3.right, p.cabeca * 0.4f);
            Girar(cabecaOsso, Vector3.right, p.cabeca * 0.6f + olharCima);
            Girar(cabecaOsso, Vector3.up, olharLado * (1f - bracosAcima * 0.7f));
            Girar(cabecaOsso, Vector3.forward, -p.inclinacao - balanco * 0.3f);

            // 6. Ombros, braços e mãos.
            foreach (var l in new[] { esq, dir })
            {
                float sobe = resp * 0.8f + bracosAcima * 10f;
                Girar(l.ombro, Vector3.forward, -l.s * (p.ombroQueda - sobe));
                Girar(l.ombro, Vector3.up, l.s * p.ombroTras);

                // Braço relaxado ao lado do corpo, levemente afastado para não entrar no quadril.
                var baixo = new Vector3(l.s * 0.17f, -1f, 0.03f + resp * 0.006f);
                var cima = new Vector3(l.s * abertura, 1f, 0.12f);
                var dBraco = Vector3.Slerp(baixo.normalized, cima.normalized, bracosAcima);
                Apontar(l.braco, l.antebraco, dBraco);

                var dAnte = Vector3.Slerp(new Vector3(l.s * 0.08f, -1f, 0.2f).normalized, cima.normalized, bracosAcima);
                Apontar(l.antebraco, l.mao, dAnte);

                // A pose A do FBX deixa as palmas para a frente: gira o antebraço e a mão
                // em volta do próprio eixo para as palmas ficarem viradas para as coxas.
                float palma = l.s * GiroDaPalma * (1f - bracosAcima);
                var eixo = l.mao.position - l.antebraco.position;
                l.antebraco.rotation = Quaternion.AngleAxis(palma * 0.55f, eixo) * l.antebraco.rotation;
                l.mao.rotation = Quaternion.AngleAxis(palma * 0.45f, eixo) * l.mao.rotation;

                // Dedos dobrados em volta do eixo da palma (do indicador ao mínimo).
                if (l.indicador != null && l.minimo != null)
                {
                    var atravessa = l.minimo.position - l.indicador.position;
                    float k = SinalDosDedos * (1f - bracosAcima * 0.6f);
                    foreach (var (f, graus) in l.dedos)
                        f.rotation = Quaternion.AngleAxis(graus * k, atravessa) * f.rotation;
                }
            }

            // 7. Piscadas (ossos das pálpebras) e expressões (blend shapes).
            Piscar(t);
            foreach (var (r, s, c) in rostos)
            {
                if (s >= 0) r.SetBlendShapeWeight(s, Mathf.Max(p.sorriso, bracosAcima * 80f));
                if (c >= 0) r.SetBlendShapeWeight(c, p.cansado * (1f - bracosAcima));
            }
        }

        void Piscar(float t)
        {
            if (t >= proximaPiscada)
            {
                inicioPiscada = t;
                proximaPiscada = t + UnityEngine.Random.Range(2.5f, 5.5f);
            }
            float duracao = 0.16f + atual.cansado * 0.0012f;   // cansado pisca mais devagar
            float u = (t - inicioPiscada) / duracao;
            float fechar = u >= 0f && u < 1f ? Mathf.Sin(u * Mathf.PI) : 0f;
            if (forcarPiscada >= 0f) fechar = forcarPiscada;
            if (fechar <= 0f) return;
            foreach (var l in new[] { esq, dir })
            {
                if (l.palpebraCima != null) Girar(l.palpebraCima, Vector3.right, fechar * 28f);
                if (l.palpebraBaixo != null) Girar(l.palpebraBaixo, Vector3.right, -fechar * 8f);
            }
        }

        Quaternion Descanso(Transform osso)
        {
            foreach (var (o, r) in descanso) if (o == osso) return r;
            return osso.localRotation;
        }

        /// <summary>Gira o osso em volta de um eixo do personagem (em graus).</summary>
        void Girar(Transform osso, Vector3 eixo, float graus)
        {
            if (osso == null || graus == 0f) return;
            osso.rotation = Quaternion.AngleAxis(graus, transform.TransformDirection(eixo)) * osso.rotation;
        }

        /// <summary>Gira o osso para que o filho fique na direção pedida (espaço do personagem).</summary>
        void Apontar(Transform osso, Transform filho, Vector3 direcao)
        {
            var atualDir = filho.position - osso.position;
            if (atualDir.sqrMagnitude < 1e-8f) return;
            osso.rotation = Quaternion.FromToRotation(atualDir, transform.TransformDirection(direcao)) * osso.rotation;
        }

        /// <summary>Arco de pulo: 0 fora de [ini, fim], sobe e desce como uma parábola no meio.</summary>
        static float Arco(float t, float ini, float fim)
        {
            if (t <= ini || t >= fim) return 0f;
            float u = (t - ini) / (fim - ini);
            return 4f * u * (1f - u);
        }

        /// <summary>Sobe em <paramref name="subida"/> s, fica e volta em <paramref name="descida"/> s até <paramref name="total"/>.</summary>
        static float Envelope(float t, float total, float subida, float descida)
        {
            float a = Mathf.SmoothStep(0f, 1f, t / subida);
            float b = Mathf.SmoothStep(0f, 1f, (total - t) / descida);
            return Mathf.Min(a, b);
        }
    }
}
