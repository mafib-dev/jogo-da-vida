# Jogo da Vida

App Android (Unity) que transforma tarefas do dia a dia em pontos, XP, nível e moedas.
Backend no Supabase; a pontuação usa o Qwen por uma Edge Function.

## Estrutura

```
supabase/
  migrations/      esquema do banco (perfil, tarefas, conclusoes, nível, concluir_tarefa)
  functions/
    pontuar-tarefa/  cadastra ou recalcula uma tarefa; o Qwen define o fator pessoal
    gerar-memoria/   transforma as 8 respostas do perfil inicial na "memória" do jogador
    _shared/         fórmula de pontuação e chamada ao Qwen
unity/             app Android em Unity (veja unity/LEIA-ME.md)
```

## Como o app conversa com o backend

O app faz login no Supabase Auth e usa o token do usuário em tudo:

| Ação | Chamada |
|---|---|
| Responder o perfil inicial | `POST /functions/v1/gerar-memoria` `{ nome, respostas: { rotina, moradia, atividade_fisica, sono, alimentacao, tarefas_dificeis, objetivos, saude } }` |
| Cadastrar tarefa | `POST /functions/v1/pontuar-tarefa` `{ titulo, descricao?, contexto?, minutos, intensidade, recorrencia?, dias? }` |
| Dar mais contexto e recalcular | `POST /functions/v1/pontuar-tarefa` `{ tarefa_id, contexto }` |
| Marcar como feita | `POST /rest/v1/rpc/concluir_tarefa` `{ p_tarefa_id }` → pontos, bônus de streak, moedas, nível, `subiu_de_nivel` |
| Arquivar tarefa | `POST /rest/v1/rpc/arquivar_tarefa` `{ p_tarefa_id }` |
| Ler tarefas / histórico | `GET /rest/v1/tarefas`, `GET /rest/v1/conclusoes` |
| Dormir e acordar (barra de Energia) | `POST /rest/v1/rpc/registrar_sono` `{ p_acao: "dormir" \| "acordar" }` → energia, horas dormidas, pontos |
| Plano alimentar | `GET/POST /rest/v1/refeicoes`, `PATCH/DELETE /rest/v1/refeicoes?id=eq.<id>` (o jogador edita o próprio plano; não tem pontos) |
| Marcar refeição | `POST /rest/v1/rpc/registrar_refeicao` `{ p_refeicao_id (null = fora do plano), p_status: "seguida" \| "substituida" \| "perdida", p_o_que_comi? }` |
| Meta de água | `PATCH /rest/v1/perfil?user_id=eq.<id>` `{ meta_agua_ml }` |
| Ler XP, nível, moedas | `GET /rest/v1/status_jogador` |
| Catálogo de tarefas padrão | `GET/POST /rest/v1/tarefas_padrao`, `PATCH/DELETE /rest/v1/tarefas_padrao?id=eq.<id>` (o jogador edita o próprio catálogo; não tem pontos) |
| Restaurar as tarefas iniciais do catálogo | `POST /rest/v1/rpc/restaurar_tarefas_padrao` → quantas voltaram |

O app só lê as tabelas (exceto o catálogo `tarefas_padrao`, que o jogador edita); pontos, XP e moedas só são gravados pelo servidor
(o jogador não mexe na pontuação).

## Regras provisórias (ajustar com o uso)

- Pontos = minutos × intensidade (leve 0,5 · moderada 1 · pesada 1,5) × fator pessoal (0,75 a 1,5), entre 3 e 150.
- Sem LLM (chave ausente ou erro), o fator é 1 e a tarefa fica marcada como `pontuada_por = 'formula'`.
- Nível: 100, 120, 145, 175, 210... XP por nível.
- Moedas: 1 a cada 10 pontos de XP.
- Streak: +5% por dia (ou semana) seguido além do primeiro, até +25%.
- Tarefa diária só pode ser concluída uma vez por dia; semanal, uma vez por semana. Fuso: America/Sao_Paulo.
- Diária com dias fixos (`dias`, 0 = domingo a 6 = sábado), como academia de seg a sex: a sequência só conta os dias marcados. Fazer num dia de folga vale os pontos e não quebra a sequência.
- Energia: esvazia em ~16 h acordado; ao acordar, 7,5 h de sono enchem 100%. Enquanto o jogador diz que está dormindo, nenhuma barra desce. "Vou dormir" e "Acordei" dão 2 pontos cada, uma vez por noite/dia.
- Alimentação: refeição enche 35%, lanche 15%; esvazia em ~6 h acordado. Seguida dá 3 pontos, substituída 2, perdida 0 (sem punição).
- Água enche pela meta diária (`meta_agua_ml`, padrão 2000 ml): 200 ml ≈ 12% com a meta padrão.
- Tarefa avulsa não feita continua na lista e mostra "atrasada há N dias" (contado a partir de `criada_em`).

## Configuração do Qwen

Segredos das Edge Functions (Supabase → Edge Functions → Secrets):

- `LLM_API_KEY`: chave do Alibaba Cloud Model Studio (ou do OpenRouter)
- `LLM_BASE_URL` (opcional): padrão `https://dashscope-intl.aliyuncs.com/compatible-mode/v1`; OpenRouter: `https://openrouter.ai/api/v1`
- `LLM_MODEL` (opcional): padrão `qwen-plus`; no OpenRouter, por exemplo `qwen/qwen-plus`
