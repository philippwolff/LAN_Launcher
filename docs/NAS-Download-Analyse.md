# NAS-Download: Analyse und Korrektur

Die nachfolgende Untersuchung des verbleibenden Vorbereitungs-Timeouts und der aktuelle Teststand stehen in [NAS-Vorbereitung-Timeout.md](NAS-Vorbereitung-Timeout.md).

Stand: 17. November 2026, Branch `codex/nas-anmeldung-pruefen`.

## Nachweisbare Client-Ursachen und offene Infrastrukturfrage

Der bisherige Download kehrte nach HTTP- und Anmelde-`await`s auf den WPF-Dispatcher zurück. Dort liefen `File.Exists` auf dem UNC-Paketpfad, der `FileStream`-Konstruktor und `input.Length`. `useAsync: true` macht weder das Öffnen einer Datei noch alle Metadatenabfragen asynchron. Auch die anfänglichen Streamzugriffe konnten synchron blockieren. Währenddessen konnte WPF weder den Fortschritt zeichnen noch den Abbrechen-Klick oder HTTP-Fortsetzungen verarbeiten. Das erklärt eine nachweisbare Blockiermöglichkeit im Client; ein lokaler Dispatcher-Regressionstest deckt die korrigierte Threadaufteilung ab.

Beim reinen Download wurden Übertragungsmodus und Paketpfad zweimal abgefragt: einmal für den Dateinamen, erneut für den eigentlichen Transfer. Beide Auflösungen enthielten auch die NAS-Prüfung und `File.Exists`. Bei der Installation wurde nur einmal aufgelöst, aber dieselbe blockierende SMB-Öffnung verwendet. Beim Anmelden nach fehlgeschlagener Vorprüfung wurde vor dem Verbindungsaufbau zusätzlich dieselbe Prüfung wiederholt.

Die pauschale Fortschrittsmeldung verriet nicht, ob HTTP, NAS-Berechtigungsprüfung, Dateiöffnung oder das erste Lesen wartete. Der Download-HttpClient verwendete ein unendliches Timeout auch für die Metadaten. Der separate gemeinsame Client für Bibliothek, Anwesenheit und bisherige Mod-Abfrage hat dagegen 15 Sekunden Timeout. Seine Fehler dürfen nicht mit einem NAS-Passwortfehler gleichgesetzt werden.

**Die konkrete Ursache des beobachteten SMB-Netzwerkfehlers ist nicht bewiesen.** Die Meldung „Unerwarteter Netzwerkfehler“ entspricht üblicherweise Windows-Code 59; ohne native Fehlernummer oder Trace des ursprünglichen Vorfalls bleibt das eine Zuordnung anhand des Meldungstextes. Die Meldung entstand zuvor im Fehlerbereich der SMB-Öffnung/Kopie und beweist kein Authentifizierungsproblem. Eine schnelle Verzeichnisauflistung und erreichbarer Port 445 prüfen weder das Öffnen noch das vollständige Lesen genau der 0AD-Paketdatei. Der Client zeigt die native Fehlernummer nun an, wenn Windows sie liefert.

## Einordnung des Raspberry Pi

Der lokale Pi-Code verwendet `ThreadingHTTPServer`. `/api/capabilities` und `/api/health` benötigen keinen NAS-Dateizugriff. `/api/games`, spielbezogene Metadaten und `/mod` lesen dagegen den Katalog von der NAS. `/package-location` liest den Katalog und ruft `safe_package` auf; dieser verwendet `Path.resolve` und `is_file` auf dem Pi-NAS-Mount. Diese Operationen können unabhängig vom Windows-Client warten. Beim späteren Schreiben der Antwort kann `BrokenPipeError` auftreten, wenn der Client die Verbindung inzwischen wegen Timeout/Abbruch geschlossen hat. Das ist eine mögliche Folge, kein Beweis für einen Serverabsturz oder eine bestimmte NAS-Fehlerursache.

