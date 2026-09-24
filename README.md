# Smurfm3u

Presents M3U playlists and Xtream panels to the *arr stack as if they were a Usenet indexer
and a download client. Prowlarr searches it over **Newznab**; Sonarr and Radarr grab from it
over **SABnzbd**. Underneath there is no Usenet — the sources are filtered down to video on
demand, parsed into release metadata, and downloaded over plain HTTP.

```
Prowlarr ──search──► /api  (Newznab)  ──► Postgres ──► parsed VOD entries
                        │
Sonarr / Radarr ─grab──►│ t=get  ──► pseudo-.nzb (pointers to playlist entries)
                        │
Sonarr / Radarr ─send──► /api  (SABnzbd, mode=…) ──► queue ──► HTTP download
                                                                    │
                                                        /downloads/complete/<cat>/<release>/
```

## What it does

### Sources

- **M3U playlists** from a URL or a file on a mounted volume, and **Xtream panels** read
  through their player API instead.
- A panel's films and series list are imported in bulk; its **episodes are fetched when a
  search names the series**, which on a large panel is five requests per refresh instead of
  thirty thousand.
- Scheduled refresh per playlist (cron), with a live progress bar, and a status that says both
  what it is doing now and when it last worked.
- Entries that disappear are **retired rather than deleted**, so finished downloads keep
  something to point back at.

### Search

- A **Newznab indexer** Prowlarr can point at: search, tv-search and movie-search.
- Every word must appear in the title, with a **closest-matches fallback** when none does.
- A season or episode written into the query text is understood: `sherlock & daughter s01`.
- **Season packs** — a season search is answered with the whole season as one release,
  carrying its episode count.
- Search by hand in the web UI, scoped to chosen playlists.

### Downloads

- A **SABnzbd download client** Sonarr and Radarr can point at.
- Queue with pause, resume, retry and priorities; **resumes after a crash or reboot** from the
  last checkpointed byte.
- Speed limits: a global cap, scheduled windows, and a per-playlist cap on top.
- Path mappings for when the *arr apps see the files somewhere else.

### Running it

- Web UI with light and dark themes, a live **log** page, download and search **history**, and
  a **cache browser** for inspecting and clearing what each playlist has stored.
- **HTTP and SOCKS proxy** for playlist fetches and downloads, applied without a restart.
- **Email notifications** per event.

## What it does not do

Worth reading before you rely on it.

| Limitation | Why, and what it means |
| --- | --- |
| **No TVDB, IMDb or TMDb ids** | The indexer advertises only `q`, `season` and `ep`, so the *arr apps match on **title text alone**. A show whose provider name differs from the one Sonarr knows will not be found, and Sonarr may reject a release as an unknown series. |
| **Metadata is guessed from names** | For an m3u there is nothing else — season, episode, year and title are read back out of the display name with regular expressions, and odd naming gets misread. A panel states them outright, so panels are more reliable. |
| **Sizes are estimates** | Providers rarely declare one, so it is inferred from runtime and quality. The *arr apps use size to choose between releases and to check free space, and a season pack multiplies the error. |
| **RSS only contains what has been fetched** | A panel's episodes arrive when searched for, so a series nobody has searched for is not in the feed. Use search rather than RSS sync to find new episodes. |
| **The first search for a show waits** | A panel has no search endpoint, so a local index is unavoidable; the first query naming a series costs a round trip to fetch its episodes. Every one after is answered locally. |
| **A season pack is not known to be complete** | There is no episode count to check against — a pack is "every episode we hold of this season", which may be fewer than exist. Sonarr imports the folder file by file and re-searches the gaps. |
| **A season pack saves no bandwidth** | Its episodes are still fetched one at a time over one request each. What it saves is grabs and queue slots. |
| **One playlist per grab** | A release comes from a single playlist; a pack cannot draw episodes from two. |
| **No dedupe across playlists** | The same film on two playlists is two results. Entries are unique within a playlist, not across them. |
| **Plain HTTP downloads** | One connection per file, resumed with range requests where the provider supports them and restarted from the beginning where it does not. No segmented downloading. |
| **Provider rate limits still apply** | A panel answering `429` slows a refresh down; the pace backs off and climbs again, but it cannot be faster than the provider allows. |
| **One account, no roles** | The admin account is seeded on first run and its password can be changed in Settings. There is no UI for adding users. |
| **No HTTPS of its own** | It serves plain HTTP on 8080. Put it behind a reverse proxy if it needs to be reachable from anywhere but your own network. |
| **Email notifications only** | SMTP is the only transport. |
| **Not a player** | Nothing is streamed, transcoded or played here; files are downloaded for the *arr apps to import. |

## Start here

```bash
docker compose up -d
```

The web UI is on <http://localhost:8090>. On first run it creates its schema, an `admin`
account and an API key, and writes both to the log:

```bash
docker compose logs smurfm3uapp | grep -E "API key|generated password"
```

Then work through [Setting it up](docs/setup.md) and
[Connecting the *arr apps](docs/connecting-the-arrs.md), in that order.

## Documentation

| | |
| --- | --- |
| **[How it works under the hood](docs/how-it-works.md)** | What the round trip actually is, and why an m3u is read in full while a panel's episodes are fetched when asked |
| [Setting it up](docs/setup.md) | Running it, the environment, and where the data lives |
| [Connecting the *arr apps](docs/connecting-the-arrs.md) | Prowlarr, Sonarr and Radarr, step by step, and what to check when it does not work |
| [Playlists](docs/playlists.md) | Adding sources: remote URLs, local files and Xtream panels |
| [Searching](docs/searching.md) | What counts as VOD, how a query is matched, release naming, and searching by hand |
| [Downloads](docs/downloads.md) | Speed limits, crash recovery and history |
| [Proxy](docs/proxy.md) | HTTP and SOCKS proxies for playlist fetches and downloads |
| [Notifications](docs/notifications.md) | SMTP, and which events send one |
| [Cache](docs/cache.md) | What the playlists have cached, and clearing any of it |
| [Logs](docs/logs.md) | The log page, and what a refresh writes to it |
| [Development](docs/development.md) | Project layout, building, migrations and versioning |

Changes are recorded in [CHANGELOG.md](CHANGELOG.md).

## The short version of the design

A Newznab search is an arbitrary query, and neither an m3u nor an Xtream panel can answer one
— a playlist is a flat file, and a panel's API offers listings but no search. So both are read
into Postgres, which can.

How much gets read in advance depends on what a request costs:

> Import in bulk whatever arrives in **one request**. Fetch on demand whatever costs **one
> request each**.

An m3u is entirely the first kind: one download, everything imported. A panel gives its films
in one request and its series list in one, but its episodes one series at a time — so those
are fetched when a search names the series, and kept from then on.

On a panel of 143,000 films and 30,300 series that is the difference between 30,308 requests
and five. [The long version](docs/how-it-works.md) has the rest, including how content going
away is noticed and what happens when a panel starts refusing.
