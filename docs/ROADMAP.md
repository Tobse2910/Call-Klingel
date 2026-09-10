# Roadmap

Regel für jeden Meilenstein: keine Funktion gilt als fertig, bevor sie auf echter
Hardware nachgewiesen ist. Mocks sind ausschliesslich für die reine UI-Entwicklung
erlaubt, niemals in der produktiven Anwendung.

---

## Meilenstein 1 - Cross-Platform-Grundgerüst — ABGESCHLOSSEN

- [x] Avalonia-Projekt für Windows und Linux
- [x] Startseite mit Plattformerkennung
- [x] Status des Telefonie-Backends
- [x] Schaltfläche "Geräte suchen"
- [x] Anzeige gefundener Bluetooth-Geräte

Nachweis: App startet, Fenster reagiert, Gerätesuche läuft über den echten Backend-Pfad.

## Meilenstein 2 - Telefonie-Diagnose — ABGESCHLOSSEN

- [x] Diagnoseseite mit Betriebssystem, Bluetooth, HFP, Leitungen, Audio
- [x] Schaltflächen: Test Verbindung, Capabilities prüfen, Telefonleitungen suchen
- [x] Kopflose Variante `phonelink-diagnose`
- [x] Nachgewiesen: Telefonie-APIs funktionieren ohne MSIX

Ergebnis siehe `WINDOWS_TELEPHONY.md`. Offen bleibt `RequestAccessAsync()`, weil dafür
ein gekoppeltes Telefon nötig ist.

## Meilenstein 2b - Hardware-Test — ABGESCHLOSSEN

- [x] Samsung S26 Ultra gekoppelt, HFP 0x111F und PBAP 0x112F nachgewiesen
- [x] HFP-Transportgerät von Windows erkannt
- [x] `AudioRoutingStatus = CanRouteToLocalDevice`
- [x] Unpackaged: `RequestAccessAsync()` -> `DeniedBySystem`
- [x] Als MSIX mit `rescap:phoneCall`: **`Allowed`**

Ergebnis: Das Projektziel ist unter Windows erreichbar. Die App muss als MSIX
ausgeliefert werden; der Microsoft Store ist nicht nötig.

Nebenbefund: Das Realtek RTL8852CE-Modul lief auf dem generischen Microsoft-Treiber und
empfing dadurch nichts. Behoben mit den Treibern aus dem Microsoft Update Katalog.

## Bluetooth-Kopplung - Diagnose — ABGESCHLOSSEN

Am 2026-09-10 blockierte ein **LE-only-Bond** die Verbindung: Windows führte das Telefon
als gekoppelt, ohne Classic-Bond gab es aber kein HFP. Alle Telefonie-Abfragen filtern auf
Classic, das Telefon fiel damit aus jeder heraus, und die App konnte nur "kein Gerät
gekoppelt" melden.

- [x] `Core/Bluetooth/BondAnalyzer.cs` unterscheidet die Bond-Zustände, 12 Tests
- [x] Diagnosezeile "Kopplung Classic vs. LE" mit Rohdaten aller Endpunkte
- [x] Startseite nennt Ursache und Lösung statt einer leeren Liste
- [x] Start von 31 s auf 1,4 s: Geräteselektoren statt Association-Endpoint-Enumeration

## Meilenstein 3 - Eingehenden Anruf erkennen — ABGESCHLOSSEN

- [x] HFP-Client in `Core/Hfp/` (Protokoll) und `Platform.Windows` (RFCOMM-Transport)
- [x] Service Level Connection nachgewiesen (`+BRSF`, `+CIND`, `+CMER`, `+CHLD`)
- [x] `RING` und `+CLIP` werden ausgewertet
- [x] `+CIEV` treibt die abstrakten Zustände
- [x] **Mit echtem Anruf nachgewiesen** (2026-09-10, 22:22 Uhr)

Der Nachweis scheiterte zunächst daran, dass der Handshake `AT+CHLD=?` nicht sendete,
obwohl beide Seiten Dreierkonferenz meldeten. Das Telefon hielt die Service Level
Connection damit für unfertig und schwieg bei jedem Anruf. Siehe `WINDOWS_TELEPHONY.md`
Abschnitt 6.

## Meilenstein 4 - Anrufsteuerung — ABGESCHLOSSEN

