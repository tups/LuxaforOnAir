# Plan d'implémentation — État « hors service »

> **Pour les agents :** SOUS-COMPÉTENCE REQUISE : utiliser `superpowers:subagent-driven-development`
> (recommandé) ou `superpowers:executing-plans` pour dérouler ce plan tâche par tâche. Les étapes
> utilisent la syntaxe case à cocher (`- [ ]`).

**Spec de référence :** `docs/superpowers/specs/2026-07-29-etat-hors-service-design.md`

**Objectif :** ajouter un état « hors service » configurable (couleur ou extinction) déclenché par la
mise en veille, l'arrêt, le redémarrage et la déconnexion de Windows.

**Architecture :** deux nouveaux champs de réglages en miroir du bloc « lock » existant, une méthode
`SetOutOfService()` dans la couche lumière, et le remplacement du `ManagementEventWatcher` WMI
d'alimentation par `SystemEvents.PowerModeChanged` + `SystemEvents.SessionEnding`. La décision
« couleur ou extinction » vit dans `MainWindow`, là où vit déjà celle du verrouillage.

**Pile technique :** C# / WPF / .NET Framework 4.7.2, `Microsoft.Win32.SystemEvents`,
LuxaforSharp 2.0.0.0, HidLibrary 3.3.40. Compilation par MSBuild de Visual Studio 2022.

## Contraintes globales

- **Langage : C# 7.3.** Le projet est un csproj ancien format ciblant `net472` sans `<LangVersion>` ;
  Roslyn retombe donc sur C# 7.3. Pas de types référence nullables, pas de `switch` expression, pas
  d'opérateur `??=`, pas de `using` déclaratif.
- **Commentaires, identifiants et libellés d'interface en anglais**, comme tout le reste du projet.
  Les commentaires publics utilisent le format `/// <summary>`.
- **Aucun projet de test dans la solution.** La vérification est manuelle et assumée : chaque tâche
  se termine par une compilation sans erreur et une observation concrète décrite explicitement. Ne
  pas introduire de framework de test — c'est un choix acté au design.
- **Comportement par défaut inchangé.** `ChangeOnOutOfService` vaut `false` par défaut : après mise à
  jour, une installation existante doit se comporter exactement comme avant.
- **Ne pas toucher** à `MicrophoneHelper.cs`, `MessageHelper.cs`, `SettingsHelper.cs`,
  `ColorHelper.cs`, ni au mécanisme mono-instance.
- **Commande de compilation** (identique pour toutes les tâches) :

  ```powershell
  & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    "C:\Users\MSI\source\repos\LuxaforOnAir\LuxOnAir.sln" `
    /t:Build /p:Configuration=Debug /v:minimal /nologo
  ```

- **Avant tout lancement de l'application**, tuer les instances résiduelles, sinon le mutex
  mono-istance fait sortir la nouvelle instance immédiatement avec le code 0 :

  ```powershell
  Get-Process -Name LuxOnAir -ErrorAction SilentlyContinue | Stop-Process -Force
  ```

- **Le dépôt a déjà des modifications non commitées** (`LuxOnAir/LuxOnAir.csproj` et
  `LuxOnAir/Properties/app.manifest`) issues d'une session de remise en état antérieure. Ne les
  inclure dans aucun commit de ce plan : chaque `git add` ci-dessous nomme explicitement ses fichiers.

## Structure des fichiers

| Fichier | Responsabilité | Tâches |
| --- | --- | --- |
| `LuxOnAir/StatusColors.cs` | Réglages sérialisés : quels états, quelles couleurs | 1 |
| `LuxOnAir/LightSettings.cs` | Contrat abstrait de la couche lumière | 1 |
| `LuxOnAir/LFRSettings.cs` | Implémentation Luxafor du contrat | 1 |
| `LuxOnAir/MainWindow.xaml` | Interface : bloc de configuration + bouton de test | 2, 3 |
| `LuxOnAir/MainWindow.xaml.cs` | Détection des événements système et décision d'état | 2, 3, 4, 5 |

L'ordre des tâches est choisi pour que la capacité de test arrive au plus tôt : la tâche 3 fournit un
bouton qui déclenche l'état hors service à la demande, ce qui évite d'éteindre le PC pour valider les
tâches suivantes.

---

## Tâche 1 : Réglages persistants et couche lumière

**Fichiers :**
- Modifier : `LuxOnAir/StatusColors.cs`
- Modifier : `LuxOnAir/LightSettings.cs:22` et ajout
- Modifier : `LuxOnAir/LFRSettings.cs:75-89` et ajout

**Interfaces :**
- Consomme : rien.
- Produit : `StatusColors.ChangeOnOutOfService` (`bool`), `StatusColors.OutOfService` (`int`, ARGB),
  `LightSettings.SetOutOfService()` (`void`), et la nouvelle signature
  `LightSettings.ShutdownHardware(bool turnLightsOff = true)`.

- [ ] **Étape 1 : ajouter les deux champs de réglages**

Dans `LuxOnAir/StatusColors.cs`, après le champ `WaveMicInUse` et avant le constructeur :

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

- [ ] **Étape 2 : donner leurs valeurs par défaut**

Toujours dans `LuxOnAir/StatusColors.cs`, à la fin du constructeur `StatusColors()`, après
`WaveMicInUse = false;` :

```csharp
            ChangeOnOutOfService = false;
            OutOfService = System.Drawing.Color.Blue.ToArgb();
