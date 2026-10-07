# Event Pipeline

Web app integral de la agenda de muxo jaleo (.NET 8 + EF Core + SQL Server): reconocimiento de
carteles de Instagram vía DeepSeek, scrape de Instagram vía Bright Data, cruce con muxojaleo.com,
calendario ICS y limpieza de duplicados. **La propia app orquesta y programa todo** (sin n8n, sin
Nextcloud, sin emails). **BD propia y dedicada** (`EventPipelineDb`), sin relación con el proyecto
legacy. Solución con tres proyectos:

- `src/EventPipeline.Core` — librería de dominio: entidades, DbContext, migraciones, servicios (reconocimiento, muxo, eventos, ICS, DeepSeek), barrera `X-DeepSeek-API-Key`.
- `src/EventPipeline.Web` — Blazor Server UI (cookie auth, español) + Hangfire (jobs/crons) + seed de arranque. Ejecuta los servicios de Core directamente, sin HTTP interno. **Es quien crea y migra la BD al arrancar** (`Database:MigrateOnStartup=true`, también en producción — BD propia).
- `src/EventPipeline.Api` — API REST pura (sin UI, sin Hangfire, sin cookie auth; no migra).

## Cómo trabajar en este repo

- **Entorno de desarrollo**: `docker compose -f docker-compose.dev.yml up -d` (SQL Server en 1434, BD `EventPipelineDb` creada por la Web en el primer arranque) + F5 en Visual Studio con el perfil "Web + API" del `EventPipeline.slnLaunch` (arranca y depura Web 5199 + Api 5200; en Desarrollo la Web reenvía `/api/*` y `/swagger*` a la Api local, como cloudflared en prod). Login `admin` / `dev-password-123` (si se olvida: borrar `dbo.AppUsers` en la BD dev y reiniciar la Web — ver README). Sin key de DeepSeek en Ajustes, los flujos con LLM fallan con mensaje claro (esperado).
- Tests en `EventPipeline.Tests` (xUnit + SQLite in-memory, sin BD real; referencian Core + Api + Web). Antes de commitear: `dotnet test EventPipeline.sln`.
- Migraciones EF: `dotnet ef migrations add <Name> --project src/EventPipeline.Core`. Las aplica la Web al arrancar (dev y prod); la Api no migra (evita competir en el arranque). Migraciones nuevas aditivas; los Ids de migración (nombres de clase) no se cambian una vez aplicadas a una BD real.
- **Nunca commitear ni pushear por iniciativa propia**: solo cuando Javier lo pida explícitamente. Commits en inglés, lowercase imperativo, **sin trailer de co-autoría** (Javier no quiere que Claude aparezca como colaborador: los commits van con su identidad local). Push directo a `main`.
- El usuario habla español: explicaciones y resúmenes en español.

## Arquitectura (decisiones — no deshacer sin motivo)

