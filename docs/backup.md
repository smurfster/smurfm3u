# Backup

**Backup** in the sidebar saves the configuration to a file, and puts it back from one.

## What is in a backup

| | |
| --- | --- |
| **Settings** | everything on the Settings page, the API key included |
| **Playlists** | each one's address, login, tags, schedule and limits |
| **Speed limit windows** | all of them |
| **Logins** | each username and its password hash |

What is left out is everything that can be got back without it. The cache is most of the
database, and a refresh rebuilds it — see [Cache](cache.md). The download and search history
describe this install rather than configure it.

So a backup is small, a few kilobytes, however large the catalogue has grown.

**Keep it private.** It holds the API key Prowlarr, Sonarr and Radarr use, the panel passwords
as they were entered, and the password hashes.

## Restoring

Pick a backup file under **Restore from a backup**. The page shows what is in it — when it was
made, by which version, and which playlists and logins it holds — and nothing changes until
you press **Restore this backup**.

A restore does this:

| | |
| --- | --- |
| Settings | replaced, API key and all, so the *arr apps carry on without being told anything |
| Speed limit windows | replaced as a set |
| Playlists | matched by name. A match is updated in place, so it keeps its cache and history; anything new is added, empty until it is refreshed; a playlist not in the backup is left alone |
| Logins | matched by username. A match gets the backup's password; anything new is added; a login not in the backup is left alone, so the one you are restoring with still works |

The database part happens in one transaction: if anything fails, nothing is changed.

A backup made by a newer version of Smurfm3u is refused. Update first, then restore it.

### Moving to a new machine

1. Download a backup from the old install.
2. Start the new one and sign in with the login it generates or you gave it.
3. Restore the backup, then refresh each playlist on the Playlists page.

The logins from the backup work from then on, and the API key is the old one, so nothing on
the *arr side needs changing beyond the address if that moved.

[← Back to the index](../README.md)
