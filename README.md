# Smurfm3u

Presents one or more M3U playlists to the *arr stack as if they were a Usenet indexer and a
download client. Prowlarr searches it over **Newznab**; Sonarr and Radarr grab from it over
**SABnzbd**. Underneath there is no Usenet — the playlists are filtered down to video on
demand, parsed into release metadata, and downloaded over plain HTTP.

## How it fits together

```
Prowlarr ──search──► /api  (Newznab)  ──► Postgres ──► parsed VOD entries
                        │
Sonarr / Radarr ─grab──►│ t=get  ──► pseudo-.nzb (a pointer to one playlist entry)
                        │
Sonarr / Radarr ─send──► /api  (SABnzbd, mode=…) ──► queue ──► HTTP download
                                                                    │
                                                        /downloads/complete/<cat>/<release>/
```

The `.nzb` a client grabs is not a real Usenet document. It carries the playlist entry's id,
and the SABnzbd endpoint reads that id back when the client posts the file to it. That
round trip is what turns a search result into a queued download.

## Running it

```bash
docker compose up -d
```

The web UI is on <http://localhost:8090>.

On first run the app creates its schema, an `admin` account and an API key. If you did not
set them in the environment, both are generated and written to the log:

```bash
docker compose logs app | grep -E "API key|generated password"
```

Set them up front instead by creating a `.env` beside `docker-compose.yml`:

```env
POSTGRES_PASSWORD=change-me
ADMIN_USERNAME=admin
ADMIN_PASSWORD=something-long
API_KEY=your-own-api-key
HTTP_PORT=8090
TZ=Europe/London
```

These seed the database on first run only. Afterwards the UI is the source of truth.

## Connecting the *arr apps

**Prowlarr** — add a *Generic Newznab* indexer:

| Field | Value |
| --- | --- |
| URL | `http://smurfm3u:8080` |
| API Path | `/api` |
| API Key | from Settings |
| Categories | Movies (2000), TV (5000) |

**Sonarr / Radarr** — add a *SABnzbd* download client:

| Field | Value |
| --- | --- |
| Host | `smurfm3u` |
| Port | `8080` |
| URL Base | *leave empty* |
| API Key | the same key |
| Category | `tv` for Sonarr, `movies` for Radarr |

Both APIs answer on `/api`. A SABnzbd request always carries a `mode` parameter and a
Newznab request never does, which is what tells them apart — so neither app needs a custom
URL base. `/newznab/api` and `/sabnzbd/api` are also routed if you would rather be explicit.

Sonarr and Radarr must be able to read completed downloads at the path Smurfm3u reports,
which is `/downloads/complete` inside the container. Either mount the same host directory
into all three containers at that path, or set a Remote Path Mapping in the *arr app.

## Playlists

Add them under **Playlists**. Each one has:

| Option | What it does |
| --- | --- |
| Type / Location | A remote URL, or a path to a file mounted into `/playlists` |
| Enabled | Disabled playlists are ignored by search and by the scheduler |
| Quality / resolution / group tags | Appended to every release name from this playlist, e.g. `…1080p.WEB-DL-Smurfm3u`. The resolution tag also picks the Newznab subcategory |
| Simultaneous downloads | Per-playlist cap, under the global cap in Settings |
| Pause between starts | Seconds to wait before starting the next download from this playlist |
| Speed limit | Per-playlist cap in KiB/s, on top of the global limit |
| Refresh schedule | Five-field cron, in UTC. Blank means manual refresh only |
| Extra HTTP headers | One `Name: value` per line, used for the playlist fetch and for downloads |

**Force update** is the *Refresh* button on each row. A newly added playlist refreshes
immediately.

### What counts as VOD

Nothing in the M3U format marks on-demand content, so entries are scored on the signals
providers actually emit, in this order:

1. A `/live/`, `/movie/` or `/series/` segment in the URL path decides on its own.
2. A video container extension (`.mkv`, `.mp4`, …) means on demand.
3. `.m3u8` / `.ts` needs corroboration — a declared runtime, or a group name that says VOD.
4. A group name containing `live`, `24/7`, `radio`, `sport`, … means live.
5. A declared runtime, or an on-demand group name, means on demand.

Everything else is dropped. Entries that leave a playlist are retired rather than deleted,
so finished downloads keep something to point at.

### Release naming

Playlist names are written for humans browsing an app, so they are parsed and rebuilt into
the dot-separated scene form Sonarr's and Radarr's parsers handle most reliably:

| Playlist name | Release name |
| --- | --- |
| `Top Gear (2002) S01 E03` | `Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u` |
| `Pacific Rim - 2013` | `Pacific.Rim.2013.1080p.WEB-DL-Smurfm3u` |
| `[4K] Breaking Bad S05E14 - Ozymandias` | `Breaking.Bad.S05E14.1080p.WEB-DL-Smurfm3u` |

Language and quality badges (`EN -`, `|VIP|`, `[4K]`) are stripped, as are resolution,
source and codec tokens that would otherwise leak into the title. The year is kept and
placed where the *arr parsers expect it. Numeric titles such as `1917 (2019)` survive.

## Speed limits

Two mechanisms combine, and the tightest active constraint wins:

- **Manual global limit** in Settings, in KiB/s. `0` is unlimited. Sonarr and Radarr can
  change this through the SABnzbd API.
- **Scheduled windows**, each with days of the week, a start and end time, and a limit. A
  window may wrap past midnight. A window set to **0 KiB/s pauses downloading entirely**
  while it is active.

Per-playlist limits apply on top of whichever global limit is in force.

## History

- **Queue** and **History** show downloads; history rows can be retried or deleted.
- **Searches** records every query the *arr apps send, with result counts and timings, and
  can delete entries older than a chosen age.
- Both are also trimmed automatically by the retention settings. `0` means keep forever.

## Layout

| Project | What lives there |
| --- | --- |
| `src/Smurfm3u.Core` | Entities, playlist parsing, VOD classification, release naming |
| `src/Smurfm3u.Data` | EF Core `AppDbContext` and migrations |
| `src/Smurfm3u.App` | Web UI, the two APIs, the download engine and the schedulers |
| `tests/Smurfm3u.Core.Tests` | Parser tests over real-world playlist name shapes |

## Development

```bash
docker run -d --name smurfm3u-db -e POSTGRES_DB=smurfm3u -e POSTGRES_USER=smurfm3u \
  -e POSTGRES_PASSWORD=smurfm3u -p 5432:5432 postgres:17-alpine

dotnet test
dotnet run --project src/Smurfm3u.App
```

Migrations:

```bash
dotnet ef migrations add <Name> -p src/Smurfm3u.Data -s src/Smurfm3u.Data
```

They are applied automatically at startup.
