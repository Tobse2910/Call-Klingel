# Architektur

## Schichtenmodell

```
                    PhoneLink PC
                         |
                  Avalonia Oberfläche          PhoneLinkPC.App
                         |
                 Telephony Interface            PhoneLinkPC.Core
                         |
             +-----------+-----------+
             |                       |
          Windows                  Linux
             |                       |
   Windows Calls / HFP       BlueZ / D-Bus / HFP
             |               PipeWire / WirePlumber
             +-----------+-----------+
                         |
                       Bluetooth
                         |
                     Dein Samsung
                         |
                        SIM
                         |
                   Mobilfunknetz
```

Das Smartphone bleibt das eigentliche Mobilfunktelefon. Der PC verhält sich gegenüber
dem Telefon als Freisprecheinrichtung (Hands-Free Unit). Es gibt keine zweite Rufnummer,
keinen SIP-Anbieter und keine Cloud.

## Projekte

| Projekt | Zielframework | Inhalt |
|---|---|---|
| `PhoneLinkPC.Core` | `net9.0` | Modelle, `ITelephonyService`, Zustände, Nummernmaskierung |
| `PhoneLinkPC.Infrastructure` | `net9.0` | Logging, Backend-Auswahl |
| `PhoneLinkPC.Platform.Windows` | `net9.0-windows10.0.26100.0` | WinRT-Telefonie |
| `PhoneLinkPC.Platform.Linux` | `net9.0` | BlueZ / PipeWire (Meilenstein 6) |
| `PhoneLinkPC.App` | plattformabhängig | Avalonia-Oberfläche |
| `PhoneLinkPC.Diagnostics.Cli` | plattformabhängig | Kopflose Diagnose |
| `PhoneLinkPC.Core.Tests` | `net9.0` | Unit-Tests |

### Warum das Zielframework bedingt gesetzt ist

WinRT-Projektionen existieren nur unter Windows. Die plattformabhängigen Projekte setzen
ihr Zielframework darum über eine MSBuild-Bedingung:

```xml
<TargetFramework Condition="$([MSBuild]::IsOSPlatform('Windows'))">net9.0-windows10.0.26100.0</TargetFramework>
<TargetFramework Condition="!$([MSBuild]::IsOSPlatform('Windows'))">net9.0</TargetFramework>
```

Damit bleibt `dotnet build PhoneLinkPC.sln` auf beiden Plattformen lauffähig. Unter Linux
werden die WinRT-Quellen aus `WinRt/` per `<Compile Remove>` ausgeschlossen.

## Die Abstraktionsgrenze

Die Oberfläche kennt ausschliesslich `ITelephonyService` und die abstrakten Zustände
`DeviceConnectionState` und `CallState`. Sie kennt weder `Windows.ApplicationModel.Calls`
noch BlueZ.

```
DeviceConnectionState : Disconnected | Connecting | Connected | Error
CallState            : Idle | Ringing | Dialing | Active | Held | Ended | Error
```

`TelephonyBackendFactory` ist die einzige Stelle, die das Betriebssystem kennt. Sie lädt
das passende Backend per Reflection, damit die neutralen Projekte keinen
Kompilierzeit-Verweis auf eine Assembly brauchen, die auf dem Zielsystem eventuell fehlt.

Gibt es für eine Plattform kein Backend, liefert die Factory `UnsupportedTelephonyService`.
Dieser meldet jede Aktion ehrlich als nicht unterstützt, statt stillschweigend nichts zu tun.

## Fehlerbehandlung

Telefonieaktionen werfen keine Ausnahmen für erwartbare Fehlschläge. Sie liefern
`TelephonyResult` mit `Success` und `Error`. Die Oberfläche zeigt den echten Fehlertext
inklusive HRESULT an.

Grund: eine Schaltfläche darf niemals so aussehen, als hätte sie einen Mobilfunkanruf
angenommen, wenn das Betriebssystem den Aufruf abgelehnt hat.

## Diagnose

`ITelephonyDiagnostics` liefert einen `TelephonyDiagnosticsReport` aus einzelnen
`DiagnosticCheck`-Zeilen. Jede Zeile trägt:

- `Status` - Ok, Warning, Failed, Skipped, Info, Unknown
- `Value` - Kurzergebnis
- `Probe` - der tatsächlich ausgeführte API-Aufruf
- `Detail` - HRESULT, Ausnahmetext oder Grund für das Überspringen

`Probe` macht jede rote Zeile von Hand nachvollziehbar. Oberfläche und CLI verwenden
denselben Bericht, damit Support-Ausgabe und Diagnoseseite nicht auseinanderlaufen.

## Datenschutz

- Vollständige Rufnummern erscheinen nie im Log. `PhoneNumberMasker` erzeugt
  `+49 176 **** 5678`.
- Keine Gesprächsaufzeichnung, keine Audioübertragung an eigene Server.
- Lokal gespeichert werden nur Einstellungen, bekannte Geräte und optional Kontakte
  und Anrufverlauf.
