// Edge Function: cadastra (ou recalcula) uma tarefa e define a pontuação.
//
// POST /functions/v1/pontuar-tarefa
// Authorization: Bearer <token do usuário>
// {
//   "titulo": "Lavar a louça",
//   "descricao": "louça do jantar",          (opcional)
//   "contexto": "tinha panela de feijoada",   (opcional)
//   "minutos": 20,
//   "intensidade": "leve" | "moderada" | "pesada",
//   "recorrencia": "avulsa" | "diaria" | "semanal",   (opcional, padrão avulsa)
//   "tarefa_id": "uuid"   (opcional: recalcula uma tarefa existente com mais contexto)
// }
//
// A LLM só escolhe o fator pessoal (0,75 a 1,5); o número final sai da fórmula.
// Se a LLM (Qwen) falhar, usa fator 1 (fórmula base) e marca pontuada_por = 'formula'.

import {
  clienteAdmin,
  corsHeaders,
  json,
  pedirJson,
  usuarioDaRequisicao,
} from "../_shared/comum.ts";
import {
  calcularPontos,
  FATOR_MAX,
  FATOR_MIN,
  type Intensidade,
  limitarFator,
  PONTOS_POR_MINUTO,
} from "../_shared/pontuacao.ts";

const INTENSIDADES = ["leve", "moderada", "pesada"] as const;
const RECORRENCIAS = ["avulsa", "diaria", "semanal"] as const;

const SYSTEM = `Você é o juiz de pontuação do "Jogo da Vida", um jogo em que uma pessoa ganha pontos por tarefas reais do dia a dia.

A pontuação segue a fórmula: pontos = minutos × intensidade × fator pessoal.
Intensidade: leve = ${PONTOS_POR_MINUTO.leve}/min, moderada = ${PONTOS_POR_MINUTO.moderada}/min, pesada = ${PONTOS_POR_MINUTO.pesada}/min.
Os minutos e a intensidade vêm do jogador. Seu trabalho é só escolher o fator pessoal, entre ${FATOR_MIN} e ${FATOR_MAX}, que mede o esforço RELATIVO para esta pessoa:
- Acima de 1 quando a tarefa é nova, difícil, desconfortável ou fora do normal para ela (ex.: caminhada para quem é sedentário; faxina num banheiro muito sujo).
- 1 quando é um esforço normal para ela.
- Abaixo de 1 quando já é automático na rotina dela (ex.: treino de 1 h para quem treina há anos).
Use a memória sobre o jogador quando houver. Sem memória, fique perto de 1, só se afastando quando o contexto da tarefa justificar.
Se a intensidade informada parecer claramente errada para a tarefa, compense um pouco no fator, sem passar dos limites.
A justificativa é mostrada ao jogador: uma ou duas frases, em português do Brasil, em tom amigável, falando com ele ("você").

Responda SOMENTE com um objeto JSON neste formato:
{"fator_pessoal": <número entre ${FATOR_MIN} e ${FATOR_MAX}>, "justificativa": "<texto>"}`;

type RespostaLlm = { fator_pessoal: number; justificativa: string };
function respostaValida(v: unknown): v is RespostaLlm {
  const r = v as RespostaLlm;
  return typeof r === "object" && r !== null &&
    typeof r.fator_pessoal === "number" && Number.isFinite(r.fator_pessoal) &&
    typeof r.justificativa === "string";
}

type Entrada = {
  titulo?: unknown;
  descricao?: unknown;
  contexto?: unknown;
  minutos?: unknown;
  intensidade?: unknown;
  recorrencia?: unknown;
  tarefa_id?: unknown;
};