```

`false` est délibéré : il préserve le comportement actuel pour les réglages déjà enregistrés.

- [ ] **Étape 3 : déclarer les nouveaux membres abstraits**

Dans `LuxOnAir/LightSettings.cs`, remplacer la déclaration de `ShutdownHardware` (lignes 19-22) par :

```csharp
        /// <summary>
        /// Shutdown RGB lighting
        /// </summary>
        /// <param name="turnLightsOff">Whether to turn the lights off before releasing the devices.
        /// Pass false to leave the current color displayed, e.g. when Windows is shutting down and an
        /// out-of-service color has just been set.</param>
        public abstract void ShutdownHardware(bool turnLightsOff = true);
```

Puis, juste après la déclaration de `SetLocked()` (ligne 38), ajouter :

```csharp
        /// <summary>
        /// Set RGB lights to out-of-service status
        /// </summary>
        public abstract void SetOutOfService();
```

- [ ] **Étape 4 : adapter `ShutdownHardware` dans l'implémentation Luxafor**

Dans `LuxOnAir/LFRSettings.cs`, remplacer intégralement la méthode `ShutdownHardware` (lignes 75-89)
par :

```csharp
        /// <summary>
        /// Shutdown Luxafor lighting
        /// </summary>
        /// <param name="turnLightsOff">Whether to turn the lights off before releasing the devices.
        /// Pass false to leave the current color displayed, e.g. when Windows is shutting down and an
        /// out-of-service color has just been set.</param>
        public override void ShutdownHardware(bool turnLightsOff = true)
        {
            if (Available())
            {
                foreach (IDevice device in devices)
                {
                    // Turn off the lights before shutting down, unless asked to leave them as-is
                    if (turnLightsOff)
                    {
                        device.SetColor(LedTarget.All, new LuxaforSharp.Color(0, 0, 0), null);
                    }
                    device.Dispose();
                }
            }
        }
```

La valeur par défaut `= true` doit être **répétée ici**. En C# elle est résolue à la compilation
d'après le type statique de l'expression appelée, et `Settings.Default.Lights` est typé `LFRSettings`
(`Properties/Settings.Designer.cs:28`), pas `LightSettings` : sans cette répétition, les appels
existants sans argument ne compilent plus.

- [ ] **Étape 5 : implémenter `SetOutOfService`**

Dans `LuxOnAir/LFRSettings.cs`, entre `SetLocked()` et `SetLightsOff()` (soit après la ligne 198) :

```csharp
        /// <summary>
        /// Set Luxafor lights to out-of-service status
        /// </summary>
        public override void SetOutOfService()
        {
            StopBlink();
            currentColor = System.Drawing.Color.FromArgb(Colors.OutOfService);
            SetAllLights(currentColor);
        }
