# SolidarityGrid — Contexto del proyecto

Prueba de concepto de una red mesh de procesamiento de pagos sin punto único de fallo.
Prueba técnica para el rol de Desarrollador de Plataforma. Presupuesto: 1 día.

## Regla de oro

Este es un PoC evaluado por **simplicidad y criterio**, no por completitud.
El enunciado dice literalmente: *"¿Es la solución innecesariamente compleja o elegantemente simple?"*.

Ante cualquier duda entre dos caminos, toma el más simple y deja el otro anotado como
trabajo futuro en el README. Menos código bien argumentado gana.

## Decisiones ya tomadas (NO reabrir)

| Decisión | Elección | Por qué |
|---|---|---|
| Estado compartido | Ninguno | Una DB compartida reintroduce el SPOF que el reto pide eliminar |
| Persistencia | Solo memoria | La durabilidad viene de la replicación 3x, no del disco |
| Consenso | Ninguno (CRDT + HRW) | Raft es sobre-ingeniería para 3 nodos y un PoC de 1 día |
| Transporte norte-sur | REST | El enunciado pide `POST /pay` |
| Transporte este-oeste | gRPC unario, tras `IPeerTransport` | Streaming es riesgo de calendario innecesario |
| Modelo CAP | AP | Preferimos disponibilidad; la seguridad la da el sink idempotente |

## Anti-objetivos

No introducir, bajo ninguna circunstancia y sin preguntar primero:

- MediatR, AutoMapper, Clean Architecture en capas, patrón Repository sobre diccionarios
- Cualquier broker externo (RabbitMQ, Kafka, Redis) — está prohibido por el enunciado
- Base de datos compartida entre nodos
- Raft, Paxos, o cualquier librería de consenso
- Streams gRPC bidireccionales
- Interfaces con una sola implementación (excepto `IPeerTransport`, que tendrá dos)

## Arquitectura

Cuatro contenedores: `node-1`, `node-2`, `node-3` (idénticos) y `psp-mock`.

```
src/SolidarityGrid.Node/
  Domain/          Modelo puro, sin dependencias de infraestructura
  Mesh/            Membresía, detección de fallos, transporte
  Processing/      Orquestación del pago y relevo
  Api/             Minimal APIs
src/SolidarityGrid.Psp/    Adquirente simulado
tests/SolidarityGrid.Node.Tests/
proto/mesh.proto
scripts/           demo.sh, pipeline.sh
```

## El PSP simulado

Modela un adquirente externo real. **Aquí vive el delay y la deduplicación.**

- `POST /charge` con header `Idempotency-Key`. Duerme 5-10s y devuelve un código de
  autorización. Si la clave ya fue vista, devuelve el **resultado cacheado** sin volver
  a cobrar y sin volver a dormir.
- `GET /charges/{key}` devuelve `{ attempts, applied, authCode }`.

`attempts` cuenta llamadas recibidas; `applied` cuenta cobros reales. La demo prueba
exactly-once mostrando `attempts: 2, applied: 1`.

Escenario clave: el nodo dueño muere *mientras el PSP procesa*. El cobro se aplicó, pero
el dueño murió antes de conocer el resultado (transacción en duda). El nodo que releva no
re-cobra: **recupera** el resultado vía la misma idempotency key.

## Modelo de dominio

```csharp
enum TxState { Received, Processing, Completed, Failed }

record LedgerEntry(
    string TxId,        // = Idempotency-Key del cliente
    TxState State,
    string Owner,       // nodeId
    int Epoch,          // fencing token, monotónico
    decimal Amount,
    string? AuthCode);  // solo en Completed
```

Rangos para el merge: `Received=0`, `Processing=1`, `Completed=2`, `Failed=2`.

## Reglas del protocolo

1. **Recepción.** El nodo que recibe `POST /pay` es dueño inicial, `Epoch = 1`.
2. **Réplica antes de trabajar.** Responde `202 Accepted` solo tras confirmar réplica en
   al menos un peer. Si no hay peers vivos, responde `503`.
3. **Latido.** Cada 1s cada nodo empuja a todos sus peers: su identidad + el digest de las
   entradas que posee. Con N=3, full-mesh push. La interfaz permite fanout-k si N crece.
