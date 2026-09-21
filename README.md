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

Three things have to be true: the *arr apps can reach Smurfm3u, Prowlarr is pointed at the
Newznab endpoint, and Sonarr and Radarr send Smurfm3u grabs to the Smurfm3u download client
rather than to a real SABnzbd. Work through the steps in order.

### 1. Find the right address

The Settings page shows the address **your browser** used to reach the UI. That is not
always an address another container or another machine can use, so pick the one that
matches your setup:

| Where the *arr app runs | Address to use |
| --- | --- |
| Same Docker network as Smurfm3u | `smurfm3u` port `8080` — the container name and its internal port |
| Another host on your LAN | The Smurfm3u host's IP and published port, e.g. `192.168.1.10` port `8090` |

`localhost` only works if the *arr app runs directly on the Smurfm3u host, outside a
container. Inside a container, `localhost` means that container itself.

Check the address before configuring anything, from the machine or container the *arr app
runs in:

```bash
curl -sS -m 10 -o /dev/null -w "%{http_code}\n" "http://ADDRESS:PORT/api?t=caps&apikey=YOUR_API_KEY"
```

`200` means you are ready. Anything else, fix it here first — nothing below can work until
this responds.

**Behind a VPN container?** If an *arr app uses `network_mode: service:gluetun` or similar,
the VPN container's firewall blocks traffic to your LAN by default, and it *drops* that
traffic rather than refusing it — so tests hang on a spinner instead of failing. Allow your
subnet on the VPN container, then recreate it along with everything sharing its network:

```yaml
environment:
  - FIREWALL_OUTBOUND_SUBNETS=192.168.1.0/24
```

A firewall on the Smurfm3u host drops traffic the same way, with the same symptom, so check
both.

### 2. Prowlarr — add the indexer

Add a *Generic Newznab* indexer:

| Field | Value |
| --- | --- |
| URL | `http://smurfm3u:8080` — host only, no path |
| API Path | `/api` — the default, leave it |
| API Key | from Settings |
| Categories | Movies (2000), TV (5000) |

> **The URL field takes the host only.** Prowlarr joins `URL` and `API Path` together to
> build its requests, so putting `/api` in the URL field asks for `/api/api`, which is not
> a route here and returns 404.

The categories Smurfm3u advertises:

| Movies | TV |
| --- | --- |
| 2000, 2030 (SD), 2040 (HD), 2045 (UHD) | 5000, 5030 (SD), 5040 (HD), 5045 (UHD) |

### 3. Sonarr and Radarr — add the download client

Add a *SABnzbd* download client:

| Field | Value |
| --- | --- |
| Name | `Smurfm3u` |
| Host | `smurfm3u` — host only, no `http://` and no port |
| Port | `8080` |
| URL Base | *leave empty* |
| API Key | the same key |
| Category | `tv` in Sonarr, `movies` in Radarr |
| Client Priority | a worse (higher) number than your real SABnzbd, e.g. `50` |
| Tags | *leave empty* |

**Leave Tags empty.** Tags on a download client are matched against the tags on a *series or
movie*, not against the indexer. A tagged client is skipped for anything not carrying that
tag, and it also breaks step 4.

**Client Priority matters.** Sonarr and Radarr consider only their best priority tier of
download clients, so a worse number keeps Smurfm3u from being picked for your real Usenet
indexers. Without it, roughly half of those grabs are handed to Smurfm3u, which rejects them
with `This nzb did not come from Smurfm3u`.

### 4. Sonarr and Radarr — pin the indexer to the client

This is the step that stops Smurfm3u grabs reaching a real SABnzbd, and it is easy to miss
because the field is hidden by default.

1. **Settings → Indexers**, and click the Smurfm3u indexer.
2. In the dialog's bottom row of buttons, click the **gear icon** between *Delete* and
   *Test*. Advanced fields appear.
3. Change **Download Client** from `Any` to `Smurfm3u`.
4. **Save**, then repeat in Radarr.

An indexer with a download client set always uses it, ahead of priority and of the usual
round-robin. Prowlarr preserves the value when it syncs indexers, so you set it once.

Leave your other indexers on `Any` — the priority from step 3 keeps them off Smurfm3u. To
guarantee it even while your real SABnzbd is unavailable, set **Download Client** explicitly
on those indexers too.

### 5. Let Sonarr and Radarr see the finished files

Smurfm3u reports its completed folder as `/downloads/complete`, with a subfolder per
category, so a finished TV grab lands in `/downloads/complete/tv/<Release.Name>/`.

Sonarr and Radarr have to be able to read that themselves:

- **Same host** — mount the same directory into every container at the same path. Nothing
  else is needed.
- **Different host** — share the directory over SMB or NFS, mount it on the *arr host, then
  add a **Remote Path Mapping**: Host is the Smurfm3u address, Remote Path is
  `/downloads/complete/`, Local Path is wherever you mounted it.

A Remote Path Mapping only rewrites the path in the message; it cannot grant access. Without
real access, downloads finish and imports fail.

### Good to know

Both APIs answer on `/api`. A SABnzbd request always carries a `mode` parameter and a
Newznab request never does, which is what tells them apart — so neither app needs a custom
URL base. `/newznab/api` and `/sabnzbd/api` are also routed if you would rather be explicit.

Searches are text-only. Smurfm3u has no TVDB or IMDb mapping and does not claim to, so the
*arr apps match on title, year, season and episode. How well that works depends on release
naming, covered below.

### If something does not work

| Symptom | Likely cause | Go to |
| --- | --- | --- |
| Prowlarr's Test spins and never finishes | Traffic is being dropped: wrong address, VPN container firewall, or host firewall | Step 1 |
| Prowlarr's Test fails straight away | Wrong URL shape, usually `/api` in the URL field | Step 2 |
| `Incorrect user credentials` | The API key does not match | Copy it again from Settings |
| Grabs fail about half the time | Those grabs went to a real SABnzbd | Steps 3 and 4 |
| `This nzb did not come from Smurfm3u` | A real Usenet nzb was sent to Smurfm3u | Step 3, Client Priority |
| `No such item` | The playlist entry is gone, or the database was reset | Search again and re-grab |
| Downloads finish but never import | The *arr app cannot read the completed folder | Step 5 |

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

## Crashes and reboots

Nothing has to be done by hand after a crash, a `docker kill`, or a host that loses power.
The service reconciles itself at startup, before it serves a single request:

| Left behind | What happens on the next start |
| --- | --- |
| A download still marked as downloading | Put back in the queue and resumed from its partial file |
| A partial file longer than the last recorded progress | Cut back to the last checkpoint, so no unwritten tail is ever treated as downloaded |
| A playlist left mid-refresh | Marked failed, so the scheduler stops skipping it and runs it again at its next cron time |
| An incomplete folder with no download left to resume it | Removed, so abandoned partials do not accumulate |

Anything it had to put right is summarised in one line in the log:

```
Recovered from an unclean shutdown: requeued 1 download(s), reset 1 source(s), trimmed 1 partial file(s), removed 1 orphaned folder(s)
```

A resumed download continues over HTTP range requests and keeps the bytes it already had. If
the provider does not support ranges, it starts again from the beginning. Being interrupted
never counts against a download's retry budget — only a real failure does — so a machine that
reboots repeatedly cannot exhaust the retries of a download that was working fine.

Paused downloads stay paused across a restart, because that was your decision rather than an
accident.

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
