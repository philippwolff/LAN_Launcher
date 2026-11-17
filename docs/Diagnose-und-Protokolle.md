# Integrierte Client-Diagnose und Protokolle

Stand: 17. November 2026, Branch `codex/nas-anmeldung-pruefen`.

## Bedienung

Unter **Einstellungen → Diagnose & Protokolle** steht das automatisch aktualisierte Client-Protokoll zur Verfügung. Die Liste ist scrollbar und virtualisiert; Zeitstempel einschließlich lokaler Zeitzone, DEBUG/INFO/WARN/ERROR, Komponente, Vorgangs-ID und Meldung/Exception werden angezeigt. Die Kontrollkästchen filtern die Schweregrade. „Neueste Einträge automatisch anzeigen“ kann zum Lesen älterer Zeilen abgeschaltet werden.

„Kopieren“ und „Exportieren“ verwenden die aktuell ausgewählten Schweregrade und die letzten bis zu 2000 geladenen Einträge. Die zusätzliche Anonymisierung ist standardmäßig aktiviert und entfernt lokale/UNC-Dateipfade, Benutzerangaben, den aktuellen Windows-Benutzernamen sowie Benutzer-SIDs. Ohne diese Option bleiben Diagnosepfade erhalten; Geheimnisse werden auch dann nicht exportiert. Vollständige aufbewahrte Rohdateien liegen im über die dritte Schaltfläche erreichbaren Protokollordner.

Das bestehende Windows-98-Design bleibt erhalten. Die neue Ansicht unterstützt Dark Mode einschließlich heller Häkchen in den Filtern. Der Einstellungsdialog wurde für die zusätzliche Ansicht vergrößert; bestehende Einstellungen und gespeicherte Bibliotheksdaten werden beim Speichern übernommen.

## Speicher und Grenzen

Verzeichnis: `%LOCALAPPDATA%\LAN_Launcher\Logs` (bewusst nicht der vorhandene Einstellungsordner `LANLauncher`). Dateiformat: UTF-8 JSON Lines, `client-<UTC-Zeit>-<PID>-<GUID>.jsonl`.

- Standard: 20 MiB insgesamt; unter Einstellungen zwischen 2 und 200 MiB konfigurierbar und über LauncherSettings dauerhaft gespeichert.
- Rotation bei maximal 1 MiB je Datei oder Tageswechsel in UTC. Einträge werden bereits vor der Persistierung begrenzt.
- Mindestens die letzten sieben Tage werden aufbewahrt, **soweit die Größenbegrenzung reicht**. Ältere Dateien werden entfernt; bei Platzmangel werden die ältesten nicht aktiven Dateien zuerst gelöscht. Aktive Dateien anderer Prozesse werden nicht gewaltsam geöffnet oder gelöscht. Können sie nicht gelöscht werden, zeigt die Ansicht einen Speicherfehler, statt die Grenze durch weitere Dateien zu umgehen.
- Die Anzeige hält höchstens 2000 Zeilen im Speicher. Beim Start lädt der Hintergrund-Worker die aufbewahrten Dateien wieder ein; eine unterbrochene letzte JSON-Zeile wird übersprungen.
- Die Warteschlange hat 4096 Plätze. Bei Überlast werden Einträge verworfen und die Anzahl wird als WARN protokolliert. Der aufrufende UI-/Transfercode wartet nicht auf freien Platz.
- Bei Schreibfehlern bleiben die aktuellen Einträge im begrenzten Speicher und der Fehler wird in der Statuszeile angezeigt. Nach einem solchen Fehler ist deren Persistenz nicht garantiert.

Alle Dateioperationen und die Rotation laufen auf einem separaten Hintergrund-Worker. Die Oberfläche fragt einmal pro Sekunde ausschließlich einen begrenzten Speicher-Snapshot ab. Beim Beenden wird die Warteschlange abgeschlossen; ein separater Abschluss-Thread erlaubt maximal zwei Sekunden für das Leeren, ohne den WPF-Dispatcher zu blockieren. Bei hartem Prozessabbruch oder ausgeschöpfter Abschlussfrist können letzte noch nicht gespeicherte Einträge fehlen. Bereits gespeicherte Protokolle bleiben beim nächsten Start verfügbar.

## Ereignisse und Korrelation

Erfasst werden Client-Start/-Ende, erhöhte Neustartanforderung, Server-Anmeldung einschließlich abgelehnter HTTP-Statuscodes, Server-Abfragefehler, NAS-Vorbereitung, Credential-Verwendung als **Schrittbezeichnung ohne Werte**, SMB-Dateiöffnung, Längenabfrage, Beginn/Dauer/Bytezahl des ersten ReadAsync, Datenübertragungs-/Installationsphasen, lokale Speicherung, Abbruch und Fehler. Unbehandelte UI-/Prozess-/Task-Exceptions werden zusätzlich erfasst.

Anmeldung, Upload, Download und Herunterladen/Installieren erhalten eigene IDs. Eine AsyncLocal-basierte Vorgangskorrelation fließt durch HTTP-awaits und Worker-Tasks. NAS-Aufträge behalten zusätzlich ihre bisherige NAS-Kennung. Auch nach dem UI-Abbruch eintreffende Worker-Ereignisse tragen die ursprüngliche ID.

SMB-Diagnosen enthalten Start- und Worker-Kontext mit SID, LogonId, Elevation und Impersonation. Der vorhandene 15-Sekunden-Timeout protokolliert den letzten Schritt und einen möglicherweise weiterlaufenden Worker als WARN. Eine weiterhin belegte Host-Sperre und das spätere tatsächliche Ende/Cleanup werden ebenfalls protokolliert. Dafür werden weder neue SMB-Tests noch parallele Verbindungsversuche gestartet.

