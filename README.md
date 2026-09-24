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