```

- [ ] **Étape 6 : compiler**

Lancer la commande de compilation des contraintes globales.
Attendu : `0 Erreur(s)`. Si le compilateur signale que `LFRSettings` n'implémente pas
`SetOutOfService`, c'est que l'étape 5 a été omise.

- [ ] **Étape 7 : vérifier la non-régression**

```powershell
Get-Process -Name LuxOnAir -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process "C:\Users\MSI\source\repos\LuxaforOnAir\LuxOnAir\bin\Debug\LuxOnAir.exe"
```

Attendu : l'application démarre et reste ouverte, la lumière prend la couleur « micro non utilisé »
comme avant. Aucun changement visible — c'est le but à ce stade. Quitter par le bouton « Exit » et
vérifier que la lumière s'éteint.

- [ ] **Étape 8 : commit**

```bash
git add LuxOnAir/StatusColors.cs LuxOnAir/LightSettings.cs LuxOnAir/LFRSettings.cs
git commit -m "Add out-of-service settings and light state"
```

---

## Tâche 2 : Interface de configuration (onglet General)

**Fichiers :**
- Modifier : `LuxOnAir/MainWindow.xaml:7` et `:23-26`
- Modifier : `LuxOnAir/MainWindow.xaml.cs:140-157` et ajout

**Interfaces :**
- Consomme : `StatusColors.ChangeOnOutOfService`, `StatusColors.OutOfService` (tâche 1).
- Produit : les contrôles nommés `radioOutOfServiceNo`, `radioOutOfServiceYes`, `btnOutOfService`,
  `labelOutOfServiceColor`, et les gestionnaires `RadioOutOfService_Checked(object, RoutedEventArgs)`
  et `BtnOutOfService_Click(object, RoutedEventArgs)`.

- [ ] **Étape 1 : agrandir la fenêtre**

Dans `LuxOnAir/MainWindow.xaml`, ligne 7, remplacer `Height="285"` par `Height="337"` et
`MinHeight="285"` par `MinHeight="337"`. Le nouveau bloc occupe 52 pixels sous le bloc lock.

- [ ] **Étape 2 : ajouter le bloc de configuration**

Dans `LuxOnAir/MainWindow.xaml`, juste après la ligne du `RadioButton` `radioLockedYes` (ligne 26) et
avant la balise `</Grid>` de l'onglet General :

```xml
                    <Label x:Name="labelOutOfService" Content="Change Color When System Sleeps or Shuts Down" HorizontalAlignment="Left" Margin="10,130,0,0" VerticalAlignment="Top" FontWeight="Bold"/>
                    <Label x:Name="labelOutOfServiceColor" Content="Color:" HorizontalAlignment="Left" Margin="157,153,0,0" VerticalAlignment="Top"/>
                    <RadioButton x:Name="radioOutOfServiceNo" Content="No" HorizontalAlignment="Left" Height="15" Margin="13,159,0,0" VerticalAlignment="Top" Width="48" GroupName="groupOutOfService" Checked="RadioOutOfService_Checked"/>
                    <RadioButton x:Name="radioOutOfServiceYes" Content="Yes" HorizontalAlignment="Left" Height="15" Margin="64,159,0,0" VerticalAlignment="Top" Width="48" GroupName="groupOutOfService" Checked="RadioOutOfService_Checked"/>
                    <Button x:Name="btnOutOfService" Content="" HorizontalAlignment="Left" Margin="199,156,0,0" VerticalAlignment="Top" Width="75" Click="BtnOutOfService_Click" ToolTip="Click to set the color displayed when the system goes to sleep, shuts down, or you log off"/>
```

Les ordonnées reprennent celles du bloc lock (78 / 101 / 104 / 107) décalées de 52.

- [ ] **Étape 3 : refléter les réglages au chargement**

Dans `LuxOnAir/MainWindow.xaml.cs`, méthode `LoadSettings()`, après la ligne
`btnLocked.Background = Settings.Default.Lights.Colors.SessionLocked.ToBrush();` :

```csharp
            radioOutOfServiceYes.IsChecked = Settings.Default.Lights.Colors.ChangeOnOutOfService;
            radioOutOfServiceNo.IsChecked = !Settings.Default.Lights.Colors.ChangeOnOutOfService;
            btnOutOfService.Background = Settings.Default.Lights.Colors.OutOfService.ToBrush();
