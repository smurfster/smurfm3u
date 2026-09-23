# Development

## Layout

| Project | What lives there |
| --- | --- |
| `src/Smurfm3u.Core` | Entities, playlist parsing, VOD classification, release naming |
| `src/Smurfm3u.Data` | EF Core `AppDbContext` and migrations |
| `src/Smurfm3u.App` | Web UI, the two APIs, the download engine and the schedulers |
| `tests/Smurfm3u.Core.Tests` | Parser tests over real-world playlist name shapes |

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
