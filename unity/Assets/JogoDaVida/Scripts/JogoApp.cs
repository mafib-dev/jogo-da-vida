using System;
using System.Linq;
using System.Threading.Tasks;
using JogoDaVida.Cena;
using JogoDaVida.Dados;
using JogoDaVida.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace JogoDaVida
{
    /// <summary>
    /// Ponto de entrada do app. Sobe sozinho ao dar Play em qualquer cena
    /// (não precisa arrastar nada no editor) e monta cena 3D + interface em código.
    /// </summary>
    public class JogoApp : MonoBehaviour
    {
        const string ChavePerfilPulado = "jdv.perfil_pulado";

        SupabaseClient api;
        BonecoProvisorio boneco;
        VisualElement raiz, conteudo, sobreposicao;
        StatusJogador status;
        Tarefa[] tarefas = new Tarefa[0];
        bool ocupado;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Iniciar()
        {
            if (FindAnyObjectByType<JogoApp>() != null) return;
            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.SystemSetting;

            // Desliga câmeras que vieram na cena de exemplo para não sobrepor a nossa.
            foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None)) cam.gameObject.SetActive(false);

            var go = new GameObject("Jogo da Vida");
            DontDestroyOnLoad(go);
            go.SetActive(false);
            var doc = go.AddComponent<UIDocument>();
            var painel = ScriptableObject.CreateInstance<PanelSettings>();
            painel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            painel.referenceResolution = new Vector2Int(1080, 1920);
            painel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            painel.match = 0f; // largura manda: fica igual em qualquer celular em pé
            painel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("JogoTema");
            painel.clearColor = false;
            doc.panelSettings = painel;
            go.AddComponent<JogoApp>();
            go.SetActive(true);
        }

        void OnEnable()
        {
            api = new SupabaseClient(Config.SupabaseUrl, Config.SupabaseChavePublica);
            boneco = BonecoProvisorio.Criar();

            raiz = GetComponent<UIDocument>().rootVisualElement;
            raiz.style.flexGrow = 1;

            conteudo = new VisualElement();
            conteudo.style.flexGrow = 1;
            raiz.Add(conteudo);

            sobreposicao = new VisualElement();
            sobreposicao.style.position = Position.Absolute;
            sobreposicao.style.left = 0; sobreposicao.style.right = 0;
            sobreposicao.style.top = 0; sobreposicao.style.bottom = 0;
            sobreposicao.pickingMode = PickingMode.Ignore;
            raiz.Add(sobreposicao);

            if (api.Logado) _ = AbrirInicio();
            else MostrarLogin();
        }

        // =============================================================== Navegação

        VisualElement NovaTela(bool comCena3D)
        {
            conteudo.Clear();
            var tela = new VisualElement();
            tela.style.flexGrow = 1;
            if (comCena3D)
            {
                // Faixa transparente em cima para o boneco aparecer
                var espaco = new VisualElement();
                espaco.style.height = Length.Percent(BonecoProvisorio.AlturaDaCena * 100);
                espaco.pickingMode = PickingMode.Ignore;
                tela.Add(espaco);
            }
            var corpo = new VisualElement();
            corpo.style.flexGrow = 1;
            corpo.style.backgroundColor = Estilo.Fundo;
            corpo.style.Preenchimento(24, 36);
            tela.Add(corpo);
            conteudo.Add(tela);
            return corpo;
        }

        async Task AbrirInicio()
        {
            try
            {
                status = await api.Status();
                bool pulou = PlayerPrefs.GetInt(ChavePerfilPulado, 0) == 1;
                if (status != null && !status.perfil_completo && !pulou) MostrarPerfil();
                else await MostrarPrincipal();
            }
            catch (ErroApi e) when (e.Status == 401)
            {
                MostrarLogin(e.Message);
            }
            catch (Exception e)
            {
                MostrarErroFatal(e.Message);
            }
        }

        void MostrarErroFatal(string msg)
        {
            var t = NovaTela(false);
            t.style.justifyContent = Justify.Center;
            t.Add(Estilo.Texto_("Não consegui falar com o servidor.", 40, true));
            t.Add(Estilo.Texto_(msg, 30, false, Estilo.TextoSuave));
            t.Add(Estilo.Botao("Tentar de novo", () => _ = AbrirInicio()));
        }

        // =============================================================== Login

        void MostrarLogin(string aviso = null)
        {
            var t = NovaTela(false);
            t.style.justifyContent = Justify.Center;
            t.Add(Estilo.Texto_("Jogo da Vida", 72, true));
            t.Add(Estilo.Texto_("Faça tarefas, ganhe pontos, suba de nível.", 32, false, Estilo.TextoSuave));
            var email = Estilo.Campo("E-mail");
            email.keyboardType = TouchScreenKeyboardType.EmailAddress;
            var senha = Estilo.Campo("Senha");
            senha.isPasswordField = true;
            var msg = Estilo.Texto_(aviso ?? "", 30, false, Estilo.Perigo);
            t.Add(email); t.Add(senha); t.Add(msg);

            async Task Executar(bool cadastro)
            {
                if (ocupado) return;
                ocupado = true;
                msg.style.color = Estilo.TextoSuave;
                msg.text = "Aguarde...";
                try
                {
                    if (cadastro)
                    {
                        bool entrou = await api.Cadastrar(email.value.Trim(), senha.value);
                        if (!entrou)
                        {
                            msg.style.color = Estilo.Sucesso;
                            msg.text = "Conta criada! Confirme pelo link que chegou no seu e-mail e depois toque em Entrar.";
                            return;
                        }
                    }
                    else await api.Entrar(email.value.Trim(), senha.value);
                    ocupado = false;
                    await AbrirInicio();
                }
                catch (Exception e)
                {
                    msg.style.color = Estilo.Perigo;
                    msg.text = e.Message;
                }
                finally { ocupado = false; }
            }

            t.Add(Estilo.Botao("Entrar", () => _ = Executar(false)));
            t.Add(Estilo.Botao("Criar conta", () => _ = Executar(true), contorno: true));
        }

        // =============================================================== Perfil inicial

        static readonly (string chave, string pergunta)[] Perguntas =
        {
            ("rotina", "Como é sua rotina de trabalho ou estudo? (horários, presencial ou remoto)"),
            ("moradia", "Mora sozinho ou com alguém? Quem divide as tarefas da casa?"),
            ("atividade_fisica", "Como está sua atividade física hoje? (nenhuma, às vezes, regular)"),
            ("sono", "A que horas costuma dormir e acordar?"),
            ("alimentacao", "Como é sua alimentação e quanto de água costuma beber?"),
            ("tarefas_dificeis", "Quais tarefas são mais difíceis ou chatas para você?"),
            ("objetivos", "O que você quer melhorar na sua vida agora?"),
            ("saude", "Tem alguma limitação de saúde ou lesão que a gente deve considerar?"),
        };

        void MostrarPerfil()
        {
            var t = NovaTela(false);
            t.Add(Estilo.Texto_("Conta um pouco de você", 52, true));
            t.Add(Estilo.Texto_("Isso ajuda a pontuar as tarefas pelo seu esforço real. Pode responder do seu jeito.", 30, false, Estilo.TextoSuave));
            var rolagem = Estilo.Rolagem();
            t.Add(rolagem);
            var nome = Estilo.Campo("Como quer ser chamado?");
            rolagem.Add(nome);
            var campos = Perguntas.Select(p => (p.chave, campo: Estilo.Campo(p.pergunta, "", true))).ToArray();
            foreach (var c in campos) rolagem.Add(c.campo);
            var msg = Estilo.Texto_("", 30, false, Estilo.Perigo);
            rolagem.Add(msg);

            async Task Salvar()
            {
                if (ocupado) return;
                ocupado = true;
                msg.style.color = Estilo.TextoSuave;
                msg.text = "Montando seu perfil...";
                try
                {
                    var r = new RespostasPerfil();
                    foreach (var (chave, campo) in campos)
                        typeof(RespostasPerfil).GetField(chave).SetValue(r, campo.value.Trim());
                    await api.EnviarPerfil(new EnvioPerfil { nome = nome.value.Trim(), respostas = r });
                    ocupado = false;
                    await AbrirInicio();
                }
                catch (Exception e)
                {
                    msg.style.color = Estilo.Perigo;
                    msg.text = e.Message;
                }
                finally { ocupado = false; }
            }

            rolagem.Add(Estilo.Botao("Salvar", () => _ = Salvar()));
            rolagem.Add(Estilo.Botao("Responder depois", () =>
            {
                PlayerPrefs.SetInt(ChavePerfilPulado, 1);
                _ = MostrarPrincipal();
            }, contorno: true));
        }

        // =============================================================== Tela principal

        static DateTime HojeBrasilia() => DateTime.UtcNow.AddHours(-3).Date;

        async Task MostrarPrincipal()
        {
            var t = NovaTela(true);

            // Cabeçalho: nível, barra de XP e moedas
            var cab = new VisualElement();
            cab.style.flexDirection = FlexDirection.Row;
            cab.style.alignItems = Align.Center;
            var nivel = Estilo.Texto_("Nível -", 44, true);
            nivel.style.flexGrow = 1;
            var moedas = Estilo.Texto_("- moedas", 36, true, Estilo.Moeda);
            cab.Add(nivel); cab.Add(moedas);
            t.Add(cab);

            var trilho = new VisualElement();
            trilho.style.height = 30;
            trilho.style.backgroundColor = Estilo.Borda;
            trilho.style.Raio(15);
            trilho.style.overflow = Overflow.Hidden;
            var barra = new VisualElement();
            barra.style.height = Length.Percent(100);
            barra.style.backgroundColor = Estilo.Primaria;
            barra.style.width = Length.Percent(0);
            trilho.Add(barra);
            t.Add(trilho);
            var xp = Estilo.Texto_("", 28, false, Estilo.TextoSuave);
            t.Add(xp);

            var lista = Estilo.Rolagem();
            t.Add(lista);

            var rodape = new VisualElement();
            rodape.style.flexDirection = FlexDirection.Row;
            var nova = Estilo.Botao("+ Nova tarefa", MostrarNovaTarefa);
            nova.style.flexGrow = 1;
            var sair = Estilo.Botao("Sair", () => { api.Sair(); MostrarLogin(); }, Estilo.TextoSuave, true);
            sair.style.width = 180;
            sair.style.marginLeft = 16;
            rodape.Add(nova); rodape.Add(sair);
            t.Add(rodape);

            void Desenhar()
            {
                if (status != null)
                {
                    nivel.text = $"Nível {status.nivel}";
                    moedas.text = $"{status.moedas} moedas";
                    float p = status.xp_proximo_nivel > 0 ? (float)status.xp_no_nivel / status.xp_proximo_nivel : 0;
                    barra.style.width = Length.Percent(Mathf.Clamp01(p) * 100);
                    xp.text = $"{status.xp_no_nivel} / {status.xp_proximo_nivel} XP para o nível {status.nivel + 1}";
                }

                lista.Clear();
                var hoje = HojeBrasilia();
                var ordenadas = tarefas.OrderBy(x => x.FeitaNoPeriodo(hoje)).ThenBy(x => x.recorrencia == "avulsa" ? 1 : 0).ToArray();
                if (ordenadas.Length == 0)
                {
                    lista.Add(Estilo.Texto_("Nenhuma tarefa ainda. Toque em \"+ Nova tarefa\" para cadastrar a primeira, como lavar a louça ou tirar o lixo.", 32, false, Estilo.TextoSuave));
                }
                foreach (var tarefa in ordenadas) lista.Add(CartaoTarefa(tarefa, hoje));

                int feitasHoje = tarefas.Count(x => x.ultima_conclusao == hoje.ToString("yyyy-MM-dd"));
                boneco.DefinirEstado(feitasHoje >= 3 ? EstadoVisual.Bem : feitasHoje >= 1 ? EstadoVisual.Normal : EstadoVisual.Sinais);
            }

            VisualElement CartaoTarefa(Tarefa tarefa, DateTime hoje)
            {
                bool feita = tarefa.FeitaNoPeriodo(hoje);
                var c = Estilo.CaixaCartao();
                c.style.flexDirection = FlexDirection.Row;
                c.style.alignItems = Align.Center;
                if (feita) c.style.opacity = 0.55f;

                var info = new VisualElement();
                info.style.flexGrow = 1;
                info.style.flexShrink = 1;
                info.Add(Estilo.Texto_(tarefa.titulo, 36, true));
                string rec = tarefa.recorrencia == "diaria" ? "diária" : tarefa.recorrencia == "semanal" ? "semanal" : "avulsa";
                string streak = tarefa.streak_atual > 1 ? $" · sequência {tarefa.streak_atual}" : "";
                info.Add(Estilo.Texto_($"{tarefa.pontos} pontos · {tarefa.minutos} min · {rec}{streak}", 28, false, Estilo.TextoSuave));
                info.RegisterCallback<ClickEvent>(_ => MostrarDetalhe(tarefa));
                c.Add(info);

                var botao = Estilo.Botao(feita ? "Feita" : "Feito!", () => _ = Concluir(tarefa), feita ? Estilo.TextoSuave : Estilo.Sucesso);
                botao.style.width = 200;
                botao.style.marginLeft = 16;
                botao.SetEnabled(!feita);
                c.Add(botao);
                return c;
            }

            async Task Concluir(Tarefa tarefa)
            {
                if (ocupado) return;
                ocupado = true;
                try
                {
                    var r = await api.ConcluirTarefa(tarefa.id);
                    boneco.Comemorar();
                    string bonus = r.bonus_streak > 0 ? $" (+{r.bonus_streak} de sequência)" : "";
                    string moedasTxt = r.moedas_ganhas > 0 ? $"  ·  +{r.moedas_ganhas} moedas" : "";
                    Aviso($"+{r.pontos} pontos{bonus}{moedasTxt}", Estilo.Sucesso);
                    await Recarregar();
                    if (r.subiu_de_nivel) MostrarSubiuDeNivel(r.nivel);
                }
                catch (Exception e) { Aviso(e.Message, Estilo.Perigo); }
                finally { ocupado = false; }
            }

            async Task Recarregar()
            {
                var s = api.Status();
                var l = api.TarefasAtivas();
                await Task.WhenAll(s, l);
                status = s.Result;
                tarefas = l.Result;
                Desenhar();
            }

            Desenhar();
            try { await Recarregar(); }
            catch (ErroApi e) when (e.Status == 401) { MostrarLogin(e.Message); }
            catch (Exception e) { Aviso(e.Message, Estilo.Perigo); }
        }

        // =============================================================== Nova tarefa

        void MostrarNovaTarefa()
        {
            var t = NovaTela(false);
            t.Add(Estilo.Texto_("Nova tarefa", 52, true));
            var rolagem = Estilo.Rolagem();
            t.Add(rolagem);

            var titulo = Estilo.Campo("O que você fez ou vai fazer?", "Ex.: Lavar a louça");
            var minutos = Estilo.Campo("Quanto tempo leva (minutos)?", "Ex.: 20");
            minutos.keyboardType = TouchScreenKeyboardType.NumberPad;
            string intensidade = "moderada", recorrencia = "diaria";
            var esc1 = Estilo.Escolha("Intensidade", new[] { ("leve", "Leve"), ("moderada", "Moderada"), ("pesada", "Pesada") },
                intensidade, v => intensidade = v);
            var esc2 = Estilo.Escolha("Repete?", new[] { ("avulsa", "Só uma vez"), ("diaria", "Todo dia"), ("semanal", "Toda semana") },
                recorrencia, v => recorrencia = v);
            var descricao = Estilo.Campo("Detalhes (opcional)", "", true);
            var contexto = Estilo.Campo("Algo fora do comum? (opcional)", "Ex.: o banheiro estava muito sujo", true);
            var msg = Estilo.Texto_("", 30, false, Estilo.Perigo);
            foreach (var e in new VisualElement[] { titulo, minutos, esc1, esc2, descricao, contexto, msg }) rolagem.Add(e);

            async Task Salvar()
            {
                if (ocupado) return;
                if (string.IsNullOrWhiteSpace(titulo.value)) { msg.text = "Dê um nome para a tarefa."; return; }
                if (!int.TryParse(minutos.value.Trim(), out int min) || min < 1 || min > 600)
                {
                    msg.text = "Informe os minutos (de 1 a 600)."; return;
                }
                ocupado = true;
                msg.style.color = Estilo.TextoSuave;
                msg.text = "Calculando os pontos...";
                try
                {
                    var tarefa = await api.PontuarTarefa(new NovaTarefa
                    {
                        titulo = titulo.value.Trim(),
                        minutos = min,
                        intensidade = intensidade,
                        recorrencia = recorrencia,
                        descricao = descricao.value.Trim(),
                        contexto = contexto.value.Trim(),
                    });
                    ocupado = false;
                    MostrarDetalhe(tarefa, recemCriada: true);
                }
                catch (Exception e)
                {
                    msg.style.color = Estilo.Perigo;
                    msg.text = e.Message;
                }
                finally { ocupado = false; }
            }

            rolagem.Add(Estilo.Botao("Calcular pontos", () => _ = Salvar()));
            rolagem.Add(Estilo.Botao("Cancelar", () => _ = MostrarPrincipal(), Estilo.TextoSuave, true));
        }

        // =============================================================== Detalhe da tarefa

        void MostrarDetalhe(Tarefa tarefa, bool recemCriada = false)
        {
            var t = NovaTela(false);
            var rolagem = Estilo.Rolagem();
            t.Add(rolagem);
            if (recemCriada) rolagem.Add(Estilo.Texto_("Tarefa cadastrada!", 34, true, Estilo.Sucesso));
            rolagem.Add(Estilo.Texto_(tarefa.titulo, 52, true));

            var cartao = Estilo.CaixaCartao();
            cartao.Add(Estilo.Texto_($"{tarefa.pontos} pontos", 64, true, Estilo.Primaria));
            string origem = tarefa.pontuada_por == "llm" ? $"fator pessoal ×{tarefa.fator_pessoal:0.00}" : "fórmula base";
            cartao.Add(Estilo.Texto_($"{tarefa.minutos} min · {tarefa.intensidade} · {origem}", 28, false, Estilo.TextoSuave));
            if (!string.IsNullOrEmpty(tarefa.justificativa))
                cartao.Add(Estilo.Texto_(tarefa.justificativa, 32));
            if (tarefa.streak_recorde > 0)
                cartao.Add(Estilo.Texto_($"Sequência atual: {tarefa.streak_atual} · recorde: {tarefa.streak_recorde}", 28, false, Estilo.TextoSuave));
            rolagem.Add(cartao);

            rolagem.Add(Estilo.Texto_("Achou a pontuação estranha? Conte mais e eu recalculo. O número final é do jogo, não dá para editar.", 28, false, Estilo.TextoSuave));
            var contexto = Estilo.Campo("Mais contexto", "Ex.: levo 40 min porque cozinho para 4 pessoas", true);
            var msg = Estilo.Texto_("", 30, false, Estilo.Perigo);
            rolagem.Add(contexto);
            rolagem.Add(msg);

            async Task Recalcular()
            {
                if (ocupado || string.IsNullOrWhiteSpace(contexto.value)) return;
                ocupado = true;
                msg.style.color = Estilo.TextoSuave;
                msg.text = "Recalculando...";
                try
                {
                    var nova = await api.PontuarTarefa(new NovaTarefa { tarefa_id = tarefa.id, contexto = contexto.value.Trim() });
                    ocupado = false;
                    MostrarDetalhe(nova);
                }
                catch (Exception e) { msg.style.color = Estilo.Perigo; msg.text = e.Message; }
                finally { ocupado = false; }
            }

            async Task Arquivar()
            {
                if (ocupado) return;
                ocupado = true;
                try { await api.ArquivarTarefa(tarefa.id); ocupado = false; await MostrarPrincipal(); }
                catch (Exception e) { msg.style.color = Estilo.Perigo; msg.text = e.Message; }
                finally { ocupado = false; }
            }

            rolagem.Add(Estilo.Botao("Recalcular com esse contexto", () => _ = Recalcular(), contorno: true));
            rolagem.Add(Estilo.Botao("Voltar", () => _ = MostrarPrincipal()));
            rolagem.Add(Estilo.Botao("Arquivar tarefa", () => _ = Arquivar(), Estilo.Perigo, true));
        }

        // =============================================================== Avisos

        void Aviso(string texto, Color cor)
        {
            var a = new Label(texto);
            a.style.position = Position.Absolute;
            a.style.left = 36; a.style.right = 36;
            a.style.top = Length.Percent(BonecoProvisorio.AlturaDaCena * 100 - 9);
            a.style.backgroundColor = cor;
            a.style.color = Color.white;
            a.style.fontSize = 34;
            a.style.unityFontStyleAndWeight = FontStyle.Bold;
            a.style.unityTextAlign = TextAnchor.MiddleCenter;
            a.style.whiteSpace = WhiteSpace.Normal;
            a.style.Raio(20);
            a.style.Preenchimento(22, 24);
            a.pickingMode = PickingMode.Ignore;
            sobreposicao.Add(a);
            a.schedule.Execute(() => a.RemoveFromHierarchy()).StartingIn(2800);
        }

        void MostrarSubiuDeNivel(int nivel)
        {
            boneco.SubiuDeNivel();
            var fundo = new VisualElement();
            fundo.style.position = Position.Absolute;
            fundo.style.left = 0; fundo.style.right = 0; fundo.style.top = 0; fundo.style.bottom = 0;
            fundo.style.backgroundColor = new Color(0, 0, 0, 0.45f);
            fundo.style.justifyContent = Justify.Center;
            fundo.style.Preenchimento(0, 60);

            var c = Estilo.CaixaCartao();
            c.style.alignItems = Align.Center;
            c.Add(Estilo.Texto_("Subiu de nível!", 56, true, Estilo.Moeda));
            c.Add(Estilo.Texto_($"Agora você está no nível {nivel}.", 36));
            var ok = Estilo.Botao("Boa!", () => fundo.RemoveFromHierarchy());
            ok.style.width = 300;
            c.Add(ok);
            fundo.Add(c);
            sobreposicao.Add(fundo);
        }
    }
}
