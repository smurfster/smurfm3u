# Downloads

## Speed limits

Two mechanisms combine, and the tightest active constraint wins:

- **Manual global limit** in Settings, in KiB/s. `0` is unlimited. Sonarr and Radarr can
  change this through the SABnzbd API.
- **Scheduled windows**, each with days of the week, a start and end time, and a limit. A
  window may wrap past midnight. A window set to **0 KiB/s pauses downloading entirely**
  while it is active.

Per-playlist limits apply on top of whichever global limit is in force.

## Categories

Categories work as they do in SABnzbd. Sonarr and Radarr file each grab under the category
set in their download client, and the category decides two things: which folder the finished
grab is moved into, and where it sits in the queue. They are edited under
**Settings → Categories**, and reported to the *arr apps through `get_cats` and `get_config`.

| Field | Meaning |
| --- | --- |
| Name | What the client sends. Kept in lower case, as SABnzbd keeps it. |
| Folder | Relative to the complete directory (`media/tv` nests), or a full path. Blank means a folder named after the category. |
| Priority | `Force`, `High`, `Normal` or `Low`, or `Default` to take the Default category's. |

**Default** (SABnzbd's `*`) is always there and cannot be removed. It catches a grab with no
category, one naming a category that is not listed, and one whose category was removed while
it was queued. With a blank folder it puts finished grabs straight into the complete
directory. A fresh install starts with Default, `tv`, `movies` and `prowlarr`, the last
being the category Prowlarr's download client asks for unless it is changed.

**Priority.** A grab asking for the `Default` priority &mdash; which is what Sonarr and Radarr
send unless their *Recent/Older Priority* is changed &mdash; runs at its category's. A grab
asking for a priority of its own keeps it, and one asking for `Paused` is queued paused.
Higher priorities are started first, and grabs of equal priority in the order they arrived.
`Force` is simply the highest: unlike SABnzbd, it does not download through a pause.

**Changing a grab's category.** The Queue page has a category picker on every row, and
clients can do the same through SABnzbd's `change_cat`. A grab already downloading can be
moved too; the category is read again when it finishes. Its priority is left as it was.

Grabs from the [Search page](searching.md#by-hand-in-the-web-ui) are filed under the
categories picked under *Search page TV grabs* and *Search page movie grabs*.

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

## Seeing inside a grab

A grab carrying more than one file &mdash; a [season pack](searching.md#season-packs) &mdash;
can be opened from its row on **Queue** or **Download History** to see the episodes it is
made of:

| | |
| --- | --- |
| **Done** | transferred and in the folder |
| **Getting** | the one being transferred now, with its own progress |
| **Waiting** | not started; the size shown is what it is expected to be |
| **Partial** | stopped partway and never finished. Only in history &mdash; while a grab runs, the same file reads "Getting" |
| **Gone** | the provider no longer had it, so it was left out. The reason sits beside it |

Files are transferred in order, one at a time, so at most one reads **Getting**.

A single episode or film is one file and its row already says everything, so only a grab with
several is worth opening.

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

## File permissions

Smurfm3u runs as root inside its container, and Sonarr and Radarr usually run as another user
(the linuxserver images use `PUID`, often 1000). They have to move a finished download into
the library, which needs write access to it - so by default everything Smurfm3u creates under
`/downloads` is open to every user (`UMASK=000`). Without that, an import fails with
"Permission denied" even though the *arr can see the file.

Set `UMASK` in the environment to tighten it: `002` keeps write access to the owner and the
group, which is enough when the *arrs run with a group that owns the downloads folder.

## History

- **Queue** and **History** show downloads; history rows can be retried or deleted.
- **Searches** records every query the *arr apps send, with result counts and timings, and
  can delete entries older than a chosen age. **Results** on a row lists the releases that
  query actually answered with, so a grab can be traced back to the search that offered it.
  Names are rebuilt from the playlist rather than stored, so an entry since dropped from
  every playlist is marked retired instead of disappearing.
- Both are also trimmed automatically by the retention settings. `0` means keep forever.


[← Back to the index](../README.md)
