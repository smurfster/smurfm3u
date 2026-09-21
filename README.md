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
docker compose logs smurfm3uapp | grep -E "API key|generated password"
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

The account and API key settings seed the database on first run only. Afterwards the UI is
the source of truth.

### Where the data lives

Downloads land on the host, under `./data/downloads` beside `docker-compose.yml` unless you
say otherwise. Everything else lives in Docker volumes:

| What | Where | Holds |
| --- | --- | --- |
| `DOWNLOADS_PATH` | `./data/downloads` on the host | Completed and in-progress downloads |
| `smurfm3u-db-data` | Docker volume | The database |
| `smurfm3uapp-config` | Docker volume | Data protection keys, so a rebuild does not sign everyone out |

Point the downloads somewhere else — another disk, a NAS mount — with a `.env` entry:

```env
DOWNLOADS_PATH=/mnt/media/smurfm3u
```

A relative path must start with `./` or `../`, which is Compose's rule rather than ours.
Only the host side moves: inside the container it stays at `/downloads`, because that is
what the app is configured with and what it reports to Sonarr and Radarr.

To add local `.m3u` files as a playlist source, uncomment the `/playlists` line in
`docker-compose.yml` and set `PLAYLISTS_PATH` to the directory holding them. Remote playlist
URLs need nothing mounted.

That folder is where the **Browse** button on a local playlist opens, but it is not a
boundary. Every field that takes a path has a picker, and each browses wherever the container
can read, because the box beside it already accepts any path typed into it. Change where the
playlist picker opens with **Playlist directory** under Settings → Downloads.

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

Sonarr and Radarr have to be able to read that themselves. There are two parts to this, and
they are easy to confuse: the files must be **reachable**, and the path must **match**.

**Reachable** — if the *arr app runs on the same host, mount the same directory into every
container. If it runs elsewhere, share the directory over SMB or NFS and mount it there.
Nothing below substitutes for this: no amount of path rewriting grants access to files an
app cannot open.

**Matching** — if that mount lands somewhere other than `/downloads/complete`, tell Smurfm3u
under **Settings → Path mappings**:

| Our path | As Sonarr and Radarr see it |
| --- | --- |
| `/downloads/complete` | `/mnt/smurfm3u/complete` |

Every path handed to a client is then rewritten, so a finished grab is reported at
`/mnt/smurfm3u/complete/tv/<Release.Name>/` and imports without either app needing its own
Remote Path Mapping. Where the files are actually written never changes.

Add a row per location that differs. The longest match wins, so a mapping for a subfolder
beats one for its parent, and a path only matches on a whole folder name — `/downloads` will
not match `/downloads-old`. If the target is a Windows path the separators follow it, so
`D:\media\complete` yields `D:\media\complete\tv\Show`.

You can still use each app's own Remote Path Mapping instead if you prefer; doing it here
just means configuring it once rather than in every app.

### Good to know

Both APIs answer on `/api`. A SABnzbd request always carries a `mode` parameter and a
Newznab request never does, which is what tells them apart — so neither app needs a custom
URL base. `/newznab/api` and `/sabnzbd/api` are also routed if you would rather be explicit.

An empty query is a feed of the newest entries rather than a result set to be walked. Its
size is capped by **Browse feed size** under Settings (100 by default), because Prowlarr
keeps asking for the next page until a short one comes back: uncapped, one RSS sync costs a
request every two seconds until it hits its own thirty-page ceiling. Set it to 0 to remove
the cap. A real query is unaffected and pages as far as the client wants.

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
| Downloads finish but never import | The *arr app cannot reach the completed folder, or sees it at a different path | Step 5 |

## Playlists

Add them under **Playlists**. Each one has:

| Option | What it does |
| --- | --- |
| Type / Location | A remote URL, or a local file. **Browse** lists the playlist directory so the path does not have to be typed |
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

### Searching

Every word of a query has to appear in an entry title. That keeps "top gear" from matching
"Gear Top", but it also means one word the playlist does not use sinks the whole query:
searching for "Mark Rober's CrunchLabs" finds nothing when the playlist calls the show
"Camp CrunchLabs".

So when nothing contains every word, the search falls back to the entries that match best
rather than answering empty. Each word scores its own length, and only the best-scoring
entries come back. For the query above:

