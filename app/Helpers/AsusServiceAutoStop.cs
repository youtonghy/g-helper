using System.Threading;

namespace GHelper.Helpers
{
    public static class AsusServiceAutoStop
    {
        private const int TriggerThrottleMs = 2500;
        private static long _lastTrigger;
        private static int _inProgress;

        public static void Trigger(string source)
        {
            if (!AppConfig.Is("auto_stop_asus_services")) return;

            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (Math.Abs(now - Interlocked.Read(ref _lastTrigger)) < TriggerThrottleMs) return;
            if (Interlocked.Exchange(ref _inProgress, 1) == 1) return;

            Interlocked.Exchange(ref _lastTrigger, now);

            Task.Run(() =>
            {
                try
                {
                    Execute(source);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Auto-stop ASUS services failed ({source}): {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _inProgress, 0);
                }
            });
        }

        private static void Execute(string source)
        {
            if (!AppConfig.Is("auto_stop_asus_services")) return;

            int servicesCount = AsusService.GetRunningCount();
            if (servicesCount <= 0) return;

            Logger.WriteLine($"Auto-stop ASUS services ({source}): {servicesCount}");

            if (ProcessHelper.IsUserAdministrator())
            {
                AsusService.StopAsusServices();
                Program.inputDispatcher?.Init();
                return;
            }

            if (!ProcessHelper.RunAsAdminDetached("services-stop"))
            {
                AppConfig.Set("auto_stop_asus_services", 0);
                Program.settingsForm.SyncAutoStopAsusServicesOption();
                Logger.WriteLine("Auto-stop ASUS services disabled: UAC cancelled or elevation failed");
            }
        }
    }
}
