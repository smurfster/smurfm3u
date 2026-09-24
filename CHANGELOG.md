# Changelog

Notable changes to Smurfm3u, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The version is set in `Directory.Build.props` and shown at the bottom of the sidebar, so
what the UI reports is always what was built.

## [Unreleased]

Nothing yet.

## [1.2.7] - 2026-09-24

### Fixed

- **A search run from the Search page is recorded like any other.** It never was: only the
  Newznab endpoint wrote history, and the page calls the search service directly, so searching
  by hand left no trace at all - and **Results**, which is only reachable from the history,
  could not be opened for one. The page even described itself as a log of what the *arr apps
  ask for, which is what it had become.

  Both now go through one recorder, and a new origin tells them apart: a search of your own
  shows as "Search page" where an indexer client shows its user agent and address. Rows
  already stored are marked as coming from the indexer, which every one of them did.

## [1.2.6] - 2026-09-24

### Fixed

- **A title with an ampersand in it is findable from Sonarr again.** Sonarr spells an
  ampersand out when it searches - it asks for "Sherlock and Daughter" where the provider
  wrote "Sherlock & Daughter" - and the match key dropped the ampersand entirely, storing
  `sherlock daughter`. That made "and" a word the stored title could never contain, so the
  match failed and the query fell through to near matches, which never carry a season pack.
  It looked like season packs were broken; what was broken was every title with an ampersand
  in it. On a real catalogue that was 20,109 entries and 627 series, two percent of the whole.

  The key now spells it out, so "Sherlock & Daughter" and "Sherlock and Daughter" land on the
  same place and either spelling finds the other. Keys already stored were computed under the
  old rule and are rewritten once at startup: a refresh re-derives most of them, but not a
  panel's episodes, which are only written when they are fetched.

## [1.2.5] - 2026-09-24

### Added

- The README now opens with **what it does and what it does not do** - the sources, search,
  downloads and the rest in a page, and a table of the limitations worth knowing before
  relying on it: no TVDB or IMDb ids so matching is on title text alone, metadata guessed from
  names for an m3u, estimated sizes, an RSS feed that only holds what has been fetched, and a
  season pack that cannot know whether it is complete.

## [1.2.4] - 2026-09-24

### Added

- An **integration test suite**, `tests/Smurfm3u.Integration.Tests`, running against a
  throwaway Postgres that Testcontainers starts and the real migrations set up. It covers what
  cannot be reached without a database: which reading of a query wins, how a season is grouped
  into a pack and resolved at grab time, which playlists answer, and what clearing the cache
  removes. `dotnet test` runs both suites; the unit tests still need nothing but the SDK.

### Fixed

- `Smurfm3u.Data` now states its Entity Framework dependency outright rather than inheriting it
  from the Design package, which is marked private and so does not reach anything referencing
  it. The project compiled against 10.0.12 while advertising the 10.0.4 that Npgsql asks for,
  and anything referencing it failed to compile against its own public surface.

## [1.2.3] - 2026-09-24

### Added

- **Which playlists to search.** The Search page lists every enabled playlist as a tickbox;
  tick none and it answers from all of them, as it always has. It narrows the fetching as well
  as the matching, so a panel whose answer would be filtered out is not asked in the first
  place. The *arr apps still get everything, because a Newznab client has no way to name a
  playlist and no way to know what to name.
- A season pack now shows **how many episodes are in it** in place of the usual TV badge. A
  season is offered as one release, so how much of it is actually there is the thing worth
  knowing before grabbing it.

### Fixed

- **A season written into a query no longer sinks it.** "sherlock &amp; daughter s01" and
  "top gear season 3" were matched as if "s01" were a word the title had to contain, and no
  title can: entries are stored with the season stripped out and the number in its own column.
  So the search found nothing, and came back only if the relaxed fallback rescued it &mdash;
  with the whole series rather than the season, no season pack, and nothing at all if that
  fallback was switched off. The season is now lifted out of the words the same way
  `s01e01` already was, which was the half of that fix left behind.

  The words as written are tried **first**, and this reading only gets its turn when nothing
  contains every one of them. The two cannot be told apart by shape &mdash; `top gear season 2`
  is a season and `open season 2` is a film, and only the catalogue knows which &mdash; so a
  title that really does contain the words always wins.

  A name a provider gave an entry is left alone here for the same reason. Measured over
  143,440 real films, reading a bare season as a marker in a name misfiled eight of them
  &mdash; "Open Season 2", "Making The Witcher: Season 2" &mdash; and gained nothing.

## [1.2.2] - 2026-09-24

### Changed

