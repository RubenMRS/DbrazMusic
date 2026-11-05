# 🎵 Discord Music Bot (C#)

Bot pessoal de música para Discord: colas um link do YouTube (ou escreves o nome da música)
e ele entra no teu canal de voz e toca. Feito em **C# / .NET 10** com `Discord.Net`,
`yt-dlp` e `ffmpeg`.

## Comandos

| Comando | O que faz |
|---|---|
| `/play <link ou pesquisa>` | Entra no teu canal de voz e toca (ou adiciona à fila) |
| `/skip` | Salta para a música seguinte |
| `/pause` / `/resume` | Pausa / continua |
| `/queue` | Mostra a fila |
| `/nowplaying` | Mostra a música atual |
| `/stop` | Para tudo, limpa a fila e sai do canal |
| `/leave` | Sai do canal de voz |

## Como pôr a funcionar

### 1. Criar o bot no Discord

1. Vai a <https://discord.com/developers/applications> → **New Application**.
2. No menu lateral: **Bot** → **Reset Token** → copia o token.
   *(Não é preciso ativar nenhuma "Privileged Intent".)*
3. **OAuth2** → **URL Generator**:
   - **Scopes:** `bot`, `applications.commands`
   - **Permissões:** `Connect`, `Speak`, `Send Messages`, `Embed Links`
4. Abre o URL gerado e adiciona o bot ao teu servidor.

### 2. Configurar

Abre o `config.json` e preenche:

```jsonc
"token": "O_TEU_TOKEN_AQUI",
"guildId": 123456789012345678
```

O `guildId` é opcional, mas recomendado: faz os comandos slash aparecerem **de imediato**.
Se ficar `0`, os comandos são globais e podem demorar até 1 hora.

Para obter o ID do servidor: Discord → Definições → Avançado → ativa **Modo de programador**,
depois botão direito no servidor → **Copiar ID do servidor**.

### 3. Arrancar

```powershell
.\run.ps1
```

ou faz duplo clique em `iniciar.bat`.

## Diagnóstico

```powershell
.\run.ps1 --check      # verifica token, yt-dlp, ffmpeg e bibliotecas de voz
.\run.ps1 --selftest   # testa download + descodificação de uma música (sem Discord)
.\run.ps1 --voicetest  # entra num canal vazio, toca ~6 s e sai (precisa de guildId)
```

Exemplo de saída saudável do `--check`:

```
yt-dlp      : ...\tools\yt-dlp.exe  (2026.10.26)
ffmpeg      : ...\tools\ffmpeg.exe
libsodium.dll : OK (1.0.18)
opus.dll      : OK (libopus 1.3.1...)
libdave.dll   : OK
libdave E2EE: OK (protocolo 1)
```

## Estrutura

```
DiscordMusicBot/
├── Program.cs              arranque, login, verificação
├── Config.cs               leitura do config.json
├── Audio/
│   ├── GuildPlayer.cs      fila + ligação de voz por servidor
│   ├── AudioPipeline.cs    yt-dlp -> ffmpeg -> PCM
│   ├── YtDlp.cs            pesquisa/links e metadados
│   └── ...
├── Commands/               registo e tratamento dos comandos slash
├── natives/                libsodium, opus e libdave (copiadas no build)
├── tools/                  yt-dlp.exe, ffmpeg.exe
├── config.json             token e opções
├── setup.ps1               volta a descarregar tudo (noutro PC, por exemplo)
└── run.ps1 / iniciar.bat    arrancar
```

## Na nuvem (Render 24/7)

O projeto já traz `Dockerfile` e `render.yaml`, prontos para o Render.

### Diferenças do PC

