using System;
using System.Linq;
using System.Threading.Tasks;
using JogoDaVida.Dados;
using JogoDaVida.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace JogoDaVida
{
    // Lista de mercado e agenda (módulo 4 do Organizador Pessoal). Não dão pontos.
    public partial class JogoApp
    {
        ItemCompra[] compras = new ItemCompra[0];
        Evento[] eventos = new Evento[0];

        static readonly (string valor, string texto)[] CategoriasCompra =
        {
            ("mercado", "Mercado"), ("farmacia", "Farmácia"), ("casa", "Casa"), ("outros", "Outros"),
        };

        static string Iso(DateTime d) => d.ToString("yyyy-MM-dd");

        /// <summary>Aceita "20/11", "20/11/2026" ou "2026-11-20". Devolve null se não entender.</summary>
        static DateTime? LerData(string texto)
        {
            texto = (texto ?? "").Trim();
            var hoje = HojeBrasilia();
            if (texto.Equals("hoje", StringComparison.OrdinalIgnoreCase)) return hoje;
            if (texto.Equals("amanhã", StringComparison.OrdinalIgnoreCase) || texto.Equals("amanha", StringComparison.OrdinalIgnoreCase)) return hoje.AddDays(1);
            var partes = texto.Split('/', '-');
            try
            {
                if (partes.Length == 3 && partes[0].Length == 4)
                    return new DateTime(int.Parse(partes[0]), int.Parse(partes[1]), int.Parse(partes[2]));
                if (partes.Length == 3)
                {
                    int ano = int.Parse(partes[2]);
                    if (ano < 100) ano += 2000;
                    return new DateTime(ano, int.Parse(partes[1]), int.Parse(partes[0]));
                }
                if (partes.Length == 2)
                {
                    var d = new DateTime(hoje.Year, int.Parse(partes[1]), int.Parse(partes[0]));
                    return d < hoje ? d.AddYears(1) : d;
                }
            }
            catch { /* data inválida */ }
            return null;
        }

        static string TextoData(DateTime d)
        {
            var hoje = HojeBrasilia();
            if (d == hoje) return "Hoje";
            if (d == hoje.AddDays(1)) return "Amanhã";
            string[] dias = { "dom", "seg", "ter", "qua", "qui", "sex", "sáb" };
            return $"{dias[(int)d.DayOfWeek]}, {d:dd/MM}";
        }

        /// <summary>Carrega o que a tela principal mostra (lista de mercado e eventos de hoje).</summary>
        async Task CarregarOrganizador()
        {
            var c = api.ListaDeCompras();
            var e = api.Agenda(Iso(HojeBrasilia()));
            await Task.WhenAll(c, e);
            compras = c.Result;
            eventos = e.Result;
        }

        /// <summary>Cartões da tela principal: eventos de hoje (acima das tarefas) e a tarefa automática de compras.</summary>
        void CartoesOrganizador(VisualElement lista)
        {
            var hoje = HojeBrasilia();
            var deHoje = eventos.Where(ev => ev.Dia == hoje).ToArray();
            if (deHoje.Length > 0)
            {
                var c = Estilo.CaixaCartao();
                c.Add(Estilo.Texto_("Eventos de hoje", 26, true, Estilo.TextoSuave));
                foreach (var ev in deHoje)
                {
                    var l = Estilo.Texto_((ev.Hora.Length > 0 ? ev.Hora + " · " : "") + ev.titulo + (ev.Feito ? " (feito)" : ""), 32, !ev.Feito,
                        ev.Feito ? Estilo.TextoSuave : Estilo.Texto);
                    c.Add(l);
                }
                c.RegisterCallback<ClickEvent>(_ => _ = MostrarAgenda());
                lista.Add(c);
            }
            if (compras.Length > 0)
            {
                var c = Estilo.CaixaCartao();
                c.style.flexDirection = FlexDirection.Row;
                c.style.alignItems = Align.Center;
                var info = new VisualElement();
                info.style.flexGrow = 1; info.style.flexShrink = 1;
                info.Add(Estilo.Texto_(compras.Length == 1 ? "Compras: 1 item pendente" : $"Compras: {compras.Length} itens pendentes", 34, true));
                info.Add(Estilo.Texto_(string.Join(", ", compras.Take(4).Select(x => x.item)) + (compras.Length > 4 ? "..." : ""), 28, false, Estilo.TextoSuave));
                c.Add(info);
                var abrir = Estilo.Botao("Abrir", () => _ = MostrarCompras(), Estilo.Primaria, true);
                abrir.style.width = 170;
                abrir.style.marginLeft = 16;
                c.Add(abrir);
                lista.Add(c);
            }
        }

        // =============================================================== Lista de mercado

        async Task MostrarCompras()
        {
            var t = NovaTela(false);
            t.Add(Estilo.Texto_("Lista de compras", 52, true));
            var rolagem = Estilo.Rolagem();
            t.Add(rolagem);
            t.Add(Estilo.Botao("Voltar", () => _ = MostrarPrincipal(), Estilo.TextoSuave, true));

            var item = Estilo.Campo("O que comprar?", "Ex.: café");
            var qtd = Estilo.Campo("Quantidade (opcional)", "Ex.: 2 pacotes");
            string categoria = "mercado";
            var escCat = Estilo.Escolha("Onde", CategoriasCompra, categoria, v => categoria = v);
            var msg = Estilo.Texto_("", 30, false, Estilo.Perigo);
            ItemCompra[] historico = new ItemCompra[0];

            void Desenhar()
            {
                rolagem.Clear();
                if (compras.Length == 0) rolagem.Add(Estilo.Texto_("Nada pendente. Adicione um item abaixo.", 30, false, Estilo.TextoSuave));
                foreach (var (cat, nomeCat) in CategoriasCompra)
                {
                    var daCat = compras.Where(x => (x.categoria ?? "mercado") == cat).ToArray();
                    if (daCat.Length == 0) continue;
                    rolagem.Add(Estilo.Texto_(nomeCat, 36, true, Estilo.TextoSuave));
                    foreach (var x in daCat)
                    {
                        var c = Estilo.CaixaCartao();
                        c.style.flexDirection = FlexDirection.Row;
                        c.style.alignItems = Align.Center;
                        var info = new VisualElement();
                        info.style.flexGrow = 1; info.style.flexShrink = 1;
                        info.Add(Estilo.Texto_(x.item, 34, true));
                        if (!string.IsNullOrEmpty(x.quantidade)) info.Add(Estilo.Texto_(x.quantidade, 28, false, Estilo.TextoSuave));
                        c.Add(info);
                        var comprei = Estilo.Botao("Comprei", () => _ = Acao(() => api.MarcarComprado(x.id), $"{x.item}: comprado."), Estilo.Sucesso);
                        comprei.style.width = 200; comprei.style.marginLeft = 12;
                        var tirar = Estilo.Botao("X", () => _ = Acao(() => api.RemoverCompra(x.id), $"{x.item} saiu da lista."), Estilo.Perigo, true);
                        tirar.style.width = 90; tirar.style.marginLeft = 12;
                        c.Add(comprei); c.Add(tirar);
                        rolagem.Add(c);
                    }
                }
                rolagem.Add(item); rolagem.Add(qtd); rolagem.Add(escCat); rolagem.Add(msg);
                rolagem.Add(Estilo.Botao("Adicionar à lista", () => _ = Adicionar()));

                if (historico.Length > 0)
                {
                    rolagem.Add(Estilo.Texto_("Comprados recentemente", 36, true, Estilo.TextoSuave));
                    foreach (var h in historico.Take(15))
                    {
                        string quando = DateTimeOffset.TryParse(h.comprado_em, out var dt) ? dt.UtcDateTime.AddHours(-3).ToString("dd/MM") : "";
                        rolagem.Add(Estilo.Texto_($"{quando}  {h.item}", 28, false, Estilo.TextoSuave));
                    }
                }
            }

            async Task Carregar()
            {
                var c = api.ListaDeCompras();
                var h = api.HistoricoDeCompras();
                await Task.WhenAll(c, h);
                compras = c.Result;
                historico = h.Result;
                Desenhar();
            }

            async Task Acao(Func<Task> chamada, string aviso)
            {
                if (ocupado) return;
                ocupado = true;
                try { await chamada(); Aviso(aviso, Estilo.Sucesso); ocupado = false; await Carregar(); }
                catch (Exception e) { Aviso(e.Message, Estilo.Perigo); }
                finally { ocupado = false; }
            }

            async Task Adicionar()
            {
                if (string.IsNullOrWhiteSpace(item.value)) { msg.text = "Escreva o item."; return; }
                string nome = item.value.Trim();
                string q = qtd.value.Trim();
                item.value = ""; qtd.value = ""; msg.text = "";
                await Acao(() => api.AdicionarCompra(nome, q, categoria), $"{nome} entrou na lista.");
            }

            try { await Carregar(); }
            catch (Exception e) { Aviso(e.Message, Estilo.Perigo); }
        }

        // =============================================================== Agenda

        async Task MostrarAgenda()
        {
            var t = NovaTela(false);
            t.Add(Estilo.Texto_("Agenda", 52, true));
            var rolagem = Estilo.Rolagem();
            t.Add(rolagem);
            t.Add(Estilo.Botao("Voltar", () => _ = MostrarPrincipal(), Estilo.TextoSuave, true));

            var titulo = Estilo.Campo("Compromisso", "Ex.: Dentista");
            var data = Estilo.Campo("Data (dd/mm, \"hoje\" ou \"amanhã\")", "Ex.: 20/11");
            var hora = Estilo.Campo("Hora (opcional, HH:MM)", "Ex.: 14:00");
            var msg = Estilo.Texto_("", 30, false, Estilo.Perigo);
            Evento[] passados = new Evento[0];

            void Desenhar()
            {
                rolagem.Clear();
                var hoje = HojeBrasilia();
                if (eventos.Length == 0) rolagem.Add(Estilo.Texto_("Nenhum compromisso marcado.", 30, false, Estilo.TextoSuave));
                DateTime? diaAtual = null;
                foreach (var ev in eventos.OrderBy(e => e.Dia).ThenBy(e => e.Hora))
                {
                    if (diaAtual != ev.Dia)
                    {
                        diaAtual = ev.Dia;
                        rolagem.Add(Estilo.Texto_(TextoData(ev.Dia), 36, true, Estilo.TextoSuave));
                    }
                    rolagem.Add(CartaoEvento(ev, permitirFeito: ev.Dia <= hoje));
                }

                rolagem.Add(Estilo.Texto_("Novo compromisso", 36, true, Estilo.TextoSuave));
                rolagem.Add(titulo); rolagem.Add(data); rolagem.Add(hora); rolagem.Add(msg);
                rolagem.Add(Estilo.Botao("Marcar na agenda", () => _ = Criar()));

                var naoFeitos = passados.Where(e => !e.Feito).ToArray();
                if (naoFeitos.Length > 0)
                {
                    rolagem.Add(Estilo.Texto_("Não realizados", 36, true, Estilo.TextoSuave));
                    foreach (var ev in naoFeitos)
                        rolagem.Add(Estilo.Texto_($"{ev.Dia:dd/MM}  {ev.titulo}", 28, false, Estilo.TextoSuave));
                }
            }

            VisualElement CartaoEvento(Evento ev, bool permitirFeito)
            {
                var c = Estilo.CaixaCartao();
                c.Add(Estilo.Texto_((ev.Hora.Length > 0 ? ev.Hora + " · " : "") + ev.titulo + (ev.Feito ? " (feito)" : ""), 34, !ev.Feito,
                    ev.Feito ? Estilo.TextoSuave : Estilo.Texto));
                if (!string.IsNullOrEmpty(ev.descricao)) c.Add(Estilo.Texto_(ev.descricao, 28, false, Estilo.TextoSuave));
                if (ev.Feito) return c;
                var linha = new VisualElement();
                linha.style.flexDirection = FlexDirection.Row;
                if (permitirFeito)
                {
                    var feito = Estilo.Botao("Feito", () => _ = Acao(() => api.MarcarEventoFeito(ev.id), $"{ev.titulo}: feito."), Estilo.Sucesso);
                    feito.style.flexGrow = 1; feito.style.flexBasis = 0; feito.style.marginRight = 8;
                    linha.Add(feito);
                }
                var adiar = Estilo.Botao("Adiar", () => PedirNovaData(ev), Estilo.Primaria, true);
                adiar.style.flexGrow = 1; adiar.style.flexBasis = 0; adiar.style.marginRight = 8;
                var apagar = Estilo.Botao("Apagar", () => _ = Acao(() => api.RemoverEvento(ev.id), $"{ev.titulo} saiu da agenda."), Estilo.Perigo, true);
                apagar.style.flexGrow = 1; apagar.style.flexBasis = 0;
                linha.Add(adiar); linha.Add(apagar);
                c.Add(linha);
                return c;
            }

            void PedirNovaData(Evento ev)
            {
                var hoje = HojeBrasilia();
                Escolher($"Adiar \"{ev.titulo}\" para", new[]
                {
                    ("Amanhã", (Action)(() => _ = Adiar(ev, hoje.AddDays(1)))),
                    ("Daqui a 2 dias", (Action)(() => _ = Adiar(ev, hoje.AddDays(2)))),
                    ("Semana que vem", (Action)(() => _ = Adiar(ev, hoje.AddDays(7)))),
                    ("Data do campo \"Data\" abaixo", (Action)(() =>
                    {
                        var d = LerData(data.value);
                        if (d == null) { msg.text = "Escreva a nova data no campo Data e tente de novo."; return; }
                        _ = Adiar(ev, d.Value);
                    })),
                });
            }

            async Task Adiar(Evento ev, DateTime nova) =>
                await Acao(() => api.AdiarEvento(ev.id, Iso(nova)), $"{ev.titulo} foi para {TextoData(nova).ToLowerInvariant()}.");

            async Task Carregar()
            {
                var hoje = HojeBrasilia();
                var f = api.Agenda(Iso(hoje));
                var p = api.EventosPassados(Iso(hoje));
                await Task.WhenAll(f, p);
                eventos = f.Result;
                passados = p.Result;
                Desenhar();
            }

            async Task Acao(Func<Task> chamada, string aviso)
            {
                if (ocupado) return;
                ocupado = true;
                try { await chamada(); Aviso(aviso, Estilo.Sucesso); ocupado = false; await Carregar(); }
                catch (Exception e) { Aviso(e.Message, Estilo.Perigo); }
                finally { ocupado = false; }
            }

            async Task Criar()
            {
                if (string.IsNullOrWhiteSpace(titulo.value)) { msg.text = "Escreva o compromisso."; return; }
                var d = LerData(data.value);
                if (d == null) { msg.text = "Data inválida. Use 20/11, hoje ou amanhã."; return; }
                string h = "";
                if (!string.IsNullOrWhiteSpace(hora.value))
                {
                    if (!TimeSpan.TryParse(hora.value.Trim(), out var ts) || ts < TimeSpan.Zero || ts >= TimeSpan.FromDays(1))
                    {
                        msg.text = "Hora inválida. Use 14:00."; return;
                    }
                    h = $"{ts.Hours:00}:{ts.Minutes:00}:00";
                }
                string nome = titulo.value.Trim();
                titulo.value = ""; hora.value = ""; data.value = ""; msg.text = "";
                await Acao(() => api.CriarEvento(nome, "", Iso(d.Value), h), $"{nome}: {TextoData(d.Value).ToLowerInvariant()}.");
            }

            try { await Carregar(); }
            catch (Exception e) { Aviso(e.Message, Estilo.Perigo); }
        }
    }
}
