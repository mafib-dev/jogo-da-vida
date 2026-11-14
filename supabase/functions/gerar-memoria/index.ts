// Edge Function: recebe as respostas do perfil inicial e cria a primeira
// versão da "memória" da LLM sobre o jogador.
//
// POST /functions/v1/gerar-memoria
// Authorization: Bearer <token do usuário>
// { "nome": "Matheus", "respostas": { "rotina": "...", "moradia": "...", ... } }
//
// As chaves de "respostas" são as de PERGUNTAS abaixo (todas opcionais).
// Se a LLM falhar, a memória vira as próprias respostas em texto.

import {
  clienteAdmin,
  corsHeaders,
  json,
  pedirJson,
  usuarioDaRequisicao,
} from "../_shared/comum.ts";

export const PERGUNTAS: Record<string, string> = {
  rotina: "Como é sua rotina de trabalho ou estudo? (horários, presencial ou remoto)",
  moradia: "Mora sozinho ou com alguém? Quem divide as tarefas da casa?",
  atividade_fisica: "Como está sua atividade física hoje? (nenhuma, às vezes, regular)",
  sono: "A que horas costuma dormir e acordar?",
  alimentacao: "Como é sua alimentação e quanto de água costuma beber?",
  tarefas_dificeis: "Quais tarefas são mais difíceis ou chatas para você?",
  objetivos: "O que você quer melhorar na sua vida agora?",
  saude: "Tem alguma limitação de saúde ou lesão que a gente deve considerar?",
};

const SYSTEM = `Você mantém a "memória" do "Jogo da Vida", um jogo em que uma pessoa ganha pontos por tarefas reais.
Essa memória será lida depois por outra chamada que decide quanto esforço cada tarefa representa PARA ESTA PESSOA.
A partir das respostas do perfil inicial, escreva fatos curtos e objetivos, em português do Brasil, em terceira pessoa:
rotina e horários, quem divide a casa, nível de atividade física, sono, alimentação e água, o que é difícil ou fácil para ela, objetivos e limitações de saúde.
Inclua só o que foi dito; não invente. Também estime horários de acordar e dormir (formato HH:MM) e uma meta diária de água em ml (padrão 2000, ajuste se fizer sentido pelo que foi dito).

Responda SOMENTE com um objeto JSON neste formato:
{"fatos": ["<fato>", ...], "acorda": "HH:MM", "dorme": "HH:MM", "meta_agua_ml": <inteiro>}`;

type Memoria = { fatos: string[]; acorda: string; dorme: string; meta_agua_ml: number };
function memoriaValida(v: unknown): v is Memoria {
  const m = v as Memoria;
  return typeof m === "object" && m !== null &&
    Array.isArray(m.fatos) && m.fatos.every((f) => typeof f === "string") &&
    typeof m.acorda === "string" && typeof m.dorme === "string" &&
    typeof m.meta_agua_ml === "number";
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: corsHeaders });
  if (req.method !== "POST") return json({ erro: "use POST" }, 405);

  const usuario = await usuarioDaRequisicao(req);
  if (!usuario) return json({ erro: "não autenticado" }, 401);

  let corpo: { nome?: unknown; respostas?: Record<string, unknown> };
  try {
    corpo = await req.json();
  } catch {
    return json({ erro: "corpo JSON inválido" }, 400);
  }

  const respostas: Record<string, string> = {};
  for (const chave of Object.keys(PERGUNTAS)) {
    const v = corpo.respostas?.[chave];
    if (typeof v === "string" && v.trim()) respostas[chave] = v.trim().slice(0, 1000);
  }
  if (Object.keys(respostas).length === 0) {
    return json({ erro: "envie pelo menos uma resposta", perguntas: PERGUNTAS }, 400);
  }

  const textoRespostas = Object.entries(respostas)
    .map(([k, v]) => `Pergunta: ${PERGUNTAS[k]}\nResposta: ${v}`)
    .join("\n\n");

  const llm = await pedirJson<Memoria>({
    system: SYSTEM,
    prompt: `<respostas_do_perfil>\n${textoRespostas}\n</respostas_do_perfil>`,
    validar: memoriaValida,
  });

  const memoria = llm
    ? [
      ...llm.fatos.map((f) => `- ${f}`),
      `- Acorda por volta de ${llm.acorda} e dorme por volta de ${llm.dorme}.`,
      `- Meta diária de água: ${llm.meta_agua_ml} ml.`,
    ].join("\n")
    : textoRespostas;

  const nome = typeof corpo.nome === "string" && corpo.nome.trim() ? corpo.nome.trim().slice(0, 60) : undefined;
  const { data, error } = await clienteAdmin()
    .from("perfil")
    .upsert({
      user_id: usuario.id,
      ...(nome ? { nome } : {}),
      respostas: { ...respostas, ...(llm ? { _horarios: { acorda: llm.acorda, dorme: llm.dorme }, _meta_agua_ml: llm.meta_agua_ml } : {}) },
      memoria,
      atualizado_em: new Date().toISOString(),
    })
    .select("user_id, nome, memoria")
    .single();
  if (error) return json({ erro: error.message }, 500);

  return json({ perfil: data, gerada_por: llm ? "llm" : "respostas" });
});
