-- Jogo da Vida: sono (vindo do Organizador Pessoal) e barra de Energia.
--   * "Vou dormir" e "Acordei" ficam em sono_registro; uma linha por noite.
--     data = o dia em que acordou; horas = acordou_em - dormiu_em.
--   * Energia: esvazia em ~16 h acordado (6,25%/h). Ao acordar, uma noite de
--     7,5 h enche 100% (proporcional abaixo disso; nunca diminui a barra).
--   * Enquanto o jogador está dormindo (disse "vou dormir" e ainda não acordou),
--     nenhuma barra desce. O horário fixo do perfil continua valendo como reserva
--     para quando ele não registrar.
--   * Cada registro (dormir ou acordar) dá 2 pontos, uma vez por dia por ação.

alter table public.perfil
  add column energia numeric(5,2) not null default 70 check (energia between 0 and 100),
  add column dormindo_desde timestamptz;

create table public.sono_registro (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null references auth.users (id) on delete cascade,
  data        date,                 -- dia em que acordou (null enquanto dorme)
  dormiu_em   timestamptz,
  acordou_em  timestamptz,
  horas       numeric(4,2),         -- null se faltou um dos dois registros
  criado_em   timestamptz not null default now()
);
create index sono_registro_user_idx on public.sono_registro (user_id, criado_em desc);

-- Muda a assinatura (entra energia), então precisa recriar.
drop function public.atualizar_barras(uuid);
create function public.atualizar_barras(p_uid uuid,
  out hidratacao numeric, out movimento numeric, out energia numeric, out dormindo boolean)
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
  -- Dormindo de verdade: só conta o tempo até a hora em que foi dormir.
  v_horas := case
    when v_p.dormindo_desde is null then public.horas_acordado(v_p.barras_em, now(), v_acorda, v_dorme)
    when v_p.dormindo_desde > v_p.barras_em then public.horas_acordado(v_p.barras_em, v_p.dormindo_desde, v_acorda, v_dorme)
    else 0 end;
  hidratacao := greatest(0, v_p.hidratacao - v_horas * 12.5);
  movimento  := greatest(0, v_p.movimento  - v_horas * (100.0 / 24));
  energia    := greatest(0, v_p.energia    - v_horas * (100.0 / 16));
  dormindo := v_p.dormindo_desde is not null
           or not (case when v_dorme > v_acorda
                        then v_agora >= v_acorda and v_agora < v_dorme
                        else v_agora >= v_acorda or v_agora < v_dorme end);
  update public.perfil
     set hidratacao = atualizar_barras.hidratacao,
         movimento = atualizar_barras.movimento,
         energia = atualizar_barras.energia,
         barras_em = now()
   where user_id = p_uid;
end $$;
revoke all on function public.atualizar_barras(uuid) from public, anon, authenticated;

create or replace function public.status_barras()
returns jsonb language plpgsql security definer set search_path = '' as $$
declare r record;
begin
  if auth.uid() is null then raise exception 'não autenticado' using errcode = '28000'; end if;
  select * into r from public.atualizar_barras(auth.uid());
  return jsonb_build_object('hidratacao', round(r.hidratacao), 'movimento', round(r.movimento),
    'energia', round(r.energia), 'dormindo', r.dormindo,
    'dormindo_agora', (select dormindo_desde is not null from public.perfil where user_id = auth.uid()));
end $$;

-- p_acao: 'dormir' ou 'acordar'.
create or replace function public.registrar_sono(p_acao text)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := auth.uid();
  r record;
  v_sono public.sono_registro%rowtype;
  v_horas numeric;
  v_energia numeric;
  v_pontos integer := 0;
  c record;
begin
  if v_uid is null then raise exception 'não autenticado' using errcode = '28000'; end if;
  if p_acao not in ('dormir', 'acordar') then raise exception 'ação inválida' using errcode = '22023'; end if;

  select * into r from public.atualizar_barras(v_uid);  -- desgaste até agora
  v_energia := r.energia;

  if p_acao = 'dormir' then
    -- Fecha uma noite que ficou aberta (esqueceu de dizer que acordou).
    update public.sono_registro set data = (dormiu_em at time zone 'America/Sao_Paulo')::date
     where user_id = v_uid and acordou_em is null and data is null;
    if not exists (select 1 from public.sono_registro
                    where user_id = v_uid and dormiu_em > now() - interval '12 hours') then
      v_pontos := 2;  -- uma vez por noite (evita somar pontos com vários "vou dormir")
    end if;
    insert into public.sono_registro (user_id, dormiu_em) values (v_uid, now());
    update public.perfil set dormindo_desde = now() where user_id = v_uid;
  else
    select * into v_sono from public.sono_registro
     where user_id = v_uid and acordou_em is null and data is null and dormiu_em > now() - interval '24 hours'
     order by dormiu_em desc limit 1;
    if found then
      v_horas := round(extract(epoch from now() - v_sono.dormiu_em) / 3600.0, 2);
      v_energia := greatest(v_energia, least(100, v_horas * 100 / 7.5));
      update public.sono_registro set acordou_em = now(), data = public.hoje(), horas = v_horas where id = v_sono.id;
    else
      insert into public.sono_registro (user_id, data, acordou_em) values (v_uid, public.hoje(), now());
    end if;
    if (select count(*) from public.sono_registro where user_id = v_uid and data = public.hoje() and acordou_em is not null) = 1 then
      v_pontos := 2;
    end if;
    update public.perfil set dormindo_desde = null, energia = v_energia, barras_em = now() where user_id = v_uid;
  end if;

  select * into c from public.creditar_xp(v_uid, v_pontos);
  return jsonb_build_object('energia', round(v_energia), 'horas', v_horas, 'pontos', v_pontos,
    'moedas_ganhas', c.moedas_ganhas, 'nivel', c.nivel, 'subiu_de_nivel', c.subiu_de_nivel);
end $$;

-- Segurança: o app só lê o próprio sono; escrita pela função.
revoke insert, update, delete, truncate on public.sono_registro from anon, authenticated;
revoke all on public.sono_registro from anon;
alter table public.sono_registro enable row level security;
create policy "ler o próprio sono" on public.sono_registro for select to authenticated using (user_id = (select auth.uid()));

revoke all on function public.registrar_sono(text) from public, anon;
grant execute on function public.registrar_sono(text) to authenticated;
