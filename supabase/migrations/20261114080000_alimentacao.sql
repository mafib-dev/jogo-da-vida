-- Jogo da Vida: plano alimentar (vindo do Organizador Pessoal), barra de
-- Alimentação e meta de água.
--   * refeicoes: o plano da semana (dia, horário, nome, descrição). O jogador
--     edita à vontade, como o catálogo de tarefas (não tem pontos).
--   * refeicoes_registro: cada refeição do dia marcada como seguida,
--     substituída ou perdida. Também serve para "comi outra coisa" fora do plano.
--   * Barra de Alimentação: refeição enche 35%, lanche 15%; esvazia em ~6 h
--     acordado. Perdida não enche e não tem punição.
--   * Pontos: seguida 3, substituída 2, perdida 0 (máx. 8 registros pontuados por dia).
--   * Meta de água (perfil.meta_agua_ml, padrão 2000): a barra de Hidratação
--     passa a encher proporcionalmente à meta (com 2000 ml, igual a antes).
-- Sem drop de função: o Supabase pede confirmação para isso.

alter table public.perfil
  add column alimentacao numeric(5,2) not null default 50 check (alimentacao between 0 and 100),
  add column meta_agua_ml integer not null default 2000 check (meta_agua_ml between 500 and 6000);

-- O jogador pode ajustar a própria meta de água (o nome já era editável).
grant update (meta_agua_ml) on public.perfil to authenticated;

create table public.refeicoes (
  id           uuid primary key default gen_random_uuid(),
  user_id      uuid not null default auth.uid() references auth.users (id) on delete cascade,
  dia_semana   smallint not null check (dia_semana between 0 and 6),  -- 0 = domingo
  horario      time not null,
  nome         text not null check (length(nome) between 1 and 60),  -- café da manhã, almoço...
  tipo         text not null default 'refeicao' check (tipo in ('refeicao', 'lanche')),
  descricao    text,
  observacoes  text,                                                  -- substituições do plano
  criada_em    timestamptz not null default now()
);
create index refeicoes_user_dia_idx on public.refeicoes (user_id, dia_semana, horario);

create table public.refeicoes_registro (
  id           uuid primary key default gen_random_uuid(),
  user_id      uuid not null references auth.users (id) on delete cascade,
  refeicao_id  uuid references public.refeicoes (id) on delete set null,  -- null = fora do plano
  data         date not null default public.hoje(),
  status       text not null check (status in ('seguida', 'substituida', 'perdida')),
  o_que_comi   text,
  pontos       integer not null default 0,
  criado_em    timestamptz not null default now()
);
create index refeicoes_registro_user_data_idx on public.refeicoes_registro (user_id, data desc);
create unique index refeicoes_registro_unica_idx on public.refeicoes_registro (refeicao_id, data)
  where refeicao_id is not null;

-- Desgaste das barras: mesma assinatura; Energia e Alimentação vão direto no perfil.
create or replace function public.atualizar_barras(p_uid uuid,
  out hidratacao numeric, out movimento numeric, out dormindo boolean)
language plpgsql security definer set search_path = '' as $$
declare
  v_p public.perfil%rowtype;
  v_acorda time;
  v_dorme time;
  v_horas numeric;
  v_agora time := (now() at time zone 'America/Sao_Paulo')::time;
