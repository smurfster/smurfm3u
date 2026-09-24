# How it works under the hood

Smurfm3u pretends to be two Usenet services at once. Prowlarr searches it over **Newznab**;
Sonarr and Radarr grab from it over **SABnzbd**. There is no Usenet underneath — there are
playlists of video files, and everything here exists to make those look like an indexer.

## The round trip

```
Prowlarr ──search──► /api  (Newznab)  ──► Postgres ──► parsed VOD entries
                        │
Sonarr / Radarr ─grab──►│ t=get  ──► pseudo-.nzb (pointers to playlist entries)
                        │
Sonarr / Radarr ─send──► /api  (SABnzbd, mode=…) ──► queue ──► HTTP download
                                                                    │
                                                        /downloads/complete/<cat>/<release>/
```

The `.nzb` a client grabs is not a real Usenet document. It carries the ids of the playlist
entries behind the release, and the SABnzbd endpoint reads them back when the client posts the
file to it. That round trip is what turns a search result into a queued download.

Usually that is one id. A [season pack](searching.md#season-packs) carries one per episode, in
the order they should be transferred, which is how a whole season arrives as a single grab.

## Why anything is imported at all

A Newznab search is an arbitrary query — "this show, season 1, episode 3". Neither kind of
source can answer one:

- an **m3u** is a flat file, so there is nothing to ask
- an **Xtream panel** has an API, but no search in it — `action=search` is not a thing it
  understands, and it answers with the account details instead

So both are read into Postgres, which *can* answer a query. The local database is an index of
a remote catalogue that cannot be queried. That part is not a choice.

What *is* a choice is how much of it gets filled in advance, and the two kinds differ there.

## The rule

> Import in bulk whatever arrives in **one request**. Fetch on demand whatever costs **one
> request each**.

That is the whole design, and it is not a split between m3u and Xtream — it is a split between
cheap and expensive. An m3u happens to be entirely on the cheap side.

| | How it loads | Cost |
| --- | --- | --- |
| m3u playlist | bulk | one download |
| Xtream **films** | bulk | one request |
| Xtream **series list** | bulk | one request |
| Xtream **episodes** | on demand | one request **per series** |

## An m3u is a document

One HTTP request, or one file on disk, streamed and parsed as it arrives. Nothing about it is
addressable, so it is read start to finish:

1. **Read** — the response is counted through a wrapper so progress has a denominator. Local
   files know their size; a remote one has `Content-Length`, or nothing, in which case there
   is no percentage to show and the page shows a live byte count instead.
2. **Classify** — nothing in the M3U format marks what is on demand, so every entry is scored
   on the signals providers actually emit. See [Searching](searching.md#what-counts-as-vod).
3. **Parse** — the display name is all there is, so the season, episode, year and title are
   read back out of it with regular expressions.
4. **Reconcile** — entries are upserted against a key derived from their stream URL, and
   anything the run did not touch is retired.

Everything is in the database when the refresh finishes, because everything arrived in the one
response that was going to arrive anyway.

## An Xtream panel is a service

A panel answers five useful questions, and their costs are wildly different:

| Request | Returns | Cost on a large panel |
| --- | --- | --- |
| `player_api.php` | the account | instant |
| `get_vod_categories` | ~50 categories | instant |
| `get_vod_streams` | **every film** | ~55 MB, 5s |
| `get_series_categories` | ~40 categories | instant |
| `get_series` | **every series**, without episodes | ~29 MB, 4s |
| `get_series_info&series_id=…` | **one series'** episodes | ~50 KB, 0.5s |

The last row is the entire problem. A panel with 30,000 series needs 30,000 of those requests
to read every episode, which at half a second each is hours of waiting on someone else's
server — and it is *their* server doing the work, which is why panels rate limit.

So the first five are made on every refresh and the sixth is not made at all until something
asks for it:

**A refresh** authenticates, pulls the films, and pulls the series list into its own table.
Five requests, and done in well under a minute however large the catalogue.

**A search** that names a series fetches that series' episodes and stores them. The first
search for a show waits about half a second; every search after it is answered locally in
milliseconds, like anything else.

Episodes are fetched when the series is new, when the panel's own `last_modified` for it has
moved since they were last read, or when the search found nothing and the series might have
gained something (at most once an hour per series — the *arrs search for every missing episode
they have, and each of those misses would otherwise be a request).

Because the panel states the season, episode, episode title and container outright, none of it
has to be guessed back out of a name the way an m3u's does.

## Knowing when something has gone

An m3u says what exists every time it is read, so anything missing from a run is retired at the
end of it. A panel is only partly read each time, so withdrawal is noticed in more places:

| What went | How it is noticed |
| --- | --- |
| A film | the next refresh — films are read in full every time |
| A whole series | the next refresh — the series list is read in full every time, and its episodes are retired with it |
| An episode, series still present | the next time that series is fetched — anything not in the answer is retired |
| An episode, nobody searching | within a week — each refresh re-reads the oldest episode lists it has not checked, up to 200 of them |
| Anything the panel never admits to | when a download is answered `404` or `410`, which retires the entry on the spot |

Entries are retired rather than deleted, so a finished download still has something to point
back at.

## When a panel says no

A panel answering `429 Too Many Requests` is not broken and the series it refused is not
missing — it is asking to be asked more slowly. Refused requests wait and try again, honouring
the panel's own `Retry-After` where it sends one, and the pause applies to every request in
flight rather than only the one that was refused.

The number of episode lists read at once halves when the panel pushes back and climbs again as
it answers cleanly, getting more reluctant each time, so a panel with a hard limit settles at
that limit instead of oscillating around it. The ceiling is per playlist — see
[Playlists](playlists.md#xtream-panels).

`502`, `503` and `504` are treated the same way. A `401` or a `404` is the panel meaning it,
and asking again only asks again.

## What this costs

Measured against a real panel of 143,000 films and 30,300 series:

| | Reading every episode list in advance | Fetching them when asked |
| --- | --- | --- |
| Requests per refresh | 30,308 | **5** |
| Time | 2h 17m | **38s** |
| Downloaded | ~1.5 GB | **84 MB** |

The trade is that a series nobody has searched for is not in the RSS feed, because a feed can
only contain what has been fetched. Searches are unaffected — that is how the *arrs find
things when they know what they want. It matters only if you rely on RSS sync to notice new
episodes, and on a catalogue approaching a million entries a feed capped at a hundred was
never a useful window on what was new anyway.

[← Back to the index](../README.md)
