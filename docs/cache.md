# Cache

**Cache** in the sidebar shows what the playlists have put in the database, and lets you throw
any of it away.

Everything a search is answered from lives here. An m3u's entries arrive with the playlist; a
panel's films and series list arrive on every refresh, and its episodes arrive one series at a
time as searches ask for them — see
[How it works under the hood](how-it-works.md#an-xtream-panel-is-a-service).

## What the page shows

The top table is one row per playlist:

| | |
| --- | --- |
| **Films** | how many film entries are held |
| **Episodes** | how many episode entries are held |
| **Series** | for a panel, how many series' episode lists have been **read** out of how many are **listed** |

That last column is the shape of the whole design in one number. A panel can list thirty
thousand series in a single request and still have read almost none of their episodes, because
reading one costs a request of its own and nothing spends that until a search asks.

**Browse** opens a playlist underneath, in two tabs:

- **Series** — one row per show, with its season and episode counts and when its episode list
  was last read. Click a show to see its seasons, and a season to see its episodes.
- **Films** — every film, paged, with a filter.

The filter matches the way a search matches, so what the page lists is what a query would find.
It is also what makes the page quick: an unfiltered series list has to group every episode row
in the playlist, which is about a second on a catalogue of 845,000, against milliseconds once a
filter narrows it.

Entries marked **Retired** are ones the provider has stopped offering. They are kept rather
than deleted so a finished download still has something to point back at, and they are never
returned by a search.

## Clearing

Tick anything and **Clear selected** removes it. A tick can mean a whole show, one season of
one, a single episode, or any number of films — a show is held as one selection rather than as
its twelve hundred episodes, so ticking one is cheap and survives paging away from it.

**Clear this playlist** removes everything that playlist holds, after a confirmation naming the
counts.

### What clearing actually does

It deletes cached entries. It does not delete the playlist, its settings, its series list, or
any download history — a finished download keeps its record whether or not the entry behind it
still exists.

And it is not permanent, because none of this is original data:

| | How it comes back |
| --- | --- |
| Films | the next refresh — they arrive in bulk, in one request |
| A panel's series list | the next refresh — same |
| A panel's episodes | the next search that names the series |
| An m3u's episodes | the next refresh — an m3u has no per-series fetch, it is all one document |

Clearing a panel's episodes also clears the series' **fetch stamps**, which is what makes it
work. Those stamps are how a search decides whether it already holds a series' episodes; wiped,
the next search for that show fetches them again. Without that step the entries would be gone
and nothing would ever think to go and get them.

### When it is worth doing

- A show whose episodes are wrong, misparsed or stale, and you want it read again rather than
  waiting for the weekly re-read to come round to it.
- A playlist you have reconfigured — changed the quality or resolution tags, say — where the
  stored entries were parsed under the old settings.
- Reclaiming space from a catalogue you no longer search — followed by **Reclaim disk space**,
  below.

### Getting the disk space back

Clearing on its own does not make the database any smaller. PostgreSQL deletes a row by
marking it dead; autovacuum later lets the table reuse that space for new rows, but the files
on disk stay the size they were.

**Reclaim disk space** at the top of the page rewrites the cache tables without the dead rows
(`VACUUM FULL`), and reports the database size before and after. Searches and refreshes wait
while it runs — seconds on a small catalogue, minutes on a very large one — and it briefly
needs free disk room for a copy of whatever is left. So clear first, then reclaim once.

It is not a fix for a provider that has taken something down. A refresh notices that on its
own, and so does a download answered `404`.

[← Back to the index](../README.md)
