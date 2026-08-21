using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Forms;
using LuxOnAir.Properties;
using System.Management;
using System.Windows.Media;
using System.Threading;
using System.Windows.Interop;

namespace LuxOnAir
{
        /// <summary>
        /// Interaction logic for MainWindow.xaml
        /// </summary>
        public partial class MainWindow : Window
    {
        /// <summary>
        /// Indicate if the application should fully exit when closing a window.
        /// </summary>
        private bool ReallyExit = false;

        /// <summary>
        /// Notification icon for this application
        /// </summary>
        private static NotifyIcon notifyIcon;

        /// <summary>
        /// Handles session switch events
        /// </summary>
        private static SessionSwitchEventHandler SessionSwitchHandler;

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

        /// <summary>
        /// Clears bSystemShutdown if Windows announced a session end but then cancelled it.
        /// </summary>
        private System.Timers.Timer shutdownCancelTimer;

        /// <summary>
        /// Watches for system hardware changes (e.g. USB connect/disconnect)
        /// </summary>
        private static ManagementEventWatcher hardwareWatcher;

        /// <summary>
        /// Watches for registry changes (mic in use)
        /// </summary>
        private static ManagementEventWatcher regWatcher;

        /// <summary>
        /// Polls for microphone usage. Audio sessions raise no system-wide event we can subscribe to,
        /// and the registry watcher no longer fires on Windows builds that stopped keeping the
        /// ConsentStore up to date during a capture.
        /// </summary>
        private System.Timers.Timer micPollTimer;

        /// <summary>
        /// How often to check whether the microphone is in use, in milliseconds
        /// </summary>
        private const double micPollInterval = 1000;

        /// <summary>
        /// The status the lights were last set to, so repeated checks don't resend the same status.
        /// </summary>
        private LightStatus currentStatus = LightStatus.Unknown;

        /// <summary>
        /// The statuses the lights can be showing
        /// </summary>
        private enum LightStatus
        {
            /// <summary>Status is unknown, so the next check must apply whatever it finds</summary>
            Unknown,
            /// <summary>The console is locked</summary>
            Locked,
            /// <summary>The microphone is in use</summary>
            InUse,
            /// <summary>The microphone is not in use</summary>
            NotInUse
        }

        /// <summary>
        /// Keeps track of whether the console is locked or unlocked.
        /// </summary>
        private static bool bConsoleLocked = false;

        /// <summary>
        /// Stores the window's state prior to being minimized to the notification area
        /// </summary>
        private WindowState storedWindowState = WindowState.Normal;

        /// <summary>
        /// Mutex used to check whether another instance of this app is already running.
        /// </summary>
        private static readonly Mutex mutex = new Mutex(true, "{FEB97F90-3F85-429F-9F59-526F29856FC0}");

        /// <summary>
        /// Whether to hide the settings UI on startup
        /// </summary>
        private static bool HideOnStart = true;

        public MainWindow()
        {
            // Check no other instance is already running
            if (mutex.WaitOne(TimeSpan.Zero, true))
            {

                InitializeComponent();

                lblProductVer.Content = string.Format("{0} {1}", System.Windows.Forms.Application.ProductName, System.Windows.Forms.Application.ProductVersion);
                lblAbout.Content = SettingsHelper.About;

                SystemEvents.SessionSwitch += SessionSwitchHandler = new SessionSwitchEventHandler(OnSessionSwitch);
                SystemEvents.PowerModeChanged += PowerModeHandler = new PowerModeChangedEventHandler(OnPowerModeChanged);
                SystemEvents.SessionEnding += SessionEndingHandler = new SessionEndingEventHandler(OnSessionEnding);

                InitNotifyIcon();
                LoadSettings();

                // Initialize devices
                Settings.Default.Lights.InitHardware();
                WriteToDebug(Settings.Default.Lights.ConnectedDeviceDesc());

                // Update the device status UI
                UpdateDeviceStatus();

                // Check if program is correctly set to run at logon
                chkStartAtLogon.IsChecked = GetRunAtLogon();

                // Get current user SID so we can monitor the correct key in the USERS registry hive
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;

                // Watch for registry changes
                regWatcher = new ManagementEventWatcher
                {
                    Query = new WqlEventQuery(string.Format("SELECT * FROM RegistryTreeChangeEvent WHERE Hive='HKEY_USERS' AND RootPath='{0}\\\\{1}'", sid, MicrophoneHelper.MicCapabilityKey.Replace("\\", "\\\\")))
                };
                regWatcher.EventArrived += RegWatcher_EventArrived;
                regWatcher.Start();

                // Watch for hardware changes
                hardwareWatcher = new ManagementEventWatcher
                {
                    Query = new WqlEventQuery("SELECT * FROM __InstanceOperationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity' GROUP WITHIN " + Settings.Default.Lights.InitSeconds.ToString())
                };
                hardwareWatcher.EventArrived += USBDevices_Changed;
                hardwareWatcher.Start();

                // Poll for microphone usage, as active audio sessions raise no system-wide event
                micPollTimer = new System.Timers.Timer(micPollInterval);
                micPollTimer.Elapsed += MicPollTimer_Elapsed;
                micPollTimer.Start();

                // Run the first mic check now
                CheckMicUsage();
            }
            else
            {
                // Another instance is already running
                
                // Message the other instance to show the settings window
                MessageHelper.PostMessage((IntPtr)MessageHelper.HWND_BROADCAST, MessageHelper.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);

                ReallyExit = true;
                Close();
            }
        }