```

Les deux radios sont positionnées explicitement, contrairement au bloc lock qui ne coche que « Yes »
(`MainWindow.xaml.cs:152`). Sans cela, quand `ChangeOnOutOfService` vaut `false`, aucun des deux
boutons n'apparaîtrait sélectionné au démarrage — défaut cosmétique que le bloc lock présente
aujourd'hui et que l'on ne reproduit pas ici.

L'ordre des deux affectations importe : `radioOutOfServiceYes` d'abord, `radioOutOfServiceNo`
ensuite. Chacune déclenche `RadioOutOfService_Checked` lorsqu'elle passe à `true`, et ce handler lit
`radioOutOfServiceYes.IsChecked`. Dans cet ordre, cette propriété a toujours déjà sa valeur
définitive quand le handler s'exécute. C'est aussi ce qui rend le déclenchement anticipé inoffensif :
`ApplySettings()` appelle `CheckMicUsage()` avant `InitHardware()`, mais `Available()` renvoie alors
`false` et la couche lumière ne fait rien — exactement ce qui se produit déjà avec le bloc lock.

- [ ] **Étape 4 : ajouter le gestionnaire du choix Oui/Non**

Dans `LuxOnAir/MainWindow.xaml.cs`, après la méthode `RadioLocked_Checked` (fin de fichier, avant
l'accolade fermante de la classe) :

```csharp
        private void RadioOutOfService_Checked(object sender, RoutedEventArgs e)
        {
            // Set the UI and saved settings to match whether this option is enabled or not
            labelOutOfServiceColor.IsEnabled = btnOutOfService.IsEnabled = Settings.Default.Lights.Colors.ChangeOnOutOfService = (bool)radioOutOfServiceYes.IsChecked;
            ApplySettings();
        }
```

- [ ] **Étape 5 : ajouter le sélecteur de couleur**

Dans `LuxOnAir/MainWindow.xaml.cs`, après la méthode `BtnLocked_Click` :

```csharp
        private void BtnOutOfService_Click(object sender, RoutedEventArgs e)
        {
            // Show a color dialog with the current color for the user to change
            ColorDialog colorDialog = new ColorDialog()
            {
                Color = System.Drawing.Color.FromArgb(Settings.Default.Lights.Colors.OutOfService)
            };

            // If the user did not cancel, set the picked color as the new out-of-service indicator color
            if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Settings.Default.Lights.Colors.OutOfService = colorDialog.Color.ToArgb();
                btnOutOfService.Background = Settings.Default.Lights.Colors.OutOfService.ToBrush();
                ApplySettings();
            }
        }
```

- [ ] **Étape 6 : compiler**

Lancer la commande de compilation. Attendu : `0 Erreur(s)`.

Piège connu : `RadioOutOfService_Checked` s'exécute pendant `InitializeComponent()` si le XAML
cochait un bouton par défaut. Ici aucun `IsChecked` n'est posé dans le XAML, donc l'événement ne se
déclenche qu'au premier appel de `LoadSettings()`, après `InitializeComponent()`. Si une
`NullReferenceException` survient au démarrage, c'est qu'un `IsChecked` a été ajouté par erreur dans
le XAML.

- [ ] **Étape 7 : vérifier l'interface**

```powershell
Get-Process -Name LuxOnAir -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process "C:\Users\MSI\source\repos\LuxaforOnAir\LuxOnAir\bin\Debug\LuxOnAir.exe"
```

Attendu :
1. L'onglet General affiche le nouveau bloc sous « Change Color When Console Locked », sans
   chevauchement ni troncature.
2. « No » est sélectionné, et le libellé « Color: » ainsi que le bouton de couleur sont grisés.
3. Cliquer « Yes » : le bouton de couleur devient actif et affiche du bleu.
4. Cliquer le bouton de couleur, choisir du violet, valider : le bouton devient violet.
5. Cliquer « Done », rouvrir les réglages par double-clic sur l'icône de notification : « Yes » est
   toujours sélectionné et la couleur est toujours violette.
6. Quitter par « Exit », relancer : les réglages sont conservés.

- [ ] **Étape 8 : commit**

```bash
git add LuxOnAir/MainWindow.xaml LuxOnAir/MainWindow.xaml.cs
git commit -m "Add out-of-service settings UI"
```

---

## Tâche 3 : Décision d'état et bouton de test

**Fichiers :**
- Modifier : `LuxOnAir/MainWindow.xaml:46-50`
- Modifier : `LuxOnAir/MainWindow.xaml.cs` (ajouts)

**Interfaces :**
- Consomme : `LightSettings.SetOutOfService()`, `LightSettings.SetLightsOff()`,
  `StatusColors.ChangeOnOutOfService` (tâche 1).
- Produit : `MainWindow.GoOutOfService()` (`private void`), point d'entrée unique appelé par les
  tâches 4 et 5.

- [ ] **Étape 1 : ajouter la méthode de décision**

Dans `LuxOnAir/MainWindow.xaml.cs`, juste après la méthode `CheckMicUsage()` :

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

- [ ] **Étape 2 : décaler la rangée de boutons de test**

Dans `LuxOnAir/MainWindow.xaml`, onglet Debug, modifier les marges droites des lignes 46 à 49 :

| Élément | `Margin` actuelle | `Margin` cible |
| --- | --- | --- |
| `label2` | `0,0,330,7` | `0,0,410,7` |
| `btnTestInUse` | `0,0,250,10` | `0,0,330,10` |
| `btnTestNotInUse` | `0,0,170,10` | `0,0,250,10` |
| `btnTestLocked` | `0,0,90,10` | `0,0,170,10` |

`btnTestReset` (ligne 50, marge `0,0,10,10`) ne bouge pas.

- [ ] **Étape 3 : ajouter le bouton de test**

Dans `LuxOnAir/MainWindow.xaml`, entre `btnTestLocked` et `btnTestReset` :

```xml
                    <Button x:Name="btnTestOutOfService" Content="Sleep/Off" HorizontalAlignment="Right" Margin="0,0,90,10" Width="75" Click="BtnTestOutOfService_Click" ToolTip="Test the 'System Sleeps or Shuts Down' behavior" VerticalAlignment="Bottom"/>
