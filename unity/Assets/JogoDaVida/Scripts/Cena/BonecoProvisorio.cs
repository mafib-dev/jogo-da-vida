using UnityEngine;

namespace JogoDaVida.Cena
{
    public enum EstadoVisual { Bem, Normal, Sinais, Abatido }

    /// <summary>
    /// Personagem da cena 3D (o nome ficou da primeira versão). Usa o
    /// <see cref="PersonagemHumano"/> quando o prefab dele está em Resources; se não
    /// estiver (ou o rig não bater), cai no boneco de primitivas (cápsula + esfera).
    /// Fica dentro do <see cref="CenaDoQuarto"/>, que cuida de câmera, luz e móveis;
    /// a cena ocupa a parte de cima da tela.
    /// </summary>
    public class BonecoProvisorio : MonoBehaviour
    {
        /// <summary>Fração da altura da tela usada pela cena 3D (de cima para baixo).</summary>
        public const float AlturaDaCena = 0.42f;

        Transform corpo, cabeca, bocaPivo;
        Renderer corpoRender;
        CenaDoQuarto quarto;
        PersonagemHumano humano;
        EstadoVisual estado = EstadoVisual.Normal;
        float tempoPulo = -10f, tempoGiro = -10f;
        Vector3 posBase;

        public static BonecoProvisorio Criar()
        {
            var raiz = new GameObject("Cena do boneco");
            DontDestroyOnLoad(raiz);
            var b = raiz.AddComponent<BonecoProvisorio>();
            b.Montar();
            return b;
        }

        void Montar()
        {
            // Câmera, luz, paredes e móveis ficam por conta do quarto.
            quarto = CenaDoQuarto.Criar(transform);

            humano = PersonagemHumano.Criar(transform);
            if (humano != null)
            {
                DefinirEstado(EstadoVisual.Normal);
                return;
            }
            MontarCapsula();
        }

        /// <summary>Reserva: boneco de primitivas, se o personagem humano não carregar.</summary>
        void MontarCapsula()
        {
            // Corpo
            var c = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            c.name = "Corpo";
            c.transform.SetParent(transform);
            c.transform.localPosition = new Vector3(0, 0.75f, 0);
            c.transform.localScale = new Vector3(0.7f, 0.75f, 0.5f);
            corpo = c.transform;
            corpoRender = c.GetComponent<Renderer>();
            posBase = corpo.localPosition;

            var h = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            h.name = "Cabeça";
            h.transform.SetParent(corpo);
            h.transform.localPosition = new Vector3(0, 1.3f, 0);
            h.transform.localScale = new Vector3(0.9f, 0.85f, 1.2f);
            Pintar(h, new Color(0.96f, 0.8f, 0.66f));
            cabeca = h.transform;

            foreach (var x in new[] { -0.17f, 0.17f })
            {
                var olho = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                olho.transform.SetParent(cabeca);
                olho.transform.localPosition = new Vector3(x, 0.08f, -0.45f);
                olho.transform.localScale = new Vector3(0.12f, 0.14f, 0.08f);
                Pintar(olho, Color.black);
            }

            bocaPivo = new GameObject("Boca").transform;
            bocaPivo.SetParent(cabeca);
            bocaPivo.localPosition = new Vector3(0, -0.2f, -0.46f);
            var boca = GameObject.CreatePrimitive(PrimitiveType.Cube);
            boca.transform.SetParent(bocaPivo);
            boca.transform.localPosition = Vector3.zero;
            boca.transform.localScale = new Vector3(0.3f, 0.05f, 0.05f);
            Pintar(boca, new Color(0.5f, 0.15f, 0.15f));

            foreach (var col in GetComponentsInChildren<Collider>()) Destroy(col);
            DefinirEstado(EstadoVisual.Normal);
        }

        static void Pintar(GameObject go, Color cor) => CenaDoQuarto.Pintar(go.GetComponent<Renderer>(), cor);

        public void DefinirEstado(EstadoVisual novo)
        {
            estado = novo;
            quarto.DefinirEstado(novo);
            if (humano != null)
            {
                humano.DefinirEstado(novo);
                return;
            }
            Color cor = novo switch
            {
                EstadoVisual.Bem => new Color(0.25f, 0.65f, 0.4f),
                EstadoVisual.Normal => new Color(0.3f, 0.5f, 0.8f),
                EstadoVisual.Sinais => new Color(0.75f, 0.6f, 0.3f),
                _ => new Color(0.5f, 0.5f, 0.55f),
            };
            Pintar(corpoRender.gameObject, cor);
        }

        public void Comemorar()
        {
            if (humano != null) humano.Comemorar();
            tempoPulo = Time.time;
        }

        public void SubiuDeNivel()
        {
            if (humano != null) humano.SubiuDeNivel();
            tempoPulo = Time.time; tempoGiro = Time.time;
        }

        void Update()
        {
            if (humano != null) return;   // o personagem humano se anima sozinho
            float t = Time.time;
            // Respiração (idle). Mais lenta e curvada quando abatido.
            float ritmo = estado == EstadoVisual.Abatido ? 1.2f : 2.2f;
            float resp = Mathf.Sin(t * ritmo) * 0.015f;
            float curvatura = estado switch
            {
                EstadoVisual.Bem => -3f,
                EstadoVisual.Normal => 0f,
                EstadoVisual.Sinais => 8f,
                _ => 18f,
            };

            float pulo = 0f;
            float dp = t - tempoPulo;
            if (dp < 0.6f) pulo = Mathf.Sin(dp / 0.6f * Mathf.PI) * 0.45f;

            float giro = 0f;
            float dg = t - tempoGiro;
            if (dg < 1f) giro = Mathf.SmoothStep(0, 360, dg);

            corpo.localPosition = posBase + new Vector3(0, resp + pulo, 0);
            corpo.localRotation = Quaternion.Euler(curvatura, giro, 0);
            corpo.localScale = new Vector3(0.7f, 0.75f + resp, 0.5f);

            // Boca: sorriso (inclinação) conforme o estado
            float sorriso = estado switch
            {
                EstadoVisual.Bem => 1f,
                EstadoVisual.Normal => 0.4f,
                EstadoVisual.Sinais => -0.2f,
                _ => -0.7f,
            };
            bocaPivo.localScale = new Vector3(1, 1 + Mathf.Abs(sorriso) * 1.5f, 1);
            bocaPivo.localPosition = new Vector3(0, -0.2f - sorriso * 0.03f, -0.46f);
            cabeca.localRotation = Quaternion.Euler(estado == EstadoVisual.Abatido ? 15f : 0f, Mathf.Sin(t * 0.7f) * 6f, 0);
        }
    }
}
