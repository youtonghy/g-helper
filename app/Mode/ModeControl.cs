using GHelper.Gpu.NVidia;
using GHelper.Helpers;
using GHelper.USB;
using Ryzen;

namespace GHelper.Mode
{
    public class ModeControl
    {

        static SettingsForm settings = Program.settingsForm;

        private static bool customFans = false;
        private static int customPower = 0;
        private static bool customTemp = false;

        private int _cpuUV = 0;
        private int _igpuUV = 0;
        private bool _ryzenPower = false;
        private static long _lastWakeReapply = 0;
        private static int _screenOffFanKeepAliveEnabled = 0;
        private static int _screenOffFanReapplyRunning = 0;

        private const int WakeReapplyDelayMs = 3000;
        private const int WakeReapplyCooldownMs = 10000;
        private const int ScreenOffFanKeepAliveIntervalMs = 60_000;
        private const int ScreenOffFanInitialDelayMs = 3000;
        private const int FanRetryDelayMs = 250;

        static System.Timers.Timer reapplyTimer = default!;
        static System.Timers.Timer modeToggleTimer = default!;
        static System.Timers.Timer screenOffFanKeepAliveTimer = default!;

        private static readonly SemaphoreSlim modeApplySemaphore = new(1, 1);
        private static long modeApplySequence;

        private sealed class ModeApplySnapshot
        {
            public long SequenceId { get; init; }
            public int Mode { get; init; }
            public int OldMode { get; init; }
            public int ModeBase { get; init; }
            public int OldModeBase { get; init; }
            public string ModeName { get; init; } = "";
            public bool ResetRequired { get; init; }
            public bool StatusModeEnabled { get; init; }
            public bool ManualModeRequired { get; init; }
            public bool AutoApplyFans { get; init; }
            public bool AutoApplyPower { get; init; }
            public bool ForceApplyFans { get; init; }
            public bool AutoUV { get; init; }
            public bool MidFanEnabled { get; init; }
            public bool XgmFanEnabled { get; init; }
            public bool FanRequired { get; init; }
            public bool PowerRequired { get; init; }
            public int AutoBoost { get; init; }
            public int LimitTotal { get; init; }
            public int LimitCpu { get; init; }
            public int LimitSlow { get; init; }
            public int LimitFast { get; init; }
            public int GpuPower { get; init; }
            public int GpuBoost { get; init; }
            public int GpuTemp { get; init; }
            public int GpuCore { get; init; }
            public int GpuMemory { get; init; }
            public int GpuClockLimit { get; init; }
            public int CpuTemp { get; init; }
            public int CpuUV { get; init; }
            public int IgpuUV { get; init; }
            public string? PowerMode { get; init; }
            public string? ModeCommand { get; init; }
            public byte[] CpuFanCurve { get; init; } = [];
            public byte[] GpuFanCurve { get; init; } = [];
            public byte[] MidFanCurve { get; init; } = [];
            public byte[] XgmFanCurve { get; init; } = [];
        }

        public ModeControl()
        {
            reapplyTimer = new System.Timers.Timer(AppConfig.GetMode("reapply_time", 30) * 1000);
            reapplyTimer.Enabled = false;
            reapplyTimer.Elapsed += ReapplyTimer_Elapsed;

            screenOffFanKeepAliveTimer = new System.Timers.Timer(ScreenOffFanKeepAliveIntervalMs);
            screenOffFanKeepAliveTimer.Enabled = false;
            screenOffFanKeepAliveTimer.Elapsed += ScreenOffFanKeepAliveTimer_Elapsed;
        }


        private void ReapplyTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            SetCPUTemp(AppConfig.GetMode("cpu_temp"));
            SetRyzenPower();
        }

        private static (bool shouldApply, bool forceApply) GetScreenOffFanKeepAliveApplyState()
        {
            bool applyFans = AppConfig.IsMode("auto_apply");
            bool applyPowerFans = AppConfig.IsMode("auto_apply_power") && AppConfig.IsFanRequired();
            bool shouldApply = applyFans || applyPowerFans;
            bool forceApply = !applyFans && applyPowerFans;
            return (shouldApply, forceApply);
        }

