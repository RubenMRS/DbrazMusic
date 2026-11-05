#!/bin/sh
# Arranque da imagem: sobe o gerador de PO tokens do yt-dlp e depois o bot.
# Sem isto, o YouTube responde "Sign in to confirm you're not a bot" a IPs de datacenter.
set -e

POT_PORT=4416

node /opt/bgutil/build/main.js --host 127.0.0.1 --port "$POT_PORT" &
POT_PID=$!

ok=0
i=0
while [ "$i" -lt 40 ]; do
    if curl -fsS -m 2 "http://127.0.0.1:$POT_PORT/ping" >/dev/null 2>&1; then
        echo "[ OK ] PO token provider (bgutil) em 127.0.0.1:$POT_PORT"
        ok=1
        break
    fi
    if ! kill -0 "$POT_PID" 2>/dev/null; then
        echo "[WARN] O PO token provider terminou - o /play pode falhar com 'not a bot'."
        ldd /usr/local/bin/node 2>/dev/null | grep "not found" || true
        break
    fi
    i=$((i + 1))
    sleep 0.5
done

if [ "$ok" -ne 1 ] && kill -0 "$POT_PID" 2>/dev/null; then
    echo "[WARN] O PO token provider não respondeu em 20 s - o bot arranca na mesma."
fi

exec dotnet MusicBot.dll
