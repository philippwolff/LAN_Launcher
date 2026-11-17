# Ursachenanalyse nach dem NAS4Free-Neustart

Stand: 17. November 2026. Branch: `codex/nas-anmeldung-pruefen`.

## Ergebnis vor einer funktionalen Korrektur

Eine langsame Öffnung der konkreten 0-A.D.-EXE ist mit dem eigenständigen C#-Test **ohne Launcher-Vorbereitung, HTTP, Credential Store, WPF oder Datenkopie reproduziert**: der synchrone FileStream-Konstruktor kehrte erst nach 22,1052 Sekunden zurück. Die unmittelbar folgende asynchrone Öffnung derselben Datei benötigte 1,7 ms. Das ist ein Nachweis einer Öffnungsverzögerung außerhalb des Launchers, aber kein Beweis, dass der ursprüngliche Launcher-Vorfall denselben internen Windows-/NAS-Mechanismus hatte.

Eine pauschale Umstellung auf synchrone Dateiöffnung ist dadurch nicht begründet. Auch für einen anderen Timeout, das Entfernen der Rechteprüfung oder einen Eingriff in SMB-Sitzungen gibt es keine ausreichende Grundlage. In diesem Arbeitsschritt wird deshalb **nur die Ursachenanalyse dokumentiert, keine weitere funktionale Codekorrektur implementiert**.

## Ausgangsdaten des Benutzers

Nach NAS4Free-Neustart war `\\192.168.68.250\Daten\Games\Geschwindigkeitstest.zip` in PowerShell erfolgreich: Öffnung 0,04 s; Lesen von 64 MiB in 1,49 s, entsprechend 42,97 MiB/s. Der Launcher öffnet dagegen `\\192.168.68.250\Daten\Games\b860cbd6e109420d998927e3a60693f7.exe`. Der erfolgreiche ZIP-Test ist somit kein Test derselben Datei wie der hängende 0-A.D.-Download.

Vor dem Neustart benötigten die vom Benutzer gemeldeten Probe-Läufe 83.268,5 ms (`file-open`, synchron) und 138.450,6 ms (`launcher`, asynchron). Beide meldeten identische Caller-/Worker-Kontexte: SID `S-1-5-21-705091475-1571223880-3861010185-1001`, LogonId `00000000:00058FDD`, Session 1, Elevated False, Impersonation None. Diese Werte sind historische Probe-Daten; sie werden nicht als aktueller Launcher-Kontext ausgegeben.

Die vollständigen aktuellen Launcher-Felder `Startkontext` und `Worker-Kontext` liegen nach ausdrücklicher Rückmeldung des Benutzers weiterhin nicht als Text vor. Ein Video war hier nicht verfügbar; die Beschreibung des Benutzers wird nicht als eigener Video- oder Stack-Befund bezeichnet. Der Launcher-Benutzername `test` und die NAS-Rolle `launcher_reader` beweisen für sich weder Windows-SID noch tatsächliche SMB-Sitzungsidentität.

## Kontrollierter Öffnungsvergleich auf der NAS

Die bestehende `NasOpenProbe` wurde in separaten Prozessen nacheinander ausgeführt. Alle erfolgreichen Läufe fanden außerhalb der Codex-Sandbox statt. Identisch waren Mode Open, Access Read, Share Read und 131072 Bytes Puffer. Einzige Parameterdifferenz zwischen `sync-128k` und `launcher` war FileOptions.None gegenüber FileOptions.Asynchronous. Die Probe öffnete und schloss nur: kein Read, keine Length-Abfrage, keine Kopie, keine Ausführung der NAS-Datei, kein WNetAddConnection und kein Passwortzugriff.

| Datei | Profil | Start (Europe/Berlin, UTC+1) | Öffnung | Schließen |
| --- | --- | --- | --- | --- |
| Geschwindigkeitstest.zip | sync-128k | 17.11.2026 14:31:12 | 2,1 ms | 0,1 ms |
| Geschwindigkeitstest.zip | launcher | 17.11.2026 14:38:12 | 1,7 ms | 0,1 ms |
| b860cbd6e109420d998927e3a60693f7.exe | sync-128k | 17.11.2026 14:38:12 | **22.105,2 ms** | 3,7 ms |
| dieselbe EXE | launcher | 17.11.2026 14:38:34 | 1,7 ms | 3,5 ms |

