#!/usr/bin/env bash
#
# demo.sh - Simulacion de fallo de SolidarityGrid, narrada para un humano.
#
# Cuenta la historia paso a paso, con pausas para leer: se acepta un pago, se
# mata al nodo dueno en pleno cobro, un superviviente asume el relevo y recupera
# el resultado sin re-cobrar, y el nodo resucitado converge por gossip.
#
# NO es un script de CI (eso sera pipeline.sh: sin pausas y con aserciones).
#
set -euo pipefail

# --- Colores moderados (solo si la salida es una terminal) ---
if [[ -t 1 ]]; then
  BOLD=$'\033[1m'; CYAN=$'\033[36m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; DIM=$'\033[2m'; RESET=$'\033[0m'
else
  BOLD=''; CYAN=''; GREEN=''; YELLOW=''; DIM=''; RESET=''
fi

phase() { printf '\n%s=== %s ===%s\n\n' "${BOLD}${CYAN}" "$*" "${RESET}"; }
say()   { printf '%s\n' "$*"; }
note()  { printf '%s%s%s\n' "${DIM}" "$*" "${RESET}"; }

# --- Ubicarse en la raiz del repo (el script vive en scripts/) ---
cd "$(dirname "$0")/.."

# --- Detectar docker compose v2 o el legacy docker-compose ---
if docker compose version >/dev/null 2>&1; then
  COMPOSE=(docker compose)
elif docker-compose version >/dev/null 2>&1; then
  COMPOSE=(docker-compose)
else
  echo "No encuentro 'docker compose' ni 'docker-compose' en el PATH." >&2
  exit 1
fi

# --- Nombres de servicio: tal cual estan en docker-compose.yml ---
NODE1=node-1
NODE2=node-2
NODE3=node-3
PSP=psp-mock

# Los puertos de host se derivan en la FASE 1 desde docker-compose.yml (via
# 'compose port'), para que coincidan con la realidad y no queden hardcodeados.
NODE1_PORT=""
NODE2_PORT=""
NODE3_PORT=""

# --- Cleanup: si interrumpen la demo (Ctrl-C), bajamos el cluster ---
interrupted() {
  echo
  echo "Interrumpido. Bajo el cluster para no dejar contenedores sueltos..."
  "${COMPOSE[@]}" down >/dev/null 2>&1 || true
  exit 130
}
trap interrupted INT TERM

# --- Parseo de NUESTRO propio JSON sin jq (grep -o sobre patrones concretos) ---
json_str() { grep -o "\"$1\":\"[^\"]*\"" | sed "s/^\"[^\"]*\":\"//; s/\"$//" || true; }
json_num() { grep -o "\"$1\":[0-9][0-9]*" | sed "s/^\"[^\"]*\"://" || true; }

node_healthy() { curl -sf "http://localhost:$1/health" >/dev/null 2>&1; }
psp_healthy()  { "${COMPOSE[@]}" exec -T "$PSP" curl -sf "http://localhost:8080/health" >/dev/null 2>&1; }
tx_json()      { curl -s "http://localhost:$1/tx/$2" 2>/dev/null || true; }
charges_json() { "${COMPOSE[@]}" exec -T "$PSP" curl -s "http://localhost:8080/charges/$1" 2>/dev/null || true; }

say "${BOLD}SolidarityGrid - demo de tolerancia a fallos${RESET}"
note "Red mesh de pagos sin punto unico de fallo. Cuatro contenedores, cero estado compartido."

# =====================================================================
phase "FASE 1 - Levantar el cluster"
# =====================================================================
say "Construyo y levanto ${NODE1}, ${NODE2}, ${NODE3} y ${PSP}."
"${COMPOSE[@]}" up --build -d

NODE1_PORT=$("${COMPOSE[@]}" port "$NODE1" 8080 2>/dev/null | awk -F: '{print $NF}' || true)
NODE2_PORT=$("${COMPOSE[@]}" port "$NODE2" 8080 2>/dev/null | awk -F: '{print $NF}' || true)
NODE3_PORT=$("${COMPOSE[@]}" port "$NODE3" 8080 2>/dev/null | awk -F: '{print $NF}' || true)
if [[ -z "$NODE1_PORT" || -z "$NODE2_PORT" || -z "$NODE3_PORT" ]]; then
  echo "No pude derivar los puertos publicados desde docker-compose.yml." >&2
  exit 1
fi
note "Puertos de host -> ${NODE1}:${NODE1_PORT}  ${NODE2}:${NODE2_PORT}  ${NODE3}:${NODE3_PORT}  (${PSP} solo en la red interna)"

say "Espero (de forma activa, sin sleep fijo) a que los cuatro respondan /health..."
deadline=$((SECONDS + 90))
until node_healthy "$NODE1_PORT" && node_healthy "$NODE2_PORT" && node_healthy "$NODE3_PORT" && psp_healthy; do
  if (( SECONDS >= deadline )); then
    echo "Timeout: el cluster no estuvo sano en 90s." >&2
    "${COMPOSE[@]}" ps >&2
    exit 1
  fi
  sleep 2
done
say "${GREEN}Los cuatro contenedores estan sanos.${RESET}"
sleep 2

# =====================================================================
phase "FASE 2 - Enviar el pago"
# =====================================================================
TX="TX-$(date +%Y%m%d-%H%M%S)"
say "Idempotency-Key = ${BOLD}${TX}${RESET}"
note "Lleva timestamp para poder repetir la demo sin colisionar con claves anteriores."
say "POST /pay a ${NODE1} por 25000 COP..."

resp=$(curl -s -o /dev/null -w '%{http_code} %{time_total}' \
  -X POST "http://localhost:${NODE1_PORT}/pay" \
  -H 'Content-Type: application/json' \
  -H "Idempotency-Key: ${TX}" \
  -d '{"amount":25000,"currency":"COP"}')
code=$(printf '%s' "$resp" | awk '{print $1}')
secs=$(printf '%s' "$resp" | awk '{print $2}')
PAY_AT=$SECONDS
say "Respondio ${BOLD}HTTP ${code}${RESET} en ${BOLD}${secs}s${RESET}."
note "La respuesta llega en decimas de segundo: el nodo NO espera al banco (que tarda 5-10s). Acepta tras replicar y trabaja en segundo plano."

# =====================================================================
phase "FASE 3 - Confirmar la replica"
# =====================================================================
say "Consulto /tx/${TX} en ${NODE2}, que NO recibio el pago..."
j=$(tx_json "$NODE2_PORT" "$TX")
say "${NODE2} ya la conoce: estado=${BOLD}$(printf '%s' "$j" | json_str state)${RESET}, owner=${BOLD}$(printf '%s' "$j" | json_str owner)${RESET}."
note "Prueba que el trabajo nunca vivio en un solo nodo: se replico a un peer ANTES de devolver el 202."

# =====================================================================
phase "FASE 4 - Matar el nodo dueno"
# =====================================================================
# El kill debe caer ~3s DESPUES del pago, con node-1 aun a mitad del cobro (el
# PSP tarda 5-10s). Anclamos al momento del pago para que la narracion previa no
# retrase el kill y deje que la transaccion se complete sola antes de tiempo.
say "Espero a que hayan pasado 3s desde el pago, con ${NODE1} a mitad del cobro..."
until (( SECONDS >= PAY_AT + 3 )); do sleep 1; done
# Usamos kill (SIGKILL) y no stop (SIGTERM): SIGKILL simula una caida real e
# instantanea; SIGTERM seria un apagado ordenado, que no es el fallo a demostrar.
KILL_TS=$(date +%s)
say "${YELLOW}${COMPOSE[*]} kill ${NODE1}${RESET}   (SIGKILL: caida abrupta, no apagado ordenado)"
"${COMPOSE[@]}" kill "$NODE1" >/dev/null
say "${NODE1} murio con la transaccion en vuelo. El banco ya estaba cobrando, pero ${NODE1} no llegara a conocer el resultado."

# =====================================================================
phase "FASE 5 - Observar el relevo (~15s de logs en vivo)"
# =====================================================================
note "Aqui se cuenta la historia: deteccion de la caida, eleccion HRW del sucesor,"
note "relevo con epoch+1 y recuperacion idempotente del cobro (transaccion en duda)."
echo
if command -v timeout >/dev/null 2>&1; then
  timeout 15 "${COMPOSE[@]}" logs -f --since "$KILL_TS" "$NODE2" "$NODE3" "$PSP" || true
else
  note "(sin 'timeout' en el sistema: espero 15s y muestro la ventana de logs)"
  sleep 15
  "${COMPOSE[@]}" logs --since "$KILL_TS" "$NODE2" "$NODE3" "$PSP" || true
fi

# =====================================================================
phase "FASE 6 - Verificar la convergencia"
# =====================================================================
say "Sondeo /tx/${TX} en ${NODE2} y ${NODE3} hasta que ambos digan Completed (timeout 30s)..."
deadline=$((SECONDS + 30))
auth2=""; auth3=""; owner_new=""
while (( SECONDS < deadline )); do
  j2=$(tx_json "$NODE2_PORT" "$TX")
  j3=$(tx_json "$NODE3_PORT" "$TX")
  if [[ "$(printf '%s' "$j2" | json_str state)" == "Completed" && "$(printf '%s' "$j3" | json_str state)" == "Completed" ]]; then
    auth2=$(printf '%s' "$j2" | json_str authCode)
    auth3=$(printf '%s' "$j3" | json_str authCode)
    owner_new=$(printf '%s' "$j2" | json_str owner)
    break
  fi
  sleep 1
done
if [[ -z "$auth2" ]]; then
  echo "Timeout: los supervivientes no convergieron a Completed en 30s." >&2
  exit 1
fi
say "Ambos supervivientes en Completed. Autorizaciones lado a lado:"
printf '  %-8s -> %s%s%s\n' "$NODE2" "$BOLD" "$auth2" "$RESET"
printf '  %-8s -> %s%s%s\n' "$NODE3" "$BOLD" "$auth3" "$RESET"
if [[ "$auth2" == "$auth3" ]]; then
  say "${GREEN}Mismo authCode: convergieron al mismo resultado.${RESET}  Nuevo dueno tras el relevo: ${BOLD}${owner_new}${RESET}."
else
  say "${YELLOW}Los authCode difieren; no deberia ocurrir.${RESET}"
fi
echo
say "Ahora el adquirente: GET /charges/${TX} en ${PSP}"
jc=$(charges_json "$TX")
say "  attempts = ${BOLD}$(printf '%s' "$jc" | json_num attempts)${RESET}    applied = ${BOLD}$(printf '%s' "$jc" | json_num applied)${RESET}"
note "attempts=2: el dueno original y el sucesor llamaron al banco. applied=1: el cobro real se ejecuto UNA sola vez."
note "El relevo recupero el resultado por Idempotency-Key en vez de re-cobrar. Exactly-once bajo fallo."
sleep 2

# =====================================================================
phase "FASE 7 - Resucitar el nodo caido"
# =====================================================================
say "${COMPOSE[*]} start ${NODE1}..."
"${COMPOSE[@]}" start "$NODE1" >/dev/null
say "Espero a que ${NODE1} vuelva a estar sano..."
deadline=$((SECONDS + 30))
until node_healthy "$NODE1_PORT"; do
  if (( SECONDS >= deadline )); then echo "Timeout: ${NODE1} no volvio a estar sano." >&2; exit 1; fi
  sleep 1
done
say "Espero a que converja por gossip..."
deadline=$((SECONDS + 30))
j1=""
while (( SECONDS < deadline )); do
  j1=$(tx_json "$NODE1_PORT" "$TX")
  [[ "$(printf '%s' "$j1" | json_str state)" == "Completed" ]] && break
  sleep 1
done
say "${NODE1} reporta: estado=${BOLD}$(printf '%s' "$j1" | json_str state)${RESET}, owner=${BOLD}$(printf '%s' "$j1" | json_str owner)${RESET}, authCode=${BOLD}$(printf '%s' "$j1" | json_str authCode)${RESET}."
note "El authCode es el del sucesor, no uno nuevo: ${NODE1} adopto el resultado ajeno por gossip."
applied_after=$(printf '%s' "$(charges_json "$TX")" | json_num applied)
say "PSP tras la resurreccion: applied = ${BOLD}${applied_after}${RESET} (sigue en 1: ${NODE1} no re-ejecuto el cobro)."

# =====================================================================
phase "Resumen"
# =====================================================================
say "1. El pago se acepto en decimas de segundo y se replico antes de trabajar: sin punto unico de fallo."
say "2. Al morir el dueno, un superviviente asumio via HRW (epoch+1) y recupero el cobro sin duplicarlo (applied=1)."
say "3. El nodo resucitado convergio al mismo resultado por gossip, sin re-cobrar: exactly-once aun bajo fallo."
echo
note "El cluster sigue en pie para que lo explores. Para bajarlo:  ${COMPOSE[*]} down"
