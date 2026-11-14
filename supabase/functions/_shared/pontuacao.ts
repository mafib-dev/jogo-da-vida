// Fórmula base de pontuação (Banco de Ideias, seção 3.1).
// Pontos = minutos × intensidade × fator pessoal, entre 3 e 150.

export type Intensidade = "leve" | "moderada" | "pesada";

export const PONTOS_POR_MINUTO: Record<Intensidade, number> = {
  leve: 0.5,
  moderada: 1,
  pesada: 1.5,
};

export const FATOR_MIN = 0.75;
export const FATOR_MAX = 1.5;
export const PONTOS_MIN = 3;
export const PONTOS_MAX = 150;

export function limitarFator(fator: number): number {
  if (!Number.isFinite(fator)) return 1;
  const limitado = Math.min(FATOR_MAX, Math.max(FATOR_MIN, fator));
  return Math.round(limitado * 100) / 100;
}

export function calcularPontos(minutos: number, intensidade: Intensidade, fator = 1): number {
  const bruto = minutos * PONTOS_POR_MINUTO[intensidade] * limitarFator(fator);
  return Math.min(PONTOS_MAX, Math.max(PONTOS_MIN, Math.round(bruto)));
}
