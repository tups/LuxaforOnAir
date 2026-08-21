using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LuxOnAir
{
    internal class MicrophoneHelper
    {
        // Registry path to mic usage data
        public const string MicCapabilityKey = "Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone";

        /// <summary>
        /// Describes where the last mic usage result came from, for the debug log.
        /// </summary>
        public static string LastDetectionSource { get; private set; } = "none";

        /// <summary>
        /// Gets the names of all applications currently capturing from a microphone.
        /// </summary>
        /// <remarks>
        /// Windows 11 build 26220 (August 2026 updates) stopped keeping the ConsentStore registry
        /// values up to date while a capture is in progress, so the registry alone can no longer be
        /// trusted. Audio session enumeration is the primary source; the registry is kept as a
        /// fallback for systems where session enumeration is unavailable.
        /// </remarks>
        public static List<string> GetMicUsers()
        {
            List<string> micUsers;

            if (TryGetMicUsersFromAudioSessions(out micUsers))
            {
                LastDetectionSource = "audio sessions";
                return micUsers;
            }

            LastDetectionSource = "registry (fallback)";
            return GetMicUsersFromRegistry();
        }

        /// <summary>
        /// Look for active capture sessions on every enabled recording device.
        /// </summary>
        /// <param name="micUsers">Names of the applications holding an active capture session</param>
        /// <returns>True if the audio sessions could be enumerated, false if this source is unusable</returns>
        private static bool TryGetMicUsersFromAudioSessions(out List<string> micUsers)
        {
            micUsers = new List<string>();

            try
            {
                using (MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator())
                {
                    MMDeviceCollection captureDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                    // No enabled microphone at all means nothing can be capturing
                    if (captureDevices.Count == 0) { return true; }

                    int ownProcessId = Process.GetCurrentProcess().Id;

                    foreach (MMDevice captureDevice in captureDevices)
                    {
                        using (captureDevice)
                        {
                            AudioSessionManager sessionManager = captureDevice.AudioSessionManager;
                            sessionManager.RefreshSessions();
                            SessionCollection sessions = sessionManager.Sessions;

                            for (int i = 0; i < sessions.Count; i++)
                            {
                                AudioSessionControl session = sessions[i];

                                // Only a session in the active state is actually capturing right now
                                if (session.State != AudioSessionState.AudioSessionStateActive) { continue; }

                                // Ignore Windows' own system sounds session and our own process
                                if (session.IsSystemSoundsSession) { continue; }
                                int processId = (int)session.GetProcessID;
                                if (processId == 0 || processId == ownProcessId) { continue; }

                                string name = DescribeProcess(processId, session);
                                if (!micUsers.Contains(name)) { micUsers.Add(name); }
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception)
            {
                // Core Audio is unavailable on this system, fall back to the registry
                micUsers = new List<string>();
                return false;
            }
        }

        /// <summary>
        /// Build a readable name for the process holding a capture session.
        /// </summary>
        /// <param name="processId">Process ID reported by the audio session</param>
        /// <param name="session">Session to fall back on if the process cannot be inspected</param>
        private static string DescribeProcess(int processId, AudioSessionControl session)
        {
            try
            {
                Process process = Process.GetProcessById(processId);
                try
                {
                    // Full path where we are allowed to read it, matching what the registry used to report
                    return process.MainModule.FileName;
                }
                catch (Exception)
                {
                    return process.ProcessName;
                }
            }
            catch (Exception)
            {
                // The process ended between enumeration and inspection, use whatever the session knows
                string displayName = session.DisplayName;
                return string.IsNullOrEmpty(displayName) ? string.Format("PID {0}", processId) : displayName;
            }
        }

        /// <summary>
        /// Read mic usage from the capability access ConsentStore in the registry.
        /// </summary>
        private static List<string> GetMicUsersFromRegistry()
        {
            List<string> micUsers = new List<string>();

            // Open the current user mic usage key
            RegistryKey rootStore = Registry.CurrentUser.OpenSubKey(MicCapabilityKey);

            if (rootStore == null) { return micUsers; }

            // Get any current mic users and add their names to the list
            foreach (string micUser in FindRegKeyWithValueData(rootStore))
            {
                micUsers.Add(micUser.Replace("#", "\\"));
            }

            return micUsers;
        }

        private static List<string> FindRegKeyWithValueData(RegistryKey parentKey)
        {
            List<string> micUserNames = new List<string>();

            // Check if this key has a last used value
            object lastUsed = parentKey.GetValue("LastUsedTimeStop");

            // If it does...
            if (lastUsed != null)
            {
                // ... check if it is zero, indicating a program currently using the mic
                ulong value = Convert.ToUInt64(lastUsed);
                if (value == 0)
                {
                    micUserNames.Add(parentKey.Name.Substring(parentKey.Name.LastIndexOf("\\") + 1));
                }
            }

            // Now recursively check all subkeys
            foreach (string childKeyName in parentKey.GetSubKeyNames())
            {
                RegistryKey childKey = parentKey.OpenSubKey(childKeyName);

                // A key we are not allowed to open cannot tell us anything
                if (childKey == null) { continue; }

                micUserNames.AddRange(FindRegKeyWithValueData(childKey));
            }

            return micUserNames;
        }
    }
}
