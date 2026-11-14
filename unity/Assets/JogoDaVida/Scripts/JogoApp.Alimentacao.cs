using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JogoDaVida.Dados;
using JogoDaVida.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace JogoDaVida
{
    // Plano alimentar e barra de Alimentação (módulo 3 do Organizador Pessoal).
    public partial class JogoApp
    {
        Refeicao[] plano = new Refeicao[0];

        static readonly string[] NomesDiasCompletos = { "Domingo", "Segunda", "Terça", "Quarta", "Quinta", "Sexta", "Sábado" };

        static DateTime AgoraBrasilia() => DateTime.UtcNow.AddHours(-3);

        /// <summary>Refeições do plano para hoje, em ordem de horário.</summary>
        Refeicao[] RefeicoesDeHoje() =>
            plano.Where(r => r.dia_semana == (int)AgoraBrasilia().DayOfWeek).OrderBy(r => r.Minutos).ToArray();

        /// <summary>Cartão "Próxima refeição" da tela principal (null se não há plano para hoje).</summary>
        VisualElement CartaoProximaRefeicao()
        {
            var hoje = RefeicoesDeHoje();
            if (hoje.Length == 0) return null;
            var agora = AgoraBrasilia();
            int minutos = agora.Hour * 60 + agora.Minute;
            // A refeição em andamento continua "a próxima" por 30 min depois do horário.
            var proxima = hoje.FirstOrDefault(r => r.Minutos >= minutos - 30);
            if (proxima == null) return null;
            var c = Estilo.CaixaCartao();
            c.Add(Estilo.Texto_("Próxima refeição", 26, true, Estilo.TextoSuave));
            c.Add(Estilo.Texto_($"{proxima.Hora} · {proxima.nome}", 34, true));
            if (!string.IsNullOrEmpty(proxima.descricao)) c.Add(Estilo.Texto_(proxima.descricao, 28, false, Estilo.TextoSuave));
            c.RegisterCallback<ClickEvent>(_ => _ = MostrarRefeicoes());
            return c;
        }

        // =============================================================== Refeições do dia

        async Task MostrarRefeicoes()
        {
            var t = NovaTela(false);
            t.Add(Estilo.Texto_("Alimentação", 52, true));
            var rolagem = Estilo.Rolagem();
            t.Add(rolagem);
            t.Add(Estilo.Botao("Voltar", () => _ = MostrarPrincipal(), Estilo.TextoSuave, true));

            RegistroRefeicao[] marcadas = new RegistroRefeicao[0];
            var oQueComi = Estilo.Campo("O que você comeu? (para troca ou fora do plano)", "Ex.: frango com arroz em vez de peixe", true);
            var msg = Estilo.Texto_("", 30, false, Estilo.Perigo);

            void Desenhar()
            {
                rolagem.Clear();
                var hoje = RefeicoesDeHoje();
                rolagem.Add(Estilo.Texto_($"Hoje ({NomesDiasCompletos[(int)AgoraBrasilia().DayOfWeek]})", 36, true, Estilo.TextoSuave));
                if (hoje.Length == 0)
                    rolagem.Add(Estilo.Texto_("Nenhuma refeição no plano para hoje. Toque em \"Editar plano\" para cadastrar.", 30, false, Estilo.TextoSuave));
                foreach (var r in hoje)
                {
                    var c = Estilo.CaixaCartao();
                    c.Add(Estilo.Texto_($"{r.Hora} · {r.nome}" + (r.tipo == "lanche" ? " (lanche)" : ""), 34, true));
                    if (!string.IsNullOrEmpty(r.descricao)) c.Add(Estilo.Texto_(r.descricao, 28, false, Estilo.TextoSuave));
                    if (!string.IsNullOrEmpty(r.observacoes)) c.Add(Estilo.Texto_("Substituições: " + r.observacoes, 26, false, Estilo.TextoSuave));
                    var feita = marcadas.FirstOrDefault(m => m.refeicao_id == r.id);
                    if (feita != null)
                    {
                        string txt = feita.status == "seguida" ? "Seguida" : feita.status == "substituida" ? "Substituída" : "Perdida";
                        if (!string.IsNullOrEmpty(feita.o_que_comi)) txt += $": {feita.o_que_comi}";
                        c.Add(Estilo.Texto_(txt, 28, true, feita.status == "perdida" ? Estilo.TextoSuave : Estilo.Sucesso));
                    }
                    else
                    {
                        var linha = new VisualElement();
                        linha.style.flexDirection = FlexDirection.Row;
                        foreach (var (status, rotulo, cor) in new[] { ("seguida", "Segui", Estilo.Sucesso), ("substituida", "Troquei", Estilo.Primaria), ("perdida", "Perdi", Estilo.TextoSuave) })
                        {
                            var b = Estilo.Botao(rotulo, () => _ = Marcar(r.id, status), cor, contorno: status != "seguida");
                            b.style.flexGrow = 1;
                            b.style.flexBasis = 0;
                            b.style.marginRight = 8;
                            linha.Add(b);
                        }
                        c.Add(linha);
                    }
                    rolagem.Add(c);
                }

                var fora = marcadas.Where(m => string.IsNullOrEmpty(m.refeicao_id)).ToArray();
                foreach (var m in fora)
                    rolagem.Add(Estilo.Texto_($"Fora do plano: {m.o_que_comi}", 28, false, Estilo.TextoSuave));

                rolagem.Add(oQueComi);
                rolagem.Add(Estilo.Botao("Comi algo fora do plano", () => _ = Marcar(null, "substituida"), contorno: true));
                rolagem.Add(msg);

                // Meta de água
                var meta = Estilo.Campo("Meta de água por dia (ml)", "Ex.: 2500");
                meta.keyboardType = TouchScreenKeyboardType.NumberPad;
                if (barras != null && barras.meta_agua_ml > 0) meta.value = barras.meta_agua_ml.ToString();
                rolagem.Add(meta);
                rolagem.Add(Estilo.Botao("Salvar meta de água", () => _ = SalvarMeta(meta.value), Estilo.TextoSuave, true));
                rolagem.Add(Estilo.Botao("Editar plano", () => _ = MostrarPlano()));
            }

            async Task Carregar()
            {
                var p = api.PlanoAlimentar();
                var m = api.RefeicoesDoDia(AgoraBrasilia().ToString("yyyy-MM-dd"));
                await Task.WhenAll(p, m);
                plano = p.Result;
                marcadas = m.Result;
                Desenhar();
            }

            async Task Marcar(string refeicaoId, string status)
            {
                if (ocupado) return;
                string texto = oQueComi.value.Trim();
                if (refeicaoId == null && texto.Length == 0) { msg.text = "Conte o que você comeu no campo acima."; return; }
                ocupado = true;
                try
                {
                    var r = await api.RegistrarRefeicao(refeicaoId, status, status == "perdida" ? "" : texto);
                    if (status != "perdida") boneco.Comemorar();
                    string extra = r.pontos > 0 ? $"  ·  +{r.pontos} pontos" : "";
                    Aviso(status == "perdida" ? "Anotado. Sem problema, a próxima refeição está aí." : $"Alimentação: {r.alimentacao}%{extra}",
                        status == "perdida" ? Estilo.TextoSuave : Estilo.Sucesso);
                    oQueComi.value = "";
                    ocupado = false;
                    await Carregar();
                    if (r.subiu_de_nivel) MostrarSubiuDeNivel(r.nivel);
                }
                catch (Exception e) { msg.style.color = Estilo.Perigo; msg.text = e.Message; }
                finally { ocupado = false; }
            }

            async Task SalvarMeta(string valor)
            {
                if (ocupado) return;
                if (!int.TryParse(valor.Trim(), out int ml) || ml < 500 || ml > 6000) { msg.text = "A meta de água vai de 500 a 6000 ml."; return; }
                ocupado = true;
                try
                {
                    await api.SalvarMetaAgua(ml);
                    barras = await api.StatusBarras();
                    Aviso($"Meta de água: {ml} ml por dia.", Estilo.Sucesso);
                }
                catch (Exception e) { msg.style.color = Estilo.Perigo; msg.text = e.Message; }
                finally { ocupado = false; }
            }

            rolagem.Add(Estilo.Texto_("Carregando...", 30, false, Estilo.TextoSuave));
            try { await Carregar(); }
            catch (Exception e) { Aviso(e.Message, Estilo.Perigo); }
        }

        // =============================================================== Plano da semana

        async Task MostrarPlano()
        {
            var t = NovaTela(false);
            t.Add(Estilo.Texto_("Plano alimentar", 52, true));
            t.Add(Estilo.Texto_("Cadastre as refeições de cada dia. Dá para cadastrar a mesma refeição em vários dias de uma vez.", 28, false, Estilo.TextoSuave));
            var rolagem = Estilo.Rolagem();
            t.Add(rolagem);
            t.Add(Estilo.Botao("Voltar", () => _ = MostrarRefeicoes(), Estilo.TextoSuave, true));

            try { plano = await api.PlanoAlimentar(); }
            catch (Exception e) { Aviso(e.Message, Estilo.Perigo); }

            rolagem.Add(Estilo.Botao("+ Adicionar refeição", () => MostrarEditarRefeicao(null)));
            // Semana começando na segunda.
            foreach (int dia in new[] { 1, 2, 3, 4, 5, 6, 0 })
            {
                var doDia = plano.Where(r => r.dia_semana == dia).OrderBy(r => r.Minutos).ToArray();
                rolagem.Add(Estilo.Texto_(NomesDiasCompletos[dia], 36, true, Estilo.TextoSuave));
                if (doDia.Length == 0) rolagem.Add(Estilo.Texto_("Nada cadastrado.", 28, false, Estilo.TextoSuave));
                foreach (var r in doDia)
                {
                    var c = Estilo.CaixaCartao();
                    c.Add(Estilo.Texto_($"{r.Hora} · {r.nome}" + (r.tipo == "lanche" ? " (lanche)" : ""), 32, true));
                    if (!string.IsNullOrEmpty(r.descricao)) c.Add(Estilo.Texto_(r.descricao, 26, false, Estilo.TextoSuave));
                    c.RegisterCallback<ClickEvent>(_ => MostrarEditarRefeicao(r));
                    rolagem.Add(c);
                }
            }
        }

        /// <summary>Cria (item null) ou edita uma refeição. Marcando mais dias, cria cópias neles.</summary>
        void MostrarEditarRefeicao(Refeicao item)
        {
            bool nova = item == null;
            var t = NovaTela(false);
            t.Add(Estilo.Texto_(nova ? "Nova refeição" : "Editar refeição", 52, true));
            var rolagem = Estilo.Rolagem();
            t.Add(rolagem);

            var nome = Estilo.Campo("Nome", "Ex.: Almoço");
            var horario = Estilo.Campo("Horário (HH:MM)", "Ex.: 12:30");
            var descricao = Estilo.Campo("O que comer", "Ex.: 120 g de frango, 4 colheres de arroz, salada à vontade", true);
            var observacoes = Estilo.Campo("Substituições do plano (opcional)", "Ex.: frango pode ser trocado por peixe", true);
            string tipo = item?.tipo ?? "refeicao";
            var escTipo = Estilo.Escolha("Tipo", new[] { ("refeicao", "Refeição"), ("lanche", "Lanche") }, tipo, v => tipo = v);
            var dias = new HashSet<int>();
            if (!nova)
            {
                nome.value = item.nome;
                horario.value = item.Hora;
                descricao.value = item.descricao ?? "";
                observacoes.value = item.observacoes ?? "";
                dias.Add(item.dia_semana);
            }
            var escDias = EscolhaDias(dias);
            var msg = Estilo.Texto_("", 30, false, Estilo.Perigo);
            foreach (var e in new VisualElement[] { nome, horario, escTipo, escDias, descricao, observacoes, msg }) rolagem.Add(e);

            async Task Salvar()
            {
                if (ocupado) return;
                if (string.IsNullOrWhiteSpace(nome.value)) { msg.text = "Dê um nome para a refeição."; return; }
                if (!TimeSpan.TryParse(horario.value.Trim(), out var hora) || hora < TimeSpan.Zero || hora >= TimeSpan.FromDays(1))
                {
                    msg.text = "Informe o horário como 12:30."; return;
                }
                // Nenhum dia marcado = todos os dias.
                var escolhidos = dias.Count == 0 ? Enumerable.Range(0, 7).ToList() : dias.OrderBy(d => d).ToList();
                ocupado = true;
                msg.style.color = Estilo.TextoSuave;
                msg.text = "Salvando...";
                try
                {
                    string hhmm = $"{hora.Hours:00}:{hora.Minutes:00}:00";
                    for (int i = 0; i < escolhidos.Count; i++)
                    {
                        // Na edição, o primeiro dia marcado atualiza a própria refeição; os outros viram cópias.
                        bool atualiza = !nova && (escolhidos[i] == item.dia_semana || (i == 0 && !escolhidos.Contains(item.dia_semana)));
                        await api.SalvarRefeicao(new Refeicao
                        {
                            id = atualiza ? item.id : null,
                            dia_semana = escolhidos[i],
                            horario = hhmm,
                            nome = nome.value.Trim(),
                            tipo = tipo,
                            descricao = descricao.value.Trim(),
                            observacoes = observacoes.value.Trim(),
                        });
                    }
                    ocupado = false;
                    Aviso(escolhidos.Count > 1 ? $"Salva em {escolhidos.Count} dias." : "Refeição salva.", Estilo.Sucesso);
                    await MostrarPlano();
                }
                catch (Exception e) { msg.style.color = Estilo.Perigo; msg.text = e.Message; }
                finally { ocupado = false; }
            }

            async Task Remover()
            {
                if (ocupado) return;
                ocupado = true;
                try
                {
                    await api.RemoverRefeicao(item.id);
                    ocupado = false;
                    Aviso($"\"{item.nome}\" saiu do plano de {NomesDiasCompletos[item.dia_semana].ToLowerInvariant()}.", Estilo.Sucesso);
                    await MostrarPlano();
                }
                catch (Exception e) { msg.style.color = Estilo.Perigo; msg.text = e.Message; }
                finally { ocupado = false; }
            }

            rolagem.Add(Estilo.Botao("Salvar", () => _ = Salvar()));
            rolagem.Add(Estilo.Botao("Cancelar", () => _ = MostrarPlano(), Estilo.TextoSuave, true));
            if (!nova) rolagem.Add(Estilo.Botao("Remover deste dia", () => _ = Remover(), Estilo.Perigo, true));
        }
    }
}
