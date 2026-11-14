-- Jogo da Vida: esquema inicial do MVP (passo 1)
-- Tabelas: perfil (jogador), tarefas, conclusoes (histórico).
-- Regra importante: o jogador não altera a pontuação. Por isso tarefas e
-- pontos só são gravados pelas Edge Functions (service role) e pela função
-- concluir_tarefa (security definer). O app só lê e chama essas funções.

create extension if not exists pgcrypto;

-- Fuso usado para "hoje", streaks e semanas.
create or replace function public.hoje() returns date
language sql stable set search_path = '' as $$ select (now() at time zone 'America/Sao_Paulo')::date $$;

-- ---------------------------------------------------------------------------
-- Tipos
-- ---------------------------------------------------------------------------
create type public.intensidade as enum ('leve', 'moderada', 'pesada');
create type public.recorrencia as enum ('avulsa', 'diaria', 'semanal');
create type public.origem_pontuacao as enum ('llm', 'formula');

-- ---------------------------------------------------------------------------
-- Perfil do jogador
-- ---------------------------------------------------------------------------
create table public.perfil (
  user_id      uuid primary key references auth.users (id) on delete cascade,
  nome         text,
  respostas    jsonb not null default '{}'::jsonb,   -- as 8 perguntas do perfil inicial
  memoria      text,                                  -- "memória" da LLM sobre o jogador
  xp_total     integer not null default 0 check (xp_total >= 0),
  moedas       integer not null default 0 check (moedas >= 0),
  criado_em    timestamptz not null default now(),
  atualizado_em timestamptz not null default now()
);

-- Cria o perfil automaticamente quando o usuário se cadastra.
create or replace function public.criar_perfil_novo_usuario()
returns trigger language plpgsql security definer set search_path = '' as $$
begin
  insert into public.perfil (user_id) values (new.id) on conflict do nothing;
  return new;
end $$;

create trigger ao_criar_usuario
  after insert on auth.users
  for each row execute function public.criar_perfil_novo_usuario();

-- ---------------------------------------------------------------------------
-- Tarefas
-- ---------------------------------------------------------------------------
create table public.tarefas (
  id               uuid primary key default gen_random_uuid(),
  user_id          uuid not null references auth.users (id) on delete cascade,
  titulo           text not null check (length(titulo) between 1 and 120),
  descricao        text,
  contexto         text,                      -- contexto extra que o jogador deu ("banheiro muito sujo")
  minutos          integer not null check (minutos between 1 and 600),
  intensidade      public.intensidade not null,
  recorrencia      public.recorrencia not null default 'avulsa',
  fator_pessoal    numeric(3,2) not null default 1.00 check (fator_pessoal between 0.75 and 1.50),
  pontos           integer not null check (pontos between 3 and 150),
  justificativa    text,                      -- explicação curta da LLM
  pontuada_por     public.origem_pontuacao not null,
  ativa            boolean not null default true,
  streak_atual     integer not null default 0,
  streak_recorde   integer not null default 0,
  ultima_conclusao date,
  criada_em        timestamptz not null default now()
);

create index tarefas_user_ativa_idx on public.tarefas (user_id, ativa);

-- ---------------------------------------------------------------------------
-- Histórico de conclusões
-- ---------------------------------------------------------------------------
create table public.conclusoes (
  id            uuid primary key default gen_random_uuid(),
  user_id       uuid not null references auth.users (id) on delete cascade,
  tarefa_id     uuid not null references public.tarefas (id) on delete cascade,
  data          date not null default public.hoje(),
  concluida_em  timestamptz not null default now(),
  pontos_base   integer not null,
  bonus_streak  integer not null default 0,
  pontos        integer not null,             -- base + bônus (é o XP ganho)
  moedas        integer not null default 0,
  streak        integer not null default 0
);

create index conclusoes_user_data_idx on public.conclusoes (user_id, data desc);
create index conclusoes_tarefa_idx on public.conclusoes (tarefa_id, data desc);

-- ---------------------------------------------------------------------------
-- XP e nível (curva crescente: 100, 120, 145, 175, 210...)
-- custo do nível n -> n+1 = 100 + 15(n-1) + 5(n-1)n/2
-- ---------------------------------------------------------------------------
create or replace function public.xp_para_subir(nivel integer)
returns integer language sql immutable set search_path = '' as $$
  select 100 + 15 * (nivel - 1) + (5 * (nivel - 1) * nivel) / 2
$$;

create or replace function public.calcular_nivel(xp integer,
  out nivel integer, out xp_no_nivel integer, out xp_proximo_nivel integer)
language plpgsql immutable set search_path = '' as $$
declare
  restante integer := greatest(xp, 0);
begin
  nivel := 1;
  while restante >= public.xp_para_subir(nivel) loop
    restante := restante - public.xp_para_subir(nivel);
    nivel := nivel + 1;
  end loop;
  xp_no_nivel := restante;
  xp_proximo_nivel := public.xp_para_subir(nivel);
end $$;

create view public.status_jogador with (security_invoker = true) as
select p.user_id, p.nome, p.xp_total, p.moedas,
       n.nivel, n.xp_no_nivel, n.xp_proximo_nivel,
       (p.memoria is not null) as perfil_completo
from public.perfil p
cross join lateral public.calcular_nivel(p.xp_total) n;

