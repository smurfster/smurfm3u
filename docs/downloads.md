# Downloads

## Speed limits

Two mechanisms combine, and the tightest active constraint wins:

- **Manual global limit** in Settings, in KiB/s. `0` is unlimited. Sonarr and Radarr can
  change this through the SABnzbd API.
- **Scheduled windows**, each with days of the week, a start and end time, and a limit. A
  window may wrap past midnight. A window set to **0 KiB/s pauses downloading entirely**
  while it is active.

Per-playlist limits apply on top of whichever global limit is in force.

## What one grab is

A grab holds one or more files. A film or an episode is one; a
[season pack](searching.md#season-packs) is one per episode. Either way it is a single queue
slot, a single nzo id to the *arr apps, and a single folder on disk &mdash; the size and
progress shown are the whole grab's.

The files of a grab are transferred one after another rather than at once, so a pack counts
once against the global and per-playlist concurrency limits and a playlist's speed cap
governs the whole season.

A file the provider no longer has is left out and the rest carry on. The grab completes with
what it got and its history row says how many were missing, because the *arr apps import a
folder file by file and will re-search whatever is not in it. A grab where nothing at all was
still available fails, which is the answer the client needs in order to look elsewhere.

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


[← Back to the index](../README.md)
