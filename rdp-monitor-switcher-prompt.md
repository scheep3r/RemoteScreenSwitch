# Projekt: RDP Monitor Switcher – Remote Desktop wie TeamViewer

Du bist ein erfahrener Windows-Desktop-Entwickler mit Expertise in C#, .NET, Windows Remote Desktop Protocol (RDP), Windows APIs und Remote-Desktop-Architekturen.

Entwickle eine funktionsfähige Windows-Anwendung, mit der ich innerhalb einer Remote-Desktop-Verbindung bequem zwischen den einzelnen Monitoren eines entfernten Windows-PCs wechseln kann – ähnlich wie bei TeamViewer.

## 1. Ziel und gewünschtes Verhalten

Ich verbinde mich von meinem lokalen Windows-PC per RDP mit einem entfernten Windows-PC, an den mehrere Monitore angeschlossen sind.

Beispiel:
- Remote-PC hat 3 Monitore.
- Mein lokaler PC hat 1 Monitor.
- Ich öffne die RDP-Verbindung in deiner Anwendung.
- Es wird zunächst der erste Remote-Monitor angezeigt.
- Über eine Monitorleiste kann ich auf Monitor 2 oder Monitor 3 wechseln.
- Die Remote-Sitzung soll möglichst durchgehend bestehen bleiben.
- Ich möchte nicht alle Remote-Monitore gleichzeitig nebeneinander sehen.
- Der ausgewählte Remote-Monitor soll möglichst den gesamten verfügbaren Platz im Anwendungsfenster nutzen.

Das Verhalten soll sich für den Anwender so natürlich wie der Monitorwechsel in TeamViewer anfühlen.

## 2. Zuerst die technische Machbarkeit untersuchen

Bevor du die Anwendung implementierst, analysiere die technischen Möglichkeiten und Grenzen.

Untersuche insbesondere:

- Den Windows-RDP-Client `mstsc.exe`.
- Die Konfigurationsmöglichkeiten von `.rdp`-Dateien.
- Die RDP-Einstellungen `use multimon` und `selectedmonitors`.
- Die Windows-APIs für Monitorinformationen und Display-Konfigurationen.
- Die Möglichkeit, einen RDP-Client in eine eigene Windows-Anwendung einzubetten.
- Die Möglichkeiten von FreeRDP beziehungsweise dessen Windows-Client-Komponenten.
- Die Frage, ob eine bestehende RDP-Sitzung zur Laufzeit auf einen anderen Remote-Monitor umgeschaltet werden kann, ohne die Verbindung zu trennen.
- Die Alternative, mehrere Verbindungen oder unterschiedliche RDP-Konfigurationen zu verwenden.

Wichtig: Behaupte nicht, dass eine Funktion möglich ist, wenn das verwendete RDP-Protokoll oder der Client sie nicht unterstützt.

Unterscheide ausdrücklich zwischen:
1. Wechsel innerhalb derselben bestehenden RDP-Sitzung.
2. Neuverhandlung der Monitor-Konfiguration innerhalb einer Verbindung.
3. Neuaufbau einer RDP-Verbindung mit einer anderen Monitor-Konfiguration.
4. Separaten Verbindungen zu unterschiedlichen Remote-PCs.

Erkläre kurz, welcher Ansatz technisch realistisch ist, und implementiere anschließend die beste praktikable Lösung.

Falls die gewünschte nahtlose Umschaltung mit dem klassischen Microsoft-RDP-Client nicht möglich ist, suche eine alternative Architektur. Bevorzuge eine Lösung, bei der die Remote-Sitzung möglichst nicht unterbrochen wird.

## 3. Technologievorgaben

Bevorzugte Technologien:

- C#.
- Aktuelles, stabiles .NET LTS.
- WPF als Windows-Desktop-Oberfläche.
- MVVM-Architektur, soweit sinnvoll.
- Windows APIs für Monitorerkennung und Fensterverwaltung.
- Ein geeigneter RDP-Client beziehungsweise eine geeignete RDP-Bibliothek.

