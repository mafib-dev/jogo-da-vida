using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace JogoDaVida.Dados
{
    public class ErroApi : Exception
    {
        public readonly long Status;
        public ErroApi(long status, string mensagem) : base(mensagem) { Status = status; }
    }

    /// <summary>
    /// Cliente mínimo do Supabase (Auth, REST, RPC e Edge Functions) com UnityWebRequest.
    /// Guarda a sessão no PlayerPrefs para o login durar entre aberturas do app.
    /// </summary>
    public class SupabaseClient
    {
        const string ChaveSessao = "jdv.sessao";

        readonly string url;
        readonly string chavePublica;
        public Sessao Sessao { get; private set; }
        public bool Logado => Sessao != null && !string.IsNullOrEmpty(Sessao.access_token);

        public SupabaseClient(string url, string chavePublica)
        {
            this.url = url.TrimEnd('/');
            this.chavePublica = chavePublica;
            var salva = PlayerPrefs.GetString(ChaveSessao, "");
            if (!string.IsNullOrEmpty(salva)) Sessao = JsonUtility.FromJson<Sessao>(salva);
        }

        // ------------------------------------------------------------------ Auth

        public async Task Entrar(string email, string senha)
        {
            var corpo = JsonUtility.ToJson(new LoginSenha { email = email, password = senha });
            var json = await Enviar("POST", "/auth/v1/token?grant_type=password", corpo, false);
            GuardarSessao(JsonUtility.FromJson<Sessao>(json));
        }

        /// <summary>Cria a conta. Devolve true se já entrou; false se precisa confirmar o e-mail.</summary>
        public async Task<bool> Cadastrar(string email, string senha)
        {
            var corpo = JsonUtility.ToJson(new LoginSenha { email = email, password = senha });
            var json = await Enviar("POST", "/auth/v1/signup", corpo, false);
            var sessao = JsonUtility.FromJson<Sessao>(json);
            if (sessao != null && !string.IsNullOrEmpty(sessao.access_token))
            {
                GuardarSessao(sessao);
                return true;
            }
            return false;
        }

        public void Sair()
        {
            Sessao = null;
            PlayerPrefs.DeleteKey(ChaveSessao);
            PlayerPrefs.Save();
        }

        async Task GarantirToken()
        {
            if (!Logado) throw new ErroApi(401, "Faça login de novo.");
            var agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Sessao.expires_at > 0 && Sessao.expires_at - agora > 60) return;
            var corpo = JsonUtility.ToJson(new TokenRefresh { refresh_token = Sessao.refresh_token });
            try
            {
                var json = await Enviar("POST", "/auth/v1/token?grant_type=refresh_token", corpo, false);
                GuardarSessao(JsonUtility.FromJson<Sessao>(json));
            }
            catch (ErroApi e) when (e.Status == 400 || e.Status == 401)
            {
                Sair();
                throw new ErroApi(401, "Sua sessão expirou. Faça login de novo.");
            }
        }

        void GuardarSessao(Sessao s)
        {
            Sessao = s;
            PlayerPrefs.SetString(ChaveSessao, JsonUtility.ToJson(s));
            PlayerPrefs.Save();
        }

        // ------------------------------------------------------------------ Dados

        public async Task<StatusJogador> Status()
        {
            var lista = await GetLista<StatusJogador>("/rest/v1/status_jogador?select=*");
            return lista.Length > 0 ? lista[0] : null;
        }

        public Task<Tarefa[]> TarefasAtivas() =>
            GetLista<Tarefa>("/rest/v1/tarefas?select=*&ativa=eq.true&order=criada_em.asc");

        public async Task<Tarefa> PontuarTarefa(NovaTarefa nova)
        {
            var json = await Autenticado("POST", "/functions/v1/pontuar-tarefa", JsonUtility.ToJson(nova));
            return JsonUtility.FromJson<RespostaPontuacao>(json).tarefa;
        }

        public async Task<ResultadoConclusao> ConcluirTarefa(string tarefaId)
        {
            var corpo = JsonUtility.ToJson(new IdTarefa { p_tarefa_id = tarefaId });
            var json = await Autenticado("POST", "/rest/v1/rpc/concluir_tarefa", corpo);
            return JsonUtility.FromJson<ResultadoConclusao>(json);
        }

        public Task ArquivarTarefa(string tarefaId) =>
            Autenticado("POST", "/rest/v1/rpc/arquivar_tarefa",
                JsonUtility.ToJson(new IdTarefa { p_tarefa_id = tarefaId }));

        public Task EnviarPerfil(EnvioPerfil perfil) =>
            Autenticado("POST", "/functions/v1/gerar-memoria", JsonUtility.ToJson(perfil));

        public async Task<Barras> StatusBarras()
        {
            var json = await Autenticado("POST", "/rest/v1/rpc/status_barras", "{}");
            return JsonUtility.FromJson<Barras>(json);
        }

        /// <summary>acao: "dormir" ou "acordar". Ao acordar, a barra de Energia enche pelas horas dormidas.</summary>
        public async Task<ResultadoSono> RegistrarSono(string acao)
        {
            var json = await Autenticado("POST", "/rest/v1/rpc/registrar_sono", JsonUtility.ToJson(new AcaoSono { p_acao = acao }));
            return JsonUtility.FromJson<ResultadoSono>(json);
        }

        // ------------------------------------------------- Lista de mercado

        public Task<ItemCompra[]> ListaDeCompras() =>
            GetLista<ItemCompra>("/rest/v1/lista_compras?select=*&comprado=eq.false&order=categoria.asc,criado_em.asc");

        public Task<ItemCompra[]> HistoricoDeCompras() =>
            GetLista<ItemCompra>("/rest/v1/lista_compras?select=*&comprado=eq.true&order=comprado_em.desc&limit=50");

        public Task AdicionarCompra(string item, string quantidade, string categoria) =>
            Autenticado("POST", "/rest/v1/lista_compras",
                JsonUtility.ToJson(new EnvioItemCompra { item = item, quantidade = quantidade, categoria = categoria }));

        public Task MarcarComprado(string id) =>
            Autenticado("PATCH", "/rest/v1/lista_compras?id=eq." + id,
                JsonUtility.ToJson(new CorpoComprado { comprado = true, comprado_em = DateTime.UtcNow.ToString("o") }));

        public Task RemoverCompra(string id) =>
            Autenticado("DELETE", "/rest/v1/lista_compras?id=eq." + id, null);

        // ------------------------------------------------- Agenda

        /// <summary>Eventos a partir de uma data ("2026-11-14"), mais os não realizados dos últimos 30 dias.</summary>
        public Task<Evento[]> Agenda(string desde) =>
            GetLista<Evento>("/rest/v1/eventos?select=*&data=gte." + desde + "&order=data.asc,hora.asc.nullsfirst&limit=200");

        public Task<Evento[]> EventosPassados(string ate) =>
            GetLista<Evento>("/rest/v1/eventos?select=*&data=lt." + ate + "&order=data.desc&limit=30");

        /// <summary>hora vazia = sem horário.</summary>
        public Task CriarEvento(string titulo, string descricao, string data, string hora) =>
            Autenticado("POST", "/rest/v1/eventos", string.IsNullOrEmpty(hora)
                ? JsonUtility.ToJson(new EnvioEvento { titulo = titulo, descricao = descricao, data = data })
                : JsonUtility.ToJson(new EnvioEventoComHora { titulo = titulo, descricao = descricao, data = data, hora = hora }));

        public Task MarcarEventoFeito(string id) =>
            Autenticado("PATCH", "/rest/v1/eventos?id=eq." + id,
                JsonUtility.ToJson(new CorpoEventoFeito { status = "feito", feito_em = DateTime.UtcNow.ToString("o") }));

        public Task AdiarEvento(string id, string novaData) =>
            Autenticado("PATCH", "/rest/v1/eventos?id=eq." + id, JsonUtility.ToJson(new CorpoAdiar { data = novaData }));

        public Task RemoverEvento(string id) =>
            Autenticado("DELETE", "/rest/v1/eventos?id=eq." + id, null);

        // ------------------------------------------------- Plano alimentar

        public Task<Refeicao[]> PlanoAlimentar() =>
            GetLista<Refeicao>("/rest/v1/refeicoes?select=*&order=dia_semana.asc,horario.asc");

        /// <summary>Marcações de hoje (data no fuso de Brasília, "2026-11-14").</summary>
        public Task<RegistroRefeicao[]> RefeicoesDoDia(string data) =>
            GetLista<RegistroRefeicao>("/rest/v1/refeicoes_registro?select=refeicao_id,status,o_que_comi,pontos&data=eq." + data + "&order=criado_em.asc");

        /// <summary>Cria (id vazio) ou atualiza uma refeição do plano.</summary>
        public Task SalvarRefeicao(Refeicao r)
        {
            var corpo = JsonUtility.ToJson(new EnvioRefeicao
            {
                dia_semana = r.dia_semana, horario = r.horario, nome = r.nome, tipo = r.tipo,
                descricao = r.descricao, observacoes = r.observacoes,
            });
            return string.IsNullOrEmpty(r.id)
                ? Autenticado("POST", "/rest/v1/refeicoes", corpo)
                : Autenticado("PATCH", "/rest/v1/refeicoes?id=eq." + r.id, corpo);
        }

        public Task RemoverRefeicao(string id) =>
            Autenticado("DELETE", "/rest/v1/refeicoes?id=eq." + id, null);

        /// <summary>refeicaoId vazio = comeu algo fora do plano (oQueComi obrigatório).</summary>
        public async Task<ResultadoRefeicao> RegistrarRefeicao(string refeicaoId, string status, string oQueComi = "")
        {
            var corpo = JsonUtility.ToJson(new EnvioRegistroRefeicao { p_refeicao_id = refeicaoId ?? "", p_status = status, p_o_que_comi = oQueComi ?? "" });
            // O JsonUtility escreve string nula como "", mas o banco espera null para "fora do plano".
            if (string.IsNullOrEmpty(refeicaoId)) corpo = corpo.Replace("\"p_refeicao_id\":\"\"", "\"p_refeicao_id\":null");
            var json = await Autenticado("POST", "/rest/v1/rpc/registrar_refeicao", corpo);
            return JsonUtility.FromJson<ResultadoRefeicao>(json);
        }

        public Task SalvarMetaAgua(int ml) =>
            Autenticado("PATCH", "/rest/v1/perfil?user_id=eq." + Sessao.user.id, JsonUtility.ToJson(new MetaAgua { meta_agua_ml = ml }));

        /// <summary>tipo: "hidratacao" (quantidade em ml) ou "movimento" (quantidade em minutos).</summary>
        public async Task<Barras> RegistrarBarra(string tipo, int quantidade, string intensidade = "moderada")
        {
            var corpo = JsonUtility.ToJson(new RegistroBarra { p_tipo = tipo, p_quantidade = quantidade, p_intensidade = intensidade });
            var json = await Autenticado("POST", "/rest/v1/rpc/registrar_barra", corpo);
            return JsonUtility.FromJson<Barras>(json);
        }

        public Task<Item[]> Itens() => GetLista<Item>("/rest/v1/itens?select=*&order=ordem.asc");

        public Task<Compra[]> Compras() => GetLista<Compra>("/rest/v1/compras?select=item_id,equipado");

        public Task ComprarItem(string itemId) =>
            Autenticado("POST", "/rest/v1/rpc/comprar_item", JsonUtility.ToJson(new IdItem { p_item_id = itemId }));

        // ------------------------------------------------- Catálogo de tarefas padrão

        public Task<TarefaPadrao[]> CatalogoTarefas() =>
            GetLista<TarefaPadrao>("/rest/v1/tarefas_padrao?select=*&order=ordem.asc,criada_em.asc");

        /// <summary>Cria (id vazio) ou atualiza um item do catálogo.</summary>
        public Task SalvarTarefaPadrao(TarefaPadrao t)
        {
            var corpo = JsonUtility.ToJson(new EnvioTarefaPadrao
            {
                titulo = t.titulo, descricao = t.descricao, categoria = t.categoria,
                minutos = t.minutos, intensidade = t.intensidade, recorrencia = t.recorrencia,
            });
            return string.IsNullOrEmpty(t.id)
                ? Autenticado("POST", "/rest/v1/tarefas_padrao", corpo)
                : Autenticado("PATCH", "/rest/v1/tarefas_padrao?id=eq." + t.id, corpo);
        }

        public Task RemoverTarefaPadrao(string id) =>
            Autenticado("DELETE", "/rest/v1/tarefas_padrao?id=eq." + id, null);

        /// <summary>Devolve ao catálogo as tarefas iniciais que foram removidas. Retorna quantas voltaram.</summary>
        public async Task<int> RestaurarTarefasPadrao()
        {
            var json = await Autenticado("POST", "/rest/v1/rpc/restaurar_tarefas_padrao", "{}");
            return int.TryParse(json.Trim(), out var n) ? n : 0;
        }

        // ------------------------------------------------------------------ HTTP

        async Task<T[]> GetLista<T>(string caminho)
        {
            var json = await Autenticado("GET", caminho, null);
            return JsonUtility.FromJson<Lista<T>>("{\"itens\":" + json + "}").itens ?? new T[0];
        }

        async Task<string> Autenticado(string metodo, string caminho, string corpo)
        {
            await GarantirToken();
            return await Enviar(metodo, caminho, corpo, true);
        }

        async Task<string> Enviar(string metodo, string caminho, string corpo, bool comToken)
        {
            using var req = new UnityWebRequest(url + caminho, metodo);
            req.downloadHandler = new DownloadHandlerBuffer();
            if (corpo != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(corpo));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.SetRequestHeader("apikey", chavePublica);
            if (comToken) req.SetRequestHeader("Authorization", "Bearer " + Sessao.access_token);
            req.timeout = 40;

            var op = req.SendWebRequest();
            var tcs = new TaskCompletionSource<bool>();
            op.completed += _ => tcs.TrySetResult(true);
            await tcs.Task;

            var texto = req.downloadHandler?.text ?? "";
            if (req.result == UnityWebRequest.Result.Success) return texto;
            if (req.result == UnityWebRequest.Result.ConnectionError)
                throw new ErroApi(0, "Sem conexão com o servidor.");
            throw new ErroApi(req.responseCode, MensagemDeErro(texto, req.responseCode));
        }

        static string MensagemDeErro(string json, long status)
        {
            try
            {
                var e = JsonUtility.FromJson<ErroSupabase>(json);
                foreach (var m in new[] { e.erro, e.message, e.msg, e.error_description, e.error })
                    if (!string.IsNullOrEmpty(m)) return Traduzir(m);
            }
            catch { /* corpo não é JSON */ }
            return $"Erro {status} no servidor.";
        }

        static string Traduzir(string m)
        {
            if (m.Contains("Invalid login credentials")) return "E-mail ou senha incorretos.";
            if (m.Contains("Email not confirmed")) return "Confirme seu e-mail antes de entrar (veja sua caixa de entrada).";
            if (m.Contains("User already registered")) return "Esse e-mail já tem conta. Use Entrar.";
            if (m.Contains("Password should be")) return "A senha precisa ter pelo menos 6 caracteres.";
            return m;
        }
    }
}
