namespace GHelper.Mode
{
    internal class Modes
    {
        public const string FansTileKey = "fans";

        private const string PerformanceTilesOrderKey = "performance_tiles_order";
        private const string ModeTilePrefix = "mode:";

        static Dictionary<string, string> settings = new Dictionary<string, string>
        {
            { "mode_base", "_" },
            { "mode_name", "_" },
            { "powermode", "string" },
            { "limit_total", "int" },
            { "limit_slow", "int" },
            { "limit_fast", "int" },
            { "limit_cpu", "int" },
            { "fan_profile_cpu", "string" },
            { "fan_profile_gpu", "string" },
            { "fan_profile_mid", "string" },
            { "gpu_power", "int" },
            { "gpu_boost", "int" },
            { "gpu_temp", "int" },
            { "gpu_core", "int" },
            { "gpu_memory", "int" },
            { "gpu_clock_limit", "int" },
            { "cpu_temp", "_" },
            { "cpu_uv", "_" },
            { "igpu_uv", "_" },
            { "auto_boost", "int" },
            { "auto_apply", "int" },
            { "auto_apply_power", "int" },
            { "auto_uv", "_" }
        };

        const int maxModes = 20;

        public static Dictionary<int, string> GetDictonary()
        {
            Dictionary<int, string> modes = new();
            foreach (int mode in GetOrderedModes())
            {
                modes[mode] = GetName(mode);
            }

            return modes;
        }

        public static List<int> GetList()
        {
            return GetOrderedModes();
        }

        public static void Remove(int mode)
        {
            foreach (string clean in settings.Keys)
            {
                AppConfig.Remove(clean + "_" + mode);
            }
        }

        public static int Add()
        {
            int currentMode = GetCurrent();

            for (int i = 3; i < maxModes; i++)
            {
                if (Exists(i)) continue;

                AppConfig.Set("mode_base_" + i, GetCurrentBase());
                AppConfig.Set("mode_name_" + i, "Custom " + (i - 2));

                if (Exists(currentMode))
                {
                    foreach (var kvp in settings)
                    {
                        if (kvp.Value == "_") continue;

                        string sourceKey = kvp.Key + "_" + currentMode;
                        string targetKey = kvp.Key + "_" + i;

                        if (!AppConfig.Exists(sourceKey)) continue;

                        if (kvp.Value == "int")
                            AppConfig.Set(targetKey, AppConfig.Get(sourceKey));
                        else
                            AppConfig.Set(targetKey, AppConfig.GetString(sourceKey));
                    }
                }

                return i;
            }
            return -1;
        }


        public static int GetCurrent()
        {
            return AppConfig.Get("performance_mode");
        }

        public static bool IsCurrentCustom()
        {
            return GetCurrent() > 2;
        }

        public static void SetCurrent(int mode)
        {
            AppConfig.Set("performance_" + (int)SystemInformation.PowerStatus.PowerLineStatus, mode);
            AppConfig.Set("performance_mode", mode);
        }

        public static int GetCurrentBase()
        {
            return GetBase(GetCurrent());
        }

        public static string GetCurrentName()
        {
            return GetName(GetCurrent());
        }

        public static bool Exists(int mode)
        {
            return GetBase(mode) >= 0;
        }

        public static int GetBase(int mode)
        {
            if (mode >= 0 && mode <= 2)
                return mode;
            else
                return AppConfig.Get("mode_base_" + mode);
        }

        public static string GetName(int mode)
        {
            switch (mode)
            {
                case 0:
                    return Properties.Strings.Balanced;
                case 1:
                    return Properties.Strings.Turbo;
                case 2:
                    return Properties.Strings.Silent;
                default:
                    return AppConfig.GetString("mode_name_" + mode);
            }
        }

        public static string GetTileKey(int mode)
        {
            return ModeTilePrefix + mode;
        }

        public static bool TryGetModeFromTileKey(string? tileKey, out int mode)
        {
            mode = -1;
            if (string.IsNullOrWhiteSpace(tileKey) || !tileKey.StartsWith(ModeTilePrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            return int.TryParse(tileKey.Substring(ModeTilePrefix.Length), out mode) && Exists(mode);
        }

        public static List<string> GetPerformanceTileOrder()
        {
            string rawOrder = AppConfig.GetString(PerformanceTilesOrderKey, "");
            List<string> normalized = NormalizePerformanceTileOrder(ParseTileOrder(rawOrder));
            string normalizedOrder = string.Join(",", normalized);

            if (!string.Equals(rawOrder, normalizedOrder, StringComparison.Ordinal))
            {
                AppConfig.Set(PerformanceTilesOrderKey, normalizedOrder);
            }

            return normalized;
        }

        public static void SetPerformanceTileOrder(IEnumerable<string> tileOrder)
        {
            List<string> normalized = NormalizePerformanceTileOrder(tileOrder);
            AppConfig.Set(PerformanceTilesOrderKey, string.Join(",", normalized));
        }

        public static List<int> GetOrderedModes()
        {
            List<int> modes = new();

            foreach (string tileKey in GetPerformanceTileOrder())
            {
                if (TryGetModeFromTileKey(tileKey, out int mode))
                    modes.Add(mode);
            }

            return modes;
        }

        private static List<string> ParseTileOrder(string? rawOrder)
        {
            if (string.IsNullOrWhiteSpace(rawOrder))
                return new List<string>();

            return rawOrder
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        private static List<string> NormalizePerformanceTileOrder(IEnumerable<string> tileOrder)
        {
            List<int> availableModes = GetDefaultModeList();
            HashSet<int> availableModeSet = availableModes.ToHashSet();
            HashSet<int> addedModes = new();
            List<string> normalized = new();
            bool fansAdded = false;

            foreach (string rawTileKey in tileOrder)
            {
                if (TryGetModeFromTileKey(rawTileKey, out int mode))
                {
                    if (availableModeSet.Contains(mode) && addedModes.Add(mode))
                        normalized.Add(GetTileKey(mode));

                    continue;
                }

                if (!fansAdded && string.Equals(rawTileKey, FansTileKey, StringComparison.OrdinalIgnoreCase))
                {
                    normalized.Add(FansTileKey);
                    fansAdded = true;
                }
            }

            foreach (int mode in availableModes)
            {
                if (addedModes.Add(mode))
                    normalized.Add(GetTileKey(mode));
            }

            if (!fansAdded)
                normalized.Add(FansTileKey);

            return normalized;
        }

        private static List<int> GetDefaultModeList()
        {
            List<int> modes = new();

            if (Exists(AsusACPI.PerformanceSilent))
                modes.Add(AsusACPI.PerformanceSilent);
            if (Exists(AsusACPI.PerformanceBalanced))
                modes.Add(AsusACPI.PerformanceBalanced);
            if (Exists(AsusACPI.PerformanceTurbo))
                modes.Add(AsusACPI.PerformanceTurbo);

            for (int i = 3; i < maxModes; i++)
            {
                if (Exists(i))
                    modes.Add(i);
            }

            return modes;
        }

        public static int GetNext(bool back = false)
        {
            var modes = GetOrderedModes();
            int index = modes.IndexOf(GetCurrent());

            if (back)
            {
                index--;
                if (index < 0) index = modes.Count - 1;
                return modes[index];
            }
            else
            {
                index++;
                if (index > modes.Count - 1) index = 0;
                return modes[index];
            }
        }
    }
}
