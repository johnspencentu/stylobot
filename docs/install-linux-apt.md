# Linux APT Install (Debian/Ubuntu)

StyloBot is published to a Cloudsmith apt repository on every stable release.

## Install

```bash
# 1. Add the repository and GPG key (one time)
curl -1sLf 'https://dl.cloudsmith.io/public/scottgal/stylobot/setup.deb.sh' | sudo bash

# 2. Install
sudo apt update
sudo apt install stylobot
```

Supports: Debian 11+, Ubuntu 20.04+, and any apt-compatible distro on x64 or ARM64.

## Upgrade

```bash
sudo apt update && sudo apt upgrade stylobot
```

## Verify the binary

```bash
which stylobot      # /usr/local/bin/stylobot
stylobot --version
```

## Quick start after install

```bash
# Demo mode (verbose, no blocking)
stylobot 5080 https://www.example.com

# Production mode (blocking enabled)
stylobot 5080 https://www.example.com --mode production

# With Cloudflare public tunnel
stylobot 5080 https://www.example.com --tunnel

# Full reference
stylobot man
```

## Manual download

If you prefer not to use the apt repo, download the binary directly from the
[GitHub Releases](https://github.com/scottgal/stylobot/releases) page:

| Platform | File |
|----------|------|
| Linux x64 | `stylobot-linux-x64.tar.gz` |
| Linux ARM64 (Raspberry Pi 4/5) | `stylobot-linux-arm64.tar.gz` |

```bash
tar xzf stylobot-linux-x64.tar.gz
chmod +x stylobot
sudo mv stylobot /usr/local/bin/
```
