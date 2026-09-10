# Windows-Telefonie: geprüfte Fakten

Alle Angaben in diesem Dokument wurden auf echter Hardware gemessen, nicht aus der
Dokumentation abgeschrieben. Messdatum: 2026-09-10, Windows 11 Pro Build 10.0.26200,
.NET 9.0.20, App **unpackaged** (kein MSIX).

Reproduzierbar mit:

```
dotnet run --project tools/CallKlingel.Diagnostics.Cli
```

---

## 1. Die entscheidende Erkenntnis

Gemessen am 2026-09-10 mit gekoppeltem und verbundenem Samsung S26 Ultra.

**Eine unpackaged .NET-App bekommt KEINEN Telefoniezugriff auf das HFP-Gerät.**

| Aufruf | Ergebnis | Bedeutung |
|---|---|---|
| `PhoneCallManager.RequestStoreAsync()` | **OK** | Store funktioniert unpackaged |
| `PhoneCallStore.RequestLineWatcher()` | **OK** | Watcher funktioniert unpackaged |
| `PhoneLineTransportDevice.RequestAccessAsync()` | **DeniedBySystem** | **Blockade** |
| `PhoneLineTransportDevice.AudioRoutingStatus` | `CanRouteToLocalDevice` | Audio wäre routbar |
| `PhoneCallStore.GetDefaultLineAsync()` | `0x8007139F` | Folge der fehlenden Freigabe |
| `PhoneLineWatcher` Leitungen | 0 | Folge der fehlenden Freigabe |

### Korrektur einer früheren Annahme

Ein erster Messlauf ohne gekoppeltes Telefon zeigte, dass `RequestStoreAsync()` unpackaged
funktioniert. Daraus wurde zunächst geschlossen, MSIX sei generell nicht nötig.

**Das war zu früh geschlossen.** Der Store ist nur die halbe Miete. Der Zugriff auf das
konkrete HFP-Transportgerät - und damit auf Anrufe - läuft über
`PhoneLineTransportDevice.RequestAccessAsync()`, und der wird ohne Paketidentität
systemseitig verweigert.

### Warum DeniedBySystem und nicht DeniedByUser

Beides wurde geprüft und ausgeschlossen:

| Mögliche Ursache | Messung | Ergebnis |
|---|---|---|
| Datenschutzeinstellung | `ConsentStore\phoneCall` (HKCU + HKLM) | `Allow` |
| Anrufverlauf-Berechtigung | `ConsentStore\phoneCallHistory` | `Allow` |
| Gruppenrichtlinie | `HKLM\SOFTWARE\Policies\...\AppPrivacy` | nicht gesetzt |
| Telefon nicht verbunden | HFP-Transportgeräte | 1, verbunden |
| Paketidentität | `GetCurrentPackageFullName` | **keine** |

Es bleibt genau eine Erklärung: `phoneCall` ist eine **Restricted Capability**. Eine App
ohne MSIX-Paket kann sie nicht deklarieren, also verweigert Windows den Zugriff.

### Konsequenz

Für Meilenstein 3 und 4 ist ein **MSIX-Paket** nötig, das im Manifest deklariert:

```xml
<Package
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  <Capabilities>
    <rescap:Capability Name="phoneCall" />
    <rescap:Capability Name="phoneCallHistory" />
    <DeviceCapability Name="bluetooth" />
  </Capabilities>
</Package>
```

Sideloading genügt; der Microsoft Store ist nicht erforderlich. Ob eine lokal signierte
MSIX die Restricted Capability tatsächlich freischaltet, ist der **nächste zu messende
Punkt** - nicht anzunehmen, sondern zu prüfen.

## 2. Verfügbare API-Oberfläche

Alle folgenden Typen sind in der .NET-WinRT-Projektion vorhanden und wurden gegen
Windows SDK 10.0.26100 kompiliert.

### Anrufsteuerung: `Windows.ApplicationModel.Calls.PhoneCall`

Diese Klasse liefert exakt die geforderten Aktionen:

| Anforderung | API |
|---|---|
| Annehmen | `AcceptIncomingAsync()` |
| Ablehnen | `RejectIncomingAsync()` |
| Auflegen | `EndAsync()` |
| Stummschalten | `MuteAsync()` / `UnmuteAsync()` |
| DTMF-Tastatur | `SendDtmfKeyAsync(DtmfKey, DtmfToneAudioPlayback)` |
| Halten | `HoldAsync()` / `ResumeFromHoldAsync()` |
| Audioausgabe umschalten | `ChangeAudioDeviceAsync(PhoneCallAudioDevice)` |

