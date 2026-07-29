# État « hors service » : veille, arrêt, redémarrage, déconnexion

Date : 2026-07-29

## Problème

Luxafor On Air sait déjà réagir au verrouillage de session (couleur dédiée, activable). Il ne sait
pas réagir de façon configurable quand la machine quitte le service.

État actuel du code :

- **La veille est gérée, mais en dur.** `PowerEvent_Arrive` (`LuxOnAir/MainWindow.xaml.cs:278-305`)
  écoute `Win32_PowerManagementEvent` via un `ManagementEventWatcher` : type `4` (mise en veille)
  appelle `SetLightsOff()`, type `7` (reprise) appelle `CheckMicUsage()`. Aucune option, aucun choix
  de couleur.
- **L'arrêt, le redémarrage et la déconnexion ne sont pas gérés du tout.**
- **Un bug empêche toute action propre à l'arrêt.** `Window_Closing`
  (`LuxOnAir/MainWindow.xaml.cs:456-466`) pose `e.Cancel = true` tant que `ReallyExit` est `false`.
  Pendant un arrêt de Windows, l'application refuse donc de se fermer, le système finit par la tuer,
  et `Window_Closed` — donc `ShutdownHardware()` — n'est jamais exécuté. Sur les montages où le port
  USB reste alimenté PC éteint, et sur les modèles Bluetooth qui ont leur propre batterie, la lumière
  reste allumée indéfiniment.

Par ailleurs, `Win32_PowerManagementEvent` type `4` est peu fiable en veille moderne (Modern Standby,
S0), le mode par défaut de la plupart des portables Windows 11. La détection actuelle peut donc déjà
ne rien déclencher selon la machine.

## Objectif

Un unique état **« hors service »** couvrant la mise en veille, l'arrêt, le redémarrage et la
déconnexion, configurable comme le verrouillage : un choix Oui/Non et une couleur.

- **Non** (défaut) : la lumière s'éteint. C'est le comportement actuel à la veille, désormais étendu
  à l'arrêt.
- **Oui** : la lumière passe à la couleur choisie.

Veille et arrêt ne sont volontairement **pas** distingués : un seul réglage pour les deux.

## Périmètre

Fichiers modifiés :

- `LuxOnAir/StatusColors.cs`
- `LuxOnAir/LightSettings.cs`
- `LuxOnAir/LFRSettings.cs`
- `LuxOnAir/MainWindow.xaml`
- `LuxOnAir/MainWindow.xaml.cs`

Hors périmètre : `MicrophoneHelper.cs`, `MessageHelper.cs`, `SettingsHelper.cs`, `ColorHelper.cs`,
la logique de détection du micro, le mécanisme mono-instance.

## Conception

### 1. Réglages — `StatusColors.cs`

Deux champs publics, en miroir exact de `ChangeOnLock` / `SessionLocked` :

```csharp
/// <summary>
/// Whether a different color should be set when the system goes out of service
/// (sleep, shutdown, restart, logoff)
/// </summary>
public bool ChangeOnOutOfService;

/// <summary>
/// Color to use when the system is going out of service
/// </summary>
public int OutOfService;
```

Valeurs par défaut dans le constructeur :

```csharp
ChangeOnOutOfService = false;
OutOfService = System.Drawing.Color.Blue.ToArgb();
```

`ChangeOnOutOfService` vaut `false` par défaut pour que les installations existantes conservent
exactement leur comportement actuel après mise à jour.

**Rétrocompatibilité des réglages enregistrés.** `LightSettings` porte
`[SettingsSerializeAs(SettingsSerializeAs.Xml)]`. `XmlSerializer` construit l'objet par son
constructeur sans paramètre puis n'écrase que les éléments présents dans le XML. Un fichier de
réglages écrit par une version antérieure ne contient pas ces deux éléments : ils gardent donc les
valeurs par défaut ci-dessus. Aucune migration n'est nécessaire.

### 2. Couche matérielle — `LightSettings.cs` et `LFRSettings.cs`

Nouvelle méthode abstraite dans `LightSettings` :

```csharp
/// <summary>
/// Set RGB lights to out-of-service status
/// </summary>
public abstract void SetOutOfService();
```

Implémentation dans `LFRSettings`, calquée sur `SetLocked()` (`LFRSettings.cs:193-198`) :

```csharp
public override void SetOutOfService()
{
    StopBlink();
    currentColor = System.Drawing.Color.FromArgb(Colors.OutOfService);
    SetAllLights(currentColor);
}
```

**Changement de signature de `ShutdownHardware`.** La méthode éteint aujourd'hui inconditionnellement
les lumières avant de libérer les périphériques (`LFRSettings.cs:78-89`). Appelée depuis
`Window_Closed` pendant un arrêt système, elle effacerait la couleur « hors service » tout juste
appliquée. Elle devient :

```csharp
public abstract void ShutdownHardware(bool turnLightsOff = true);
```

Dans `LFRSettings`, la boucle n'émet le `SetColor(..., 0, 0, 0)` que si `turnLightsOff` est vrai ;
le `device.Dispose()` a lieu dans tous les cas. Le paramètre par défaut préserve tous les appels
existants.

