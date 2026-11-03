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

- Vorhanden: `ServerTransferResolver` mit optionaler Erkennung, HTTP-Fallback
  und Prüfung relativer SMB-Pfade.
- Noch offen: Resolver in den bestehenden Download-/Installationsablauf einbinden,
  Fortschritt und Downloadrate beim Kopieren messen, Paketpfade über die API
  bereitstellen, Raspberry-Pi-ARMv6-Server erstellen, Admin-Uploads und Tests.
- Bis zur vollständigen Integration bleibt das Verhalten des bestehenden Clients
  unverändert.
- Der bisherige Windows-Server und seine Bandbreitenoptionen werden nicht geändert.

## Geplante Ablage auf NAS4Free

`/mnt/lanlauncher-nas/LANLauncher/Games` und
`/mnt/lanlauncher-nas/LANLauncher/Mods` auf dem Pi;
Windows greift auf dieselben Dateien über den UNC-Pfad zu.
