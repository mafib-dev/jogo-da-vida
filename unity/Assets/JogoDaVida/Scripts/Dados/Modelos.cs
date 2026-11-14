using System;

namespace JogoDaVida.Dados
{
    // Classes no formato do JSON do Supabase. Os nomes dos campos seguem as
    // colunas do banco (snake_case) porque o JsonUtility usa o nome do campo.

    [Serializable]
    public class Tarefa
    {
        public string id;
        public string titulo;
        public string descricao;
        public string contexto;
        public int minutos;
        public string intensidade;   // leve | moderada | pesada
        public string recorrencia;   // avulsa | diaria | semanal
        public float fator_pessoal;
        public int pontos;
        public string justificativa;
        public string pontuada_por;  // llm | formula
        public bool ativa;
        public int streak_atual;
        public int streak_recorde;
        public string ultima_conclusao; // "2026-11-13" ou vazio

        /// <summary>Recorrente que já foi feita no período atual (hoje ou nesta semana).</summary>
        public bool FeitaNoPeriodo(DateTime hoje)
        {
            if (string.IsNullOrEmpty(ultima_conclusao)) return false;
            if (!DateTime.TryParse(ultima_conclusao, out var ultima)) return false;
            if (recorrencia == "diaria") return ultima.Date == hoje.Date;
            if (recorrencia == "semanal") return InicioDaSemana(ultima) == InicioDaSemana(hoje);
            return false;
        }

        static DateTime InicioDaSemana(DateTime d)
        {
            int desdeSegunda = ((int)d.DayOfWeek + 6) % 7; // semana começa na segunda (igual ao Postgres)
            return d.Date.AddDays(-desdeSegunda);
        }
    }

    [Serializable]
    public class StatusJogador
    {
        public string user_id;
        public string nome;
        public int xp_total;
        public int moedas;
        public int nivel;
        public int xp_no_nivel;
        public int xp_proximo_nivel;
        public bool perfil_completo;
    }

    [Serializable]
    public class ResultadoConclusao
    {
        public int pontos;
        public int pontos_base;
        public int bonus_streak;
        public int streak;
        public int moedas_ganhas;
        public int xp_total;
        public int nivel;
        public bool subiu_de_nivel;
    }

    [Serializable]
    public class NovaTarefa
    {
        public string tarefa_id; // vazio = nova tarefa
        public string titulo;
        public string descricao;
        public string contexto;
        public int minutos;
        public string intensidade;
        public string recorrencia;
    }

    [Serializable]
    public class RespostaPontuacao
    {
        public Tarefa tarefa;
        public string erro;
    }

    [Serializable]
    public class RespostasPerfil
    {
        public string rotina;
        public string moradia;
        public string atividade_fisica;
        public string sono;
        public string alimentacao;
        public string tarefas_dificeis;
        public string objetivos;
        public string saude;
    }

    [Serializable]
    public class EnvioPerfil
    {
        public string nome;
        public RespostasPerfil respostas;
    }

    [Serializable]
    public class Sessao
    {
        public string access_token;
        public string refresh_token;
        public long expires_at; // segundos desde 1970
        public Usuario user;
    }

    [Serializable]
    public class Usuario
    {
        public string id;
        public string email;
    }

    [Serializable]
    public class ErroSupabase
    {
        public string erro;            // Edge Functions
        public string message;         // PostgREST
        public string msg;             // Auth
        public string error_description;
        public string error;
    }

    [Serializable]
    class Lista<T>
    {
        public T[] itens;
    }

    [Serializable]
    class LoginSenha
    {
        public string email;
        public string password;
    }

    [Serializable]
    class TokenRefresh
    {
        public string refresh_token;
    }

    [Serializable]
    class IdTarefa
    {
        public string p_tarefa_id;
    }
}