Alle Methoden liefern `PhoneCallOperationStatus`:
`Succeeded, OtherFailure, TimedOut, ConnectionLost, InvalidCallState`.

> Achtung: der Erfolgswert heisst `Succeeded`, nicht `Success`.

Ereignisse: `StatusChanged`, `IsMutedChanged`, `AudioDeviceChanged`.

### Zustände: `PhoneCallStatus`

`Lost, Incoming, Dialing, Talking, Held, Ended`

Abbildung auf den abstrakten `CallState` der Anwendung:

| Windows | CallKlingel |
|---|---|
| `Incoming` | `Ringing` |
| `Dialing` | `Dialing` |
| `Talking` | `Active` |
| `Held` | `Held` |
| `Ended`, `Lost` | `Ended` |

### Anruferinformationen: `PhoneCallInfo`

`CallDirection`, `DisplayName`, `PhoneNumber`, `StartTime`, `LineId`, `IsHoldSupported`.

Damit sind Nummer und Name direkt verfügbar; PBAP ist dafür nicht nötig.

### Bluetooth-Transport: `PhoneLineTransportDevice`

| Mitglied | Zweck |
|---|---|
| `GetDeviceSelector(PhoneLineTransport.Bluetooth)` | Selektor für HFP-Geräte |
| `FromId(string)` | Instanz zu einer Geräte-ID |
| `RequestAccessAsync()` | **Instanzmethode**, liefert `DeviceAccessStatus` |
| `RegisterApp()` | App als Telefonie-Client anmelden |
| `ConnectAsync()` | Verbindung aufbauen |
| `AudioRoutingStatus` | `Unknown` / `CanRouteToLocalDevice` / `CannotRouteToLocalDevice` |
| `InBandRingingEnabled` | Klingelton über Bluetooth |

`CanRouteToLocalDevice` ist der Nachweis, dass Gesprächsaudio auf PC-Mikrofon und
PC-Kopfhörer gelegt werden darf. Das ist die Grundlage für Meilenstein 5.

`DeviceAccessStatus`: `Unspecified, Allowed, DeniedByUser, DeniedBySystem`.

### Leitungen: `PhoneLine`

`FromIdAsync(Guid)`, `GetAllActivePhoneCallsAsync()`, `DialWithResult(...)`,
`Transport`, `NetworkState`, `CanDial`, `TransportDeviceId`, Ereignis `LineChanged`.

Wichtig: **`PhoneLine` hat keine Annehmen/Ablehnen-Methoden.** Die Anrufsteuerung liegt
ausschliesslich auf `PhoneCall`. Der Weg dorthin ist:

```
PhoneCallManager.RequestStoreAsync()
  -> PhoneCallStore.RequestLineWatcher()   // LineAdded liefert Guid
  -> PhoneLine.FromIdAsync(guid)
  -> PhoneLine.GetAllActivePhoneCallsAsync()
  -> PhoneCall                              // hier sitzen Answer/Reject/End/Mute
```

`DialWithResult` liefert `PhoneLineDialResult` mit `DialCallStatus` und `DialedCall` -
kein Enum, sondern eine Klasse.

---

## 3. Wie Windows das Telefon sieht

Der von Windows selbst erzeugte HFP-Selektor lautet:

```
System.Devices.DevObjectType:=8
AND System.Devices.InterfaceClassGuid:="{BD41DF2D-ADDD-4FC9-A194-B9881D2A2EFA}"
AND System.Devices.DeviceInstanceId:~~"{0000111f-0000-1000-8000-00805f9b34fb}"
AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True
```

`0000111f` ist die Bluetooth-SIG-UUID für **Hands-Free Audio Gateway**. Das Telefon ist
das Audio Gateway, der PC ist die Hands-Free Unit. Genau diese Rollenverteilung wurde im
Projektziel gefordert - sie wird vom Betriebssystem so bestätigt.

---

## 4. Messergebnis auf diesem PC

Hardware: Gigabyte X670 AORUS ELITE AX, Realtek RTL8852CE (WLAN 6E + Bluetooth).
Telefon: Samsung S26 Ultra, gekoppelt und verbunden.

