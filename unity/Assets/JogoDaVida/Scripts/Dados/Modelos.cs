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
        public int energia;
        public int alimentacao;
        public int meta_agua_ml;   // meta diária de água
        public int agua_hoje_ml;   // quanto já bebeu hoje
        public bool dormindo;        // dormindo pelo horário do perfil ou de verdade
        public bool dormindo_agora;  // disse "vou dormir" e ainda não acordou
        // Preenchidos só ao registrar
        public int pontos;
        public int moedas_ganhas;
        public int nivel;
        public bool subiu_de_nivel;

        /// <summary>A barra mais baixa decide o estado visual (Banco de Ideias 3.4).</summary>
        public int Menor => Math.Min(Math.Min(hidratacao, movimento), Math.Min(energia, alimentacao));
    }

    /// <summary>Uma refeição do plano alimentar (um dia da semana, um horário).</summary>
    [Serializable]
    public class Refeicao
    {
        public string id;
        public int dia_semana;   // 0 = domingo a 6 = sábado
        public string horario;   // "12:30:00"
        public string nome;      // café da manhã, almoço...
        public string tipo;      // refeicao | lanche
        public string descricao;
        public string observacoes;

        /// <summary>Minutos desde a meia-noite (para ordenar e achar a próxima).</summary>
        public int Minutos
        {
            get
            {
                var partes = (horario ?? "").Split(':');
                if (partes.Length < 2 || !int.TryParse(partes[0], out var h) || !int.TryParse(partes[1], out var m)) return 0;
                return h * 60 + m;
            }
        }

        public string Hora => (horario ?? "").Length >= 5 ? horario.Substring(0, 5) : horario;
    }

    /// <summary>Corpo para criar ou editar uma refeição do plano (sem id e sem user_id).</summary>
    [Serializable]
    class EnvioRefeicao
    {
        public int dia_semana;
        public string horario;
        public string nome;
        public string tipo;
        public string descricao;
        public string observacoes;
    }

    /// <summary>Uma marcação do dia: seguida, substituída ou perdida.</summary>
    [Serializable]
    public class RegistroRefeicao
    {
        public string refeicao_id; // vazio = fora do plano
        public string status;      // seguida | substituida | perdida
        public string o_que_comi;
        public int pontos;
    }

    [Serializable]
    class EnvioRegistroRefeicao
    {
        public string p_refeicao_id;
        public string p_status;
        public string p_o_que_comi;
    }

    [Serializable]
    public class ResultadoRefeicao
    {
        public int alimentacao;
        public int pontos;
        public int moedas_ganhas;
        public int nivel;
        public bool subiu_de_nivel;
    }

    [Serializable]
    class MetaAgua
    {
        public int meta_agua_ml;
    }

    /// <summary>Resposta de registrar_sono.</summary>
    [Serializable]
    public class ResultadoSono
    {
        public int energia;
        public float horas;   // 0 quando não deu para calcular
        public int pontos;
        public int moedas_ganhas;
        public int nivel;
        public bool subiu_de_nivel;
    }

    [Serializable]
    class AcaoSono
    {
        public string p_acao; // dormir | acordar
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
