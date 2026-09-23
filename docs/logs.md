# Logs

**Logs** shows the last 1,000 lines as they are written, without reaching for
`docker compose logs`. Newest first, so nothing scrolls out from under you while you are
reading it.

| Control | What it does |
| --- | --- |
| Level | Hides anything below it. Defaults to information and above, which is what the service is configured to write |
| Filter | Matches the message, the source and the exception text |
| Pause | Stops the page updating. What arrives meanwhile is dropped rather than queued, so resuming shows now rather than replaying a backlog &mdash; the lines are still in the buffer either way |
| Clear | Empties the buffer |

Playlists narrate themselves, so a refresh can be watched rather than waited on:

| When | What is written |
| --- | --- |
| Added, edited, deleted, enabled, disabled | Who did it and to what. An edit names the fields that changed, never their values &mdash; one of them is a panel password |
| Refresh starts | Which playlist, its kind, and where it is being read from |
| Playlist fetched | The status and the size, which is how a one-line error page from a provider shows up before the parse gets to it |
| Panel connected | The address, the account and its status |
| Every 25,000 entries | How many read, how many on demand, how many new |
| Every 200 series | Progress through the episode walk, which is the slow part |
| Finished | On-demand of total, new, retired, and how long it took |

Locations are **redacted** before they are written: a provider's link carries the password in
its query string, and the log is a page in the web UI. The same masking applies to the
location in a refresh-failed email.

This is a **second copy** of what the container logs, not a replacement: everything still
goes to stdout exactly as before, and `docker compose logs` is still the place to look for
anything older than the buffer or from before the last restart.

It is deliberately in memory only and deliberately bounded. A log that grows without limit is
a memory leak with a nicer name, and one written to the database would have every refresh
writing rows about writing rows.

What reaches the page is whatever the configured levels let through, the same as the console.
Those live under `Logging:LogLevel` and can be set per category from the environment:

```env
Logging__LogLevel__Default=Information
Logging__LogLevel__Smurfm3u.App.Services.FileDownloader=Debug
```

`System.Net.Http.HttpClient` is pinned to `Warning`, because at information level it writes
four lines per request &mdash; which during an Xtream series walk is several thousand lines
saying nothing.


[← Back to the index](../README.md)