```

- [ ] **Étape 4 : ajouter le gestionnaire du bouton**

Dans `LuxOnAir/MainWindow.xaml.cs`, après la méthode `BtnTestLocked_Click` :

```csharp
        private void BtnTestOutOfService_Click(object sender, RoutedEventArgs e)
        {
            WriteToDebug("Testing 'System Sleeps or Shuts Down' behavior.");
            GoOutOfService();
        }
```

Contrairement aux autres boutons de test qui appellent directement une méthode de couleur, celui-ci
passe par `GoOutOfService()` : c'est justement la décision « couleur ou extinction » que l'on veut
éprouver.

- [ ] **Étape 5 : compiler**

Lancer la commande de compilation. Attendu : `0 Erreur(s)`.

- [ ] **Étape 6 : vérifier les deux branches de la décision**

```powershell
Get-Process -Name LuxOnAir -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process "C:\Users\MSI\source\repos\LuxaforOnAir\LuxOnAir\bin\Debug\LuxOnAir.exe"
```

Attendu, dans l'onglet Debug :
1. Les cinq boutons de test tiennent sur une rangée sans se chevaucher, et le libellé
   « Color Tests: » reste lisible à la largeur minimale de la fenêtre.
2. Onglet General réglé sur « No » : cliquer « Sleep/Off » éteint la lumière, et le journal affiche
   `Testing 'System Sleeps or Shuts Down' behavior.`
3. Cliquer « Reset » : la lumière revient à la couleur micro.
4. Onglet General réglé sur « Yes » avec une couleur bien distincte : cliquer « Sleep/Off » applique
   cette couleur.
5. Cliquer « Reset » : retour à la couleur micro.

- [ ] **Étape 7 : commit**

```bash
git add LuxOnAir/MainWindow.xaml LuxOnAir/MainWindow.xaml.cs
git commit -m "Add out-of-service decision logic and debug test button"
```

---

## Tâche 4 : Détection de la veille et de la fin de session

**Fichiers :**
- Modifier : `LuxOnAir/MainWindow.xaml.cs:44-47` (champ `powerWatcher`), `:80` (abonnements),
  `:114-120` (initialisation du watcher), `:275-305` (`PowerEvent_Arrive` et son commentaire),
  `:474` et `:483-484` (`Window_Closed`)

**Interfaces :**
- Consomme : `MainWindow.GoOutOfService()` (tâche 3), `MainWindow.CheckMicUsage()` (existant).
- Produit : `MainWindow.bSystemShutdown` (`private bool`), consommé par la tâche 5.

- [ ] **Étape 1 : relever le comportement de référence**

Avant toute modification, lancer l'application, mettre le PC en veille, le réveiller, et noter ce qui
se passe. Sur une machine en veille moderne (S0), il est possible que **rien** ne se produise
aujourd'hui — c'est précisément la faiblesse que cette tâche corrige. Consigner l'observation : elle
sert de point de comparaison à l'étape 8.

Pour connaître le type de veille de la machine :

```powershell
powercfg /a
```

- [ ] **Étape 2 : supprimer le watcher WMI d'alimentation**

Dans `LuxOnAir/MainWindow.xaml.cs`, supprimer les trois blocs suivants :

1. Le champ statique et son commentaire (lignes 44-47) :

```csharp
        /// <summary>
        /// Watches for system power events (e.g. suspend/resume)
        /// </summary>
        private static ManagementEventWatcher powerWatcher;