Der Client setzt bei NAS-Fehlern weder `serverConnected` zurück noch stoppt er den Bibliotheks-Timer. Die SMB-Vorbereitung blockiert den Dispatcher nicht mehr. Ein Regressionstest zeigt parallel eine erfolgreiche simulierte Health-Anfrage und Dispatcher-Verarbeitung bei blockierender NAS-Operation. **Die Erreichbarkeit des realen Pi bei einem blockierenden NAS-Mount kann der Windows-Client nicht garantieren.** Dafür wären Pi-Logs und gegebenenfalls eine gesonderte Serverkorrektur nötig. In dieser Änderung wurde kein Pi- oder Windows-Server-Code verändert und kein Server neu gestartet.

## Änderungen

- `PrepareAsync` liefert ein Paket mit aufgelöstem Namen, Pfad und Freigabe. Beim Speichern wird dasselbe Ergebnis für den Transfer weiterverwendet. Die Vorbereitung enthält ausschließlich HTTP und reine Pfadvalidierung; `File.Exists` auf der NAS entfällt. Die Dateiöffnung liefert den tatsächlichen Zugriffsfehler.
- Die NAS-Prüfung findet einmal unmittelbar vor dem tatsächlichen Zugriff statt. Die zusätzliche Prüfung vor dem Verbindungsaufbau nach einer bereits fehlgeschlagenen Vorprüfung entfällt; nach einer neuen Anmeldung bleibt die Rechtekontrolle erhalten. Die Prüfung beim initialen Server-Login bleibt sinnvoll, weil eine Sitzung bis zum späteren Transfer verloren gehen kann.
- NAS-Prüfung, Windows-Credential-Lesen, Verbindungsaufbau, Dateiöffnung und Längenabfrage laufen auf Worker-Threads. Das Kopieren läuft ebenfalls außerhalb des Dispatchers. Nur der Anmeldedialog bleibt auf dem UI-Thread.
- Die Fortschrittsanzeige benennt HTTP-Mod-Konfiguration, Übertragungsmodus, Paketpfad, SMB-Sitzungsprüfung, Anmeldung, Dateiöffnung und Lesen. Meldungen zu Wiederholungen nennen den letzten Versuch und bei SMB den Windows-Code. Fortschritts- und Statusmeldungen von Worker-Threads werden über den Dispatcher zugestellt; Datenfortschritt wird auf ungefähr zehn Meldungen pro Sekunde begrenzt.
- Die Mod-Abfrage für die Installation findet jetzt innerhalb des Fortschritts-/Abbruchablaufs statt. Ein HTTP-Fehler außer 404 wird nicht mehr stillschweigend als „kein Mod“ behandelt.
- NAS-Prüfungen verschlucken temporäre IO-Fehler nicht mehr als fehlende Zugangsdaten. Authentifizierung, Berechtigung und temporärer Transportfehler werden anhand von Win32-Code/HRESULT unterschieden. Für temporäre Transportfehler gibt es keinen Hinweis zum Trennen einer SMB-Sitzung. Lokale Schreibfehler im Downloadziel werden nicht mehr fälschlich als NAS-Lesefehler bezeichnet.
- Der reine Download verwendet eine eigene eindeutige `.part-<GUID>`-Datei. Die Fehlerbereinigung kann dadurch keine vorher vorhandene gleichnamige `.part`-Datei löschen. Die Zielordneranlage bei Installation liegt im geschützten `try/finally`, damit das Fortschrittsfenster bei einem Fehler geschlossen wird.

## Grenzen der Wiederholung und des Abbruchs

