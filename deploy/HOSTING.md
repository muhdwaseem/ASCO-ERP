# Hosting ASCO for client testing

`deploy/publish.ps1` builds one package (`deploy/out`): the API serves the app itself, so the
client gets a single address. `run-hosted.cmd` starts it in **Production** mode on
`http://127.0.0.1:8080` with its own clean demo database (`asco_hosted.db`).

```powershell
powershell -ExecutionPolicy Bypass -File deploy\publish.ps1     # build
deploy\out\run-hosted.cmd                                        # asks for the new demo password, then runs
```

The client signs in as `owner@aegiserp.com` (full access), `accounts@aegiserp.com` (accountant)
or `readonly@aegiserp.com` (view only), all with the password you chose.

## Option A: test link from this PC (free, about 5 minutes)

Cloudflare's quick tunnel gives an `https://<random>.trycloudflare.com` address with no account.

```powershell
winget install Cloudflare.cloudflared            # once
deploy\out\run-hosted.cmd                        # window 1
cloudflared tunnel --url http://127.0.0.1:8080   # window 2: prints the https link to send
```

- The link only works while this PC is on, awake and both windows are open.
- The address changes each time cloudflared starts.
- Good for a demo call or a few days of testing.

## Option B: always-on test server (about $5-10 a month)

A small Linux VM (Hetzner, DigitalOcean, Azure or AWS Lightsail) with your own sub-domain, for example `asco-demo.yourcompany.com`:

1. Build for Linux with `deploy\publish.ps1 -Runtime linux-x64`, then copy `deploy/out` to the server.
2. Run it as a systemd service with the same variables as `run-hosted.cmd`.
3. Put **Caddy** in front: two lines of config, and it gets a free HTTPS certificate automatically.
4. Use Postgres instead of SQLite once real client data goes in. Back it up nightly.

## What protects a hosted copy (built in)

| Risk | Protection |
|---|---|
| Logging in with the demo passwords from the source code | The app **refuses to start** until `Security__DemoUsersPassword` replaces them |
| Password guessing | Lockout after 5 wrong attempts; at most 10 login tries per minute per IP; failed sign-ins are logged |
| Stolen session / cookie theft | Cookie is HttpOnly, Secure-only, SameSite=Strict and expires after 8 hours |
| Another site posting forms as the user (CSRF) | Every change needs the `X-ASCO` header, which other sites cannot send |
| Script injection / click-jacking | Strict Content-Security-Policy, X-Frame-Options DENY, no inline scripts |
| Seeing another company's books | Every request is checked against the user's company grants, plus post/payroll/admin rights |
| Flooding the API | 600 requests per minute per user (`Security__ApiRequestsPerMinute`) |
| Plain-HTTP downgrade | HSTS; HTTPS comes from the tunnel or proxy (`Security__BehindProxy=true`) |
| Financial data left in browser caches | All `/api` responses are `no-store` |
| Vulnerable packages | `dotnet list package --vulnerable` and `npm audit`: 0 found (01 Oct 2026) |

**Before real client data:**
- give each person their own login, and switch on two-factor sign-in;
- use Postgres with backups;
- restrict `AllowedHosts` to your domain;
- keep `deploy/out` (database and `keys` folder) out of git and off shared drives.
