-- Jogo da Vida: barras do Tamagotchi (Hidratação e Movimento) e loja.
-- Valores provisórios (Banco de Ideias 3.4 e 3.10), para ajustar com o uso.

-- ---------------------------------------------------------------------------
-- Crédito de XP e moedas (usado por barras e, no futuro, sugestões do dia)
-- ---------------------------------------------------------------------------
create or replace function public.creditar_xp(p_uid uuid, p_pontos integer,
  out moedas_ganhas integer, out nivel integer, out subiu_de_nivel boolean)
language plpgsql security definer set search_path = '' as $$
declare
  v_antes integer;
  v_depois integer;
begin
  select xp_total into v_antes from public.perfil where user_id = p_uid for update;
  if not found then
    insert into public.perfil (user_id) values (p_uid);
    v_antes := 0;
  end if;
  v_depois := v_antes + greatest(p_pontos, 0);
  moedas_ganhas := (v_depois / 10) - (v_antes / 10);
  nivel := (public.calcular_nivel(v_depois)).nivel;
  subiu_de_nivel := nivel > (public.calcular_nivel(v_antes)).nivel;
  update public.perfil
     set xp_total = v_depois, moedas = moedas + moedas_ganhas, atualizado_em = now()
   where user_id = p_uid;
end $$;
revoke all on function public.creditar_xp(uuid, integer) from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- Barras
--   Hidratação: 200 ml ≈ +12%; esvazia em ~8 h acordado (12,5%/h)
--   Movimento:  leve 1,5%/min, moderada 2%/min, pesada 2,5%/min
--               (10 min de caminhada ≈ +20%; treino pesado de 40 min = 100%);
--               esvazia em ~24 h acordado (~4,2%/h)
--   As barras não descem durante o sono (horários do perfil; padrão 23:00 a 07:00).
-- ---------------------------------------------------------------------------
alter table public.perfil
  add column hidratacao numeric(5,2) not null default 50 check (hidratacao between 0 and 100),
  add column movimento  numeric(5,2) not null default 50 check (movimento between 0 and 100),
  add column barras_em  timestamptz not null default now();

create type public.tipo_barra as enum ('hidratacao', 'movimento');

create table public.registros_barra (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null references auth.users (id) on delete cascade,
  tipo        public.tipo_barra not null,
  quantidade  integer not null check (quantidade between 1 and 3000), -- ml ou minutos
  intensidade public.intensidade,
  ganho       numeric(5,2) not null,
  pontos      integer not null default 0,
  data        date not null default public.hoje(),
  criado_em   timestamptz not null default now()
);
create index registros_barra_user_data_idx on public.registros_barra (user_id, data desc);

-- Horas acordado entre dois instantes (passos de 5 min, no máximo 48 h).
create or replace function public.horas_acordado(p_de timestamptz, p_ate timestamptz,
  p_acorda time, p_dorme time)
returns numeric language sql stable set search_path = '' as $$
  select coalesce(count(*) filter (where
           case when p_dorme > p_acorda
                then (t at time zone 'America/Sao_Paulo')::time >= p_acorda
                 and (t at time zone 'America/Sao_Paulo')::time <  p_dorme
                else (t at time zone 'America/Sao_Paulo')::time >= p_acorda
                  or (t at time zone 'America/Sao_Paulo')::time <  p_dorme
           end), 0) * 5 / 60.0
  from generate_series(greatest(p_de, p_ate - interval '48 hours'), p_ate - interval '5 minutes', interval '5 minutes') t
$$;

-- Aplica o desgaste desde a última atualização e devolve o estado atual.
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
  v_horas := public.horas_acordado(v_p.barras_em, now(), v_acorda, v_dorme);

  hidratacao := greatest(0, v_p.hidratacao - v_horas * 12.5);
  movimento  := greatest(0, v_p.movimento  - v_horas * (100.0 / 24));
  dormindo := not (case when v_dorme > v_acorda
                        then v_agora >= v_acorda and v_agora < v_dorme
                        else v_agora >= v_acorda or v_agora < v_dorme end);

  update public.perfil
     set hidratacao = atualizar_barras.hidratacao,
         movimento = atualizar_barras.movimento,
         barras_em = now()
   where user_id = p_uid;
end $$;
revoke all on function public.atualizar_barras(uuid) from public, anon, authenticated;

-- Para o app: estado atual das barras.
create or replace function public.status_barras()
returns jsonb language plpgsql security definer set search_path = '' as $$
declare r record;
begin
  if auth.uid() is null then raise exception 'não autenticado' using errcode = '28000'; end if;
  select * into r from public.atualizar_barras(auth.uid());
  return jsonb_build_object('hidratacao', round(r.hidratacao), 'movimento', round(r.movimento), 'dormindo', r.dormindo);
end $$;

-- Para o app: registrar água (ml) ou movimento (minutos).
-- Dá 2 pontos por registro, até 10 registros pontuados por barra por dia.
create or replace function public.registrar_barra(p_tipo public.tipo_barra, p_quantidade integer,
  p_intensidade public.intensidade default 'moderada')