| Operation | Grenze | Wiederholung |
| --- | --- | --- |
| HTTP-Metadaten inklusive Antwortkörper | 10 Sekunden je Versuch | höchstens zweimal bei Transportfehler, Timeout oder HTTP 408/502/503/504 |
| NAS-Vorprüfung, Dateiöffnung inklusive Länge | 15 Sekunden Wartezeit je Versuch | höchstens zweimal bei Windows 59/64/121/1231/1232, nur nach tatsächlich beendetem Fehlversuch |
| Windows-Credentials lesen / Verbindung herstellen | 15 Sekunden Wartezeit | keine automatische Wiederholung des Verbindungsaufbaus |
| Authentifizierung/Berechtigung, fehlende Datei, SMB-Konflikt 1219 | Fehler sofort weitergeben | kein automatischer Netzwerk-Retry; Anmeldedialog behält maximal drei Benutzerversuche |
| Kopieren nach Beginn der Zieldatei | CancellationToken für Read/Write/Flush | kein Neustart, kein Anhängen und keine automatische Fortsetzung |

Ein Timeout während eines noch laufenden synchronen SMB-Aufrufs löst keinen zweiten parallelen Versuch aus. Bei Abbruch/Timeout wartet die Oberfläche nicht mehr auf diesen Aufruf; ein verspätet geöffnetes Handle wird im Hintergrund geschlossen. Ein bereits angestoßener Windows-Verbindungsaufbau kann nach Abbruch noch fertig werden; er wird nicht automatisch rückgängig gemacht. In diesem Fall wird kein Passwort nachträglich gespeichert.

Während der Kopie bleibt der Worker Eigentümer der Zieldatei, bis die IO beendet ist. Es wird kein schreibender Hintergrundtask abgetrennt, während gleichzeitig dieselbe Datei bereinigt wird. Der Abbruch hängt während eines bereits laufenden SMB-Leseaufrufs auch von der Windows-Treiberunterstützung für IO-Abbruch ab. Der UI-Thread bleibt dabei ansprechbar. Der separate NAS-Anmeldedialog bietet seinen eigenen Abbrechen-Button.

## Lokale Verifikation

```powershell
dotnet build src/LanLauncher.Client/LanLauncher.Client.csproj --artifacts-path .artifacts/nas-review
dotnet run --project tests/NasRegression/NasRegression.csproj --artifacts-path .artifacts/nas-tests
git diff --check
```

Der paketfreie Regressionstest umfasst 41 Prüfungen einschließlich der bestehenden zwölf. Er prüft Fehlerklassifizierung, begrenzte Wiederholungen, HTTP-Timeout trotz unendlichem HttpClient-Timeout, HTTP-Abbruch, unveränderten HTTP-Fallback nur bei 404, einfache Metadatenauflösung, unveränderte Paketbytes, Abbruch vor und während der Kopie, fehlende automatische Fortsetzung nach einem Lesefehler, Erkennung unvollständiger Pakete, spätes Handle-Cleanup und die Dispatcher-/Worker-Aufteilung. Der Dispatcher wird durch einen eigenen SynchronizationContext simuliert; WPF-Dialogbedienung und echte SMB-Treiber werden damit nicht getestet.

Client-Build erfolgreich, alle 41 Prüfungen bestanden und `git diff --check` ohne Befund. Bestehende Warnung CS8601 in `LauncherUpdateChecker.cs:63` (durch WPF zweimal ausgegeben). Die fünf geschützten Binärdateien sind laut SHA-256-Vergleich vor/nach der Arbeit unverändert. Alle Build-Ausgaben liegen unter `.artifacts`; keine Commits oder Pull Requests.

## Noch erforderlicher Test mit NAS

Mit einem normalen Benutzer genau dieselbe 0AD-Paketdatei herunterladen und installieren. Die jeweils letzte Statusphase sowie die vollständige Fehlermeldung inklusive Windows-Code festhalten. Während der Vorbereitung und während der Kopie abbrechen; UI-Reaktion und Bereinigung der eigenen temporären Datei prüfen. Parallel Pi-Health und Bibliothek abrufen. Bei erneuten Timeouts die Zeitpunkte mit Pi-Logs und dessen NAS-Mount-Zugriff vergleichen. Reader-/Admin-Sitzungswechsel, echte Credentials und Datei-/Mods-ACLs bleiben Integrationstests aus dem ersten Prüfbericht. Keine vorhandenen SMB-Sitzungen automatisch trennen.