Wenn FreeRDP oder eine andere native Bibliothek benötigt wird, kapsle sie hinter einer klaren Schnittstelle. Beachte Lizenzbedingungen, Architekturkompatibilität, Bereitstellung nativer Abhängigkeiten und mögliche Einschränkungen.

Verwende keine erfundene API. Prüfe anhand offizieller Dokumentation und des tatsächlichen Quellcodes, welche Funktionen verfügbar sind.

Wenn eine andere Technologie deutlich besser geeignet ist, begründe die Entscheidung vor der Implementierung.

## 4. Benutzeroberfläche

Entwickle eine moderne, übersichtliche Windows-Oberfläche.

### Hauptfenster

Oben eine kompakte Toolbar mit:

- Verbindungsstatus.
- Verbindungsbutton.
- Monitor-Auswahl.
- Vorheriger/Nächster-Monitor.
- Vollbildmodus.
- Skalierung beziehungsweise Originalauflösung.
- Einstellungen.

Darunter der Remote-Bildschirm.

### Monitor-Auswahl

Die Toolbar soll beispielsweise so aussehen:

`[ Verbunden ] [ Monitor 1 ] [ Monitor 2 ] [ Monitor 3 ] [ Vollbild ]`

Der aktive Monitor muss optisch eindeutig erkennbar sein.

Wenn der Remote-PC weniger oder mehr Monitore hat, muss sich die Auswahl dynamisch anpassen.

Zusätzlich:
- Tastenkürzel für den vorherigen und nächsten Monitor.
- Optional nummerierte Schnellwahltasten.
- Monitorbezeichnungen und Auflösungen anzeigen.
- Fehler verständlich darstellen, wenn ein Monitor nicht verfügbar ist.

### Fensterverhalten

- Frei skalierbares Fenster.
- Vollbildmodus.
- Sinnvolle Skalierung des Remote-Bildes.
- Möglichst keine unnötigen schwarzen Ränder.
- Toolbar bei Bedarf ausblendbar.
- Tastatur- und Mauseingaben müssen an die Remote-Sitzung weitergegeben werden.
- Zwischen Monitorwechsel und normaler Bedienung darf es keine unbeabsichtigten Tastatur- oder Mausprobleme geben.

## 5. Verbindung und Konfiguration

Implementiere:

- Eingabe des Remote-Hostnamens oder der IP-Adresse.
- Benutzername und geeignete Authentifizierung.
- Unterstützung für die üblichen RDP-Verbindungsoptionen.
- Konfigurierbare Auflösung und Skalierung.
- Erkennung der verfügbaren Remote-Monitore, soweit die gewählte RDP-Architektur das erlaubt.
- Verbindungsstatus und Fehlerbehandlung.
- Wiederverbindung nach Verbindungsabbrüchen, soweit sinnvoll.
- Speichern nicht sensibler Verbindungseinstellungen.

Passwörter dürfen nicht im Klartext in Konfigurationsdateien oder Logs gespeichert werden. Nutze bei Bedarf geeignete Windows-Sicherheitsmechanismen.

## 6. Anforderungen an den Monitorwechsel

Der Monitorwechsel ist das zentrale Feature.

Implementiere den technisch besten verfügbaren Ansatz.

Priorität A:
Der aktive Remote-Monitor lässt sich unmittelbar wechseln, ohne die Sitzung zu trennen.

Priorität B:
Wenn das technisch nicht möglich ist, verwende einen möglichst schnellen und zuverlässigen Wechsel mit minimaler Unterbrechung.

Priorität C:
Wenn mehrere vorbereitete RDP-Verbindungen notwendig sind, kapsle diese Implementierung so, dass die Oberfläche weiterhin wie ein einziger Remote-Desktop wirkt.

Dokumentiere transparent, welche Variante tatsächlich umgesetzt wurde.

Wichtig: Eine separate RDP-Verbindung darf nicht als nahtloser Wechsel innerhalb derselben Sitzung ausgegeben werden.