| Entry | Words it has | Score |
| --- | --- | --- |
| `camp crunchlabs` | crunchlabs | 10 |
| `mark robers revengineers` | mark, robers | 10 |
| `top gear america` | none | dropped |

Length stands in for rarity, so one long distinctive word counts for as much as two shorter
ones without needing statistics over the whole playlist. Entries matching nothing are never
returned, and a query that matches in full never reaches this stage, so nothing that worked
before becomes less precise.

Turn it off under **Settings → Search results** if you would rather a query either match in
full or return nothing.

Punctuation is already ignored on both sides, so "Mark Rober's CrunchLabs" and
"Mark Robers CrunchLabs" are the same query. Both are reduced to lowercase words with
apostrophes removed and dotted acronyms welded back together, which is also how titles are
indexed.

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

## Searching and downloading by hand

**Search** in the sidebar queries the same index Prowlarr does, without going through the
*arr apps at all. Type a title, narrow it to TV or Movies, optionally give a season and
episode, and every match comes back with a **Download** button.

Downloading from here queues the entry exactly as a grab from Sonarr or Radarr would,
category included, so the file lands in the same place and can still be imported afterwards.
The row says **Queued** once it is in, and the Queue page takes it from there.

Useful for checking what a playlist actually contains, for grabbing something the *arr apps
have no interest in, and for seeing how a query behaves before blaming Prowlarr for it.

A search whose words are not all found is labelled **Closest matches**, which is the fallback
described under Searching above.

### Paging

Every grid pages the same way &mdash; Dashboard, Playlists, Queue, History, Search and
Searches &mdash; with a row count, First, Prev, Next and Last, and a page size of 25, 50, 100
or 200. Changing the page size returns to the first page, since page 9 of the old size is
rarely page 9 of the new one.

The dashboard tiles still count whole tables; only the grids beneath them are paged. Its
recent downloads grid used to stop at ten rows and now walks the whole history.

Deleting a row keeps you on the page you were on. Clearing a whole page, or a queue draining
while you watch it, returns to the first page rather than leaving you somewhere empty.

## Speed limits

Two mechanisms combine, and the tightest active constraint wins:

- **Manual global limit** in Settings, in KiB/s. `0` is unlimited. Sonarr and Radarr can
  change this through the SABnzbd API.
- **Scheduled windows**, each with days of the week, a start and end time, and a limit. A
  window may wrap past midnight. A window set to **0 KiB/s pauses downloading entirely**
  while it is active.

Per-playlist limits apply on top of whichever global limit is in force.

## Notifications

Set up under **Settings → Notifications**. Turn it on, tick the events you care about, fill
in the mail server, and use **Send test** to check it before saving.

| Event | When it fires |
| --- | --- |
| Download queued | Sonarr or Radarr grabs a release |
| Download started | The transfer begins &mdash; once only, not again when a restart resumes it |
| Download completed | The file is in place under the completed directory |
| Download failed | After the last retry, not once per attempt |
| Playlist refresh failed | A playlist could not be read |

Completed, failed and refresh-failed are on by default. Queued and started fire on every
grab, so they are worth leaving off unless the instance is quiet.

SMTP is the only protocol so far:

| Field | Notes |
| --- | --- |
| Port | `587` for STARTTLS, `465` for implicit TLS, `25` for a relay that needs no login |
| Implicit TLS | On for `465` only. Everything else negotiates STARTTLS when the server offers it |
| Username | Leave blank for an unauthenticated relay |
| To | Several addresses may be separated by commas |

Messages are plain text: the headline and release name in the subject, the detail in the
body.

```
[Smurfm3u] Download completed: Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u

Download completed
Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u

Category: tv
Playlist: Evo
Size: 2.4 GiB
Took: 0h 12m
Folder: /downloads/complete/tv/Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u
```

Sending happens in the background, so a mail server that is slow or down never holds up or
fails a download &mdash; the attempt is logged and abandoned.

> The SMTP password is stored as written, in the same settings row as the API key. That is
> what SMTP has to present, so it cannot be hashed. Use an app password rather than your
> main account password, and treat a database backup as containing a credential.

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
  can delete entries older than a chosen age. **Results** on a row lists the releases that
  query actually answered with, so a grab can be traced back to the search that offered it.
  Names are rebuilt from the playlist rather than stored, so an entry since dropped from
  every playlist is marked retired instead of disappearing.
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