| Prüfung | Ergebnis |
|---|---|
| Bluetooth-Adapter | OK, Adresse `112233445566` |
| Bluetooth Classic (BR/EDR) | Unterstützt |
| Gekoppelte Geräte | 1 (Samsung S26 Ultra) |
| Geräteklasse | `Phone` |
| HFP 0x111F | **vorhanden** |
| PBAP 0x112F | **vorhanden** (Kontakte später möglich) |
| HFP-Transportgeräte | **1** |
| Telefonie-Zugriff | **DeniedBySystem** |
| Audio-Route | **CanRouteToLocalDevice** |
| In-Band Ringing | False |
| PhoneCallStore | Zugriff erteilt |
| Standardleitung | `0x8007139F` |
| Telefonleitungen | 0 |

### Wichtiger Hardware-Hinweis

Das Realtek-Modul lief anfangs auf dem generischen Microsoft-Treiber (`bth.inf`). In dem
Zustand meldete Windows Status OK und Radio On, der Chip empfing aber nichts: Die Firmware
`rtl8852c_mp_chip_new.dat` wird nur vom Realtek-Treiber geladen.

Abhilfe: Treiber `rtkbtfilter.inf` 18.4032.2510.900 und `netrtwlane602.inf` 6001.16.175.0
aus dem Microsoft Update Katalog (Suche nach der Hardware-ID, nicht nach dem Modellnamen).
Danach heisst das Gerät `Realtek Bluetooth Adapter` statt `Generic Bluetooth Adapter`.

## 5. Ergebnis: Die Microsoft-API ist für Drittanbieter tot

Gemessen am 2026-09-10 mit gekoppeltem Samsung S26 Ultra, als signiertes MSIX mit
`rescap:phoneCall`.

| Aufruf | unpackaged | als MSIX |
|---|---|---|
| `RequestAccessAsync()` | `DeniedBySystem` | **`Allowed`** |
| `RegisterApp()` | - | wirft nichts, **wirkt aber nicht** |
| `IsRegistered()` danach | - | **`False`** |
| `ConnectAsync()` | - | `False` |
| Telefonleitungen | 0 | **0** |

Ausgeschlossen wurde: Datenschutzeinstellung (`phoneCall = Allow` in HKCU und HKLM),
Gruppenrichtlinie (keine gesetzt), fehlende Kopplung (Telefon verbunden, HFP aktiv).

Dasselbe Verhalten meldet ein anderes Projekt seit März 2023:
<https://github.com/BestOwl/MyPhone/issues/26> - seit Windows 11 22H2 liefert
`RequestAccessAsync` zwar `Allowed`, `RegisterApp` bleibt aber wirkungslos. Microsofts
eigenes Phone Link funktioniert weiter.

**Schlussfolgerung:** `Windows.ApplicationModel.Calls` ist für dieses Projekt unbrauchbar.
Nicht wegen eines Fehlers im Code, sondern weil Microsoft die Funktion hinter der
Berechtigungsprüfung abgeschaltet hat.

---

## 6. Der Weg, der funktioniert: HFP selbst sprechen

Statt Windows um eine Telefonleitung zu bitten, öffnet der PC selbst einen RFCOMM-Kanal
zum Hands-Free Audio Gateway des Telefons (UUID `0x111F`) und spricht das AT-Protokoll.

Verifiziert am 2026-09-10:

```
*** RFCOMM-VERBINDUNG HERGESTELLT ***
>> AT+BRSF=127
<< +BRSF: 4079
>> AT+CIND=?
<< +CIND: ("call",(0,1)),("callsetup",(0-3)),("service",(0-1)),
          ("signal",(0-5)),("roam",(0,1)),("battchg",(0-5)),("callheld",(0-2))
>> AT+CIND?
<< +CIND: 0,0,1,3,0,4,0
>> AT+CMER=3,0,0,1
<< OK
```

Das Telefon antwortet, obwohl Windows seinen eigenen Hands-Free-Dienst am selben Gerät
hält. Zwei parallele Kanäle sind hier also möglich.

### Der Handshake oben ist unvollständig - und das kostete einen Abend

Gemessen am 2026-09-10, spät abends. Symptom: Die Verbindung stand, der Handshake galt als
fertig, `+CIND` lieferte echte Netzwerte - und das Telefon klingelte, ohne dass am PC
irgendetwas ankam. Über zwanzig Minuten Verbindung, **keine einzige unaufgeforderte
Zeile**.

Die Ursache steht im Protokollmitschnitt oben: Nach `AT+CMER` ist Schluss. Es fehlt
`AT+CHLD=?`.

