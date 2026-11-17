# SMB-Dateiöffnung: Parameter und Benutzerkontext

Der kontrollierte NAS-Vergleich nach dem NAS4Free-Neustart und die aktualisierte Einordnung stehen in [NAS-Ursachenanalyse-Nach-Neustart.md](NAS-Ursachenanalyse-Nach-Neustart.md).

Stand: 17. November 2026, Branch `codex/nas-anmeldung-pruefen`.

## Was der neue Befund beweist

Die Diagnose `SMB-Paketdatei: FileStream öffnen für <Pfad>` wird unmittelbar vor dem Konstruktor gesetzt. Die Längenabfrage folgt erst nach dessen Rückkehr. `ReadAsync` folgt nochmals später beim Kopieren. Bleibt genau diese gestartete Phase aktiv, blockiert die **Konstruktor-/Handle-Erzeugung**, nicht der erste vom Launcher aufgerufene Lesevorgang. Welche internen Windows-/Filtertreiberoperationen dabei warten, lässt sich ohne Stack/Trace nicht feststellen. Insbesondere wird nicht behauptet, dass Windows oder ein Sicherheitsfilter intern keinerlei Dateiinhalte prüft.

Der erfolgreiche PowerShell-Test nach erneuter SMB-Anmeldung beweist Zugriff zu diesem Zeitpunkt in dessen Windows-Kontext. Er beweist weder, dass ein vorher gestarteter Windows-Aufruf dadurch beendet wurde, noch dass der Launcher denselben Anmeldekontext besitzt.

## Parametervergleich

| Parameter | Launcher | erfolgreicher PowerShell-Aufruf |
| --- | --- | --- |
| Pfad | identischer vom Benutzer genannter UNC-Pfad | derselbe UNC-Pfad |
| FileMode | Open | Open |
| FileAccess | Read | Read |
| FileShare | Read | Read |
| FileOptions | Asynchronous (`useAsync: true`) | synchron, ohne Asynchronous |
| FileStream-Puffer | 131072 Bytes | Standardpuffer (im .NET-10-Vergleich 4096 Bytes) |

Der [offizielle .NET-10-Quelltext von File.Open](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/File.cs) delegiert an den entsprechenden FileStream-Standardkonstruktor. Der lokale Lauf bestätigte `IsAsync=False` für `File.Open` sowohl in Windows PowerShell als auch im .NET-10-Test; der Launcher-Modus meldet `IsAsync=True`. Hier ist Windows PowerShell 5.1 mit CLR 4.0.30319 sichtbar, der C#-Test läuft mit .NET 10.0.14. Derselbe API-Aufruf allein stellt daher noch keinen identischen Laufzeitvergleich her.

**Die unterschiedlichen Öffnungsoptionen sind ein gezielt testbarer Unterschied, kein nachgewiesener Fehler.** Die Launcher-Optionen wurden nicht auf Verdacht geändert. Auch FileShare wurde nicht erweitert. Ein lokaler Test mit exklusiv gesperrter Datei liefert den erwarteten Windows-Sharing-Fehler 32; das schließt serverseitige SMB-Lease-/Sperrwartezeiten im tatsächlichen Vorfall nicht aus.

## Windows-Kontext und frühere Worker

`App.xaml.cs` kann den gesamten Launcher bei aktivierter Einstellung `RunAsAdministrator` über `runas` neu starten. `SettingsWindow.xaml.cs` bietet ebenfalls einen erhöhten Neustart an. Der normale Benutzerstatus innerhalb des Launchers (NAS-Rolle `launcher_reader`) ist davon unabhängig. Es wurde keine sonstige Impersonation des NAS-Zugriffs im Client gefunden.