begin
  select * into v_p from public.perfil where user_id = p_uid for update;
  if not found then
    insert into public.perfil (user_id) values (p_uid) returning * into v_p;
  end if;
  v_acorda := coalesce(nullif(v_p.respostas #>> '{_horarios,acorda}', '')::time, '07:00');
  v_dorme  := coalesce(nullif(v_p.respostas #>> '{_horarios,dorme}', '')::time, '23:00');
  v_horas := case
    when v_p.dormindo_desde is null then public.horas_acordado(v_p.barras_em, now(), v_acorda, v_dorme)
    when v_p.dormindo_desde > v_p.barras_em then public.horas_acordado(v_p.barras_em, v_p.dormindo_desde, v_acorda, v_dorme)
    else 0 end;
  hidratacao := greatest(0, v_p.hidratacao - v_horas * 12.5);
  movimento  := greatest(0, v_p.movimento  - v_horas * (100.0 / 24));
  dormindo := v_p.dormindo_desde is not null
           or not (case when v_dorme > v_acorda
                        then v_agora >= v_acorda and v_agora < v_dorme
                        else v_agora >= v_acorda or v_agora < v_dorme end);
  update public.perfil
     set hidratacao = atualizar_barras.hidratacao,
         movimento = atualizar_barras.movimento,
         energia = greatest(0, v_p.energia - v_horas * (100.0 / 16)),
         alimentacao = greatest(0, v_p.alimentacao - v_horas * (100.0 / 6)),
         barras_em = now()
   where user_id = p_uid;
end $$;

create or replace function public.status_barras()
returns jsonb language plpgsql security definer set search_path = '' as $$
declare r record; p record;
begin
  if auth.uid() is null then raise exception 'não autenticado' using errcode = '28000'; end if;
  select * into r from public.atualizar_barras(auth.uid());
  select energia, alimentacao, meta_agua_ml, dormindo_desde into p from public.perfil where user_id = auth.uid();
  return jsonb_build_object('hidratacao', round(r.hidratacao), 'movimento', round(r.movimento),
    'energia', round(p.energia), 'alimentacao', round(p.alimentacao),
    'dormindo', r.dormindo, 'dormindo_agora', p.dormindo_desde is not null,
    'meta_agua_ml', p.meta_agua_ml,
    'agua_hoje_ml', (select coalesce(sum(quantidade), 0) from public.registros_barra
                      where user_id = auth.uid() and tipo = 'hidratacao' and data = public.hoje()));
end $$;

-- Água agora enche pela meta: ganho = ml × 120 / meta (meta 2000 → 200 ml ≈ 12%, como antes).
create or replace function public.registrar_barra(p_tipo public.tipo_barra, p_quantidade integer,
  p_intensidade public.intensidade default 'moderada')
returns jsonb language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := auth.uid();
  r record;
  v_ganho numeric;
  v_pontos integer := 0;
  v_hoje_qtd integer;
  v_meta integer;
  c record;
begin
  if v_uid is null then raise exception 'não autenticado' using errcode = '28000'; end if;
  if p_quantidade is null or p_quantidade < 1 or p_quantidade > 3000 then
    raise exception 'quantidade inválida' using errcode = '22023';
  end if;
  select * into r from public.atualizar_barras(v_uid);
  select meta_agua_ml into v_meta from public.perfil where user_id = v_uid;
  if p_tipo = 'hidratacao' then
    v_ganho := p_quantidade * 120.0 / greatest(coalesce(v_meta, 2000), 500);
  else
    v_ganho := p_quantidade * case coalesce(p_intensidade, 'moderada')
                 when 'leve' then 1.5 when 'pesada' then 2.5 else 2.0 end;
  end if;
  select count(*) into v_hoje_qtd from public.registros_barra
   where user_id = v_uid and tipo = p_tipo and data = public.hoje() and pontos > 0;
  if v_hoje_qtd < 10 then v_pontos := 2; end if;
  update public.perfil set
    hidratacao = case when p_tipo = 'hidratacao' then least(100, r.hidratacao + v_ganho) else r.hidratacao end,
    movimento  = case when p_tipo = 'movimento'  then least(100, r.movimento  + v_ganho) else r.movimento  end
  where user_id = v_uid;
  insert into public.registros_barra (user_id, tipo, quantidade, intensidade, ganho, pontos)
  values (v_uid, p_tipo, p_quantidade, case when p_tipo = 'movimento' then p_intensidade end, least(v_ganho, 999), v_pontos);
  select * into c from public.creditar_xp(v_uid, v_pontos);
  return jsonb_build_object(
    'hidratacao', round(case when p_tipo = 'hidratacao' then least(100, r.hidratacao + v_ganho) else r.hidratacao end),
    'movimento',  round(case when p_tipo = 'movimento'  then least(100, r.movimento  + v_ganho) else r.movimento  end),
    'pontos', v_pontos,
    'moedas_ganhas', c.moedas_ganhas,
    'nivel', c.nivel,
    'subiu_de_nivel', c.subiu_de_nivel);
end $$;

-- Para o app: marcar uma refeição do plano (p_refeicao_id) ou algo fora do plano (null).
-- p_status: seguida | substituida | perdida. Fora do plano exige o_que_comi e conta como substituída.
create or replace function public.registrar_refeicao(p_refeicao_id uuid, p_status text, p_o_que_comi text default null)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := auth.uid();
  v_tipo text := 'refeicao';
  v_ganho numeric := 0;
  v_pontos integer := 0;
  v_alim numeric;
  c record;
begin
  if v_uid is null then raise exception 'não autenticado' using errcode = '28000'; end if;
  if p_status not in ('seguida', 'substituida', 'perdida') then
    raise exception 'status inválido' using errcode = '22023';
  end if;
  if p_refeicao_id is null then
    if coalesce(trim(p_o_que_comi), '') = '' then
      raise exception 'conte o que você comeu' using errcode = '22023';
    end if;
    p_status := 'substituida';
  else
    select tipo into v_tipo from public.refeicoes where id = p_refeicao_id and user_id = v_uid;
    if not found then raise exception 'refeição não encontrada' using errcode = 'P0002'; end if;
    if exists (select 1 from public.refeicoes_registro where refeicao_id = p_refeicao_id and data = public.hoje()) then
      raise exception 'essa refeição já foi marcada hoje' using errcode = 'P0001';
    end if;
  end if;

  perform public.atualizar_barras(v_uid);
  if p_status <> 'perdida' then
    v_ganho := case when v_tipo = 'lanche' then 15 else 35 end;
    if (select count(*) from public.refeicoes_registro
         where user_id = v_uid and data = public.hoje() and pontos > 0) < 8 then
      v_pontos := case when p_status = 'seguida' then 3 else 2 end;
    end if;
  end if;

  update public.perfil set alimentacao = least(100, alimentacao + v_ganho)
   where user_id = v_uid returning alimentacao into v_alim;
  insert into public.refeicoes_registro (user_id, refeicao_id, status, o_que_comi, pontos)
  values (v_uid, p_refeicao_id, p_status, nullif(trim(p_o_que_comi), ''), v_pontos);
  select * into c from public.creditar_xp(v_uid, v_pontos);
  return jsonb_build_object('alimentacao', round(v_alim), 'pontos', v_pontos,
    'moedas_ganhas', c.moedas_ganhas, 'nivel', c.nivel, 'subiu_de_nivel', c.subiu_de_nivel);
end $$;

-- Segurança: o plano é editável pelo próprio jogador; o registro só pela função.
alter table public.refeicoes enable row level security;
alter table public.refeicoes_registro enable row level security;
create policy "ler o próprio plano"     on public.refeicoes for select to authenticated using (user_id = (select auth.uid()));
create policy "criar no próprio plano"  on public.refeicoes for insert to authenticated with check (user_id = (select auth.uid()));
create policy "editar o próprio plano"  on public.refeicoes for update to authenticated
  using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));
create policy "apagar do próprio plano" on public.refeicoes for delete to authenticated using (user_id = (select auth.uid()));
create policy "ler as próprias refeições" on public.refeicoes_registro for select to authenticated using (user_id = (select auth.uid()));
grant execute on function public.registrar_refeicao(uuid, text, text) to authenticated;