| Seite | Bitmap | Bit | Bedeutung |
|---|---|---|---|
| Hands-Free (wir) | `AT+BRSF=127` | 1 | Anklopfen und Dreierkonferenz |
| Audio Gateway (Telefon) | `+BRSF: 4079` | 0 | Dreierkonferenz |

Melden **beide** Seiten diese Fähigkeit, schreibt das Profil vor, dass die
Freisprecheinrichtung `AT+CHLD=?` sendet. Die Antwort des Gateways schliesst die Service
Level Connection ab. Vorher gilt sie als unfertig - und **ein Audio Gateway sendet vor
Abschluss der SLC keine unaufgeforderten Result Codes**. Kein `RING`, kein `+CIEV`, kein
`+CLIP`.

Das erklärt jede Beobachtung des Abends:

| Beobachtung | Erklärung |
|---|---|
| Alle Kommandos mit `OK` beantwortet | Angefragte Antworten sind davon nicht betroffen |
| `+CIND` liefert echte Netzwerte | ebenfalls angefragt |
| Telefon klingelt, PC bleibt still | unaufgeforderte Codes werden zurückgehalten |
| Verbindung wirkt stabil und gesund | ist sie auch - nur eben unfertig |

Nach dem Nachziehen von `AT+CHLD=?` antwortete das Telefon sofort:

```
>> AT+CHLD=?
<< +CHLD: (0,1,1x,2,2x,3)
<< OK
```

Und beim nächsten Anruf, zum ersten Mal überhaupt:

```
<< +CIEV: 2,1        callsetup=1, eingehender Anruf
>> ATA
<< OK
<< +CIEV: 1,1        call=1, Gespräch aktiv
<< +CIEV: 2,0
```

**Lehre:** Ein Feature in `AT+BRSF` zu melden ist eine Zusage, keine Höflichkeit. Das
Telefon hält die Gegenstelle daran fest und wartet auf die zugehörigen Kommandos. Wer
Fähigkeiten meldet, die er nicht ausspricht, bekommt eine Verbindung, die sich
einwandfrei anfühlt und nie etwas meldet.

Falsche Fährten auf dem Weg dorthin, alle widerlegt: die Kopplungsart, eine geratene
PIN `0000`, abgelehnte Authentifizierungsanforderungen im Ereignisprotokoll
(`BTHUSB` 16 und 37 - die traten während des Kopplungsvorgangs auf und waren normal), und
der zurückspringende Schalter "Anrufe" am Telefon. Keine davon war die Ursache. Der
Protokollcode wurde zuletzt geprüft statt zuerst.

### Zuordnung auf die Projektanforderungen

| Anforderung | HFP |
|---|---|
| Eingehender Anruf | `RING`, Nummer über `+CLIP` |
| Zustandswechsel | `+CIEV` auf `call` und `callsetup` |
| Annehmen | `ATA` |
| Ablehnen und Auflegen | `AT+CHUP` |
| Stummschalten | `AT+VGM=0` |
| DTMF | `AT+VTS` |
| Wählen | `ATD<nummer>;` |
| Akkustand | `battchg` aus `+CIND`, Skala 0-5 |
| Signalstärke | `signal` aus `+CIND` |

### Warum das die bessere Architektur ist

Der Code liegt in `CallKlingel.Core/Hfp/`, nicht im Windows-Projekt. Nur der Transport ist
plattformabhängig: ein RFCOMM-Socket unter Windows, ein BlueZ-Dateideskriptor unter Linux.
Damit bedient eine Protokollimplementierung beide Plattformen - der Linux-Meilenstein wird
dadurch kleiner statt grösser.

### Bleibt MSIX nötig?

Für den HFP-Weg nicht zwingend - RFCOMM zu einem gekoppelten Gerät funktioniert auch
unpackaged. Das Paket bleibt trotzdem sinnvoll: saubere Installation, Startmenü-Eintrag,
Deinstallation in einem Schritt.

---

## 7. Die LE-Falle: gekoppelt ist nicht gleich gekoppelt

Gemessen am 2026-09-10. Symptom: Die App meldete `0 gekoppelte Geräte` und `0
HFP-Transportgeräte`, während das Telefon in den Windows-Bluetooth-Einstellungen als
gekoppeltes Gerät stand. Ein Kopplungsversuch aus der App lief in
`AuthenticationTimeout`.

### Messung

Beide Endpunkte gehören zur selben Adresse `aa:bb:cc:dd:ee:ff`:

