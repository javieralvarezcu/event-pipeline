# Event Pipeline — agenda de muxo jaleo

Aplicación web integral (.NET 8 + EF Core + SQLite) para la agenda de
[muxojaleo.com](https://muxojaleo.com): reconoce carteles de Instagram con DeepSeek,
raspa y cruza eventos con muxojaleo.com y sirve el calendario en ICS. **Todo se
gestiona desde una web UI propia** (Blazor Server): sin n8n, sin Nextcloud, sin emails.

## Características

- **Reconocimiento de posts de Instagram con DeepSeek** en dos fases (filtro barato
  `is_event` + extracción solo para eventos). Cada post analizado (evento o no) queda
  registrado por URL y no vuelve a pasar por el LLM.
- **Scrape de Instagram vía Bright Data**: el job dispara un snapshot por cuenta,
  espera a que termine y descarga los posts recientes (por defecto 4 por cuenta).
- **Scrape y cruce con muxojaleo.com**: el scrape es puro HTTP; el cruce lo decide el
  LLM y guarda coincidencias 1:1.
- **Calendario ICS** servido on demand en `GET /api/calendar/ics` (por defecto hoy →
  +90 días), listo para suscribirse desde cualquier app de calendario.
- **Limpieza mensual de duplicados** con el LLM (protege al evento «keeper» de cada grupo).
- **Web UI en español** (Blazor Server): calendario FullCalendar, eventos, registro de
  posts, eventos de muxojaleo, cuentas de Instagram, trabajos programados, auditoría de
  llamadas a DeepSeek (con coste estimado) y ajustes.
- **Ejecuciones programadas con Hangfire** en el propio proceso: crons editables desde
  la UI, persistidos en base de datos, con historial de cada ejecución (resumen/error).
- **Log en tiempo real** en el Inicio: los trabajos emiten su progreso (scrape,
  reconocimiento, cruce…) y se ve en vivo mientras se ejecutan (persistido en
  `JobRunLogs`).
- **Excluir eventos del calendario**: un evento se puede excluir (desde su modal o
  desde la lista de Eventos); desaparece del calendario y del ICS pero sigue en la
  lista (badge "excluido"), y no vuelve aunque se vuelva a raspar su post. El
  cleanup y el crosscheck lo ignoran; se puede reincluir en cualquier momento.
- **Autenticación propia ligera** (cookie + PBKDF2) para la UI de administración.
- **Claves guardadas cifradas** (Data Protection con anillo de claves en la BD):
  la API key de DeepSeek y el token de Bright Data se gestionan desde Ajustes y las
  usan los trabajos programados.

## Arquitectura

Solución con **tres proyectos** y una librería de dominio compartida:

```
src/EventPipeline.Core   (librería)   Entidades, DbContext, migraciones, servicios de dominio
                          (reconocimiento, muxo, eventos, ICS, DeepSeek), barrera X-DeepSeek-API-Key
src/EventPipeline.Web    (web app)    Blazor Server UI (cookie auth, español)
                                       + Hangfire (jobs y crons) + seed de arranque
src/EventPipeline.Api    (web app)    API REST pura (sin UI, sin Hangfire, sin auth de cookie)
tests/EventPipeline.Tests              xUnit + SQLite en memoria
```

La **WebUI ejecuta los servicios de Core directamente** en su proceso (sin HTTP interno)
y es quien orquesta todo:

```
EventPipeline.Web (un contenedor)
 ├─ Páginas: Calendario · Eventos · Posts · Muxojaleo · Cuentas IG · Trabajos · DeepSeek · Ajustes
 └─ Jobs Hangfire (mismo proceso, almacenamiento en memoria)
     ├─ ig-scrape             Bright Data → reconocimiento
     ├─ muxo-sync-crosscheck  scrape muxojaleo.com → cruce LLM
     └─ monthly-cleanup       limpieza de duplicados del mes anterior

EventPipeline.Api (otro contenedor, documentada en /swagger)
 ├─ GETs públicos (/api/events, /api/muxo, /api/calendar/ics, /healthz)
 └─ mutaciones con header X-DeepSeek-API-Key (barrera de coste para llamadores REST)

Producción: mismo dominio con enrutado por path en cloudflared
 (/ → Web, /api/* y /swagger → Api), así el enlace ICS relativo sigue funcionando.
```

### Trabajos programados (crons por defecto, UTC — los que usaba n8n)

| Job | Cron | Qué hace |
| --- | --- | --- |
| Scrape IG + reconocimiento | `0 0 * * 2,5` (mar/vie 00:00) | Snapshot Bright Data por cuenta habilitada → primeros N posts → reconocimiento |
| Muxo sync + crosscheck | `45 0 * * *` (diario 00:45) | Scrape de muxojaleo.com (2 meses) → cruce con el LLM |
| Cleanup mensual | `15 0 1 * *` (día 1, 00:15) | Elimina duplicados del mes anterior |

Los crons se editan en la página **Trabajos** y se guardan en la tabla `AppSettings`
(sobreviven a reinicios). «Ejecutar ahora» encola una ejecución manual.

## Base de datos (propia y dedicada)

La app usa su **propia base de datos**: un fichero SQLite (`/data/eventpipeline.db`
en producción, en el volumen `data` que comparten `web` y `api`), sin relación con el
proyecto legacy. Va en modo WAL, así la Api lee mientras la Web escribe. El esquema lo
crean y mantienen las migraciones EF: la Web las aplica al arrancar
(`Database:MigrateOnStartup=true`, también en producción — es BD propia, sin
conflictos; la Api no migra para no competir en el arranque).

Hangfire usa almacenamiento **en memoria**: los crons se registran en cada arranque
desde `AppSettings` y el historial vive en `JobRuns`/`JobRunLogs`. Un disparo que caiga
con el contenedor parado no se recupera; espera al siguiente.

Hasta el 24-11-2026 la BD era un SQL Server Express. Para copiar sus datos a SQLite
está `tools/EventPipeline.SqlServerToSqlite` (ver el comentario de su `Program.cs`).

Las 11 tablas (`EventRecords`, `MuxoEvents`, `CrossMatches`, `Posts`,
`DeepSeekCallLogs`, `IgAccounts`, `AppUsers`, `AppSettings`, `JobRuns`,
`JobRunLogs`, `DataProtectionKeys`) y sus índices únicos (guardianes de
concurrencia del dedup) están cubiertos por `SchemaGuardTests`.

## Configuración

| Variable | Necesaria | Uso |
| --- | --- | --- |
| `ConnectionStrings__DefaultConnection` | sí | Fichero SQLite, p. ej. `Data Source=/data/eventpipeline.db` |
| `Auth__AdminUsername` | no (default `admin`) | Usuario administrador de la UI, sembrado en el primer arranque |
| `Auth__InitialAdminPassword` | dev: no / prod: sí | Contraseña inicial (se fuerza el cambio en el primer login; en dev, si falta, se genera y se loguea una vez) |
| `Database__MigrateOnStartup` | no (default `false`) | `true` en la Web (dev y prod): crea y migra la BD propia. La Api nunca migra |

El resto (API key de DeepSeek, token de Bright Data, URL/dataset de Bright Data, posts
por cuenta, crons) se configura **desde la página Ajustes** y queda cifrado en la BD.

## Desarrollo (con Visual Studio)

No hay dependencias externas: la BD de desarrollo es el fichero SQLite
`eventpipeline-dev.db` en la raíz del repo (ignorado por git), compartido por los dos
proyectos, que se ejecutan y **depuran con F5 desde Visual Studio**.

```bash
# Abrir EventPipeline.sln en Visual Studio y pulsar F5 con el perfil
#    "Web + API" del desplegable junto al botón de inicio (definido en el
#    EventPipeline.slnLaunch compartido del repo): arranca y depura los dos
#    proyectos a la vez — Web en http://localhost:5199 (login) y Api en
#    http://localhost:5200 (Swagger en /swagger).
#    En Desarrollo la Web reenvía /api/* y /swagger* a la Api local, así los
#    enlaces relativos (p. ej. el ICS) funcionan igual que en producción.
```

El primer arranque de la Web lo deja todo listo él solo: crea el fichero de la BD,
aplica las migraciones EF (todo el esquema) y siembra datos de prueba:

- **Login:** `admin` / `dev-password-123` (se fuerza el cambio en el primer login).
- **¿Se te ha olvidado la contraseña del admin en dev?** Borra el usuario de la BD de
  desarrollo y reinicia la Web (el seed lo recrea):

  ```bash
  sqlite3 eventpipeline-dev.db "DELETE FROM AppUsers;"
  ```

- **Datos sembrados:** las 34 cuentas de Instagram, ajustes por defecto (crons, Bright Data).
- **Claves:** para probar flujos con LLM, mete una key de DeepSeek (y un token de
  Bright Data para el scrape de IG) en la página **Ajustes**.
- Sin key, los jobs/acciones con LLM fallan con un mensaje claro en **Trabajos** —
  comportamiento normal y esperado.

Otras utilidades:

```bash
dotnet restore EventPipeline.sln
dotnet test EventPipeline.sln              # xUnit + SQLite en memoria, sin BD real
dotnet run --project src/EventPipeline.Web  # alternativa a F5 para la UI
dotnet run --project src/EventPipeline.Api  # solo la API REST (puerto 5200)
# Migraciones EF (viven en Core):
dotnet ef migrations add <Nombre> --project src/EventPipeline.Core
```

Para reiniciar la BD de desarrollo desde cero: para la Web y la Api y borra
`eventpipeline-dev.db*` (el primer arranque la recrea). Health check en `/healthz`
(Api). La BD dev se inspecciona con `sqlite3 eventpipeline-dev.db`.

## Despliegue

1. Definir en el `.env` del servidor: `INITIAL_ADMIN_PASSWORD` (y opcionalmente
   `ADMIN_USERNAME`) y `TUNNEL_TOKEN`. `docker compose up -d --build` arranca los tres
   servicios: `web` (UI + Hangfire; crea y migra la BD al arrancar), `api` (REST) y
   `cloudflared`. La BD vive en el volumen `data`. Para copiarla en caliente usa la API
   de backup de SQLite (`sqlite3 eventpipeline.db ".backup copia.db"`), no copies el
   fichero sin más.
2. **Configurar el enrutado por path en el túnel cloudflared** (dashboard de
   Cloudflare): `/` y el resto → `http://web:8080`; `/api/*` y `/swagger*` →
   `http://api:8080`. Así la UI y el enlace ICS relativo `/api/calendar/ics`
   funcionan en el mismo dominio.
3. Primer login → cambiar la contraseña → configurar en **Ajustes** la API key de
   DeepSeek y el token de Bright Data (se recuperan de las credenciales que tenía n8n).
4. Verificar en **Trabajos** los próximos disparos de los 3 jobs.

Notas:
- **Una sola réplica** de los contenedores: Hangfire y los semáforos del LLM
  (`AnalysisTurn`, rate-limit) son por proceso; dos réplicas duplicarían los crons.
- El anillo de claves de Data Protection vive en la BD (sobrevive a recrear los
  contenedores). Si se restaura un backup antiguo de la BD, los secretos cifrados
  quedan ilegibles → reintroducirlos en Ajustes.
- La BD de producción **empieza vacía**: el histórico del legacy se queda en la BD
  antigua (que ya nadie de esta app toca). El calendario se va llenando con los
  scrapeos programados.

## Tests

`dotnet test EventPipeline.sln` — 160 tests: reconocimiento, DeepSeek (HTTP simulado),
muxo scraper/sync/crosscheck, cleanup, CRUD, recurrencias, ICS, guardianes de esquema,
settings cifrados, auth, Bright Data, jobs/Hangfire y mapper del calendario.
