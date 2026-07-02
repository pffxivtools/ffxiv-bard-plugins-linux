# Running Multiple FFXIV Clients on Linux

If you want to run multiple game profiles side by side — for example, to have one character perform music while another character plays — you need each launcher instance to use its own configuration. The cleanest way to do that on Linux is to run each profile under a separate local Linux user.

Each user gets their own home directory and `.xlcore` config, so launcher settings, Wine prefixes, and plugin state never collide.

## How It Works

You create one Linux user per game profile, then use a launcher script that runs `xivlauncher-core` under that user with the right environment variables (`HOME`, `USER`, `LOGNAME`, `DISPLAY`). The script uses `sudo -u` to switch users and `xhost` to grant display access.

## Prerequisites

- **XIVLauncher installed** — see the [XIVLauncher Linux install guide](https://goatcorp.github.io/faq/steamdeck.html)
- **The game installed** in a location readable by all account users
- **Base tools** — install these if you don't have them:

```bash
sudo apt install x11-xserver-utils mangohud
```

Package names vary by distribution — adjust for your distro.

## Step-by-Step

### 1. Create the Account List

Create a text file called `accounts.txt` with one Linux username per line:

```text
bard_one
bard_two
bard_three
```

Keep names lowercase, no spaces or special characters.

### 2. Create a Shared Group

If your game files live in a shared directory (like a Steam library), create a group so all account users can access them:

```bash
sudo groupadd ffxiv
sudo usermod -aG ffxiv "$USER"
sudo chgrp -R ffxiv /path/to/game/library
sudo chmod -R g+rwX /path/to/game/library
```

Log out and back in after adding yourself to the group.

### 3. Create the Linux Users

<details>
<summary>Script: create the users</summary>

```bash
while IFS= read -r account; do
  [[ -z "${account// }" ]] && continue
  [[ "$account" =~ ^[[:space:]]*# ]] && continue

  sudo useradd --create-home --shell /bin/bash "$account"
  sudo usermod -aG ffxiv "$account"
  sudo passwd -l "$account" >/dev/null
done < accounts.txt
```

This creates a home directory for each profile and locks password login (the accounts are only used through `sudo -u`).
</details>

<details>
<summary>Script: prepare launcher directories</summary>

```bash
while IFS= read -r account; do
  [[ -z "${account// }" ]] && continue
  [[ "$account" =~ ^[[:space:]]*# ]] && continue

  home_dir="$(getent passwd "$account" | cut -d: -f6)"
  sudo -u "$account" mkdir -p "$home_dir/.xlcore"
done < accounts.txt
```
</details>

### 4. Create the Launcher Script

Save this as `run-other.sh` next to your `accounts.txt`:

<details>
<summary><code>run-other.sh</code></summary>

```bash
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ACCOUNTS_FILE="$SCRIPT_DIR/accounts.txt"

account="${1:-}"

if [[ -z "$account" ]]; then
  echo "Usage: $0 <account>" >&2
  exit 1
fi

if [[ ! -f "$ACCOUNTS_FILE" ]]; then
  echo "Account list not found: $ACCOUNTS_FILE" >&2
  exit 1
fi

if ! grep -Fxq "$account" "$ACCOUNTS_FILE"; then
  echo "Account not found in account list: $account" >&2
  exit 1
fi

if ! id "$account" >/dev/null 2>&1; then
  echo "Linux user does not exist: $account" >&2
  exit 1
fi

account_home="$(getent passwd "$account" | cut -d: -f6)"
if [[ -z "$account_home" ]]; then
  echo "Could not determine home directory for $account" >&2
  exit 1
fi

printf 'account=%q\n' "$account"
printf 'home=%q\n' "$account_home"

sudo -u "$account" mkdir -p "$account_home/.xlcore"

# Allow this user to open windows in your current X11 session.
xhost +SI:localuser:"$account"

exec sudo -u "$account" env \
  HOME="$account_home" \
  USER="$account" \
  LOGNAME="$account" \
  DISPLAY="${DISPLAY:-}" \
  XAUTHORITY="${XAUTHORITY:-$HOME/.Xauthority}" \
  MANGOHUD=1 \
  MANGOHUD_CONFIG="fps_limit=30,no_display" \
  mangohud /usr/bin/xivlauncher-core
```
</details>

Make it executable and test it:

```bash
chmod +x run-other.sh
./run-other.sh bard_one
```

### 5. Launch All Profiles at Once (Optional)

Save this as `run-many.sh`:

<details>
<summary><code>run-many.sh</code></summary>

```bash
#!/usr/bin/env bash
set -euo pipefail

ACCOUNTS_FILE="${1:-accounts.txt}"

if [[ ! -f "$ACCOUNTS_FILE" ]]; then
  echo "Account list not found: $ACCOUNTS_FILE" >&2
  exit 1
fi

while IFS= read -r account; do
  [[ -z "${account// }" ]] && continue
  [[ "$account" =~ ^[[:space:]]*# ]] && continue

  ./run-other.sh "$account" &
  sleep 30
done < "$ACCOUNTS_FILE"
```
</details>

Make it executable and run:

```bash
chmod +x run-many.sh
./run-many.sh accounts.txt
```

The 30-second delay gives each launcher time to initialize before the next one starts.

## Optional: DXVK Config

If you want to limit VRAM per client (helpful when running multiple instances), add a DXVK config file to the `env` block in `run-other.sh`:

```bash
  DXVK_CONFIG_FILE=/opt/game-client/dxvk.conf \
```

Example config (`dxvk.conf`):

```ini
dxgi.maxDeviceMemory = 2048
dxgi.maxSharedMemory = 2048
```

Make sure every account user can read the file.

## Migrating Existing Profiles

If you already have launcher profiles saved, copy them into the target user's `.xlcore` directory:

```bash
sudo rsync -a --delete /path/to/profile-data/ "$account_home/.xlcore"/
sudo chown -R "$account:$account" "$account_home/.xlcore"
```

For character-specific configs, launch the account once first so the launcher creates its target directories, then copy only what you need.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `Linux user does not exist` | Run the user creation script from step 3 |
| `Account not found` | Add the username to `accounts.txt` |
| Launcher uses the wrong profile | Make sure `HOME`, `USER`, and `LOGNAME` are set inside the `env` block of `run-other.sh` |
| No window appears | Run `xhost +SI:localuser:<account>` from your desktop session and confirm `DISPLAY` is set |
| Permission errors on game files | Add the account user to the shared group and check directory permissions |
| `mangohud: command not found` | Remove `mangohud` from the final command in `run-other.sh`, or install MangoHud for your distribution |
