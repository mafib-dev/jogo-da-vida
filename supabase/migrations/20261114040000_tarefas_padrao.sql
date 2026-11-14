-- Jogo da Vida: catálogo de tarefas padrão.
-- Cada jogador tem o próprio catálogo, que começa com 20 tarefas e pode ser
-- editado à vontade (adicionar, editar, remover). O catálogo não tem pontos:
-- usar um item só preenche o cadastro, e a tarefa continua sendo pontuada pela
-- Edge Function pontuar-tarefa. Por isso o próprio app pode gravar aqui.

create type public.categoria_tarefa as enum ('casa', 'saude', 'conhecimento', 'financas', 'outra');

create table public.tarefas_padrao (
  id           uuid primary key default gen_random_uuid(),
  user_id      uuid not null default auth.uid() references auth.users (id) on delete cascade,
  titulo       text not null check (length(titulo) between 1 and 120),
  descricao    text,
  categoria    public.categoria_tarefa not null default 'outra',
  minutos      integer not null check (minutos between 1 and 600),
  intensidade  public.intensidade not null,
  recorrencia  public.recorrencia not null default 'avulsa',
  ordem        integer not null default 1000,
  criada_em    timestamptz not null default now()
);

create index tarefas_padrao_user_idx on public.tarefas_padrao (user_id, categoria, ordem);

-- ---------------------------------------------------------------------------
-- As 20 tarefas iniciais (aprovadas pelo Matheus em 13/11/2026)
-- ---------------------------------------------------------------------------
create or replace function public.lista_tarefas_padrao()
returns table (titulo text, categoria public.categoria_tarefa, minutos integer,
               intensidade public.intensidade, recorrencia public.recorrencia, ordem integer)
language sql immutable set search_path = '' as $$
  values
    ('Tirar o lixo',                'casa'::public.categoria_tarefa,  5, 'leve'::public.intensidade,     'diaria'::public.recorrencia,   10),
    ('Lavar a louça',               'casa',                          20, 'moderada',                     'diaria',                       20),
    ('Arrumar a cama',              'casa',                           3, 'leve',                         'diaria',                       30),
    ('Regar as plantas',            'casa',                           5, 'leve',                         'diaria',                       40),
    ('Varrer ou aspirar a casa',    'casa',                          20, 'moderada',                     'semanal',                      50),
    ('Passar pano no chão',         'casa',                          25, 'moderada',                     'semanal',                      60),
    ('Limpar o banheiro',           'casa',                          30, 'pesada',                       'semanal',                      70),
    ('Lavar e estender roupa',      'casa',                          20, 'moderada',                     'semanal',                      80),
    ('Dobrar e guardar roupa',      'casa',                          15, 'leve',                         'semanal',                      90),
    ('Trocar a roupa de cama',      'casa',                          10, 'leve',                         'semanal',                     100),
    ('Fazer compras do mercado',    'casa',                          45, 'moderada',                     'semanal',                     110),
    ('Cozinhar uma refeição',       'casa',                          40, 'moderada',                     'avulsa',                      120),
    ('Caminhada',                   'saude',                         30, 'moderada',                     'avulsa',                      210),
    ('Treino na academia',          'saude',                         60, 'pesada',                       'avulsa',                      220),
    ('Alongamento',                 'saude',                         10, 'leve',                         'diaria',                      230),
    ('Ler',                         'conhecimento',                  20, 'moderada',                     'diaria',                      310),
    ('Estudar ou fazer um curso',   'conhecimento',                  60, 'moderada',                     'avulsa',                      320),
    ('Praticar um idioma',          'conhecimento',                  15, 'moderada',                     'diaria',                      330),
    ('Anotar os gastos do dia',     'financas',                       5, 'leve',                         'diaria',                      410),
    ('Pagar contas',                'financas',                      15, 'leve',                         'avulsa',                      420)
$$;

-- Coloca no catálogo do jogador as tarefas iniciais que ainda não estão lá
-- (compara pelo título, sem diferenciar maiúsculas). Devolve quantas entraram.
create or replace function public.semear_tarefas_padrao(p_uid uuid)
returns integer language plpgsql security definer set search_path = '' as $$
declare
  v_qtd integer;
begin
  insert into public.tarefas_padrao (user_id, titulo, categoria, minutos, intensidade, recorrencia, ordem)
  select p_uid, l.titulo, l.categoria, l.minutos, l.intensidade, l.recorrencia, l.ordem
    from public.lista_tarefas_padrao() l
   where not exists (select 1 from public.tarefas_padrao t
                      where t.user_id = p_uid and lower(t.titulo) = lower(l.titulo));
  get diagnostics v_qtd = row_count;
  return v_qtd;
end $$;
revoke all on function public.semear_tarefas_padrao(uuid) from public, anon, authenticated;

-- Botão "Restaurar padrões" do app: devolve ao catálogo as iniciais que foram removidas.
create or replace function public.restaurar_tarefas_padrao()
returns integer language plpgsql security definer set search_path = '' as $$
begin
  if auth.uid() is null then raise exception 'Faça login de novo.'; end if;
  return public.semear_tarefas_padrao(auth.uid());
end $$;
revoke all on function public.restaurar_tarefas_padrao() from public, anon;
grant execute on function public.restaurar_tarefas_padrao() to authenticated;

-- Jogador novo já ganha o catálogo junto com o perfil.
create or replace function public.semear_catalogo_novo_perfil()
returns trigger language plpgsql security definer set search_path = '' as $$
begin
  perform public.semear_tarefas_padrao(new.user_id);
  return new;
end $$;
revoke all on function public.semear_catalogo_novo_perfil() from public, anon, authenticated;

create trigger ao_criar_perfil_semear_catalogo
  after insert on public.perfil
  for each row execute function public.semear_catalogo_novo_perfil();

-- Jogadores que já existem
do $$ begin perform public.semear_tarefas_padrao(user_id) from public.perfil; end $$;

-- ---------------------------------------------------------------------------
-- Segurança: cada jogador lê e edita só o próprio catálogo
-- ---------------------------------------------------------------------------
alter table public.tarefas_padrao enable row level security;
revoke all on public.tarefas_padrao from anon;
grant select, insert, update, delete on public.tarefas_padrao to authenticated;

create policy "ler o próprio catálogo" on public.tarefas_padrao
  for select to authenticated using (user_id = (select auth.uid()));
create policy "adicionar ao próprio catálogo" on public.tarefas_padrao
  for insert to authenticated with check (user_id = (select auth.uid()));
create policy "editar o próprio catálogo" on public.tarefas_padrao
  for update to authenticated using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));
create policy "remover do próprio catálogo" on public.tarefas_padrao
  for delete to authenticated using (user_id = (select auth.uid()));
