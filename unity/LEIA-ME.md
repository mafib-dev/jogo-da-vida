# App Unity (passo 2: ciclo de tarefas)

Tudo é montado em código: não precisa arrastar nada no editor.
`JogoApp` sobe sozinho quando você dá Play em qualquer cena.

## Abrir pela primeira vez

1. Instale o **Unity 6** pelo Unity Hub, com o módulo **Android Build Support** (inclui SDK, NDK e OpenJDK).
2. No Hub: **Add → Add project from disk** e escolha esta pasta `unity/`.
   O Unity cria sozinho `ProjectSettings/` e `Library/` na primeira abertura (demora uns minutos).
3. Abra qualquer cena (ou crie uma vazia em File → New Scene) e aperte **Play**.

## Testar no editor

- Use a aba Game com resolução em pé (ex.: 1080x1920) para ver como fica no celular.
- Crie a conta na tela de login. O Supabase manda um e-mail de confirmação; confirme e toque em Entrar.
- Depois do perfil inicial (pode pular), cadastre tarefas e toque em **Feito!**.

## Gerar o APK

1. File → Build Profiles → **Android** → Switch Platform.
2. Player Settings:
   - Company Name e Product Name: como preferir (ex.: "Jogo da Vida").
   - Other Settings → Package Name: ex. `com.mafib.jogodavida`.
   - Resolution and Presentation → Default Orientation: **Portrait**.
3. Salve a cena atual em `Assets/` e adicione na lista de cenas do build.
4. Conecte o celular com depuração USB ligada e use **Build And Run**, ou **Build** para gerar o `.apk`.

## Onde fica cada coisa

| Arquivo | O que faz |
|---|---|
| `Scripts/Config.cs` | Endereço do Supabase e chave pública |
| `Scripts/Dados/SupabaseClient.cs` | Login, tarefas, concluir, status (HTTP) |
| `Scripts/Dados/Modelos.cs` | Formato dos dados |
| `Scripts/JogoApp.cs` | Telas: login, perfil, principal, nova tarefa, detalhe, avisos |
| `Scripts/UI/Estilo.cs` | Cores, botões e campos |
| `Scripts/Cena/CenaDoQuarto.cs` | Quarto 3D: paredes, móveis, câmera e luz (hora do dia e humor) |
| `Scripts/Cena/BonecoProvisorio.cs` | Ponte da cena 3D usada pelo JogoApp: carrega o personagem humano; se falhar, mostra o boneco de cápsula |
| `Scripts/Cena/PersonagemHumano.cs` | Personagem humano: pose relaxada, respiração, olhar, piscadas, 4 humores (postura + rosto) e pulos de comemoração, tudo por código nos ossos (ainda sem clipes do Mixamo) |
| `Resources/PersonagemHumano.prefab` | Prefab do personagem carregado em tempo de execução (referencia os materiais, então eles entram no APK) |
| `Arte/Personagem/` | Personagem.fbx (rig mixamo, blend shapes Rosto_Sorriso e Rosto_OlhosCansados), texturas e materiais |
| `Editor/ImportarPersonagem.cs` | Configuração de importação do FBX, texturas e materiais do personagem |
| `Editor/GerarPrefabDoPersonagem.cs` | Menu Jogo da Vida → Gerar prefab do personagem (rodar de novo após reexportar o FBX) |
| `../arte/blender/gerar_personagem.py` | Script do Blender que gera o personagem e exporta o FBX e as texturas |

O humor do personagem segue a barra mais baixa entre Hidratação e Movimento
(70-100%: bem; 40-70%: normal; 15-40%: sinais; abaixo de 15%: abatido). As barras descem com o tempo,
mas não enquanto você dorme (horários do perfil; padrão 23:00 às 07:00).

## Barras, loja e sugestões

- **Barras:** toque no "+" de Água (copo, garrafinha, litro) ou de Movimento (alongar, caminhar, treinar). Cada registro dá 2 pontos (até 10 por barra por dia).
- **Loja:** 10 itens (5 para o personagem, 5 para o quarto). Por enquanto a compra fica registrada; os itens aparecem no 3D quando os modelos existirem.
- **Sugestões do dia:** um cartão no topo da lista em cada janela (8-11h, 12-14h, 15-18h, 19-22h), pela barra mais baixa. No Android também vira notificação (8:30, 12:30, 15:30, 19:30). Tocar na notificação abre o app; o botão "Feito!" fica no cartão, porque o pacote de notificações do Unity não tem botão de ação no Android.
- As notificações usam o pacote **Mobile Notifications** (`com.unity.mobile.notifications`).

## Entrar sem tela de login (uso pessoal)

Crie `Assets/JogoDaVida/Resources/conta_local.json` com `{ "email": "...", "senha": "..." }`.
Com esse arquivo o app entra sozinho nessa conta e não mostra login nem o botão Sair.
O arquivo está no `.gitignore` e nunca vai para o GitHub (vai só dentro do APK).
Sem ele, o app mostra a tela de login normal.
