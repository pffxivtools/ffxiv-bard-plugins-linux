# Recreating A Multi-User Game Client Setup On Linux

This guide describes how to run the game client as separate local Linux users from one desktop session. The main pattern is a `run-other.sh` launcher: you start it from your normal desktop user, pass the account name, and it runs `xivlauncher-core` under the matching local Linux user.

This is useful when each game profile should have its own home directory, launcher config, Wine prefix, and game settings.

The examples use placeholders:

- `<account>`: a local Linux username for one game profile
- `<account-list>`: a text file containing one account username per line
- `<shared-group>`: a Linux group allowed to access shared game files
- `<dxvk-config>`: optional path to a DXVK config file

## Overview

The setup has four parts:

1. Install the game and `xivlauncher-core`.
2. Create one local Linux user per game profile.
3. Allow those users to access the display and shared game files.
4. Use `run-other.sh` to launch the client as the selected user.

The important idea is that the launched process receives the target user's `HOME`, `USER`, and `LOGNAME`, then runs through `sudo -u <account>`. That makes the launcher use that user's own `.xlcore` directory instead of your normal desktop user's config.

## Prerequisites

Install the base tools:

```bash
sudo apt install sudo x11-xserver-utils mangohud
```

Package names vary by distribution. You also need:

- Steam, Proton, Wine, or the runtime your launcher uses
- `xivlauncher-core` available on the system path, usually as `/usr/bin/xivlauncher-core`
- The game installed in a location readable by every account user
- A desktop session with `DISPLAY` set

If the game files live in a shared Steam library or shared install directory, create a group for access:

```bash
sudo groupadd <shared-group>
sudo usermod -aG <shared-group> "$USER"
```

Then make the shared game directory group-readable and group-writable if needed:

```bash
sudo chgrp -R <shared-group> /path/to/game/library
sudo chmod -R g+rwX /path/to/game/library
```

Log out and back in after changing your own group membership.

## Create The Account List

Create a simple text file with one local username per game profile:

```text
account_one
account_two
account_three
```

Save it as `accounts.txt` next to `run-other.sh`.

Use Linux-safe usernames: lowercase letters, numbers, underscores, and hyphens.

## Create Local Users

Create one Linux user for each account in `accounts.txt`:

```bash
while IFS= read -r account; do
  [[ -z "${account// }" ]] && continue
  [[ "$account" =~ ^[[:space:]]*# ]] && continue

  sudo useradd --create-home --shell /bin/bash "$account"
  sudo usermod -aG <shared-group> "$account"
  sudo passwd -l "$account" >/dev/null
done < accounts.txt
```

This creates a real home directory for each profile and locks password login for those users. They can still be used through `sudo -u`.

Prepare each launcher home directory:

```bash
while IFS= read -r account; do
  [[ -z "${account// }" ]] && continue
  [[ "$account" =~ ^[[:space:]]*# ]] && continue

  home_dir="$(getent passwd "$account" | cut -d: -f6)"
  sudo -u "$account" mkdir -p "$home_dir/.xlcore"
done < accounts.txt
```

## Create `run-other.sh`

Create a file named `run-other.sh`:

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

# Allow this local Linux user to open windows in your current X11 session.
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

Make it executable:

```bash
chmod +x run-other.sh
```

Launch one profile:

```bash
./run-other.sh <account>
```

## Optional DXVK Config

If you use a shared DXVK config file, add this line to the `env` block before `MANGOHUD=1`:

```bash
  DXVK_CONFIG_FILE=<dxvk-config> \
```

Example:

```bash
  DXVK_CONFIG_FILE=/opt/game-client/dxvk.conf \
```

Make sure every account user can read that file.

## Batch Launching

After `run-other.sh` works for one account, you can add a batch wrapper:

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

Save it as `run-many.sh`, make it executable, then run:

```bash
chmod +x run-many.sh
./run-many.sh accounts.txt
```

The delay gives each launcher time to initialize before the next one starts.

## Migrating Existing Launcher Profiles

If you already have saved launcher profiles, copy each profile into the target user's `.xlcore` directory:

```bash
sudo rsync -a --delete /path/to/profile-data/ "$account_home/.xlcore"/
sudo chown -R "$account:$account" "$account_home/.xlcore"
```

For character config folders, launch the account once first so the launcher creates its target directories. Then copy only the matching config data into that user's launcher config path.

## Troubleshooting

- `Linux user does not exist`: create the local user first.
- `Account not found in account list`: add the username to `accounts.txt`.
- Launcher opens under the wrong profile: confirm `HOME`, `USER`, and `LOGNAME` are set inside the `sudo -u ... env` block.
- No window appears: confirm `DISPLAY` is set and run `xhost +SI:localuser:<account>` from your desktop session.
- Permission errors reading game files: add the account user to the shared group and check group permissions on the game directory.
- MangoHud is missing: remove `mangohud` from the final command or install MangoHud for your distribution.
