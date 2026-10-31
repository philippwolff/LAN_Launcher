# LAN Party Launcher

Windows-LAN-Launcher mit zentralem ASP.NET-Core-Server und WPF-Client. **Erste Entwicklungsstufe:** Serververbindung, passwortloser Benutzername, Spielerübersicht und (noch leere) Spieleliste.

## Voraussetzungen
- Windows 10/11 oder Windows Server
- .NET 10 SDK auf dem Entwicklungs-PC
- GitHub Desktop zum Synchronisieren

## Test auf einem Windows-PC

Terminal 1 (im Repository):

\`\`\`powershell
dotnet run --project src/LanLauncher.Server
\`\`\`

Terminal 2:

\`\`\`powershell
dotnet run --project src/LanLauncher.Client
\`\`\`

Im Launcher Server \`127.0.0.1:8080\` und einen Benutzernamen eingeben, dann **Verbinden**. Für Tests auf einem zweiten PC die LAN-IP des Server-PCs verwenden und TCP-Port 8080 in der Windows-Firewall nur für das private LAN freigeben.

API-Test: \`http://127.0.0.1:8080/api/health\`.

## Aktueller Stand
- Server: Health-Endpoint, In-Memory-Spielerliste, Join, Status-Heartbeat, leere Spielebibliothek
- Client: WPF-Oberfläche, IP/Port-Eingabe, Benutzername, Spielerübersicht
- Noch **nicht** implementiert: persistente Einstellungen, Upload/Download/Installation, Admin-Authentifizierung, Spielstart/-erkennung, dauerhafte Speicherung

**Sicherheit:** Die passwortlose Anmeldung ist nicht für Adminrechte geeignet. Adminfunktionen werden erst mit separater Absicherung eingeführt. Der Server ist derzeit nur für ein vertrauenswürdiges LAN gedacht und verwendet standardmäßig HTTP.
