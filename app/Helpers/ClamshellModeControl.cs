using GHelper.Display;
using GHelper.Mode;
using Microsoft.Win32;

namespace GHelper.Helpers
{
    internal class ClamshellModeControl
    {
        private const int DisplaySettingsDebounceMs = 750;

        private readonly System.Timers.Timer _displaySettingsChangedTimer = new(DisplaySettingsDebounceMs)
        {
            AutoReset = false,
        };

        private int _pendingDisplaySettingsEvents;
        private bool _lastExternalDisplayConnected;

        public ClamshellModeControl()
        {
            //Save current setting if hibernate or shutdown to prevent reverting the user set option.
            CheckAndSaveLidAction();
            _lastExternalDisplayConnected = DetectExternalDisplayConnected();
            _displaySettingsChangedTimer.Elapsed += DisplaySettingsChangedTimer_Elapsed;
        }

        public bool IsExternalDisplayConnected()
        {
            try
            {
                var devicesList = ScreenInterrogatory.GetAllDevices();
                var devices = devicesList.ToArray();

                string internalName = AppConfig.GetString("internal_display");

                foreach (var device in devices)
                {
                    if (device.outputTechnology != ScreenInterrogatory.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL &&
                        device.outputTechnology != ScreenInterrogatory.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                        && device.monitorFriendlyDeviceName != internalName)
                    {
                        Logger.WriteLine("Found external screen: " + device.monitorFriendlyDeviceName + ":" + device.outputTechnology.ToString());

                        //Already found one, we do not have to check whether there are more
                        return true;
                    }

                }
            } catch (Exception ex)
            {
                Logger.WriteLine(ex.ToString());
            }

            return false;
        }

        public bool IsClamshellEnabled()
        {
            return AppConfig.Is("toggle_clamshell_mode");
        }

        public bool IsChargerConnected()
        {
            return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        }

        public bool IsClamshellReady()
        {
            return IsExternalDisplayConnected() && (IsChargerConnected() || AppConfig.Is("clamshell_battery"));
        }

        public void ToggleLidAction()
        {
            if (!IsClamshellEnabled())
            {
                return;
            }

            if (IsClamshellReady())
            {
                EnableClamshellMode();
            }
            else
            {
                DisableClamshellMode();
            }
        }
        public static void DisableClamshellMode()
        {
            if (PowerNative.GetLidAction(true) == GetDefaultLidAction()) return;
            PowerNative.SetLidAction(GetDefaultLidAction(), true);
            Logger.WriteLine("Disengaging Clamshell Mode");
        }

        public static void EnableClamshellMode()
        {
            if (PowerNative.GetLidAction(true) == 0) return;
            PowerNative.SetLidAction(0, true);
            Logger.WriteLine("Engaging Clamshell Mode");
        }

        public void UnregisterDisplayEvents()
        {
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _displaySettingsChangedTimer.Stop();
        }

        public void RegisterDisplayEvents()
        {
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        }

        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            int pending = Interlocked.Increment(ref _pendingDisplaySettingsEvents);
            Logger.WriteLine($"Display configuration changed queued (events={pending})");

            _displaySettingsChangedTimer.Stop();
            _displaySettingsChangedTimer.Start();
        }

        private void DisplaySettingsChangedTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            int pendingEvents = Interlocked.Exchange(ref _pendingDisplaySettingsEvents, 0);
            bool externalDisplayConnected = DetectExternalDisplayConnected();
            string reason = GetDisplayChangeReason(externalDisplayConnected);
            bool clamshellToggled = false;

            Logger.WriteLine($"Display configuration changed ({reason}, events={pendingEvents})");

            int lidActionBefore = -1;
            if (IsClamshellEnabled())
            {
                lidActionBefore = PowerNative.GetLidAction(true);
                ToggleLidAction();
                int lidActionAfter = PowerNative.GetLidAction(true);
                if (lidActionBefore != lidActionAfter)
                {
                    clamshellToggled = true;
                    Logger.WriteLine($"Display configuration changed (clamshell-toggle: {lidActionBefore}->{lidActionAfter})");
                }
            }

            _lastExternalDisplayConnected = externalDisplayConnected;
            Program.MarkDisplayTopologyChange(clamshellToggled ? "clamshell-toggle" : reason);

            if (Program.settingsForm.Visible)
                ScreenControl.InitScreen();

            if (AppConfig.IsForceMiniled())
                ScreenControl.InitMiniled();
        }

        private bool DetectExternalDisplayConnected()
        {
            return IsExternalDisplayConnected();
        }

        private string GetDisplayChangeReason(bool externalDisplayConnected)
        {
            if (externalDisplayConnected != _lastExternalDisplayConnected)
                return externalDisplayConnected ? "external-monitor-connect" : "external-monitor-disconnect";

            return "display-change";
        }

        private static int CheckAndSaveLidAction()
        {
            if (AppConfig.Get("clamshell_default_lid_action", -1) != -1)
            {
                //Seting was alredy set. Do not touch it
                return AppConfig.Get("clamshell_default_lid_action", -1);
            }

            try
            {
                int val = PowerNative.GetLidAction(true);
                //If it is 0 then it is likely already set by clamshell mdoe
                //If 0 was set by the user, then why do they even use clamshell mode?
                //We only care about hibernate or shutdown setting here
                if (val == 2 || val == 3)
                {
                    AppConfig.Set("clamshell_default_lid_action", val);
                    return val;
                }
            } catch (Exception ex)
            {
                Logger.WriteLine("Can't get Lid Action: " + ex.ToString());
            }

            return 1;
        }

        //Power users can change that setting.
        //0 = Do nothing
        //1 = Sleep (default)
        //2 = Hibernate
        //3 = Shutdown
        private static int GetDefaultLidAction()
        {
            int val = AppConfig.Get("clamshell_default_lid_action", 1);

            if (val < 0 || val > 3)
            {
                val = 1;
            }

            return val;
        }
    }
}
