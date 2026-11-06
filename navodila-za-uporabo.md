# Glasba · Osrednja Slovenija – navodila za uporabo

Aplikacija prikazuje glasbene dogodke v Osrednji Sloveniji s portala [Kulturnik](https://dogodki.kulturnik.si): dogodke, ki so v teku, dogodke izbranega dne in napovedane dogodke za naslednja dva tedna. Žanre doda iz koledarja [SIGIC](https://www.sigic.si). Aplikacija teče na vašem računalniku in se odpre v brskalniku.

## Kaj potrebujete

- Mac ali računalnik z Windows 10 ali 11
- internetno povezavo
- .NET 10 SDK (brezplačen, namestitev je opisana spodaj)
- mapo `kulturnik-glasba` z datotekami `app.cs`, `open.sh`, `open.ps1`, `glasba.icns` in `glasba.ico`

## Namestitev na Mac

### 1. Namestite .NET 10 SDK

Odprite <https://dotnet.microsoft.com/download/dotnet/10.0> in v stolpcu SDK prenesite namestitveni program (Installer) za macOS:

- **Arm64** za Mac s čipom Apple (M1 ali novejši),
- **x64** za Mac s procesorjem Intel.

Zaženite preneseno datoteko `.pkg` in sledite navodilom. Nato odprite aplikacijo Terminal in vpišite:

```sh
dotnet --version
```

Izpisati se mora številka, ki se začne z `10.`.

### 2. Prekopirajte mapo z aplikacijo

Mapo `kulturnik-glasba` prekopirajte v svojo domačo mapo, tako da je njena pot `~/kulturnik-glasba`.

### 3. Preverite, ali aplikacija deluje

V Terminalu vpišite:

```sh
cd ~/kulturnik-glasba
dotnet run app.cs -- --check
```

Prvi zagon traja dlje, ker se aplikacija prevede. Na koncu se mora izpisati `ok`.

### 4. Ustvarite ikono Glasba

V Terminalu vpišite naslednje ukaze. Ustvarijo aplikacijo `Glasba.app` z ikono note in jo podpišejo za uporabo na vašem računalniku:

```sh
cd ~/kulturnik-glasba
chmod +x open.sh
mkdir -p ~/Applications
osacompile -o ~/Applications/Glasba.app -e "do shell script \"/bin/zsh $HOME/kulturnik-glasba/open.sh\""
cp glasba.icns ~/Applications/Glasba.app/Contents/Resources/applet.icns
/usr/libexec/PlistBuddy -c 'Delete :CFBundleIconName' ~/Applications/Glasba.app/Contents/Info.plist
codesign --force --deep --sign - ~/Applications/Glasba.app
```

Sporočilo `replacing existing signature` je pričakovano. Aplikacija Glasba je zdaj v mapi `Applications` v vaši domači mapi. Najdete jo tudi s Spotlightom (Cmd + preslednica, vpišite »Glasba«). Povlecite jo v Dock, da jo boste imeli vedno pri roki.

### 5. Požarni zid (samo če ga uporabljate)

Če uporabljate požarni zid za odhodne povezave, na primer LuLu ali Little Snitch, ob prvem nalaganju strani dovolite povezave programu `app`. Sicer stran izpiše »Kulturnik ni dosegljiv.«

## Namestitev na Windows

> Opomba: navodila za Windows še niso bila preizkušena na računalniku z Windows.

### 1. Namestite .NET 10 SDK

Odprite <https://dotnet.microsoft.com/download/dotnet/10.0> in v stolpcu SDK prenesite namestitveni program (Installer) za Windows: **x64** za večino računalnikov ali **Arm64** za računalnike s procesorjem ARM. Zaženite ga in sledite navodilom.

Namesto tega lahko v PowerShellu vpišete:

```powershell
winget install Microsoft.DotNet.SDK.10
```

Nato odprite **nov** PowerShell in preverite namestitev:

```powershell
dotnet --version
```

Izpisati se mora številka, ki se začne z `10.`.

### 2. Prekopirajte mapo z aplikacijo

Mapo `kulturnik-glasba` prekopirajte v svojo uporabniško mapo, tako da je njena pot `C:\Users\<vaše ime>\kulturnik-glasba`.

### 3. Preverite, ali aplikacija deluje

V PowerShellu vpišite:

```powershell
cd $env:USERPROFILE\kulturnik-glasba
dotnet run app.cs -- --check
```

Prvi zagon traja dlje, ker se aplikacija prevede. Na koncu se mora izpisati `ok`.

### 4. Ustvarite bližnjico Glasba na namizju

V PowerShellu vpišite:

```powershell
$folder = "$env:USERPROFILE\kulturnik-glasba"
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut("$([Environment]::GetFolderPath('Desktop'))\Glasba.lnk")
$shortcut.TargetPath = 'powershell.exe'
$shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$folder\open.ps1`""
$shortcut.WorkingDirectory = $folder
$shortcut.IconLocation = "$folder\glasba.ico"
$shortcut.WindowStyle = 7
$shortcut.Save()
```

Na namizju se pojavi bližnjica Glasba z ikono note. Če jo želite v opravilni vrstici, kliknite nanjo z desno tipko in izberite »Pripni v opravilno vrstico«.

## Uporaba

- Kliknite ikono Glasba. Odpre se brskalnik z naslovom <http://localhost:5080>. Prvo nalaganje traja približno 10 sekund, ker aplikacija prenese podatke. Nato je preklapljanje med izbirami takojšnje, podatki pa se osvežijo vsakih 10 minut.
- **Datum:** aplikacija se odpre na današnjem dnevu. Ko v polju z datumom izberete drug dan, stran takoj skoči nanj. Gumb **Danes** vas vrne na današnji dan.
- **Prizorišča:** Vsa prizorišča, Underground (Metelkova, Kino Šiška, Cvetličarna, Orto Bar in Klub K4), Metelkova, Kino Šiška, Cvetličarna.
- **Žanri:** prihajajo iz koledarja SIGIC. Dogodki brez žanra so vidni samo pod »Vsi žanri«. Rock, metal in elektronska glasba so vsi pod »popularna«. Pretekli dnevi žanrov nimajo.
- Klik na kartico odpre stran dogodka v novem zavihku. Nekaj dogodkov povezave nima.
- Izbrani datum in filtri so del naslova strani, zato lahko izbiro shranite med zaznamke.
- Aplikacija se sama ugasne po 30 minutah brez obiska. Naslednji klik na ikono jo znova zažene.

## Koledar Underground

Dogodke prizorišč Underground (od 7 dni nazaj do 28 dni naprej) lahko dobite v aplikacijo Koledar. Koledar se osveži vsako uro na GitHubu, zato vaš računalnik pri tem ni potreben.

1. V aplikaciji Koledar na Macu izberite **Datoteka → Nova naročnina na koledar** (v angleščini **File → New Calendar Subscription**).
2. Vpišite naslov `https://tr0nky.github.io/kulturnik-glasba/underground.ics` in kliknite **Naroči se**.
3. Pri **Lokacija** izberite **iCloud**, pri **Samodejno osveževanje** pa **Vsako uro**. Koledar se tako prikaže tudi na iPhonu in iPadu.

Na isti naslov se lahko naroči kdorkoli. Če na GitHubu 60 dni ni nobene spremembe, GitHub urno osveževanje ustavi in pošlje e-pošto z gumbom za ponovni vklop.

## Težave

- **»Kulturnik ni dosegljiv.«** Osvežite stran, ker Kulturnik včasih za kratek čas ne odgovarja. Če se napaka ponavlja, preverite internetno povezavo in požarni zid.
- **Stran se ne odpre.** Zaženite aplikacijo ročno, da vidite napako:
  - Mac: `cd ~/kulturnik-glasba` in nato `dotnet run app.cs`
  - Windows: `cd $env:USERPROFILE\kulturnik-glasba` in nato `dotnet run app.cs`

  Pogost vzrok je, da vrata 5080 že uporablja drug program. Na Macu je izpis zadnjega zagona tudi v datoteki `~/kulturnik-glasba/server.log`.
- **Ročna ustavitev aplikacije:**
  - Mac: v Terminalu vpišite `lsof -ti tcp:5080 -sTCP:LISTEN | xargs kill`
  - Windows: v upravitelju opravil končajte proces `app`.

## Posodobitev

Zamenjajte datoteko `app.cs` z novo različico. Ob naslednjem zagonu se aplikacija sama ponovno prevede. Če aplikacija takrat teče, jo najprej ustavite (glejte Težave) ali počakajte 30 minut, da se ugasne sama.

## Odstranitev

- Mac: izbrišite mapo `~/kulturnik-glasba` in aplikacijo `~/Applications/Glasba.app`.
- Windows: izbrišite mapo `kulturnik-glasba` in bližnjico Glasba na namizju.

.NET SDK lahko odstranite kot vsak drug program.
