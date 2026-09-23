# Connecting the *arr apps

Three things have to be true: the *arr apps can reach Smurfm3u, Prowlarr is pointed at the
Newznab endpoint, and Sonarr and Radarr send Smurfm3u grabs to the Smurfm3u download client
rather than to a real SABnzbd. Work through the steps in order.

### 1. Find the right address

The Settings page shows the address **your browser** used to reach the UI. That is not
always an address another container or another machine can use, so pick the one that
matches your setup:

| Where the *arr app runs | Address to use |
| --- | --- |
| Same Docker network as Smurfm3u | `smurfm3u` port `8080` — the container name and its internal port |
| Another host on your LAN | The Smurfm3u host's IP and published port, e.g. `192.168.1.10` port `8090` |

`localhost` only works if the *arr app runs directly on the Smurfm3u host, outside a
container. Inside a container, `localhost` means that container itself.

Check the address before configuring anything, from the machine or container the *arr app
runs in:

```bash
curl -sS -m 10 -o /dev/null -w "%{http_code}\n" "http://ADDRESS:PORT/api?t=caps&apikey=YOUR_API_KEY"
```

`200` means you are ready. Anything else, fix it here first — nothing below can work until
this responds.

**Behind a VPN container?** If an *arr app uses `network_mode: service:gluetun` or similar,
the VPN container's firewall blocks traffic to your LAN by default, and it *drops* that
traffic rather than refusing it — so tests hang on a spinner instead of failing. Allow your
subnet on the VPN container, then recreate it along with everything sharing its network:

```yaml
environment:
  - FIREWALL_OUTBOUND_SUBNETS=192.168.1.0/24
```

A firewall on the Smurfm3u host drops traffic the same way, with the same symptom, so check
both.

### 2. Prowlarr — add the indexer

Add a *Generic Newznab* indexer:

| Field | Value |
| --- | --- |
| URL | `http://smurfm3u:8080` — host only, no path |
| API Path | `/api` — the default, leave it |
| API Key | from Settings |
| Categories | Movies (2000), TV (5000) |

> **The URL field takes the host only.** Prowlarr joins `URL` and `API Path` together to
> build its requests, so putting `/api` in the URL field asks for `/api/api`, which is not
> a route here and returns 404.

The categories Smurfm3u advertises:

| Movies | TV |
| --- | --- |
| 2000, 2030 (SD), 2040 (HD), 2045 (UHD) | 5000, 5030 (SD), 5040 (HD), 5045 (UHD) |

### 3. Sonarr and Radarr — add the download client

Add a *SABnzbd* download client:

| Field | Value |
| --- | --- |
| Name | `Smurfm3u` |
| Host | `smurfm3u` — host only, no `http://` and no port |
| Port | `8080` |
| URL Base | *leave empty* |
| API Key | the same key |
| Category | `tv` in Sonarr, `movies` in Radarr |
| Client Priority | a worse (higher) number than your real SABnzbd, e.g. `50` |
| Tags | *leave empty* |

**Leave Tags empty.** Tags on a download client are matched against the tags on a *series or
movie*, not against the indexer. A tagged client is skipped for anything not carrying that
tag, and it also breaks step 4.

**Client Priority matters.** Sonarr and Radarr consider only their best priority tier of
download clients, so a worse number keeps Smurfm3u from being picked for your real Usenet
indexers. Without it, roughly half of those grabs are handed to Smurfm3u, which rejects them
with `This nzb did not come from Smurfm3u`.

### 4. Sonarr and Radarr — pin the indexer to the client

This is the step that stops Smurfm3u grabs reaching a real SABnzbd, and it is easy to miss
because the field is hidden by default.

1. **Settings → Indexers**, and click the Smurfm3u indexer.
2. In the dialog's bottom row of buttons, click the **gear icon** between *Delete* and
   *Test*. Advanced fields appear.
3. Change **Download Client** from `Any` to `Smurfm3u`.
4. **Save**, then repeat in Radarr.

An indexer with a download client set always uses it, ahead of priority and of the usual
round-robin. Prowlarr preserves the value when it syncs indexers, so you set it once.

Leave your other indexers on `Any` — the priority from step 3 keeps them off Smurfm3u. To
guarantee it even while your real SABnzbd is unavailable, set **Download Client** explicitly
on those indexers too.

### 5. Let Sonarr and Radarr see the finished files

Smurfm3u reports its completed folder as `/downloads/complete`, with a subfolder per
category, so a finished TV grab lands in `/downloads/complete/tv/<Release.Name>/`.

Sonarr and Radarr have to be able to read that themselves. There are two parts to this, and
they are easy to confuse: the files must be **reachable**, and the path must **match**.

**Reachable** — if the *arr app runs on the same host, mount the same directory into every
container. If it runs elsewhere, share the directory over SMB or NFS and mount it there.
Nothing below substitutes for this: no amount of path rewriting grants access to files an
app cannot open.

**Matching** — if that mount lands somewhere other than `/downloads/complete`, tell Smurfm3u
under **Settings → Path mappings**:

| Our path | As Sonarr and Radarr see it |
| --- | --- |
| `/downloads/complete` | `/mnt/smurfm3u/complete` |

Every path handed to a client is then rewritten, so a finished grab is reported at
`/mnt/smurfm3u/complete/tv/<Release.Name>/` and imports without either app needing its own
Remote Path Mapping. Where the files are actually written never changes.

Add a row per location that differs. The longest match wins, so a mapping for a subfolder
beats one for its parent, and a path only matches on a whole folder name — `/downloads` will
not match `/downloads-old`. If the target is a Windows path the separators follow it, so
`D:\media\complete` yields `D:\media\complete\tv\Show`.

You can still use each app's own Remote Path Mapping instead if you prefer; doing it here
just means configuring it once rather than in every app.

## Good to know

Both APIs answer on `/api`. A SABnzbd request always carries a `mode` parameter and a
Newznab request never does, which is what tells them apart — so neither app needs a custom
URL base. `/newznab/api` and `/sabnzbd/api` are also routed if you would rather be explicit.

An empty query is a feed of the newest entries rather than a result set to be walked. Its
size is capped by **Browse feed size** under Settings (100 by default), because Prowlarr
keeps asking for the next page until a short one comes back: uncapped, one RSS sync costs a
request every two seconds until it hits its own thirty-page ceiling. Set it to 0 to remove
the cap. A real query is unaffected and pages as far as the client wants.

Searches are text-only. Smurfm3u has no TVDB or IMDb mapping and does not claim to, so the
*arr apps match on title, year, season and episode. How well that works depends on release
naming, covered below.

## If something does not work

| Symptom | Likely cause | Go to |
| --- | --- | --- |
| Prowlarr's Test spins and never finishes | Traffic is being dropped: wrong address, VPN container firewall, or host firewall | Step 1 |
| Prowlarr's Test fails straight away | Wrong URL shape, usually `/api` in the URL field | Step 2 |
| `Incorrect user credentials` | The API key does not match | Copy it again from Settings |
| Grabs fail about half the time | Those grabs went to a real SABnzbd | Steps 3 and 4 |
| `This nzb did not come from Smurfm3u` | A real Usenet nzb was sent to Smurfm3u | Step 3, Client Priority |
| `No such item` | The playlist entry is gone, or the database was reset | Search again and re-grab |
| Downloads finish but never import | The *arr app cannot reach the completed folder, or sees it at a different path | Step 5 |


[← Back to the index](../README.md)
