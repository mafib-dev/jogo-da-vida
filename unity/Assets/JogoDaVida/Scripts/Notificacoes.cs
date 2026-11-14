using System;
using JogoDaVida.Dados;
using UnityEngine;
#if UNITY_ANDROID
using Unity.Notifications.Android;
#endif

namespace JogoDaVida
{
    /// <summary>
    /// Sugestões do dia como notificação local (Banco de Ideias 3.9): uma por janela
    /// (manhã, almoço, tarde, noite), escolhida pela barra mais baixa no momento do agendamento.
    /// Tocar na notificação abre o app, onde a sugestão aparece com o botão "Feito!".
    /// Reagendadas toda vez que a tela principal carrega, para refletir as barras atuais.
    /// </summary>
    public static class Notificacoes
    {
        const string Canal = "sugestoes";
        static readonly (int hora, int minuto, string nome)[] Horarios =
        {
            (8, 30, "manha"), (12, 30, "almoco"), (15, 30, "tarde"), (19, 30, "noite"),
        };

#if UNITY_ANDROID
        static bool iniciado;
        static PermissionRequest pedido; // mantém o pedido de permissão (Android 13+) vivo

        static void Iniciar()
        {
            if (iniciado) return;
            iniciado = true;
            AndroidNotificationCenter.RegisterNotificationChannel(new AndroidNotificationChannel(
                Canal, "Sugestões do dia", "Pequenas ações ao longo do dia", Importance.Default));
            if (AndroidNotificationCenter.UserPermissionToPost != PermissionStatus.Allowed)
                pedido = new PermissionRequest();
        }
#endif

        public static void Agendar(Barras barras)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                Iniciar();
                AndroidNotificationCenter.CancelAllScheduledNotifications();
                var agoraBrasilia = DateTime.UtcNow.AddHours(-3);
                var hojeBrasilia = agoraBrasilia.Date;
                bool agua = barras == null || barras.hidratacao <= barras.movimento;

                for (int dia = 0; dia < 2; dia++)
                {
                    foreach (var (hora, minuto, nome) in Horarios)
                    {
                        var quandoBrasilia = hojeBrasilia.AddDays(dia).AddHours(hora).AddMinutes(minuto);
                        if (quandoBrasilia <= agoraBrasilia) continue;
                        string chave = $"jdv.sugestao.{quandoBrasilia:yyyy-MM-dd}.{nome}";
                        if (PlayerPrefs.GetInt(chave, 0) == 1) continue;

                        // O celular agenda no horário local dele; convertemos de Brasília para local.
                        var quandoLocal = DateTime.SpecifyKind(quandoBrasilia.AddHours(3), DateTimeKind.Utc).ToLocalTime();
                        string texto = agua ? "Beba um copo de água (200 ml)."
                            : nome == "almoco" ? "Caminhe 10 min depois de comer." : "Levante e alongue por 5 min.";
                        // Alterna para não repetir o mesmo tipo o dia todo (água pode repetir).
                        if (nome == "tarde") agua = !agua;

                        var n = new AndroidNotification("Sugestão do Jogo da Vida", texto, quandoLocal);
                        AndroidNotificationCenter.SendNotification(n, Canal);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Não consegui agendar as notificações: " + e.Message);
            }
#endif
        }
    }
}