- **The tables work on a phone.** A single cell that could not shrink - a button, a release
  name, a long URL - used to widen the whole document past the viewport, so every page scrolled
  sideways and whatever was in the last column sat off-screen with no way to reach it. A table
  now scrolls inside its own card, keeps a readable minimum width, and drops its least useful
  columns below 720px: the playlist's location and tags, a download's category and size, a
  search's playlist and date, and so on. What is left on every row is what identifies it, what
  says its state, and what acts on it. Nothing changes above that width.

## [1.2.1] - 2026-09-24

### Added

- A **Cache** page, under Settings in the sidebar. It shows what each playlist has put in the
  database &mdash; films, episodes, and for a panel how many series' episode lists have
  actually been read out of how many are listed &mdash; and lets any of it be thrown away: a
  whole playlist, a whole show, one season, one episode, or any number of films at once. A
  show is held as a single selection rather than as its twelve hundred episodes, so ticking
  one is cheap and survives paging away from the row that made it. Clearing a panel's episodes
  also clears the series' fetch stamps, which is what makes the next search go and read them
  again rather than leaving them gone for good. See [Cache](docs/cache.md).
- Playlists now show **when they last refreshed successfully**, alongside what they are doing
  now. Kept apart from when the last run *ended*, because a failed run does not replace what is
  being served: a playlist failing nightly for a week reads "still serving data from 7 days
  ago", where one date would only say the failure was a minute ago.

### Changed

- The sidebar is reordered: Dashboard, Queue, Search, Playlists, Settings, Cache, Download
  History, Search History, Logs. Day to day first, then what is set up once, then what is
  looked back at.
- `Directory.Build.props` is now copied into the Docker image. It holds `<Version>`, and
  MSBuild only finds it by walking up from the project directory, so inside the image there
  was nothing to find and every container build fell back to the SDK default &mdash; the
  sidebar has been reporting **v1.0.0** since the first Docker build, and 1.1.0 and 1.2.0 both
  went unreported.

### Fixed

- Clearing finished downloads no longer **deletes the incomplete directory**. A grab's
  recorded path used to name the file inside its folder and now names the folder itself; the
  tidy-up still assumed the old shape, so for anything already finished it fell through to
  "remove the empty parent", and the parent is the incomplete directory. It came back on the
  next download, so nothing failed visibly, but it is a configured path and often a mount
  point.

## [1.2.0] - 2026-09-24

### Added

- **Season packs.** A search for a season now offers the whole season as one release
  alongside the individual episodes, which is what Sonarr's "Search Season" is asking for: a
  release named for the season with no episode number is how it is told one covers the lot.
  Grabbing it produces a single queue slot whose size and progress are the season's, writing
  one file per episode into one folder for the importer to walk. A pack takes one slot against
  the concurrency limits rather than one per episode, and a playlist's speed cap governs the
  whole season. An episode withdrawn partway through is left out and the rest carry on, since
  the *arr apps re-search whatever is not in the folder; only a season with nothing left
  available fails outright. Switched on by default, with a minimum size, under
  **Settings &rarr; Search results**.
- A **weekly re-read** of the episode lists nobody has searched for, oldest first and at most
  two hundred a refresh. Everything else about a panel's episodes waits to be asked; this is
  the part that does not, so a series nobody looks for cannot keep a withdrawn episode on
  offer indefinitely. It replaces 1.1.0's weekly full walk, which read every series in one
  run: the ceiling is what stops a long-neglected catalogue turning a refresh back into that,
  and a catalogue with nothing stale pays nothing.
- The README is now **an index over `docs/`**, with a page each for setup, the *arr apps,
  playlists, searching, downloads, proxy, notifications, logs and development &mdash; and a
  new **[How it works under the hood](docs/how-it-works.md)** explaining what the round trip
  actually is, and why an m3u is read in full while a panel's episodes are fetched when asked.

### Changed

- **An Xtream refresh no longer reads every episode list.** A panel gives its films in one
  request and its series list in one, but its episodes one series at a time, and the refresh
  was treating all three alike &mdash; pulling everything, storing everything, for a catalogue
  almost none of which anyone ever searches for. Films and the series list are still imported
  in bulk; episodes are now fetched the first time a search names their series, and kept from
  then on. On a panel of 143,000 films and 30,300 series, measured:

  | | Before | After |
  | --- | --- | --- |
  | Requests per refresh | 30,308 | **5** |
  | Time | 2h 17m | **38s** |
  | Downloaded | ~1.5 GB | **84 MB** |

  The first search for a show waits about half a second; every search after it is answered
  locally. The trade is that a series nobody has searched for is not in the RSS feed, because
  a feed can only contain what has been fetched &mdash; searches are unaffected, and on a
  catalogue approaching a million entries a feed capped at a hundred was never a useful window
  on what was new.
