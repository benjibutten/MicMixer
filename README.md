# MicMixer

MicMixer är en Windows-app som skickar mikrofon och musik till en virtuell
mikrofonkanal. Den är byggd för lägen där andra program bara kan välja en enda
mikrofon, men du vill kunna prata, byta till en röstmoddad mic med hotkey och
mixa in MP3-musik i samma kanal.

Appen är en WPF-app för Windows och använder NAudio för ljudroutning.

## Funktioner

- Routar din vanliga mikrofon till en virtuell kabel, till exempel VB-CABLE.
- Växlar till en moddad mikrofon medan en global hotkey hålls nere.
- Kan köras utan moddad mikrofon via valet `Ingen moddad mic`.
- Har återgångsdelay så moddad mic kan ligga kvar kort efter att hotkeyn släpps.
- Mäter ljudnivå för vanlig och moddad mikrofon.
- Spelar MP3-filer och mixar musiken in i mikrofonkanalen.
- Kan hämta ljud från YouTube-länkar och konvertera till MP3.
- Har separat medhörning så du kan höra musiken i egna hörlurar.
- Har spellista, sök, kö, nästa/föregående och volymreglage.
- Minimeras till system tray och öppnar befintlig instans om appen redan kör.
- Skriver loggar för felsökning.

## Krav

- Windows 10 eller Windows 11, x64.
- En vanlig mikrofon.
- En virtuell kabel, rekommenderat VB-CABLE.
- Valfritt: Voicemod eller annat röstmodd-program som exponerar en egen
  mikrofonenhet.
- Internet vid första YouTube/MP3-hämtningen. MicMixer laddar då ner `yt-dlp`
  och `ffmpeg` till sin lokala appdata-mapp och verifierar filerna med SHA-256.

Release-zippen är self-contained, så användare behöver normalt inte installera
.NET separat.

## Ladda ner

Senaste byggda version finns på GitHub Releases:

https://github.com/benjibutten/MicMixer/releases/latest

Ladda ner `MicMixer-win-x64.zip`, packa upp den och starta `MicMixer.exe`.

## Grundsetup

1. Installera VB-CABLE från https://vb-audio.com/Cable/.
2. Starta om Windows om VB-CABLE-installationen ber om det.
3. Starta `MicMixer.exe`.
4. Välj din riktiga mikrofon som `Vanlig mic`.
5. Välj en moddad mikrofon, till exempel `Voicemod Virtual Audio Device`, eller
   välj `Ingen moddad mic`.
6. Välj `CABLE Input (VB-Audio Virtual Cable)` som `Virtuell kabel ut`.
7. I Discord, OBS, FiveM eller spelet väljer du `CABLE Output` som mikrofon.
8. Klicka `Aktivera`.

Om du väljer en utgång som inte ser ut som en virtuell kabel visar appen en
varning. Du kan starta ändå genom att klicka `Aktivera` en gång till, vilket är
praktiskt för andra kabeldrivrutiner.

## Hotkey

Klicka `Ändra` under `Global hotkey` och tryck den tangent eller musknapp som
ska växla till moddad mic. När routningen är aktiv gäller:

- Hotkey nere: moddad mic hörs.
- Hotkey släppt: vanlig mic hörs.
- Återgångsdelay över `0 ms`: moddad mic ligger kvar tills delayn löpt ut.
- `Ingen moddad mic`: hotkeyn stängs av och vanlig mic används hela tiden.

## Musik och MP3

Musiken mixas in i samma virtuella mikrofonkanal som rösten. Det betyder att
mottagaren hör både mic och musik via `CABLE Output`.

Du kan lägga till musik på två sätt:

- Klistra in en YouTube-länk i fältet och klicka `Hämta MP3`.
- Klicka mappikonen och lägg egna `.mp3`-filer i musikmappen.

Första gången du hämtar en länk laddar MicMixer ner `yt-dlp` och `ffmpeg`.
`ffmpeg`-paketet är stort, så första hämtningen kan ta en stund. Efteråt ligger
verktygen kvar lokalt och återanvänds.

Musik kan spelas när routningen är aktiv eller när medhörning är aktiverad. Om
båda saknas finns ingen ljudklocka som driver uppspelningen, så appen pausar i
stället för att visa en låt som ser ut att spela men står still.

Mottagande appar kan dämpa musik om brusreducering, echo cancellation eller
automatic gain control är aktiverat. Stäng av sådana funktioner i Discord,
FiveM, OBS eller spelet om musiken låter hackig eller försvinner.

## Medhörning

Medhörning spelar musiken i dina egna hörlurar eller högtalare. Den påverkar
inte vad andra hör i mic-kanalen. Välj en fysisk ljudutgång som monitor-enhet,
inte den virtuella kabeln.

## Lokal data

MicMixer sparar inställningar, musik, verktyg och loggar under:

```text
%LocalAppData%\MicMixer
```

Viktiga undermappar och filer:

- `settings.json`: valda enheter, hotkey, volymer och övriga inställningar.
- `Music\`: MP3-filer som visas i spellistan.
- `tools\`: nedladdade `yt-dlp` och `ffmpeg`.
- `logs\micmixer-YYYYMMDD.log`: runtime-loggar.
- `startup-timeline.log`: enkel tidslinje för uppstart.

Loggar roteras dagligen, rullas vid 5 MB och behålls i 14 dagar.

## Felsökning

- Om andra inte hör något: kontrollera att MicMixer skickar till `CABLE Input`
  och att mottagande app använder `CABLE Output`.
- Om musiken inte hörs hos andra: kontrollera att routningen är aktiv och att
  mottagande app inte filtrerar bort musik med brusreducering.
- Om du inte själv hör musiken: aktivera `Medhörning` och välj hörlurar eller
  högtalare.
- Om YouTube-hämtning misslyckas: kontrollera internetanslutning och titta i
  loggen under `%LocalAppData%\MicMixer\logs`.
- Om en ljudenhet har kopplats ur: klicka uppdatera-knappen i appen och välj
  enheterna igen.

## Utveckling

Projektet ligger i `src/MicMixer` och targetar `net10.0-windows`.

Bygg lokalt:

```powershell
dotnet build .\src\MicMixer\MicMixer.csproj
```

Publicera en lokal win-x64-build:

```powershell
dotnet publish .\src\MicMixer\MicMixer.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\artifacts\publish\win-x64
```

## CI/CD

Pull requests mot `main` kör `.github/workflows/pr-build.yml`, som återställer
paket och bygger appen i Release-konfiguration på Windows.

Pushar till `main` som ändrar appens källkod eller projektfiler kör
`.github/workflows/release.yml`, som publicerar en self-contained
`win-x64`-build, zippar den och skapar eller uppdaterar senaste GitHub Release.

README-, dokumentations- och workflow-only-pushar startar inte release-workflowen
och skapar därför ingen ny version. Manuell release kan fortfarande köras via
`workflow_dispatch`.

## Licens

Se [LICENSE](LICENSE).
