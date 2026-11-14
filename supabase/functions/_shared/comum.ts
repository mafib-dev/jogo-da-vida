// Utilidades compartilhadas pelas Edge Functions do Jogo da Vida.
import { createClient, type SupabaseClient, type User } from "npm:@supabase/supabase-js@2.119.0";

// LLM: Qwen por uma API compatível com o formato da OpenAI.
// Segredos/variáveis das Edge Functions:
//   LLM_API_KEY   (obrigatório) chave do Alibaba Cloud Model Studio ou do OpenRouter
//   LLM_BASE_URL  (opcional) padrão: Model Studio internacional
//                 OpenRouter: https://openrouter.ai/api/v1
//   LLM_MODEL     (opcional) padrão: qwen-plus  (no OpenRouter, ex.: qwen/qwen-plus)
const LLM_BASE_URL = Deno.env.get("LLM_BASE_URL") ?? "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
const LLM_MODEL = Deno.env.get("LLM_MODEL") ?? "qwen-plus";

export const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...corsHeaders, "Content-Type": "application/json" },
  });
}

/** Cliente com service role: ignora RLS. Só usar no servidor. */
export function clienteAdmin(): SupabaseClient {
  return createClient(
    Deno.env.get("SUPABASE_URL")!,
    Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
    { auth: { persistSession: false } },
  );
}

/** Descobre o usuário pelo token JWT enviado pelo app. */
export async function usuarioDaRequisicao(req: Request): Promise<User | null> {
  const token = req.headers.get("Authorization")?.replace(/^Bearer\s+/i, "");
  if (!token) return null;
  const { data, error } = await clienteAdmin().auth.getUser(token);
  if (error) return null;
  return data.user;
}

/**
 * Pede ao Qwen uma resposta em JSON. O formato esperado vai descrito no prompt
 * e a resposta é conferida por `validar`.
 * Devolve null em qualquer falha (sem chave, erro de rede, JSON inválido),
 * para quem chamou usar o plano B (fórmula base).
 */
export async function pedirJson<T>(opts: {
  system: string;
  prompt: string;
  validar: (v: unknown) => v is T;
  maxTokens?: number;
}): Promise<T | null> {
  const chave = Deno.env.get("LLM_API_KEY");
  if (!chave) {
    console.warn("LLM_API_KEY não configurada; usando plano B.");
    return null;
  }
  try {
    const resp = await fetch(`${LLM_BASE_URL}/chat/completions`, {
      method: "POST",
      headers: { Authorization: `Bearer ${chave}`, "Content-Type": "application/json" },
      signal: AbortSignal.timeout(30_000),
      body: JSON.stringify({
        model: LLM_MODEL,
        max_tokens: opts.maxTokens ?? 1500,
        temperature: 0.2,
        response_format: { type: "json_object" },
        messages: [
          { role: "system", content: opts.system },
          { role: "user", content: opts.prompt },
        ],
      }),
    });
    if (!resp.ok) {
      console.error("Erro da API do Qwen:", resp.status, (await resp.text()).slice(0, 500));
      return null;
    }
    const dados = await resp.json();
    const texto: string | undefined = dados?.choices?.[0]?.message?.content;
    if (!texto) return null;
    const limpo = texto.trim().replace(/^```(?:json)?\s*/i, "").replace(/```$/, "");
    const valor = JSON.parse(limpo);
    if (!opts.validar(valor)) {
      console.warn("Resposta do Qwen fora do formato:", limpo.slice(0, 300));
      return null;
    }
    return valor;
  } catch (erro) {
    console.error("Falha ao chamar o Qwen:", erro);
    return null;
  }
}
