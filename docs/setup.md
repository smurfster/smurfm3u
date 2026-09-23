# Setting it up

```bash
docker compose up -d
```

The web UI is on <http://localhost:8090>.

On first run the app creates its schema, an `admin` account and an API key. If you did not
set them in the environment, both are generated and written to the log:

```bash
docker compose logs smurfm3uapp | grep -E "API key|generated password"
```

Set them up front instead by creating a `.env` beside `docker-compose.yml`:

```env
POSTGRES_PASSWORD=change-me
ADMIN_USERNAME=admin
ADMIN_PASSWORD=something-long
API_KEY=your-own-api-key
HTTP_PORT=8090
TZ=Europe/London
```

The account and API key settings seed the database on first run only. Afterwards the UI is
the source of truth.

## Where the data lives

Downloads land on the host, under `./data/downloads` beside `docker-compose.yml` unless you
say otherwise. Everything else lives in Docker volumes:

| What | Where | Holds |
| --- | --- | --- |
| `DOWNLOADS_PATH` | `./data/downloads` on the host | Completed and in-progress downloads |
| `smurfm3u-db-data` | Docker volume | The database |
| `smurfm3uapp-config` | Docker volume | Data protection keys, so a rebuild does not sign everyone out |

Point the downloads somewhere else — another disk, a NAS mount — with a `.env` entry:

```env
DOWNLOADS_PATH=/mnt/media/smurfm3u
```

A relative path must start with `./` or `../`, which is Compose's rule rather than ours.
Only the host side moves: inside the container it stays at `/downloads`, because that is
what the app is configured with and what it reports to Sonarr and Radarr.

To add local `.m3u` files as a playlist source, uncomment the `/playlists` line in
`docker-compose.yml` and set `PLAYLISTS_PATH` to the directory holding them. Remote playlist
URLs need nothing mounted.

That folder is where the **Browse** button on a local playlist opens, but it is not a
boundary. Every field that takes a path has a picker, and each browses wherever the container
can read, because the box beside it already accepts any path typed into it. Change where the
playlist picker opens with **Playlist directory** under Settings → Downloads.


[← Back to the index](../README.md)
