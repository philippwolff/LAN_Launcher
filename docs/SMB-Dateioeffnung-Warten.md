# SMB-Dateiöffnung: Hinweis statt 15-Sekunden-Abbruch

Stand: 17. November 2026, Branch `codex/nas-anmeldung-pruefen`.

## Verhalten

Die Paketdateiöffnung beim Herunterladen und beim Herunterladen/Installieren hat keinen festen 15-Sekunden-Abbruch mehr. Nach 15 Sekunden erscheint im bestehenden Fortschrittsfenster:

> NAS-Datei wird geöffnet. Die Windows-Sicherheitsprüfung kann etwas dauern.

Dazu stehen **Weiter warten** und **Abbrechen** bereit. Der Hinweis ist nicht modal. Die Dateiöffnung läuft bereits weiter, auch wenn der Benutzer noch keine Schaltfläche betätigt. Sobald Windows die Datei erfolgreich geöffnet und die Länge geliefert hat, beginnt der Download automatisch.

**Weiter warten** bestätigt die weitere Wartezeit und blendet die zusätzliche Schaltfläche aus. Es wird weder die Task neu erzeugt noch der FileStream-Konstruktor erneut aufgerufen, die NAS-Anmeldung wiederholt oder ein Zeitlimit zurückgesetzt. Abbrechen bleibt verfügbar. Es gibt keine automatische Wiederholung der Paketöffnung nach einem Fehler; ein echter Fehler wird an den vorhandenen Download-/Installationsfehlerpfad weitergegeben.

**Abbrechen** beendet das Warten des Downloadauftrags über dessen vorhandenen CancellationToken. Die Oberfläche kann den Auftrag schließen, auch wenn der native Windows-Aufruf noch blockiert. Ein bereits laufender FileStream-Konstruktor kann nicht zuverlässig unterbrochen werden. Sein Task und die anschließende Handle-Bereinigung werden daher bis zum tatsächlichen Ende verfolgt. Ein nach dem Abbruch zurückgegebenes Handle wird nicht an den Download übergeben, sondern geschlossen. Bei bereits bemerktem Abbruch wird keine zusätzliche Längenabfrage mehr begonnen.

Die vorhandene Host-Sperre bleibt bis zum Ende von **Windows-Aufruf und Cleanup** bestehen. Es kann vorher keine neue Paketöffnung oder Verbindungs-Vorbereitung desselben Hosts beginnen. Die Sperre serialisiert die Dateiöffnung, nicht die gesamte Datenkopie: Nach erfolgreicher Übergabe kann die bestehende Unterstützung mehrerer Transfers weiterhin genutzt werden.

Anmeldung und andere NAS-Vorprüfungen behalten ihre bisherigen Zeitlimits. Insbesondere kann ein neuer Auftrag beim Warten auf eine noch belegte Host-Sperre weiterhin nach 15 Sekunden einen Belegt-Fehler melden; dabei wird kein weiterer SMB-Zugriff gestartet. Die neue Wartebehandlung gilt gezielt für die Paketdateiöffnung einschließlich Längenabfrage. Sie ersetzt die entsprechende ältere Timeout-Beschreibung in den bisherigen Diagnoseberichten.

## Fortschritt und Protokoll

Der Fortschrittsdialog behält Prozentanzeige und Geschwindigkeit. Zusätzlich zeigt er eine geschätzte Restzeit an; vor der ersten belastbaren Geschwindigkeitsmessung steht „wird berechnet …“, bei unbekannter Gesamtgröße „unbekannt“. Die Geschwindigkeit beginnt erst mit der ersten Datenfortschrittsmeldung zu messen. Die vorherige NAS-/Defender-Wartezeit verfälscht dadurch weder Geschwindigkeit noch Restzeit. Nach Transferbeginn eintreffende alte Wartehinweise dürfen den Datenfortschritt nicht überschreiben.

Die bestehende Diagnose erfasst die Öffnungsdauer, den Hinweis, zusätzliche Wartezeit nach dem Hinweis, Weiter-warten-Bestätigung, Abbruch, spätes Handle-Cleanup und tatsächliche Fehler einschließlich Windows-Code. Die bisherige Vorgangs-ID bleibt über Task und verspätete Bereinigung erhalten. Die bisherige Messung des FileStream-Konstruktors und der Längenabfrage bleibt zusätzlich bestehen.

## Geänderte Dateien in diesem Arbeitsschritt

