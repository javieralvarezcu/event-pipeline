# Event Pipeline — agenda de muxo jaleo

Aplicación web integral (.NET 8 + EF Core + SQL Server) para la agenda de
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
 └─ Jobs Hangfire (mismo proceso, SQL Server)
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

La app usa su **propia base de datos** (`EventPipelineDb`) en un SQL Server del
docker-compose, sin relación con el proyecto legacy. El esquema lo crean y mantienen
las migraciones EF: la Web las aplica al arrancar (`Database:MigrateOnStartup=true`,
también en producción — es BD propia, sin conflictos; la Api no migra para no competir
en el arranque). Hangfire usa su schema `HangFire` dentro de la misma BD.

Las 11 tablas (`EventRecords`, `MuxoEvents`, `CrossMatches`, `Posts`,
`DeepSeekCallLogs`, `IgAccounts`, `AppUsers`, `AppSettings`, `JobRuns`,
`JobRunLogs`, `DataProtectionKeys`) y sus índices únicos (guardianes de
concurrencia del dedup) están cubiertos por `SchemaGuardTests`.

## Configuración

| Variable | Necesaria | Uso |
| --- | --- | --- |
| `ConnectionStrings__DefaultConnection` | sí | SQL Server del docker-compose (BD propia `EventPipelineDb`) |
| `Auth__AdminUsername` | no (default `admin`) | Usuario administrador de la UI, sembrado en el primer arranque |
| `Auth__InitialAdminPassword` | dev: no / prod: sí | Contraseña inicial (se fuerza el cambio en el primer login; en dev, si falta, se genera y se loguea una vez) |
| `Database__MigrateOnStartup` | no (default `false`) | `true` en la Web (dev y prod): crea y migra la BD propia. La Api nunca migra |

El resto (API key de DeepSeek, token de Bright Data, URL/dataset de Bright Data, posts
por cuenta, crons) se configura **desde la página Ajustes** y queda cifrado en la BD.

## Desarrollo (con Docker + Visual Studio)

La única dependencia externa es SQL Server, que se levanta en Docker. Los dos
proyectos se ejecutan y **depuran con F5 desde Visual Studio** contra ese contenedor.

```bash
# 1. Levantar SQL Server de desarrollo (puerto 1434, datos persistentes en volumen)
docker compose -f docker-compose.dev.yml up -d

# 2. Abrir EventPipeline.sln en Visual Studio y pulsar F5 con el perfil
#    "Web + API" del desplegable junto al botón de inicio (definido en el
#    EventPipeline.slnLaunch compartido del repo): arranca y depura los dos
#    proyectos a la vez — Web en http://localhost:5199 (login) y Api en
#    http://localhost:5200 (Swagger en /swagger).
#    En Desarrollo la Web reenvía /api/* y /swagger* a la Api local, así los
#    enlaces relativos (p. ej. el ICS) funcionan igual que en producción.
```

El primer arranque de la Web lo deja todo listo él solo: crea la BD `EventPipelineDb`
en el contenedor, aplica las migraciones EF (todo el esquema), crea el schema de
Hangfire y siembra datos de prueba:

- **Login:** `admin` / `dev-password-123` (se fuerza el cambio en el primer login).
- **¿Se te ha olvidado la contraseña del admin en dev?** Borra el usuario de la BD de
  desarrollo y reinicia la Web (el seed lo recrea):

  ```bash
  docker exec -it eventpipeline-dev-sql /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P Your_password123 -C -d EventPipelineDb \
    -Q "DELETE FROM dbo.AppUsers;"
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

Para reiniciar la BD de desarrollo desde cero: `docker compose -f docker-compose.dev.yml down -v` y
vuelve a levantarla (el volumen se borra y el primer arranque la recrea).
Health check en `/healthz` (Api). El SQL de la BD dev se inspecciona con:

```bash
docker exec -it eventpipeline-dev-sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P Your_password123 -C -d EventPipelineDb
```

## Despliegue

1. **Desactivar los 4 workflows de n8n en producción** (`https://n8n.davru.link/`)
   antes de desplegar: los jobs nuevos cubren 3 de ellos y duplicarían ejecuciones;
   el de «publicar calendario» desaparece (el ICS se sirve on demand).
2. Definir en el `.env` del servidor: `SA_PASSWORD` (contraseña del SQL Server del
   compose), `INITIAL_ADMIN_PASSWORD` (y opcionalmente `ADMIN_USERNAME`) y
   `CLOUDFLARED_TUNNEL_TOKEN`. `docker compose up -d --build` arranca los cuatro
   servicios: `sqlserver` (BD propia, volumen persistente), `web` (UI + Hangfire;
   crea y migra la BD al arrancar), `api` (REST) y `cloudflared`.
3. **Configurar el enrutado por path en el túnel cloudflared** (dashboard de
   Cloudflare): `/` y el resto → `web` (puerto 8083); `/api/*` y `/swagger*` →
   `api` (puerto 8082). Así la UI y el enlace ICS relativo `/api/calendar/ics`
   funcionan en el mismo dominio.
4. Primer login → cambiar la contraseña → configurar en **Ajustes** la API key de
   DeepSeek y el token de Bright Data (se recuperan de las credenciales que tenía n8n).
5. Verificar en **Trabajos** los próximos disparos de los 3 jobs.

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
