using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gestosh
{
    internal sealed class Config
    {
        public bool Enabled = true;
        public bool ShowTrayIcon = true;

        public bool ThreeFingerDrag = true;
        public double DragSensitivity = 1.0;
        public double DragStartThreshold = 0.012;

        public int PinchFingers = 5;
        public double PinchOutRatio = 1.35;
        public double PinchInRatio = 0.74;
        public double PinchMinTravel = 0.045;
        public int PinchSettleMs = 60;
        public int GestureCooldownMs = 350;

        public int AnimationDurationMs = 340;
        public int AnimationFps = 60;

        public int ExposePx = 42;
        public int StackStepPx = 10;
        public int MaxExposePx = 220;
        public bool IncludeToolWindows = false;
        public bool IncludeMinimized = false;
        public List<string> OnlyProcesses = new List<string>();
        public List<string> ExcludeProcesses = new List<string>();

        public bool ManageThreeFingerGesture = true;
        public bool ManageFourFingerGesture = true;
        public bool Hotkeys = true;

        public double ExposeSwipeDistance = 0.16;
        public double ExposeSwipeVerticalRatio = 1.5;
        public int ExposeSwipeMaxMs = 900;

        public bool DesktopSwipe = true;
        public double DesktopSwipeGain = 1.0;
        public double DesktopSwipeStartDistance = 0.012;
        public double DesktopSwipeCommitRatio = 0.30;
        public int DesktopSwipeSettleMs = 170;

        // What each gesture does. The dialog edits these; the older booleans above are
        // kept in sync so existing configurations and call sites keep working.
        // values: none | drag | expose | taskview | desktop | actioncenter
        public string ThreeFingerAction = "drag";
        public string FourFingerDownAction = "expose";
        public string FourFingerUpAction = "taskview";
        public string FourFingerHorizontalAction = "desktop";
        public string EdgeSwipeAction = "actioncenter";

        public string SourcePath = "";

        public void CopyFrom(Config other)
        {
            Enabled = other.Enabled;
            ShowTrayIcon = other.ShowTrayIcon;
            ThreeFingerDrag = other.ThreeFingerDrag;
            DragSensitivity = other.DragSensitivity;
            DragStartThreshold = other.DragStartThreshold;
            PinchFingers = other.PinchFingers;
            PinchOutRatio = other.PinchOutRatio;
            PinchInRatio = other.PinchInRatio;
            PinchMinTravel = other.PinchMinTravel;
            PinchSettleMs = other.PinchSettleMs;
            GestureCooldownMs = other.GestureCooldownMs;
            AnimationDurationMs = other.AnimationDurationMs;
            AnimationFps = other.AnimationFps;
            ExposePx = other.ExposePx;
            StackStepPx = other.StackStepPx;
            MaxExposePx = other.MaxExposePx;
            IncludeToolWindows = other.IncludeToolWindows;
            IncludeMinimized = other.IncludeMinimized;
            OnlyProcesses = other.OnlyProcesses;
            ExcludeProcesses = other.ExcludeProcesses;
            ManageThreeFingerGesture = other.ManageThreeFingerGesture;
            ManageFourFingerGesture = other.ManageFourFingerGesture;
            Hotkeys = other.Hotkeys;
            ExposeSwipeDistance = other.ExposeSwipeDistance;
            ExposeSwipeVerticalRatio = other.ExposeSwipeVerticalRatio;
            ExposeSwipeMaxMs = other.ExposeSwipeMaxMs;
            DesktopSwipe = other.DesktopSwipe;
            DesktopSwipeGain = other.DesktopSwipeGain;
            DesktopSwipeStartDistance = other.DesktopSwipeStartDistance;
            DesktopSwipeCommitRatio = other.DesktopSwipeCommitRatio;
            DesktopSwipeSettleMs = other.DesktopSwipeSettleMs;
            ThreeFingerAction = other.ThreeFingerAction;
            FourFingerDownAction = other.FourFingerDownAction;
            FourFingerUpAction = other.FourFingerUpAction;
            FourFingerHorizontalAction = other.FourFingerHorizontalAction;
            EdgeSwipeAction = other.EdgeSwipeAction;
            SourcePath = other.SourcePath;
        }

        public static Config Load(string path)
        {
            var cfg = new Config();
            cfg.SourcePath = path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return cfg;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch { return cfg; }
            string section = "";
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                values[section + "." + key] = value;
            }

            cfg.Enabled = GetBool(values, "general.enabled", cfg.Enabled);
            cfg.ShowTrayIcon = GetBool(values, "general.tray", cfg.ShowTrayIcon);
            cfg.ManageThreeFingerGesture = GetBool(values, "general.manageThreeFingerGesture", cfg.ManageThreeFingerGesture);
            cfg.ManageFourFingerGesture = GetBool(values, "general.manageFourFingerGesture", cfg.ManageFourFingerGesture);
            cfg.Hotkeys = GetBool(values, "general.hotkeys", cfg.Hotkeys);

            cfg.ThreeFingerDrag = GetBool(values, "gestures.threeFingerDrag", cfg.ThreeFingerDrag);
            cfg.DragSensitivity = GetDouble(values, "gestures.dragSensitivity", cfg.DragSensitivity, 0.2, 5.0);
            cfg.DragStartThreshold = GetDouble(values, "gestures.dragStartThreshold", cfg.DragStartThreshold, 0.002, 0.10);
            cfg.PinchFingers = GetInt(values, "gestures.pinchFingers", cfg.PinchFingers, 4, 5);
            cfg.PinchOutRatio = GetDouble(values, "gestures.pinchOutRatio", cfg.PinchOutRatio, 1.05, 5.0);
            cfg.PinchInRatio = GetDouble(values, "gestures.pinchInRatio", cfg.PinchInRatio, 0.2, 0.95);
            cfg.PinchMinTravel = GetDouble(values, "gestures.pinchMinTravel", cfg.PinchMinTravel, 0.005, 0.5);
            cfg.PinchSettleMs = GetInt(values, "gestures.pinchSettleMs", cfg.PinchSettleMs, 0, 500);
            cfg.GestureCooldownMs = GetInt(values, "gestures.gestureCooldownMs", cfg.GestureCooldownMs, 0, 3000);
            cfg.ExposeSwipeDistance = GetDouble(values, "gestures.exposeSwipeDistance", cfg.ExposeSwipeDistance, 0.05, 0.4);
            cfg.ExposeSwipeVerticalRatio = GetDouble(values, "gestures.exposeSwipeVerticalRatio", cfg.ExposeSwipeVerticalRatio, 1.0, 4.0);
            cfg.ExposeSwipeMaxMs = GetInt(values, "gestures.exposeSwipeMaxMs", cfg.ExposeSwipeMaxMs, 200, 2000);
            cfg.DesktopSwipe = GetBool(values, "gestures.desktopSwipe", cfg.DesktopSwipe);
            cfg.DesktopSwipeGain = GetDouble(values, "gestures.desktopSwipeGain", cfg.DesktopSwipeGain, 0.4, 3.0);
            cfg.DesktopSwipeStartDistance = GetDouble(values, "gestures.desktopSwipeStartDistance", cfg.DesktopSwipeStartDistance, 0.004, 0.06);
            cfg.DesktopSwipeCommitRatio = GetDouble(values, "gestures.desktopSwipeCommitRatio", cfg.DesktopSwipeCommitRatio, 0.1, 0.9);
            cfg.ThreeFingerAction = GetChoice(values, "gestures.threeFingerAction",
                cfg.ThreeFingerDrag ? "drag" : "none", "none", "drag");
            cfg.FourFingerDownAction = GetChoice(values, "gestures.fourFingerDownAction",
                "expose", "none", "expose", "taskview");
            cfg.FourFingerUpAction = GetChoice(values, "gestures.fourFingerUpAction",
                "taskview", "none", "expose", "taskview");
            cfg.FourFingerHorizontalAction = GetChoice(values, "gestures.fourFingerHorizontalAction",
                cfg.DesktopSwipe ? "desktop" : "none", "none", "desktop");
            cfg.EdgeSwipeAction = GetChoice(values, "gestures.edgeSwipeAction",
                "actioncenter", "none", "actioncenter");
            cfg.ThreeFingerDrag = cfg.ThreeFingerAction == "drag";
            cfg.DesktopSwipe = cfg.FourFingerHorizontalAction == "desktop";

            cfg.AnimationDurationMs = GetInt(values, "animation.durationMs", cfg.AnimationDurationMs, 0, 2000);
            cfg.AnimationFps = GetInt(values, "animation.fps", cfg.AnimationFps, 20, 144);
            cfg.DesktopSwipeSettleMs = GetInt(values, "animation.desktopSwipeSettleMs", cfg.DesktopSwipeSettleMs, 60, 600);

            cfg.ExposePx = GetInt(values, "dock.exposePx", cfg.ExposePx, 4, 300);
            cfg.StackStepPx = GetInt(values, "dock.stackStepPx", cfg.StackStepPx, 0, 80);
            cfg.MaxExposePx = GetInt(values, "dock.maxExposePx", cfg.MaxExposePx, 8, 800);
            cfg.IncludeToolWindows = GetBool(values, "dock.includeToolWindows", cfg.IncludeToolWindows);
            cfg.IncludeMinimized = GetBool(values, "dock.includeMinimized", cfg.IncludeMinimized);
            cfg.OnlyProcesses = GetList(values, "dock.onlyProcesses");
            cfg.ExcludeProcesses = GetList(values, "dock.excludeProcesses");

            if (cfg.MaxExposePx < cfg.ExposePx) cfg.MaxExposePx = cfg.ExposePx;
            if (cfg.PinchInRatio >= cfg.PinchOutRatio) cfg.PinchInRatio = Math.Max(0.2, cfg.PinchOutRatio - 0.2);
            return cfg;
        }

        public static void WriteTemplate(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Template, new UTF8Encoding(false));
        }

        /// <summary>
        /// Writes the five gesture mappings into an existing configuration file without
        /// dropping comments or unrelated keys. Missing keys are appended to [gestures].
        /// </summary>
        public static void SaveGestureActions(string path, string three, string down, string up,
            string horizontal, string edge)
        {
            var updates = new List<KeyValuePair<string, string>>();
            updates.Add(new KeyValuePair<string, string>("threeFingerAction", three));
            updates.Add(new KeyValuePair<string, string>("fourFingerDownAction", down));
            updates.Add(new KeyValuePair<string, string>("fourFingerUpAction", up));
            updates.Add(new KeyValuePair<string, string>("fourFingerHorizontalAction", horizontal));
            updates.Add(new KeyValuePair<string, string>("edgeSwipeAction", edge));

            var lines = new List<string>();
            if (File.Exists(path))
            {
                try { lines.AddRange(File.ReadAllLines(path, Encoding.UTF8)); }
                catch (Exception ex) { Log.Warn("Cannot read " + path + ": " + ex.Message); }
            }
            if (lines.Count == 0)
                lines.AddRange(Template.Split(new[] { "\r\n" }, StringSplitOptions.None));

            int sectionStart = -1;
            int sectionEnd = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string text = lines[i].Trim();
                if (text.Length < 3 || text[0] != '[' || text[text.Length - 1] != ']') continue;
                if (sectionStart >= 0)
                {
                    sectionEnd = i;
                    break;
                }
                if (string.Equals(text.Substring(1, text.Length - 2).Trim(), "gestures", StringComparison.OrdinalIgnoreCase))
                    sectionStart = i;
            }
            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length > 0) lines.Add("");
                lines.Add("[gestures]");
                sectionStart = lines.Count - 1;
                sectionEnd = lines.Count;
            }

            foreach (var pair in updates)
            {
                bool replaced = false;
                for (int i = sectionStart + 1; i < sectionEnd && i < lines.Count; i++)
                {
                    string text = lines[i].Trim();
                    if (text.Length == 0 || text[0] == ';' || text[0] == '#') continue;
                    int equals = text.IndexOf('=');
                    if (equals <= 0) continue;
                    if (!string.Equals(text.Substring(0, equals).Trim(), pair.Key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    lines[i] = pair.Key + "=" + pair.Value;
                    replaced = true;
                    break;
                }
                if (replaced) continue;
                lines.Insert(sectionEnd, pair.Key + "=" + pair.Value);
                sectionEnd++;
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }

        public const string Template = @"; Gestosh configuration
; All pixel values are at 96 DPI and are scaled per monitor.
; Restart or choose ""Reload configuration"" after editing.

[general]
enabled=true
tray=true
manageThreeFingerGesture=true
manageFourFingerGesture=true
hotkeys=true

[gestures]
threeFingerDrag=true
dragSensitivity=1.0
dragStartThreshold=0.012
pinchFingers=5
pinchOutRatio=1.35
pinchInRatio=0.74
pinchMinTravel=0.045
pinchSettleMs=60
gestureCooldownMs=350
exposeSwipeDistance=0.16
exposeSwipeVerticalRatio=1.5
exposeSwipeMaxMs=900
desktopSwipe=true
desktopSwipeGain=1.0
desktopSwipeStartDistance=0.012
desktopSwipeCommitRatio=0.30
; Gesture mappings, also edited by the tray menu entry ""手势设置..."".
; threeFingerAction:          none | drag
; fourFingerDownAction:       none | expose | taskview
; fourFingerUpAction:         none | expose | taskview
; fourFingerHorizontalAction: none | desktop
; edgeSwipeAction:            none | actioncenter
threeFingerAction=drag
fourFingerDownAction=expose
fourFingerUpAction=taskview
fourFingerHorizontalAction=desktop
edgeSwipeAction=actioncenter

[animation]
durationMs=340
fps=60
desktopSwipeSettleMs=170

[dock]
exposePx=42
stackStepPx=10
maxExposePx=220
includeToolWindows=false
includeMinimized=false
; Optional comma-separated process names (without .exe). Empty means all windows.
onlyProcesses=
excludeProcesses=
";

        static string GetChoice(Dictionary<string, string> values, string key, string fallback, params string[] allowed)
        {
            string text;
            if (!values.TryGetValue(key, out text)) return fallback;
            text = (text ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < allowed.Length; i++)
            {
                if (allowed[i] == text) return text;
            }
            return fallback;
        }

        static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
        {
            string text;
            if (!values.TryGetValue(key, out text)) return fallback;
            bool result;
            if (bool.TryParse(text, out result)) return result;
            if (text == "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase) || text.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (text == "0" || text.Equals("no", StringComparison.OrdinalIgnoreCase) || text.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        static int GetInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
        {
            string text;
            int value;
            if (!values.TryGetValue(key, out text) || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        static double GetDouble(Dictionary<string, string> values, string key, double fallback, double min, double max)
        {
            string text;
            double value;
            if (!values.TryGetValue(key, out text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        static List<string> GetList(Dictionary<string, string> values, string key)
        {
            var list = new List<string>();
            string text;
            if (!values.TryGetValue(key, out text) || string.IsNullOrWhiteSpace(text)) return list;
            foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = part.Trim();
                if (name.Length == 0) continue;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                list.Add(name);
            }
            return list;
        }
    }
}
