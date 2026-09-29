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

An **ampersand is spelled out** rather than dropped, because Sonarr spells it out when it
searches: it asks for "Sherlock and Daughter" where the provider wrote "Sherlock & Daughter".
Both land on `sherlock and daughter`, so either spelling finds the other. Dropped, as it used
to be, the stored title was `sherlock daughter` and "and" became a word it could never
contain &mdash; which made every title carrying an ampersand unfindable from Sonarr, and a
season search fall through to near matches with no [season pack](#season-packs) among them.

### Seasons and episodes written into the query

The *arr apps send a season and episode as their own parameters, but a query typed by hand
carries them in the words. Those are lifted out and used to narrow the search, exactly as the
parameters would:

| Typed | Searched for |
| --- | --- |
| `lanterns s01e01` | "lanterns", season 1, episode 1 |
| `sherlock & daughter s01` | "sherlock & daughter", season 1 |
| `top gear season 3` | "top gear", season 3 |
| `the wire 3x07` | "the wire", season 3, episode 7 |

Left in the words they would be terms the title has to contain, and no title can: entries are
stored with the season and episode stripped out and the numbers kept in their own columns. A
query written that way used to match nothing at all, and came back only if the relaxed
fallback rescued it &mdash; with the whole series rather than the season asked for.

**The words as written are always tried first.** Only when nothing contains every one of them
is the query read this second way. That ordering matters, because the two readings cannot be
told apart by shape:

| Typed | What it is |
| --- | --- |
| `top gear season 2` | season 2 of a show |
| `open season 2` | a film called "Open Season 2" |

Identical in form, and the only thing that separates them is which one the catalogue actually
holds. Trying the literal words first means a title that really does contain them always wins,
and the season reading only gets its turn when the literal one found nothing.

What a client sends explicitly beats both. A query is only reinterpreted when it carries a
season or episode alone, and one that is nothing but a season number is left as written, since
there would be no title left to search for.

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

## Season packs

When a client searches for a season rather than a single episode &mdash; Sonarr's
**Search Season**, which sends a season with no episode number &mdash; the whole season is
offered as one release alongside the individual episodes:

| | |
| --- | --- |
| `House.of.Knives.2025.S01.1080p.WEB-DL-Smurfm3u` | the pack, 8 files |
| `House.of.Knives.2025.S01E01.1080p.WEB-DL-Smurfm3u` | one episode |
| `House.of.Knives.2025.S01E02.1080p.WEB-DL-Smurfm3u` | one episode |

A season name and no episode number is exactly how Sonarr is told a release covers the whole
season, so it reads the pack for what it is and grabs it as one thing. Without packs a season
search turns into one grab per episode, and a 24-episode season is 24 queue slots.

Grabbing one produces a single queue slot whose size and progress are the whole season's. Its
episodes are transferred one after another into one folder, each named after its own episode,
which is the layout the importer walks file by file:

```
/downloads/complete/tv/House.of.Knives.2025.S01.1080p.WEB-DL-Smurfm3u/
    House.of.Knives.2025.S01E01.1080p.WEB-DL-Smurfm3u.mkv
    House.of.Knives.2025.S01E02.1080p.WEB-DL-Smurfm3u.mkv
    ...
```

Because it is one grab, it takes one slot against the global and per-playlist download limits
rather than one per episode, and a playlist's speed cap governs the season as a whole.

### What a pack is, and is not

A pack is not a stored thing. It is every episode of that season that the index happens to
hold, worked out again when the grab arrives &mdash; so a grab that lands an hour after the
search picks up an episode that turned up in between, and a season withdrawn in the meantime
is refused rather than handed over as a list of dead links.

There is also no way to know whether a season is *complete*: there is no episode count to
check against, only what the playlist offers. A pack is "every episode we have of this
season", which may be fewer than exist. Sonarr imports the folder file by file and goes
looking for whatever is not in it, so a short pack costs a re-search rather than a wrong
result.

An episode withdrawn partway through a transfer is left out and the rest carry on; the grab
still completes, and its history row says how many were missing. Only a season where nothing
at all is still available fails outright.

A season of more than 200 episodes is not offered as a pack at all. Past that it is almost
always a daily show whose air year has been read as a season number &mdash; the index holds
"season 2025" of a nightly news programme with 620 episodes in it &mdash; and several hundred
gigabytes under one name nobody meant to ask for is worse than no pack. Those seasons are
answered episode by episode.

### Turning them off

**Settings &rarr; Season packs** switches them off, which returns a season search to being
answered episode by episode. **Minimum episodes** is the smallest season worth offering as a
pack, two by default &mdash; below it a "pack" is a single episode under a name that hides
which one it is.

Two things worth knowing before leaving them on:

- **Size estimates add up.** Sizes are estimated from runtime and quality when a playlist does
  not declare one, and a pack's size is the sum of those estimates. An estimate that is 20%
  out on one episode is 20% out on the whole season, which matters because the *arr apps
  reject grabs on size limits and on free disk space.
- **Nothing is saved on bandwidth.** The episodes are still fetched one at a time over one
  request each. What a pack saves is queue slots, grabs and round trips through the *arr apps.

## By hand, in the web UI

**Search** in the sidebar queries the same index Prowlarr does, without going through the
*arr apps at all. Type a title, narrow it to TV or Movies, optionally give a season and
episode, and every match comes back with a **Download** button.

Downloading from here queues the entry exactly as a grab from Sonarr or Radarr would,
category included, so the file lands in the same place and can still be imported afterwards.
Which category that is for TV and for films is set under **Settings → Categories**.
The row says **Queued** once it is in, and the Queue page takes it from there.

Useful for checking what a playlist actually contains, for grabbing something the *arr apps
have no interest in, and for seeing how a query behaves before blaming Prowlarr for it.

A search whose words are not all found is labelled **Closest matches**, which is the fallback
described under Searching above.

A [season pack](#season-packs) is marked with **how many episodes are in it** in place of the
usual TV badge, because a season is offered as one release and how much of it is actually
there is the thing worth knowing before grabbing it. The line under the release name says the
same, alongside the show and season.

## Choosing which playlists to search

**Search** in the sidebar lists every enabled playlist as a tickbox. Tick none and the search
answers from all of them, which is what the *arr apps always get: a Newznab client has no way
to name a playlist, and no way to know what to name.

Ticking one or more narrows the search to those. It narrows the fetching too &mdash; a panel
whose answer would be filtered out is not asked in the first place.

A disabled playlist is not listed, because a search would not answer from it either way.

## Looking at a release before taking it

Every release carries a link to a page describing it, and Sonarr and Radarr open that page
when you click through to a release in their search results. It answers the question a single
line in their grid cannot: **what exactly would this download?**

For a film or an episode that is one file and little more than its row restated. For a
[season pack](searching.md#season-packs) it is the point &mdash; the pack arrives as one line,
and the page lists every episode behind it:

| | |
| --- | --- |
| **Episode** | `S01E03`, so a gap in the numbering is obvious at a glance |
| **File** | the name it lands under, with the provider's episode title beneath |
| **Group** | the provider's category |
| **Size** | marked **approx** where it was worked out from runtime rather than declared |

A **Download** button queues it from here, the same as the Search page does.

The page is behind the same sign-in as the rest of the UI, so the link carries no API key
&mdash; which also keeps the key out of your *arr app's database and off its screen.

A release withdrawn since the search that found it says so plainly rather than showing a page
for something the playlist no longer has.

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