La valeur par défaut doit être **redéclarée à l'identique dans l'override** :

```csharp
public override void ShutdownHardware(bool turnLightsOff = true)
```

En C#, la valeur par défaut est résolue à la compilation d'après le type statique de l'expression
appelée. Or `Settings.Default.Lights` est typé `LFRSettings`, le type concret
(`Properties/Settings.Designer.cs:28`), et non `LightSettings` : sans redéclaration, l'appel sans
argument `Settings.Default.Lights.ShutdownHardware()` ne compilerait pas.

### 3. Détection des événements — `MainWindow.xaml.cs`

Le `ManagementEventWatcher` dédié à l'alimentation est **supprimé** au profit de `SystemEvents`,
déjà utilisé par le projet pour le verrouillage (`MainWindow.xaml.cs:80`). Motifs : `SystemEvents`
s'appuie sur les messages Windows natifs et reste fiable en Modern Standby ; `SessionEnding` se
déclenche sur `WM_QUERYENDSESSION`, donc avant que le système commence à terminer les processus,
ce qui laisse le temps d'émettre la commande USB ; et le projet passe de trois watchers WMI à deux.

À supprimer :

- le champ statique `powerWatcher` et son bloc d'initialisation (`MainWindow.xaml.cs:115-120`) ;
- la méthode `PowerEvent_Arrive` (`MainWindow.xaml.cs:278-305`) ;
- son `Stop()` / `Dispose()` dans `Window_Closed` (`MainWindow.xaml.cs:483-484`).

À ajouter, sur le modèle de `SessionSwitchHandler` — champs statiques pour les handlers, abonnement
dans le constructeur, désabonnement dans `Window_Closed` :

```csharp
private static PowerModeChangedEventHandler PowerModeHandler;
private static SessionEndingEventHandler SessionEndingHandler;
```

Correspondance événement → action :

| Événement | Action |
| --- | --- |
| `PowerModeChanged` avec `PowerModes.Suspend` | `GoOutOfService()` |
| `PowerModeChanged` avec `PowerModes.Resume` | `CheckMicUsage()` |
| `PowerModeChanged` avec `PowerModes.StatusChange` | ignoré |
| `SessionEnding` (`Logoff` ou `SystemShutdown`) | `GoOutOfService()` puis `bSystemShutdown = true` |

`SessionEnding` n'est jamais annulé : `SessionEndingEventArgs.Cancel` reste à `false`, l'application
ne doit pas retarder l'arrêt de Windows. Les deux valeurs de `SessionEndReasons` sont traitées de
façon identique, conformément au choix d'un état unique.

Comme pour `PowerEvent_Arrive` aujourd'hui, les handlers appellent la couche lumière via
`Dispatcher.Invoke(...)` : ils sont déclenchés hors du thread UI.

### 4. Décision couleur ou extinction — `MainWindow.xaml.cs`

La décision reste dans `MainWindow`, là où vit déjà celle du verrouillage
(`OnSessionSwitch`, `MainWindow.xaml.cs:310-330`), et non dans la couche matérielle :

```csharp
/// <summary>
/// React to the system going out of service (sleep, shutdown, restart, logoff)
/// </summary>
private void GoOutOfService()
{
    if (Settings.Default.Lights.Colors.ChangeOnOutOfService)
    {
        Settings.Default.Lights.SetOutOfService();
    }
    else
    {
        Settings.Default.Lights.SetLightsOff();
    }
}
```

`SetLightsOff()` existe déjà (`LightSettings.cs:33`, `LFRSettings.cs:203-207`) et est réutilisé tel
quel.

### 5. Fermeture pendant un arrêt système — `MainWindow.xaml.cs`

Nouveau champ d'instance :

```csharp
/// <summary>
/// Indicates the session is ending because Windows is shutting down or logging off.
/// </summary>
private bool bSystemShutdown = false;
```

`Window_Closing` cesse d'annuler la fermeture dans ce cas :

```csharp
if (!ReallyExit && !bSystemShutdown)
{
    Hide();
    e.Cancel = true;
}
```

`Window_Closed` ne doit pas éteindre la lumière si l'on vient d'appliquer une couleur « hors
service » :

```csharp
Settings.Default.Lights.ShutdownHardware(
    !(bSystemShutdown && Settings.Default.Lights.Colors.ChangeOnOutOfService));
```

Propriété de robustesse : la couleur est posée dès `SessionEnding`, en amont de toute fermeture de
fenêtre. Même si Windows tue le processus avant que `Window_Closed` s'exécute, l'état lumineux
correct a déjà été émis.

Quand `ChangeOnOutOfService` vaut `false`, `GoOutOfService()` éteint puis `ShutdownHardware(true)`
éteint de nouveau. Cette redondance est sans effet visible et n'est pas optimisée : elle garantit
l'extinction même si l'un des deux chemins est court-circuité.

### 6. Interface — `MainWindow.xaml`

Sous le bloc « Change Color When Console Locked » de l'onglet General, un bloc de structure
identique :

