# NAS-/Raspberry-Pi-Transport (in Arbeit)

Der vorhandene Windows-Server bleibt unverändert. Ein neuer Client fragt optional
`GET /api/capabilities` ab. Alte Windows-Server ohne diesen Endpunkt bleiben beim
bisherigen HTTP-Download.

Beispielantwort des **zukünftigen** Raspberry-Pi-Servers:

```json
{
  "protocolVersion": 1,
  "downloadMode": "smb",
  "smbShare": "\\\\192.168.68.250\\Daten\\LANLauncher"
}
```

Die SMB-Freigabe wird vom Client nur als Pfad verwendet; keine NAS-Zugangsdaten
in API-Antworten oder im Programmcode hinterlegen. Windows muss bereits mit
einem separaten schreibgeschützten NAS-Konto Zugriff besitzen.

## Implementierungsstand

- Vorhanden: `ServerTransferResolver` und `GamePackageTransfer`; die Download- und
  Installationspfade im Client nutzen beide die neue Transportauswahl.
- Der Pi-Server muss `GET /api/capabilities` und
  `GET /api/games/{id}/package-location` bereitstellen. Letzterer antwortet mit
  `{ "relativePath": "Games/beispiel.zip" }` relativ zur angegebenen SMB-Freigabe.
- Noch offen: Raspberry-Pi-ARMv6-Server, NAS-Paketregistrierung, Admin-Uploads,
  separate NAS-Leseberechtigungen und Tests unter Windows.
- Bis der Pi-Server existiert, nutzt der Client den bisherigen HTTP-Download.
- Der bisherige Windows-Server und seine Bandbreitenoptionen werden nicht geändert.

## Geplante Ablage auf NAS4Free

`/mnt/lanlauncher-nas/LANLauncher/Games` und
`/mnt/lanlauncher-nas/LANLauncher/Mods` auf dem Pi;
Windows greift auf dieselben Dateien über den UNC-Pfad zu.