Falls der Ansatz mit `mstsc.exe` nicht die erforderliche Kontrolle bietet, implementiere nicht einfach eine Oberfläche, die nur `.rdp`-Dateien startet. Bewerte eine Integration oder Einbettung eines geeigneten RDP-Clients.

## 7. Architektur und Codequalität

Strukturiere das Projekt sauber, beispielsweise in:

- `Views`
- `ViewModels`
- `Services`
- `Models`
- `Infrastructure`
- `Rdp`
- `Configuration`

Trenne UI, Verbindung, Monitorverwaltung und Konfiguration voneinander.

Verwende asynchrone Operationen, wo sinnvoll. Vermeide blockierende UI-Threads.

Berücksichtige:
- Fehlerbehandlung.
- Ressourcenfreigabe.
- Verbindungsabbrüche.
- Fenstergrößenänderungen.
- Unterschiedliche DPI-Einstellungen.
- Mehrere Monitorauflösungen.
- Sicherheitsaspekte.
- Sauberes Beenden der Anwendung.

Erstelle sinnvolle Logs, aber protokolliere keine Passwörter oder andere Zugangsdaten.

## 8. Tests

Erstelle Tests für alle Komponenten, die ohne aktive RDP-Verbindung getestet werden können.

Teste insbesondere:
- Monitor-Auswahl.
- Wechsel zwischen Monitoren.
- Tastenkürzel.
- Fehlerbehandlung.
- Konfigurationsverwaltung.
- Verbindungsstatus.
- Wiederverbindung.

Dokumentiere zusätzlich, wie das Programm mit einem echten Remote-PC mit zwei oder drei Monitoren getestet wird.

## 9. Arbeitsweise

Arbeite direkt im vorhandenen Projektverzeichnis. Wenn noch kein Projekt existiert, lege ein vollständiges Projekt mit einer passenden Solution-Struktur an.

Gehe schrittweise vor:

1. Prüfe die Umgebung und vorhandenen Dateien.
2. Analysiere die RDP-Technik und dokumentiere die Entscheidung kurz.
3. Erstelle die Projektstruktur.
4. Implementiere eine funktionierende Verbindung.
5. Implementiere den Monitorwechsel.
6. Entwickle die Oberfläche.
7. Füge Fehlerbehandlung und Tests hinzu.
8. Baue die Anwendung.
9. Behebe Compilerfehler und offensichtliche Laufzeitprobleme.
10. Erstelle eine README mit Installation, Voraussetzungen, Konfiguration und bekannten Einschränkungen.

Arbeite nicht nur einen Plan aus. Implementiere die Anwendung tatsächlich.

Verwende echte APIs und reale Bibliotheken. Ersetze fehlende Funktionalität nicht durch Platzhalter, simulierte Verbindungen oder lediglich optische Buttons.

Wenn eine wichtige technische Voraussetzung nicht erfüllt ist, erkläre das Problem und implementiere die bestmögliche funktionierende Alternative.

## 10. Lieferumfang

Am Ende möchte ich erhalten:

- Vollständigen Quellcode.
- Eine baubare Visual-Studio-Solution.
- Eine funktionierende Windows-Anwendung.
- Eine Anleitung zum Starten und Bauen.
- Tests.
- Eine Dokumentation der tatsächlich erreichten Monitorwechsel-Funktion.
- Eine Liste der verbleibenden Einschränkungen.

Nenne zum Abschluss ausdrücklich:
1. Welcher RDP-Client tatsächlich verwendet wird.
2. Ob beim Monitorwechsel die bestehende RDP-Sitzung erhalten bleibt.
3. Falls nicht, ob eine neue Verbindung aufgebaut wird.
4. Welche Voraussetzungen auf dem Remote-PC erfüllt sein müssen.
5. Wie ich die Anwendung selbst testen kann.

Beginne jetzt mit der technischen Analyse und setze anschließend die beste praktikable Lösung um.