```

2. Le bloc d'initialisation dans le constructeur (lignes 114-120) :

```csharp
                // Watch for power changes
                powerWatcher = new ManagementEventWatcher
                {
                    Query = new WqlEventQuery("SELECT * FROM Win32_PowerManagementEvent")
                };
                powerWatcher.EventArrived += PowerEvent_Arrive;
                powerWatcher.Start();
```

3. La méthode `PowerEvent_Arrive` en entier (lignes 275-305, commentaire `/// <summary>` compris).

Dans `Window_Closed`, supprimer également ces deux lignes (483-484) :

```csharp
            powerWatcher.Stop();
            powerWatcher.Dispose();
```

Ne pas retirer `using System.Management;` : `regWatcher` et `hardwareWatcher` en dépendent toujours.

- [ ] **Étape 3 : déclarer les nouveaux handlers et l'indicateur d'arrêt**

Dans `LuxOnAir/MainWindow.xaml.cs`, juste après le champ `SessionSwitchHandler` (ligne 32 avant
modification), pour regrouper tous les handlers `SystemEvents` au même endroit :

```csharp
        /// <summary>
        /// Handles system power mode changes (suspend/resume)
        /// </summary>
        private static PowerModeChangedEventHandler PowerModeHandler;

        /// <summary>
        /// Handles the end of the Windows session (shutdown, restart, logoff)
        /// </summary>
        private static SessionEndingEventHandler SessionEndingHandler;

        /// <summary>
        /// Indicates the session is ending because Windows is shutting down or logging off.
        /// </summary>
        private bool bSystemShutdown = false;
```

`PowerModeChangedEventHandler` et `SessionEndingEventHandler` viennent de `Microsoft.Win32`, déjà
importé en tête de fichier pour `SystemEvents` et `Registry`.

Noter la dissymétrie volontaire : les deux handlers sont `static`, comme `SessionSwitchHandler` et
les watchers WMI voisins, mais `bSystemShutdown` est un champ **d'instance**, comme `ReallyExit`
(`MainWindow.xaml.cs:22`) qu'il complète dans `Window_Closing`. Ne pas le rendre statique par
mimétisme avec ses voisins.

- [ ] **Étape 4 : s'abonner aux événements**

Dans le constructeur `MainWindow()`, juste après la ligne existante :

```csharp
                SystemEvents.SessionSwitch += SessionSwitchHandler = new SessionSwitchEventHandler(OnSessionSwitch);
```

ajouter :

```csharp
                SystemEvents.PowerModeChanged += PowerModeHandler = new PowerModeChangedEventHandler(OnPowerModeChanged);
                SystemEvents.SessionEnding += SessionEndingHandler = new SessionEndingEventHandler(OnSessionEnding);
```

- [ ] **Étape 5 : écrire les deux gestionnaires**

Dans `LuxOnAir/MainWindow.xaml.cs`, à l'emplacement libéré par `PowerEvent_Arrive` :

```csharp
        /// <summary>
        /// Respond to suspend/resume events
        /// </summary>
        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    WriteToDebug("System entering Suspend, going out of service.");
                    Dispatcher.Invoke(() =>
                    {
                        GoOutOfService();
                    });
                    break;
                case PowerModes.Resume:
                    WriteToDebug("System resuming from Suspend, returning to normal status.");
                    Dispatcher.Invoke(() =>
                    {
                        CheckMicUsage();
                    });
                    break;
            }
        }

        /// <summary>
        /// Respond to the Windows session ending (shutdown, restart, logoff)
        /// </summary>
        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            WriteToDebug(string.Format("Session ending ({0}), going out of service.", e.Reason));

            Dispatcher.Invoke(() =>
            {
                GoOutOfService();
            });

            // Allow the window to close without being cancelled, and never delay Windows shutting down
            bSystemShutdown = true;
        }
```