returns jsonb language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := auth.uid();
  r record;
  v_ganho numeric;
  v_pontos integer := 0;
  v_hoje_qtd integer;
  c record;
begin
  if v_uid is null then raise exception 'não autenticado' using errcode = '28000'; end if;
  if p_quantidade is null or p_quantidade < 1 or p_quantidade > 3000 then
    raise exception 'quantidade inválida' using errcode = '22023';
  end if;

  select * into r from public.atualizar_barras(v_uid);

  if p_tipo = 'hidratacao' then
    v_ganho := p_quantidade * 0.06;
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

-- ---------------------------------------------------------------------------
-- Loja (preços provisórios: com 1 moeda a cada 10 pontos, um dia ativo rende ~10 a 20 moedas)
-- ---------------------------------------------------------------------------
create type public.categoria_item as enum ('roupa', 'movel');

create table public.itens (
  id        text primary key,
  nome      text not null,
  categoria public.categoria_item not null,
  preco     integer not null check (preco > 0),
  descricao text,
  ordem     integer not null default 0
);

insert into public.itens (id, nome, categoria, preco, descricao, ordem) values
  ('bone',            'Boné',               'roupa', 20,  'Para os dias de sol.', 1),
  ('camiseta',        'Camiseta estampada', 'roupa', 35,  'Um toque de estilo.', 2),
  ('tenis_corrida',   'Tênis de corrida',   'roupa', 60,  'Pronto para se mexer.', 3),
  ('relogio',         'Relógio',            'roupa', 90,  'Quem cumpre horário merece.', 4),
  ('jaqueta',         'Jaqueta',            'roupa', 120, 'A peça mais cara do guarda-roupa.', 5),
  ('planta',          'Planta',             'movel', 25,  'Deixa o quarto mais vivo.', 6),
  ('luminaria',       'Luminária de mesa',  'movel', 40,  'Luz boa para estudar.', 7),
  ('tapete',          'Tapete',             'movel', 55,  'Conforto no chão.', 8),
  ('estante',         'Estante de livros',  'movel', 100, 'Espaço para os livros que você terminar.', 9),
  ('poltrona',        'Poltrona',           'movel', 150, 'O canto do descanso merecido.', 10);

create table public.compras (
  user_id     uuid not null references auth.users (id) on delete cascade,
  item_id     text not null references public.itens (id),
  preco_pago  integer not null,
  equipado    boolean not null default true,
  comprado_em timestamptz not null default now(),
  primary key (user_id, item_id)
);

create or replace function public.comprar_item(p_item_id text)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := auth.uid();
  v_item public.itens%rowtype;
  v_moedas integer;
begin
  if v_uid is null then raise exception 'não autenticado' using errcode = '28000'; end if;
  select * into v_item from public.itens where id = p_item_id;
  if not found then raise exception 'item não existe' using errcode = 'P0002'; end if;
  if exists (select 1 from public.compras where user_id = v_uid and item_id = p_item_id) then
    raise exception 'você já tem esse item' using errcode = 'P0001';
  end if;
  select moedas into v_moedas from public.perfil where user_id = v_uid for update;
  if coalesce(v_moedas, 0) < v_item.preco then
    raise exception 'moedas insuficientes: faltam %', v_item.preco - coalesce(v_moedas, 0) using errcode = 'P0001';
  end if;
  update public.perfil set moedas = moedas - v_item.preco, atualizado_em = now() where user_id = v_uid;
  insert into public.compras (user_id, item_id, preco_pago) values (v_uid, p_item_id, v_item.preco);
  return jsonb_build_object('item_id', p_item_id, 'moedas', v_moedas - v_item.preco);
end $$;

create or replace function public.equipar_item(p_item_id text, p_equipado boolean)
returns void language sql security definer set search_path = '' as $$
  update public.compras set equipado = p_equipado where user_id = auth.uid() and item_id = p_item_id;
$$;

-- ---------------------------------------------------------------------------
-- Segurança
-- ---------------------------------------------------------------------------
revoke insert, update, delete, truncate on public.registros_barra, public.itens, public.compras from anon, authenticated;
revoke all on public.registros_barra, public.compras from anon;
alter table public.registros_barra enable row level security;
alter table public.itens enable row level security;
alter table public.compras enable row level security;
create policy "ler os próprios registros" on public.registros_barra for select to authenticated using (user_id = (select auth.uid()));
create policy "ler o catálogo" on public.itens for select to authenticated using (true);
create policy "ler as próprias compras" on public.compras for select to authenticated using (user_id = (select auth.uid()));

revoke all on function public.status_barras() from public, anon;
revoke all on function public.registrar_barra(public.tipo_barra, integer, public.intensidade) from public, anon;
revoke all on function public.comprar_item(text) from public, anon;
revoke all on function public.equipar_item(text, boolean) from public, anon;
grant execute on function public.status_barras() to authenticated;
grant execute on function public.registrar_barra(public.tipo_barra, integer, public.intensidade) to authenticated;
grant execute on function public.comprar_item(text) to authenticated;
grant execute on function public.equipar_item(text, boolean) to authenticated;