| Endpunkt | `System.Devices.Aep.IsPaired` |
|---|---|
| Classic (BR/EDR), Protokoll `{e0cbf06c-...}` | **False** |
| Low Energy, Protokoll `{bb7bb05e-...}` | **True** |

Bestätigt im Geräte-Manager: es existiert ausschliesslich ein Knoten
`BTHLE\DEV_AABBCCDDEEFF`, **kein** `BTHENUM\...`. Der Bond-Datensatz unter
`HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\aabbccddeeff` führt
`LastConnected = 0` sowie ausschliesslich LE-Felder (`LEName`, `LEAddressType`,
`LocalEvaldIoCapLE`).

### Ursache

Das Telefon war **nur über Bluetooth LE** gebondet. HFP ist ein Classic-Profil und läuft
über RFCOMM; ohne BR/EDR-Bond gibt es kein Hands-Free Audio Gateway, kein
Transportgerät und keine Telefonleitung. Sämtliche Folgesymptome stammen aus dieser
einen Tatsache.

Auch der `AuthenticationTimeout` erklärt sich damit: solange der LE-Bond besteht, kennt
das Telefon den PC bereits und zeigt beim Koppelversuch keine Bestätigung mehr an.

### Warum das so schwer zu sehen war

Jede Telefonie-Abfrage unter Windows filtert auf Classic - das ist richtig, denn nur
Classic trägt HFP. Ein LE-only gebondetes Telefon fällt damit aus **allen** Abfragen
heraus. Die App konnte nur "kein Gerät gekoppelt" sagen, obwohl das Telefon sichtbar
gekoppelt war. Genau diese Lücke schliesst jetzt `Core/Bluetooth/BondAnalyzer.cs`.

### Behebung

Kopplung auf **beiden** Seiten entfernen, dann neu koppeln. Eine Seite genügt nicht.

### Nebenbefund: Enumeration über Association Endpoints ist langsam

`DeviceInformation.FindAllAsync(..., DeviceInformationKind.AssociationEndpoint)` auf die
Bluetooth-Protokolle braucht reproduzierbar **~30 Sekunden**, unabhängig davon, ob auf
`IsPaired` gefiltert wird - sie führt eine Inquiry aus. Ein `DeviceWatcher` verhält sich
genauso (`EnumerationCompleted` nach 30 s).

Die Geräteselektoren liefern dieselben Fakten in **unter 100 ms**:

| Aufruf | Dauer |
|---|---|
| `FindAllAsync(AssociationEndpoint, Classic + LE)` | ~30 000 ms |
| `BluetoothDevice.GetDeviceSelectorFromPairingState(true)` | ~3 ms |
| `BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)` | ~4 ms |
| `BluetoothDevice.FromBluetoothAddressAsync()` für die Geräteklasse | ~11 ms |

Deshalb nutzt `WindowsBondInspector` die Selektoren. Zwei Fallstricke dabei:

- Das `DeviceInformation` einer Geräteschnittstelle meldet `Pairing.IsPaired = False`,
  auch wenn das Gerät gebondet ist. Massgeblich ist die Mitgliedschaft in der Liste des
  Selektors - wer die Eigenschaft liest, dreht die Diagnose ins Gegenteil.
- `System.Devices.Aep.Bluetooth.Cod.Major` kommt als **UInt16**. Wer `UInt32` abfragt,
  bekommt stillschweigend nichts und hält jedes Telefon für ein unbekanntes Gerät.

---

## 8. Kontakte: PBAP liest, OPP schreibt nicht

Gemessen am 2026-09-10 mit dem Samsung S26 Ultra.

### Telefonbuch lesen funktioniert

| Schritt | Ergebnis |
|---|---|
| SDP-Eintrag `0x112F` | Profil `0x1130`, Version `0x0102` (PBAP 1.2) |
| `0x0004` ProtocolDescriptorList | L2CAP / RFCOMM **Kanal 19** / OBEX |
| `0x0200` GoepL2CapPsm | `0x1005` - L2CAP wird ebenfalls angeboten |
| `0x0314` SupportedRepositories | `0x0B` - Telefonbuch, SIM, Favoriten |
| `OBEX CONNECT` über RFCOMM | `0xA0`, ConnectionId 1 |
| `GET telecom/pb.vcf` | 8 Pakete, 61 321 Zeichen, **571 vCards** |

