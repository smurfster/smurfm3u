# Playlists

Add them under **Playlists**. Each one has:

| Option | What it does |
| --- | --- |
| Type / Location | A remote URL, a local file, or an [Xtream panel](#xtream-panels). **Browse** lists the playlist directory so a path does not have to be typed |
| Username / password | Xtream panels only, and only shown for them |
| Include series | Xtream panels only. Off reads the films and skips the episode walk |
| Enabled | Disabled playlists are ignored by search and by the scheduler |
| Quality / resolution / group tags | Appended to every release name from this playlist, e.g. `…1080p.WEB-DL-Smurfm3u`. The resolution tag also picks the Newznab subcategory |
| Simultaneous downloads | Per-playlist cap, under the global cap in Settings |
| Pause between starts | Seconds to wait before starting the next download from this playlist |
| Speed limit | Per-playlist cap in KiB/s, on top of the global limit |
| Refresh schedule | Five-field cron, in UTC. Blank means manual refresh only |
| Extra HTTP headers | One `Name: value` per line, used for the playlist fetch and for downloads |

**Force update** is the *Refresh* button on each row. A newly added playlist refreshes
immediately.

## Xtream panels

A provider that hands out a username, a password and a server address is running an Xtream
Codes panel. Add it with **Type → Xtream panel** and it is read through the panel's own API
rather than as a playlist.

That is worth doing because the panel *states* what a playlist leaves to guesswork:

| | Playlist (`get.php`) | Xtream panel |
| --- | --- | --- |
| Season and episode | Read back out of the entry name, when it is in there at all | Given by the panel |
| Episode titles | Whatever is in the name | Given by the panel |
| Container (`mkv`, `mp4`) | Guessed from the URL | Given by the panel |
| Runtime | Only if the playlist sets one | Given by the panel |
| Live channels | Downloaded, then filtered back out | Never requested |

Fill in the address and credentials, then use **Test connection** to check them before saving.
It reports what the panel says about the account, which is how an expired line tells you what
is wrong.

| Field | Notes |
| --- | --- |
| Panel address | `http://line.example.com:8080`. Pasting the whole `get.php` link works: the credentials are taken from it, and only the address is stored |
| Username / password | As the provider issued them. Leave blank if the address already carries them |
| Include series | Off keeps the films only &mdash; see below |

**Series take longer than films.** The panel returns every film in one request, but the
episode list is one request per series, and a panel may carry thousands. They are fetched a
few at a time, and progress goes to the log every 200 series. If that is more than you want
on a six-hourly schedule, turn **Include series** off and the films still refresh in seconds.

**Only the series that changed are read.** `get_series` is one request and already tells us
when each series last changed, so a refresh compares that against what it stored last time
and asks for the episode list only where it has moved. On a panel of 30,000 series that is
the difference between 30,000 requests and a handful: hours become minutes.

The episodes of a series that was left alone are marked as still present without being
fetched, so nothing is retired for not having been asked about.

> **A panel that does not keep its own `last_modified` current would hide new episodes.** So
> the stamps are trusted for a week at a time: one run every seven days ignores them and reads
> every series, which puts right anything that drifted. The log says which kind of run it is.

**If the panel pushes back, the refresh eases off rather than losing content.** A `429 Too
Many Requests` is not a missing series, it is a panel asking to be asked more slowly: the
request waits and is tried again, honouring the panel's own `Retry-After` where it sends one,
and the walk halves how many series it reads at once. It climbs back a step at a time once
the panel is answering cleanly again, so one busy moment early in a large catalogue does not
set the pace for the rest of it. The same applies to a
`502`, `503` or `504`. Everything else &mdash; a `401`, a `404` &mdash; is the panel meaning
it, and is not retried.

A series that still fails after that is logged and skipped rather than failing the whole
refresh; one bad entry out of thousands is not worth losing the rest.

> The panel password is stored as written, in the playlist's row. That is what the panel has
> to be presented with, so it cannot be hashed. Treat a database backup as containing a
> credential, as with the SMTP and proxy passwords.

You do not have to use this. A panel's `get.php` link still works as a plain **Remote URL**
playlist, and always did &mdash; the URL layout panels use is one of the signals the VOD
filter already reads. Xtream is the better option where the API is available; the m3u link is
the fallback where it is not.


[← Back to the index](../README.md)