        private bool StopScreenOffFanKeepAlive(string reason)
        {
            bool wasEnabled = Interlocked.Exchange(ref _screenOffFanKeepAliveEnabled, 0) == 1;
            screenOffFanKeepAliveTimer.Stop();

            if (wasEnabled)
            {
                Logger.WriteLine($"Screen-off fan keep-alive stopped: {reason}");
            }

            return wasEnabled;
        }

        private void ScreenOffFanKeepAliveTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            ReapplyFansForScreenOffKeepAlive("interval");
        }

        private void ReapplyFansForScreenOffKeepAlive(string source)
        {
            if (Interlocked.CompareExchange(ref _screenOffFanReapplyRunning, 1, 0) == 1)
            {
                Logger.WriteLine($"Screen-off fan keep-alive skipped ({source}): reapply already running");
                return;
            }

            try
            {
                if (Interlocked.CompareExchange(ref _screenOffFanKeepAliveEnabled, 0, 0) == 0)
                {
                    Logger.WriteLine($"Screen-off fan keep-alive skipped ({source}): keep-alive not active");
                    return;
                }

                var state = GetScreenOffFanKeepAliveApplyState();
                if (!state.shouldApply)
                {
                    Logger.WriteLine($"Screen-off fan keep-alive stopped ({source}): custom fan apply no longer enabled");
                    StopScreenOffFanKeepAlive("apply conditions changed");
                    return;
                }

                Logger.WriteLine($"Screen-off fan keep-alive reapply ({source}, force={state.forceApply})");
                AutoFans(state.forceApply);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Screen-off fan keep-alive error: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _screenOffFanReapplyRunning, 0);
            }
        }

        public bool SetScreenOffFanKeepAlive(bool isScreenOff)
        {
            if (isScreenOff)
            {
                var state = GetScreenOffFanKeepAliveApplyState();
                if (!state.shouldApply)
                {
                    Logger.WriteLine("Screen-off fan keep-alive not started: custom fan apply not enabled");
                    return false;
                }

                if (Interlocked.CompareExchange(ref _screenOffFanKeepAliveEnabled, 1, 0) == 1)
                {
                    Logger.WriteLine("Screen-off fan keep-alive already active");
                    return true;
                }

                Logger.WriteLine("Screen-off fan keep-alive started");
                screenOffFanKeepAliveTimer.Start();

                Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(ScreenOffFanInitialDelayMs));
                    ReapplyFansForScreenOffKeepAlive("initial");
                });

                return true;
            }

            return StopScreenOffFanKeepAlive("monitor power on");
        }

        public void AutoPerformance(bool powerChanged = false)
        {
            var Plugged = SystemInformation.PowerStatus.PowerLineStatus;

            int mode = AppConfig.Get("performance_" + (int)Plugged);

            if (mode != -1)
                SetPerformanceMode(mode, powerChanged);
            else
                SetPerformanceMode(Modes.GetCurrent());
        }

        public void ReapplyCurrentModeAfterWake()
        {
            bool shouldReapply = AppConfig.IsModeReapplyRequired()
                || AppConfig.IsMode("auto_apply")
                || (AppConfig.IsMode("auto_apply_power") && AppConfig.IsFanRequired());

            if (!shouldReapply) return;

            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            long lastReapply = Interlocked.Read(ref _lastWakeReapply);

            if (now - lastReapply < WakeReapplyCooldownMs)
            {
                Logger.WriteLine("Wake reapply skipped due to cooldown");
                return;
            }

            Interlocked.Exchange(ref _lastWakeReapply, now);
            int currentMode = Modes.GetCurrent();

            Logger.WriteLine($"Wake reapply scheduled for mode {currentMode}");
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(WakeReapplyDelayMs));
                Logger.WriteLine($"Wake reapply executing for mode {currentMode}");
                SetPerformanceMode(currentMode);
            });
        }

        private static bool IsCurrentModeApply(long sequenceId)
        {
            return sequenceId == Interlocked.Read(ref modeApplySequence);
        }

        private static void LogSkippedModeApply(ModeApplySnapshot snapshot, string stage)
        {
            Logger.WriteLine($"Mode apply skipped ({stage}): seq={snapshot.SequenceId}, mode={snapshot.Mode}, latest={Interlocked.Read(ref modeApplySequence)}");
        }

        private static ModeApplySnapshot CreateModeApplySnapshot(int mode, int oldMode)
        {
            bool autoApplyFans = AppConfig.IsMode("auto_apply", mode);
            bool autoApplyPower = AppConfig.IsMode("auto_apply_power", mode);
            bool fanRequired = AppConfig.IsFanRequired();

            return new ModeApplySnapshot
            {
                SequenceId = Interlocked.Increment(ref modeApplySequence),
                Mode = mode,
                OldMode = oldMode,
                ModeBase = Modes.GetBase(mode),
                OldModeBase = Modes.GetBase(oldMode),
                ModeName = Modes.GetName(mode),
                StatusModeEnabled = AppConfig.Is("status_mode"),
                AutoApplyFans = autoApplyFans,
                AutoApplyPower = autoApplyPower,
                AutoUV = AppConfig.IsMode("auto_uv", mode),
                MidFanEnabled = AppConfig.Is("mid_fan"),
                XgmFanEnabled = AppConfig.Is("xgm_fan"),
                FanRequired = fanRequired,
                PowerRequired = AppConfig.IsPowerRequired(),
                ForceApplyFans = autoApplyFans || (autoApplyPower && fanRequired),
                AutoBoost = AppConfig.GetMode("auto_boost", mode, -1),
                LimitTotal = AppConfig.GetMode("limit_total", mode, -1),
                LimitCpu = AppConfig.GetMode("limit_cpu", mode, -1),
                LimitSlow = AppConfig.GetMode("limit_slow", mode, -1),
                LimitFast = AppConfig.GetMode("limit_fast", mode, -1),
                GpuPower = AppConfig.GetMode("gpu_power", mode, -1),
                GpuBoost = AppConfig.GetMode("gpu_boost", mode, -1),
                GpuTemp = AppConfig.GetMode("gpu_temp", mode, -1),
                GpuCore = AppConfig.GetMode("gpu_core", mode, -1),
                GpuMemory = AppConfig.GetMode("gpu_memory", mode, -1),
                GpuClockLimit = AppConfig.GetMode("gpu_clock_limit", mode, -1),
                CpuTemp = AppConfig.GetMode("cpu_temp", mode, -1),
                CpuUV = AppConfig.GetMode("cpu_uv", mode, 0),
                IgpuUV = AppConfig.GetMode("igpu_uv", mode, 0),
                PowerMode = AppConfig.GetModeString("powermode", mode),
                ModeCommand = AppConfig.GetModeString("mode_command", mode),
                CpuFanCurve = AppConfig.GetFanConfig(AsusFan.CPU, mode),
                GpuFanCurve = AppConfig.GetFanConfig(AsusFan.GPU, mode),
                MidFanCurve = AppConfig.GetFanConfig(AsusFan.Mid, mode),
                XgmFanCurve = AppConfig.GetFanConfig(AsusFan.XGM, mode),
                ResetRequired = AppConfig.IsResetRequired() && (Modes.GetBase(oldMode) == Modes.GetBase(mode)) && customPower > 0 && !autoApplyPower,
                ManualModeRequired = autoApplyPower && (AppConfig.Is("manual_mode") || AppConfig.ContainsModel("G733")),
            };
        }

        private async Task ApplyPerformanceModeAsync(ModeApplySnapshot snapshot)
        {
            await modeApplySemaphore.WaitAsync();
            try
            {
                if (!IsCurrentModeApply(snapshot.SequenceId))
                {
                    LogSkippedModeApply(snapshot, "before-start");
                    return;
                }

                Logger.WriteLine($"Mode apply start: seq={snapshot.SequenceId}, mode={snapshot.Mode}, base={snapshot.ModeBase}");

                customFans = false;
                customPower = 0;
                customTemp = false;

                SetModeLabel(snapshot);

                if (snapshot.ResetRequired)
                {
                    Program.acpi.DeviceSet(AsusACPI.PerformanceMode, snapshot.OldModeBase != 1 ? AsusACPI.PerformanceTurbo : AsusACPI.PerformanceBalanced, "ModeReset");
                    await Task.Delay(TimeSpan.FromMilliseconds(1500));

                    if (!IsCurrentModeApply(snapshot.SequenceId))
                    {
                        LogSkippedModeApply(snapshot, "after-reset");
                        return;
                    }
                }

                if (snapshot.StatusModeEnabled)
                    Program.acpi.DeviceSet(AsusACPI.StatusMode, [0x00, snapshot.ModeBase == AsusACPI.PerformanceSilent ? (byte)0x02 : (byte)0x03], "StatusMode");

                int status = Program.acpi.DeviceSet(AsusACPI.PerformanceMode, snapshot.ManualModeRequired ? AsusACPI.PerformanceManual : snapshot.ModeBase, "Mode");
                if (status != 1)
                    Program.acpi.SetVivoMode(snapshot.ModeBase);

                if (!IsCurrentModeApply(snapshot.SequenceId))
                {
                    LogSkippedModeApply(snapshot, "after-mode");
                    return;
                }

                ApplyGPUClocks(snapshot);

                await Task.Delay(TimeSpan.FromMilliseconds(100));
                if (!IsCurrentModeApply(snapshot.SequenceId))
                {
                    LogSkippedModeApply(snapshot, "before-fans");
                    return;
                }

                AutoFans(snapshot, source: "mode-apply");

                await Task.Delay(TimeSpan.FromMilliseconds(1000));
                if (!IsCurrentModeApply(snapshot.SequenceId))
                {
                    LogSkippedModeApply(snapshot, "before-power");
                    return;
                }

                AutoPower(snapshot);

                if (!IsCurrentModeApply(snapshot.SequenceId))
                {
                    LogSkippedModeApply(snapshot, "after-power");
                    return;
                }

                if (snapshot.ModeCommand is not null)
                {
                    Logger.WriteLine("Running mode command: " + snapshot.ModeCommand);
                    RestrictedProcessHelper.RunAsRestrictedUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/C " + snapshot.ModeCommand);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Mode apply error: seq={snapshot.SequenceId}, mode={snapshot.Mode}, error={ex}");
            }
            finally
            {
                modeApplySemaphore.Release();
            }
        }


        public void ResetPerformanceMode()
        {
            ResetRyzen();

            Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Modes.GetCurrentBase(), "Mode");

            // Default power mode
            AppConfig.RemoveMode("powermode");
            PowerNative.SetPowerMode(Modes.GetCurrentBase());
        }

        public void Toast()
        {
            Program.toast.RunToast(Modes.GetCurrentName(), SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online ? ToastIcon.Charger : ToastIcon.Battery);
        }

        public void SetPerformanceMode(int mode = -1, bool notify = false)
        {

            int oldMode = Modes.GetCurrent();
            if (mode < 0) mode = oldMode;

            if (!Modes.Exists(mode)) mode = 0;

            settings.ShowMode(mode);

            Modes.SetCurrent(mode);

            var snapshot = CreateModeApplySnapshot(mode, oldMode);

            Task.Run(() => ApplyPerformanceModeAsync(snapshot));


            if (AppConfig.Is("xgm_fan")) XGM.Reset();

            if (notify) Toast();

            if (!AppConfig.Is("skip_powermode"))
            {
                // Windows power mode
                if (snapshot.PowerMode is not null)
                    PowerNative.SetPowerMode(snapshot.PowerMode);
                else
                    PowerNative.SetPowerMode(snapshot.ModeBase);

                if (AppConfig.Is("aspm") && PowerNative.GetASPM() > 0) PowerNative.SetASPM(0);
            }

            // CPU Boost setting override
            if (snapshot.AutoBoost != -1)
                    PowerNative.SetCPUBoost(snapshot.AutoBoost);

            settings.FansInit();
        }


        private void ModeToggleTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            modeToggleTimer.Stop();
            Logger.WriteLine($"Timed mode: {Modes.GetCurrent()}");
            SetPerformanceMode();

        }

        public void CyclePerformanceMode(bool back = false)
        {
            int delay = AppConfig.Get("mode_delay");
            if (delay > 0)
            {
                if (modeToggleTimer is null)
                {
                    modeToggleTimer = new System.Timers.Timer(delay);
                    modeToggleTimer.Elapsed += ModeToggleTimer_Elapsed;
                }

                modeToggleTimer.Stop();
                modeToggleTimer.Start();
                Modes.SetCurrent(Modes.GetNext(back));
                Toast();
            }
            else
            {
                SetPerformanceMode(Modes.GetNext(back), true);
            }

        }

        public void AutoFans(bool force = false)
        {
            AutoFans(null, force, "direct");
        }

        private void AutoFans(ModeApplySnapshot? snapshot, bool force = false, string source = "direct")
        {
            customFans = false;

            bool shouldApply = force || (snapshot?.ForceApplyFans ?? AppConfig.IsMode("auto_apply"));
            bool shouldApplyPower = snapshot?.AutoApplyPower ?? AppConfig.IsMode("auto_apply_power");
            bool powerRequired = snapshot?.PowerRequired ?? AppConfig.IsPowerRequired();

            if (!shouldApply && !force)
            {
                SetModeLabel(snapshot);
                return;
            }

            bool xgmFan = false;
            if (snapshot?.XgmFanEnabled ?? AppConfig.Is("xgm_fan"))
            {
                byte[] xgmCurve = snapshot?.XgmFanCurve ?? AppConfig.GetFanConfig(AsusFan.XGM);
                XGM.SetFan(xgmCurve);
                xgmFan = Program.acpi.IsXGConnected();
            }

            byte[] cpuCurve = snapshot?.CpuFanCurve ?? AppConfig.GetFanConfig(AsusFan.CPU);
            byte[] gpuCurve = snapshot?.GpuFanCurve ?? AppConfig.GetFanConfig(AsusFan.GPU);
            byte[] midCurve = snapshot?.MidFanCurve ?? AppConfig.GetFanConfig(AsusFan.Mid);
            long sequenceId = snapshot?.SequenceId ?? 0;

            int cpuResult = ApplyFanCurveWithRetry(AsusFan.CPU, cpuCurve, source, sequenceId);
            int gpuResult = ApplyFanCurveWithRetry(AsusFan.GPU, gpuCurve, source, sequenceId);

            int midResult = 1;
            if (snapshot?.MidFanEnabled ?? AppConfig.Is("mid_fan"))
                midResult = ApplyFanCurveWithRetry(AsusFan.Mid, midCurve, source, sequenceId);

            if (midResult != 1)
                Logger.WriteLine($"Fan curve apply warning: source={source}, seq={sequenceId}, midCurve={midResult}");

            if (cpuResult != 1 || gpuResult != 1)
            {
                int cpuRangeResult = Program.acpi.SetFanRange(AsusFan.CPU, cpuCurve);
                int gpuRangeResult = Program.acpi.SetFanRange(AsusFan.GPU, gpuCurve);

                Logger.WriteLine($"Fan curve fallback: source={source}, seq={sequenceId}, cpuCurve={cpuResult}, cpuRange={cpuRangeResult}, gpuCurve={gpuResult}, gpuRange={gpuRangeResult}, midCurve={midResult}");

                if (cpuRangeResult != 1 || gpuRangeResult != 1)
                {
                    int modeBase = snapshot?.ModeBase ?? Modes.GetCurrentBase();
                    Program.acpi.DeviceSet(AsusACPI.PerformanceMode, modeBase, "Reset Mode");
                    settings.LabelFansResult("Temporary fan curve fallback to range mode");
                }
                else
                {
                    settings.LabelFansResult("Temporary fan curve fallback to range mode");
                }
            }
            else
            {
                settings.LabelFansResult("");
                customFans = true;
            }

            if ((powerRequired || xgmFan) && !shouldApplyPower)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    if (sequenceId > 0 && !IsCurrentModeApply(sequenceId)) return;
                    Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, 80, "PowerLimit Fix A0");
                    Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, 80, "PowerLimit Fix A3");
                });
            }

            SetModeLabel(snapshot);
        }

        private int ApplyFanCurveWithRetry(AsusFan device, byte[] curve, string source, long sequenceId)
        {
            int result = Program.acpi.SetFanCurve(device, curve);
            if (result == 1) return result;

            Logger.WriteLine($"Fan curve retry scheduled: source={source}, seq={sequenceId}, device={device}, result={result}");
            Thread.Sleep(FanRetryDelayMs);

            int retryResult = Program.acpi.SetFanCurve(device, curve);
            if (retryResult != 1)
                Logger.WriteLine($"Fan curve retry failed: source={source}, seq={sequenceId}, device={device}, result={retryResult}");

            return retryResult;
        }

        public void AutoPower(bool launchAsAdmin = false)
        {
            AutoPower(null, launchAsAdmin);
        }

        private void AutoPower(ModeApplySnapshot? snapshot, bool launchAsAdmin = false)
        {
            customPower = 0;

            bool applyPower = snapshot?.AutoApplyPower ?? AppConfig.IsMode("auto_apply_power");
            bool applyFans = snapshot?.AutoApplyFans ?? AppConfig.IsMode("auto_apply");
            bool fanRequired = snapshot?.FanRequired ?? AppConfig.IsFanRequired();

            if (applyPower && !applyFans && fanRequired)
            {
                AutoFans(snapshot, true, "power-prep");
                Thread.Sleep(500);
            }

            if (applyPower) SetPower(snapshot, launchAsAdmin);

            Thread.Sleep(500);
            SetGPUPower(snapshot);
            AutoRyzen(snapshot);
        }

        public void SetModeLabel()
        {
            SetModeLabel(null);
        }

        private void SetModeLabel(ModeApplySnapshot? snapshot)
        {
            string modeName = snapshot?.ModeName ?? Modes.GetCurrentName();
            settings.SetModeLabel(Properties.Strings.PerformanceMode + ": " + modeName + (customFans ? "+" : "") + ((customPower > 0) ? " " + customPower + "W" : ""));
        }

        public void SetRyzenPower(bool init = false)
        {
            SetRyzenPower(null, init);
        }

        private void SetRyzenPower(ModeApplySnapshot? snapshot, bool init = false)
        {
            if (init) _ryzenPower = true;

            if (!_ryzenPower) return;
            if (!RyzenControl.IsRingExsists()) return;
            if (!(snapshot?.AutoApplyPower ?? AppConfig.IsMode("auto_apply_power"))) return;

            int limit_total = snapshot?.LimitTotal ?? AppConfig.GetMode("limit_total");
            int limit_slow = snapshot is not null
                ? (snapshot.LimitSlow < 0 ? limit_total : snapshot.LimitSlow)
                : AppConfig.GetMode("limit_slow", limit_total);

            if (limit_total > AsusACPI.MaxTotal) return;
            if (limit_total < AsusACPI.MinTotal) return;

            var stapmResult = SendCommand.set_stapm_limit((uint)limit_total * 1000);
            if (init) Logger.WriteLine($"STAPM: {limit_total} {stapmResult}");

            var slowResult = SendCommand.set_slow_limit((uint)limit_slow * 1000);
            if (init) Logger.WriteLine($"SLOW: {limit_slow} {slowResult}");

            var fastResult = SendCommand.set_fast_limit((uint)limit_slow * 1000);
            if (init) Logger.WriteLine($"FAST: {limit_slow} {fastResult}");

        }

        public void SetPower(bool launchAsAdmin = false)
        {
            SetPower(null, launchAsAdmin);
        }

        private void SetPower(ModeApplySnapshot? snapshot, bool launchAsAdmin = false)
        {

            bool allAMD = Program.acpi.IsAllAmdPPT();
            bool isAMD = RyzenControl.IsAMD();

            int limit_total = snapshot?.LimitTotal ?? AppConfig.GetMode("limit_total");
            int limit_cpu = snapshot?.LimitCpu ?? AppConfig.GetMode("limit_cpu");
            int limit_slow = snapshot?.LimitSlow ?? AppConfig.GetMode("limit_slow");
            int limit_fast = snapshot?.LimitFast ?? AppConfig.GetMode("limit_fast");

            if (limit_slow < 0 || allAMD) limit_slow = limit_total;

            if (limit_total > AsusACPI.MaxTotal) return;
            if (limit_total < AsusACPI.MinTotal) return;

            if (limit_cpu > AsusACPI.MaxCPU) return;
            if (limit_cpu < AsusACPI.MinCPU) return;

            if (limit_fast > AsusACPI.MaxTotal) return;
            if (limit_fast < AsusACPI.MinTotal) return;

            if (limit_slow > AsusACPI.MaxTotal) return;
            if (limit_slow < AsusACPI.MinTotal) return;

            // SPL and SPPT 
            if (Program.acpi.DeviceGet(AsusACPI.PPT_APUA0) >= 0)
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, limit_total, "PowerLimit A3");
                Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, limit_slow, "PowerLimit A0");
                customPower = limit_total;
            }
            else if (isAMD)
            {

                if (ProcessHelper.IsUserAdministrator())
                {
                    SetRyzenPower(snapshot, true);
                }
                else if (launchAsAdmin)
                {
                    ProcessHelper.RunAsAdmin("cpu");
                    return;
                }
            }

            if (Program.acpi.IsAllAmdPPT()) // CPU limit all amd models
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_CPUB0, limit_cpu, "PowerLimit B0");
                customPower = limit_cpu;
            }
            else if (isAMD && Program.acpi.DeviceGet(AsusACPI.PPT_APUC1) >= 0) // FPPT boost for non all-amd models
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_APUC1, limit_fast, "PowerLimit C1");
            }


            SetModeLabel(snapshot);

        }

        public void SetGPUClocks(bool launchAsAdmin = true, bool reset = false)
        {
            Task.Run(() =>
            {
                ApplyGPUClocks(null, launchAsAdmin, reset);
            });
        }

        private void ApplyGPUClocks(ModeApplySnapshot? snapshot, bool launchAsAdmin = true, bool reset = false)
        {
            int core = snapshot?.GpuCore ?? AppConfig.GetMode("gpu_core");
            int memory = snapshot?.GpuMemory ?? AppConfig.GetMode("gpu_memory");
            int clockLimit = snapshot?.GpuClockLimit ?? AppConfig.GetMode("gpu_clock_limit");

            if (reset) core = memory = clockLimit = 0;
            if (core == -1 && memory == -1 && clockLimit == -1) return;

            if (Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1) { Logger.WriteLine("Clocks: Eco"); return; }
            if (HardwareControl.GpuControl is null) { Logger.WriteLine("Clocks: NoGPUControl"); return; }
            if (!HardwareControl.GpuControl!.IsNvidia) { Logger.WriteLine("Clocks: NotNvidia"); return; }

            using NvidiaGpuControl nvControl = (NvidiaGpuControl)HardwareControl.GpuControl;
            try
            {
                int statusLimit = nvControl.SetMaxGPUClock(clockLimit);
                int statusClocks = nvControl.SetClocks(core, memory);
                if ((statusLimit != 0 || statusClocks != 0) && launchAsAdmin) ProcessHelper.RunAsAdmin("gpu");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Clocks Error:" + ex.ToString());
            }

            settings.GPUInit();
        }

        public void SetGPUPower()
        {
            SetGPUPower(null);
        }

        private void SetGPUPower(ModeApplySnapshot? snapshot)
        {
            int gpu_boost = snapshot?.GpuBoost ?? AppConfig.GetMode("gpu_boost");
            int gpu_temp = snapshot?.GpuTemp ?? AppConfig.GetMode("gpu_temp");
            int gpu_power = snapshot?.GpuPower ?? AppConfig.GetMode("gpu_power");

            int boostResult = -1;

            if (gpu_power >= AsusACPI.MinGPUPower && gpu_power <= AsusACPI.MaxGPUPower && Program.acpi.DeviceGet(AsusACPI.GPU_POWER) >= 0)
                Program.acpi.DeviceSet(AsusACPI.GPU_POWER, gpu_power, "PowerLimit TGP (GPU VAR)");

            if (gpu_boost >= AsusACPI.MinGPUBoost && gpu_boost <= AsusACPI.MaxGPUBoost && Program.acpi.DeviceGet(AsusACPI.PPT_GPUC0) >= 0)
                boostResult = Program.acpi.DeviceSet(AsusACPI.PPT_GPUC0, gpu_boost, "PowerLimit C0 (GPU BOOST)");

            if (gpu_temp >= AsusACPI.MinGPUTemp && gpu_temp <= AsusACPI.MaxGPUTemp && Program.acpi.DeviceGet(AsusACPI.PPT_GPUC2) >= 0)
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC2, gpu_temp, "PowerLimit C2 (GPU TEMP)");

            // Fallback
            if (boostResult == 0)
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC0, gpu_boost, "PowerLimit C0");

        }

        public void SetCPUTemp(int? cpuTemp, bool init = false)
        {
            if (cpuTemp == RyzenControl.MaxTemp && customTemp)
            {
                cpuTemp = RyzenControl.DefaultTemp;
                Logger.WriteLine($"Custom CPU Temp reset");
            }

            if (cpuTemp >= RyzenControl.MinTemp && cpuTemp < RyzenControl.MaxTemp)
            {
                var resultCPU = SendCommand.set_tctl_temp((uint)cpuTemp);
                if (init) Logger.WriteLine($"CPU Temp: {cpuTemp} {resultCPU}");
                if (resultCPU == Smu.Status.OK) customTemp = cpuTemp != RyzenControl.DefaultTemp;
            }
        }

        public void SetUV(int cpuUV)
        {
            if (!RyzenControl.IsSupportedUV()) return;

            if (cpuUV >= RyzenControl.MinCPUUV && cpuUV <= RyzenControl.MaxCPUUV)
            {
                var uvResult = SendCommand.set_coall(cpuUV);
                Logger.WriteLine($"UV: {cpuUV} {uvResult}");
                if (uvResult == Smu.Status.OK) _cpuUV = cpuUV;
            }
        }

        public void SetUViGPU(int igpuUV)
        {
            if (!RyzenControl.IsSupportedUViGPU()) return;

            if (igpuUV >= RyzenControl.MinIGPUUV && igpuUV <= RyzenControl.MaxIGPUUV)
            {
                var iGPUResult = SendCommand.set_cogfx(igpuUV);
                Logger.WriteLine($"iGPU UV: {igpuUV} {iGPUResult}");
                if (iGPUResult == Smu.Status.OK) _igpuUV = igpuUV;
            }
        }


        public void SetRyzen(bool launchAsAdmin = false)
        {
            SetRyzen(null, launchAsAdmin);
        }

        private void SetRyzen(ModeApplySnapshot? snapshot, bool launchAsAdmin = false)
        {
            if (!ProcessHelper.IsUserAdministrator())
            {
                if (launchAsAdmin) ProcessHelper.RunAsAdmin("uv");
                return;
            }

            if (!RyzenControl.IsRingExsists()) return;

            try
            {
                SetUV(snapshot?.CpuUV ?? AppConfig.GetMode("cpu_uv", 0));
                SetUViGPU(snapshot?.IgpuUV ?? AppConfig.GetMode("igpu_uv", 0));
                SetCPUTemp(snapshot?.CpuTemp ?? AppConfig.GetMode("cpu_temp"), true);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("UV Error: " + ex.ToString());
            }

            reapplyTimer.Enabled = snapshot?.AutoUV ?? AppConfig.IsMode("auto_uv");
        }

        public void ResetRyzen()
        {
            if (_cpuUV != 0) SetUV(0);
            if (_igpuUV != 0) SetUViGPU(0);
            reapplyTimer.Enabled = false;
        }

        public void AutoRyzen()
        {
            AutoRyzen(null);
        }

        private void AutoRyzen(ModeApplySnapshot? snapshot)
        {
            if (!RyzenControl.IsAMD()) return;

            if (snapshot?.AutoUV ?? AppConfig.IsMode("auto_uv")) SetRyzen(snapshot);
            else ResetRyzen();
        }

        public void ShutdownReset()
        {
            if (!AppConfig.IsShutdownReset()) return;
            Program.acpi.DeviceSet(AsusACPI.PerformanceMode,AsusACPI.PerformanceBalanced, "Mode Reset");
        }

    }
}
