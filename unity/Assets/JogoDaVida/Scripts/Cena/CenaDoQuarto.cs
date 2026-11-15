using System;
using System.Collections.Generic;
using UnityEngine;

namespace JogoDaVida.Cena
{
    /// <summary>
    /// Quarto do personagem: piso, paredes, janela, cama, mesa, câmera e luz.
    ///
    /// Os móveis vêm dos prefabs em Resources/Quarto/&lt;Tipo&gt;/&lt;id&gt; e as texturas de piso e parede
    /// dos materiais em Resources/Quarto/Texturas/&lt;Superfície&gt;/&lt;id&gt; (gerados pelo ImportarQuarto a partir
    /// de Arte/Quarto). O que ainda não tem modelo continua feito de blocos (primitivas), então a cena
    /// funciona com qualquer subconjunto dos assets.
    ///
    /// Medidas em metros. A câmera olha para +Z; o personagem fica na origem.
    /// </summary>
    public class CenaDoQuarto : MonoBehaviour
    {
        // Tamanho do quarto
        const float Largura = 4.4f, Altura = 3.0f, Fundo = 2.0f, Frente = -2.8f;

        // Assets do set inicial (ids do Poly Haven; ver Banco de Ideias 3.11). Se faltar, entra o bloco.
        const string PrefabCama = "Quarto/Cama/vintage_day_bed";
        const string PrefabCadeira = "Quarto/Cadeira/chinese_armchair";
        const string MaterialPiso = "Quarto/Texturas/Piso/wood_floor";
        const string MaterialParede = "Quarto/Texturas/Parede/beige_wall_001";
        // As texturas do Poly Haven cobrem ~2 m de parede/piso real; repete a partir disso.
        const float MetrosPorTextura = 2f;

        Camera cam;
        Light luz;
        Renderer vidro, garrafa;
        readonly Dictionary<string, Transform> vagas = new Dictionary<string, Transform>();

        EstadoVisual estado = EstadoVisual.Normal;
        float proximaLeituraDaHora;
        float dia;               // 0 = noite, 1 = meio-dia
        Color ceu;
        float intensidadeAtual = 1f;
        Color corDaLuzAtual = Color.white, ambienteAtual = Color.gray;

        public Camera CameraDaCena => cam;

        /// <summary>Lugares vazios onde os móveis da loja vão aparecer.</summary>
        public static readonly string[] NomesDasVagas = { "estante", "quadro", "planta", "luminaria", "cabeceira" };

        public static CenaDoQuarto Criar(Transform pai)
        {
            var go = new GameObject("Quarto");
            go.transform.SetParent(pai, false);
            var q = go.AddComponent<CenaDoQuarto>();
            q.Montar();
            return q;
        }

        /// <summary>Ponto de encaixe de um móvel da loja (ver <see cref="NomesDasVagas"/>).</summary>
        public Transform Vaga(string nome) => vagas.TryGetValue(nome, out var t) ? t : null;

        public void DefinirEstado(EstadoVisual novo)
        {
            estado = novo;
            if (garrafa != null)
            {
                bool cheia = novo == EstadoVisual.Bem || novo == EstadoVisual.Normal;
                Pintar(garrafa, cheia ? new Color(0.25f, 0.6f, 0.95f) : new Color(0.82f, 0.86f, 0.88f));
            }
        }

        void Montar()
        {
            MontarCamera();
            MontarLuz();
            MontarParedes();
            MontarMoveis();
            MontarVagas();

            foreach (var col in GetComponentsInChildren<Collider>()) Destroy(col);

            DefinirEstado(estado);
            LerHora();
            // Começa já no valor certo, sem "acender" a cena aos poucos.
            CalcularAlvos(out intensidadeAtual, out corDaLuzAtual, out ambienteAtual);
            Aplicar();
        }

        void MontarCamera()
        {
            cam = new GameObject("Câmera").AddComponent<Camera>();
            cam.transform.SetParent(transform, false);
            cam.transform.localPosition = new Vector3(0, 1.1f, -4.2f);
            cam.transform.LookAt(transform.TransformPoint(new Vector3(0, 0.9f, 0)));
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.83f, 0.89f, 0.97f);
            // Só a faixa de cima da tela; o resto é a interface do app.
            cam.rect = new Rect(0, 1 - BonecoProvisorio.AlturaDaCena, 1, BonecoProvisorio.AlturaDaCena);
            cam.fieldOfView = 35;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 20f;
        }

