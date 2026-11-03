# LAN Launcher Pi – erste Testversion

Dieser Verwaltungsserver läuft mit Python 3 (Standardbibliothek) auf einem
Raspberry Pi 1 Model B / ARMv6. Die Pakete liegen auf NAS4Free; der Pi liefert
**keine** Paketbytes über HTTP aus. Der Windows-Client kopiert per SMB.

## Installation auf dem Raspberry Pi

Voraussetzung: `/mnt/lanlauncher-nas/LANLauncher/Games` und `Mods` sind
nach dem Neustart erreichbar. Python prüfen:

```bash
python3 --version
```

Das Repository auf den Pi holen (falls noch nicht vorhanden):

```bash
sudo apt install -y git python3
git clone --branch feature/raspberry-pi-nas-downloads \
  https://github.com/philippwolff/LAN_Launcher.git ~/LAN_Launcher
```

Admin-Schlüssel nur auf dem Pi ablegen:

```bash
sudo mkdir -p /etc/lanlauncher
sudo sh -c 'umask 077; python3 -c "import secrets; print(secrets.token_urlsafe(36))" > /etc/lanlauncher/admin-key'
sudo chmod 600 /etc/lanlauncher/admin-key
```

Der Prozess benötigt Leserechte auf die Schlüsseldatei; für den ersten Test
daher mit `sudo` starten:

```bash
sudo python3 ~/LAN_Launcher/src/LanLauncher.Pi/server.py \
  --admin-key-file /etc/lanlauncher/admin-key
```

In einem zweiten SSH-Terminal prüfen:

```bash
curl http://127.0.0.1:8080/api/health
curl http://127.0.0.1:8080/api/capabilities
curl http://127.0.0.1:8080/api/games
```

Zum Beenden `Strg+C`. Noch **keinen** automatischen Dienst einrichten;
zuerst die Schnittstellen testen und den Dienst mit eingeschränkten
Berechtigungen absichern.

## Erstes Spiel registrieren

Eine `.zip` oder `.exe` Datei direkt in den NAS-Ordner
`LANLauncher/Games` kopieren, z. B. `Demo.zip`. Die Registrierung
erfolgt zunächst per API:

```bash
curl -X POST http://127.0.0.1:8080/api/admin/games/register \
  -H "X-Admin-Key: $(sudo cat /etc/lanlauncher/admin-key)" \
  -H "Content-Type: application/json" \
  -d '{"id":"demo","name":"Demo","version":"1.0","relativePath":"Games/Demo.zip"}'
```

**Achtung:** Ein Admin-Schlüssel in einem HTTP-Header wird im LAN unverschlüsselt
übertragen. Den Admin-Endpunkt zunächst nur lokal auf dem Pi aufrufen und
den Schlüssel nicht in Screenshots, Logs oder öffentlich geteilten Befehlen
veröffentlichen.

Die NAS muss für die Windows-Clients über
`\\192.168.68.250\Daten\LANLauncher` erreichbar sein. Dafür später einen
eigenen SMB-Leseaccount einrichten; den bestehenden NAS-Schreibaccount
nicht an alle Spieler verteilen.

## Unterstützte Schnittstellen

- `GET /api/health`, `GET /api/capabilities`
- `GET /api/games`, `GET /api/games/{id}/package-location`
- `POST /api/join`, `POST /api/status`, `GET /api/players`
- `GET /api/games/{id}/test-status|start-notice|tags|mod|launch-options|launch-path`
- `GET /api/admin/check`
- `POST /api/admin/games/register` (NAS-Datei muss existieren)
- `PUT /api/admin/games/{id}/...` für grundlegende Metadaten

## Noch nicht fertig

Admin-Uploads aus dem WPF-Client, Import, Löschen, serverseitige
Bibliotheks-Synchronisierung, vollständige Metadatenvalidierung,
separater Dienstbenutzer, systemd-Autostart und Ende-zu-Ende-Tests.
Diese Version ist nur für ein vertrauenswürdiges LAN und erste Tests
gedacht. Der Windows-Server bleibt unverändert.
