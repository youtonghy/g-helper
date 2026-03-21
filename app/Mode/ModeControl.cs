using GHelper.Fan;
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
        private static int _screenOffFanKeepAliveReason = (int)Program.KeepAliveReason.None;
        private static int _screenOffFanReapplyRunning = 0;
        private static int _activeFanWatchdogEnabled = 0;
        private static int _activeFanWatchdogRunning = 0;
        private static int _fanRecoveryRunning = 0;
        private static int _cpuFanDriftHits = 0;
        private static int _gpuFanDriftHits = 0;
        private static int _fanRecoveryStage = 0;
        private static long _lastSilentBounceRecovery = 0;

        private const int WakeReapplyDelayMs = 3000;
        private const int WakeReapplyCooldownMs = 10000;
        private const int ScreenOffFanKeepAliveIntervalMs = 60_000;
        private const int ScreenOffFanInitialDelayMs = 3000;
        private const int FanRetryDelayMs = 250;
        private const int FanSelfHealDelayMs = 3000;
        private const int ActiveFanWatchdogIntervalMs = 10_000;
        private const int FanDriftHitThreshold = 3;
        private const int FanDriftDeltaThreshold = 15;
        private const int FanDriftHighFanThreshold = 75;
        private const int SilentBounceCooldownMs = 5 * 60 * 1000;
        private const int SilentBounceDelayMs = 1500;

        static System.Timers.Timer reapplyTimer = default!;
        static System.Timers.Timer modeToggleTimer = default!;
        static System.Timers.Timer screenOffFanKeepAliveTimer = default!;
        static System.Timers.Timer activeFanWatchdogTimer = default!;

        private static readonly SemaphoreSlim modeApplySemaphore = new(1, 1);
        private static long modeApplySequence;
        private static long fanApplySequence;

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

        private sealed class FanApplySnapshot
        {
            public long SequenceId { get; init; }
            public long ModeSequenceId { get; init; }
            public int Mode { get; init; }
            public int ModeBase { get; init; }
            public string ModeName { get; init; } = "";
            public bool ForceApply { get; init; }
            public bool AutoApplyFans { get; init; }
            public bool AutoApplyPower { get; init; }
            public bool MidFanEnabled { get; init; }
            public bool XgmFanEnabled { get; init; }
            public bool FanRequired { get; init; }
            public bool PowerRequired { get; init; }
            public string Source { get; init; } = "direct";
            public Program.KeepAliveReason KeepAliveReason { get; init; } = Program.KeepAliveReason.None;
            public bool AllowSelfHeal { get; init; } = true;
            public byte[] CpuFanCurve { get; init; } = [];
            public byte[] GpuFanCurve { get; init; } = [];
            public byte[] MidFanCurve { get; init; } = [];
            public byte[] XgmFanCurve { get; init; } = [];
        }

        private sealed class ActiveFanWatchdogSnapshot
        {
            public long SequenceId { get; init; }
            public int Mode { get; init; }
            public int ModeBase { get; init; }
            public string ModeName { get; init; } = "";
            public bool ManualModeRequired { get; init; }
            public byte[] CpuFanCurve { get; init; } = [];
            public byte[] GpuFanCurve { get; init; } = [];
        }

        private sealed class ActiveFanWatchdogSample
        {
            public long SequenceId { get; init; }
            public int Mode { get; init; }
            public int ModeBase { get; init; }
            public string ModeName { get; init; } = "";
            public bool ManualModeRequired { get; init; }
            public int CpuTemp { get; init; }
            public int GpuTemp { get; init; }
            public int CpuFan { get; init; }
            public int GpuFan { get; init; }
            public int ExpectedCpuFan { get; init; }
            public int ExpectedGpuFan { get; init; }
        }

        public ModeControl()
        {
            reapplyTimer = new System.Timers.Timer(AppConfig.GetMode("reapply_time", 30) * 1000);
            reapplyTimer.Enabled = false;
            reapplyTimer.Elapsed += ReapplyTimer_Elapsed;

            screenOffFanKeepAliveTimer = new System.Timers.Timer(ScreenOffFanKeepAliveIntervalMs);
            screenOffFanKeepAliveTimer.Enabled = false;
            screenOffFanKeepAliveTimer.Elapsed += ScreenOffFanKeepAliveTimer_Elapsed;

            activeFanWatchdogTimer = new System.Timers.Timer(ActiveFanWatchdogIntervalMs);
            activeFanWatchdogTimer.Enabled = false;
            activeFanWatchdogTimer.Elapsed += ActiveFanWatchdogTimer_Elapsed;

            UpdateActiveFanWatchdogState("init");
        }


        private void ReapplyTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            SetCPUTemp(AppConfig.GetMode("cpu_temp"));
            SetRyzenPower();
        }

        private void ActiveFanWatchdogTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _activeFanWatchdogRunning, 1, 0) == 1)
                return;

            _ = RunActiveFanWatchdogAsync();
        }

        private async Task RunActiveFanWatchdogAsync()
        {
            try
            {
                if (modeApplySemaphore.CurrentCount == 0)
                    return;

                ActiveFanWatchdogSnapshot? snapshot = CreateActiveFanWatchdogSnapshot();
                if (snapshot is null)
                {
                    UpdateActiveFanWatchdogState("tick");
                    return;
                }

                ActiveFanWatchdogSample sample = CaptureActiveFanWatchdogSample(snapshot);
                if (modeApplySemaphore.CurrentCount == 0 || snapshot.SequenceId != Interlocked.Read(ref modeApplySequence))
                    return;

                bool cpuDrift = IsFanDrift(AsusFan.CPU, sample.CpuFan, sample.ExpectedCpuFan);
                bool gpuDrift = IsFanDrift(AsusFan.GPU, sample.GpuFan, sample.ExpectedGpuFan);

                int cpuHits = cpuDrift ? Interlocked.Increment(ref _cpuFanDriftHits) : Interlocked.Exchange(ref _cpuFanDriftHits, 0);
                int gpuHits = gpuDrift ? Interlocked.Increment(ref _gpuFanDriftHits) : Interlocked.Exchange(ref _gpuFanDriftHits, 0);

                if (!cpuDrift && !gpuDrift)
                {
                    if (cpuHits > 0 || gpuHits > 0 || Interlocked.CompareExchange(ref _fanRecoveryStage, 0, 0) > 0)
                    {
                        Logger.WriteLine($"fan-drift-cleared: mode={sample.Mode}, modeBase={sample.ModeBase}, manualRequired={sample.ManualModeRequired}, cpuTemp={sample.CpuTemp}, gpuTemp={sample.GpuTemp}, cpuFan={sample.CpuFan}, gpuFan={sample.GpuFan}, expectedCpu={sample.ExpectedCpuFan}, expectedGpu={sample.ExpectedGpuFan}, seq={sample.SequenceId}");
                    }

                    ResetActiveFanWatchdogCounters();
                    return;
                }

                if (Math.Max(cpuHits, gpuHits) < FanDriftHitThreshold)
                    return;

                Logger.WriteLine($"fan-drift-detected: mode={sample.Mode}, modeBase={sample.ModeBase}, manualRequired={sample.ManualModeRequired}, cpuTemp={sample.CpuTemp}, gpuTemp={sample.GpuTemp}, cpuFan={sample.CpuFan}, gpuFan={sample.GpuFan}, expectedCpu={sample.ExpectedCpuFan}, expectedGpu={sample.ExpectedGpuFan}, seq={sample.SequenceId}, cpuHits={cpuHits}, gpuHits={gpuHits}");
                ResetActiveFanWatchdogCounters(keepRecoveryStage: true);

                int recoveryStage = Interlocked.CompareExchange(ref _fanRecoveryStage, 0, 0);
                if (recoveryStage == 0)
                {
                    Interlocked.Exchange(ref _fanRecoveryStage, 1);
                    await RunFanRecoveryAsync(silentBounce: false, sample);
                    return;
                }

                long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                long lastSilentBounce = Interlocked.Read(ref _lastSilentBounceRecovery);
                if (now - lastSilentBounce < SilentBounceCooldownMs)
                {
                    Logger.WriteLine($"fan-recovery-cooldown-skip: mode={sample.Mode}, modeBase={sample.ModeBase}, manualRequired={sample.ManualModeRequired}, cpuTemp={sample.CpuTemp}, gpuTemp={sample.GpuTemp}, cpuFan={sample.CpuFan}, gpuFan={sample.GpuFan}, expectedCpu={sample.ExpectedCpuFan}, expectedGpu={sample.ExpectedGpuFan}, seq={sample.SequenceId}");
                    return;
                }

                Interlocked.Exchange(ref _fanRecoveryStage, 2);
                Interlocked.Exchange(ref _lastSilentBounceRecovery, now);
                await RunFanRecoveryAsync(silentBounce: true, sample);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("fan-watchdog-error: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _activeFanWatchdogRunning, 0);
            }
        }

        private static bool ShouldEnableActiveFanWatchdog()
        {
            if (!AppConfig.ContainsModel("GA402X"))
                return false;

            if (Interlocked.CompareExchange(ref _screenOffFanKeepAliveEnabled, 0, 0) == 1)
                return false;

            if (Interlocked.CompareExchange(ref _fanRecoveryRunning, 0, 0) == 1)
                return false;

            int currentMode = Modes.GetCurrent();
            if (!AppConfig.IsMode("auto_apply", currentMode))
                return false;

            return AppConfig.GetFanConfig(AsusFan.CPU, currentMode).Length == 16
                && AppConfig.GetFanConfig(AsusFan.GPU, currentMode).Length == 16;
        }

        private static ActiveFanWatchdogSnapshot? CreateActiveFanWatchdogSnapshot()
        {
            if (!ShouldEnableActiveFanWatchdog())
                return null;

            int currentMode = Modes.GetCurrent();
            return new ActiveFanWatchdogSnapshot
            {
                SequenceId = Interlocked.Read(ref modeApplySequence),
                Mode = currentMode,
                ModeBase = Modes.GetBase(currentMode),
                ModeName = Modes.GetName(currentMode),
                ManualModeRequired = AppConfig.IsManualModeRequired(currentMode),
                CpuFanCurve = CloneCurve(AppConfig.GetFanConfig(AsusFan.CPU, currentMode)),
                GpuFanCurve = CloneCurve(AppConfig.GetFanConfig(AsusFan.GPU, currentMode)),
            };
        }

        private static ActiveFanWatchdogSample CaptureActiveFanWatchdogSample(ActiveFanWatchdogSnapshot snapshot)
        {
            int cpuTemp = (int)Math.Round(HardwareControl.GetCPUTemp() ?? -1);
            int gpuTemp = (int)Math.Round(HardwareControl.GetGPUTemp() ?? -1);

            return new ActiveFanWatchdogSample
            {
                SequenceId = snapshot.SequenceId,
                Mode = snapshot.Mode,
                ModeBase = snapshot.ModeBase,
                ModeName = snapshot.ModeName,
                ManualModeRequired = snapshot.ManualModeRequired,
                CpuTemp = cpuTemp,
                GpuTemp = gpuTemp,
                CpuFan = Program.acpi.GetFan(AsusFan.CPU),
                GpuFan = Program.acpi.GetFan(AsusFan.GPU),
                ExpectedCpuFan = GetExpectedFanFromCurve(AsusFan.CPU, snapshot.CpuFanCurve, cpuTemp),
                ExpectedGpuFan = GetExpectedFanFromCurve(AsusFan.GPU, snapshot.GpuFanCurve, gpuTemp),
            };
        }

        private static int GetExpectedFanFromCurve(AsusFan device, byte[] curve, int temperature)
        {
            if (curve.Length != 16 || temperature < 0 || AsusACPI.IsEmptyCurve(curve))
                return -1;

            int expectedPercent = GetExpectedFanPercent(curve, temperature);
            int fanScale = AppConfig.Get("fan_scale", 100);
            expectedPercent = Math.Clamp(expectedPercent * fanScale / 100, 0, 100);

            int minFan = FanSensorControl.GetFanMin(device);
            int maxFan = FanSensorControl.GetFanMax(device);
            return minFan + (maxFan - minFan) * expectedPercent / 100;
        }

        private static int GetExpectedFanPercent(byte[] curve, int temperature)
        {
            if (temperature <= curve[0])
                return curve[8];

            for (int i = 1; i < 8; i++)
            {
                int currentTemp = curve[i];
                if (temperature > currentTemp)
                    continue;

                int previousTemp = curve[i - 1];
                int previousFan = curve[i + 7];
                int currentFan = curve[i + 8];

                if (currentTemp <= previousTemp)
                    return currentFan;

                double position = (double)(temperature - previousTemp) / (currentTemp - previousTemp);
                return (int)Math.Round(previousFan + ((currentFan - previousFan) * position));
            }

            return curve[15];
        }

        private static bool IsFanDrift(AsusFan device, int actualFan, int expectedFan)
        {
            if (actualFan <= 0 || expectedFan < 0)
                return false;

            int minFan = FanSensorControl.GetFanMin(device);
            int maxFan = FanSensorControl.GetFanMax(device);
            int highThreshold = minFan + (maxFan - minFan) * FanDriftHighFanThreshold / 100;
            int driftThreshold = Math.Max(3, (maxFan - minFan) * FanDriftDeltaThreshold / 100);

            return actualFan >= highThreshold && actualFan >= expectedFan + driftThreshold;
        }

        private static void ResetActiveFanWatchdogCounters(bool keepRecoveryStage = false)
        {
            Interlocked.Exchange(ref _cpuFanDriftHits, 0);
            Interlocked.Exchange(ref _gpuFanDriftHits, 0);

            if (!keepRecoveryStage)
                Interlocked.Exchange(ref _fanRecoveryStage, 0);
        }

        private void StopActiveFanWatchdog(string reason, bool keepRecoveryStage = false)
        {
            bool wasEnabled = Interlocked.Exchange(ref _activeFanWatchdogEnabled, 0) == 1;
            activeFanWatchdogTimer.Stop();

            if (wasEnabled)
            {
                Logger.WriteLine($"fan-watchdog-stop: reason={reason}, mode={Modes.GetCurrent()}, seq={Interlocked.Read(ref modeApplySequence)}");
            }

            ResetActiveFanWatchdogCounters(keepRecoveryStage);
        }

        private void StartActiveFanWatchdog(string reason, bool keepRecoveryStage = false)
        {
            if (Interlocked.CompareExchange(ref _activeFanWatchdogEnabled, 1, 0) == 1)
                return;

            ResetActiveFanWatchdogCounters(keepRecoveryStage);
            activeFanWatchdogTimer.Start();

            int currentMode = Modes.GetCurrent();
            Logger.WriteLine($"fan-watchdog-start: reason={reason}, mode={currentMode}, modeBase={Modes.GetBase(currentMode)}, manualRequired={AppConfig.IsManualModeRequired(currentMode)}, seq={Interlocked.Read(ref modeApplySequence)}");
        }

        private void UpdateActiveFanWatchdogState(string reason, bool keepRecoveryStage = false)
        {
            if (ShouldEnableActiveFanWatchdog())
                StartActiveFanWatchdog(reason, keepRecoveryStage);
            else
                StopActiveFanWatchdog(reason, keepRecoveryStage);
        }

        private async Task RunFanRecoveryAsync(bool silentBounce, ActiveFanWatchdogSample sample)
        {
            if (Interlocked.CompareExchange(ref _fanRecoveryRunning, 1, 0) == 1)
                return;

            StopActiveFanWatchdog(silentBounce ? "silent-bounce-recovery" : "reapply-recovery", keepRecoveryStage: true);

            try
            {
                int currentMode = Modes.GetCurrent();
                var snapshot = CreateModeApplySnapshot(currentMode, currentMode);

                if (silentBounce)
                    Logger.WriteLine($"fan-recovery-silent-bounce: mode={sample.Mode}, modeBase={sample.ModeBase}, manualRequired={sample.ManualModeRequired}, cpuTemp={sample.CpuTemp}, gpuTemp={sample.GpuTemp}, cpuFan={sample.CpuFan}, gpuFan={sample.GpuFan}, expectedCpu={sample.ExpectedCpuFan}, expectedGpu={sample.ExpectedGpuFan}, seq={snapshot.SequenceId}");
                else
                    Logger.WriteLine($"fan-recovery-reapply: mode={sample.Mode}, modeBase={sample.ModeBase}, manualRequired={sample.ManualModeRequired}, cpuTemp={sample.CpuTemp}, gpuTemp={sample.GpuTemp}, cpuFan={sample.CpuFan}, gpuFan={sample.GpuFan}, expectedCpu={sample.ExpectedCpuFan}, expectedGpu={sample.ExpectedGpuFan}, seq={snapshot.SequenceId}");

                await ApplyPerformanceModeAsync(snapshot, silentBounce, runModeCommand: false, source: silentBounce ? "fan-recovery-silent-bounce" : "fan-recovery-reapply");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"fan-recovery-error: silentBounce={silentBounce}, error={ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _fanRecoveryRunning, 0);
                UpdateActiveFanWatchdogState(silentBounce ? "silent-bounce-finished" : "reapply-finished", keepRecoveryStage: true);
            }
        }

        private static (bool shouldApply, bool forceApply) GetScreenOffFanKeepAliveApplyState()
        {
            bool applyFans = AppConfig.IsMode("auto_apply");
            bool applyPowerFans = AppConfig.IsMode("auto_apply_power") && AppConfig.IsFanRequired();
            bool shouldApply = applyFans || applyPowerFans;
            bool forceApply = !applyFans && applyPowerFans;
            return (shouldApply, forceApply);
        }

        private static Program.KeepAliveReason GetScreenOffFanKeepAliveReason()
        {
            return (Program.KeepAliveReason)Interlocked.CompareExchange(ref _screenOffFanKeepAliveReason, 0, 0);
        }

        private Program.KeepAliveReason StopScreenOffFanKeepAlive(string reason)
        {
            bool wasEnabled = Interlocked.Exchange(ref _screenOffFanKeepAliveEnabled, 0) == 1;
            Program.KeepAliveReason keepAliveReason = (Program.KeepAliveReason)Interlocked.Exchange(ref _screenOffFanKeepAliveReason, (int)Program.KeepAliveReason.None);
            screenOffFanKeepAliveTimer.Stop();

            if (wasEnabled)
            {
                Logger.WriteLine($"Screen-off fan keep-alive stopped: {reason}, keepAliveReason={keepAliveReason}");
            }

            UpdateActiveFanWatchdogState($"screen-off-stop:{reason}");

            return wasEnabled ? keepAliveReason : Program.KeepAliveReason.None;
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

            _ = ReapplyFansForScreenOffKeepAliveAsync(source);
        }

        private async Task ReapplyFansForScreenOffKeepAliveAsync(string source)
        {
            try
            {
                if (Interlocked.CompareExchange(ref _screenOffFanKeepAliveEnabled, 0, 0) == 0)
                {
                    Logger.WriteLine($"Screen-off fan keep-alive skipped ({source}): keep-alive not active");
                    return;
                }

                Program.KeepAliveReason keepAliveReason = GetScreenOffFanKeepAliveReason();
                if (keepAliveReason != Program.KeepAliveReason.RealScreenOff)
                {
                    Logger.WriteLine($"Screen-off fan keep-alive skipped ({source}): keepAliveReason={keepAliveReason}");
                    return;
                }

                var state = GetScreenOffFanKeepAliveApplyState();
                if (!state.shouldApply)
                {
                    Logger.WriteLine($"Screen-off fan keep-alive stopped ({source}): custom fan apply no longer enabled");
                    StopScreenOffFanKeepAlive("apply conditions changed");
                    return;
                }

                var snapshot = CreateCurrentFanApplySnapshot(state.forceApply, $"screen-off-{source}", keepAliveReason);

                await modeApplySemaphore.WaitAsync();
                try
                {
                    if (!IsCurrentFanApply(snapshot))
                    {
                        LogSkippedFanApply(snapshot, "before-start");
                        return;
                    }

                    Logger.WriteLine($"Screen-off fan keep-alive reapply ({source}, seq={snapshot.SequenceId}, keepAliveReason={snapshot.KeepAliveReason}, force={snapshot.ForceApply})");
                    ApplyFanSnapshot(snapshot);
                }
                finally
                {
                    modeApplySemaphore.Release();
                }
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

        internal Program.KeepAliveReason SetScreenOffFanKeepAlive(bool isScreenOff, Program.KeepAliveReason keepAliveReason = Program.KeepAliveReason.None)
        {
            if (isScreenOff)
            {
                if (keepAliveReason != Program.KeepAliveReason.RealScreenOff)
                {
                    StopScreenOffFanKeepAlive($"non-screen-off event ({keepAliveReason})");
                    Logger.WriteLine($"Screen-off fan keep-alive not started: keepAliveReason={keepAliveReason}");
                    return keepAliveReason;
                }

                var state = GetScreenOffFanKeepAliveApplyState();
                if (!state.shouldApply)
                {
                    Logger.WriteLine("Screen-off fan keep-alive not started: custom fan apply not enabled");
                    return Program.KeepAliveReason.None;
                }

                Interlocked.Exchange(ref _screenOffFanKeepAliveReason, (int)keepAliveReason);
                if (Interlocked.CompareExchange(ref _screenOffFanKeepAliveEnabled, 1, 0) == 1)
                {
                    Logger.WriteLine($"Screen-off fan keep-alive already active: keepAliveReason={GetScreenOffFanKeepAliveReason()}");
                    return keepAliveReason;
                }

                Logger.WriteLine($"Screen-off fan keep-alive started: keepAliveReason={keepAliveReason}");
                screenOffFanKeepAliveTimer.Start();
                UpdateActiveFanWatchdogState($"screen-off-start:{keepAliveReason}");

                Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(ScreenOffFanInitialDelayMs));
                    ReapplyFansForScreenOffKeepAlive("initial");
                });

                return keepAliveReason;
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
                ManualModeRequired = AppConfig.IsManualModeRequired(mode),
            };
        }

        private static byte[] CloneCurve(byte[] curve)
        {
            return (byte[])curve.Clone();
        }

        private static FanApplySnapshot CreateFanApplySnapshot(
            int mode,
            int modeBase,
            string modeName,
            bool forceApply,
            bool autoApplyFans,
            bool autoApplyPower,
            bool midFanEnabled,
            bool xgmFanEnabled,
            bool fanRequired,
            bool powerRequired,
            byte[] cpuFanCurve,
            byte[] gpuFanCurve,
            byte[] midFanCurve,
            byte[] xgmFanCurve,
            string source,
            Program.KeepAliveReason keepAliveReason = Program.KeepAliveReason.None,
            bool allowSelfHeal = true,
            long modeSequenceId = 0)
        {
            return new FanApplySnapshot
            {
                SequenceId = Interlocked.Increment(ref fanApplySequence),
                ModeSequenceId = modeSequenceId,
                Mode = mode,
                ModeBase = modeBase,
                ModeName = modeName,
                ForceApply = forceApply,
                AutoApplyFans = autoApplyFans,
                AutoApplyPower = autoApplyPower,
                MidFanEnabled = midFanEnabled,
                XgmFanEnabled = xgmFanEnabled,
                FanRequired = fanRequired,
                PowerRequired = powerRequired,
                Source = source,
                KeepAliveReason = keepAliveReason,
                AllowSelfHeal = allowSelfHeal,
                CpuFanCurve = CloneCurve(cpuFanCurve),
                GpuFanCurve = CloneCurve(gpuFanCurve),
                MidFanCurve = CloneCurve(midFanCurve),
                XgmFanCurve = CloneCurve(xgmFanCurve),
            };
        }

        private static FanApplySnapshot CreateFanApplySnapshot(ModeApplySnapshot snapshot, string source, Program.KeepAliveReason keepAliveReason = Program.KeepAliveReason.None, bool allowSelfHeal = true, bool forceApply = false)
        {
            return CreateFanApplySnapshot(
                snapshot.Mode,
                snapshot.ModeBase,
                snapshot.ModeName,
                forceApply || (snapshot.AutoApplyPower && snapshot.FanRequired),
                snapshot.AutoApplyFans,
                snapshot.AutoApplyPower,
                snapshot.MidFanEnabled,
                snapshot.XgmFanEnabled,
                snapshot.FanRequired,
                snapshot.PowerRequired,
                snapshot.CpuFanCurve,
                snapshot.GpuFanCurve,
                snapshot.MidFanCurve,
                snapshot.XgmFanCurve,
                source,
                keepAliveReason,
                allowSelfHeal,
                snapshot.SequenceId);
        }

        private static FanApplySnapshot CreateCurrentFanApplySnapshot(bool forceApply, string source, Program.KeepAliveReason keepAliveReason = Program.KeepAliveReason.None, bool allowSelfHeal = true)
        {
            int currentMode = Modes.GetCurrent();
            bool autoApplyFans = AppConfig.IsMode("auto_apply", currentMode);
            bool autoApplyPower = AppConfig.IsMode("auto_apply_power", currentMode);

            return CreateFanApplySnapshot(
                currentMode,
                Modes.GetBase(currentMode),
                Modes.GetName(currentMode),
                forceApply,
                autoApplyFans,
                autoApplyPower,
                AppConfig.Is("mid_fan"),
                AppConfig.Is("xgm_fan"),
                AppConfig.IsFanRequired(),
                AppConfig.IsPowerRequired(),
                AppConfig.GetFanConfig(AsusFan.CPU, currentMode),
                AppConfig.GetFanConfig(AsusFan.GPU, currentMode),
                AppConfig.GetFanConfig(AsusFan.Mid, currentMode),
                AppConfig.GetFanConfig(AsusFan.XGM, currentMode),
                source,
                keepAliveReason,
                allowSelfHeal,
                Interlocked.Read(ref modeApplySequence));
        }

        private static FanApplySnapshot CloneFanApplySnapshot(FanApplySnapshot snapshot, string source, bool allowSelfHeal)
        {
            return CreateFanApplySnapshot(
                snapshot.Mode,
                snapshot.ModeBase,
                snapshot.ModeName,
                snapshot.ForceApply,
                snapshot.AutoApplyFans,
                snapshot.AutoApplyPower,
                snapshot.MidFanEnabled,
                snapshot.XgmFanEnabled,
                snapshot.FanRequired,
                snapshot.PowerRequired,
                snapshot.CpuFanCurve,
                snapshot.GpuFanCurve,
                snapshot.MidFanCurve,
                snapshot.XgmFanCurve,
                source,
                snapshot.KeepAliveReason,
                allowSelfHeal,
                snapshot.ModeSequenceId);
        }

        private static bool IsCurrentFanApply(FanApplySnapshot snapshot)
        {
            if (snapshot.SequenceId != Interlocked.Read(ref fanApplySequence))
                return false;

            if (snapshot.ModeSequenceId > 0 && snapshot.ModeSequenceId != Interlocked.Read(ref modeApplySequence))
                return false;

            if (snapshot.KeepAliveReason != Program.KeepAliveReason.None && Interlocked.CompareExchange(ref _screenOffFanKeepAliveEnabled, 0, 0) == 0)
                return false;

            return true;
        }

        private static void LogSkippedFanApply(FanApplySnapshot snapshot, string stage)
        {
            Logger.WriteLine($"Fan apply skipped ({stage}): seq={snapshot.SequenceId}, source={snapshot.Source}, mode={snapshot.Mode}, keepAliveReason={snapshot.KeepAliveReason}, latestFan={Interlocked.Read(ref fanApplySequence)}, latestMode={Interlocked.Read(ref modeApplySequence)}");
        }

        private async Task ApplyPerformanceModeCoreAsync(ModeApplySnapshot snapshot, bool silentBounce, bool runModeCommand, string source)
        {
            if (!IsCurrentModeApply(snapshot.SequenceId))
            {
                LogSkippedModeApply(snapshot, "before-start");
                return;
            }

            Logger.WriteLine($"Mode apply start: seq={snapshot.SequenceId}, source={source}, mode={snapshot.Mode}, base={snapshot.ModeBase}, manualRequired={snapshot.ManualModeRequired}, silentBounce={silentBounce}");

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

            if (silentBounce)
            {
                Program.acpi.DeviceSet(AsusACPI.PerformanceMode, AsusACPI.PerformanceSilent, "ModeRecoverySilent");
                await Task.Delay(TimeSpan.FromMilliseconds(SilentBounceDelayMs));

                if (!IsCurrentModeApply(snapshot.SequenceId))
                {
                    LogSkippedModeApply(snapshot, "after-silent-bounce");
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

            AutoFans(snapshot, source: source);

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

            if (runModeCommand && snapshot.ModeCommand is not null)
            {
                Logger.WriteLine("Running mode command: " + snapshot.ModeCommand);
                RestrictedProcessHelper.RunAsRestrictedUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/C " + snapshot.ModeCommand);
            }
        }

        private async Task ApplyPerformanceModeAsync(ModeApplySnapshot snapshot, bool silentBounce = false, bool runModeCommand = true, string source = "mode-apply")
        {
            await modeApplySemaphore.WaitAsync();
            try
            {
                await ApplyPerformanceModeCoreAsync(snapshot, silentBounce, runModeCommand, source);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Mode apply error: seq={snapshot.SequenceId}, mode={snapshot.Mode}, error={ex}");
            }
            finally
            {
                modeApplySemaphore.Release();
                UpdateActiveFanWatchdogState($"mode-apply-finished:{source}", keepRecoveryStage: Interlocked.CompareExchange(ref _fanRecoveryRunning, 0, 0) == 1 || source.StartsWith("fan-recovery"));
            }
        }


        public void ResetPerformanceMode()
        {
            ResetRyzen();

            Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Modes.GetCurrentBase(), "Mode");

            // Default power mode
            AppConfig.RemoveMode("powermode");
            PowerNative.SetPowerMode(Modes.GetCurrentBase());
            UpdateActiveFanWatchdogState("reset-performance-mode");
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

            ResetActiveFanWatchdogCounters();
            Task.Run(() => ApplyPerformanceModeAsync(snapshot));
            UpdateActiveFanWatchdogState("set-performance-mode");


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
            _ = ApplyStandaloneFanSnapshotAsync(CreateCurrentFanApplySnapshot(force, "direct"));
        }

        private async Task ApplyStandaloneFanSnapshotAsync(FanApplySnapshot snapshot)
        {
            await modeApplySemaphore.WaitAsync();
            try
            {
                if (!IsCurrentFanApply(snapshot))
                {
                    LogSkippedFanApply(snapshot, "before-start");
                    return;
                }

                ApplyFanSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Fan apply error: seq={snapshot.SequenceId}, source={snapshot.Source}, keepAliveReason={snapshot.KeepAliveReason}, error={ex}");
            }
            finally
            {
                modeApplySemaphore.Release();
                UpdateActiveFanWatchdogState($"fan-apply-finished:{snapshot.Source}");
            }
        }

        private void AutoFans(ModeApplySnapshot? snapshot, bool force = false, string source = "direct")
        {
            FanApplySnapshot fanSnapshot = snapshot is not null
                ? CreateFanApplySnapshot(snapshot, source, forceApply: force)
                : CreateCurrentFanApplySnapshot(force, source);

            ApplyFanSnapshot(fanSnapshot);
        }

        private void ApplyFanSnapshot(FanApplySnapshot snapshot)
        {
            customFans = false;

            bool shouldApply = snapshot.ForceApply || snapshot.AutoApplyFans;
            bool shouldApplyPower = snapshot.AutoApplyPower;
            bool powerRequired = snapshot.PowerRequired;

            if (!shouldApply)
            {
                SetModeLabel(snapshot);
                return;
            }

            bool xgmFan = false;
            if (snapshot.XgmFanEnabled)
            {
                XGM.SetFan(CloneCurve(snapshot.XgmFanCurve));
                xgmFan = Program.acpi.IsXGConnected();
            }

            byte[] cpuCurve = CloneCurve(snapshot.CpuFanCurve);
            byte[] gpuCurve = CloneCurve(snapshot.GpuFanCurve);
            byte[] midCurve = CloneCurve(snapshot.MidFanCurve);

            int cpuResult = ApplyFanCurveWithRetry(AsusFan.CPU, cpuCurve, snapshot);
            int gpuResult = ApplyFanCurveWithRetry(AsusFan.GPU, gpuCurve, snapshot);

            int midResult = 1;
            if (snapshot.MidFanEnabled)
                midResult = ApplyFanCurveWithRetry(AsusFan.Mid, midCurve, snapshot);

            if (midResult != 1)
                Logger.WriteLine($"Fan curve apply warning: source={snapshot.Source}, seq={snapshot.SequenceId}, keepAliveReason={snapshot.KeepAliveReason}, midCurve={midResult}");

            List<string> fallbackDevices = [];
            bool fallbackApplied = false;
            bool unsupportedCustomCurve = false;

            if (cpuResult != 1)
            {
                fallbackDevices.Add("CPU");
                fallbackApplied = true;
                unsupportedCustomCurve |= ApplyFanRangeFallback(AsusFan.CPU, cpuCurve, snapshot, cpuResult) != 1;
            }

            if (gpuResult != 1)
            {
                fallbackDevices.Add("GPU");
                fallbackApplied = true;
                unsupportedCustomCurve |= ApplyFanRangeFallback(AsusFan.GPU, gpuCurve, snapshot, gpuResult) != 1;
            }

            if (unsupportedCustomCurve)
            {
                Program.acpi.DeviceSet(AsusACPI.PerformanceMode, snapshot.ModeBase, "Reset Mode");
                settings.LabelFansResult("Model doesn't support custom fan curves");
            }
            else if (fallbackApplied)
            {
                settings.LabelFansResult("Temporary fan curve fallback to range mode");
                if (snapshot.AllowSelfHeal)
                    ScheduleFanCurveSelfHeal(snapshot, string.Join(",", fallbackDevices));
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
                    if (snapshot.ModeSequenceId > 0 && snapshot.ModeSequenceId != Interlocked.Read(ref modeApplySequence)) return;
                    Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, 80, "PowerLimit Fix A0");
                    Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, 80, "PowerLimit Fix A3");
                });
            }

            SetModeLabel(snapshot);
        }

        private int ApplyFanRangeFallback(AsusFan device, byte[] curve, FanApplySnapshot snapshot, int curveResult)
        {
            int rangeResult = Program.acpi.SetFanRange(device, CloneCurve(curve));
            Logger.WriteLine($"Fan curve fallback: source={snapshot.Source}, seq={snapshot.SequenceId}, keepAliveReason={snapshot.KeepAliveReason}, fanFallbackDevice={device}, curveResult={curveResult}, rangeResult={rangeResult}");
            return rangeResult;
        }

        private void ScheduleFanCurveSelfHeal(FanApplySnapshot snapshot, string fallbackDevices)
        {
            Logger.WriteLine($"Fan curve self-heal scheduled: source={snapshot.Source}, seq={snapshot.SequenceId}, keepAliveReason={snapshot.KeepAliveReason}, fanFallbackDevice={fallbackDevices}");

            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(FanSelfHealDelayMs));
                FanApplySnapshot retrySnapshot = CloneFanApplySnapshot(snapshot, $"{snapshot.Source}-self-heal", false);
                await ApplyStandaloneFanSnapshotAsync(retrySnapshot);
            });
        }

        private int ApplyFanCurveWithRetry(AsusFan device, byte[] curve, FanApplySnapshot snapshot)
        {
            int result = Program.acpi.SetFanCurve(device, CloneCurve(curve));
            if (result == 1) return result;

            Logger.WriteLine($"Fan curve retry scheduled: source={snapshot.Source}, seq={snapshot.SequenceId}, keepAliveReason={snapshot.KeepAliveReason}, device={device}, result={result}");
            Thread.Sleep(FanRetryDelayMs);

            int retryResult = Program.acpi.SetFanCurve(device, CloneCurve(curve));
            if (retryResult != 1)
                Logger.WriteLine($"Fan curve retry failed: source={snapshot.Source}, seq={snapshot.SequenceId}, keepAliveReason={snapshot.KeepAliveReason}, device={device}, result={retryResult}");

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
            SetModeLabel((ModeApplySnapshot?)null);
        }

        private void SetModeLabel(FanApplySnapshot snapshot)
        {
            settings.SetModeLabel(Properties.Strings.PerformanceMode + ": " + snapshot.ModeName + (customFans ? "+" : "") + ((customPower > 0) ? " " + customPower + "W" : ""));
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