Die drei letzten Aufrufe liefen unmittelbar nacheinander in derselben Shell. Die erste ZIP-Messung lag wegen getrennter Tool-Freigaben etwa sieben Minuten davor. Kein Auftrag lief parallel zu einem noch aktiven Probe-Auftrag. Der 22-Sekunden-Aufruf durfte im eigenständigen Test regulär zu Ende gehen; das 15-Sekunden-Zeitlimit des Launchers wurde nicht geändert.

Alle vier Läufe meldeten .NET 10.0.14, X64 sowie dieselbe SID/LogonId/Session/Elevation/Impersonation wie die oben angegebenen Probe-Daten. Caller und Worker waren jeweils identisch. Die Prozess-IDs waren 34048 (ZIP synchron), 38888 (ZIP asynchron), 18340 (EXE synchron), 9940 (EXE asynchron).

Wichtig: Dieser Test hat **keinen definierten kalten Cachezustand** hergestellt. Es wurden weder NAS-Dienste neu gestartet noch Windows-/NAS-Caches geleert. Die Dateireihenfolge und der Zeitpunkt können daher relevant sein. Aus „erst synchron langsam, danach asynchron schnell“ folgt keine kausale Überlegenheit von Async. Ebenso folgt aus diesen zwei unterschiedlichen Dateien nicht, dass generell jede EXE betroffen ist.

Ein vorheriger Sandbox-Lauf des ZIP-Profils scheiterte nach 4,4 ms mit HRESULT `0x80070041` (Netzwerkzugriff verweigert), obwohl dieselben vier Windows-Kontextfelder gemeldet wurden. Dieser Lauf ist **aus der NAS-Leistungsbewertung ausgeschlossen**. Die gemeldeten SID-/LUID-Felder bilden nicht sämtliche zusätzlichen Prozess-/Sandbox-Einschränkungen ab. Daraus wird insbesondere nicht geschlossen, dass der Launcher in der Codex-Sandbox läuft.

## Zuordnung zu den sechs Fragestellungen

### Windows-Sicherheitskontext

Die Probe-Kontexte sind vergleichbar und belegt; der aktuelle Launcher-Kontext bleibt offen. Der Client kann durch die vorhandene Einstellung `RunAsAdministrator` über UAC neu starten. Im NAS-Transferpfad wurde keine zusätzliche Impersonation gefunden. Die nächste Launcher-Timeout-Meldung muss mit den Probe-Feldern verglichen werden; ein bloßer gleicher Anzeigename reicht nicht.

### SMB-Sitzung und gespeicherte Anmeldung

`FileStream` erhält im Launcher keinen Benutzernamen und kein Passwort. Nach erfolgreicher Verzeichnisprüfung kehrt `EnsureNasConnectedCoreAsync` zurück, bevor der rollenbezogene Credential Store gelesen oder `WNetAddConnection2` erreicht wird. Die vorhandene Windows-Sitzung wird in diesem Fall weiterverwendet. Erst bei fehlendem Zugriff werden gespeicherte NAS-Credentials oder der Anmeldedialog eingesetzt. Der spätere Datei-Konstruktor liest den Credential Store nicht erneut und erhält auch nicht den Server-Admin-Schlüssel.

Welche SMB-Identität Windows für den aktuellen tatsächlichen Launcher-Aufruf verwendet hat, ist nicht unabhängig nachgewiesen. Die Probe nutzte den bestehenden Windows-Netzwerkkontext, ohne ihn neu anzumelden. Aus einem erfolgreichen Zugriff wird hier nicht behauptet, die SMB-Sitzung sei dadurch als `launcher_reader` identifiziert worden. Auch ein fehlgeschlagener Zugriff auf eine bestimmte Datei beweist bei lesbarem Ordner keine falschen Credentials.

### Mehrfachöffnung und alter Worker

Pro tatsächlichem Download gibt es eine Quell-Dateiöffnung in `OpenNasAsync`. Metadatenauflösung und Verzeichnisprüfung öffnen nicht zusätzlich dieselbe Paketdatei. Eine zweite Öffnung ist ausschließlich nach einem tatsächlich beendeten, als temporär klassifizierten Fehler möglich; ein noch laufender, nur im UI abgelaufener Versuch wird nicht automatisch wiederholt.

`NasPreparation` hält die Host-Sperre nach Timeout oder Abbruch bis zum Ende der echten Worker und gegebenenfalls des verspäteten Handle-Cleanup. Ein folgender Fehler „Vorbereitung noch belegt; kein weiterer SMB-Aufruf gestartet“ bezeichnet deshalb den vorherigen Auftrag, keine neue parallele Öffnung. Die bestehenden Regressionstests prüfen genau diesen Fall. Eine nachträgliche erfolgreiche Anmeldung oder ein NAS-Neustart ist kein Beweis, dass ein bereits laufender Windows-Aufruf beendet wurde. Für den ursprünglichen aktuellen Launcher-Worker fehlt ein eigener Live-Stack; es war hier keine Launcher-Instanz sichtbar.

