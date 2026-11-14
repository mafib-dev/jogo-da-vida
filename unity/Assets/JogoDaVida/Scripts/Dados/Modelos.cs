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
        public int[] dias;              // dias fixos da diária (0 = domingo a 6 = sábado); vazio = todo dia
        public string criada_em;        // timestamptz

        static readonly string[] NomesDias = { "dom", "seg", "ter", "qua", "qui", "sex", "sáb" };

        bool TemDiasFixos => recorrencia == "diaria" && dias != null && dias.Length > 0 && dias.Length < 7;

        /// <summary>A tarefa é para este dia? Diária com dias fixos só nos dias marcados; o resto, sempre.</summary>
        public bool MarcadaPara(DateTime dia) => !TemDiasFixos || Array.IndexOf(dias, (int)dia.DayOfWeek) >= 0;

        /// <summary>Texto da recorrência: "avulsa", "semanal", "diária" ou os dias ("seg a sex", "ter, qui, sáb").</summary>
        public string TextoRecorrencia()
        {
            if (recorrencia == "semanal") return "semanal";
            if (recorrencia != "diaria") return "avulsa";
            return TemDiasFixos ? TextoDias(dias) : "diária";
        }

        public static string TextoDias(int[] dias)
        {
            if (dias == null || dias.Length == 0 || dias.Length == 7) return "todo dia";
            var ord = (int[])dias.Clone();
            Array.Sort(ord);
            bool seguidos = ord.Length >= 3;
            for (int i = 1; i < ord.Length && seguidos; i++) seguidos = ord[i] == ord[i - 1] + 1;
            if (seguidos) return $"{NomesDias[ord[0]]} a {NomesDias[ord[ord.Length - 1]]}";
            return string.Join(", ", Array.ConvertAll(ord, d => NomesDias[d]));
        }

        /// <summary>Dias de atraso de uma tarefa avulsa ainda não feita (0 = criada hoje).</summary>
        public int DiasAtraso(DateTime hoje)
        {
            if (recorrencia != "avulsa" || string.IsNullOrEmpty(criada_em)) return 0;
            if (!DateTimeOffset.TryParse(criada_em, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var criada)) return 0;
            var diaCriacao = criada.UtcDateTime.AddHours(-3).Date; // Brasília
            return Math.Max(0, (int)(hoje.Date - diaCriacao).TotalDays);
        }

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
    public class Barras
    {
        public int hidratacao;
        public int movimento;
        public bool dormindo;
        // Preenchidos só ao registrar
        public int pontos;
        public int moedas_ganhas;
        public int nivel;
        public bool subiu_de_nivel;

        /// <summary>A barra mais baixa decide o estado visual (Banco de Ideias 3.4).</summary>
        public int Menor => Math.Min(hidratacao, movimento);
    }

    [Serializable]
    public class Item
    {
        public string id;
        public string nome;
        public string categoria; // roupa | movel
        public int preco;
        public string descricao;
        public int ordem;
    }

    [Serializable]
    public class Compra
    {
        public string item_id;
        public bool equipado;
    }

    [Serializable]
    class RegistroBarra
    {
        public string p_tipo;
        public int p_quantidade;
        public string p_intensidade;
    }

    [Serializable]
    class IdItem
    {
        public string p_item_id;
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
        public int[] dias; // só diária; vazio = todo dia
    }

    /// <summary>Item do catálogo de tarefas padrão (sem pontos: só preenche o cadastro).</summary>
    [Serializable]
    public class TarefaPadrao
    {
        public string id;
        public string titulo;
        public string descricao;
        public string categoria;   // casa | saude | conhecimento | financas | outra
        public int minutos;
        public string intensidade;
        public string recorrencia;
        public int ordem;
    }

    /// <summary>Corpo para criar ou editar um item do catálogo (sem id e sem user_id).</summary>
    [Serializable]
    class EnvioTarefaPadrao
    {
        public string titulo;
        public string descricao;
        public string categoria;
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
