# Searching

## What counts as VOD

Nothing in the M3U format marks on-demand content, so entries are scored on the signals
providers actually emit, in this order:

1. A `/live/`, `/movie/` or `/series/` segment in the URL path decides on its own.
2. A video container extension (`.mkv`, `.mp4`, …) means on demand.
3. `.m3u8` / `.ts` needs corroboration — a declared runtime, or a group name that says VOD.
4. A group name containing `live`, `24/7`, `radio`, `sport`, … means live.
5. A declared runtime, or an on-demand group name, means on demand.

Everything else is dropped. Entries that leave a playlist are retired rather than deleted,
so finished downloads keep something to point at.

## How a query is matched

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

## Release naming

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

## By hand, in the web UI

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

## Paging

Every grid pages the same way &mdash; Dashboard, Playlists, Queue, History, Search and
Searches &mdash; with a row count, First, Prev, Next and Last, and a page size of 25, 50, 100
or 200. Changing the page size returns to the first page, since page 9 of the old size is
rarely page 9 of the new one.

The dashboard tiles still count whole tables; only the grids beneath them are paged. Its
recent downloads grid used to stop at ten rows and now walks the whole history.

Deleting a row keeps you on the page you were on. Clearing a whole page, or a queue draining
while you watch it, returns to the first page rather than leaving you somewhere empty.


[← Back to the index](../README.md)
