# Changelog

Notable changes to Smurfm3u, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The version is set in `Directory.Build.props` and shown at the bottom of the sidebar, so
what the UI reports is always what was built.

## [Unreleased]

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

### Changed

- A grab now holds one or more files instead of exactly one. A film or an episode is still a
  single file and lands on disk exactly where it always did; existing downloads and history
  are carried across unchanged.
- A pack is worked out when the grab arrives rather than when the search ran, so a grab that
  lands an hour later picks up an episode that turned up in between, and a season withdrawn in
  the meantime is refused rather than handed over as a list of dead links.

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

[Unreleased]: https://github.com/smurfster/smurfm3u/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/smurfster/smurfm3u/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/smurfster/smurfm3u/releases/tag/v1.0.0