- **Episode lists at once** has a job again. It was left doing nothing when the walk it
  governed was removed, and now paces the weekly re-read instead. A setting that does nothing
  is a worse thing to ship than no setting at all.
- A grab now holds **one or more files** instead of exactly one. A film or an episode is still
  a single file and lands on disk exactly where it always did; existing downloads and history
  are carried across unchanged.
- A pack is worked out **when the grab arrives** rather than when the search ran, so a grab
  that lands an hour later picks up an episode that turned up in between, and a season
  withdrawn in the meantime is refused rather than handed over as a list of dead links.

### Fixed

- **"lanterns s01e01" now finds the episode.** A query's words all have to appear in the
  title, and "s01e01" never can: titles are stored with the season and episode stripped out
  and the numbers kept in their own columns, so a query written that way always failed the
  strict match and came back only because the relaxed fallback rescued it &mdash; with the
  whole series rather than the episode asked for, and only while that fallback is switched on.
  A season and episode written into the query are now lifted out of it and narrow the search
  exactly as the *arrs' own parameters would. What a client sends explicitly still wins.
- **Withdrawn episodes are retired.** Episodes arrive from a search rather than from a
  refresh, so the refresh exempts them from its reconcile &mdash; which left nowhere for an
  episode the panel had taken down to be noticed, and the *arrs would keep being handed it. A
  fetch now stamps everything it stores and retires anything of that series still carrying an
  older stamp.
- **A download answered `404` or `410` retires the entry on the spot**, and stops being
  retried. The provider saying outright that a file is gone is better evidence than any
  schedule, and nothing about a 404 improves by asking again &mdash; it used to spend three
  attempts proving the same point.
- **A search that finds nothing asks the panel again.** The stamp deciding whether a series
  is worth re-reading only moves when a refresh reads the series list, so between refreshes a
  new episode was invisible however many times it was searched for &mdash; and searching
  repeatedly for a missing episode is precisely what Sonarr does. A miss now re-reads the
  series whatever its stamp says, bounded to once an hour per series, which makes a new
  episode findable within the hour rather than within the day.

## [1.1.0] - 2026-09-22

### Added

- **Episode lists at once** is now a per-playlist setting rather than a fixed four. It is a
  ceiling rather than a rate: a panel that answers `429` still pulls the refresh below it and
  earns its way back. Worth raising only on a panel the log shows no push-back from.
- An Xtream refresh now reads **only the series that changed**. The panel already says when
  each series last changed, so the episode list is fetched only where it has moved: on a panel
  of 30,000 series that is hours of requests replaced by a handful. Episodes of a series left
  alone are kept without being re-read, and one run a week reads everything regardless so a
  panel that neglects its own stamps cannot hide new episodes indefinitely.
- **Xtream panel playlists**, read through the panel's player API instead of as an m3u. The
  panel states the season, episode, episode title, container and runtime outright, so none of
  it has to be read back out of an entry name, and nothing live is requested in the first
  place. **Test connection** checks the login before saving, and a pasted `get.php` link is
  accepted whole: the credentials are lifted out of it and only the address is kept.
- A **progress bar** on a playlist while it refreshes, with a count and a rough time
  remaining. A panel reports its films and then its series, one series being one request and
  so the unit the waiting is made of; a playlist reports bytes against the length the provider
  declared. Where nothing declares a length there is no bar, because there is nothing honest
  to divide by, and it shows a live count instead.
- A **Logs** page, showing the last 1,000 lines as they are written, with a level filter, a
  text filter, pause and clear. Kept in memory only and bounded; the container's log is
  untouched and still the place to look for anything older.
- **Playlists now say what they are doing.** Adding, editing, deleting, enabling and
  disabling are logged with who did it, an edit naming the fields that changed. A refresh
  logs what it is reading and from where, the size the provider answered with, progress every
  25,000 entries, and the panel account for an Xtream source.
- A **light theme** alongside the dark one, with a toggle in the top bar. It follows the
  system preference until a choice is made here, and then remembers that choice per browser.
- **Icons** on the navigation links, drawn inline so there is nothing to fetch.

### Fixed

- A **rate-limited panel no longer costs content**. A `429` was treated as a failed series
  and skipped, so a panel that only wanted to be asked more slowly lost part of its catalogue
  on every refresh. Refused requests now wait and try again, honouring the panel's own
  `Retry-After`, and the pause applies to every request in flight rather than just the one
  that was refused. `502`, `503` and `504` are treated the same way; a `401` or `404` still
  is not.
