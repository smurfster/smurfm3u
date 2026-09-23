# Proxy

Set up under **Settings → Proxy**. It covers **playlist refreshes and downloads** — the two
things that talk to your provider. Notifications go out over SMTP and do not pass through
it, and neither does anything Prowlarr, Sonarr or Radarr sends *in*.

| Option | What it does |
| --- | --- |
| Type | `HTTP`, `SOCKS5`, `SOCKS4a` or `SOCKS4` |
| Host | Name or IP. A whole URL pasted here is picked apart, and the type dropdown still decides the protocol |
| Port | `1080` is the usual SOCKS port, `8080` the usual HTTP one. A port typed into the host field wins |
| Username / password | Leave blank for a proxy that needs no login. SOCKS4 has no authentication at all |
| Bypass | Hosts that go direct instead, separated by commas or newlines |

**Test proxy** fetches your first enabled remote playlist through it, using what is on
screen, so it can be checked before saving. Any HTTP status counts as a pass — a provider
answering `403` to a bare fetch has still been reached through the proxy. With no remote
playlist configured it falls back to checking that something is listening on the port, and
says so rather than reporting a pass it did not earn.

Changes take effect on the next request. Nothing needs restarting, and a download already
running is not interrupted — it finishes on the route it started on.

Names are resolved **at the proxy**, not here, so a SOCKS5 tunnel leaks no DNS lookups for
your provider.

Bypass patterns match whole host names, case-insensitively:

| Pattern | Matches |
| --- | --- |
| `nas.local` | That host and nothing else |
| `192.168.*` | Anything starting `192.168.` |
| `*.example.com` | Subdomains of `example.com`, but not `example.com` itself |
| `.example.com` | `example.com` **and** everything under it |

> The proxy password is stored as written, in the same settings row as the API key and the
> SMTP password. That is what the proxy has to be presented with, so it cannot be hashed.
> Treat a database backup as containing a credential.

If the proxy is switched on but its address will not parse, the log says so at startup and
traffic goes direct rather than failing outright.


[← Back to the index](../README.md)
