using GHelper.Display;
using GHelper.Gpu.NVidia;
using GHelper.Helpers;
using GHelper.USB;
using Microsoft.Win32;
using System.Diagnostics;

namespace GHelper.Gpu
{
    public class GPUModeControl
    {
        public const int PendingModeNone = 0;
        public const int PendingModeEco = 1;
        public const int PendingModeUltimate = 2;

        private const string PendingModeKey = "gpu_pending_mode";
        private const string PendingCreatedAtKey = "gpu_pending_created_at";

        SettingsForm settings;

        public static int gpuMode;
        public static bool? gpuExists = null;

        private bool shutdownFixRestoreDone;


        public GPUModeControl(SettingsForm settingsForm)
        {
            settings = settingsForm;
        }

        public void InitGPUMode()
        {
            if (AppConfig.NoGpu())
            {
                settings.HideGPUModes(false);
                return;
            }

            int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);

            Logger.WriteLine("Eco flag : " + eco);
            Logger.WriteLine("Mux flag : " + mux);

            settings.VisualiseGPUButtons(eco >= 0, mux >= 0);

            if (mux == 0)
            {
                gpuMode = AsusACPI.GPUModeUltimate;
            }
            else
            {
                if (eco == 1)
                    gpuMode = AsusACPI.GPUModeEco;
                else
                    gpuMode = AsusACPI.GPUModeStandard;

                // GPU mode not supported
                if (eco < 0 && mux < 0)
                {
                    if (gpuExists is null) gpuExists = Program.acpi.GetFan(AsusFan.GPU) >= 0;
                    settings.HideGPUModes((bool)gpuExists);
                }
            }

            AppConfig.Set("gpu_mode", gpuMode);
            ClearPendingModeIfAlreadyActive(gpuMode);
            settings.VisualiseGPUMode(gpuMode);

            Aura.CustomRGB.ApplyGPUColor(gpuMode);

        }


        public void RestoreRememberedModeAfterShutdownFix(int rememberedGpuMode)
        {
            if (shutdownFixRestoreDone) return;
            shutdownFixRestoreDone = true;

            if (!AppConfig.IsGPUFix()) return;
            if (AppConfig.Is("gpu_auto")) return;
            if (AppConfig.IsForceSetGPUMode()) return;
            if (rememberedGpuMode != AsusACPI.GPUModeEco) return;
            if (AppConfig.IsAlwaysUltimate()) return;
            if (AppConfig.NoGpu()) return;

            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);
            if (mux == 0) return;

            int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
            if (eco != 0) return;

            if (Program.acpi.IsXGConnected()) return;