- [x] Annehmen (`ATA`), Ablehnen und Auflegen (`AT+CHUP`), Stumm (`AT+VGM=0`)
- [x] Anruffenster mit Kontaktbild, Name, Nummer, Gesprächsdauer
- [x] Immer im Vordergrund, unten rechts wie eine Benachrichtigung
- [x] Lokales Adressbuch löst Nummern zu Namen auf
- [x] **Annehmen auf Hardware nachgewiesen**: `ATA` -> `OK` -> `+CIEV: 1,1`

Offen: Bei diesem Anruf kam weder `RING` noch `+CLIP`, die Oberfläche zeigte
"Unbekannter Anrufer". Ob das an Rufnummernunterdrückung lag oder an einem weiteren
Protokolldetail, ist ungeklärt.

## Meilenstein 5 - Audio-Routing — BLOCKIERT, Architekturfrage offen

- [ ] Gesprächsaudio auf PC-Mikrofon und Kopfhörer
- [x] Auswahl der Audiogeräte in den Einstellungen

Gemessen am 2026-09-10 während eines echten Gesprächs: Der Ton lief ausschliesslich über
das Telefon. Die Steuerung am PC funktionierte gleichzeitig einwandfrei.

Der Hinweis oben ist beantwortet, und die Antwort ist unbequem:

| Geprüft | Ergebnis |
|---|---|
| Treiber `BTHHFENUM\BTHHFPAUDIO\...` vorhanden | ja, Status OK |
| Audio-Endpunkt "Hands-Free" in `AudioEndpoint` | **keiner** |
| Hands-Free-Geräte in `DeviceClass.AudioRender` / `AudioCapture` | **0 von 13 bzw. 4** |

Die Steuerung läuft über RFCOMM, das Gesprächsaudio dagegen über einen separaten
SCO-Kanal. Den baut nur der Windows-Bluetooth-Stack auf, und zwar nur, wenn **Windows'
eigener** Hands-Free-Client mit dem Telefon verbunden ist - also genau das, was am Telefon
der Schalter "Anrufe" freigibt. Eine eigene SCO-Verbindung kann die App nicht öffnen:
WinRT bietet dafür keine Schnittstelle.

Damit stehen Steuerung und Audio in Konkurrenz um dieselbe HFP-Verbindung, denn ein Telefon
vergibt sie nur einmal. Zu klären, bevor hier weitergebaut wird:

- [ ] Lässt das Telefon Windows' Hands-Free-Client und unseren RFCOMM-Kanal gleichzeitig zu?
- [ ] Falls nein: liefert die Windows-Telefonie-API bei verbundenem HFP doch eine
      Telefonleitung? Abschnitt 5 in `WINDOWS_TELEPHONY.md` misst 0 - aber nie mit
      tatsächlich aktiver Hands-Free-Verbindung.

## Meilenstein 6 - Linux-Backend

- [ ] `IHfpTransport` über BlueZ implementieren
- [ ] Protokollcode aus `Core/Hfp/` wird unverändert wiederverwendet
- [ ] Oberfläche bleibt unverändert

## Kontakte über PBAP — UMGESETZT

- [x] OBEX-Client in `Core/Pbap/`, Transport in `Platform.Windows`
- [x] vCard-Parser mit 13 Tests: Umlaute, Quoted-Printable, gefaltete Zeilen, Mehrfachnummern
- [x] Auf Hardware nachgewiesen: **571 vCards**, 575 Kontakte importiert
- [x] Suche über Name und Nummer, `0157...` findet `+49157...`
- [x] Senden an das Telefon über OPP - kommt als Datei an, der Import bleibt beim Nutzer

Bleibt Kür, nie Voraussetzung: ohne Kontakt zeigt die App die Rufnummer.

Grenze, gemessen: **Kontakte lassen sich nicht auf das Telefon schreiben.** PBAP darf nur
lesen, OPP liefert eine Datei ab. Siehe `WINDOWS_TELEPHONY.md` Abschnitt 8.

## Später

- Signalstärke feiner über `AT+CSQ` (0-31 statt 0-5), siehe `WINDOWS_TELEPHONY.md` Abschnitt 10
- Anrufverlauf
- System Tray mit "Nicht stören"
- Streamer-Modus über obs-websocket.
  Rufnummer und Kontaktname erscheinen dort standardmäßig **nie**.