- **Dos procesos (Web + Api) sobre una librería de dominio compartida** (`EventPipeline.Core`): la WebUI ejecuta los servicios de Core directamente (sin HTTP interno) y orquesta los jobs; la Api es REST puro con la barrera de coste. Una sola réplica de cada contenedor (semáforos LLM y recurring jobs son por proceso).
- **Web UI** (`EventPipeline.Web/Components/Pages/`): Calendario (FullCalendar vía CDN + `wwwroot/js/calendarInterop.js`; la lógica de fechas está en C# en `Components/Calendar/CalendarEventMapper.cs`, testeada), Eventos, Posts (registry), Muxojaleo, Cuentas IG (CRUD), Trabajos (crons editables + historial), DeepSeek (auditoría + coste estimado), Ajustes (claves cifradas + contraseña). Páginas interactivas con `@rendermode InteractiveServerRenderMode(prerender: false)`; Login es SSR estático (antiforgery manual). Los componentes NO inyectan servicios con `AppDbContext` scoped: scope por operación (`IServiceScopeFactory`) o `IDbContextFactory` (servicios de consulta en `Features/Ui/UiQueries.cs`).
- **Auth ligera propia** (`EventPipeline.Web/Features/Auth/`): cookie + PBKDF2-SHA256 (210k iteraciones, `PasswordHasher`), un admin (`AppUsers`), cambio de contraseña forzado tras el primer login. `AuthController` (MVC) gestiona login/logout con antiforgery. Los endpoints REST de la Api NO usan la cookie.
- **Claves en `AppSettings`** (`EventPipeline.Web/Features/Settings/SettingsService`): DeepSeek y Bright Data cifradas con Data Protection (anillo de claves en `DataProtectionKeys`, en la BD; lo registra solo la Web). Los jobs y la UI las leen de ahí. La API REST mantiene la barrera `X-DeepSeek-API-Key` (`[RequireDeepSeekKey]`, en Core) para llamadores externos: nunca la guarda, solo la reenvía.
- **Jobs Hangfire** (`EventPipeline.Web/Features/Jobs/`): `ig-scrape` (0 0 * * 2,5), `muxo-sync-crosscheck` (45 0 * * *), `monthly-cleanup` (15 0 1 * *) — crons UTC, por defecto los que tenía n8n, **editables en la UI y persistidos en AppSettings** (sobreviven reinicios; `JobsService.EnsureRecurringJobsAsync` los registra al arrancar). `[DisableConcurrentExecution]` + `[AutomaticRetry(Attempts = 2)]` (barrera de coste LLM: nunca reintentos masivos). Historial propio en `JobRuns` (start/complete/fail con summary JSON), mostrado en Trabajos/Dashboard. El job de publicar calendario NO existe: el ICS se sirve on demand en `GET /api/calendar/ics`.
- **Log en vivo** (`JobActivityHub`, singleton): los jobs emiten líneas de progreso en español que se emiten por canal en proceso a los suscriptores (Dashboard) y se persisten en `JobRunLogs` (el purge del cleanup borra las de >90 días). La cola/servidor de Hangfire son configurables (`Hangfire__Queues`, `Hangfire__ServerName`) para instancias de diagnóstico.
- **Exclusión de eventos** (`EventRecords.Excluded`, migración `AddEventExclusion`): los excluidos salen del calendario/ICS (`GetAllAsync(includeExcluded: false)` por defecto) pero siguen en la lista de Eventos; el cleanup y el crosscheck los ignoran, y como su `PostId` sigue ocupado, el reconocimiento no los recrea al re-raspar el post. Se reincluyen con `SetExcludedAsync`.
- **Scrape IG vía Bright Data** (`EventPipeline.Web/Features/BrightData/BrightDataService`): trigger por cuenta → poll hasta que no esté "running" (deadline 6 min) → download → primeros N posts (default 4). Token/dataset/URL base en Ajustes. Cuentas en `IgAccounts` (sembradas con las 34 del workflow n8n; CRUD en la UI).
- **Registro `Posts` por URL**: los posts ya analizados (eventos y no-eventos) nunca vuelven al LLM. Turno global de análisis (`AnalysisTurn`, singleton) para runs concurrentes: la primera paga, las demás re-consultan. **No reintroducir dedup in-flight por hash: provocaba deadlocks**.
- **Análisis dos fases**: filtro barato `is_event` (60/chunk) + extracción solo para eventos (20/chunk; 30 desbordaba los 4096 tokens de salida y las respuestas truncadas se facturan igual). Split-and-retry si un chunk agota intentos.
- **Fechas del LLM**: `event_date` es SOLO `YYYY-MM-DD`, sin hora ni zona horaria; la hora del cartel va aparte en `event_time` ("HH:mm", solo si el cartel la dice explícitamente) y se combina en `RecognitionService.CombineDateAndTime`. **Prohibido** reintroducir horas en `event_date` o convertir fechas: `ParseEventDate` NO debe llamar a `ToUniversalTime()` (desplazaba un día los eventos, p. ej. "12 de octubre" → 11). Las fechas son granularidad de DÍA, guardadas tal cual.
- **Auditoría `DeepSeekCallLogs`**: una fila por intento HTTP al LLM; la API key nunca se guarda.
- **Esquema de la BD** (`SchemaGuardTests`): las 10 tablas y los índices únicos guardianes de concurrencia (dedup de posts/eventos/cruces) viven solo en el modelo EF — nadie más escribe en la BD. Migraciones EF (Baseline + AddWebAppTables) son la única fuente del esquema.
- **Hangfire schema `HangFire`**: `PrepareSchemaIfNecessary=true` al arrancar — crea idempotentemente solo sus tablas dentro del schema `HangFire`.
- **Data Protection en BD**: el anillo de claves sobrevive a recrear los contenedores (el anillo y el volumen SQL son persistentes). Si se restaura un backup antiguo de la BD, los secretos cifrados quedan ilegibles → reintroducirlos en Ajustes. En Linux el anillo reposa sin cifrar (mismo nivel de confianza que la BD).

## Tablas (BD propia `EventPipelineDb`)

- **Todas propias de esta app** (migraciones `20260927203458_Baseline`, `20260927233939_AddWebAppTables` y `AddJobRunLogs`): `EventRecords`, `MuxoEvents`, `CrossMatches`, `Posts`, `DeepSeekCallLogs`, `IgAccounts`, `AppUsers`, `AppSettings`, `JobRuns`, `JobRunLogs`, `DataProtectionKeys`.
- **Hangfire**: schema `HangFire` (tablas de Hangfire, fuera del modelo EF).

## Despliegue

- Docker + cloudflared (docker-compose: servicios `sqlserver`, `web`, `api`, `cloudflared`). `.env` de prod con `SA_PASSWORD`, `INITIAL_ADMIN_PASSWORD` (contraseña inicial del admin, se fuerza cambio en el primer login) y `CLOUDFLARED_TUNNEL_TOKEN`. La Web crea y migra la BD al arrancar.
- **Enrutado cloudflared por path** (dashboard de Cloudflare): `/` → web (8083); `/api/*` y `/swagger*` → api (8082). El enlace ICS relativo depende de esto.
- **Antes de desplegar: desactivar los 4 workflows n8n de producción** (`https://n8n.davru.link/`) — duplicarían los 3 jobs nuevos.
- Tras el primer login: configurar key DeepSeek y token Bright Data en Ajustes (estaban en las credenciales de n8n).
