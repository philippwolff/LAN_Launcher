# NAS-Vorbereitung: verbleibender Timeout

Die anschließende Eingrenzung auf den FileStream-Konstruktor, der eigenständige Öffnungstest und Windows-Kontextdiagnosen stehen in [NAS-Dateioeffnung-Analyse.md](NAS-Dateioeffnung-Analyse.md).

Stand: 17. November 2026. Arbeitsbranch: `codex/nas-anmeldung-pruefen`.

## Gefundene Ursache und belastbare Eingrenzung

Die bisherige Meldung „vorhandene SMB-Sitzung und Berechtigungen prüfen“ gehörte zu genau einem Worker-Aufruf: `HasRequiredAccess(share, admin)`. Es fand dort keine separate SMB-Sitzungsabfrage statt. Für `admin == false` enthielt dieser Aufruf nur:

1. `Directory.EnumerateFileSystemEntries(Path.Combine(share, "Games")).GetEnumerator()`;
2. `MoveNext()` für den ersten Eintrag;
3. `Dispose()` des Enumerators.

Erst nach einem beendeten, erfolglosen Zugriffstest wurden gespeicherte Credentials gelesen, der Anmeldedialog geöffnet oder `WNetAddConnection2` aufgerufen. Bei erfolgreichem Zugriff kehrte die Methode sofort zurück. Ein Timeout in dieser Phase belegt daher **keinen Credential- oder Verbindungsaufbau-Timeout**. Für einen normalen Benutzer gab es auch keinen Admin-Schreibtest. Falls der Worker noch nicht gestartet war, konnte die Sammelmeldung ebenfalls unverändert stehen bleiben.

**Ein konkreter Fehler lag in der Lebensdauer der Vorbereitung:** `WaitAsync` beendete nur das Warten nach 15 Sekunden bzw. Abbruch, nicht zuverlässig den synchronen Windows-Aufruf. Es gab keine gemeinsame Koordination zwischen Server-Anmeldung, Installation, Download und Upload. Ein neuer Aufruf konnte daher gleichzeitig mit dem alten, noch laufenden Zugriffstest oder Verbindungsaufbau starten. Das ist im Regressionstest reproduzierbar. Es gab vorher keinen expliziten Lock und keinen synchronen Dispatcher-Aufruf in `HasRequiredAccess`; eine belegte Ursache durch einen solchen Lock wurde nicht gefunden.

**Nicht bewiesen ist, warum der erste reale Windows-Verzeichnisaufruf auf diesem Rechner länger als 15 Sekunden benötigt.** Die außerhalb des Launchers gemessenen 210–625 ms widerlegen eine pauschale Aussage „NAS grundsätzlich unerreichbar“, zeigen aber weder den tatsächlich vom Server gelieferten Launcher-Pfad noch Rolle und den blockierenden Einzelschritt im Launcher. Aus der alten Sammelmeldung lassen sich Enumerator-Erzeugung, `MoveNext`, `Dispose` und fehlender Worker-Start nicht unterscheiden. Es lief bei der lokalen Prüfung kein Launcher-Prozess, an dem ein Stack des ursprünglichen Vorfalls hätte erfasst werden können. Es wurde keine produktive NAS-Abfrage zur Reproduktion durchgeführt.

## Konkrete Korrektur

`NasPreparation` verwaltet eine asynchrone Sperre pro UNC-Host, gemeinsam für Freigaben, Rollen und Aufrufstellen innerhalb des Clientprozesses. Groß-/Kleinschreibung und ein abschließender Punkt im Hostnamen werden normalisiert. Die Sperre umfasst Rechteprüfung, Credential-Verwendung, Dialog und gegebenenfalls Verbindungsaufbau. Auch die anschließende Paketöffnung nutzt dieselbe Host-Koordination. Die eigentliche Datenkopie hält die Vorbereitungssperre nicht fest.

