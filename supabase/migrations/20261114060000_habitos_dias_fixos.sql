-- Jogo da Vida: hábitos com dias fixos (vindo do Organizador Pessoal).
-- Uma tarefa diária pode ter dias fixos da semana (0 = domingo a 6 = sábado),
-- como academia de segunda a sexta ou corrida terça, quinta e sábado.
--   * dias null = todo dia (comportamento antigo).
--   * A sequência só conta os dias marcados: fazer a corrida na terça e na quinta
--     mantém o streak, mesmo sem nada na quarta.
--   * Fazer num dia que não está marcado vale os pontos normais (sistema de honra)
--     e não quebra a sequência.

alter table public.tarefas
  add column dias smallint[]
    check (dias is null or (cardinality(dias) between 1 and 7
                            and dias <@ array[0,1,2,3,4,5,6]::smallint[]));

comment on column public.tarefas.dias is
  'Dias fixos da semana (0 = domingo a 6 = sábado) para tarefa diária; null = todo dia.';

-- Último dia marcado antes de p_data (null se a tarefa não tem dias fixos).
create or replace function public.dia_marcado_anterior(p_dias smallint[], p_data date)
returns date language sql immutable set search_path = '' as $$
  select max(d)::date
    from generate_series(p_data - 7, p_data - 1, interval '1 day') d
   where p_dias is not null and extract(dow from d)::smallint = any (p_dias)
$$;

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
  v_anterior  date;
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
    if v_tarefa.dias is null then
      if v_tarefa.ultima_conclusao = v_hoje - 1 then
        v_streak := v_tarefa.streak_atual + 1;
      end if;
    else
      -- Dias fixos: a sequência continua se nenhum dia marcado ficou sem fazer.
      v_anterior := public.dia_marcado_anterior(v_tarefa.dias, v_hoje);
      if v_tarefa.ultima_conclusao >= v_anterior then
        v_streak := v_tarefa.streak_atual + 1;
      end if;
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

revoke all on function public.concluir_tarefa(uuid) from public, anon;
grant execute on function public.concluir_tarefa(uuid) to authenticated;
