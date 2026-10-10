# Parquet Explorer

Un "SQL Server Management Studio" per file **Parquet**: un'applicazione web (solo browser, con login) che
espone ogni cartella di file parquet come una **tabella SQL**, su **cartelle locali** o su **S3**
(AWS, MinIO, Cloudflare R2, Ceph…).

![Screenshot](docs/screenshot.png)

- **C# / .NET 10 + Blazor Server**: tutta la logica gira sul server, il browser riceve solo l'interfaccia.
- **DuckDB** in-process come motore SQL: colonnare, vettoriale, multi-thread, legge parquet in streaming
  con projection/filter pushdown e partition pruning. Niente import, niente database da gestire.
- **Tailwind CSS v4** per il design (tema scuro/chiaro) e **CodeMirror 6** come editor SQL.

## Funzionalità

| Area | Cosa fa |
| --- | --- |
| Object Explorer | Albero sorgenti → tabelle → colonne (tipo, chiavi di partizione), filtro per nome tabella/colonna, refresh per sorgente |
| Cartelle = tabelle | `sorgente.cartella_sottocartella`; partizioni Hive (`year=2024/month=01`) unite nella tabella padre e disponibili come colonne; i file parquet nella root diventano tabelle singole |
| Editor SQL | Tab multipli, evidenziazione, autocompletamento su schemi/tabelle/colonne, **F5 / Ctrl+Enter**, esegue solo la **selezione** se presente (come SSMS), tab salvati nel browser |
| Risultati | Griglia virtualizzata (100k righe fluide), più result set per script, ordinamento per colonna, ispezione/copia celle, copia TSV per Excel, pannello Messages con errori e tempi |
| Script rapidi | Click destro su una tabella: *Select top 1000*, *Count*, *Describe*, *Summarize* (statistiche colonne), *File & row group*, *Script as SELECT* |
| Export | Risultato completo (senza limite di righe) in **CSV**, **Parquet** (zstd) o **JSON lines**, in streaming |
| Esecuzione | Barra di avanzamento reale, **Cancel** immediato, timeout configurabile, query parallele tra utenti |
| Cronologia | Storico query per utente, riapribile in un nuovo tab |
| Sicurezza | Login con cookie, password PBKDF2, blocco dopo N tentativi, ruoli `Admin` / sola lettura, sandbox DuckDB (vedi sotto) |

## Avvio rapido (sviluppo)

```bash
cd src/ParquetExplorer
dotnet run
```

Apri l'URL mostrato (es. `http://localhost:5063`). In ambiente *Development* esistono due utenti di prova:

| Utente | Password | Ruolo |
| --- | --- | --- |
| `admin` | `admin` | Admin (può eseguire anche CREATE/COPY…) |
| `viewer` | `viewer` | Sola lettura |

La cartella `samples/` contiene un piccolo dataset dimostrativo (ordini partizionati per anno/canale, clienti, prodotti).

> ⚠️ Gli utenti di prova sono definiti solo in `appsettings.Development.json`. In produzione nessun utente
> è configurato finché non ne aggiungi uno.

## Configurazione

Tutto sta nella sezione `ParquetExplorer` di `appsettings.json` (o variabili d'ambiente,
es. `ParquetExplorer__DataSources__0__Path=/data`).

```jsonc
{
  "ParquetExplorer": {
    "CatalogRefreshMinutes": 10,           // ri-scansione automatica delle cartelle (0 = solo manuale)
    "DataProtectionKeysPath": "/keys",     // persiste le chiavi dei cookie (sessioni valide dopo un riavvio)
    "DataSources": [
      { "Name": "local", "Type": "Local", "Path": "/mnt/datalake" },
      {
        "Name": "lake", "Type": "S3",
        "Bucket": "my-bucket", "Prefix": "warehouse/",
        "Region": "eu-west-1",
        "AccessKeyId": "AKIA...", "SecretAccessKey": "..."
        // oppure "UseCredentialChain": true  (env, profilo ~/.aws, IAM role)
      },
      {
        "Name": "minio", "Type": "S3", "Bucket": "lake",
        "Endpoint": "minio:9000", "UrlStyle": "path", "UseSsl": false,
        "AccessKeyId": "minioadmin", "SecretAccessKey": "minioadmin"
      }
    ],
    "Query": {
      "DefaultRowLimit": 1000,             // righe caricate in griglia (l'export non ha limiti)
      "MaxRowLimit": 100000,
      "Threads": 0,                        // 0 = tutti i core
      "MemoryLimit": "8GB",                // vuoto = default DuckDB (80% RAM)
      "TempDirectory": "/var/tmp/px",      // spill su disco per join/sort enormi + export
      "ExtensionDirectory": "",            // dove DuckDB installa httpfs (S3)
      "TimeoutSeconds": 300,
      "EnableObjectCache": true            // cache dei metadata parquet tra query
    },
    "Auth": {
      "SessionHours": 12,
      "MaxFailedAttempts": 5,
      "LockoutMinutes": 5,
      "Users": [
        { "Username": "mario", "DisplayName": "Mario Rossi", "PasswordHash": "AQAAAA...", "Roles": [ "Admin" ] },
        { "Username": "analista", "PasswordHash": "AQAAAA...", "Roles": [] }
      ]
    }
  }
}
```