Entscheidend: Jeder Worker und die spätere Handle-Bereinigung erhalten einen Anteil an der Sperrenlebensdauer. Der Besitzer im UI kann nach Timeout oder Abbruch freigeben; die Host-Sperre bleibt trotzdem bestehen, bis sämtliche unterliegenden Worker und gegebenenfalls verspätetes Handle-Cleanup tatsächlich beendet sind. Ein wartender zweiter Aufruf startet vorher weder eine neue Prüfung noch einen neuen Verbindungsversuch.

Das Warten auf die Host-Sperre ist asynchron, abbrechbar und auf 15 Sekunden begrenzt. Bei Ablauf lautet der Fehler ausdrücklich „NAS-Vorbereitung noch belegt; kein weiterer SMB-Aufruf gestartet“, ergänzt um den vorherigen Auftrag und dessen letzten Schritt. Kein erzwungenes Entsperren und kein automatisches Trennen einer SMB-Sitzung.

Die bisherigen Zeitlimits für Windows-Operationen bleiben bei 15 Sekunden. Berechtigungsprüfungen bleiben erhalten: Leser müssen `Games` auflisten können; Administratoren durchlaufen weiterhin die Schreibprobe einschließlich Löschen der eigenen Testdatei. Die redundante `Directory.Exists`-Prüfung vor der Admin-Schreibprobe entfällt; ein fehlender Ordner wird weiterhin als fehlender Zugriff behandelt. Ein erfolgreicher Zugriff verwendet die vorhandene Windows-Sitzung ohne Credential-Lesen oder `WNetAddConnection2` weiter. Neue Verbindungen verwenden weiterhin `launcher_reader` oder `launcher_admin` und die automatische Anmeldung bleibt erhalten. Der Server-Admin-Schlüssel bleibt davon getrennt.

## Neue Diagnosemeldungen

Jeder Auftrag trägt eine Kennung, Aufrufquelle, angeforderte NAS-Rolle und Freigabe. Einzeloperationen melden Start, Worker-ID, Abschlussdauer oder Fehlertyp/Windows-Code. Die Rolle beschreibt die Anforderung; sie behauptet nicht, dass eine wiederverwendete fremde Windows-Sitzung unter genau diesem Konto läuft. Passwörter werden nicht ausgegeben.

| Meldung/Schritt | Bedeutung |
| --- | --- |
| `wartet auf Worker-Start` | Worker hat die erste Diagnose noch nicht erreicht; noch kein Nachweis für eine blockierte SMB-API |
| `auf laufende Vorbereitung warten` | anderer Auftrag hält dieselbe Host-Sperre; es wird keine zweite Windows-Operation gestartet |
| `Enumerator öffnen für <Pfad>` | Erzeugen des Verzeichnis-Enumerators einschließlich möglichem Windows-Zugriff |
| `ersten Eintrag lesen (MoveNext)` | erster Enumerationsschritt |
| `Enumerator schließen` | Aufräumen des Verzeichnis-Handles |
| `Admin-Schreibprüfung: Testdatei ...` | getrennte Schritte Anlegen, Schließen und Löschen; nur für Admin-Anforderung |
| `Windows-Credential-Manager: CredRead` | Lesen des gespeicherten NAS-Passworts, noch kein Verbindungsaufbau |
| `SMB-Verbindungsaufbau: WNetAddConnection2` | tatsächlicher neuer Verbindungsversuch, einschließlich Rückgabecode |
| `SMB-Paketdatei: FileStream öffnen` / `Länge lesen` | Vorbereitung der konkreten Paketdatei nach erfolgreicher Anmeldung |

Die Meldungen gehen an das Fortschrittsfenster und `System.Diagnostics.Trace`. Eine neue persistente Protokolldatei wird nicht angelegt. Insbesondere enthält die abschließende Timeout-Ausnahme den atomar gespeicherten letzten Einzelschritt; sie ist nicht davon abhängig, ob WPF die letzte gepostete Anzeige schon gezeichnet hat. Beim initialen Server-Login ohne Fortschrittsfenster liefert der Fehlerdialog dieselbe Eingrenzung. Aufrufquellen sind `Server-Anmeldung`, `Installation/Download`, `Paket-Download`, `Upload` und `Paket öffnen`.