            Logger.WriteLine("Restoring GPU mode after shutdown fix: Eco");
            SetGPUEco(1);
        }



        public void SetGPUMode(int GPUMode, int auto = 0)
        {

            int CurrentGPU = AppConfig.Get("gpu_mode");
            AppConfig.Set("gpu_auto", auto);

            if (CurrentGPU == GPUMode)
            {
                settings.VisualiseGPUMode();
                return;
            }

            var restart = false;
            var changed = false;

            int status;

            if (CurrentGPU == AsusACPI.GPUModeUltimate)
            {
                DialogResult dialogResult = MessageBox.Show(Properties.Strings.AlertUltimateOff, Properties.Strings.AlertUltimateTitle, MessageBoxButtons.YesNo);
                if (dialogResult == DialogResult.Yes)
                {
                    status = Program.acpi.DeviceSet(AsusACPI.GPUMux, 1, "GPUMux");
                    restart = true;
                    changed = true;
                }
            }
            else if (GPUMode == AsusACPI.GPUModeUltimate)
            {
                DialogResult dialogResult = MessageBox.Show(Properties.Strings.AlertUltimateOn, Properties.Strings.AlertUltimateTitle, MessageBoxButtons.YesNo);
                if (dialogResult == DialogResult.Yes)
                {
                    if (AppConfig.NoAutoUltimate())
                    {
                        Program.acpi.SetGPUEco(0);
                        Thread.Sleep(500);

                        int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
                        Logger.WriteLine("Eco flag : " + eco);
                        if (eco == 1)
                        {
                            settings.VisualiseGPUMode();
                            return;
                        }
                    }
                    status = Program.acpi.DeviceSet(AsusACPI.GPUMux, 0, "GPUMux");
                    restart = true;
                    changed = true;
                }

            }
            else if (GPUMode == AsusACPI.GPUModeEco)
            {
                settings.VisualiseGPUMode(GPUMode);
                SetGPUEco(1);
                changed = true;
            }
            else if (GPUMode == AsusACPI.GPUModeStandard)
            {
                settings.VisualiseGPUMode(GPUMode);
                SetGPUEco(0);
                changed = true;
            }

            if (changed)
            {
                AppConfig.Set("gpu_mode", GPUMode);
                ClearPendingGpuMode();
            }

            if (restart)
            {
                settings.VisualiseGPUMode();
                Process.Start("shutdown", "/r /t 1");
            }

        }



        public int GetPendingGpuMode()
        {
            int pendingMode = AppConfig.Get(PendingModeKey, PendingModeNone);
            return pendingMode switch
            {
                PendingModeEco => PendingModeEco,
                PendingModeUltimate => PendingModeUltimate,
                _ => PendingModeNone
            };
        }

        public void SchedulePendingGpuMode(int gpuMode)
        {
            int pendingMode = ToPendingMode(gpuMode);
            if (pendingMode == PendingModeNone) return;

            if (!IsPendingModeSupported(pendingMode))
            {
                string unsupportedTemplate = Properties.Strings.ResourceManager.GetString("PendingGpuSwitchUnsupported", Properties.Strings.Culture) ?? "This GPU mode is not supported on this device.";
                Program.toast.RunToast(unsupportedTemplate);
                return;
            }

            AppConfig.Set(PendingModeKey, pendingMode);
            AppConfig.Set(PendingCreatedAtKey, (int)DateTimeOffset.Now.ToUnixTimeSeconds());

            settings.VisualiseGPUMode();

            string scheduledTemplate = Properties.Strings.ResourceManager.GetString("PendingGpuSwitchScheduled", Properties.Strings.Culture) ?? "Scheduled: {0}. It will apply after your next shutdown.";
            Program.toast.RunToast(FormatPendingMessage(scheduledTemplate, pendingMode));
        }

        public bool ApplyPendingGpuModeOnShutdown()
        {
            int pendingMode = GetPendingGpuMode();
            if (pendingMode == PendingModeNone) return false;

            Logger.WriteLine("Pending GPU mode found on shutdown: " + pendingMode);

            bool applied = pendingMode switch
            {
                PendingModeEco => ApplyPendingEcoMode(),
                PendingModeUltimate => ApplyPendingUltimateMode(),
                _ => false
            };

            if (applied)
            {
                ClearPendingGpuMode();
                string appliedTemplate = Properties.Strings.ResourceManager.GetString("PendingGpuSwitchApplied", Properties.Strings.Culture) ?? "Pending GPU mode applied: {0}.";
                Logger.WriteLine(FormatPendingMessage(appliedTemplate, pendingMode));
            }
            else
            {
                string failedTemplate = Properties.Strings.ResourceManager.GetString("PendingGpuSwitchFailed", Properties.Strings.Culture) ?? "Failed to apply pending GPU mode: {0}.";
                Logger.WriteLine(FormatPendingMessage(failedTemplate, pendingMode));
            }

            return true;
        }

        private static int ToPendingMode(int gpuMode)
        {
            return gpuMode switch
            {
                AsusACPI.GPUModeEco => PendingModeEco,
                AsusACPI.GPUModeUltimate => PendingModeUltimate,
                _ => PendingModeNone
            };
        }

        private static int ToGpuMode(int pendingMode)
        {
            return pendingMode switch
            {
                PendingModeEco => AsusACPI.GPUModeEco,
                PendingModeUltimate => AsusACPI.GPUModeUltimate,
                _ => -1
            };
        }

        private bool IsPendingModeSupported(int pendingMode)
        {
            return pendingMode switch
            {
                PendingModeEco => Program.acpi.DeviceGet(AsusACPI.GPUEco) >= 0,
                PendingModeUltimate => Program.acpi.DeviceGet(AsusACPI.GPUMux) >= 0,
                _ => false
            };
        }

        private bool ApplyPendingEcoMode()
        {
            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);
            if (mux == 0)
            {
                int muxStatus = Program.acpi.DeviceSet(AsusACPI.GPUMux, 1, "GPUMux");
                if (muxStatus < 0 && Program.acpi.DeviceGet(AsusACPI.GPUMux) == 0)
                {
                    return false;
                }
                Thread.Sleep(500);
            }

            if (Program.acpi.DeviceGet(AsusACPI.GPUEco) < 0) return false;

            if (Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1) return true;

            int status = Program.acpi.SetGPUEco(1);
            return status >= 0 || Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1;
        }

        private bool ApplyPendingUltimateMode()
        {
            if (Program.acpi.DeviceGet(AsusACPI.GPUMux) < 0) return false;

            if (Program.acpi.DeviceGet(AsusACPI.GPUMux) == 0) return true;

            if (AppConfig.NoAutoUltimate())
            {
                Program.acpi.SetGPUEco(0);
                Thread.Sleep(500);
                if (Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1)
                {
                    return false;
                }
            }

            int status = Program.acpi.DeviceSet(AsusACPI.GPUMux, 0, "GPUMux");
            return status >= 0 || Program.acpi.DeviceGet(AsusACPI.GPUMux) == 0;
        }

        private void ClearPendingGpuMode()
        {
            if (AppConfig.Get(PendingModeKey, PendingModeNone) == PendingModeNone) return;

            AppConfig.Set(PendingModeKey, PendingModeNone);
            AppConfig.Remove(PendingCreatedAtKey);
        }

        private void ClearPendingModeIfAlreadyActive(int currentGpuMode)
        {
            int pendingMode = GetPendingGpuMode();
            if (pendingMode == PendingModeNone) return;

            if (currentGpuMode != ToGpuMode(pendingMode)) return;

            Logger.WriteLine("Pending GPU mode is already active, clearing pending mode.");
            ClearPendingGpuMode();
        }

        private static string FormatPendingMessage(string template, int pendingMode)
        {
            string modeName = pendingMode switch
            {
                PendingModeEco => Properties.Strings.EcoMode,
                PendingModeUltimate => Properties.Strings.UltimateMode,
                _ => Properties.Strings.GPUMode
            };

            return template.Replace("{0}", modeName);
        }

        public void SetGPUEco(int eco)
        {

            settings.LockGPUModes();

            Task.Run(async () =>
            {

                int status = 1;

                if (eco == 1)
                {
                    HardwareControl.KillGPUApps();
                    if (AppConfig.IsNVPlatform()) NvidiaGpuControl.StopNVService();
                }

                Logger.WriteLine($"Running eco command {eco}");

                try
                {

                    status = Program.acpi.SetGPUEco(eco);
                    await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("refresh_delay", 500)));

                    settings.Invoke(delegate
                    {
                        InitGPUMode();
                        ScreenControl.AutoScreen();
                    });

                    if (eco == 0)
                    {
                        if (AppConfig.IsNVPlatform())
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("nv_delay", 5000)));
                            NvidiaGpuControl.RestartNVService();
                            await Task.Delay(TimeSpan.FromMilliseconds(1000));
                        } else
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(3000));
                        }
                        HardwareControl.RecreateGpuControl();
                        Program.modeControl.SetGPUClocks(false);
                    }

                    if (AppConfig.IsModeReapplyRequired())
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(3000));
                        Program.modeControl.AutoPerformance();
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Error setting GPU Eco: " + ex.Message);
                }

            });


        }

        public static bool IsPlugged()
        {
            if (SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online) return false;
            if (!AppConfig.Is("optimized_usbc")) return true;

            if (AppConfig.ContainsModel("FA507")) Thread.Sleep(1000);

            int chargerMode = Program.acpi.DeviceGet(AsusACPI.ChargerMode);
            Logger.WriteLine("ChargerStatus: " + chargerMode);

            if (chargerMode <= 0) return true;
            return (chargerMode & AsusACPI.ChargerBarrel) > 0;

        }

        public bool AutoGPUMode(bool optimized = false, int delay = 0)
        {

            bool GpuAuto = AppConfig.Is("gpu_auto");
            bool ForceGPU = AppConfig.IsForceSetGPUMode() && !GpuAuto;

            int GpuMode = AppConfig.Get("gpu_mode");

            if (!GpuAuto && !ForceGPU) return false;

            int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);

            if (mux == 0)
            {
                if (optimized) SetGPUMode(AsusACPI.GPUModeStandard, 1);
                return false;
            }
            else
            {

                if (eco == 1)
                    if ((GpuAuto && IsPlugged()) || (ForceGPU && GpuMode == AsusACPI.GPUModeStandard))
                    {
                        if (delay > 0) Thread.Sleep(delay);
                        SetGPUEco(0);
                        return true;
                    }
                if (eco == 0)
                    if ((GpuAuto && !IsPlugged()) || (ForceGPU && GpuMode == AsusACPI.GPUModeEco))
                    {

                        if (Program.acpi.IsXGConnected()) return false;
                        if (HardwareControl.IsUsedGPU())
                        {
                            DialogResult dialogResult = MessageBox.Show(Properties.Strings.AlertDGPU, Properties.Strings.AlertDGPUTitle, MessageBoxButtons.YesNo);
                            if (dialogResult == DialogResult.No) return false;
                        }

                        if (delay > 0) Thread.Sleep(delay);
                        SetGPUEco(1);
                        return true;
                    }
            }

            return false;

        }


        public void InitXGM()
        {
            if (Program.acpi.IsXGConnected())
            {
                //Program.acpi.DeviceSet(AsusACPI.GPUXGInit, 1, "XG Init");
                XGM.Init();
            }

        }

        public void ToggleXGM(bool silent = false)
        {

            Task.Run(async () =>
            {
                settings.LockGPUModes();

                if (Program.acpi.DeviceGet(AsusACPI.GPUXG) == 1)
                {
                    XGM.Reset();
                    HardwareControl.KillGPUApps();

                    if (silent)
                    {
                        Program.acpi.DeviceSet(AsusACPI.GPUXG, 0, "GPU XGM");
                        await Task.Delay(TimeSpan.FromSeconds(15));
                    }
                    else
                    {
                        DialogResult dialogResult = MessageBox.Show("Did you close all applications running on XG Mobile?", "Disabling XG Mobile", MessageBoxButtons.YesNo);
                        if (dialogResult == DialogResult.Yes)
                        {
                            Program.acpi.DeviceSet(AsusACPI.GPUXG, 0, "GPU XGM");
                            await Task.Delay(TimeSpan.FromSeconds(15));
                        }
                    }
                }
                else
                {

                    if (AppConfig.Is("xgm_special"))
                        Program.acpi.DeviceSet(AsusACPI.GPUXG, 0x101, "GPU XGM");
                    else
                        Program.acpi.DeviceSet(AsusACPI.GPUXG, 1, "GPU XGM");

                    InitXGM();
                    XGM.Light(AppConfig.Is("xmg_light"));

                    await Task.Delay(TimeSpan.FromSeconds(15));

                    if (AppConfig.IsMode("auto_apply"))
                        XGM.SetFan(AppConfig.GetFanConfig(AsusFan.XGM));

                    HardwareControl.RecreateGpuControl();

                }

                settings.Invoke(delegate
                {
                    InitGPUMode();
                });
            });
        }

        public void KillGPUApps()
        {
            if (HardwareControl.GpuControl is not null)
            {
                HardwareControl.GpuControl.KillGPUApps();
            }
        }

        public static bool IsHibernationEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("HibernateEnabled");
                        if (value is int intValue)
                        {
                            return intValue != 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Error checking hibernation status: " + ex.Message);
            }
            return true;
        }


        // Manually forcing standard mode on shutdown/hibernate for some exotic cases
        // https://github.com/seerge/g-helper/pull/855 
        public void StandardModeFix(bool hibernate = false)
        {
            if (!AppConfig.IsGPUFix()) return; // No config entry
            if (Program.acpi.DeviceGet(AsusACPI.GPUMux) == 0) return; // Ultimate mode
            if (hibernate && !IsHibernationEnabled()) return;

            Logger.WriteLine("Forcing Standard Mode on " + (hibernate ? "hibernation" : "shutdown"));
            Program.acpi.SetGPUEco(0);
        }

    }
}