`PowerModes.StatusChange` (bascule secteur/batterie) tombe volontairement dans aucun `case` : il se
déclenche fréquemment et ne concerne pas cette fonctionnalité.

`SessionEndingEventArgs.Cancel` reste à `false` : l'application ne doit jamais retarder l'arrêt.

- [ ] **Étape 6 : se désabonner à la fermeture**

Dans `Window_Closed`, juste après la ligne existante :

```csharp
            SystemEvents.SessionSwitch -= SessionSwitchHandler;
```

ajouter :

```csharp
            SystemEvents.PowerModeChanged -= PowerModeHandler;
            SystemEvents.SessionEnding -= SessionEndingHandler;
```

`SystemEvents` conserve des références statiques vers les abonnés ; sans désabonnement, l'objet
fenêtre resterait référencé.

- [ ] **Étape 7 : compiler**

Lancer la commande de compilation. Attendu : `0 Erreur(s)` et **aucun avertissement** signalant un
`powerWatcher` ou un `PowerEvent_Arrive` restant. S'il subsiste une référence, l'étape 2 est
incomplète.

- [ ] **Étape 8 : vérifier la veille**

```powershell
Get-Process -Name LuxOnAir -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process "C:\Users\MSI\source\repos\LuxaforOnAir\LuxOnAir\bin\Debug\LuxOnAir.exe"
```

Régler l'onglet General sur « Yes » avec une couleur bien distincte, puis mettre le PC en veille.

Attendu :
1. La lumière passe à la couleur hors service au moment de la mise en veille (observable si le port
   USB reste alimenté, ou sur un périphérique Bluetooth sur batterie).
2. Au réveil, session verrouillée : la lumière prend la couleur « verrouillé ».
3. Après déverrouillage : retour à la couleur micro.
4. L'onglet Debug contient `System entering Suspend, going out of service.` puis
   `System resuming from Suspend, returning to normal status.`

Le point 4 est le critère décisif : il prouve que la détection fonctionne même si l'alimentation USB
est coupée pendant la veille et que rien n'est visible sur la lumière.

Répéter avec le réglage « No » : la lumière doit s'éteindre au lieu de changer de couleur.

- [ ] **Étape 9 : commit**

```bash
git add LuxOnAir/MainWindow.xaml.cs
git commit -m "Replace WMI power watcher with SystemEvents power and session detection"
```

---

## Tâche 5 : Fermeture propre pendant l'arrêt de Windows

**Fichiers :**
- Modifier : `LuxOnAir/MainWindow.xaml.cs:456-466` (`Window_Closing`) et `:468-471`
  (`Window_Closed`)

**Interfaces :**
- Consomme : `MainWindow.bSystemShutdown` (tâche 4),
  `LightSettings.ShutdownHardware(bool)` (tâche 1), `StatusColors.ChangeOnOutOfService` (tâche 1).
- Produit : rien de consommé par une tâche ultérieure.

- [ ] **Étape 1 : relever le comportement de référence**

Avant modification, arrêter Windows avec l'application lancée et observer si l'écran « Ces
applications vous empêchent de fermer la session » apparaît, ou si l'arrêt marque un temps d'arrêt.
C'est le défaut que cette tâche corrige : `Window_Closing` annule la fermeture, Windows finit par
tuer le processus.

- [ ] **Étape 2 : ne plus annuler la fermeture pendant un arrêt système**

Dans `LuxOnAir/MainWindow.xaml.cs`, méthode `Window_Closing`, remplacer :

```csharp
            // Hide the window, don't actually quit unless we used an Exit button or menu
            if (!ReallyExit)
```

par :

```csharp
            // Hide the window, don't actually quit unless we used an Exit button or menu,
            // or Windows is shutting us down
            if (!ReallyExit && !bSystemShutdown)
```

