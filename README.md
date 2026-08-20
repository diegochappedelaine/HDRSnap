# HDRSnap

Petit outil de capture d'écran maison pour Windows, pensé pour remplacer ShareX sur les affichages HDR — ShareX rate le tone-mapping et brûle les hautes lumières.

## Ce que ça fait

- Raccourci global (`F11` par défaut) → capture le moniteur sous le curseur via Windows Graphics Capture.
- Sur un écran HDR : capture en FP16 scRGB, tone-mapping GPU via l'effet Direct2D `HDR Tone Map` (celui utilisé par Photos / Outil Capture d'écran de Windows). La cible est calculée à partir du MaxCLL mesuré sur l'image (percentile 99,99) et du blanc SDR réel du moniteur, avec un fallback CPU (Reinhard étendu) si le chemin GPU échoue.
- Sortie en PNG SDR, un sous-dossier par application au premier plan (`Pictures\HDRSnap\<NomDuProcess>\yyyy-MM-dd HH-mm-ss.png`).
- Tourne dans le system tray, une seule instance à la fois (mutex).

## Build

Nécessite le SDK .NET 9 (Windows).

```powershell
dotnet build -c Release
```

Pour un exécutable portable autonome (un seul .exe, sans dépendance au runtime installé) :

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish
```

## Installation

1. Copier `publish\HDRSnap.exe` où tu veux (pas besoin d'installeur).
2. Le lancer une fois pour générer la config par défaut dans `%APPDATA%\HDRSnap\config.json`.
3. Pour un lancement automatique au démarrage : déposer un raccourci vers l'exe dans `shell:startup`.

### Config (`%APPDATA%\HDRSnap\config.json`)

| Clé | Défaut | Description |
|---|---|---|
| `Hotkey` | `F11` | Raccourci global (ex: `Ctrl+Shift+F11`) |
| `OutputDir` | `Pictures\HDRSnap` | Dossier de sortie |
| `CaptureCursor` | `false` | Inclure le curseur dans la capture |
| `PlaySound` | `true` | Son de confirmation |
| `SubfolderPerApp` | `true` | Sous-dossier nommé d'après le process au premier plan |
| `TonemapHeadroom` | `1.25` | Marge au-dessus du blanc SDR allouée aux hautes lumières (× blanc SDR, en nits) |

Log : `%APPDATA%\HDRSnap\hdrsnap.log`.

Test en ligne de commande : `HDRSnap.exe --shoot` (ajouter `--forcetonemap` pour forcer le chemin de tone-mapping D2D même sur une frame qui reste dans la plage SDR — utile pour débugger).

## Notes techniques / pièges connus

- L'API `DisplayConfig GET_SDR_WHITE_LEVEL` échoue (erreur 87) sur certaines builds Windows 11 (observé sur build 26200). Fallback sur le registre : `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore\<id>\SDRWhiteLevel` (valeur en unités de 1/1000, ex: 6000 = 480 nits), puis sur 200 nits par défaut si tout échoue.
- Un mauvais blanc SDR détecté (fallback silencieux à 200 nits au lieu de la vraie valeur du moniteur) était la cause principale de hautes lumières brûlées dans une version antérieure — la chaîne API → registre → défaut plus le calcul MaxCLL au percentile 99,99 (plutôt que le max absolu, sensible à un pixel parasite) corrige ça.