        /// <summary>
        /// Load saved settings and reflect them in the UI
        /// </summary>
        private void LoadSettings()
        {
            // Try to load settings, init defaults and show UI if no previous settings found
            if (!SettingsHelper.LoadSettings())
            {
                WriteToDebug("No previous settings found, loading defaults and showing UI for first run.");
                HideOnStart = false;
            }

            btnMicInUse.Background = Settings.Default.Lights.Colors.MicInUse.ToBrush();
            btnMicNotInUse.Background = Settings.Default.Lights.Colors.MicNotInUse.ToBrush();

            radioLockedYes.IsChecked = Settings.Default.Lights.Colors.ChangeOnLock;
            btnLocked.Background = Settings.Default.Lights.Colors.SessionLocked.ToBrush();

            radioOutOfServiceYes.IsChecked = Settings.Default.Lights.Colors.ChangeOnOutOfService;
            radioOutOfServiceNo.IsChecked = !Settings.Default.Lights.Colors.ChangeOnOutOfService;
            btnOutOfService.Background = Settings.Default.Lights.Colors.OutOfService.ToBrush();

            chkInUseBlink.IsChecked = Settings.Default.Lights.Colors.BlinkMicInUse;
            chkInUseWave.IsChecked = Settings.Default.Lights.Colors.WaveMicInUse;
        }

        /// <summary>
        /// Ensure currently configured settings are saved from the UI
        /// </summary>
        private void ApplySettings()
        {
            Settings.Default.Save();

            // The user may have changed a color, so always reapply the current status
            CheckMicUsage(true);
        }

        /// <summary>
        /// Handles registry change events. Check for mic usage whenever a change is detected.
        /// </summary>
        private void RegWatcher_EventArrived(object sender, EventArrivedEventArgs e)
        {
            CheckMicUsage();
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            // Listen for windows messages
            HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(WndProc);

            // Now that the window has fully initialized, hide it if we aren't showing the settings screen immediately.
            if (HideOnStart) { WindowState = WindowState.Minimized; }
        }

