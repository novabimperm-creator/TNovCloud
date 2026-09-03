# TNovCloud

TNovCloud is a common data environment with immutable major file versions,
a real WinFsp volume in Windows Explorer, and sequential approval workflows.

The project is licensed under AGPL-3.0-or-later. See `LICENSE` and
`THIRD-PARTY-NOTICES.md`.

## Architecture

- `src/TNovCloud.Api` - ASP.NET Core API for Linux, PostgreSQL and file storage.
- `src/TNovCloud.Shared` - API contracts.
- `src/TNovCloud.Client` - x64 WPF session/tray client and shell-command IPC host.
- `src/TNovCloud.FileSystemHost` - x64 WinFsp volume with a full-file cache and
  SQLite manifest.
- `src/TNovCloud.ShellExtension` - native x64 `IExplorerCommand` COM DLL.
- `packaging/TNovCloud.Package` - sparse MSIX manifest, certificate, signing,
  registration and removal scripts.
- `tests/TNovCloud.Tests` - path, cache state, IPC, optimistic concurrency,
  soft-delete and approval-lock tests.

Files are downloaded lazily on first open. A cached version can be read while
the server is unavailable. New files remain pending locally until upload
succeeds. Existing files are writable only after checkout; check-in publishes
the next immutable major version.

## Server

The Linux host runs three containers on one Docker network `tnovcloud`:

| Service | Image | Published ports | Role |
| --- | --- | --- | --- |
| `db` | `postgres:16-alpine` | none | Metadata and approvals |
| `api` | built from `src/TNovCloud.Api/Dockerfile` | none | File API, JWT, storage volume |
| `proxy` | `nginx:alpine` | `80` | Reverse proxy; the only entry point |

PostgreSQL `5432` and the API `8080` are not published to the host or the
internet. Windows clients use `http://<docker-host>/` (no port). Nginx config
lives in `deploy/nginx/nginx.conf`. TLS is prepared in
`deploy/nginx/ssl.conf.example`.

### Ubuntu Docker host VM

Start with a dedicated Ubuntu 24.04 VM, not a mixed-use desktop.

| Profile | vCPU | RAM | System disk | File volume |
| --- | --- | --- | --- | --- |
| Pilot, 5–10 users | 4 | 8 GB | 40 GB | 100 GB+ |
| Department, 15–40 users | 8 | 16 GB | 40 GB | 500 GB+ |

RAM split at idle/load: Ubuntu + Docker ~1 GB, PostgreSQL 1–2 GB, API 1–2 GB
(peaks during large CAD uploads), nginx under 128 MB. Give the rest to the
page cache. CPU matters for parallel check-in/download; 4 cores is the floor.

Disk for BIM/CAD archives is the real constraint. Attach a separate data disk
and later bind-mount it over the `files` volume if the store outgrows the
system disk.

A GitHub Actions self-hosted runner is a follow-up step after the first
manual `compose up` succeeds. Budget +1 vCPU / +1 GB if the runner stays on
the same VM.

### Deploy

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl git
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker "$USER"
# re-login after adding the user to the docker group

git clone https://github.com/novabimperm-creator/TNovCloud.git
cd TNovCloud
cp .env.example .env
# set POSTGRES_PASSWORD and JWT_KEY in .env

docker compose up --build -d
docker compose ps
curl -fsS http://127.0.0.1/health
```

Windows client server URL: `http://<vm-address>/`.

EF Core migrations are applied at API startup. Development accounts are
`admin/admin` and `designer/designer`. Change them and `JWT_KEY` before any
network exposure.

## Windows prerequisites

Run from PowerShell:

```powershell
.\scripts\Test-Prerequisites.ps1
```

Required:

- .NET 10 SDK/runtime;
- WinFsp 2.2.26215 Beta4 (`.\scripts\Install-WinFsp.ps1`);
- Visual Studio Build Tools with Desktop development with C++ (MSVC x64);
- Windows 11 SDK with `MakeAppx.exe` and `SignTool.exe`.

Only x64 builds are supported so Explorer, COM and the sparse package always
load the same architecture.

## Build and tests

Managed projects:

```powershell
dotnet build src/TNovCloud.Api/TNovCloud.Api.csproj -c Release
dotnet build src/TNovCloud.Client/TNovCloud.Client.csproj -c Release -p:Platform=x64
dotnet build src/TNovCloud.FileSystemHost/TNovCloud.FileSystemHost.csproj -c Release
dotnet test tests/TNovCloud.Tests/TNovCloud.Tests.csproj -c Release -p:Platform=x64
```

PostgreSQL integration tests expect a local cluster on `127.0.0.1:55432` with user
`tnovcloud` (override with `TNOV_PG_ADMIN`). After publishing the host, the
WinFsp smoke test mounts one volume, downloads a file, then rereads it with
the API stopped:

```powershell
.\scripts\Test-WinFspSmoke.ps1
```

Native extension, sparse package and development installation:

```powershell
.\scripts\Install-Development.ps1 -DriveLetter T
```

This publishes the client and host to
`%LOCALAPPDATA%\Programs\TNovCloud\App`, builds the `/MT` native extension,
creates and trusts a current-user development certificate, signs the sparse
MSIX and registers `windows.fileExplorerContextMenus`. The certificate is for
development only; production releases must use an organizational code-signing
certificate.

After installation, sign in to the client. The WinFsp volume is mounted and
the client records its drive letter and serial under
`HKCU\Software\TNovCloud\Mount`. Explorer then displays the `TNovCloud`
submenu in the Windows 11 primary context menu only for items on that volume.

Remove the development installation with:

```powershell
.\scripts\Uninstall-Development.ps1
```

## Explorer behavior

- Checkout refreshes the manifest and enables writes for the current user.
- Normal saves, temporary-file writes and rename/replace patterns are handled
  by WinFsp.
- Check-in verifies the expected server major version before publishing.
- Rename, move and delete use metadata revisions and are rejected while an
  affected file is in approval.
- Delete is soft-delete on the server and can be restored through the API.
- The shell extension performs no network work in `GetState`; `Invoke` sends
  a length-prefixed UTF-8 JSON command through a current-user named pipe.

The first implementation uses a full-file cache. A block/range cache for very
large CAD/BIM files is intentionally deferred until production access
patterns have been measured.