### Öffnungsparameter und synchron/asynchron

Launcher und `sync-128k` unterscheiden sich nur im Asynchronous-Flag. Der alte PowerShell-/`file-open`-Test unterscheidet sich zusätzlich beim Standardpuffer und gegebenenfalls in der Laufzeit (Windows PowerShell 5.1/.NET Framework gegenüber .NET 10). Der neue 128-KiB-Vergleich vermeidet diese beiden Unterschiede innerhalb des C#-Tests. Er bestätigt, dass auch ein synchroner Konstruktor länger als 15 Sekunden dauern kann.

### Vorbereitung oder Transfer

Die beobachtete Phase liegt bei der Handle-Erzeugung. Längenabfrage und erstes ReadAsync erfolgen erst danach. Beim 22-Sekunden-Probe-Lauf gab es weder die Launcher-NAS-Vorprüfung noch eine Datenübertragung. Damit sind diese Komponenten nicht erforderlich, um eine lange Dateiöffnung zu reproduzieren. HTTP-Metadaten und die eigentliche Kopierschleife sind für diesen reproduzierten Aufruf nicht die blockierende Operation.

### Was als technische Ursache noch offen ist

Der zugrunde liegende Windows-/SMB-/Filtertreiberzustand ist nicht bestimmt. Datei-/zeitabhängige NAS-/SMB-Latenz, Lease-/Handle-Zustände, Cacheeffekte und Sicherheitsfilter bleiben Hypothesen. Der vorige Defender-Status und Ereignisabruf liefert keinen kausalen Nachweis; Sicherheitsfunktionen wurden nicht verändert. Ohne korrelierten Trace wird keine dieser Hypothesen als Ursache festgeschrieben.

## Nächster konkreter Diagnoseschritt

Den frischen Client aus `.artifacts/nas-ursachenanalyse/bin/LanLauncher.Client/debug/LanLauncher.Client.exe` verwenden und beim nächsten langsamen Öffnen **dieser EXE** die vollständige Timeout-Meldung sichern: Kennung, exakter Pfad, Start- und Worker-Kontext sowie Meldung „eigener Timeout“ gegenüber „alter Auftrag noch belegt“. Das erlaubt den noch fehlenden aktuellen Launcher-/Probe-Kontextvergleich.

Für die darunterliegende Ursache ist der nächste entscheidende Test eine zeitlich korrelierte lokale Datei-/SMB-/Filtertreiber-Aufnahme während eines erneuten langsamen Open-Aufrufs genau dieser EXE. Beginn/Ende von CreateFile und Windows-/SMB-Resultat bzw. Stack müssen zur selben Prozess-ID und Datei gehören. Nur falls der Trace einen Sicherheitsfilterverdacht stützt, anschließend eine gezielte Defender-Performance-Aufnahme auswerten; keine Ausnahmen und keine Abschaltung. Caches oder produktive Dienste werden zum Erzwingen eines „kalten“ Zustands nicht zurückgesetzt.

## Builds, Tests und Git-Sicherheit

```powershell
dotnet build src/LanLauncher.Client/LanLauncher.Client.csproj --artifacts-path .artifacts/nas-ursachenanalyse
dotnet build tools/NasOpenProbe/NasOpenProbe.csproj --artifacts-path .artifacts/nas-probe-ursachenanalyse
dotnet run --project tests/NasRegression/NasRegression.csproj --artifacts-path .artifacts/nas-tests
git diff --check
```

Client-Build erfolgreich, bestehende CS8601-Warnung in `LauncherUpdateChecker.cs:63` unverändert (WPF gibt sie zweimal aus). Probe-Build erfolgreich ohne Warnungen. Alle 70 vorhandenen Regressionstests bestanden. Ohne weitere funktionale Änderung wurde kein redundanter Test ergänzt.

Alle NAS-Experimente waren ausschließlich lesende Handle-Öffnungen mit anschließendem Schließen. Keine Datei ausgeführt, geschrieben oder vollständig gelesen; keine explizite SMB-Anmeldung, keine Sitzung getrennt, keine Passwörter abgefragt oder protokolliert, keine Sicherheitsfunktion deaktiviert. Die fünf geschützten Binärdateien sind laut SHA-256-Vergleich vor/nach der Arbeit unverändert. `git diff --check` ohne Befund. Keine Commits oder Pull Requests.