| Datei | Änderung |
| --- | --- |
| `src/LanLauncher.Client/SmbFileOpening.cs` (neu) | Genau ein Worker, Hinweis-Timer, abbrechbares Warten ohne Öffnungs-Timeout, kontrollierte späte Ressourcenfreigabe und Diagnose |
| `src/LanLauncher.Client/NasPreparation.cs` | Spezieller Öffnungspfad mit denselben Host-/Worker-/Cleanup-Lebensdauern; gemeinsame Erfassung des Worker-Kontexts |
| `src/LanLauncher.Client/GamePackageTransfer.cs` | Paketöffnung verwendet den neuen Wartepfad; UI-Rückmeldung weiterreichen; Abbruchprüfung vor Längenabfrage |
| `src/LanLauncher.Client/MainWindow.xaml.cs` | Hinweis-Callback sowohl für Download als auch für Herunterladen/Installieren anbinden |
| `src/LanLauncher.Client/TransferProgressWindow.xaml` | Weiter-warten-Schaltfläche und Platz für Restzeit im bisherigen Dialog |
| `src/LanLauncher.Client/TransferProgressWindow.xaml.cs` | Warteanzeige, Bestätigung ohne Neustart, Abbruchstatus, Start der Geschwindigkeitsmessung und Restzeitschätzung |
| `tests/NasRegression/OpeningWaitRegression.cs` (neu) | Kontrollierte Tests mit blockierenden Worker-/Cleanup-Simulationen |
| `tests/NasRegression/Program.cs`, `NasRegression.csproj` | Neue Tests und Quelldatei einbinden |
| `tests/ClientUiSmoke/Program.cs` | Hell-/Dunkel-Prüfung des Warteablaufs, tatsächlicher Weiter-warten-Klick, Fortschritt/Restzeit und Vorschauen; Prüfung realisierter statt bereits virtualisierter Logzeilen |
| `docs/SMB-Dateioeffnung-Warten.md` (neu) | Diese Beschreibung und Verifikation |

Öffnungsparameter, NAS-Konten und Credential-Speicherung bleiben bestehen. Microsoft Defender, Defender-Ausnahmen, SMB-Sitzungen, Raspberry-Pi-Server und NAS4Free wurden nicht verändert.

## Builds und Tests

Alle bisherigen Build-Verzeichnisse bleiben erhalten. Neue Ausgaben dieses Arbeitsschritts liegen ausschließlich unter `.artifacts/smb-open-wait-20261117-1`:

```powershell
dotnet build src/LanLauncher.Client/LanLauncher.Client.csproj --artifacts-path .artifacts/smb-open-wait-20261117-1/client -m:1
dotnet run --project tests/NasRegression/NasRegression.csproj --artifacts-path .artifacts/smb-open-wait-20261117-1/tests
dotnet build tests/ClientUiSmoke/ClientUiSmoke.csproj --artifacts-path .artifacts/smb-open-wait-20261117-1/ui-build -m:1
& '.artifacts/smb-open-wait-20261117-1/ui-build/bin/ClientUiSmoke/debug/LanLauncher.UiSmoke.exe' '.artifacts/smb-open-wait-20261117-1/ui-preview'
```

Client-Build erfolgreich, bestehende Warnung CS8601 in `LauncherUpdateChecker.cs:63` unverändert (durch WPF zweimal ausgegeben). Alle **105 Regressionstests** bestanden: 90 bestehende und 15 neue Prüfungen. Diese decken insbesondere Hinweis ohne Abbruch/Neustart, Weiterwarten über die Hinweisfrist, Blockieren paralleler Öffnungen, anschließendes Kopieren, Abbruch vor/nach Hinweis, spätes Handle-Cleanup, Fehler ohne versteckten zweiten Versuch, schnelle Öffnung ohne unnötigen Hinweis und Abbruch vor Worker-Start ab. Ein gesonderter Test blockiert das Cleanup selbst und bestätigt, dass die Host-Sperre auch dann bestehen bleibt.

Der isolierte WPF-Test besteht für vier Szenarien: Einstellungen in Hell/Dunkel sowie SMB-Warten in Hell/Dunkel. Der Weiter-warten-Klick lässt den gezählten Öffnungsvorgang unverändert bei genau einem Aufruf; nach Freigabe des simulierten nativen Aufrufs verschwindet der Hinweis automatisch. Prozentanzeige, Geschwindigkeit ohne Vorbereitungszeit, Restzeit und Abbruchzustand werden geprüft. Die Warte-/Fortschrittsvorschauen unter `ui-preview` wurden visuell geprüft. Es erfolgen dabei keine NAS-Zugriffe und keine Ausführung von Spielepaketen.

Für die automatisierten Verzögerungstests wird die Hinweisfrist auf wenige Millisekunden gesetzt; im Client bleiben es 15 Sekunden. Ein echter Defender-Lauf mit einer noch nicht geprüften NAS-EXE bleibt ein Integrationstest. Es wurden keine produktiven Caches oder Sicherheitsprüfungen zum Erzwingen eines solchen Zustands verändert. Bei einem Windows-Aufruf, der dauerhaft nicht zurückkehrt, bleibt die Host-Sperre nach Abbruch absichtlich weiter belegt; es wird keine erfolgreiche native Unterbrechung vorgetäuscht.

Der neue Client ist `.artifacts/smb-open-wait-20261117-1/client/bin/LanLauncher.Client/debug/LanLauncher.Client.exe`. Die fünf geschützten veröffentlichten EXE-/DLL-/PDB-Dateien sind laut SHA-256-Vergleich vor/nach der Arbeit unverändert. `git diff --check` ohne Befund. Keine Commits und keine Pull Requests erstellt.
