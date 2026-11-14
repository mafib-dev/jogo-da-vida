using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace JogoDaVida.UI
{
    /// <summary>Atalhos para montar a interface em código, com um visual simples e consistente.</summary>
    public static class Estilo
    {
        public static readonly Color Fundo = new Color(0.96f, 0.95f, 0.93f);
        public static readonly Color Cartao = Color.white;
        public static readonly Color Texto = new Color(0.13f, 0.13f, 0.16f);
        public static readonly Color TextoSuave = new Color(0.42f, 0.42f, 0.47f);
        public static readonly Color Primaria = new Color(0.22f, 0.45f, 0.85f);
        public static readonly Color Sucesso = new Color(0.2f, 0.6f, 0.38f);
        public static readonly Color Moeda = new Color(0.85f, 0.62f, 0.1f);
        public static readonly Color Borda = new Color(0.85f, 0.84f, 0.82f);
        public static readonly Color Perigo = new Color(0.78f, 0.25f, 0.22f);

        public static void Raio(this IStyle s, float r)
        {
            s.borderTopLeftRadius = r; s.borderTopRightRadius = r;
            s.borderBottomLeftRadius = r; s.borderBottomRightRadius = r;
        }

        public static void Margem(this IStyle s, float v, float h)
        {
            s.marginTop = v; s.marginBottom = v; s.marginLeft = h; s.marginRight = h;
        }

        public static void Preenchimento(this IStyle s, float v, float h)
        {
            s.paddingTop = v; s.paddingBottom = v; s.paddingLeft = h; s.paddingRight = h;
        }

        public static void BordaCor(this IStyle s, Color c, float largura = 2)
        {
            s.borderTopColor = c; s.borderBottomColor = c; s.borderLeftColor = c; s.borderRightColor = c;
            s.borderTopWidth = largura; s.borderBottomWidth = largura; s.borderLeftWidth = largura; s.borderRightWidth = largura;
        }

        public static Label Texto_(string texto, int tamanho = 34, bool negrito = false, Color? cor = null)
        {
            var l = new Label(texto);
            l.style.fontSize = tamanho;
            l.style.color = cor ?? Texto;
            l.style.whiteSpace = WhiteSpace.Normal;
            if (negrito) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.Margem(4, 0);
            return l;
        }

        public static Button Botao(string texto, Action aoClicar, Color? cor = null, bool contorno = false)
        {
            var b = new Button(aoClicar) { text = texto };
            var c = cor ?? Primaria;
            b.style.fontSize = 34;
            b.style.height = 104;
            b.style.Raio(20);
            b.style.Margem(8, 0);
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            if (contorno)
            {
                b.style.backgroundColor = Color.clear;
                b.style.color = c;
                b.style.BordaCor(c, 3);
            }
            else
            {
                b.style.backgroundColor = c;
                b.style.color = Color.white;
                b.style.BordaCor(c, 0);
            }
            return b;
        }

        public static TextField Campo(string rotulo, string dica = "", bool multilinha = false)
        {
            var t = new TextField(rotulo) { multiline = multilinha };
            t.style.flexDirection = FlexDirection.Column;
            t.style.Margem(8, 0);
            t.labelElement.style.fontSize = 30;
            t.labelElement.style.color = TextoSuave;
            t.labelElement.style.minWidth = 0;
            t.labelElement.style.marginBottom = 6;
            // Perguntas longas quebram a linha em vez de cortar com "...".
            t.labelElement.style.whiteSpace = WhiteSpace.Normal;
            t.labelElement.style.textOverflow = TextOverflow.Clip;
            t.labelElement.style.flexShrink = 1;
            var entrada = t.Q(className: TextField.inputUssClassName);
            if (entrada != null)
            {
                entrada.style.fontSize = 34;
                entrada.style.minHeight = multilinha ? 160 : 96;
                entrada.style.backgroundColor = Color.white;
                // Texto de exemplo em cinza; o que a pessoa digita, escuro.
                void Cor() => entrada.style.color = string.IsNullOrEmpty(t.value) ? TextoSuave : Texto;
                Cor();
                t.RegisterValueChangedCallback(_ => Cor());
                entrada.style.BordaCor(Borda, 2);
                entrada.style.Raio(14);
                entrada.style.Preenchimento(10, 18);
                if (multilinha) entrada.style.whiteSpace = WhiteSpace.Normal;
            }
#if UNITY_2023_1_OR_NEWER
            if (!string.IsNullOrEmpty(dica)) t.textEdition.placeholder = dica;
#endif
            return t;
        }

        /// <summary>Grupo de botões em que só um fica marcado (substitui o dropdown).</summary>
        public static VisualElement Escolha(string rotulo, IList<(string valor, string texto)> opcoes,
            string inicial, Action<string> aoMudar)
        {
            var bloco = new VisualElement();
            bloco.style.Margem(8, 0);
            var l = Texto_(rotulo, 30, false, TextoSuave);
            bloco.Add(l);
            var linha = new VisualElement();
            linha.style.flexDirection = FlexDirection.Row;
            bloco.Add(linha);
            var botoes = new List<Button>();
            void Marcar(string v)
            {
                for (int i = 0; i < opcoes.Count; i++)
                {
                    bool sel = opcoes[i].valor == v;
                    botoes[i].style.backgroundColor = sel ? Primaria : Color.white;
                    botoes[i].style.color = sel ? Color.white : Texto;
                }
                aoMudar(v);
            }
            foreach (var (valor, texto) in opcoes)
            {
                var b = new Button(() => Marcar(valor)) { text = texto };
                b.style.flexGrow = 1;
                b.style.height = 88;
                b.style.fontSize = 30;
                b.style.Raio(14);
                b.style.Margem(0, 4);
                b.style.BordaCor(Borda, 2);
                botoes.Add(b);
                linha.Add(b);
            }
            Marcar(inicial);
            return bloco;
        }

        public static VisualElement CaixaCartao()
        {
            var c = new VisualElement();
            c.style.backgroundColor = Cartao;
            c.style.Raio(24);
            c.style.Preenchimento(20, 26);
            c.style.Margem(10, 0);
            c.style.BordaCor(Borda, 1);
            return c;
        }

        public static ScrollView Rolagem()
        {
            var s = new ScrollView(ScrollViewMode.Vertical);
            s.style.flexGrow = 1;
            s.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            s.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            s.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;

            // A caixa de texto "prende" o toque e impede a rolagem. Quando o arrasto começa
            // em cima de um campo, a própria lista rola e o campo solta o toque.
            int dedo = -1;
            float inicioY = 0, inicioOffset = 0;
            bool arrastando = false;
            s.RegisterCallback<PointerDownEvent>(e =>
            {
                dedo = -1;
                if (e.target is VisualElement alvo && (alvo is TextField || alvo.GetFirstAncestorOfType<TextField>() != null))
                {
                    dedo = e.pointerId;
                    inicioY = e.position.y;
                    inicioOffset = s.scrollOffset.y;
                    arrastando = false;
                }
            }, TrickleDown.TrickleDown);
            s.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (e.pointerId != dedo) return;
                float dy = e.position.y - inicioY;
                if (!arrastando && Mathf.Abs(dy) > 25)
                {
                    arrastando = true;
                    var preso = s.panel?.GetCapturingElement(e.pointerId);
                    if (preso != null && preso != s) preso.ReleasePointer(e.pointerId);
                    s.focusController?.focusedElement?.Blur();
                }
                if (arrastando) s.scrollOffset = new Vector2(0, inicioOffset - dy);
            }, TrickleDown.TrickleDown);
            s.RegisterCallback<PointerUpEvent>(e => { if (e.pointerId == dedo) dedo = -1; }, TrickleDown.TrickleDown);
            return s;
        }
    }
}
