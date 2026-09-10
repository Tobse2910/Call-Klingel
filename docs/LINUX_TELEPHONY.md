# Linux-Telefonie (Meilenstein 6)

Status: **noch nicht implementiert.** `LinuxTelephonyService` ist ein Skelett, das jede
Aktion ehrlich als nicht unterstützt meldet. Es täuscht keine Telefonie vor.

Dieses Dokument hält den geplanten Weg fest. Die Angaben sind noch **nicht** auf
Hardware gemessen - anders als in `WINDOWS_TELEPHONY.md`. Sie sind vor der Umsetzung zu
verifizieren.

## Rollenverteilung

Wie unter Windows ist das Telefon das **Audio Gateway** (HFP AG, UUID `0x111F`) und der
PC die **Hands-Free Unit** (HFP HF, UUID `0x111E`).

## Zu untersuchende Bausteine

| Baustein | Zweck |
|---|---|
| BlueZ / `bluetoothd` | Bluetooth-Profile, HFP |
| D-Bus | Steürschnittstelle zu BlueZ |
| PipeWire | Gesprächsaudio |
| WirePlumber | Umschalten des Bluetooth-Audioprofils auf HFP |

## Geplante D-Bus-Oberfläche

Vor der Implementierung ist zu klären, welcher der beiden Wege auf dem Zielsystem
verfügbar ist:

1. **oFono** (`org.ofono`) mit HFP-Client. Bietet `VoiceCallManager` mit `Answer`,
   `Hangup` und `Reject`. Nicht auf allen Desktop-Distributionen vorinstalliert.
2. **BlueZ-Profil direkt** (`org.bluez.Profile1`). Der PC registriert ein eigenes
   HFP-HF-Profil und spricht die AT-Kommandos selbst
   (`RING`, `+CLIP`, `ATA`, `AT+CHUP`, `AT+VGS`).

Die Zuordnung der AT-Kommandos auf `ITelephonyService`:

| Aktion | AT-Kommando |
|---|---|
| Eingehender Anruf | `RING`, Nummer über `+CLIP` |
| Annehmen | `ATA` |
| Ablehnen / Auflegen | `AT+CHUP` |
| Stummschalten | lokal im PipeWire-Knoten |
| DTMF | `AT+VTS` |

## Aktuelle Diagnose

`LinuxTelephonyDiagnostics` prüft heute nur, ob die nötigen Dienste laufen
(`bluetoothd`, `pipewire`, `wireplumber`). Die Telefoniezeile steht bewusst auf
`Unknown` statt auf `Ok`.

## Abhängigkeiten

Distributionsabhängig, unter Debian/Ubuntu etwa:

```
sudo apt install bluez pipewire pipewire-audio-client-libraries wireplumber libspa-0.2-bluetooth
```

`libspa-0.2-bluetooth` wird für die HFP-Profile in PipeWire benötigt.

## Bedingung für den Beginn

Meilenstein 6 startet erst, wenn die Windows-Meilensteine 3 bis 5 auf echter Hardware
bestanden sind. Oberfläche und Core-Logik werden dabei nicht verändert - es entsteht
ausschliesslich `LinuxTelephonyService`.
