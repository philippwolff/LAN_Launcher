#!/usr/bin/env bash
set -euo pipefail

REPO_DIR="${HOME}/LAN_Launcher"
SOURCE_DIR="${REPO_DIR}/src/LanLauncher.Pi"
TARGET_DIR="/opt/lanlauncher-pi"
SERVICE="lanlauncher-pi.service"

if [[ ! -d "${REPO_DIR}/.git" ]]; then
  echo "Repository fehlt: ${REPO_DIR}" >&2
  exit 1
fi

cd "${REPO_DIR}"
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
  echo "Lokale Änderungen vorhanden. Update abgebrochen, um nichts zu überschreiben." >&2
  exit 1
fi

git pull --ff-only
for file in server.py admin.html admin.js; do
  [[ -f "${SOURCE_DIR}/${file}" ]] || { echo "Datei fehlt: ${file}" >&2; exit 1; }
done

python3 -m py_compile "${SOURCE_DIR}/server.py"
sudo install -d -o root -g root -m 755 "${TARGET_DIR}"
for file in server.py admin.html admin.js; do
  sudo install -o root -g root -m 644 "${SOURCE_DIR}/${file}" "${TARGET_DIR}/${file}"
done
sudo install -o root -g root -m 644 "${SOURCE_DIR}/lanlauncher-pi.service" "/etc/systemd/system/${SERVICE}"
sudo systemctl daemon-reload
sudo systemctl restart "${SERVICE}"
sleep 2
sudo systemctl --no-pager --full status "${SERVICE}"
