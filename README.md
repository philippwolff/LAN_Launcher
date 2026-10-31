# LAN Party Launcher

Windows-LAN-Launcher: WPF-Client, ASP.NET-Core-Server und gemeinsame Verträge (.NET 10).

## Starten

.NET 10 SDK installieren. Im Repository zwei Terminals öffnen:

```powershell
dotnet run --project src/LanLauncher.Server
```

```powershell
dotnet run --project src/LanLauncher.Client
```

Im Client mit `127.0.0.1:8080` und einem Benutzernamen verbinden.

## Admin-Funktionen aktivieren

Adminrechte werden **nicht** über den Benutzernamen vergeben. Der Server braucht einen eigenen geheimen Schlüssel. In der PowerShell **vor dem Serverstart** setzen:

```powershell
$env:LANLAUNCHER_AdminKey = "HIER_EINEN_LANGEN_ZUFAELLIGEN_SCHLUESSEL_EINTRAGEN"
dotnet run --project src/LanLauncher.Server
```

In `cmd.exe`:

```cmd
set LANLAUNCHER_AdminKey=HIER_EINEN_LANGEN_ZUFAELLIGEN_SCHLUESSEL_EINTRAGEN
dotnet run --project src/LanLauncher.Server
```

Den gleichen Schlüssel im Feld **Admin-Schlüssel** des Clients eingeben. Den Schlüssel nicht ins Git-Repository schreiben.

## Spiele hinzufügen

**Direkt im Launcher:** Als Admin verbinden, Spielname und Version eintragen, **ZIP/EXE hochladen** wählen. Das Paket wird auf dem Server gespeichert und erscheint in der Bibliothek.

**Über Serverordner:** Eine `.zip`- oder `.exe`-Datei in den Server-Inbox-Ordner kopieren. Bei Start mit `dotnet run --project src/LanLauncher.Server` ist das standardmäßig `src/LanLauncher.Server/bin/Debug/net10.0/Storage/Inbox`. Im Client **Server-Inbox prüfen**, Datei auswählen, Name und Version eingeben, **Aus Inbox importieren**.

**Spielepakete herunterladen:** Spiel in der Liste auswählen und **Paket herunterladen** anklicken. Das speichert die ZIP/EXE-Datei lokal. Automatisches Entpacken/Installieren und Starten werden in einer späteren Stufe implementiert.

Der Server speichert Metadaten dauerhaft in `Storage/games.json` und Dateien unter `Storage/Packages`. Der Speicherort kann über `StoragePath` konfiguriert werden. Bereits entpackte Ordner werden derzeit noch **nicht** automatisch importiert; dafür ist die spätere Ordner-Paketierung vorgesehen.

## Sicherheit und Einschränkungen

- Nur im vertrauenswürdigen LAN betreiben; derzeit HTTP ohne TLS. Admin-Schlüssel wird über HTTP übertragen und kann im Netz mitgelesen werden.
- Für produktive Nutzung TLS/HTTPS, sichere Admin-Sitzungen und Upload-Validierung ergänzen.
- EXE-Dateien werden nicht automatisch ausgeführt; nur Dateien verteilen, für die entsprechende Rechte bestehen.
- Der Server ist noch keine Windows-Service-Installation; der Client hat noch keine automatische Installation, Spielstart-Überwachung oder Download-Fortsetzung.
- Erste Implementierung: noch nicht auf Windows kompiliert/getestet.
