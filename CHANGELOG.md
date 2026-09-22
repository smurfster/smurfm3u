# Changelog

Notable changes to Smurfm3u, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The version is set in `Directory.Build.props` and shown at the bottom of the sidebar, so
what the UI reports is always what was built.

## [Unreleased]

### Added

- **Xtream panel playlists**, read through the panel's player API instead of as an m3u. The
  panel states the season, episode, episode title, container and runtime outright, so none of
  it has to be read back out of an entry name, and nothing live is requested in the first
  place. **Test connection** checks the login before saving, and a pasted `get.php` link is
  accepted whole: the credentials are lifted out of it and only the address is kept.
- A **light theme** alongside the dark one, with a toggle in the top bar. It follows the
  system preference until a choice is made here, and then remembers that choice per browser.
- **Icons** on the navigation links, drawn inline so there is nothing to fetch.

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

[Unreleased]: https://github.com/smurfster/smurfm3u/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/smurfster/smurfm3u/releases/tag/v1.0.0