Die neue Diagnose erfasst für Start und tatsächlich ausführenden Worker jeweils Prozess-ID, Windows-Session, Benutzer-SID, Token-Authentication-LUID (`LogonId`), Elevation und Impersonation. Dafür erfolgen ausschließlich lokale Tokenabfragen; es wird kein Kontoname über Domäne oder Netzwerk aufgelöst. Gleicher Benutzername oder gleiche SID allein reicht zum Vergleich des Anmeldekontexts nicht: insbesondere die LogonId und Elevation vergleichen. Die [Microsoft-Dokumentation zu UAC und Anmeldesitzungen](https://learn.microsoft.com/en-us/troubleshoot/windows-client/networking/mapped-drives-not-available-from-elevated-command) erläutert die Trennung bei Netzlaufwerkszuordnungen. Diese Quelle wird nicht als Beweis verwendet, dass der konkrete UNC-Dateizugriff durch UAC fehlschlägt.

In den hier sichtbaren Prozesslisten war keine laufende Launcher-Instanz vorhanden. Daher konnte der Windows-Kontext des ursprünglich blockierenden Prozesses nicht direkt mit dem erfolgreichen PowerShell-Test verglichen werden. Die lokale Launcher-Konfigurationsdatei war ebenfalls nicht vorhanden; ein aktivierter erhöhter Start beim tatsächlichen Vorfall ist nicht bewiesen. Die neuen Kontextfelder erscheinen in der abschließenden Timeout-Ausnahme und in Trace-Diagnosen. Der eigenständige Test gibt dieselben Felder für Caller und Worker aus.

Die Host-Sperre bleibt erhalten, solange ein alter Windows-Worker oder sein Handle-Cleanup läuft. Die Fehlermeldung nennt jetzt ausdrücklich `Aufrufer beendet=True` und die Anzahl ausstehender Worker-/Cleanup-Aufgaben sowie beide Windows-Kontexte. Die Anzahl ist keine Zahl unabhängiger SMB-Verbindungen: beispielsweise kann eine Cleanup-Aufgabe auf denselben alten Worker warten. Diese Information trennt eine neue Dateiöffnung von einem Folgeversuch, der lediglich auf einen früheren Auftrag wartet. Ein Timeout oder eine nachträgliche Anmeldung wird nicht als erfolgreiche Unterbrechung des alten Workers behandelt.

## Defender und überprüfbare Grenzen

Defender Antivirus und Echtzeitschutz waren bei der rein lesenden Abfrage aktiv (`AMRunningMode=Normal`). In einer begrenzten Abfrage der letzten zwei Stunden, höchstens 30 Defender-Operational-Ereignisse, wurde kein Treffer für NAS-IP oder Paketnamen gefunden. Das schließt eine Scankosten-/Filtertreiberursache nicht aus. `Get-SmbConnection` war aus diesem Ausführungskontext selbst außerhalb der Sandbox mit Windows-Fehler 5 verweigert; die vom Benutzer gemeldeten Sitzungen konnten hier nicht unabhängig inspiziert werden.

Es wurden keine Ausschlüsse, Registry-Einstellungen, Sicherheitsrichtlinien oder Dienste geändert und keine Scans angestoßen. Falls der Vergleich später einen prozessabhängigen Unterschied belegt, wäre eine zeitlich passende Aufnahme mit dem [Microsoft-Defender-Performance-Analyzer](https://learn.microsoft.com/en-us/defender-endpoint/tune-performance-defender-antivirus) ein nachgelagerter Diagnoseweg. Ein normales Ereignisprotokoll ohne Treffer ersetzt diese Messung nicht.

## Änderungen

- `NasPackageFile` hält die bisherigen Öffnungsparameter an einer Stelle fest. Launcher und Probe binden dieselbe Quelldatei ein, damit der Test tatsächlich den verwendeten Konstruktor abbildet.
- `NasWindowsContext` erfasst den effektiven lokalen Windows-Kontext. `NasPreparation` ergänzt ihn in Trace und Timeout-/Belegt-Meldungen sowie den Zustand eines nach Abbruch weiterlaufenden Workers.
- Das Kopieren meldet getrennt das Öffnen des temporären Downloadziels, den Start des ersten `ReadAsync`, dessen Abschlussdauer/Bytezahl und einen Fehler beim ersten bzw. weiteren Lesen. Dateiöffnung und Längenabfrage bleiben eigenständige Vorbereitungsphasen.
- Keine Änderung an Anmeldekonten, Credential-Speicherung, Rechteprüfung, Timeout, Wiederholungsgrenzen oder SMB-Sitzungen. Keine neue funktionale Umgehung für einen noch unbewiesenen Treiber-/Async-/Kontextfehler.

## Eigenständiger Test

Projekt: `tools/NasOpenProbe/NasOpenProbe.csproj`. Keine WPF-, HTTP-, Credential- oder Anmeldelogik. Der Test benutzt einen Worker wie der Launcher und misst Konstruktor und Dispose getrennt. Er führt **kein Read, keine Length-Abfrage, kein Kopieren und keine Ausführung der geöffneten Datei** durch. `IsAsync` wird lediglich am Stream abgefragt.

```powershell
dotnet build tools/NasOpenProbe/NasOpenProbe.csproj --artifacts-path .artifacts/nas-probe
```

Profile:

| Profil | Öffnung |
| --- | --- |
| launcher (Standard) | exakt `NasPackageFile.OpenRead`: async, 128 KiB |
| file-open | `File.Open(path, Open, Read, Read)` wie der PowerShell-API-Aufruf, aber .NET 10 |
| sync-128k | nur Asynchronous gegenüber launcher entfernt |
| async-4k | nur Puffer gegenüber launcher auf 4 KiB geändert |

Der Test hat keinen künstlichen WaitAsync-Timeout: Solange `OPEN_OK` fehlt, ist der Konstruktor nicht zurückgekehrt. Ein Timeout, der nur das Warten beendet, würde diese Beobachtung verschleiern. Ein Testprozess pro Variante macht einen hängen gebliebenen Aufruf sichtbar. Keine weiteren Varianten parallel starten, solange eine Variante noch hängt. Ein regulärer Prozessabbruch ist keine Änderung von SMB-Verbindungen durch die Probe.

## Nächster konkreter Test mit der NAS

Aus derselben PowerShell und demselben Windows-Anmeldekontext, in dem das direkte `File.Open` nach der Anmeldung erfolgreich war, nacheinander ausführen:

```powershell
$probe = 'C:\Projekte\LAN_Launcher\LAN_Launcher\.artifacts\nas-probe\bin\NasOpenProbe\debug\NasOpenProbe.exe'
$file = '\\192.168.68.250\Daten\Games\b860cbd6e109420d998927e3a60693f7.exe'
& $probe $file --profile file-open
& $probe $file --profile launcher
```

`Caller context`, `Worker context`, `OPEN_START`, `OPEN_OK`/`OPEN_ERROR` und `CLOSE_OK` sichern. Für einen erneuten Launcher-Test den frischen Client unter `.artifacts/nas-open-review/bin/LanLauncher.Client/debug/LanLauncher.Client.exe` verwenden und dessen Fehler-Kontext mit den Probe-Feldern vergleichen. Die Anwendung kann aufgrund ihrer gespeicherten UAC-Einstellung erhöht neu starten; der Start aus derselben Shell allein garantiert daher noch keinen identischen Launcher-Kontext.

- Nur launcher hängt bei identischem Kontext: anschließend einzeln `sync-128k` und `async-4k` vergleichen. Das isoliert Async-Option und Puffergröße. Erst ein reproduzierbarer Unterschied begründet eine Änderung an den Öffnungsparametern.
- Beide C#-Profile funktionieren: die erste Dateiöffnung in diesem Prozess ist grundsätzlich möglich. Den ursprünglichen Worker, die zeitliche Reihenfolge der SMB-Anmeldung, App-Kontext und gegebenenfalls prozessspezifische Filter untersuchen.
- C# file-open hängt, Windows-PowerShell File.Open funktioniert: zuerst Token-/Laufzeitunterschied prüfen, nicht FileShare aufweichen oder NAS-Passwörter austauschen.
- Folgeversuch meldet nur belegte Vorbereitung: Das ist keine zweite hängende Dateiöffnung; der angegebene alte Auftrag ist noch aktiv. Die Sperre nicht erzwingen oder SMB-Sitzungen automatisch trennen.

## Lokale Verifikation

70 Regressionstests bestanden. Neue Prüfungen: Windows-SID/LogonId/Elevation-Diagnose, Kontext auf Worker, unveränderte read-only/asynchrone Öffnung, synchroner File.Open-Vergleich, unveränderte Datei, Sharing-Verletzung 32, getrennte erste ReadAsync-Phasen und native Fehlernummer beim ersten Lesen. Der eigenständige Test wurde mit allen vier Profilen ausschließlich gegen die lokale `README.md` geprüft: erfolgreiches Öffnen/Schließen, Position 0 im PowerShell-Vergleich, kein Lesen/Kopieren/Ausführen durch die Probe. Daraus wird kein NAS-Erfolg abgeleitet.

Probe-Build erfolgreich ohne Warnungen. Client-Build im neuen Ausgabeverzeichnis erfolgreich; vorhandene Warnung CS8601 in `LauncherUpdateChecker.cs:63` unverändert. Der vorherige Buildversuch in `.artifacts/nas-review` scheiterte beim Ersetzen der dortigen EXE mit MSB3027/MSB3021 (Datei in Benutzung). Der Verursacher wurde nicht festgestellt und kein Prozess beendet. Diese lokale Ausgabedateisperre ist kein Nachweis für die Ursache der NAS-Dateisperre. Der neue Client liegt vollständig separat in `.artifacts/nas-open-review`.

```powershell
dotnet build src/LanLauncher.Client/LanLauncher.Client.csproj --artifacts-path .artifacts/nas-open-review
dotnet run --project tests/NasRegression/NasRegression.csproj --artifacts-path .artifacts/nas-tests
git diff --check
```

Keine produktive Datei durch den Test geöffnet, kein produktives System verändert, keine Sicherheitssoftware deaktiviert, keine SMB-Sitzung getrennt. Die fünf veröffentlichten Binärdateien sind laut SHA-256-Vergleich vor/nach der Arbeit unverändert. `git diff --check` ohne Befund. Keine Commits oder Pull Requests.