        void MontarLuz()
        {
            luz = new GameObject("Luz").AddComponent<Light>();
            luz.transform.SetParent(transform, false);
            luz.type = LightType.Directional;
            luz.transform.localRotation = Quaternion.Euler(42, -32, 0);
            luz.shadows = LightShadows.Soft;
            luz.shadowStrength = 0.55f;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        }

        void MontarParedes()
        {
            var parede = new Color(0.93f, 0.9f, 0.84f);
            float meioZ = (Fundo + Frente) / 2f, comprimento = Fundo - Frente;

            // Piso e paredes não fazem sombra: o quarto não tem teto, então a parede
            // lateral escureceria metade da cena.
            var piso = Bloco("Piso", new Vector3(0, -0.05f, meioZ), new Vector3(Largura, 0.1f, comprimento), new Color(0.66f, 0.5f, 0.36f));
            SemSombra(piso);
            Texturizar(piso, MaterialPiso, Largura, comprimento);

            var fundo = Bloco("Parede do fundo", new Vector3(0, Altura / 2, Fundo + 0.05f), new Vector3(Largura + 0.2f, Altura, 0.1f), parede);
            var esquerda = Bloco("Parede esquerda", new Vector3(-Largura / 2 - 0.05f, Altura / 2, meioZ), new Vector3(0.1f, Altura, comprimento), parede);
            var direita = Bloco("Parede direita", new Vector3(Largura / 2 + 0.05f, Altura / 2, meioZ), new Vector3(0.1f, Altura, comprimento), parede);
            SemSombra(fundo); SemSombra(esquerda); SemSombra(direita);
            Texturizar(fundo, MaterialParede, Largura, Altura);
            Texturizar(esquerda, MaterialParede, comprimento, Altura);
            Texturizar(direita, MaterialParede, comprimento, Altura);
            Bloco("Rodapé", new Vector3(0, 0.05f, Fundo - 0.01f), new Vector3(Largura, 0.1f, 0.02f), Color.white);

            // Janela na parede do fundo, à direita
            var centro = new Vector3(0.9f, 1.6f, Fundo);
            Bloco("Moldura da janela", centro + new Vector3(0, 0, -0.01f), new Vector3(1.2f, 1.1f, 0.04f), Color.white);
            vidro = Bloco("Vidro", centro + new Vector3(0, 0, -0.035f), new Vector3(1.08f, 0.98f, 0.01f), Color.cyan);
            vidro.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            vidro.receiveShadows = false;
            Bloco("Travessa vertical", centro + new Vector3(0, 0, -0.045f), new Vector3(0.04f, 0.98f, 0.02f), Color.white);
            Bloco("Travessa horizontal", centro + new Vector3(0, 0, -0.045f), new Vector3(1.08f, 0.04f, 0.02f), Color.white);
        }

