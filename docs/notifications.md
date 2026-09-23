# Notifications

Set up under **Settings → Notifications**. Turn it on, tick the events you care about, fill
in the mail server, and use **Send test** to check it before saving.

| Event | When it fires |
| --- | --- |
| Download queued | Sonarr or Radarr grabs a release |
| Download started | The transfer begins &mdash; once only, not again when a restart resumes it |
| Download completed | The file is in place under the completed directory |
| Download failed | After the last retry, not once per attempt |
| Playlist refresh failed | A playlist could not be read |

Completed, failed and refresh-failed are on by default. Queued and started fire on every
grab, so they are worth leaving off unless the instance is quiet.

SMTP is the only protocol so far:

| Field | Notes |
| --- | --- |
| Port | `587` for STARTTLS, `465` for implicit TLS, `25` for a relay that needs no login |
| Implicit TLS | On for `465` only. Everything else negotiates STARTTLS when the server offers it |
| Username | Leave blank for an unauthenticated relay |
| To | Several addresses may be separated by commas |

Messages are plain text: the headline and release name in the subject, the detail in the
body.

```
[Smurfm3u] Download completed: Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u

Download completed
Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u

Category: tv
Playlist: Evo
Size: 2.4 GiB
Took: 0h 12m
Folder: /downloads/complete/tv/Top.Gear.2002.S01E03.1080p.WEB-DL-Smurfm3u
```

Sending happens in the background, so a mail server that is slow or down never holds up or
fails a download &mdash; the attempt is logged and abandoned.

> The SMTP password is stored as written, in the same settings row as the API key. That is
> what SMTP has to present, so it cannot be hashed. Use an app password rather than your
> main account password, and treat a database backup as containing a credential.


[← Back to the index](../README.md)