| No PC | Na nuvem |
|---|---|
| `yt-dlp.exe` / `ffmpeg.exe` em `tools\` | instalados na imagem (`PATH`) |
| `libsodium.dll` / `opus.dll` / `libdave.dll` em `natives\` | pacotes `libsodium23`/`libopus0` + `libdave.so` oficial |
| token no `config.json` | `DISCORD_TOKEN` (variável de ambiente) |
| logs só na consola | consola do Render + `bot.log` (efémero) |
| PO tokens gerados pelo teu IP | servidor **bgutil** dentro do contentor (`127.0.0.1:4416`) |

Variáveis de ambiente suportadas: `DISCORD_TOKEN`, `DISCORD_GUILD_ID` (ou `GUILD_ID`),
`YTDLP_PATH`, `FFMPEG_PATH`, `COOKIES_FILE`, `YTDLP_ARGS`, `PORT` (usada pelo Render).

`YTDLP_ARGS` aceita argumentos extra para o yt-dlp, o que permite afinar o comportamento
sem rebuild (ex.: `--extractor-args "youtube:player_client=mweb,tv"`).

### PO tokens (o que faz o `/play` funcionar no Render)

Desde 2026 o YouTube não se contenta com cookies em IPs de datacenter: exige um
**PO token** (proof-of-origin). A imagem arranca o servidor
[bgutil-ytdlp-pot-provider](https://github.com/Brainicism/bgutil-ytdlp-pot-provider)
em `127.0.0.1:4416` e instala o plugin correspondente do yt-dlp. No arranque deves ver:

```
[ OK ] PO token provider (bgutil) em 127.0.0.1:4416
```

Para confirmar que o yt-dlp o está a usar: `yt-dlp -v <url>` mostra
`PO Token Providers: bgutil:http-2.0.2 (external)`.


### Passos

1. **Git + GitHub** (o `config.json` fica de fora — contém o token):
   ```powershell
   git init
   git add .
   git commit -m "bot"
   git remote add origin https://github.com/<user>/<repo>.git
   git push -u origin main
   ```
2. No Render: **New → Blueprint** → escolhe o repositório (ele lê o `render.yaml`).
3. No serviço criado → **Environment** → `DISCORD_TOKEN` = o token do bot.
4. (Recomendado) **Secret Files** → `cookies.txt` com os cookies do YouTube
   (o Render monta em `/etc/secrets/cookies.txt`, que é o que o `render.yaml` já aponta).
   Sem isto, o YouTube costuma bloquear IPs de datacenter com "Sign in to confirm you're not a bot".
5. Deploy. Testa `https://<app>.onrender.com/health` → deve devolver
   `{"status":"ok","uptime":...}`.

### Keep-alive ("watchbot")

O plano free do Render **adormece ao fim de 15 min sem tráfego HTTP** — mesmo que o bot
esteja a tocar música. Para não morrer:

- Cria um monitor grátis no [cron-job.org](https://cron-job.org) ou [UptimeRobot](https://uptimerobot.com)
  a chamar `https://<app>.onrender.com/health` **a cada 10–14 minutos**.
- Se o processo rebentar, o Render reinicia o contentor sozinho; o ping seguinte
  também o acorda. Não é preciso um "watchdog" dentro do bot.

### Limites honestos do plano free

- **750 horas/mês** de instância (um mês tem ~730 h) — dá para um serviço sempre ligado, mas não dois.
- **512 MB de RAM e 0,1 CPU**: chega para uma música, mas arranques e `yt-dlp` podem ser lentos.
- **Disco efémero**: `bot.log` e caches desaparecem em cada deploy/restart.
- **O Render não garante UDP de saída** (a voz do Discord usa UDP). Se o bot entrar no canal
  mas ninguém ouvir nada, vê no `bot.log` a latência UDP; se ficar em `UDP 0 ms`,
  a voz está bloqueada e é melhor usar um VPS (ex.: Oracle Cloud Always Free, Fly.io) —
  o mesmo `Dockerfile` funciona lá sem alterações.

## Problemas comuns

| Problema | Solução |
|---|---|
| `/play` diz "Não consegui encontrar" | Atualiza o yt-dlp: `tools\yt-dlp.exe --update-to nightly@latest` (o `setup.ps1` já traz o nightly) |
| YouTube pede login / "Sign in to confirm you're not a bot" | Exporta os cookies do browser para `cookies.txt` (extensão *Get cookies.txt LOCALLY*) e indica o caminho em `cookiesFile` no `config.json` |
| "cookies are no longer valid" | O Google roda os cookies; exporta outra vez e substitui. Usa um perfil de browser só para isto |
| Mesmo com cookies: "Sign in to confirm you're not a bot" | IP de datacenter: confirma no log de arranque `[ OK ] PO token provider (bgutil)`. Se falhar, vê o erro do node acima dessa linha |
| Bot entra mas não se ouve nada | Confirma a permissão **Speak** no canal e vê os logs na consola |
| Comandos slash não aparecem | Preenche o `guildId` e reinicia; no cliente Discord faz `Ctrl+R` |
| "Token inválido" | Copia outra vez em Bot → Reset Token (não uses o Client Secret) |
| Bot não entra no canal | Tem de estar no mesmo servidor, ter **Connect** e tu tens de estar num canal de voz |

## Notas

- Em 2026 o yt-dlp precisa de um runtime JavaScript (**deno**) para o YouTube devolver
  os formatos de áudio. O `setup.ps1` instala `tools\deno.exe` e a imagem Docker também o traz.
- O binário do yt-dlp é o **nightly**: o YouTube muda mais depressa do que as versões estáveis.
- Em 2026 o Discord exige encriptação E2EE (DAVE) na voz; o `libdave.dll` já vem incluído
  (compilado com o patch do PR #3288, porque a release 3.20.2 do Discord.Net tem o bug).
- Usa isto apenas para uso pessoal e respeita os Termos de Serviço do YouTube.
- O `config.json` contém o token do bot — não o partilhes.