-- ---------------------------------------------------------------------------
-- Concluir tarefa: calcula streak, bônus, XP, moedas e se subiu de nível.
-- Valores provisórios (ajustar com o uso):
--   * bônus de streak: +5% por dia/semana seguidos além do primeiro, máximo +25%
--   * moedas: 1 a cada 10 pontos de XP acumulado
-- ---------------------------------------------------------------------------
create or replace function public.concluir_tarefa(p_tarefa_id uuid)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid       uuid := auth.uid();
  v_tarefa    public.tarefas%rowtype;
  v_hoje      date := public.hoje();
  v_streak    integer := 1;
  v_bonus     integer := 0;
  v_pontos    integer;
  v_xp_antes  integer;
  v_xp_depois integer;
  v_moedas    integer;
  v_nivel_antes integer;
  v_nivel_depois integer;
begin
  if v_uid is null then
    raise exception 'não autenticado' using errcode = '28000';
  end if;

  select * into v_tarefa from public.tarefas
   where id = p_tarefa_id and user_id = v_uid and ativa
   for update;
  if not found then
    raise exception 'tarefa não encontrada ou inativa' using errcode = 'P0002';
  end if;

  -- Streak e bloqueio de conclusão repetida no mesmo período
  if v_tarefa.recorrencia = 'diaria' then
    if v_tarefa.ultima_conclusao = v_hoje then
      raise exception 'tarefa já concluída hoje' using errcode = 'P0001';
    end if;
    if v_tarefa.ultima_conclusao = v_hoje - 1 then
      v_streak := v_tarefa.streak_atual + 1;
    end if;
  elsif v_tarefa.recorrencia = 'semanal' then
    if date_trunc('week', v_tarefa.ultima_conclusao) = date_trunc('week', v_hoje) then
      raise exception 'tarefa já concluída nesta semana' using errcode = 'P0001';
    end if;
    if date_trunc('week', v_tarefa.ultima_conclusao) = date_trunc('week', v_hoje) - interval '1 week' then
      v_streak := v_tarefa.streak_atual + 1;
    end if;
  else
    v_streak := 0; -- avulsa não tem streak
  end if;

  if v_streak > 1 then
    v_bonus := greatest(1, round(v_tarefa.pontos * least(0.05 * (v_streak - 1), 0.25)));
  end if;
  v_pontos := v_tarefa.pontos + v_bonus;

  -- XP, moedas e nível
  select xp_total into v_xp_antes from public.perfil where user_id = v_uid for update;
  if not found then
    insert into public.perfil (user_id) values (v_uid);
    v_xp_antes := 0;
  end if;
  v_xp_depois := v_xp_antes + v_pontos;
  v_moedas := (v_xp_depois / 10) - (v_xp_antes / 10);
  v_nivel_antes := (public.calcular_nivel(v_xp_antes)).nivel;
  v_nivel_depois := (public.calcular_nivel(v_xp_depois)).nivel;

  update public.perfil
     set xp_total = v_xp_depois, moedas = moedas + v_moedas, atualizado_em = now()
   where user_id = v_uid;

  update public.tarefas
     set ultima_conclusao = v_hoje,
         streak_atual = v_streak,
         streak_recorde = greatest(streak_recorde, v_streak),
         ativa = (recorrencia <> 'avulsa')
   where id = v_tarefa.id;

  insert into public.conclusoes (user_id, tarefa_id, data, pontos_base, bonus_streak, pontos, moedas, streak)
  values (v_uid, v_tarefa.id, v_hoje, v_tarefa.pontos, v_bonus, v_pontos, v_moedas, v_streak);

  return jsonb_build_object(
    'pontos', v_pontos,
    'pontos_base', v_tarefa.pontos,
    'bonus_streak', v_bonus,
    'streak', v_streak,
    'moedas_ganhas', v_moedas,
    'xp_total', v_xp_depois,
    'nivel', v_nivel_depois,
    'subiu_de_nivel', v_nivel_depois > v_nivel_antes
  );
end $$;

-- Desativar (arquivar) uma tarefa sem apagar o histórico.
create or replace function public.arquivar_tarefa(p_tarefa_id uuid)
returns void language sql security definer set search_path = '' as $$
  update public.tarefas set ativa = false
   where id = p_tarefa_id and user_id = auth.uid();
$$;

-- ---------------------------------------------------------------------------
-- Segurança: o app (role authenticated) só lê; escrita via funções.
-- ---------------------------------------------------------------------------
-- O Supabase dá todos os privilégios às roles da API por padrão; tiramos a escrita.
revoke insert, update, delete, truncate on public.perfil, public.tarefas, public.conclusoes from anon, authenticated;
revoke all on public.perfil, public.tarefas, public.conclusoes, public.status_jogador from anon;

alter table public.perfil     enable row level security;
alter table public.tarefas    enable row level security;
alter table public.conclusoes enable row level security;

create policy "ler o próprio perfil"   on public.perfil     for select to authenticated using (user_id = (select auth.uid()));
create policy "ler as próprias tarefas" on public.tarefas    for select to authenticated using (user_id = (select auth.uid()));
create policy "ler o próprio histórico" on public.conclusoes for select to authenticated using (user_id = (select auth.uid()));

-- O jogador pode editar só o nome no perfil.
grant update (nome) on public.perfil to authenticated;
create policy "editar o próprio nome" on public.perfil for update to authenticated
  using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));

revoke all on function public.concluir_tarefa(uuid) from public, anon;
revoke all on function public.arquivar_tarefa(uuid) from public, anon;
grant execute on function public.concluir_tarefa(uuid) to authenticated;
grant execute on function public.arquivar_tarefa(uuid) to authenticated;
revoke all on function public.criar_perfil_novo_usuario() from public, anon, authenticated;
