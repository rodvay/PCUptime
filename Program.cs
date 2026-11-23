using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("PC Uptime & Power Cost")]
[assembly: AssemblyDescription("Lightweight Windows utility showing live system uptime, detected hardware specs, and estimated electricity cost.")]
[assembly: AssemblyProduct("PCUptime")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace PCUptime
{
    internal static class NativeMethods
    {
        public const int WM_DPICHANGED = 0x02E0;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWA_TEXT_COLOR = 36;

        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                    return;
            }
            catch
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }

        public static void ApplyDarkTitleBar(IntPtr hwnd, Color captionColor, Color textColor)
        {
            try
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref enabled, sizeof(int));
                }

                int capRef = ToColorRef(captionColor);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref capRef, sizeof(int));

                int txtRef = ToColorRef(textColor);
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref txtRef, sizeof(int));
            }
            catch
            {
            }
        }

        private static int ToColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);
        }
    }

    internal enum UsageProfile
    {
        Light = 0,
        Average = 1,
        Heavy = 2
    }

    internal sealed class LaunchOptions
    {
        public bool DemoMode;
        public double DemoHours;
        public string ForceLang;
        public string ForcePreset;
        public bool ShowSettingsOnStart;

        public static LaunchOptions Parse(string[] args)
        {
            LaunchOptions opt = new LaunchOptions();
            if (args == null)
                return opt;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim().ToLowerInvariant();
                if (a == "--demo")
                {
                    opt.DemoMode = true;
                }
                else if (a == "--demo-hours" && i + 1 < args.Length)
                {
                    double h;
                    if (double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out h) && h > 0)
                    {
                        opt.DemoMode = true;
                        opt.DemoHours = h;
                    }
                }
                else if (a == "--lang" && i + 1 < args.Length)
                {
                    string l = (args[++i] ?? "").Trim().ToLowerInvariant();
                    if (l == "tr" || l == "en")
                        opt.ForceLang = l;
                }
                else if (a == "--preset" && i + 1 < args.Length)
                {
                    opt.ForcePreset = (args[++i] ?? "").Trim().ToLowerInvariant();
                }
                else if (a == "--show-settings")
                {
                    opt.ShowSettingsOnStart = true;
                }
            }

            return opt;
        }
    }

    internal sealed class UserSettings
    {
        // Turkey EPDK 2026 residential tariffs (taxes included: energy + distribution + 5% BTV + 10% VAT)
        public const double TariffTrLow = 3.24;
        public const double TariffTrMixed = 4.05;
        public const double TariffTrHigh = 4.86;

        // Global average residential benchmarks
        public const double TariffUsAvg = 0.17;
        public const double TariffEuAvg = 0.29;
        public const double TariffUkAvg = 0.28;

        public string Language;      // "tr" or "en"
        public double PricePerKwh;
        public string TariffKey;     // "tr_low", "tr_mixed", "tr_high", "us_avg", "eu_avg", "uk_avg", "custom"
        public string CurrencyCode;  // "TL", "USD", "EUR", "GBP"
        public UsageProfile Profile;
        public int CustomWatts;      // 0 = automatic hardware wattage estimate
        public bool IncludeMonitors;

        public UserSettings()
        {
            bool isTurkishOs = false;
            try
            {
                isTurkishOs = string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "tr", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
            }

            if (isTurkishOs)
            {
                Language = "tr";
                PricePerKwh = TariffTrMixed;
                TariffKey = "tr_mixed";
                CurrencyCode = "TL";
            }
            else
            {
                Language = "en";
                PricePerKwh = TariffUsAvg;
                TariffKey = "us_avg";
                CurrencyCode = "USD";
            }

            Profile = UsageProfile.Average;
            CustomWatts = 0;
            IncludeMonitors = true;
        }

        public bool IsTurkish
        {
            get { return string.Equals(Language, "tr", StringComparison.OrdinalIgnoreCase); }
        }

        public string CurrencySymbol
        {
            get
            {
                if (CurrencyCode == "USD") return "$";
                if (CurrencyCode == "EUR") return "€";
                if (CurrencyCode == "GBP") return "£";
                return "₺";
            }
        }

        public CultureInfo ActiveCulture
        {
            get { return GetCulture(IsTurkish); }
        }

        public static CultureInfo GetCulture(bool isTurkish)
        {
            try
            {
                return CultureInfo.GetCultureInfo(isTurkish ? "tr-TR" : "en-US");
            }
            catch
            {
                return CultureInfo.InvariantCulture;
            }
        }

        public string GetTariffDisplayLabel()
        {
            bool tr = IsTurkish;
            if (TariffKey == "tr_low") return tr ? "Düşük" : "TR Low";
            if (TariffKey == "tr_mixed") return tr ? "Karma" : "TR Mixed";
            if (TariffKey == "tr_high") return tr ? "Yüksek" : "TR High";
            if (TariffKey == "us_avg") return tr ? "ABD Ort." : "US Avg";
            if (TariffKey == "eu_avg") return tr ? "AB Ort." : "EU Avg";
            if (TariffKey == "uk_avg") return tr ? "BK Ort." : "UK Avg";
            return tr ? "Özel" : "Custom";
        }

        public string FormatRatePerKwh()
        {
            CultureInfo c = ActiveCulture;
            if (CurrencyCode == "TL")
                return string.Format(c, "{0:0.00} ₺/kWh", PricePerKwh);
            return string.Format(c, "{0}{1:0.00}/kWh", CurrencySymbol, PricePerKwh);
        }

        public string FormatCompactMoney(double amount, string decimals)
        {
            CultureInfo c = ActiveCulture;
            string num = amount.ToString(decimals, c);
            if (CurrencyCode == "TL")
                return num + " TL";
            return CurrencySymbol + num;
        }

        private static string GetSettingsPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(Path.Combine(appData, "PCUptime"), "settings.ini");
        }

        public static UserSettings Load()
        {
            UserSettings s = new UserSettings();
            try
            {
                string path = GetSettingsPath();
                if (!File.Exists(path))
                    return s;

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    if (string.IsNullOrEmpty(raw))
                        continue;
                    int eq = raw.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = raw.Substring(0, eq).Trim();
                    string val = raw.Substring(eq + 1).Trim();

                    if (key == "Language" && (val == "tr" || val == "en"))
                    {
                        s.Language = val;
                    }
                    else if (key == "PricePerKwh")
                    {
                        double p;
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out p) && p > 0.005 && p < 1000.0)
                            s.PricePerKwh = p;
                    }
                    else if (key == "TariffKey" && !string.IsNullOrEmpty(val))
                    {
                        s.TariffKey = val;
                    }
                    else if (key == "TariffLabel" && !string.IsNullOrEmpty(val))
                    {
                        // Backward compatibility with v1.1 settings.ini
                        if (val == "Düşük") s.TariffKey = "tr_low";
                        else if (val == "Karma") s.TariffKey = "tr_mixed";
                        else if (val == "Yüksek") s.TariffKey = "tr_high";
                        else s.TariffKey = "custom";
                    }
                    else if (key == "CurrencyCode" && (val == "TL" || val == "USD" || val == "EUR" || val == "GBP"))
                    {
                        s.CurrencyCode = val;
                    }
                    else if (key == "Profile")
                    {
                        int pr;
                        if (int.TryParse(val, out pr) && pr >= 0 && pr <= 2)
                            s.Profile = (UsageProfile)pr;
                    }
                    else if (key == "CustomWatts")
                    {
                        int w;
                        if (int.TryParse(val, out w) && w >= 0 && w <= 5000)
                            s.CustomWatts = w;
                    }
                    else if (key == "IncludeMonitors")
                    {
                        s.IncludeMonitors = (val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            catch
            {
            }

            return s;
        }

        public void Save()
        {
            try
            {
                string path = GetSettingsPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Language=" + (Language ?? "tr"));
                sb.AppendLine("PricePerKwh=" + PricePerKwh.ToString("0.####", CultureInfo.InvariantCulture));
                sb.AppendLine("TariffKey=" + (TariffKey ?? "custom"));
                sb.AppendLine("CurrencyCode=" + (CurrencyCode ?? "TL"));
                sb.AppendLine("Profile=" + ((int)Profile).ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("CustomWatts=" + CustomWatts.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("IncludeMonitors=" + (IncludeMonitors ? "1" : "0"));

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    internal sealed class HardwareInfo
    {
        public bool IsDetected;
        public bool IsLaptop;
        public string CpuName;
        public int CpuCores;
        public int CpuTdpWatts;
        public string GpuName;
        public bool HasDiscreteGpu;
        public int GpuTdpWatts;
        public int RamGb;
        public int RamModules;
        public int SsdCount;
        public int HddCount;
        public int MonitorCount;

        public HardwareInfo()
        {
            CpuName = "";
            GpuName = "";
            MonitorCount = 1;
        }

        public int GetEstimatedWallWatts(UsageProfile profile, bool includeMonitors)
        {
            if (!IsDetected)
                return 120;

            double cpuFactor;
            double gpuFactor;
            double platformBase;
            double psuEfficiency;
            double monitorWattEach;

            if (IsLaptop)
            {
                if (profile == UsageProfile.Light)
                {
                    cpuFactor = 0.18;
                    gpuFactor = 0.08;
                    platformBase = 8.0;
                    psuEfficiency = 0.88;
                    monitorWattEach = 22.0;
                }
                else if (profile == UsageProfile.Heavy)
                {
                    cpuFactor = 0.78;
                    gpuFactor = 0.85;
                    platformBase = 14.0;
                    psuEfficiency = 0.89;
                    monitorWattEach = 28.0;
                }
                else
                {
                    cpuFactor = 0.38;
                    gpuFactor = 0.30;
                    platformBase = 11.0;
                    psuEfficiency = 0.88;
                    monitorWattEach = 25.0;
                }

                double ramW = Math.Max(2.0, RamGb * 0.15);
                double diskW = SsdCount * 2.0 + HddCount * 3.5;
                double dcWatts = (CpuTdpWatts * cpuFactor) + (GpuTdpWatts * gpuFactor) + platformBase + ramW + diskW;
                double wallWatts = dcWatts / psuEfficiency;

                if (includeMonitors && MonitorCount > 1)
                {
                    wallWatts += (MonitorCount - 1) * monitorWattEach;
                }

                return Math.Max(12, (int)Math.Round(wallWatts));
            }
            else
            {
                if (profile == UsageProfile.Light)
                {
                    cpuFactor = 0.22;
                    gpuFactor = 0.14;
                    platformBase = 22.0;
                    psuEfficiency = 0.84;
                    monitorWattEach = 24.0;
                }
                else if (profile == UsageProfile.Heavy)
                {
                    cpuFactor = 0.80;
                    gpuFactor = 0.88;
                    platformBase = 30.0;
                    psuEfficiency = 0.87;
                    monitorWattEach = 30.0;
                }
                else
                {
                    cpuFactor = 0.45;
                    gpuFactor = 0.42;
                    platformBase = 25.0;
                    psuEfficiency = 0.86;
                    monitorWattEach = 28.0;
                }

                double ramW = Math.Max(4.0, (RamModules > 0 ? RamModules * 3.5 : RamGb * 0.28));
                double diskW = SsdCount * 3.5 + HddCount * 7.0;
                double dcWatts = (CpuTdpWatts * cpuFactor) + (GpuTdpWatts * gpuFactor) + platformBase + ramW + diskW;
                double wallWatts = dcWatts / psuEfficiency;

                if (includeMonitors)
                {
                    int mons = Math.Max(1, MonitorCount);
                    wallWatts += mons * monitorWattEach;
                }

                return Math.Max(35, (int)Math.Round(wallWatts));
            }
        }

        public string BuildSystemSummary(bool isTurkish, CultureInfo culture)
        {
            List<string> parts = new List<string>();
            parts.Add(IsLaptop ? "Laptop" : (isTurkish ? "Masaüstü" : "Desktop"));

            if (RamGb > 0)
                parts.Add(string.Format(culture, "{0} GB RAM", RamGb));

            if (SsdCount > 0 && HddCount > 0)
                parts.Add(string.Format(culture, "{0} SSD, {1} HDD", SsdCount, HddCount));
            else if (SsdCount > 0)
                parts.Add(string.Format(culture, "{0} SSD", SsdCount));
            else if (HddCount > 0)
                parts.Add(string.Format(culture, "{0} HDD", HddCount));

            if (MonitorCount > 0)
            {
                if (isTurkish)
                    parts.Add(string.Format(culture, "{0} Monitör", MonitorCount));
                else
                    parts.Add(string.Format(culture, "{0} {1}", MonitorCount, MonitorCount == 1 ? "Monitor" : "Monitors"));
            }

            return string.Join(" · ", parts.ToArray());
        }
    }

    internal static class HardwareDetector
    {
        public static HardwareInfo Detect(int monitorCount)
        {
            HardwareInfo info = new HardwareInfo();
            info.MonitorCount = Math.Max(1, monitorCount);

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT PCSystemType, TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        object sysTypeObj = obj["PCSystemType"];
                        if (sysTypeObj != null)
                        {
                            int sysType = Convert.ToInt32(sysTypeObj);
                            if (sysType == 2 || sysType == 8 || sysType == 9 || sysType == 10 || sysType == 14)
                                info.IsLaptop = true;
                        }

                        object ramObj = obj["TotalPhysicalMemory"];
                        if (ramObj != null)
                        {
                            ulong bytes = Convert.ToUInt64(ramObj);
                            info.RamGb = Math.Max(1, (int)Math.Round(bytes / (1024.0 * 1024.0 * 1024.0)));
                        }
                    }
                }
            }
            catch
            {
            }

            if (!info.IsLaptop)
            {
                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery"))
                    {
                        foreach (ManagementObject obj in s.Get())
                        {
                            info.IsLaptop = true;
                            break;
                        }
                    }
                }
                catch
                {
                }
            }

            try
            {
                int mods = 0;
                ulong sumBytes = 0;
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        mods++;
                        if (obj["Capacity"] != null)
                            sumBytes += Convert.ToUInt64(obj["Capacity"]);
                    }
                }

                info.RamModules = mods;
                if (sumBytes > 0)
                {
                    int exactGb = (int)Math.Round(sumBytes / (1024.0 * 1024.0 * 1024.0));
                    if (exactGb > 0)
                        info.RamGb = exactGb;
                }
            }
            catch
            {
            }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        string rawName = obj["Name"] != null ? obj["Name"].ToString() : "Unknown CPU";
                        info.CpuName = CleanCpuName(rawName);
                        if (obj["NumberOfCores"] != null)
                            info.CpuCores += Convert.ToInt32(obj["NumberOfCores"]);
                    }
                }
            }
            catch
            {
                info.CpuName = "Unknown CPU";
            }

            if (info.CpuCores <= 0)
                info.CpuCores = Math.Max(2, Environment.ProcessorCount / 2);

            info.CpuTdpWatts = EstimateCpuTdp(info.CpuName, info.CpuCores, info.IsLaptop);

            List<string> gpus = new List<string>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        string gName = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                        if (string.IsNullOrEmpty(gName) || IsVirtualDisplayAdapter(gName))
                            continue;
                        gpus.Add(CleanGpuName(gName));
                    }
                }
            }
            catch
            {
            }

            if (gpus.Count == 0)
            {
                info.GpuName = "Integrated Graphics";
                info.HasDiscreteGpu = false;
                info.GpuTdpWatts = 0;
            }
            else
            {
                string bestGpu = gpus[0];
                int bestTdp = EstimateGpuTdp(gpus[0], info.IsLaptop);
                bool anyDiscrete = !IsIntegratedGpu(gpus[0]);

                for (int i = 1; i < gpus.Count; i++)
                {
                    int tdp = EstimateGpuTdp(gpus[i], info.IsLaptop);
                    bool discrete = !IsIntegratedGpu(gpus[i]);
                    if ((discrete && !anyDiscrete) || tdp > bestTdp)
                    {
                        bestGpu = gpus[i];
                        bestTdp = tdp;
                        anyDiscrete = discrete;
                    }
                }

                info.GpuName = bestGpu;
                info.HasDiscreteGpu = anyDiscrete;
                info.GpuTdpWatts = bestTdp;
            }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Model, MediaType, InterfaceType FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        string iface = obj["InterfaceType"] != null ? obj["InterfaceType"].ToString().ToUpperInvariant() : "";
                        string media = obj["MediaType"] != null ? obj["MediaType"].ToString().ToUpperInvariant() : "";
                        if (iface.Contains("USB") || media.Contains("REMOVABLE") || media.Contains("EXTERNAL"))
                            continue;

                        string model = obj["Model"] != null ? obj["Model"].ToString().ToUpperInvariant() : "";
                        if (model.Contains("SSD") || model.Contains("NVME") || model.Contains("M.2") || model.Contains("SOLID"))
                            info.SsdCount++;
                        else
                            info.HddCount++;
                    }
                }
            }
            catch
            {
            }

            if (info.SsdCount == 0 && info.HddCount == 0)
                info.SsdCount = 1;

            info.IsDetected = true;
            return info;
        }

        private static string CleanCpuName(string raw)
        {
            string s = Regex.Replace(raw ?? "", @"\s+", " ").Trim();
            s = s.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace("(r)", "");
            s = Regex.Replace(s, @"\s+\d+-Core Processor$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+Processor$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+CPU\s*@\s*[\d\.]+GHz$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+with Radeon.*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return string.IsNullOrEmpty(s) ? "Unknown CPU" : s;
        }

        private static string CleanGpuName(string raw)
        {
            string s = Regex.Replace(raw ?? "", @"\s+", " ").Trim();
            s = s.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace("(r)", "");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private static bool IsVirtualDisplayAdapter(string name)
        {
            string u = name.ToUpperInvariant();
            return u.Contains("MICROSOFT BASIC") ||
                   u.Contains("MICROSOFT REMOTE") ||
                   u.Contains("PARSEC") ||
                   u.Contains("VIRTUAL") ||
                   u.Contains("CITRIX") ||
                   u.Contains("TEAMVIEWER") ||
                   u.Contains("ANYDESK") ||
                   u.Contains("META ") ||
                   u.Contains("OCULUS") ||
                   u.Contains("LUMINONCORE") ||
                   u.Contains("IDD ");
        }

        private static bool IsIntegratedGpu(string name)
        {
            string u = name.ToUpperInvariant();
            if (u.Contains("INTEL") && !u.Contains("ARC A") && !u.Contains("ARC B"))
                return true;
            if (u.Contains("UHD GRAPHICS") || u.Contains("HD GRAPHICS") || u.Contains("IRIS"))
                return true;
            if (u == "AMD RADEON GRAPHICS" || u == "RADEON GRAPHICS" || u.Contains("VEGA 3") || u.Contains("VEGA 6") || u.Contains("VEGA 7") || u.Contains("VEGA 8") || u.Contains("VEGA 11") || u.Contains("RADEON 780M") || u.Contains("RADEON 680M"))
                return true;
            return false;
        }

        private static int EstimateCpuTdp(string cpuName, int cores, bool isLaptop)
        {
            string u = (cpuName ?? "").ToUpperInvariant();

            bool mobileSuffix = Regex.IsMatch(u, @"\d{4,5}(U|P|Y|G1|G4|G7|H|HS|HX|HK|HQ)\b");
            if (isLaptop || mobileSuffix)
            {
                if (Regex.IsMatch(u, @"\d(HX|HK)\b")) return 55;
                if (Regex.IsMatch(u, @"\d(H|HS|HQ)\b")) return 45;
                if (Regex.IsMatch(u, @"\dP\b")) return 28;
                if (Regex.IsMatch(u, @"\d(U|Y|G1|G4|G7)\b") || u.Contains("N100") || u.Contains("N200") || u.Contains("N95")) return 15;
                return cores >= 8 ? 45 : 25;
            }

            if (u.Contains("THREADRIPPER")) return 280;
            if (u.Contains("RYZEN"))
            {
                if (u.Contains("9950") || u.Contains("7950")) return 170;
                if (u.Contains("7900X") || u.Contains("9900X")) return 140;
                if (u.Contains("5950") || u.Contains("5900") || u.Contains("3950") || u.Contains("3900")) return 105;
                if (u.Contains("9800X3D") || u.Contains("7800X3D") || u.Contains("7900X3D")) return 120;
                if (u.Contains("5800X3D") || u.Contains("5700X3D") || u.Contains("5800X") || u.Contains("7700X") || u.Contains("7600X") || u.Contains("3800X")) return 105;
                if (u.Contains("3600X") || u.Contains("2600X") || u.Contains("2700X")) return 95;
                if (u.Contains("RYZEN 9")) return 120;
                if (u.Contains("RYZEN 7")) return 65;
                if (u.Contains("RYZEN 5")) return 65;
                if (u.Contains("RYZEN 3")) return 65;
                return 65;
            }
            if (u.Contains("FX-9")) return 220;
            if (u.Contains("FX-8")) return 125;
            if (u.Contains("FX-6") || u.Contains("FX-4")) return 95;
            if (u.Contains("ATHLON")) return 45;

            if (u.Contains("INTEL") || u.Contains("CORE") || u.Contains("XEON"))
            {
                bool isK = Regex.IsMatch(u, @"\d{4,5}(K|KF|KS)\b");
                if (u.Contains("I9") || u.Contains("ULTRA 9")) return isK ? 150 : 95;
                if (u.Contains("I7") || u.Contains("ULTRA 7")) return isK ? 135 : 80;
                if (u.Contains("I5") || u.Contains("ULTRA 5")) return isK ? 125 : 65;
                if (u.Contains("I3")) return 60;
                if (u.Contains("XEON")) return 105;
                if (u.Contains("PENTIUM") || u.Contains("CELERON")) return 54;
            }

            if (cores >= 16) return 150;
            if (cores >= 12) return 115;
            if (cores >= 8) return 90;
            if (cores >= 6) return 65;
            return 55;
        }

        private static int EstimateGpuTdp(string gpuName, bool isLaptop)
        {
            if (string.IsNullOrEmpty(gpuName) || IsIntegratedGpu(gpuName))
                return 0;

            string u = gpuName.ToUpperInvariant();
            bool mobileGpu = isLaptop || u.Contains("LAPTOP") || u.Contains("MOBILE") || u.Contains("MAX-Q");

            int desktopTdp = LookupDesktopGpuTdp(u);
            if (mobileGpu)
            {
                int scaled = (int)Math.Round(desktopTdp * 0.52);
                return Math.Max(35, Math.Min(150, scaled));
            }

            return desktopTdp;
        }

        private static int LookupDesktopGpuTdp(string u)
        {
            if (u.Contains("5090")) return 575;
            if (u.Contains("5080")) return 360;
            if (u.Contains("5070 TI")) return 300;
            if (u.Contains("5070")) return 250;
            if (u.Contains("5060 TI")) return 180;
            if (u.Contains("5060")) return 145;

            if (u.Contains("4090")) return 450;
            if (u.Contains("4080")) return 320;
            if (u.Contains("4070 TI")) return 285;
            if (u.Contains("4070 SUPER")) return 220;
            if (u.Contains("4070")) return 200;
            if (u.Contains("4060 TI")) return 160;
            if (u.Contains("4060")) return 115;
            if (u.Contains("4050")) return 75;

            if (u.Contains("3090 TI")) return 450;
            if (u.Contains("3090")) return 350;
            if (u.Contains("3080 TI")) return 350;
            if (u.Contains("3080")) return 320;
            if (u.Contains("3070 TI")) return 290;
            if (u.Contains("3070")) return 220;
            if (u.Contains("3060 TI")) return 200;
            if (u.Contains("3060")) return 170;
            if (u.Contains("3050")) return 130;

            if (u.Contains("2080 TI")) return 250;
            if (u.Contains("2080")) return 215;
            if (u.Contains("2070")) return 175;
            if (u.Contains("2060")) return 160;
            if (u.Contains("1660")) return 120;
            if (u.Contains("1650")) return 75;
            if (u.Contains("1080 TI")) return 250;
            if (u.Contains("1080")) return 180;
            if (u.Contains("1070 TI")) return 180;
            if (u.Contains("1070")) return 150;
            if (u.Contains("1060")) return 120;
            if (u.Contains("1050")) return 75;
            if (u.Contains("980 TI")) return 250;
            if (u.Contains("980")) return 165;
            if (u.Contains("970")) return 145;
            if (u.Contains("960")) return 120;
            if (u.Contains("950")) return 90;
            if (u.Contains("750")) return 60;
            if (u.Contains("GT 1030") || u.Contains("GT 730") || u.Contains("GT 710") || u.Contains("MX")) return 30;

            if (u.Contains("9070 XT")) return 304;
            if (u.Contains("9070")) return 220;
            if (u.Contains("7900 XTX")) return 355;
            if (u.Contains("7900 XT")) return 315;
            if (u.Contains("7900 GRE")) return 260;
            if (u.Contains("7800 XT")) return 263;
            if (u.Contains("7700 XT")) return 245;
            if (u.Contains("7600 XT")) return 190;
            if (u.Contains("7600")) return 165;
            if (u.Contains("6950 XT")) return 335;
            if (u.Contains("6900 XT") || u.Contains("6800 XT")) return 300;
            if (u.Contains("6800") || u.Contains("6750 XT")) return 250;
            if (u.Contains("6700 XT")) return 230;
            if (u.Contains("6700")) return 175;
            if (u.Contains("6650 XT")) return 176;
            if (u.Contains("6600 XT")) return 160;
            if (u.Contains("6600")) return 132;
            if (u.Contains("6500 XT")) return 107;
            if (u.Contains("6400")) return 53;
            if (u.Contains("5700 XT")) return 225;
            if (u.Contains("5700")) return 180;
            if (u.Contains("5600 XT")) return 150;
            if (u.Contains("5500 XT")) return 130;
            if (u.Contains("RX 590") || u.Contains("RADEON 590")) return 225;
            if (u.Contains("RX 580") || u.Contains("RADEON 580")) return 185;
            if (u.Contains("RX 570") || u.Contains("RADEON 570")) return 150;
            if (u.Contains("RX 560") || u.Contains("RX 460")) return 75;
            if (u.Contains("RX 550")) return 50;
            if (u.Contains("RX 480")) return 150;
            if (u.Contains("RX 470")) return 120;
            if (u.Contains("VEGA 64")) return 295;
            if (u.Contains("VEGA 56")) return 210;
            if (u.Contains("R9 390") || u.Contains("R9 290")) return 275;
            if (u.Contains("R9 380") || u.Contains("R9 280")) return 190;
            if (u.Contains("R7 ")) return 85;

            if (u.Contains("B580")) return 190;
            if (u.Contains("B570")) return 150;
            if (u.Contains("A770") || u.Contains("A750")) return 225;
            if (u.Contains("A580")) return 185;
            if (u.Contains("A380")) return 75;

            return 125;
        }
    }

    internal sealed class TariffSettingsDialog : Form
    {
        private static readonly Color ColBg = Color.FromArgb(16, 21, 28);
        private static readonly Color ColSurface = Color.FromArgb(24, 31, 40);
        private static readonly Color ColBorder = Color.FromArgb(52, 62, 77);
        private static readonly Color ColText = Color.FromArgb(240, 246, 252);
        private static readonly Color ColMuted = Color.FromArgb(139, 148, 158);
        private static readonly Color ColAccent = Color.FromArgb(245, 158, 11);
        private static readonly Color ColCyan = Color.FromArgb(94, 234, 212);

        private readonly bool _isTurkish;
        private readonly CultureInfo _culture;
        private readonly TextBox _txtPrice;
        private readonly ComboBox _cmbCurrency;
        private readonly TextBox _txtCustomWatts;
        private readonly CheckBox _chkMonitors;
        private readonly Label _lblError;
        private string _selectedKey;
        private bool _suppressCustomReset;

        public double ResultPrice { get; private set; }
        public string ResultTariffKey { get; private set; }
        public string ResultCurrencyCode { get; private set; }
        public int ResultCustomWatts { get; private set; }
        public bool ResultIncludeMonitors { get; private set; }

        public TariffSettingsDialog(UserSettings current, int autoWatts)
        {
            _isTurkish = current.IsTurkish;
            _culture = current.ActiveCulture;
            _selectedKey = current.TariffKey ?? "custom";

            ResultPrice = current.PricePerKwh;
            ResultTariffKey = _selectedKey;
            ResultCurrencyCode = current.CurrencyCode ?? "TL";
            ResultCustomWatts = current.CustomWatts;
            ResultIncludeMonitors = current.IncludeMonitors;

            Text = _isTurkish ? "Elektrik Tarife ve Güç Ayarları" : "Electricity Tariff & Power Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ColBg;
            ForeColor = ColText;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(390, 366);

            Label lblTr = new Label();
            lblTr.Text = _isTurkish ? "Türkiye EPDK 2026 Mesken (vergiler dahil):" : "Turkey EPDK 2026 Residential (tax incl.):";
            lblTr.ForeColor = ColMuted;
            lblTr.Location = new Point(18, 14);
            lblTr.AutoSize = true;
            Controls.Add(lblTr);

            Button btnTrLow = CreatePresetButton(_isTurkish ? "Düşük\r\n3,24 ₺" : "TR Low\r\n3.24 ₺", new Point(18, 36), new Size(112, 42), ColAccent);
            btnTrLow.Click += delegate { ApplyPreset(UserSettings.TariffTrLow, "tr_low", "TL"); };
            Controls.Add(btnTrLow);

            Button btnTrMixed = CreatePresetButton(_isTurkish ? "Karma (Ort.)\r\n4,05 ₺" : "TR Mixed\r\n4.05 ₺", new Point(139, 36), new Size(112, 42), ColAccent);
            btnTrMixed.Click += delegate { ApplyPreset(UserSettings.TariffTrMixed, "tr_mixed", "TL"); };
            Controls.Add(btnTrMixed);

            Button btnTrHigh = CreatePresetButton(_isTurkish ? "Yüksek\r\n4,86 ₺" : "TR High\r\n4.86 ₺", new Point(260, 36), new Size(112, 42), ColAccent);
            btnTrHigh.Click += delegate { ApplyPreset(UserSettings.TariffTrHigh, "tr_high", "TL"); };
            Controls.Add(btnTrHigh);

            Label lblGlobal = new Label();
            lblGlobal.Text = _isTurkish ? "Global Ortalama Tarifeler:" : "Global Benchmark Rates:";
            lblGlobal.ForeColor = ColMuted;
            lblGlobal.Location = new Point(18, 88);
            lblGlobal.AutoSize = true;
            Controls.Add(lblGlobal);

            Button btnUs = CreatePresetButton(_isTurkish ? "ABD Ort.\r\n$0,17" : "US Avg\r\n$0.17", new Point(18, 110), new Size(112, 40), ColCyan);
            btnUs.Click += delegate { ApplyPreset(UserSettings.TariffUsAvg, "us_avg", "USD"); };
            Controls.Add(btnUs);

            Button btnEu = CreatePresetButton(_isTurkish ? "AB Ort.\r\n€0,29" : "EU Avg\r\n€0.29", new Point(139, 110), new Size(112, 40), ColCyan);
            btnEu.Click += delegate { ApplyPreset(UserSettings.TariffEuAvg, "eu_avg", "EUR"); };
            Controls.Add(btnEu);

            Button btnUk = CreatePresetButton(_isTurkish ? "BK Ort.\r\n£0,28" : "UK Avg\r\n£0.28", new Point(260, 110), new Size(112, 40), ColCyan);
            btnUk.Click += delegate { ApplyPreset(UserSettings.TariffUkAvg, "uk_avg", "GBP"); };
            Controls.Add(btnUk);

            Label lblPrice = new Label();
            lblPrice.Text = _isTurkish ? "Birim Fiyat (/ kWh):" : "Unit Price (/ kWh):";
            lblPrice.ForeColor = ColText;
            lblPrice.Location = new Point(18, 168);
            lblPrice.AutoSize = true;
            Controls.Add(lblPrice);

            _txtPrice = new TextBox();
            _txtPrice.BackColor = ColSurface;
            _txtPrice.ForeColor = ColText;
            _txtPrice.BorderStyle = BorderStyle.FixedSingle;
            _txtPrice.Location = new Point(158, 165);
            _txtPrice.Size = new Size(106, 24);
            _txtPrice.Text = current.PricePerKwh.ToString("0.00", _culture);
            _txtPrice.TextChanged += delegate
            {
                if (!_suppressCustomReset)
                    _selectedKey = "custom";
            };
            Controls.Add(_txtPrice);

            _cmbCurrency = new ComboBox();
            _cmbCurrency.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCurrency.BackColor = ColSurface;
            _cmbCurrency.ForeColor = ColText;
            _cmbCurrency.FlatStyle = FlatStyle.Flat;
            _cmbCurrency.Location = new Point(272, 164);
            _cmbCurrency.Size = new Size(100, 24);
            _cmbCurrency.Items.AddRange(new object[] { "TL (₺)", "USD ($)", "EUR (€)", "GBP (£)" });
            SelectCurrencyInCombo(current.CurrencyCode);
            _cmbCurrency.SelectedIndexChanged += delegate
            {
                if (!_suppressCustomReset)
                    _selectedKey = "custom";
            };
            Controls.Add(_cmbCurrency);

            Label lblWatt = new Label();
            lblWatt.Text = _isTurkish
                ? string.Format(_culture, "Özel Güç (Watt) — 0 = Otomatik (~{0} W):", autoWatts)
                : string.Format(_culture, "Custom Power (Watts) — 0 = Auto (~{0} W):", autoWatts);
            lblWatt.ForeColor = ColText;
            lblWatt.Location = new Point(18, 204);
            lblWatt.AutoSize = true;
            Controls.Add(lblWatt);

            _txtCustomWatts = new TextBox();
            _txtCustomWatts.BackColor = ColSurface;
            _txtCustomWatts.ForeColor = ColText;
            _txtCustomWatts.BorderStyle = BorderStyle.FixedSingle;
            _txtCustomWatts.Location = new Point(18, 227);
            _txtCustomWatts.Size = new Size(106, 24);
            _txtCustomWatts.Text = current.CustomWatts.ToString(CultureInfo.InvariantCulture);
            Controls.Add(_txtCustomWatts);

            Label lblWattHint = new Label();
            lblWattHint.Text = _isTurkish
                ? "Wattmetre ölçümünüz varsa girebilirsiniz."
                : "Optional: enter a real watt-meter reading.";
            lblWattHint.ForeColor = ColMuted;
            lblWattHint.Font = new Font("Segoe UI", 8.2f);
            lblWattHint.Location = new Point(132, 231);
            lblWattHint.AutoSize = true;
            Controls.Add(lblWattHint);

            _chkMonitors = new CheckBox();
            _chkMonitors.Text = _isTurkish
                ? "Monitör elektrik tüketimini hesaba dahil et"
                : "Include monitor power consumption in estimate";
            _chkMonitors.ForeColor = ColText;
            _chkMonitors.Checked = current.IncludeMonitors;
            _chkMonitors.Location = new Point(18, 264);
            _chkMonitors.AutoSize = true;
            Controls.Add(_chkMonitors);

            _lblError = new Label();
            _lblError.Text = "";
            _lblError.ForeColor = Color.FromArgb(248, 113, 113);
            _lblError.Font = new Font("Segoe UI", 8.3f);
            _lblError.Location = new Point(18, 292);
            _lblError.Size = new Size(354, 20);
            Controls.Add(_lblError);

            Button btnSave = new Button();
            btnSave.Text = _isTurkish ? "Kaydet" : "Save";
            btnSave.FlatStyle = FlatStyle.Flat;
            btnSave.BackColor = Color.FromArgb(26, 61, 46);
            btnSave.ForeColor = Color.FromArgb(110, 231, 183);
            btnSave.FlatAppearance.BorderColor = Color.FromArgb(52, 211, 153);
            btnSave.Location = new Point(186, 318);
            btnSave.Size = new Size(90, 32);
            btnSave.Cursor = Cursors.Hand;
            btnSave.Click += BtnSave_Click;
            Controls.Add(btnSave);

            Button btnCancel = new Button();
            btnCancel.Text = _isTurkish ? "İptal" : "Cancel";
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.BackColor = ColSurface;
            btnCancel.ForeColor = ColText;
            btnCancel.FlatAppearance.BorderColor = ColBorder;
            btnCancel.Location = new Point(282, 318);
            btnCancel.Size = new Size(90, 32);
            btnCancel.Cursor = Cursors.Hand;
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = btnSave;
            CancelButton = btnCancel;

            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.ApplyDarkTitleBar(Handle, ColBg, ColText);
        }

        private Button CreatePresetButton(string text, Point loc, Size size, Color foreColor)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = loc;
            btn.Size = size;
            btn.FlatStyle = FlatStyle.Flat;
            btn.BackColor = ColSurface;
            btn.ForeColor = foreColor;
            btn.FlatAppearance.BorderColor = ColBorder;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 46, 58);
            btn.Cursor = Cursors.Hand;
            btn.Font = new Font("Segoe UI", 8.2f, FontStyle.Bold);
            return btn;
        }

        private void SelectCurrencyInCombo(string code)
        {
            if (code == "USD") _cmbCurrency.SelectedIndex = 1;
            else if (code == "EUR") _cmbCurrency.SelectedIndex = 2;
            else if (code == "GBP") _cmbCurrency.SelectedIndex = 3;
            else _cmbCurrency.SelectedIndex = 0;
        }

        private string GetSelectedCurrencyCode()
        {
            if (_cmbCurrency.SelectedIndex == 1) return "USD";
            if (_cmbCurrency.SelectedIndex == 2) return "EUR";
            if (_cmbCurrency.SelectedIndex == 3) return "GBP";
            return "TL";
        }

        private void ApplyPreset(double price, string key, string currencyCode)
        {
            _suppressCustomReset = true;
            try
            {
                _txtPrice.Text = price.ToString("0.00", _culture);
                SelectCurrencyInCombo(currencyCode);
                _selectedKey = key;
                _lblError.Text = "";
            }
            finally
            {
                _suppressCustomReset = false;
            }
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            string rawPrice = (_txtPrice.Text ?? "").Trim().Replace(" ", "");
            double parsedPrice;
            bool okPrice = double.TryParse(rawPrice, NumberStyles.Float, _culture, out parsedPrice) ||
                           double.TryParse(rawPrice.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsedPrice);

            if (!okPrice || parsedPrice <= 0.005 || parsedPrice > 1000.0)
            {
                _lblError.Text = _isTurkish
                    ? "Lütfen geçerli bir kWh fiyatı girin (ör. 4,05)."
                    : "Please enter a valid kWh rate (e.g. 0.17).";
                return;
            }

            string rawWatt = (_txtCustomWatts.Text ?? "").Trim();
            int parsedWatt = 0;
            if (!string.IsNullOrEmpty(rawWatt))
            {
                if (!int.TryParse(rawWatt, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedWatt) || parsedWatt < 0 || parsedWatt > 5000)
                {
                    _lblError.Text = _isTurkish
                        ? "Özel Watt değeri 0 ile 5000 arasında olmalıdır."
                        : "Custom wattage must be between 0 and 5000.";
                    return;
                }
            }

            string currency = GetSelectedCurrencyCode();
            ResultPrice = parsedPrice;
            ResultCurrencyCode = currency;
            ResultTariffKey = DetectPresetKey(parsedPrice, currency, _selectedKey);
            ResultCustomWatts = parsedWatt;
            ResultIncludeMonitors = _chkMonitors.Checked;

            DialogResult = DialogResult.OK;
            Close();
        }

        private static string DetectPresetKey(double price, string currency, string fallback)
        {
            if (currency == "TL")
            {
                if (Math.Abs(price - UserSettings.TariffTrLow) < 0.005) return "tr_low";
                if (Math.Abs(price - UserSettings.TariffTrMixed) < 0.005) return "tr_mixed";
                if (Math.Abs(price - UserSettings.TariffTrHigh) < 0.005) return "tr_high";
            }
            else if (currency == "USD" && Math.Abs(price - UserSettings.TariffUsAvg) < 0.005)
            {
                return "us_avg";
            }
            else if (currency == "EUR" && Math.Abs(price - UserSettings.TariffEuAvg) < 0.005)
            {
                return "eu_avg";
            }
            else if (currency == "GBP" && Math.Abs(price - UserSettings.TariffUkAvg) < 0.005)
            {
                return "uk_avg";
            }
            return string.IsNullOrEmpty(fallback) ? "custom" : fallback;
        }
    }

    internal sealed class UptimeForm : Form
    {
        private const int LogicalWidth = 480;
        private const int LogicalHeight = 544;

        private const int HitNone = -1;
        private const int HitBtnCopy = 0;
        private const int HitBtnPin = 1;
        private const int HitBtnTray = 2;
        private const int HitTariffPill = 3;
        private const int HitProfLight = 4;
        private const int HitProfAvg = 5;
        private const int HitProfHeavy = 6;
        private const int HitLangToggle = 7;

        private static readonly Color ColBg = Color.FromArgb(13, 17, 23);
        private static readonly Color ColCard = Color.FromArgb(22, 27, 34);
        private static readonly Color ColCardBorder = Color.FromArgb(42, 51, 64);
        private static readonly Color ColEnergyBorder = Color.FromArgb(56, 62, 74);
        private static readonly Color ColDivider = Color.FromArgb(36, 44, 56);
        private static readonly Color ColTrackBg = Color.FromArgb(14, 18, 24);
        private static readonly Color ColTextPrimary = Color.FromArgb(240, 246, 252);
        private static readonly Color ColTextBody = Color.FromArgb(208, 215, 222);
        private static readonly Color ColTextSecondary = Color.FromArgb(139, 148, 158);
        private static readonly Color ColTextMuted = Color.FromArgb(110, 118, 129);
        private static readonly Color ColTextDim = Color.FromArgb(88, 96, 108);
        private static readonly Color ColAccent = Color.FromArgb(52, 211, 153);
        private static readonly Color ColAccentSoft = Color.FromArgb(94, 234, 212);
        private static readonly Color ColAmber = Color.FromArgb(251, 191, 36);
        private static readonly Color ColAmberDark = Color.FromArgb(245, 158, 11);
        private static readonly Color ColAmberBg = Color.FromArgb(43, 36, 22);
        private static readonly Color ColAmberBorder = Color.FromArgb(180, 118, 24);
        private static readonly Color ColBtnNormal = Color.FromArgb(30, 36, 45);
        private static readonly Color ColBtnHover = Color.FromArgb(43, 51, 63);
        private static readonly Color ColBtnPress = Color.FromArgb(22, 27, 34);
        private static readonly Color ColBtnBorder = Color.FromArgb(52, 62, 77);
        private static readonly Color ColBtnActiveBg = Color.FromArgb(17, 45, 42);
        private static readonly Color ColBtnActiveHover = Color.FromArgb(22, 58, 54);
        private static readonly Color ColBtnActiveBorder = Color.FromArgb(45, 212, 191);
        private static readonly Color ColCopiedBg = Color.FromArgb(18, 46, 30);
        private static readonly Color ColCopiedBorder = Color.FromArgb(52, 211, 153);

        private readonly LaunchOptions _launch;
        private readonly System.Windows.Forms.Timer _tickTimer;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly ToolStripMenuItem _miShow;
        private readonly ToolStripMenuItem _miCopy;
        private readonly ToolStripMenuItem _miExit;
        private readonly Icon _appIcon;
        private readonly UserSettings _settings;

        private HardwareInfo _hardware;
        private float _scale = 1.0f;
        private TimeSpan _uptime;
        private TimeSpan _awakeTime;
        private bool _hasAwakeTime;
        private DateTime _bootTime;
        private long _lastRenderedSecond = -1;
        private ulong _demoStartTick;

        private DateTime _copiedUntil = DateTime.MinValue;
        private bool _isCopiedState;

        private RectangleF _langToggleRect;
        private RectangleF _btnCopyRect;
        private RectangleF _btnPinRect;
        private RectangleF _btnTrayRect;
        private RectangleF _tariffPillRect;
        private RectangleF _profLightRect;
        private RectangleF _profAvgRect;
        private RectangleF _profHeavyRect;

        private int _hoverHit = HitNone;
        private int _pressedHit = HitNone;

        private Font _fontHeader;
        private Font _fontMachine;
        private Font _fontLang;
        private Font _fontHeroNum;
        private Font _fontHeroUnit;
        private Font _fontHeroSub;
        private Font _fontTileNum;
        private Font _fontTileLabel;
        private Font _fontInfoLabel;
        private Font _fontInfoValue;
        private Font _fontCostNum;
        private Font _fontCostUnit;
        private Font _fontCostDetail;
        private Font _fontCostSub;
        private Font _fontPill;
        private Font _fontSeg;
        private Font _fontHwTag;
        private Font _fontHwVal;
        private Font _fontButton;

        public UptimeForm(LaunchOptions launch)
        {
            _launch = launch ?? new LaunchOptions();
            _settings = _launch.DemoMode ? new UserSettings() : UserSettings.Load();
            ApplyLaunchOverrides();

            _demoStartTick = NativeMethods.GetTickCount64();

            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ColBg;
            DoubleBuffered = true;
            KeyPreview = true;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw,
                true);

            _hardware = new HardwareInfo();

            _appIcon = LoadAppIcon();
            if (_appIcon != null)
                Icon = _appIcon;

            _miShow = new ToolStripMenuItem("", null, delegate { RestoreFromTray(); });
            _miCopy = new ToolStripMenuItem("", null, delegate { CopyUptimeToClipboard(); });
            _miExit = new ToolStripMenuItem("", null, delegate { Close(); });

            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add(_miShow);
            _trayMenu.Items.Add(_miCopy);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_miExit);

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = _appIcon != null ? _appIcon : SystemIcons.Application;
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Visible = false;
            _trayIcon.MouseClick += TrayIcon_MouseClick;
            _trayIcon.DoubleClick += delegate { RestoreFromTray(); };

            ApplyLocalizedChrome();

            UpdateDpiScale(GetInitialDpi());
            ClientSize = new Size(S(LogicalWidth), S(LogicalHeight));

            RefreshUptimeData(true);
            StartHardwareDetectionAsync();

            _tickTimer = new System.Windows.Forms.Timer();
            _tickTimer.Interval = 250;
            _tickTimer.Tick += TickTimer_Tick;
            _tickTimer.Start();
        }

        private void ApplyLaunchOverrides()
        {
            if (!string.IsNullOrEmpty(_launch.ForceLang))
                _settings.Language = _launch.ForceLang;

            if (_launch.ForcePreset == "tr")
            {
                _settings.PricePerKwh = UserSettings.TariffTrMixed;
                _settings.TariffKey = "tr_mixed";
                _settings.CurrencyCode = "TL";
            }
            else if (_launch.ForcePreset == "us")
            {
                _settings.PricePerKwh = UserSettings.TariffUsAvg;
                _settings.TariffKey = "us_avg";
                _settings.CurrencyCode = "USD";
            }
            else if (_launch.ForcePreset == "eu")
            {
                _settings.PricePerKwh = UserSettings.TariffEuAvg;
                _settings.TariffKey = "eu_avg";
                _settings.CurrencyCode = "EUR";
            }
        }

        private void ApplyLocalizedChrome()
        {
            bool tr = _settings.IsTurkish;
            Text = tr ? "PC Çalışma Süresi" : "PC Uptime & Power Cost";
            _miShow.Text = tr ? "Pencereyi Göster" : "Show Window";
            _miCopy.Text = tr ? "Özeti Kopyala" : "Copy Summary";
            _miExit.Text = tr ? "Çıkış" : "Exit";
        }

        private void SaveSettingsIfAllowed()
        {
            if (!_launch.DemoMode)
                _settings.Save();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
        }

        private void StartHardwareDetectionAsync()
        {
            int monitorCount = 1;
            try
            {
                monitorCount = Math.Max(1, Screen.AllScreens.Length);
            }
            catch
            {
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                HardwareInfo detected = HardwareDetector.Detect(monitorCount);
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _hardware = detected;
                            UpdateTrayTooltip();
                            Invalidate();
                            if (_launch.ShowSettingsOnStart)
                            {
                                _launch.ShowSettingsOnStart = false;
                                OpenTariffSettings();
                            }
                        });
                    }
                    else
                    {
                        _hardware = detected;
                    }
                }
                catch
                {
                }
            });
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                Icon extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null)
                    return extracted;
            }
            catch
            {
            }

            return null;
        }

        private float GetInitialDpi()
        {
            using (Graphics g = CreateGraphics())
            {
                return g.DpiX > 0 ? g.DpiX : 96f;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.ApplyDarkTitleBar(Handle, ColBg, ColTextPrimary);

            try
            {
                uint winDpi = NativeMethods.GetDpiForWindow(Handle);
                if (winDpi >= 72)
                {
                    float newScale = winDpi / 96f;
                    if (Math.Abs(newScale - _scale) > 0.01f)
                    {
                        UpdateDpiScale(winDpi);
                        ClientSize = new Size(S(LogicalWidth), S(LogicalHeight));
                    }
                }
            }
            catch
            {
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_DPICHANGED)
            {
                int newDpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (newDpi >= 72)
                {
                    UpdateDpiScale(newDpi);
                    ClientSize = new Size(S(LogicalWidth), S(LogicalHeight));
                    Invalidate();
                }
            }

            base.WndProc(ref m);
        }

        private void UpdateDpiScale(float dpi)
        {
            _scale = dpi / 96f;
            RebuildFonts();
            UpdateInteractiveRects();
        }

        private int S(float logical)
        {
            return (int)Math.Round(logical * _scale);
        }

        private float Sf(float logical)
        {
            return logical * _scale;
        }

        private float PtToPx(float pt)
        {
            return pt * (96f / 72f) * _scale;
        }

        private void RebuildFonts()
        {
            DisposeFonts();
            _fontHeader = new Font("Segoe UI", PtToPx(8.4f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontMachine = new Font("Segoe UI", PtToPx(8.3f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontLang = new Font("Segoe UI", PtToPx(7.4f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontHeroNum = new Font("Segoe UI", PtToPx(29.5f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontHeroUnit = new Font("Segoe UI", PtToPx(14f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontHeroSub = new Font("Segoe UI", PtToPx(9f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontTileNum = new Font("Segoe UI", PtToPx(20.5f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontTileLabel = new Font("Segoe UI", PtToPx(7.3f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontInfoLabel = new Font("Segoe UI", PtToPx(7.1f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontInfoValue = new Font("Segoe UI", PtToPx(10.2f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontCostNum = new Font("Segoe UI", PtToPx(25f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontCostUnit = new Font("Segoe UI", PtToPx(12.5f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontCostDetail = new Font("Segoe UI", PtToPx(9.4f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontCostSub = new Font("Segoe UI", PtToPx(8.3f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontPill = new Font("Segoe UI", PtToPx(7.7f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontSeg = new Font("Segoe UI", PtToPx(8.0f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontHwTag = new Font("Segoe UI", PtToPx(7.3f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontHwVal = new Font("Segoe UI", PtToPx(8.5f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontButton = new Font("Segoe UI", PtToPx(8.6f), FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            DisposeObj(ref _fontHeader);
            DisposeObj(ref _fontMachine);
            DisposeObj(ref _fontLang);
            DisposeObj(ref _fontHeroNum);
            DisposeObj(ref _fontHeroUnit);
            DisposeObj(ref _fontHeroSub);
            DisposeObj(ref _fontTileNum);
            DisposeObj(ref _fontTileLabel);
            DisposeObj(ref _fontInfoLabel);
            DisposeObj(ref _fontInfoValue);
            DisposeObj(ref _fontCostNum);
            DisposeObj(ref _fontCostUnit);
            DisposeObj(ref _fontCostDetail);
            DisposeObj(ref _fontCostSub);
            DisposeObj(ref _fontPill);
            DisposeObj(ref _fontSeg);
            DisposeObj(ref _fontHwTag);
            DisposeObj(ref _fontHwVal);
            DisposeObj(ref _fontButton);
        }

        private static void DisposeObj<T>(ref T obj) where T : class, IDisposable
        {
            if (obj != null)
            {
                obj.Dispose();
                obj = null;
            }
        }

        private void UpdateInteractiveRects()
        {
            _langToggleRect = new RectangleF(Sf(402f), Sf(14f), Sf(58f), Sf(21f));
            _tariffPillRect = new RectangleF(Sf(290f), Sf(283f), Sf(156f), Sf(23f));

            float segY = Sf(364f);
            float segH = Sf(26f);
            float trackX = Sf(34f);
            float trackW = Sf(412f);
            float pad = Sf(2f);
            float segW = (trackW - pad * 4f) / 3f;

            _profLightRect = new RectangleF(trackX + pad, segY + pad, segW, segH - pad * 2f);
            _profAvgRect = new RectangleF(trackX + pad * 2f + segW, segY + pad, segW, segH - pad * 2f);
            _profHeavyRect = new RectangleF(trackX + pad * 3f + segW * 2f, segY + pad, segW, segH - pad * 2f);

            float btnY = Sf(494f);
            float btnH = Sf(34f);
            float btnW = Sf(140.66f);
            float btnGap = Sf(9f);
            float x0 = Sf(20f);

            _btnCopyRect = new RectangleF(x0, btnY, btnW, btnH);
            _btnPinRect = new RectangleF(x0 + btnW + btnGap, btnY, btnW, btnH);
            _btnTrayRect = new RectangleF(x0 + (btnW + btnGap) * 2f, btnY, btnW, btnH);
        }

        private int GetActiveWatts()
        {
            if (_settings.CustomWatts > 0)
                return _settings.CustomWatts;
            return _hardware.GetEstimatedWallWatts(_settings.Profile, _settings.IncludeMonitors);
        }

        private double CalculateTotalKwh()
        {
            int watts = GetActiveWatts();
            double awakeHours = _hasAwakeTime ? _awakeTime.TotalHours : _uptime.TotalHours;
            double sleepHours = _hasAwakeTime ? Math.Max(0.0, (_uptime - _awakeTime).TotalHours) : 0.0;
            return (awakeHours * watts + sleepHours * 4.0) / 1000.0;
        }

        private double CalculateTotalCost()
        {
            return CalculateTotalKwh() * _settings.PricePerKwh;
        }

        private double CalculateHourlyCost()
        {
            return (GetActiveWatts() / 1000.0) * _settings.PricePerKwh;
        }

        private void TickTimer_Tick(object sender, EventArgs e)
        {
            bool needRepaint = RefreshUptimeData(false);

            bool copiedNow = DateTime.UtcNow < _copiedUntil;
            if (copiedNow != _isCopiedState)
            {
                _isCopiedState = copiedNow;
                needRepaint = true;
            }

            if (needRepaint && Visible)
                Invalidate();
        }

        private bool RefreshUptimeData(bool forceBootCalc)
        {
            if (_launch.DemoMode && _launch.DemoHours > 0)
            {
                ulong elapsed = NativeMethods.GetTickCount64() - _demoStartTick;
                _uptime = TimeSpan.FromHours(_launch.DemoHours) + TimeSpan.FromMilliseconds(elapsed);
                _awakeTime = _uptime;
                _hasAwakeTime = true;
            }
            else
            {
                ulong ms = NativeMethods.GetTickCount64();
                _uptime = TimeSpan.FromMilliseconds(ms);

                ulong unbiasedTicks;
                if (NativeMethods.QueryUnbiasedInterruptTime(out unbiasedTicks))
                {
                    _awakeTime = TimeSpan.FromTicks((long)unbiasedTicks);
                    _hasAwakeTime = true;
                }
                else
                {
                    _awakeTime = _uptime;
                    _hasAwakeTime = false;
                }
            }

            DateTime calcBoot = DateTime.Now - _uptime;
            if (forceBootCalc || Math.Abs((calcBoot - _bootTime).TotalSeconds) > 5.0)
            {
                _bootTime = calcBoot;
            }

            long wholeSecond = (long)_uptime.TotalSeconds;
            if (wholeSecond != _lastRenderedSecond || forceBootCalc)
            {
                _lastRenderedSecond = wholeSecond;
                UpdateTrayTooltip();
                return true;
            }

            return false;
        }

        private void UpdateTrayTooltip()
        {
            if (_trayIcon == null)
                return;

            CultureInfo c = _settings.ActiveCulture;
            string hoursText = FormatHoursShort(_uptime.TotalHours, c);
            string costText = _settings.FormatCompactMoney(CalculateTotalCost(), "N2");
            string tip = _settings.IsTurkish
                ? string.Format(c, "PC Açık: {0} saat | ~{1}", hoursText, costText)
                : string.Format(c, "PC Uptime: {0} hrs | ~{1}", hoursText, costText);

            if (tip.Length > 63)
                tip = tip.Substring(0, 63);

            _trayIcon.Text = tip;
        }

        private static string FormatHoursValue(double totalHours, CultureInfo c)
        {
            if (totalHours >= 1000.0)
                return totalHours.ToString("N1", c);
            return totalHours.ToString("N2", c);
        }

        private static string FormatHoursShort(double totalHours, CultureInfo c)
        {
            if (totalHours >= 100.0)
                return totalHours.ToString("N1", c);
            return totalHours.ToString("N2", c);
        }

        private string FormatHumanSentence(TimeSpan ts)
        {
            CultureInfo c = _settings.ActiveCulture;
            StringBuilder sb = new StringBuilder();
            if (_settings.IsTurkish)
            {
                if (ts.Days > 0)
                    sb.AppendFormat(c, "{0} gün ", ts.Days);
                if (ts.Days > 0 || ts.Hours > 0)
                    sb.AppendFormat(c, "{0} saat ", ts.Hours);
                if (ts.Days > 0 || ts.Hours > 0 || ts.Minutes > 0)
                    sb.AppendFormat(c, "{0} dakika ", ts.Minutes);

                sb.AppendFormat(c, "{0:D2} saniyedir açık", ts.Seconds);
            }
            else
            {
                sb.Append("Up for ");
                if (ts.Days > 0)
                    sb.AppendFormat(c, "{0} {1} ", ts.Days, ts.Days == 1 ? "day" : "days");
                if (ts.Days > 0 || ts.Hours > 0)
                    sb.AppendFormat(c, "{0} hr ", ts.Hours);
                if (ts.Days > 0 || ts.Hours > 0 || ts.Minutes > 0)
                    sb.AppendFormat(c, "{0} min ", ts.Minutes);

                sb.AppendFormat(c, "{0:D2} sec", ts.Seconds);
            }

            return sb.ToString();
        }

        private string GetProfileName(UsageProfile p)
        {
            if (_settings.IsTurkish)
            {
                if (p == UsageProfile.Light) return "Hafif";
                if (p == UsageProfile.Heavy) return "Oyun/Yük";
                return "Ortalama";
            }

            if (p == UsageProfile.Light) return "Light";
            if (p == UsageProfile.Heavy) return "Gaming";
            return "Average";
        }

        private void CopyUptimeToClipboard()
        {
            try
            {
                CultureInfo c = _settings.ActiveCulture;
                bool tr = _settings.IsTurkish;
                double kwh = CalculateTotalKwh();
                double cost = CalculateTotalCost();
                int watts = GetActiveWatts();

                StringBuilder sb = new StringBuilder();
                if (tr)
                {
                    sb.AppendFormat(c, "PC Çalışma Süresi: {0} saat ({1})", FormatHoursValue(_uptime.TotalHours, c), FormatHumanSentence(_uptime));
                    sb.AppendLine();
                    sb.AppendFormat(c, "Son Açılış: {0:dd.MM.yyyy HH:mm:ss}", _bootTime);
                    if (_hasAwakeTime && Math.Abs((_uptime - _awakeTime).TotalMinutes) >= 1.0)
                        sb.AppendFormat(c, " | Aktif (Uyku Hariç): {0} saat", FormatHoursValue(_awakeTime.TotalHours, c));
                    sb.AppendLine();
                    sb.AppendFormat(
                        c,
                        "Tahmini Elektrik Maliyeti: {0} ({1:N2} kWh | ~{2} W {3} | {4})",
                        _settings.FormatCompactMoney(cost, "N2"),
                        kwh,
                        watts,
                        _settings.CustomWatts > 0 ? "Özel" : GetProfileName(_settings.Profile),
                        _settings.FormatRatePerKwh());

                    if (_hardware.IsDetected)
                    {
                        sb.AppendLine();
                        sb.AppendFormat(c, "Donanım: {0} · {1} · {2}", _hardware.CpuName, _hardware.GpuName, _hardware.BuildSystemSummary(true, c));
                    }
                }
                else
                {
                    sb.AppendFormat(c, "PC Uptime: {0} hours ({1})", FormatHoursValue(_uptime.TotalHours, c), FormatHumanSentence(_uptime));
                    sb.AppendLine();
                    sb.AppendFormat(c, "Last Boot: {0:yyyy-MM-dd HH:mm:ss}", _bootTime);
                    if (_hasAwakeTime && Math.Abs((_uptime - _awakeTime).TotalMinutes) >= 1.0)
                        sb.AppendFormat(c, " | Active (excl. sleep): {0} hours", FormatHoursValue(_awakeTime.TotalHours, c));
                    sb.AppendLine();
                    sb.AppendFormat(
                        c,
                        "Estimated Electricity Cost: {0} ({1:N2} kWh | ~{2} W {3} | {4})",
                        _settings.FormatCompactMoney(cost, "N2"),
                        kwh,
                        watts,
                        _settings.CustomWatts > 0 ? "Custom" : GetProfileName(_settings.Profile),
                        _settings.FormatRatePerKwh());

                    if (_hardware.IsDetected)
                    {
                        sb.AppendLine();
                        sb.AppendFormat(c, "Hardware: {0} · {1} · {2}", _hardware.CpuName, _hardware.GpuName, _hardware.BuildSystemSummary(false, c));
                    }
                }

                Clipboard.SetText(sb.ToString());
                _copiedUntil = DateTime.UtcNow.AddMilliseconds(1600);
                _isCopiedState = true;
                Invalidate();
            }
            catch
            {
            }
        }

        private void ToggleLanguage()
        {
            _settings.Language = _settings.IsTurkish ? "en" : "tr";
            SaveSettingsIfAllowed();
            ApplyLocalizedChrome();
            UpdateTrayTooltip();
            Invalidate();
        }

        private void OpenTariffSettings()
        {
            int autoWatts = _hardware.GetEstimatedWallWatts(_settings.Profile, _settings.IncludeMonitors);
            using (TariffSettingsDialog dlg = new TariffSettingsDialog(_settings, autoWatts))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _settings.PricePerKwh = dlg.ResultPrice;
                    _settings.TariffKey = dlg.ResultTariffKey;
                    _settings.CurrencyCode = dlg.ResultCurrencyCode;
                    _settings.CustomWatts = dlg.ResultCustomWatts;
                    _settings.IncludeMonitors = dlg.ResultIncludeMonitors;
                    SaveSettingsIfAllowed();
                    UpdateTrayTooltip();
                    Invalidate();
                }
            }
        }

        private void SelectProfile(UsageProfile profile)
        {
            if (_settings.Profile != profile || _settings.CustomWatts > 0)
            {
                _settings.Profile = profile;
                _settings.CustomWatts = 0;
                SaveSettingsIfAllowed();
                UpdateTrayTooltip();
                Invalidate();
            }
        }

        private void ToggleTopMost()
        {
            TopMost = !TopMost;
            Invalidate();
        }

        private void MinimizeToTray()
        {
            UpdateTrayTooltip();
            _trayIcon.Visible = true;
            Hide();
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            _trayIcon.Visible = false;
            RefreshUptimeData(false);
            Invalidate();
        }

        private void TrayIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                RestoreFromTray();
        }

        private int HitTest(Point pt)
        {
            if (_langToggleRect.Contains(pt)) return HitLangToggle;
            if (_btnCopyRect.Contains(pt)) return HitBtnCopy;
            if (_btnPinRect.Contains(pt)) return HitBtnPin;
            if (_btnTrayRect.Contains(pt)) return HitBtnTray;
            if (_tariffPillRect.Contains(pt)) return HitTariffPill;
            if (_profLightRect.Contains(pt)) return HitProfLight;
            if (_profAvgRect.Contains(pt)) return HitProfAvg;
            if (_profHeavyRect.Contains(pt)) return HitProfHeavy;
            return HitNone;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitTest(e.Location);
            if (hit != _hoverHit)
            {
                _hoverHit = hit;
                Cursor = hit != HitNone ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverHit != HitNone || _pressedHit != HitNone)
            {
                _hoverHit = HitNone;
                _pressedHit = HitNone;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                int hit = HitTest(e.Location);
                if (hit != _pressedHit)
                {
                    _pressedHit = hit;
                    Invalidate();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                int releasedOn = HitTest(e.Location);
                int wasPressed = _pressedHit;
                _pressedHit = HitNone;

                if (wasPressed != HitNone && wasPressed == releasedOn)
                {
                    if (wasPressed == HitLangToggle) ToggleLanguage();
                    else if (wasPressed == HitBtnCopy) CopyUptimeToClipboard();
                    else if (wasPressed == HitBtnPin) ToggleTopMost();
                    else if (wasPressed == HitBtnTray) MinimizeToTray();
                    else if (wasPressed == HitTariffPill) OpenTariffSettings();
                    else if (wasPressed == HitProfLight) SelectProfile(UsageProfile.Light);
                    else if (wasPressed == HitProfAvg) SelectProfile(UsageProfile.Average);
                    else if (wasPressed == HitProfHeavy) SelectProfile(UsageProfile.Heavy);
                }

                Invalidate();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopyUptimeToClipboard();
                e.Handled = true;
            }
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.T)
            {
                ToggleTopMost();
                e.Handled = true;
            }
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.H)
            {
                MinimizeToTray();
                e.Handled = true;
            }
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.L)
            {
                ToggleLanguage();
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            g.Clear(ColBg);

            DrawHeader(g);
            DrawHeroCard(g);
            DrawTimeTiles(g);
            DrawInfoCards(g);
            DrawEnergyAndHardwareCard(g);
            DrawButtons(g);
        }

        private void DrawHeader(Graphics g)
        {
            bool tr = _settings.IsTurkish;
            float padX = Sf(20f);
            float centerY = Sf(24.5f);

            float haloR = Sf(7.2f);
            using (SolidBrush haloBrush = new SolidBrush(Color.FromArgb(46, ColAccent)))
                g.FillEllipse(haloBrush, padX, centerY - haloR, haloR * 2f, haloR * 2f);

            float dotR = Sf(3.6f);
            using (SolidBrush dotBrush = new SolidBrush(ColAccent))
                g.FillEllipse(dotBrush, padX + haloR - dotR, centerY - dotR, dotR * 2f, dotR * 2f);

            RectangleF titleRect = new RectangleF(padX + Sf(19f), Sf(14f), Sf(210f), Sf(21f));
            using (SolidBrush b = new SolidBrush(ColTextSecondary))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Alignment = StringAlignment.Near;
                g.DrawString(tr ? "SİSTEM ÇALIŞMA SÜRESİ" : "SYSTEM UPTIME", _fontHeader, b, titleRect, sf);
            }

            string machineName = _launch.DemoMode ? "WORKSTATION" : Environment.MachineName;
            RectangleF machineRect = new RectangleF(Sf(248f), Sf(14f), Sf(144f), Sf(21f));
            using (SolidBrush b = new SolidBrush(ColTextMuted))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Alignment = StringAlignment.Far;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(machineName, _fontMachine, b, machineRect, sf);
            }

            // TR | EN Language Switcher Pill
            bool langHover = _hoverHit == HitLangToggle;
            Color langBg = langHover ? ColBtnHover : ColCard;
            Color langBorder = langHover ? Color.FromArgb(72, 85, 104) : ColCardBorder;
            DrawRoundedCard(g, _langToggleRect, Sf(6f), langBg, langBorder);

            float halfW = _langToggleRect.Width * 0.5f;
            RectangleF trRect = new RectangleF(_langToggleRect.X + Sf(2f), _langToggleRect.Y, halfW - Sf(2f), _langToggleRect.Height);
            RectangleF enRect = new RectangleF(_langToggleRect.X + halfW, _langToggleRect.Y, halfW - Sf(2f), _langToggleRect.Height);

            using (SolidBrush trB = new SolidBrush(tr ? ColAccent : ColTextMuted))
            using (SolidBrush enB = new SolidBrush(!tr ? ColAccent : ColTextMuted))
            using (Pen sep = new Pen(ColDivider, 1f))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("TR", _fontLang, trB, trRect, sf);
                g.DrawString("EN", _fontLang, enB, enRect, sf);
                g.DrawLine(sep, _langToggleRect.X + halfW, _langToggleRect.Y + Sf(4f), _langToggleRect.X + halfW, _langToggleRect.Bottom - Sf(4f));
            }
        }

        private void DrawHeroCard(Graphics g)
        {
            CultureInfo c = _settings.ActiveCulture;
            RectangleF card = new RectangleF(Sf(20f), Sf(44f), Sf(440f), Sf(82f));
            DrawRoundedCard(g, card, Sf(12f), ColCard, ColCardBorder);

            string numText = FormatHoursValue(_uptime.TotalHours, c);
            string unitText = _settings.IsTurkish ? "saat" : "hours";
            float unitGap = Sf(6f);

            using (StringFormat typo = new StringFormat(StringFormat.GenericTypographic))
            {
                typo.FormatFlags |= StringFormatFlags.NoWrap;

                SizeF numSize = g.MeasureString(numText, _fontHeroNum, PointF.Empty, typo);
                SizeF unitSize = g.MeasureString(unitText, _fontHeroUnit, PointF.Empty, typo);

                float totalW = numSize.Width + unitGap + unitSize.Width;
                float startX = card.X + (card.Width - totalW) * 0.5f;
                float numY = card.Y + Sf(9f);

                float ascentNum = GetFontAscentPx(_fontHeroNum);
                float ascentUnit = GetFontAscentPx(_fontHeroUnit);
                float unitY = numY + ascentNum - ascentUnit;

                using (SolidBrush numBrush = new SolidBrush(ColTextPrimary))
                using (SolidBrush unitBrush = new SolidBrush(ColAccent))
                {
                    g.DrawString(numText, _fontHeroNum, numBrush, startX, numY, typo);
                    g.DrawString(unitText, _fontHeroUnit, unitBrush, startX + numSize.Width + unitGap, unitY, typo);
                }
            }

            RectangleF subRect = new RectangleF(card.X + Sf(12f), card.Y + Sf(55f), card.Width - Sf(24f), Sf(20f));
            using (SolidBrush subBrush = new SolidBrush(ColTextSecondary))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(FormatHumanSentence(_uptime), _fontHeroSub, subBrush, subRect, sf);
            }
        }

        private void DrawTimeTiles(Graphics g)
        {
            CultureInfo c = _settings.ActiveCulture;
            float startX = Sf(20f);
            float y = Sf(136f);
            float h = Sf(68f);
            float gap = Sf(10f);
            float w = Sf(102.5f);

            string[] values = new string[]
            {
                _uptime.Days.ToString("D2", c),
                _uptime.Hours.ToString("D2", c),
                _uptime.Minutes.ToString("D2", c),
                _uptime.Seconds.ToString("D2", c)
            };
            string[] labels = _settings.IsTurkish
                ? new string[] { "GÜN", "SAAT", "DAKİKA", "SANİYE" }
                : new string[] { "DAYS", "HOURS", "MINUTES", "SECONDS" };

            for (int i = 0; i < 4; i++)
            {
                RectangleF rect = new RectangleF(startX + i * (w + gap), y, w, h);
                DrawRoundedCard(g, rect, Sf(10f), ColCard, ColCardBorder);

                Color valColor = ColTextPrimary;
                if (i == 0 && _uptime.Days == 0)
                    valColor = ColTextDim;
                else if (i == 3)
                    valColor = ColAccentSoft;

                RectangleF numRect = new RectangleF(rect.X, rect.Y + Sf(8f), rect.Width, Sf(34f));
                using (SolidBrush b = new SolidBrush(valColor))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(values[i], _fontTileNum, b, numRect, sf);
                }

                RectangleF lblRect = new RectangleF(rect.X, rect.Y + Sf(44f), rect.Width, Sf(17f));
                using (SolidBrush b = new SolidBrush(ColTextMuted))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(labels[i], _fontTileLabel, b, lblRect, sf);
                }
            }
        }

        private void DrawInfoCards(Graphics g)
        {
            CultureInfo c = _settings.ActiveCulture;
            bool tr = _settings.IsTurkish;
            float y = Sf(214f);
            float h = Sf(50f);
            float w = Sf(215f);
            float gap = Sf(10f);

            RectangleF leftCard = new RectangleF(Sf(20f), y, w, h);
            RectangleF rightCard = new RectangleF(Sf(20f) + w + gap, y, w, h);

            string bootText = tr
                ? _bootTime.ToString("dd.MM.yyyy  HH:mm:ss", c)
                : _bootTime.ToString("yyyy-MM-dd  HH:mm:ss", c);

            DrawSingleInfoCard(
                g,
                leftCard,
                tr ? "SON AÇILIŞ ZAMANI" : "LAST BOOT TIME",
                bootText,
                ColTextPrimary);

            string awakeValue = string.Format(c, "{0} {1}", FormatHoursValue(_awakeTime.TotalHours, c), tr ? "saat" : "hours");
            bool hasSleepDiff = _hasAwakeTime && Math.Abs((_uptime - _awakeTime).TotalMinutes) >= 1.0;
            DrawSingleInfoCard(
                g,
                rightCard,
                tr ? "AKTİF SÜRE (UYKU HARİÇ)" : "ACTIVE TIME (EXCL. SLEEP)",
                awakeValue,
                hasSleepDiff ? ColAccent : ColTextPrimary);
        }

        private void DrawSingleInfoCard(Graphics g, RectangleF rect, string label, string value, Color valueColor)
        {
            DrawRoundedCard(g, rect, Sf(10f), ColCard, ColCardBorder);

            float padX = Sf(13f);
            RectangleF lblRect = new RectangleF(rect.X + padX, rect.Y + Sf(6f), rect.Width - padX * 2f, Sf(15f));
            RectangleF valRect = new RectangleF(rect.X + padX, rect.Y + Sf(22f), rect.Width - padX * 2f, Sf(21f));

            using (SolidBrush lb = new SolidBrush(ColTextMuted))
            using (SolidBrush vb = new SolidBrush(valueColor))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;

                g.DrawString(label, _fontInfoLabel, lb, lblRect, sf);
                g.DrawString(value, _fontInfoValue, vb, valRect, sf);
            }
        }

        private void DrawEnergyAndHardwareCard(Graphics g)
        {
            CultureInfo c = _settings.ActiveCulture;
            bool tr = _settings.IsTurkish;

            RectangleF card = new RectangleF(Sf(20f), Sf(274f), Sf(440f), Sf(208f));
            DrawRoundedCard(g, card, Sf(12f), ColCard, ColEnergyBorder);

            // 1. Header: Lightning bolt + title on left, clickable Tariff pill on right
            DrawLightningIcon(g, new RectangleF(Sf(34f), Sf(288f), Sf(12f), Sf(14f)), ColAmber);

            RectangleF hdrRect = new RectangleF(Sf(50f), Sf(284f), Sf(235f), Sf(21f));
            using (SolidBrush b = new SolidBrush(ColAmber))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(tr ? "TAHMİNİ ELEKTRİK MALİYETİ" : "ESTIMATED ELECTRICITY COST", _fontHeader, b, hdrRect, sf);
            }

            bool pillHover = _hoverHit == HitTariffPill;
            bool pillPress = _pressedHit == HitTariffPill;
            Color pillBg = pillPress ? ColBtnPress : (pillHover ? ColBtnHover : ColBtnNormal);
            Color pillBorder = pillHover ? ColAmberDark : ColBtnBorder;
            DrawRoundedCard(g, _tariffPillRect, Sf(6f), pillBg, pillBorder);

            string pillText = string.Format(c, "{0} · {1}", _settings.GetTariffDisplayLabel(), _settings.FormatRatePerKwh());
            RectangleF pillTextRect = new RectangleF(_tariffPillRect.X + Sf(7f), _tariffPillRect.Y, _tariffPillRect.Width - Sf(21f), _tariffPillRect.Height);
            using (SolidBrush tb = new SolidBrush(pillHover ? ColAmber : ColTextBody))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(pillText, _fontPill, tb, pillTextRect, sf);
            }

            using (Pen cp = new Pen(pillHover ? ColAmber : ColTextMuted, Math.Max(1.2f, Sf(1.3f))))
            {
                cp.StartCap = LineCap.Round;
                cp.EndCap = LineCap.Round;
                cp.LineJoin = LineJoin.Round;
                float cx = _tariffPillRect.Right - Sf(10.5f);
                float cy = _tariffPillRect.Y + _tariffPillRect.Height * 0.5f;
                PointF[] chev = new PointF[]
                {
                    new PointF(cx - Sf(3.2f), cy - Sf(1.5f)),
                    new PointF(cx, cy + Sf(1.8f)),
                    new PointF(cx + Sf(3.2f), cy - Sf(1.5f))
                };
                g.DrawLines(cp, chev);
            }

            // 2. Big Cost Display on left + kWh / Watt / Hourly Rate on right
            double totalCost = CalculateTotalCost();
            double totalKwh = CalculateTotalKwh();
            double hourlyCost = CalculateHourlyCost();
            int activeWatts = GetActiveWatts();

            string costNum = totalCost.ToString("N2", c);
            string costUnit = _settings.CurrencyCode;
            float costUnitGap = Sf(5f);

            using (StringFormat typo = new StringFormat(StringFormat.GenericTypographic))
            {
                typo.FormatFlags |= StringFormatFlags.NoWrap;
                SizeF numSize = g.MeasureString(costNum, _fontCostNum, PointF.Empty, typo);
                float numX = Sf(34f);
                float numY = Sf(314f);
                float unitY = numY + GetFontAscentPx(_fontCostNum) - GetFontAscentPx(_fontCostUnit);

                using (SolidBrush nb = new SolidBrush(ColAmber))
                using (SolidBrush ub = new SolidBrush(ColAmberDark))
                {
                    g.DrawString(costNum, _fontCostNum, nb, numX, numY, typo);
                    g.DrawString(costUnit, _fontCostUnit, ub, numX + numSize.Width + costUnitGap, unitY, typo);
                }
            }

            string lineTop = string.Format(
                c,
                "{0:N2} kWh  ·  {1}{2} W",
                totalKwh,
                _settings.CustomWatts > 0 ? "" : "~",
                activeWatts);

            string hourlyStr = _settings.FormatCompactMoney(hourlyCost, "N2");
            double dailyCost = hourlyCost * 24.0;
            string dailyStr = _settings.FormatCompactMoney(dailyCost, dailyCost < 10.0 ? "N2" : "N1");
            string lineBot = tr
                ? string.Format(c, "Saatlik ~{0}/sa  ·  Günlük ~{1}", hourlyStr, dailyStr)
                : string.Format(c, "Hourly ~{0}/hr  ·  Daily ~{1}", hourlyStr, dailyStr);

            RectangleF rightTopRect = new RectangleF(Sf(224f), Sf(317f), Sf(222f), Sf(20f));
            RectangleF rightBotRect = new RectangleF(Sf(224f), Sf(337f), Sf(222f), Sf(18f));
            using (SolidBrush tb = new SolidBrush(ColTextPrimary))
            using (SolidBrush sb = new SolidBrush(ColTextSecondary))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(lineTop, _fontCostDetail, tb, rightTopRect, sf);
                g.DrawString(lineBot, _fontCostSub, sb, rightBotRect, sf);
            }

            // 3. Usage Profile Segmented Selector
            RectangleF trackRect = new RectangleF(Sf(34f), Sf(364f), Sf(412f), Sf(26f));
            DrawRoundedCard(g, trackRect, Sf(7f), ColTrackBg, ColDivider);

            int wLight = _hardware.GetEstimatedWallWatts(UsageProfile.Light, _settings.IncludeMonitors);
            int wAvg = _hardware.GetEstimatedWallWatts(UsageProfile.Average, _settings.IncludeMonitors);
            int wHeavy = _hardware.GetEstimatedWallWatts(UsageProfile.Heavy, _settings.IncludeMonitors);

            bool autoMode = (_settings.CustomWatts == 0);
            DrawProfileSegment(g, _profLightRect, HitProfLight, string.Format(c, "{0} (~{1}W)", tr ? "Hafif" : "Light", wLight), autoMode && _settings.Profile == UsageProfile.Light);
            DrawProfileSegment(g, _profAvgRect, HitProfAvg, string.Format(c, "{0} (~{1}W)", tr ? "Ortalama" : "Average", wAvg), autoMode && _settings.Profile == UsageProfile.Average);
            DrawProfileSegment(g, _profHeavyRect, HitProfHeavy, string.Format(c, "{0} (~{1}W)", tr ? "Oyun/Yük" : "Gaming", wHeavy), autoMode && _settings.Profile == UsageProfile.Heavy);

            // 4. Divider
            using (Pen divPen = new Pen(ColDivider, 1f))
                g.DrawLine(divPen, Sf(34f), Sf(400f), Sf(446f), Sf(400f));

            // 5. Detected Hardware Specs
            string cpuVal = _hardware.IsDetected
                ? string.Format(c, "{0} ({1} {2})", _hardware.CpuName, _hardware.CpuCores, tr ? "Çekirdek" : "Cores")
                : (tr ? "Donanım bilgileri okunuyor..." : "Detecting hardware...");
            string cpuTdp = _hardware.IsDetected ? string.Format(c, "{0}W TDP", _hardware.CpuTdpWatts) : "";

            string gpuVal = _hardware.IsDetected ? _hardware.GpuName : "...";
            string gpuTdp = _hardware.IsDetected
                ? (_hardware.HasDiscreteGpu ? string.Format(c, "{0}W TDP", _hardware.GpuTdpWatts) : (tr ? "Dahili" : "Integrated"))
                : "";

            string sysVal = _hardware.IsDetected ? _hardware.BuildSystemSummary(tr, c) : "...";
            string sysRight;
            if (_settings.CustomWatts > 0)
                sysRight = string.Format(c, "{0}: {1}W", tr ? "Özel" : "Custom", _settings.CustomWatts);
            else if (_settings.IncludeMonitors)
                sysRight = tr ? "Monitör dahil" : "Monitors incl.";
            else
                sysRight = tr ? "Monitör hariç" : "Monitors excl.";

            DrawHardwareRow(g, Sf(408f), "CPU", cpuVal, cpuTdp, ColTextBody);
            DrawHardwareRow(g, Sf(431f), "GPU", gpuVal, gpuTdp, ColTextBody);
            DrawHardwareRow(g, Sf(454f), tr ? "SİSTEM" : "SYSTEM", sysVal, sysRight, ColTextSecondary);
        }

        private void DrawProfileSegment(Graphics g, RectangleF rect, int hitId, string text, bool active)
        {
            bool hovered = (_hoverHit == hitId);
            if (active)
                DrawRoundedCard(g, rect, Sf(5.5f), ColAmberBg, ColAmberBorder);
            else if (hovered)
                DrawRoundedCard(g, rect, Sf(5.5f), Color.FromArgb(26, 33, 43), ColDivider);

            Color fg = active ? ColAmber : (hovered ? ColTextPrimary : ColTextSecondary);
            using (SolidBrush b = new SolidBrush(fg))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(text, _fontSeg, b, rect, sf);
            }
        }

        private void DrawHardwareRow(Graphics g, float y, string tag, string mainText, string rightText, Color mainColor)
        {
            float rowH = Sf(19f);
            RectangleF tagRect = new RectangleF(Sf(34f), y, Sf(50f), rowH);
            RectangleF valRect = new RectangleF(Sf(88f), y, Sf(268f), rowH);
            RectangleF rightRect = new RectangleF(Sf(358f), y, Sf(88f), rowH);

            using (SolidBrush tb = new SolidBrush(ColTextMuted))
            using (SolidBrush vb = new SolidBrush(mainColor))
            using (SolidBrush rb = new SolidBrush(ColTextMuted))
            using (StringFormat sfNear = new StringFormat())
            using (StringFormat sfFar = new StringFormat())
            {
                sfNear.Alignment = StringAlignment.Near;
                sfNear.LineAlignment = StringAlignment.Center;
                sfNear.FormatFlags = StringFormatFlags.NoWrap;
                sfNear.Trimming = StringTrimming.EllipsisCharacter;

                sfFar.Alignment = StringAlignment.Far;
                sfFar.LineAlignment = StringAlignment.Center;
                sfFar.FormatFlags = StringFormatFlags.NoWrap;

                g.DrawString(tag, _fontHwTag, tb, tagRect, sfNear);
                g.DrawString(mainText, _fontHwVal, vb, valRect, sfNear);
                if (!string.IsNullOrEmpty(rightText))
                    g.DrawString(rightText, _fontHwTag, rb, rightRect, sfFar);
            }
        }

        private void DrawLightningIcon(Graphics g, RectangleF r, Color color)
        {
            PointF[] bolt = new PointF[]
            {
                new PointF(r.X + r.Width * 0.58f, r.Y),
                new PointF(r.X + r.Width * 0.12f, r.Y + r.Height * 0.54f),
                new PointF(r.X + r.Width * 0.48f, r.Y + r.Height * 0.54f),
                new PointF(r.X + r.Width * 0.36f, r.Y + r.Height),
                new PointF(r.X + r.Width * 0.88f, r.Y + r.Height * 0.42f),
                new PointF(r.X + r.Width * 0.52f, r.Y + r.Height * 0.42f)
            };
            using (SolidBrush b = new SolidBrush(color))
                g.FillPolygon(b, bolt);
        }

        private void DrawButtons(Graphics g)
        {
            bool tr = _settings.IsTurkish;

            DrawActionButton(
                g,
                _btnCopyRect,
                HitBtnCopy,
                _isCopiedState ? (tr ? "Kopyalandı" : "Copied") : (tr ? "Kopyala" : "Copy"),
                _isCopiedState,
                true);

            DrawActionButton(
                g,
                _btnPinRect,
                HitBtnPin,
                TopMost ? (tr ? "Üstte: Açık" : "Pinned: On") : (tr ? "Üstte Tut" : "Always On Top"),
                TopMost,
                false);

            DrawActionButton(
                g,
                _btnTrayRect,
                HitBtnTray,
                tr ? "Tepsiye Al" : "To Tray",
                false,
                false);
        }

        private void DrawActionButton(Graphics g, RectangleF rect, int hitId, string text, bool isHighlighted, bool isSuccessStyle)
        {
            bool hovered = _hoverHit == hitId;
            bool pressed = _pressedHit == hitId;

            Color bg;
            Color border;
            Color fg;

            if (isHighlighted)
            {
                if (isSuccessStyle)
                {
                    bg = ColCopiedBg;
                    border = ColCopiedBorder;
                    fg = ColAccent;
                }
                else
                {
                    bg = pressed ? ColBtnPress : (hovered ? ColBtnActiveHover : ColBtnActiveBg);
                    border = ColBtnActiveBorder;
                    fg = ColAccentSoft;
                }
            }
            else
            {
                bg = pressed ? ColBtnPress : (hovered ? ColBtnHover : ColBtnNormal);
                border = hovered ? Color.FromArgb(72, 85, 104) : ColBtnBorder;
                fg = ColTextPrimary;
            }

            DrawRoundedCard(g, rect, Sf(8f), bg, border);

            float iconSize = Sf(12f);
            float iconGap = Sf(6f);

            using (StringFormat typo = new StringFormat(StringFormat.GenericTypographic))
            {
                typo.FormatFlags |= StringFormatFlags.NoWrap;
                SizeF textSize = g.MeasureString(text, _fontButton, PointF.Empty, typo);
                float groupW = iconSize + iconGap + textSize.Width;
                float startX = rect.X + (rect.Width - groupW) * 0.5f;
                float centerY = rect.Y + rect.Height * 0.5f;

                RectangleF iconRect = new RectangleF(startX, centerY - iconSize * 0.5f, iconSize, iconSize);
                DrawButtonIcon(g, hitId, isHighlighted && isSuccessStyle, isHighlighted && !isSuccessStyle, iconRect, fg);

                float textY = centerY - textSize.Height * 0.5f;
                using (SolidBrush tb = new SolidBrush(fg))
                    g.DrawString(text, _fontButton, tb, startX + iconSize + iconGap, textY, typo);
            }
        }

        private void DrawButtonIcon(Graphics g, int hitId, bool isCheckmark, bool isPinned, RectangleF r, Color color)
        {
            float penW = Math.Max(1.3f, Sf(1.4f));
            using (Pen p = new Pen(color, penW))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;

                if (hitId == HitBtnCopy)
                {
                    if (isCheckmark)
                    {
                        PointF[] pts = new PointF[]
                        {
                            new PointF(r.X + r.Width * 0.14f, r.Y + r.Height * 0.54f),
                            new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.80f),
                            new PointF(r.X + r.Width * 0.88f, r.Y + r.Height * 0.24f)
                        };
                        g.DrawLines(p, pts);
                    }
                    else
                    {
                        float w = r.Width * 0.62f;
                        float h = r.Height * 0.68f;
                        g.DrawRectangle(p, r.X + r.Width * 0.32f, r.Y + r.Height * 0.08f, w, h);
                        g.DrawRectangle(p, r.X + r.Width * 0.08f, r.Y + r.Height * 0.28f, w, h);
                    }
                }
                else if (hitId == HitBtnPin)
                {
                    if (isPinned)
                    {
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            float d = r.Width * 0.55f;
                            g.FillEllipse(b, r.X + (r.Width - d) * 0.5f, r.Y + (r.Height - d) * 0.5f, d, d);
                        }
                        g.DrawEllipse(p, r.X + r.Width * 0.06f, r.Y + r.Height * 0.06f, r.Width * 0.88f, r.Height * 0.88f);
                    }
                    else
                    {
                        g.DrawEllipse(p, r.X + r.Width * 0.14f, r.Y + r.Height * 0.14f, r.Width * 0.72f, r.Height * 0.72f);
                    }
                }
                else if (hitId == HitBtnTray)
                {
                    float cx = r.X + r.Width * 0.5f;
                    g.DrawLine(p, cx, r.Y + r.Height * 0.12f, cx, r.Y + r.Height * 0.65f);
                    g.DrawLine(p, cx - r.Width * 0.24f, r.Y + r.Height * 0.44f, cx, r.Y + r.Height * 0.68f);
                    g.DrawLine(p, cx + r.Width * 0.24f, r.Y + r.Height * 0.44f, cx, r.Y + r.Height * 0.68f);
                    g.DrawLine(p, r.X + r.Width * 0.14f, r.Y + r.Height * 0.88f, r.X + r.Width * 0.86f, r.Y + r.Height * 0.88f);
                }
            }
        }

        private static float GetFontAscentPx(Font font)
        {
            int ascent = font.FontFamily.GetCellAscent(font.Style);
            int em = font.FontFamily.GetEmHeight(font.Style);
            if (em <= 0) return font.Size * 0.8f;
            return font.Size * ascent / em;
        }

        private static void DrawRoundedCard(Graphics g, RectangleF rect, float radius, Color fillColor, Color borderColor)
        {
            using (GraphicsPath path = CreateRoundedRect(rect, radius))
            using (SolidBrush fill = new SolidBrush(fillColor))
            using (Pen border = new Pen(borderColor, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
        }

        private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;

            path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_tickTimer != null)
            {
                _tickTimer.Stop();
                _tickTimer.Dispose();
            }

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            if (_trayMenu != null)
                _trayMenu.Dispose();

            if (_appIcon != null)
                _appIcon.Dispose();

            DisposeFonts();
            base.OnFormClosed(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            NativeMethods.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UptimeForm(LaunchOptions.Parse(args)));
        }
    }
}