- The episode walk **climbs back up** after easing off. A single refusal used to pin it to one
  series at a time for the rest of the run, which on a panel of 30,000 series was the
  difference between about an hour and about five. It now halves on a push-back and steps back
  up as the panel answers cleanly, getting more reluctant each time it is refused so a panel
  with a hard limit settles at that limit rather than oscillating around it.
- An Xtream series whose episodes carry no runtime is **no longer skipped**. Panels are PHP,
  and PHP encodes an empty map as `[]` rather than `{}`, so an episode's `info` block arrives
  as an empty array whenever the panel knows no duration — which failed to parse and cost the
  whole series. Any field documented as an object now reads that way, `user_info` included.
- A playlist being refreshed now **says so**. The status column kept showing the previous
  run's outcome — "Failed", with its error underneath — next to a button already reading
  "Refreshing...", because the two read different things: the button watched the page, the
  status watched a row loaded before the run began. Both now come from one answer, which also
  means a refresh the scheduler started shows as running rather than as whatever happened last.
- Playlist locations are **redacted** wherever they are written. A provider's link carries the
  password in its query string, so it was going into the log &mdash; now a page in the web UI
  &mdash; and into the body of a refresh-failed email.

### Changed

- The signed-in user now reads `User: <name>` at the top left of the page itself rather than
  in the navigation column, and **Sign out** has moved to the top right. Both sit in a bar
  that stays put while the page scrolls.

## [1.0.0] - 2026-09-22

First tagged release. Everything below was built before versioning started, and is recorded
here so the history is not lost.

### Added

- **Newznab endpoint** for Prowlarr, and a **SABnzbd endpoint** for Sonarr and Radarr, both
  on `/api` and told apart by the request's `mode` parameter. A grab hands back a pseudo-`.nzb`
  pointing at one playlist entry, which the SABnzbd endpoint reads back to queue the download.
- **Playlists** from a remote URL or a local file, each with its own quality, resolution and
  release-group tags, concurrency cap, speed limit, start delay, cron refresh schedule and
  extra HTTP headers.
- **VOD filtering and release parsing**: playlist entries are scored on the signals providers
  actually emit, then parsed into titles, years, seasons and episodes, and named the way the
  *arr apps expect to read them.
- **Search**, with a relaxed fallback that answers with the closest entries when no entry
  contains every word of a query, preferring longer and rarer words over short common ones.
- **Downloads** that stream to an incomplete directory and only move once finished, so the
  *arr apps never import a partial file. Resumes with HTTP range requests, retries, and
  restarts from the beginning against a server that ignores the range.
- **Speed limits**: a manual global cap that Sonarr and Radarr can change over the SABnzbd
  API, plus scheduled windows by day and time. The tightest active limit wins, and a window
  set to 0 KiB/s pauses downloading entirely.
- **Proxy** for playlist fetches and downloads - HTTP, SOCKS5, SOCKS4a or SOCKS4, with
  optional credentials and a bypass list. Changes take effect without a restart, and
  **Test proxy** checks it before saving.
- **Notifications** over SMTP, per event, sent in the background so a slow mail server never
  holds up a download. **Send test** checks the settings before saving.
- **Path mappings**, for when Sonarr and Radarr see the finished files under a different
  mount point than we write them to.
- **Crash recovery** at startup: partial files are trimmed back to the last checkpointed
  offset, interrupted downloads are requeued, and nothing has to be put right by hand after
  a `docker kill` or a power cut.
- **Search history** recording what each query answered with, and **download history** with
  retention limits for both.
- A **path picker** on every field that takes a path, opening over the page rather than
  inside the form.
- **Data protection keys** persisted to a volume, so a rebuild does not sign everyone out.

[Unreleased]: https://github.com/smurfster/smurfm3u/compare/v1.2.7...HEAD
[1.2.7]: https://github.com/smurfster/smurfm3u/compare/v1.2.6...v1.2.7
[1.2.6]: https://github.com/smurfster/smurfm3u/compare/v1.2.5...v1.2.6
[1.2.5]: https://github.com/smurfster/smurfm3u/compare/v1.2.4...v1.2.5
[1.2.4]: https://github.com/smurfster/smurfm3u/compare/v1.2.3...v1.2.4
[1.2.3]: https://github.com/smurfster/smurfm3u/compare/v1.2.2...v1.2.3
[1.2.2]: https://github.com/smurfster/smurfm3u/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/smurfster/smurfm3u/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/smurfster/smurfm3u/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/smurfster/smurfm3u/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/smurfster/smurfm3u/releases/tag/v1.0.0