function texto(v: unknown, max: number): string | null {
  if (typeof v !== "string") return null;
  const t = v.trim();
  return t ? t.slice(0, max) : null;
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: corsHeaders });
  if (req.method !== "POST") return json({ erro: "use POST" }, 405);

  const usuario = await usuarioDaRequisicao(req);
  if (!usuario) return json({ erro: "não autenticado" }, 401);

  let entrada: Entrada;
  try {
    entrada = await req.json();
  } catch {
    return json({ erro: "corpo JSON inválido" }, 400);
  }

  const db = clienteAdmin();
  const tarefaId = texto(entrada.tarefa_id, 64);

  // Recalcular: parte da tarefa existente e aplica o que veio de novo.
  let existente: Record<string, unknown> | null = null;
  if (tarefaId) {
    const { data, error } = await db
      .from("tarefas")
      .select("*")
      .eq("id", tarefaId)
      .eq("user_id", usuario.id)
      .maybeSingle();
    if (error) return json({ erro: error.message }, 500);
    if (!data) return json({ erro: "tarefa não encontrada" }, 404);
    existente = data;
  }

  const titulo = texto(entrada.titulo, 120) ?? (existente?.titulo as string | undefined) ?? null;
  const descricao = texto(entrada.descricao, 500) ?? (existente?.descricao as string | null) ?? null;
  const contexto = texto(entrada.contexto, 500) ?? (existente?.contexto as string | null) ?? null;
  // Campos vazios ou 0 contam como "não enviado" (o app em Unity manda todos os campos).
  const minutos = Number(entrada.minutos || existente?.minutos);
  const intensidade = (texto(entrada.intensidade, 20) ?? existente?.intensidade) as Intensidade;
  const recorrencia = (texto(entrada.recorrencia, 20) ?? existente?.recorrencia ?? "avulsa") as string;

  if (!titulo) return json({ erro: "titulo é obrigatório" }, 400);
  if (!Number.isInteger(minutos) || minutos < 1 || minutos > 600) {
    return json({ erro: "minutos deve ser um inteiro entre 1 e 600" }, 400);
  }
  if (!INTENSIDADES.includes(intensidade)) {
    return json({ erro: "intensidade deve ser leve, moderada ou pesada" }, 400);
  }
  if (!RECORRENCIAS.includes(recorrencia as typeof RECORRENCIAS[number])) {
    return json({ erro: "recorrencia deve ser avulsa, diaria ou semanal" }, 400);
  }

  const { data: perfil } = await db
    .from("perfil")
    .select("memoria")
    .eq("user_id", usuario.id)
    .maybeSingle();

  const prompt = [
    `<memoria_do_jogador>\n${perfil?.memoria ?? "(ainda não há memória: o perfil inicial não foi respondido)"}\n</memoria_do_jogador>`,
    `<tarefa>`,
    `Título: ${titulo}`,
    descricao ? `Descrição: ${descricao}` : null,
    `Duração: ${minutos} min`,
    `Intensidade informada: ${intensidade}`,
    `Recorrência: ${recorrencia}`,
    contexto ? `Contexto dado pelo jogador: ${contexto}` : null,
    `</tarefa>`,
  ].filter(Boolean).join("\n");

  const llm = await pedirJson<RespostaLlm>({ system: SYSTEM, prompt, validar: respostaValida });

  const fator = llm ? limitarFator(llm.fator_pessoal) : 1;
  const linha = {
    user_id: usuario.id,
    titulo,
    descricao,
    contexto,
    minutos,
    intensidade,
    recorrencia,
    fator_pessoal: fator,
    pontos: calcularPontos(minutos, intensidade, fator),
    justificativa: llm?.justificativa?.slice(0, 500) ?? "Pontuação pela fórmula base (a LLM não respondeu).",
    pontuada_por: llm ? "llm" : "formula",
  };

  const consulta = existente
    ? db.from("tarefas").update(linha).eq("id", tarefaId!).select().single()
    : db.from("tarefas").insert(linha).select().single();
  const { data: tarefa, error } = await consulta;
  if (error) return json({ erro: error.message }, 500);

  return json({ tarefa });
});