        void MontarMoveis()
        {
            var madeira = new Color(0.48f, 0.34f, 0.22f);

            // Tapete debaixo do personagem
            var tapete = Primitiva(PrimitiveType.Cylinder, "Tapete", new Vector3(0, 0.005f, 0.1f), new Vector3(1.9f, 0.005f, 1.4f), new Color(0.75f, 0.32f, 0.3f));
            tapete.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Cama encostada na parede esquerda, comprida no sentido do fundo (eixo Z).
            float camaX = -1.65f, camaZ = 1.0f;
            // O modelo é comprido no X; girado 90° o comprimento fica no Z e o encosto vira para a parede.
            var cama = Movel("Cama", PrefabCama, new Vector3(camaX, 0, camaZ), 90f);
            if (cama != null)
            {
                Encostar(cama, -Largura / 2);
            }
            else
            {
                Bloco("Cama (base)", new Vector3(camaX, 0.15f, camaZ), new Vector3(1.05f, 0.3f, 1.95f), madeira);
                Bloco("Colchão", new Vector3(camaX, 0.4f, camaZ), new Vector3(1.0f, 0.2f, 1.9f), new Color(0.95f, 0.95f, 0.93f));
                Bloco("Coberta", new Vector3(camaX, 0.42f, 0.69f), new Vector3(1.02f, 0.2f, 1.34f), new Color(0.3f, 0.45f, 0.7f));
                Bloco("Travesseiro", new Vector3(camaX, 0.56f, 1.68f), new Vector3(0.7f, 0.12f, 0.4f), Color.white);
                Bloco("Cabeceira da cama", new Vector3(camaX, 0.6f, 1.97f), new Vector3(1.05f, 1.2f, 0.06f), madeira);
            }

            // Mesa embaixo da janela, à direita
            var mesa = new Vector3(1.5f, 0.74f, 1.62f);
            Bloco("Mesa (tampo)", mesa, new Vector3(1.2f, 0.05f, 0.6f), madeira);
            foreach (var dx in new[] { -0.55f, 0.55f })
                foreach (var dz in new[] { -0.25f, 0.25f })
                    Bloco("Mesa (pé)", new Vector3(mesa.x + dx, 0.36f, mesa.z + dz), new Vector3(0.05f, 0.72f, 0.05f), madeira);

            // Cadeira: a poltrona fica ao lado da mesa, virada para o quarto (quem olha vê a frente dela).
            if (Movel("Cadeira", PrefabCadeira, new Vector3(1.5f, 0, 0.85f), 205f) == null)
            {
                var cadeira = new Vector3(1.4f, 0.45f, 1.05f);
                var corCadeira = new Color(0.25f, 0.27f, 0.3f);
                Bloco("Cadeira (assento)", cadeira, new Vector3(0.42f, 0.05f, 0.42f), corCadeira);
                Bloco("Cadeira (encosto)", cadeira + new Vector3(0, 0.3f, -0.2f), new Vector3(0.42f, 0.5f, 0.04f), corCadeira);
                foreach (var dx in new[] { -0.18f, 0.18f })
                    foreach (var dz in new[] { -0.18f, 0.18f })
                        Bloco("Cadeira (pé)", new Vector3(cadeira.x + dx, 0.21f, cadeira.z + dz), new Vector3(0.04f, 0.43f, 0.04f), corCadeira);
            }

            // Garrafa de água: cheia quando o personagem está bem, vazia quando dá sinais
            garrafa = Primitiva(PrimitiveType.Cylinder, "Garrafa", new Vector3(1.12f, 0.875f, 1.5f), new Vector3(0.07f, 0.11f, 0.07f), Color.blue);
        }

        void MontarVagas()
        {
            CriarVaga("estante", new Vector3(-0.2f, 0, 1.8f));
            CriarVaga("quadro", new Vector3(-1.65f, 1.9f, 1.98f));
            CriarVaga("planta", new Vector3(1.9f, 0, 0.4f));
            CriarVaga("luminaria", new Vector3(1.95f, 0.765f, 1.75f));
            CriarVaga("cabeceira", new Vector3(-0.9f, 0, 1.72f));
        }

        void CriarVaga(string nome, Vector3 pos)
        {
            var t = new GameObject("Vaga: " + nome).transform;
            t.SetParent(transform, false);
            t.localPosition = pos;
            vagas[nome] = t;
        }

        // ---------- Luz: hora do dia + humor ----------

        void LerHora()
        {
            var agora = DateTime.Now;
            float h = agora.Hour + agora.Minute / 60f;
            // Sol das 6h às 18h, com pico ao meio-dia.
            dia = (h > 6f && h < 18f) ? Mathf.Sin((h - 6f) / 12f * Mathf.PI) : 0f;

            var noite = new Color(0.05f, 0.07f, 0.16f);
            var azul = new Color(0.55f, 0.76f, 0.96f);
            var alaranjado = new Color(0.96f, 0.62f, 0.38f);
            // Perto do nascer e do pôr do sol o céu passa pelo alaranjado.
            ceu = dia < 0.35f
                ? Color.Lerp(noite, alaranjado, dia / 0.35f)
                : Color.Lerp(alaranjado, azul, (dia - 0.35f) / 0.3f);

            proximaLeituraDaHora = Time.unscaledTime + 30f;
        }

        void CalcularAlvos(out float intensidade, out Color corDaLuz, out Color ambiente)
        {
            // De noite a luz vem da lâmpada do quarto: mais fraca e amarelada.
            intensidade = Mathf.Lerp(0.75f, 1.15f, dia);
            corDaLuz = Color.Lerp(new Color(1f, 0.84f, 0.66f), new Color(1f, 0.97f, 0.92f), dia);
            ambiente = Color.Lerp(new Color(0.34f, 0.31f, 0.33f), new Color(0.56f, 0.58f, 0.62f), dia);

            switch (estado)
            {
                case EstadoVisual.Bem:
                    intensidade *= 1.05f;
                    corDaLuz *= new Color(1f, 0.97f, 0.9f);
                    break;
                case EstadoVisual.Sinais:
                    intensidade *= 0.92f;
                    break;
                case EstadoVisual.Abatido:
                    intensidade *= 0.75f;
                    corDaLuz *= new Color(0.85f, 0.9f, 1f);
                    ambiente *= new Color(0.8f, 0.84f, 0.95f);
                    break;
            }
        }

