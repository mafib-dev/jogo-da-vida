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