        // Handle incoming windows messages
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == MessageHelper.WM_SHOWME)
            {
                ShowSettingsWindow();
                handled = true;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Determines whether the application is set to run automatically when the user logs on
        /// </summary>
        private bool GetRunAtLogon()
        {
            // Check if registry value exists and has the correct value for the current app path
            return Registry.CurrentUser.OpenSubKey(SettingsHelper.RegWindowsRunKey).GetValue(SettingsHelper.ProgramValueID, "").ToString() == string.Format("\"{0}\"", System.Windows.Forms.Application.ExecutablePath);
        }

        /// <summary>
        /// Sets whether the application is set to run automatically when the user logs on
        /// </summary>
        private void SetRunAtLogon(bool value)
        {
            // Get reference to the current user's Windows Run key with edit permissions
            RegistryKey windowsRun = Registry.CurrentUser.OpenSubKey(SettingsHelper.RegWindowsRunKey, true);

            if (value)
            {
                // If not already set to run at logon, set the correct registry key now
                if (!GetRunAtLogon())
                {
                    windowsRun.SetValue(SettingsHelper.ProgramValueID, string.Format("\"{0}\"", System.Windows.Forms.Application.ExecutablePath));
                    WriteToDebug(string.Format("Added registry key at {0} to run app at startup.", windowsRun.Name));
                }
            }
            else
            {
                // Check for the presence of the startup registry value and remove it
                if (windowsRun.GetValue(SettingsHelper.ProgramValueID) != null)
                {
                    windowsRun.DeleteValue(SettingsHelper.ProgramValueID);
                    WriteToDebug(string.Format("Removed registry key from {0}, app will no longer run at startup.", windowsRun.Name));
                }
            }
        }

        /// <summary>
        /// Respond to hardware changes
        /// </summary>
        private void USBDevices_Changed(object sender, EventArrivedEventArgs e)
        {
            WriteToDebug("Hardware change detected.");
            Settings.Default.Lights.InitHardware();
            WriteToDebug(Settings.Default.Lights.ConnectedDeviceDesc());

            // Update the device status UI
            UpdateDeviceStatus();
            // Run a mic check now
            CheckMicUsage();
        }

        /// <summary>
        /// Update the indicator on the main settings screen to show how many devices are connected
        /// </summary>
        private void UpdateDeviceStatus()
        {
            // Thread-safe UI update
            Dispatcher.Invoke(() =>
            {
                lblStatus.Content = Settings.Default.Lights.ConnectedDeviceDesc();

                if (Settings.Default.Lights.ConnectedDeviceCount() == 0)
                {
                    // Red status indicator
                    elpStatus.Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x58, 0x58));
                    elpStatus.Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xB7, 0, 0));
                }
                else
                {
                    // Green status indicator
                    elpStatus.Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x55, 0xBB, 0x55));
                    elpStatus.Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0, 0xB6, 0));
                }
            });
        }

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
                        ReturnToService();
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

            // If the shutdown or logoff is later cancelled by Windows, there is no dedicated
            // event to observe that. Guard against being stuck in this state forever by clearing
            // it automatically if the app is still alive well after the session end was announced.
            // A real shutdown kills the process long before this fires.
            if (shutdownCancelTimer == null)
            {
                shutdownCancelTimer = new System.Timers.Timer(60000) { AutoReset = false };
                shutdownCancelTimer.Elapsed += ShutdownCancelTimer_Elapsed;
            }
            else
            {
                shutdownCancelTimer.Stop();
            }
            shutdownCancelTimer.Start();
        }

        /// <summary>
        /// Clears the system-shutdown flag after the safety interval, on the assumption that
        /// the previously announced session end must have been cancelled.
        /// </summary>
        private void ShutdownCancelTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            bSystemShutdown = false;
        }

        /// <summary>
        /// Respond to session switching (workstation lock/unlock)
        /// </summary>
        public void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            // Only react to console lock/unlock if set by user
            if (Settings.Default.Lights.Colors.ChangeOnLock)
            {
                switch (e.Reason)
                {
                    case SessionSwitchReason.SessionUnlock:
                    case SessionSwitchReason.ConsoleConnect:
                        WriteToDebug("Console session unlocked. Setting to standard color.");
                        bConsoleLocked = false;
                        CheckMicUsage();
                        break;
                    case SessionSwitchReason.SessionLogoff:
                        // Logoff is owned by OnSessionEnding, which sets the out-of-service
                        // state. Reacting here as well would overwrite it.
                        break;
                    default:
                        WriteToDebug("Console session locked. Setting to Locked color.");
                        bConsoleLocked = true;
                        Settings.Default.Lights.SetLocked();
                        break;
                }
            }
        }

        /// <summary>
        /// Initialize the notification icon for this application
        /// </summary>
        private void InitNotifyIcon()
        {
            // Create an Exit menu item
            ToolStripMenuItem ExitMenuItem = new ToolStripMenuItem()
            {
                Name = "ExitMenuItem",
                Text = "Exit"
            };
            ExitMenuItem.Click += ExitMenuItem_Click;

            // Create a Settings menu item
            ToolStripMenuItem SettingsMenuItem = new ToolStripMenuItem()
            {
                Name = "SettingsMenuItem",
                Text = "Show Settings"
            };
            SettingsMenuItem.Click += SettingsMenuItem_Click;

            ToolStripSeparator SpacerMenuItem = new ToolStripSeparator();

            // Create the context menu for the notification icon
            ContextMenuStrip TrayIconContextMenu = new ContextMenuStrip()
            {
                Name = "TrayIconContextMenu"
            };

            // Add items to the menu
            TrayIconContextMenu.SuspendLayout();
            TrayIconContextMenu.Items.AddRange(new ToolStripItem[] {SettingsMenuItem, SpacerMenuItem, ExitMenuItem});
            TrayIconContextMenu.ResumeLayout(false);

            // Define the notification icon 
            notifyIcon = new NotifyIcon
            {
                Icon = Properties.Resources.NotifyIcon,
                Text = System.Windows.Forms.Application.ProductName,
                ContextMenuStrip = TrayIconContextMenu,
                Visible = true,
            };

            // Set the double-click action to be the same as selecting Settings from the context menu
            notifyIcon.MouseDoubleClick += SettingsMenuItem_Click;

        }

        /// <summary>
        /// Check if the microphone is in use and react accordingly
        /// </summary>
        /// <param name="force">Set the lights even if the status has not changed since the last check,
        /// e.g. after the user picked a new color.</param>
        /// <returns>Text names of all applications using the microphone</returns>
        private List<string> CheckMicUsage(bool force = false)
        {
            List<string> micUsers;

            try
            {
                micUsers = MicrophoneHelper.GetMicUsers();
            }
            catch (Exception ex)
            {
                WriteToDebug(string.Format("Could not check microphone usage: {0}", ex.Message));
                micUsers = new List<string>();
            }

            LightStatus newStatus = bConsoleLocked
                ? LightStatus.Locked
                : (micUsers.Count > 0 ? LightStatus.InUse : LightStatus.NotInUse);

            // This check runs on a timer, so only touch the lights when something actually changed.
            // Resending the same status would restart the blink and wave effects on every poll.
            if (!force && newStatus == currentStatus) { return micUsers; }

            currentStatus = newStatus;

            switch (newStatus)
            {
                case LightStatus.Locked:
                    Settings.Default.Lights.SetLocked();
                    break;
                case LightStatus.InUse:
                    // Trigger mic in use lights
                    WriteToDebug(string.Format("Mic in use by {0} app{1} ({2}): {3}",
                        micUsers.Count, micUsers.Count == 1 ? "" : "s", MicrophoneHelper.LastDetectionSource, string.Join(", ", micUsers)));
                    Settings.Default.Lights.SetInUse();
                    break;
                default:
                    // Trigger mic not in use lights
                    WriteToDebug("Mic is not in use.");
                    Settings.Default.Lights.SetNotInUse();
                    break;
            }

            return micUsers;

        }

        /// <summary>
        /// Runs a mic check on the UI thread from the poll timer
        /// </summary>
        private void MicPollTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                CheckMicUsage();
            });
        }

        /// <summary>
        /// React to the system going out of service (sleep, shutdown, restart, logoff)
        /// </summary>
        private void GoOutOfService()
        {
            // Stop polling so the next mic check can't overwrite the out-of-service color, and forget
            // the current status so the check that resumes service always sets the lights again.
            if (micPollTimer != null) { micPollTimer.Stop(); }
            currentStatus = LightStatus.Unknown;

            if (Settings.Default.Lights.Colors.ChangeOnOutOfService)
            {
                Settings.Default.Lights.SetOutOfService();
            }
            else
            {
                Settings.Default.Lights.SetLightsOff();
            }
        }

        /// <summary>
        /// Hold a test color on the lights by suspending the mic polling, which would otherwise
        /// replace it on the next check. The Reset button on the Debug tab returns to normal.
        /// </summary>
        private void EnterTestMode()
        {
            if (micPollTimer != null) { micPollTimer.Stop(); }
            currentStatus = LightStatus.Unknown;
        }

        /// <summary>
        /// React to the system coming back into service (resume from sleep, cancelled shutdown)
        /// </summary>
        private void ReturnToService()
        {
            CheckMicUsage(true);

            // Resume polling, which was stopped while out of service
            if (micPollTimer != null) { micPollTimer.Start(); }
        }

        /// <summary>
        /// Write a message to the debug log control
        /// </summary>
        /// <param name="Msg">Message to write to the log</param>
        /// <param name="NoNewLine">Whether to add a new line at the end of the message</param>
        private void WriteToDebug(string Msg, bool NoNewLine = false)
        {
            Dispatcher.Invoke(() =>
            {
                txtDebugLog.AppendText(DateTime.Now.ToString("'['yy'-'MM'-'dd HH':'mm':'ss']' ") + Msg);
                if (!NoNewLine) { txtDebugLog.AppendText("\n"); }
                txtDebugLog.ScrollToEnd();
            });
        }

        private void BtnDone_Click(object sender, RoutedEventArgs e)
        {
            ApplySettings();
            Hide();
        }

        private void ExitMenuItem_Click(object sender, EventArgs e)
        {
            ReallyExit = true;
            Close();
        }

        private void SettingsMenuItem_Click(object sender, EventArgs e)
        {
            ShowSettingsWindow();
        }

        /// <summary>
        /// Show the settings window with the same state (maximized or not) as before it was hidden.
        /// </summary>
        private void ShowSettingsWindow()
        {
            Show();
            WindowState = storedWindowState;

            // Store the current Topmost value (usually false)
            bool top = this.Topmost;
            // Make settings jump to the top of everything
            this.Topmost = true;
            // Set it back to whatever it was before
            this.Topmost = top;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // An explicit Exit proves Windows is still running, so any announced session
            // end must have been cancelled.
            if (ReallyExit) { bSystemShutdown = false; }

            // Hide the window, don't actually quit unless we used an Exit button or menu,
            // or Windows is shutting us down
            if (!ReallyExit && !bSystemShutdown)
            {
                Hide();
                e.Cancel = true;
            }

            Settings.Default.Save();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            // Literally turn off the lights, unless we just set an out-of-service color that should
            // remain displayed after Windows has shut down
            Settings.Default.Lights.ShutdownHardware(
                !(bSystemShutdown && Settings.Default.Lights.Colors.ChangeOnOutOfService));
            
            // Stop listening for console lock & unlock events
            SystemEvents.SessionSwitch -= SessionSwitchHandler;
            SystemEvents.PowerModeChanged -= PowerModeHandler;
            SystemEvents.SessionEnding -= SessionEndingHandler;

            notifyIcon.Dispose();

            // Stop and dispose of the mic poll timer
            if (micPollTimer != null)
            {
                micPollTimer.Stop();
                micPollTimer.Dispose();
            }

            // Stop and dispose of the shutdown-cancel safety timer, if it was ever created
            if (shutdownCancelTimer != null)
            {
                shutdownCancelTimer.Stop();
                shutdownCancelTimer.Dispose();
            }

            // Stop and dispose of WMI event watchers
            regWatcher.Stop();
            regWatcher.Dispose();
            hardwareWatcher.Stop();
            hardwareWatcher.Dispose();

            // Release the single-instance mutex
            mutex.ReleaseMutex();
        }

        private void BtnMicInUse_Click(object sender, RoutedEventArgs e)
        {
            // Show a color dialog with the current color for the user to change
            ColorDialog colorDialog = new ColorDialog()
            {
                Color = System.Drawing.Color.FromArgb(Settings.Default.Lights.Colors.MicInUse)
            };

            // If the user did not cancel, set the picked color as the new mic in use indicator color
            if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Settings.Default.Lights.Colors.MicInUse = colorDialog.Color.ToArgb();
                btnMicInUse.Background = Settings.Default.Lights.Colors.MicInUse.ToBrush();
                ApplySettings();
            }
        }

        private void BtnMicNotInUse_Click(object sender, RoutedEventArgs e)
        {
            // Show a color dialog with the current color for the user to change
            ColorDialog colorDialog = new ColorDialog()
            {
                Color = System.Drawing.Color.FromArgb(Settings.Default.Lights.Colors.MicNotInUse)
            };

            // If the user did not cancel, set the picked color as the new mic not in use indicator color
            if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Settings.Default.Lights.Colors.MicNotInUse = colorDialog.Color.ToArgb();
                btnMicNotInUse.Background = Settings.Default.Lights.Colors.MicNotInUse.ToBrush();
                ApplySettings();
            }
        }

        private void BtnLocked_Click(object sender, RoutedEventArgs e)
        {
            // Show a color dialog with the current color for the user to change
            ColorDialog colorDialog = new ColorDialog()
            {
                Color = System.Drawing.Color.FromArgb(Settings.Default.Lights.Colors.SessionLocked)
            };

            // If the user did not cancel, set the picked color as the new locked console indicator color
            if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Settings.Default.Lights.Colors.SessionLocked = colorDialog.Color.ToArgb();
                btnLocked.Background = Settings.Default.Lights.Colors.SessionLocked.ToBrush();
                ApplySettings();
            }
        }

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

        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            List<string> micUsers = CheckMicUsage();

            // Report mic usage to the debug log
            if (micUsers.Count > 0)
            {
                WriteToDebug(string.Format("Mic is in use by {0} app{1}, detected via {2}:\n{3}", micUsers.Count, micUsers.Count == 1 ? "" : "s", MicrophoneHelper.LastDetectionSource, string.Join("\n", micUsers)));
            }
            else
            {
                WriteToDebug(string.Format("Mic is not currently in use, checked via {0}.", MicrophoneHelper.LastDetectionSource));
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Hide on minimize
                Hide();
            }
            else
            {
                // When not minimized, make sure it restores to the previous state
                storedWindowState = WindowState;
            }
        }

        private void ChkInUseBlink_Changed(object sender, RoutedEventArgs e)
        {
            Settings.Default.Lights.Colors.BlinkMicInUse = (bool)chkInUseBlink.IsChecked;
            // If using blink, turn off wave
            if (Settings.Default.Lights.Colors.BlinkMicInUse) { chkInUseWave.IsChecked = false; }
            ApplySettings();
        }

        private void ChkInUseWave_Changed(object sender, RoutedEventArgs e)
        {
            Settings.Default.Lights.Colors.WaveMicInUse = (bool)chkInUseWave.IsChecked;
            // If using wave, turn off blink
            if (Settings.Default.Lights.Colors.WaveMicInUse) { chkInUseBlink.IsChecked = false; }
            ApplySettings();
        }


        private void ChkStartAtLogon_Changed(object sender, RoutedEventArgs e)
        {
            // Set the correct registry value to match whether the box is checked
            SetRunAtLogon((bool)chkStartAtLogon.IsChecked);
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            // Open the default browser to the requested website
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }

        private void BtnTestInUse_Click(object sender, RoutedEventArgs e)
        {
            WriteToDebug("Testing 'In Use' color.");
            EnterTestMode();
            Settings.Default.Lights.SetInUse();
        }

        private void BtnTestNotInUse_Click(object sender, RoutedEventArgs e)
        {
            WriteToDebug("Testing 'Not In Use' color.");
            EnterTestMode();
            Settings.Default.Lights.SetNotInUse();
        }

        private void BtnTestLocked_Click(object sender, RoutedEventArgs e)
        {
            WriteToDebug("Testing 'Console Locked' color.");
            EnterTestMode();
            Settings.Default.Lights.SetLocked();
        }

        private void BtnTestOutOfService_Click(object sender, RoutedEventArgs e)
        {
            WriteToDebug("Testing 'System Sleeps or Shuts Down' behavior.");
            GoOutOfService();
        }

        private void BtnTestReset_Click(object sender, RoutedEventArgs e)
        {
            WriteToDebug("Resetting to normal status color.");
            ReturnToService();
        }

        private void TabItem_LostFocus(object sender, RoutedEventArgs e)
        {
            CheckMicUsage(true);
        }

        private void RadioLocked_Checked(object sender, RoutedEventArgs e)
        {
            // Set the UI and saved settings to match whether this option is enabled or not
            labelLockedColor.IsEnabled = btnLocked.IsEnabled = Settings.Default.Lights.Colors.ChangeOnLock = (bool)radioLockedYes.IsChecked;
            ApplySettings();
        }

        private void RadioOutOfService_Checked(object sender, RoutedEventArgs e)
        {
            // Set the UI and saved settings to match whether this option is enabled or not
            labelOutOfServiceColor.IsEnabled = btnOutOfService.IsEnabled = Settings.Default.Lights.Colors.ChangeOnOutOfService = (bool)radioOutOfServiceYes.IsChecked;
            ApplySettings();
        }
    }
}
