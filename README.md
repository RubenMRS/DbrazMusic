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
| `/diagnostico` | Testa o yt-dlp/PO tokens e mostra o resultado (só tu vês) |

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

## Como pôr a funcionar no PC

1. **Instalar dependências** (correr uma vez):
   ```powershell
   .\setup.ps1
   ```
   Isto descarrega o `yt-dlp` (nightly), o `ffmpeg`, o `deno` (runtime JS que o YouTube exige em 2026) e as bibliotecas nativas da voz (`natives\`).

2. **Criar o bot no Discord** → [Discord Developer Portal](https://discord.com/developers/applications):
   - Aplicação → Bot → token
   - Desativar **Message Content Intent** (não é preciso; usamos só comandos slash)
   - Ativar **Server Members Intent** e **Presence Intent** se quiseres (opcional)

3. **Configurar**: criar `config.json` (o ficheiro de exemplo não é partilhado com o token):
   ```json
   {
     "token": "SEU_TOKEN_DO_DISCORD",
     "guildId": 639128032501563403,
     "cookiesFile": "C:\\caminho\\para\\cookies.txt"
   }
   ```
   - `guildId`: o teu servidor (comandos slash aparecem imediatamente). Põe `0` para comandos globais (demoram até 1h).
   - `cookiesFile`: opcional. Exporta com a extensão **"Get cookies.txt LOCALLY"** no YouTube se o `/play` disser "Sign in to confirm you're not a bot".

4. **Arrancar**:
   ```powershell
   dotnet run -c Release
   # ou
   .\run.ps1
   ```

5. No Discord, ir ao teu servidor e usar `/play <link ou pesquisa>`.

### Variáveis de ambiente (opcionais, sob o config.json)

| Variável | O que faz |
|---|---|
| `DISCORD_TOKEN` | Token do bot (alternativa ao `config.json`) |
| `DISCORD_GUILD_ID` | ID do servidor para comandos slash instantâneos |
| `COOKIES_FILE` | Caminho para o `cookies.txt` do YouTube |
| `YTDLP_ARGS` | Argumentos extra para o yt-dlp, ex. `--extractor-args "youtube:player_client=mweb,tv"` |
| `YTDLP_COOKIES=0` | Desliga os cookies (útil se o Google já os rodou) |

### Comandos

| Comando | O que faz |
|---|---|
| `/play <link ou pesquisa>` | Entra no teu canal de voz e toca (ou adiciona à fila) |
| `/skip` | Salta para a música seguinte |
| `/pause` / `/resume` | Pausa / continua |
| `/queue` | Mostra a fila |
| `/nowplaying` | Mostra a música atual |
| `/stop` | Para tudo, limpa a fila e sai do canal |
| `/leave` | Sai do canal de voz |
| `/diagnostico` | Testa o yt-dlp e mostra o resultado (só tu vês) |

### Resolver problemas

| Problema | Solução |
|---|---|
| `/play` diz "Não consegui encontrar" | Atualiza o yt-dlp: `tools\yt-dlp.exe --update-to nightly@latest` |
| "Sign in to confirm you're not a bot" | Exporta cookies do YouTube para `cookies.txt` e indica o caminho em `cookiesFile` (ou `COOKIES_FILE`) |
| "cookies are no longer valid" | O Google rodou os cookies; exporta outra vez. Ou ponha `YTDLP_COOKIES=0` para ignorá-los |
| Comandos slash não aparecem | Põe o `guildId` e reinicia o bot; no cliente Discord faz `Ctrl+R` |
| "Token inválido" | Copia outra vez em Bot → Reset Token (não uses o Client Secret) |
| Bot não entra no canal | Tem de estar no mesmo servidor, ter **Connect** e tu tens de estar num canal de voz |

### Notas

- Em 2026 o yt-dlp precisa de um runtime JavaScript (**deno**) para o YouTube devolver os formatos de áudio. O `setup.ps1` instala `tools\deno.exe`.
- O binário do yt-dlp é o **nightly**: o YouTube muda mais depressa do que as versões estáveis.
- Em 2026 o Discord exige encriptação E2EE (DAVE) na voz; o `libdave.dll` já vem incluído.
- Usa isto apenas para uso pessoal e respeita os Termos de Serviço do YouTube.
- O `config.json` contém o token do bot — não o partilhes.