Wichtig: Ohne die Freigabe **"Kontakte und Anrufverlauf teilen"** für diesen PC antwortet
der Server **gar nicht** - kein `0xC3`, keine Fehlermeldung, nur Stille. Ein Timeout an
dieser Stelle ist deshalb fast immer ein Berechtigungsproblem, kein Protokollproblem.

RFCOMM genügt; die L2CAP-Variante wird nicht gebraucht, was praktisch ist, weil WinRT
keinen rohen L2CAP-Kanal öffnen kann.

### Kontakte auf das Telefon schreiben geht nicht

| Weg | Richtung | Ergebnis |
|---|---|---|
| PBAP `0x112F` | Telefon -> PC | Nur Lesen, das Profil sieht Schreiben nicht vor |
| OPP `0x1105` | PC -> Telefon | `CONNECT 0xA0`, `PUT 0xA0` - **Adressbuch bleibt unberührt** |

Die vCard kommt an und wird vom Telefon angenommen. Android legt sie als **Datei** ab und
importiert sie nicht. Auch nach erteilter Freigabe entsteht kein Kontakt.

Es existiert kein Bluetooth-Profil, mit dem ein PC in das Adressbuch eines Android-Telefons
schreiben darf. Das ist Absicht, nicht Lücke. Anwendungen, die es dennoch können, haben
eine **App auf dem Telefon** mit Schreibrechten - was dem Projektversprechen "Keine
Play-Store-App als Voraussetzung" widerspricht.

Konsequenz für die Oberfläche: Die App meldet **"an das Telefon geschickt"**, niemals
"gespeichert". Sie behauptet nur, was sie nachweisen kann.

## 9. Wo die Zeit beim Verbinden hingeht

Gemessen am 2026-09-10 über mehrere Läufe.

| Phase | warm | kalt |
|---|---|---|
| Gerätesuche (`ScanDevicesAsync`) | ~1 300 ms | ~1 300 ms |
| SDP-Abfrage, weckt den Funk-Link | 636 ms | 5 196 ms je Runde, bis zu 3 Runden |
| RFCOMM-Verbindung | 300-600 ms | bis 5 456 ms |
| HFP-Handshake (`BRSF`/`CIND`/`CMER`) | ~210 ms | ~210 ms |
| **gesamt** | **2 566 ms** | **bis 26 112 ms** |

Der Handshake ist nie das Problem. Die Zeit steckt fast vollständig im **Aufwecken des
Funk-Links**, und die 5,2 Sekunden pro SDP-Runde sind ein fester Timeout in Windows.

Ein *gescheiterter* RFCOMM-Versuch kostet dagegen nur ~270 ms. Die Wiederholung ist deshalb
nach **Zeitbudget** begrenzt (12 s) und nicht nach Versuchszahl - die Anzahl sagt nichts
darüber aus, wie lange jemand wartet.

Kein Abkürzen über den SDP-Cache: `ConnectionStatus` meldet `Disconnected`, selbst wenn
der Link warm ist und die Abfrage in 636 ms antwortet, weil noch kein Profil verbunden ist.
Eine auf diesen Status gestützte Abkürzung liefe nie, und ungeprüft ausgeführt würde
sie genau die Abfrage überspringen, die den Link weckt.

## 10. Akkustand: Stufe 0-5 ist das Ende der Fahnenstange

Gemessen am 2026-09-10.

| Aufruf | Antwort |
|---|---|
| `+CIND` `battchg` | `4` - Skala 0-5, also sechs Stufen a 20 % |
| `AT+CBC?` / `AT+CBC=?` | `ERROR` |
| `AT+XAPL=...` | `+XAPL=iPhone,7` - meldet den Akku **des PCs an das Telefon** |
| `AT+BIND=?` | `(1,2)` - HF-Indikatoren, ebenfalls falsche Richtung |
| GATT Battery Service `0x180F` | `Unreachable` - Android bietet ihn nicht an |
| `AT+CSQ` | `+CSQ: 8,99` - Signal in **0-31**, feiner als die `+CIND`-Skala |

Genauer als 20-Prozent-Stufen gibt das Telefon den Akkustand über HFP nicht heraus. Die
Oberfläche zeigt deshalb **"Akku ca. 80 %"** statt "80 %" - eine glatte Prozentzahl
behauptet eine Genauigkeit, die das Protokoll nicht liefert.

Beim **Signal** wäre `AT+CSQ` sechsmal feiner als `+CIND` und liesse sich sogar in dBm
umrechnen. Noch nicht umgesetzt.