- [ ] **Étape 3 : préserver la couleur hors service à l'arrêt**

Dans `LuxOnAir/MainWindow.xaml.cs`, méthode `Window_Closed`, remplacer :

```csharp
            // Literally turn off the lights
            Settings.Default.Lights.ShutdownHardware();
```

par :

```csharp
            // Literally turn off the lights, unless we just set an out-of-service color that should
            // remain displayed after Windows has shut down
            Settings.Default.Lights.ShutdownHardware(
                !(bSystemShutdown && Settings.Default.Lights.Colors.ChangeOnOutOfService));
```

- [ ] **Étape 4 : compiler**

Lancer la commande de compilation. Attendu : `0 Erreur(s)`.

- [ ] **Étape 5 : vérifier que la sortie normale n'a pas changé**

```powershell
Get-Process -Name LuxOnAir -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process "C:\Users\MSI\source\repos\LuxaforOnAir\LuxOnAir\bin\Debug\LuxOnAir.exe"
```

Attendu :
1. Fermer la fenêtre par la croix : l'application se réduit dans la zone de notification sans quitter
   — `bSystemShutdown` vaut `false`, donc l'annulation joue toujours.
2. Menu contextuel de l'icône → « Exit » : l'application quitte et la lumière s'éteint, quel que soit
   le réglage hors service. C'est voulu : une sortie manuelle n'est pas un arrêt système.

- [ ] **Étape 6 : vérifier l'arrêt de Windows**

Régler l'onglet General sur « Yes » avec une couleur distincte, cliquer « Done », puis arrêter
Windows.

Attendu :
1. L'écran « Ces applications vous empêchent de fermer la session » n'apparaît pas, et l'arrêt ne
   marque pas de temps d'attente attribuable à LuxOnAir.
2. La lumière affiche la couleur hors service au moment de l'extinction (visible jusqu'à la coupure
   de l'alimentation USB, ou durablement sur un périphérique Bluetooth sur batterie).

Vérification complémentaire au redémarrage, dans l'Observateur d'événements, journal
`Applications and Services Logs > Microsoft > Windows > Diagnostics-Performance > Operational` :
aucune entrée n'incrimine `LuxOnAir.exe` pour un ralentissement de l'arrêt.

Répéter avec le réglage « No » : la lumière doit s'éteindre.

- [ ] **Étape 7 : vérifier la déconnexion**

Se déconnecter de la session Windows plutôt qu'arrêter la machine. Attendu : même comportement qu'à
l'arrêt. Le journal de débogage n'étant plus consultable après coup, le critère est visuel.

- [ ] **Étape 8 : commit**

```bash
git add LuxOnAir/MainWindow.xaml.cs
git commit -m "Close cleanly during Windows shutdown and keep out-of-service color"
```

---

## Recette finale

À dérouler une fois les cinq tâches terminées, avec un fichier de réglages neuf. Pour repartir de
zéro, supprimer le dossier de configuration utilisateur :

```powershell
Get-Process -Name LuxOnAir -ErrorAction SilentlyContinue | Stop-Process -Force
Get-ChildItem "$env:LOCALAPPDATA" -Directory -Filter "LuxOnAir*" | Remove-Item -Recurse -Force
```

- [ ] Premier lancement sans réglages : la fenêtre s'ouvre d'emblée, le bloc hors service affiche
      « No », le bouton de couleur est grisé.
- [ ] Le bouton Debug « Sleep/Off » éteint la lumière ; « Reset » la rétablit.
- [ ] Passer à « Yes », choisir une couleur, « Done », rouvrir : réglages conservés.
- [ ] Win+L : couleur « verrouillé ». Déverrouiller : couleur micro. Le verrouillage n'a pas régressé.
- [ ] Veille puis réveil : couleur hors service, puis « verrouillé », puis micro après déverrouillage.
- [ ] Arrêt de Windows : couleur hors service appliquée, aucun blocage de l'arrêt.
- [ ] Déconnexion : même comportement qu'à l'arrêt.
- [ ] Sortie par « Exit » : la lumière s'éteint.
- [ ] Débrancher puis rebrancher le Luxafor pendant que l'application tourne : le voyant d'état
      repasse au rouge puis au vert. Le `hardwareWatcher` n'a pas été affecté par le retrait du
      `powerWatcher`.