Alle Windows-Aufrufe laufen weiterhin auf Workern. Statusupdates von Workern verwenden das vorhandene `Dispatcher.BeginInvoke`; es gibt keinen synchronen `Dispatcher.Invoke` und kein synchrones Warten auf eine belegte Host-Sperre. Nach einem Timeout eintreffende Meldungen eines abgeschlossenen Fensters werden von dessen bestehender Statusbehandlung verworfen.

## Tests und Build

```powershell
dotnet build src/LanLauncher.Client/LanLauncher.Client.csproj --artifacts-path .artifacts/nas-review
dotnet run --project tests/NasRegression/NasRegression.csproj --artifacts-path .artifacts/nas-tests
git diff --check
```

59 Regressionstests einschließlich der vorhandenen Downloadtests. Neue Prüfungen decken die echten lokalen Enumerator-Phasen, Wiederverwendung ohne Reconnect, Admin-Schreibprobe, Geheimnisfreiheit der Diagnose, Serialisierung über Rollen/Freigaben, unabhängige Hosts, Fehlerbereinigung der Sperre, exakte Timeout-Phase, abbrechbare Wartende und fortbestehende Sperren nach Timeout/Abbruch bis zum Ende des Windows-Workers und Cleanup ab. Ein eigener SynchronizationContext prüft Diagnosezustellung ohne Worker-/Dispatcher-Lockzyklus. Die Windows-Blockierung wird über kontrolliert blockierende Worker simuliert; kein Zugriff auf die produktive NAS und keine Windows-Credentials im Test.

Build erfolgreich, alle 59 Prüfungen bestanden und `git diff --check` ohne Befund. Vorhandene Warnung CS8601 in `LauncherUpdateChecker.cs:63` unverändert. Ausgaben liegen separat unter `.artifacts`. Die fünf vorhandenen veröffentlichten EXE-/DLL-/PDB-Dateien sind laut SHA-256-Vergleich vor/nach der Arbeit unverändert. Keine Commits oder Pull Requests; keine Serveränderung.

## Verbleibende Grenzen und nächster realer Test

Ein dauerhaft nicht zurückkehrender Windows-Aufruf hält die Host-Sperre absichtlich weiter. Ein neuer Versuch erhält dann eine belegte Vorbereitung gemeldet. Das verhindert das Aufstapeln weiterer Windows-SMB-Aufrufe, kann aber den nativen Treiber nicht reparieren. Ein bereits gestarteter nativer Verbindungs- oder Credential-Schreibaufruf kann trotz Abbruch noch enden; es wird keine erfolgreiche Unterbrechung vorgetäuscht. Die Host-Koordination gilt innerhalb eines Clientprozesses. Unterschiedliche DNS-Aliase/IP-Adressen derselben physischen NAS werden ohne zusätzliche DNS-Abfragen nicht zusammengeführt.

Beim nächsten 0AD-Test den neu gebauten Client aus `.artifacts/nas-review/bin/LanLauncher.Client/debug/LanLauncher.Client.exe` verwenden. Bei Timeout die vollständige neue Fehlermeldung einschließlich Kennung, Rolle, Aufrufquelle und letztem Einzelschritt sichern. Den dort genannten exakten `Games`-Pfad mit der schnellen PowerShell-Prüfung vergleichen und sicherstellen, dass Launcher und Shell im gleichen Windows-Benutzer-/Anmeldekontext laufen. Aus der letzten Phase lässt sich dann unterscheiden, ob Windows bei Enumeration/Handle-Close hängt, der Worker noch nicht lief oder tatsächlich ein Credential-/Verbindungsaufruf erreicht wurde. Das ist weiterhin ein erforderlicher Integrationstest; ein erfolgreicher realer NAS-Download wird aus den lokalen Tests nicht behauptet.