Opzioni per sorgente: `UnionByName` (unisce schemi diversi tra file, più lento all'avvio su S3),
`RootFilesAsTables` (default `true`), `Extensions` (default `.parquet`, `.parq`, `.pq`).
Cartelle e file che iniziano con `_` o `.` (`_SUCCESS`, `_temporary`, `_delta_log`, `.crc`…) vengono ignorati.

### Creare un utente

```bash
dotnet run --project src/ParquetExplorer -- hash-password          # chiede la password senza mostrarla
dotnet run --project src/ParquetExplorer -- hash-password 'Segreta!' # oppure come argomento
```

Copia l'hash in `PasswordHash`. Gli utenti rimossi dalla configurazione perdono l'accesso al successivo caricamento di pagina.

### Come le cartelle diventano tabelle

```
/mnt/datalake/
├── products.parquet                      → local.products
├── crm/customers/part-0.parquet          → local.crm_customers   (tutti i file della cartella)
└── sales/orders/year=2024/month=01/*.parquet
                                          → local.sales_orders     (+ colonne year, month)
```

Ogni tabella è una vista DuckDB su `read_parquet(...)`: i filtri sulle colonne di partizione
saltano intere cartelle, e solo le colonne usate vengono lette dai file.

## Sicurezza

- Tutte le pagine e le API richiedono il login; cookie `HttpOnly`, `SameSite=Strict`, sessione scorrevole.
- Gli utenti senza ruolo `Admin` possono eseguire solo istruzioni di lettura
  (`SELECT`, `WITH`, `FROM`, `DESCRIBE`, `SHOW`, `SUMMARIZE`, `EXPLAIN`, `PIVOT`…).
- **Sandbox DuckDB** (vale anche per gli admin): dopo l'avvio l'accesso al file system è limitato alle
  sorgenti configurate (`allowed_directories` + `enable_external_access = false`) e la configurazione è
  bloccata (`lock_configuration`): da SQL non si possono leggere altri file del server, installare
  estensioni, cambiare impostazioni o leggere le credenziali S3.
- In produzione mettilo dietro HTTPS (reverse proxy: gli header `X-Forwarded-*` sono supportati).

## Docker

```bash
docker build -t parquet-explorer .
docker run -p 8080:8080 \
  -v /percorso/ai/parquet:/data:ro -v px-keys:/keys \
  -e ParquetExplorer__Auth__Users__0__Username=admin \
  -e ParquetExplorer__Auth__Users__0__PasswordHash='AQAAAA...' \
  -e ParquetExplorer__Auth__Users__0__Roles__0=Admin \
  parquet-explorer
```

L'immagine ha già una sorgente `data` → `/data` e pre-installa l'estensione DuckDB `httpfs` per S3.
`docker-compose.yml` mostra un esempio con cartella locale + MinIO.
Per generare un hash con Docker: `docker run --rm parquet-explorer hash-password 'Segreta!'`.

## Struttura del progetto

```
src/ParquetExplorer/
├── Program.cs                 host, autenticazione, comandi CLI (hash-password, install-extensions)
├── Options/                   modello di configurazione
├── Services/
│   ├── DuckDbEngine.cs        database DuckDB condiviso, credenziali S3, sandbox
│   ├── CatalogService.cs      scoperta cartelle → tabelle/viste, partizioni Hive
│   ├── QueryService.cs        esecuzione streaming, limite righe, cancel, progress, timeout
│   ├── SqlGuard.cs / SqlText  split degli script e controllo sola lettura
│   ├── UserStore.cs           utenti, hash password, lockout
│   └── QueryHistoryService.cs
├── Endpoints/                 /api/export, /auth/logout
├── Components/                pagine Blazor (Login, Workbench) e componenti (ObjectExplorer, ResultGrid…)
└── ClientApp/                 sorgenti front-end: Tailwind (src/app.css) + CodeMirror (src/editor.js)
tests/ParquetExplorer.Tests/   test unitari e d'integrazione (DuckDB reale su file parquet temporanei)
```

### Front-end

CSS e JS compilati sono già in `wwwroot/` (non serve Node per eseguire l'app). Se modifichi le classi
Tailwind nei componenti o l'editor:

```bash
cd src/ParquetExplorer/ClientApp
npm install
npm run build        # oppure: npm run watch:css
```

oppure `dotnet build -p:BuildClientAssets=true`.

### Test

```bash
dotnet test
```
