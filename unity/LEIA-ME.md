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
| `Scripts/Cena/BonecoProvisorio.cs` | Boneco provisório (será trocado no passo 3) |

O humor do boneco por enquanto depende de quantas tarefas você fez hoje
(nenhuma: sinais de cansaço; 1 ou 2: normal; 3 ou mais: bem). No passo 3 ele passa a seguir as barras de Hidratação e Movimento.