        void Aplicar()
        {
            luz.intensity = intensidadeAtual;
            luz.color = corDaLuzAtual;
            RenderSettings.ambientLight = ambienteAtual;
            if (vidro != null) Pintar(vidro, ceu);
        }

        void Update()
        {
            if (Time.unscaledTime >= proximaLeituraDaHora) LerHora();

            CalcularAlvos(out float i, out Color c, out Color a);
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 2.5f); // transição suave (~1 s)
            intensidadeAtual = Mathf.Lerp(intensidadeAtual, i, k);
            corDaLuzAtual = Color.Lerp(corDaLuzAtual, c, k);
            ambienteAtual = Color.Lerp(ambienteAtual, a, k);
            Aplicar();
        }

        // ---------- Móveis modelados ----------

        /// <summary>
        /// Instancia um prefab de Resources/Quarto apoiado no chão em <paramref name="pos"/>, girado
        /// <paramref name="giroY"/> graus. Devolve null se o prefab não existe (aí entra o bloco).
        /// </summary>
        Transform Movel(string nome, string recurso, Vector3 pos, float giroY)
        {
            var prefab = Resources.Load<GameObject>(recurso);
            if (prefab == null) return null;
            var go = Instantiate(prefab, transform);
            go.name = nome;
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0, giroY, 0);
            return go.transform;
        }

        /// <summary>Desloca o móvel no X para que a lateral dele encoste na parede em <paramref name="xDaParede"/>.</summary>
        void Encostar(Transform movel, float xDaParede)
        {
            var rends = movel.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return;
            var caixa = rends[0].bounds;
            foreach (var r in rends) caixa.Encapsulate(r.bounds);
            float borda = xDaParede < 0 ? caixa.min.x : caixa.max.x;
            float folga = xDaParede < 0 ? 0.02f : -0.02f;
            var alvo = transform.TransformPoint(new Vector3(xDaParede + folga, 0, 0)).x;
            movel.position += new Vector3(alvo - borda, 0, 0);
        }

        /// <summary>
        /// Troca a cor chapada de um bloco pelo material texturizado de Resources/Quarto/Texturas, repetindo
        /// a textura conforme o tamanho real da superfície. Se o material não existe, o bloco fica como está.
        /// </summary>
        static void Texturizar(Renderer r, string recurso, float largura, float altura)
        {
            var original = Resources.Load<Material>(recurso);
            if (original == null) return;
            var m = new Material(original);
            m.mainTextureScale = new Vector2(largura / MetrosPorTextura, altura / MetrosPorTextura);
            r.sharedMaterial = m;
        }

        // ---------- Blocos ----------

        static void SemSombra(Renderer r) => r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        Renderer Bloco(string nome, Vector3 pos, Vector3 tamanho, Color cor) =>
            Primitiva(PrimitiveType.Cube, nome, pos, tamanho, cor);

        Renderer Primitiva(PrimitiveType tipo, string nome, Vector3 pos, Vector3 escala, Color cor)
        {
            var go = GameObject.CreatePrimitive(tipo);
            go.name = nome;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = escala;
            var r = go.GetComponent<Renderer>();
            Pintar(r, cor);
            return r;
        }

        static Material materialBase;

        /// <summary>
        /// Pinta um objeto. Funciona no pipeline padrão e no URP.
        /// O material padrão das primitivas não entra no build (a cena é vazia) e fica magenta
        /// no celular, então troca pelo material de Resources, que garante o shader no APK.
        /// </summary>
        public static void Pintar(Renderer r, Color cor)
        {
            if (materialBase == null) materialBase = Resources.Load<Material>("BonecoMaterial");
            if (materialBase != null && r.sharedMaterial != null && r.sharedMaterial.shader != materialBase.shader)
                r.sharedMaterial = materialBase;
            var m = r.material;
            m.color = cor;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", cor);
        }
    }
}