Exception-Erfassung umfasst Typ, redigierte Meldung, Stacktrace, HRESULT, gegebenenfalls native Windows-Fehlernummer und bis zu 16 relevante Exceptions mit begrenzter Verschachtelung (auch AggregateException). Meldungen und Details sind größenbegrenzt.

## Geheimnisse und Export

Es werden keine HTTP-Header, Passwortfelder, Request-Bodies, vollständigen Server-Antworten oder Credential-Blobs protokolliert. Nas-Passwörter und Admin-Keys werden bei ihrer Verwendung zusätzlich in einem nur im aktuellen Ausführungskontext gültigen Geheimnisfilter geschützt. Die Redaktion erfolgt vor dem Einreihen in die Warteschlange, also vor jeder Speicherung. Sie entfernt bekannte Werte sowie Passwort-/Token-/Admin-Key-Felder, Bearer/Basic-Authentifizierung, URL-Benutzerinformationen und URL-Querystrings. Diese Filter gelten auch für Exceptions und nicht anonymisierte Exporte.

Rohprotokolle dürfen Windows-SIDs und Dateipfade enthalten, da sie für die ausdrücklich gewünschte SMB-Diagnose gebraucht werden. Vor einer Weitergabe sollte die standardmäßig aktivierte Export-Anonymisierung verwendet werden. Die lokale Speicherung verwendet die normalen Zugriffsrechte des Windows-Benutzerprofils; es werden keine Sicherheitsrichtlinien oder ACLs verändert.

## Module

`Logging/LogEntry.cs` definiert Eintrag, Schweregrad, Snapshot, Formatierung und `ILogSource`. `ClientLogStore` ist für Hintergrund-IO, Rotation, Aufbewahrung und Historie zuständig. `ClientLog` stellt die Client-Fassade und Vorgangs-IDs bereit; `LogPrivacy` schützt Inhalte und anonymisiert Exporte. `DiagnosticsPanel` zeigt jede `ILogSource` an. Damit kann später eine separate Server-Quelle und ein eigener Bereich ergänzt werden, ohne die Dateispeicherung oder die Client-Ereignisquellen umzubauen. Es wird aktuell kein nicht vorhandener Server-Log-Endpunkt abgefragt.

Die NAS-Authentifizierung und die Transferkontrolle bleiben in ihren bestehenden Komponenten. Keine Änderungen an Dateiöffnungsoptionen, Kopier-/Installationsentscheidungen, Timeouts oder Wiederholungsgrenzen. Keine automatischen SMB-Trennungen und keine deaktivierten Sicherheitsfunktionen.

## Verifikation

```powershell
dotnet build src/LanLauncher.Client/LanLauncher.Client.csproj --artifacts-path .artifacts/diagnostics-review -m:1
dotnet run --project tests/NasRegression/NasRegression.csproj --artifacts-path .artifacts/diagnostics-tests
dotnet build tests/ClientUiSmoke/ClientUiSmoke.csproj --artifacts-path .artifacts/diagnostics-ui-build -m:1
& '.artifacts/diagnostics-ui-build/bin/ClientUiSmoke/debug/LanLauncher.UiSmoke.exe' '.artifacts/diagnostics-ui'
```

Client-Build erfolgreich, 90 Regressionstests bestanden (70 bestehende sowie 20 neue Prüfungen zur Protokollierung). Neue Tests prüfen insbesondere Geheimnisse auf dem Datenträger, innere Exceptions/Windows-Codes, Anonymisierung, parallele Vorgangs-IDs, Rotation/Größenlimit, begrenzten Speicher, Wiederherstellung nach Neustart, Sieben-Tage-Aufbewahrung, unvollständige JSON-Zeilen, Schreibfehler und Überlast. Die vorhandenen Tests prüfen weiterhin Transferbytes, Abbruch, Grenzen der Wiederholungen und den Umgang mit blockierenden Workern.

Der isolierte WPF-Test besteht für Hell und Dunkel: echte Fenster und Templates werden außerhalb des sichtbaren Bildschirms erzeugt, Logzeilen automatisch geladen, DEBUG-Filter aus-/eingeschaltet, neue Einträge über den Aktualisierungstimer sichtbar und die Farben sowie nutzbare Listenhöhe geprüft. Er verwendet ausschließlich lokale Testdaten und lädt nur die tatsächlichen App-Styles; Client-Start, Update-Abfragen, Anmeldung und NAS-Zugriffe werden nicht ausgeführt. Gerenderte Vorschauen unter `.artifacts/diagnostics-ui/settings-light.png` und `settings-dark.png` wurden visuell geprüft.

Bestehende CS8601-Warnung in `LauncherUpdateChecker.cs:63` unverändert (durch WPF zweimal ausgegeben). Kopieren in die echte Benutzer-Zwischenablage, Auswahl eines Exportziels im Speicherdialog und Öffnen des Protokollordners über die Shell bleiben manuelle Integrationsprüfungen; Formatierung, Anonymisierung und Speicherung selbst sind automatisiert geprüft. Echte NAS-/Server-Ereignisse sollten beim nächsten normalen Testlauf in der Ansicht kontrolliert werden. Diese Implementierung hat keine zusätzlichen produktiven NAS-/Server-Tests durchgeführt.

Der neue Client liegt unter `.artifacts/diagnostics-review/bin/LanLauncher.Client/debug/LanLauncher.Client.exe`. Die fünf geschützten veröffentlichten EXE-/DLL-/PDB-Dateien sind laut SHA-256-Vergleich vor/nach der Arbeit unverändert. `git diff --check` ohne Befund. Keine Commits oder Pull Requests erstellt.