4. **Detección.** Sin latido en 3s → `Suspect`. En 5s → `Dead`.
5. **Relevo.** Al detectar entradas en `Processing` de un nodo `Dead`, cada nodo vivo
   calcula `HRW(txId, nodosVivos)`. Solo el ganador asume: incrementa `Epoch`, se pone
   como `Owner`, y llama al PSP con la misma idempotency key.
6. **Fencing.** Si un nodo revive creyendo ser dueño, su `Epoch` menor pierde en el merge.
   Debe abortar el trabajo en vuelo al detectar un epoch superior.
7. **Convergencia.** `Completed` es absorbente e inmutable.

### Merge (función pura, en `Domain/`)

```
Merge(local, incoming):
  rango mayor gana
  empate en Processing  -> mayor Epoch; si empatan, menor Owner lexicográfico
  empate en terminal    -> Completed le gana a Failed
  empate exacto         -> conserva local
```

Debe ser **conmutativo, asociativo e idempotente**. Los tests unitarios verifican
explícitamente esas tres propiedades — es el núcleo de la corrección del sistema.

## Trampas técnicas conocidas

- **`string.GetHashCode()` NO es determinista entre procesos en .NET Core** (está
  aleatorizado por proceso). Usar SHA256 truncado para el HRW o los nodos calcularán
  sucesores distintos y el relevo se romperá de forma silenciosa.
- **No confiar en relojes sincronizados.** La expiración de lease se evalúa contra el
  último latido recibido *localmente*, nunca contra un timestamp del peer.
- **h2c:** Kestrel necesita dos endpoints explícitos — `Http1` en 8080 (REST) y `Http2`
  en 8081 (gRPC). Sin TLS no se pueden multiplexar en el mismo puerto.
- **Deadlines gRPC obligatorios:** 500ms en toda llamada de gossip. Sin esto, un nodo
  muerto congela el broadcaster.
- **`RpcException` con `StatusCode.Unavailable`** se captura y alimenta al detector de
  fallos. Nunca se deja burbujear.

## Configuración

Por variables de entorno, con Options pattern:

```
NODE_ID=node-1
PEERS=node-2,node-3
PSP_URL=http://psp-mock:8080
TRANSPORT=grpc          # grpc | http
HEARTBEAT_MS=1000
SUSPECT_MS=3000
DEAD_MS=5000
```

`TRANSPORT=http` es la red de seguridad: si gRPC falla, el sistema sigue operativo.

## Estilo

- Logs en español, narrativos, prefijados con el nodo. Deben contar una historia:
  ```
  [node-2] node-1 dejó de responder (sin latido hace 5.2s). Marcado como caído.
  [node-2] Asumo TX-99 (epoch 1 -> 2). node-1 la aceptó pero no la terminó.
  [node-2] TX-99 completada. Autorización AUTH-4F2A.
  ```
- Comentarios solo para explicar **por qué**, nunca **qué**. Si el código necesita que le
  expliquen qué hace, reescribe el código.
- Sin emojis en código, logs o README.
- Nombres de dominio en inglés, prosa en español.

## Disciplina de commits

Un commit al cerrar cada bloque de trabajo, con mensaje descriptivo real. No acumular.
El historial es parte de la entrega: debe verse como trabajo incremental de un ingeniero.

**Atribución: ninguna.** No agregues líneas `Co-Authored-By`, ni `Generated with Claude
Code`, ni ninguna otra referencia a Claude o a herramientas de IA en mensajes de commit,
descripciones de PR, o cualquier metadato de git. El mensaje termina en la última línea
de contenido técnico.

Formato: conventional commits en inglés, imperativo, con cuerpo solo cuando aporte
contexto que el diff no explique.

```
feat: add HRW successor election for orphaned transactions

Uses SHA256 instead of GetHashCode because the latter is
randomized per process in .NET Core and would break determinism
across nodes.
```

## Cierre de cada bloque

Al terminar un bloque, explícame en prosa qué construiste y por qué tomaste cada decisión,
para que yo escriba esa sección del README con mis propias palabras. No escribas tú el
README hasta el bloque final.