- `Label` en gras : « Change Color When System Sleeps or Shuts Down »
- `RadioButton` `radioOutOfServiceNo` / `radioOutOfServiceYes`, `GroupName="groupOutOfService"`
- `Label` « Color: » nommé `labelOutOfServiceColor`
- `Button` `btnOutOfService`, infobulle : « Click to set the color displayed when the system goes to
  sleep, shuts down, or you log off »

Le bloc lock occupe les ordonnées 78 à 107 ; le nouveau bloc se place à partir de 130, ce qui impose
de porter `Height` et `MinHeight` de la fenêtre de 285 à 337 (`MainWindow.xaml:7`).

Onglet Debug : ajout d'un bouton `btnTestOutOfService` intitulé « Sleep/Off », sans quoi tester cette
fonctionnalité obligerait à éteindre le PC à chaque essai. Il s'insère dans la rangée existante,
entre « Locked » et « Reset ». Les marges droites (`MainWindow.xaml:46-50`) deviennent :

| Élément | Marge droite avant | après |
| --- | --- | --- |
| `label2` « Color Tests: » | 330 | 410 |
| `btnTestInUse` | 250 | 330 |
| `btnTestNotInUse` | 170 | 250 |
| `btnTestLocked` | 90 | 170 |
| `btnTestOutOfService` | — | 90 |
| `btnTestReset` | 10 | 10 |

Gestionnaires à ajouter dans `MainWindow.xaml.cs`, copies conformes de leurs équivalents lock :

| Nouveau gestionnaire | Modèle |
| --- | --- |
| `RadioOutOfService_Checked` | `RadioLocked_Checked` (`:629-634`) |
| `BtnOutOfService_Click` | `BtnLocked_Click` (`:524-539`) |
| `BtnTestOutOfService_Click` | `BtnTestLocked_Click` (`:612-616`), appelant `GoOutOfService()` |

`LoadSettings()` (`:140-157`) reflète les deux nouveaux réglages dans l'UI, comme il le fait pour
`ChangeOnLock` et `SessionLocked`.

### 7. Priorité des états

`hors service` > `verrouillé` > `micro en cours` > `micro libre`.

Aucun code supplémentaire n'est requis pour la reprise de veille : `PowerModes.Resume` appelle
`CheckMicUsage()`, qui consulte déjà `bConsoleLocked` (`MainWindow.xaml.cs:388-391`). Au réveil, la
session étant normalement verrouillée, la couleur « verrouillé » est appliquée — comportement
attendu.

## Vérification

Le projet ne contient aucun projet de test et le code visé est étroitement couplé à l'interface WPF
et au matériel HID. La vérification est manuelle et assumée comme telle.

1. **Réglages par défaut préservés** — lancer avec un fichier de réglages existant : le nouveau bloc
   affiche « No », et la mise en veille éteint la lumière comme avant.
2. **Bouton de test** — onglet Debug, « Sleep/Off » : avec « No » la lumière s'éteint, avec « Yes »
   elle prend la couleur choisie.
3. **Persistance** — choisir « Yes » et une couleur, fermer par « Done », rouvrir les réglages :
   les valeurs sont conservées.
4. **Veille réelle** — mettre le PC en veille, observer la lumière ; réveiller, vérifier le retour à
   la couleur « verrouillé » puis, après déverrouillage, à la couleur micro.
5. **Verrouillage inchangé** — Win+L applique toujours la couleur « verrouillé ».
6. **Arrêt** — arrêter Windows et observer la lumière avant coupure de l'alimentation USB (test
   concluant surtout sur port USB toujours alimenté ou périphérique Bluetooth sur batterie).
7. **L'arrêt n'est plus retardé** — l'application ne doit plus apparaître dans l'écran « Ces
   applications vous empêchent de fermer la session » ni provoquer d'attente à l'arrêt. Vérification
   complémentaire possible dans l'Observateur d'événements, journal
   `Applications and Services Logs > Microsoft > Windows > Diagnostics-Performance > Operational`,
   qui trace les processus ayant ralenti l'arrêt.
8. **Déconnexion** — se déconnecter : même comportement qu'à l'arrêt.
9. **Non-régression** — quitter par le bouton « Exit » : la lumière s'éteint, comme aujourd'hui.

## Choix écartés

- **Distinguer veille et arrêt** avec deux réglages séparés : rejeté au profit d'un état unique, plus
  simple à configurer.
- **Étendre le watcher WMI** (`Win32_PowerManagementEvent`) : peu fiable en Modern Standby et
  incapable de détecter l'arrêt de façon exploitable.
- **Traiter `WM_POWERBROADCAST` et `WM_QUERYENDSESSION`** directement dans le `WndProc` existant
  (`MainWindow.xaml.cs:187-196`) : plus précoce et plus fiable, mais impose du P/Invoke et des
  constantes Win32 à maintenir pour un gain marginal sur `SystemEvents`, qui repose sur ces mêmes
  messages.
- **Doubler `SystemEvents` d'un garde-fou WMI** : imposerait un anti-rebond pour éviter la double
  émission, sans bénéfice démontré.
- **Introduire un projet de tests** et extraire la logique de décision dans une classe sans UI :
  écarté pour cette itération, le projet n'ayant jamais eu de tests automatisés.
