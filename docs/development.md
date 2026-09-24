# Development

## Layout

| Project | What lives there |
| --- | --- |
| `src/Smurfm3u.Core` | Entities, playlist parsing, VOD classification, release naming |
| `src/Smurfm3u.Data` | EF Core `AppDbContext` and migrations |
| `src/Smurfm3u.App` | Web UI, the two APIs, the download engine and the schedulers |
| `tests/Smurfm3u.Core.Tests` | Parsing, naming and query interpretation, with no dependencies |
| `tests/Smurfm3u.Integration.Tests` | Searching, season packs and the cache, against a real Postgres |

## Working on it

```bash
docker run -d --name smurfm3u-db -e POSTGRES_DB=smurfm3u -e POSTGRES_USER=smurfm3u \
  -e POSTGRES_PASSWORD=smurfm3u -p 5432:5432 postgres:17-alpine

dotnet test
dotnet run --project src/Smurfm3u.App
```

Migrations:

```bash
dotnet ef migrations add <Name> -p src/Smurfm3u.Data -s src/Smurfm3u.Data
```

They are applied automatically at startup.

## Tests

Two suites, both run by `dotnet test` from the root.

| Project | What it covers | Needs |
| --- | --- | --- |
| `tests/Smurfm3u.Core.Tests` | Parsing, release naming, query interpretation, the panel's loose JSON, backoff and pacing | nothing |
| `tests/Smurfm3u.Integration.Tests` | Searching, season packs and the cache browser, against a real database | Docker |

The integration suite starts a throwaway `postgres:17-alpine` with
[Testcontainers](https://dotnet.testcontainers.org/), applies the real migrations to it, and
gives each test a clean set of rows. The container is started once for the whole run and
thrown away at the end; nothing touches a database you are using.

A real Postgres rather than a substitute, because what those tests are about only exists in
Postgres: the trigram indexes the matching leans on, `ILIKE`, array columns, and the set-based
updates and deletes. An in-memory provider would answer differently, which would make the
tests worse than none at all.

They exist for the behaviour that cannot be reached any other way &mdash; which reading of a
query wins depends on what the catalogue holds, so `open season 2` finding a film rather than
season 2 of a show is only demonstrable against rows. Fixtures build those rows through
`ReleaseTitleParser`, the same way an ingest would, so a test can never pass on data nothing
produces.

If Docker is not running the integration suite fails to start. Run the unit tests alone with:

```bash
dotnet test tests/Smurfm3u.Core.Tests
```

## Versioning

Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html), and changes are
recorded in [CHANGELOG.md](../CHANGELOG.md).

The number lives in `Directory.Build.props` and nowhere else. The app reads it back from its
own assembly and shows it under the sign-out button, so what the UI reports is always what
was built &mdash; hover it to see the commit. To cut a release:

```bash
# 1. Bump <Version> in Directory.Build.props
# 2. Move the Unreleased entries in CHANGELOG.md under the new heading
git commit -am "Release 1.1.0"
git tag -a v1.1.0 -m "1.1.0"
git push --follow-tags
```

The SABnzbd endpoint separately reports a *SABnzbd* version (`4.3.3`) to Sonarr and Radarr.
That is the protocol version they check against, not ours, and it does not move with releases.

[← Back to the index](../README.md)
