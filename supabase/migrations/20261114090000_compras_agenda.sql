-- Jogo da Vida: lista de mercado e agenda (vindas do Organizador Pessoal).
-- Não dão pontos: são utilidades da vida real dentro do jogo.
--   * lista_compras: item pendente até "comprei" (vai para o histórico) ou excluir.
--     (O nome "compras" já é a tabela das compras da loja do jogo.)
--   * eventos: compromissos com data. No dia, aparecem na tela principal.
--     Feito fica riscado; se o dia passar sem marcar, vira "não realizado"
--     (derivado: pendente com data no passado). Adiar só muda a data.
-- O próprio jogador grava nas duas tabelas (RLS restringe às linhas dele).

create table public.lista_compras (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null default auth.uid() references auth.users (id) on delete cascade,
  item        text not null check (length(item) between 1 and 80),
  quantidade  text,
  categoria   text not null default 'mercado' check (categoria in ('mercado', 'farmacia', 'casa', 'outros')),
  comprado    boolean not null default false,
  comprado_em timestamptz,
  criado_em   timestamptz not null default now()
);
create index lista_compras_user_idx on public.lista_compras (user_id, comprado, categoria);

create table public.eventos (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null default auth.uid() references auth.users (id) on delete cascade,
  titulo      text not null check (length(titulo) between 1 and 120),
  descricao   text,
  data        date not null,
  hora        time,
  status      text not null default 'pendente' check (status in ('pendente', 'feito')),
  feito_em    timestamptz,
  criado_em   timestamptz not null default now()
);
create index eventos_user_data_idx on public.eventos (user_id, data);

alter table public.lista_compras enable row level security;
alter table public.eventos enable row level security;

create policy "ler a própria lista"    on public.lista_compras for select to authenticated using (user_id = (select auth.uid()));
create policy "criar na própria lista" on public.lista_compras for insert to authenticated with check (user_id = (select auth.uid()));
create policy "editar a própria lista" on public.lista_compras for update to authenticated
  using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));
create policy "apagar da própria lista" on public.lista_compras for delete to authenticated using (user_id = (select auth.uid()));

create policy "ler a própria agenda"    on public.eventos for select to authenticated using (user_id = (select auth.uid()));
create policy "criar na própria agenda" on public.eventos for insert to authenticated with check (user_id = (select auth.uid()));
create policy "editar a própria agenda" on public.eventos for update to authenticated
  using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));
create policy "apagar da própria agenda" on public.eventos for delete to authenticated using (user_id = (select auth.uid()));
