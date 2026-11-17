// What the PC settings need from the system (Windows here; Linux and macOS in the #else parts), enumerated the same way the game's F1 menu does it
// (pc_settings.cpp): the ini file, the monitors (primary first), their refresh rates and the
// DXGI graphics cards (the ini keeps the DXGI index; software adapters are not offered).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PortLauncher {

static class Ini {
#if NETFRAMEWORK
  [DllImport("kernel32", CharSet = CharSet.Unicode)]
  static extern int GetPrivateProfileIntW(string section, string key, int def, string file);
  [DllImport("kernel32", CharSet = CharSet.Unicode)]
  static extern bool WritePrivateProfileStringW(string section, string key, string value, string file);

  public static int Get(string file, string section, string key, int def) => GetPrivateProfileIntW(section, key, def, file);
  public static void Put(string file, string section, string key, int value) =>
      WritePrivateProfileStringW(section, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), file);
  public static string GetText(string file, string section, string key) {
    var b = new StringBuilder(64);
    GetPrivateProfileStringW(section, key, "", b, b.Capacity, file);
    return b.ToString();
  }
  public static void PutText(string file, string section, string key, string value) => WritePrivateProfileStringW(section, key, value, file);
  [DllImport("kernel32", CharSet = CharSet.Unicode)]
  static extern int GetPrivateProfileStringW(string section, string key, string def, StringBuilder value, int size, string file);
#else
  // Linux / macOS (.NET): the same calls on a plain UTF-8 ini file.
  public static int Get(string file, string section, string key, int def) {
    string v = IniFile.Read(file, section, key);
    if (v == null) return def;
    int i = 0, sign = 1;
    v = v.Trim();
    if (v.StartsWith("-")) { sign = -1; i = 1; }
    long n = 0; int start = i;
    while (i < v.Length && char.IsDigit(v[i]) && n < int.MaxValue) n = n * 10 + (v[i++] - '0');
    return i == start ? 0 : (int)(sign * Math.Min(n, int.MaxValue));
  }
  public static void Put(string file, string section, string key, int value) =>
      IniFile.Write(file, section, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
  public static string GetText(string file, string section, string key) => IniFile.Read(file, section, key) ?? "";
  public static void PutText(string file, string section, string key, string value) => IniFile.Write(file, section, key, value);
#endif
}

// A Windows-style ini file read and written by the launcher itself (Linux / macOS; Windows uses its own API).
static class IniFile {
  static string Strip(string v) {
    v = v.Trim();
    if (v.Length >= 2 && (v[0] == '"' && v[v.Length - 1] == '"' || v[0] == '\'' && v[v.Length - 1] == '\'')) v = v.Substring(1, v.Length - 2);
    return v;
  }

  public static string Read(string file, string section, string key) {
    if (!System.IO.File.Exists(file)) return null;
    bool inSection = false;
    foreach (string raw in System.IO.File.ReadAllLines(file)) {
      string line = raw.Trim();
      if (line.StartsWith("[") && line.EndsWith("]")) { inSection = string.Equals(line.Substring(1, line.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase); continue; }
      if (!inSection || line.StartsWith(";") || line.StartsWith("#")) continue;
      int eq = line.IndexOf('=');
      if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) return Strip(line.Substring(eq + 1));
    }
    return null;
  }

  // Replaces the key's line (or adds it at the end of its section, or a new section at the end); null removes it.
  public static void Write(string file, string section, string key, string value) {
    var lines = System.IO.File.Exists(file) ? System.IO.File.ReadAllLines(file).ToList() : new List<string>();
    int sectionAt = -1, sectionEnd = lines.Count, keyAt = -1;
    for (int i = 0; i < lines.Count; ++i) {
      string line = lines[i].Trim();
      if (line.StartsWith("[") && line.EndsWith("]")) {
        if (sectionAt >= 0 && sectionEnd == lines.Count) sectionEnd = i;
        if (sectionAt < 0 && string.Equals(line.Substring(1, line.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase)) sectionAt = i;
        continue;
      }
      if (sectionAt >= 0 && sectionEnd == lines.Count && keyAt < 0) {
        int eq = line.IndexOf('=');
        if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) keyAt = i;
      }
    }
    if (keyAt >= 0) { if (value == null) lines.RemoveAt(keyAt); else lines[keyAt] = key + "=" + value; }
    else if (value != null && sectionAt >= 0) {
      int at = sectionEnd;
      while (at > sectionAt + 1 && lines[at - 1].Trim().Length == 0) --at;
      lines.Insert(at, key + "=" + value);
    } else if (value != null) {
      if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
      lines.Add("[" + section + "]");
      lines.Add(key + "=" + value);
    }
    System.IO.File.WriteAllText(file, string.Join("\n", lines) + "\n");
  }
}

// key=value lines without sections (the PS2 runtime's pc-settings.ini). Lines the launcher does not know are kept.
static class PlainIni {
  public static Dictionary<string, string> Read(string file) {
    var d = new Dictionary<string, string>();
    if (!System.IO.File.Exists(file)) return d;
    foreach (var line in System.IO.File.ReadAllLines(file)) {
      int eq = line.IndexOf('=');
      if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
    }
    return d;
  }
  public static void Write(string file, Dictionary<string, string> values) {
    var lines = new List<string>();
    var done = new HashSet<string>();
    if (System.IO.File.Exists(file))
      foreach (var line in System.IO.File.ReadAllLines(file)) {
        int eq = line.IndexOf('=');
        string key = eq > 0 ? line.Substring(0, eq).Trim() : null;
        if (key != null && values.ContainsKey(key)) { if (done.Add(key)) lines.Add(key + "=" + values[key]); }
        else lines.Add(line);
      }
    foreach (var kv in values) if (!done.Contains(kv.Key)) lines.Add(kv.Key + "=" + kv.Value);
    System.IO.File.WriteAllText(file, string.Join("\n", lines) + "\n");
  }
}

// Windows' per-program graphics card preference (Settings > Display > Graphics), the one the PS2 menu sets:
// 0 = let Windows decide (no value), 1 = power saving, 2 = high performance.
static class GpuPreference {
#if NETFRAMEWORK
  const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
  public static int Get(string exe) {
    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key)) {
      var v = k?.GetValue(exe) as string;
      int at = v == null ? -1 : v.IndexOf("GpuPreference=", StringComparison.Ordinal);
      if (at < 0) return 0;
      int.TryParse(new string(v.Substring(at + 14).TakeWhile(char.IsDigit).ToArray()), out int n);
      return Math.Max(0, Math.Min(2, n));
    }
  }
  public static void Set(string exe, int value) {
    using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key)) {
      if (value <= 0) k.DeleteValue(exe, false);
      else k.SetValue(exe, "GpuPreference=" + Math.Min(value, 2) + ";");
    }
  }
#else
  public static int Get(string exe) => 0;            // Linux / macOS: the system picks the card (no per-program setting here)
  public static void Set(string exe, int value) { }
#endif
}

sealed class MonitorInfo {
  public string Device, Name;
}

static class Displays {
#if NETFRAMEWORK
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct MONITORINFOEX {
    public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
  }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct DISPLAY_DEVICE {
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
    public uint StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
  }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
    public uint dmFields;
    public int dmPositionX, dmPositionY;
    public uint dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public ushort dmLogPixels;
    public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
  [DllImport("user32")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
  [DllImport("user32", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);
  [DllImport("user32", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevicesW(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
  [DllImport("user32", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);
  const int ENUM_CURRENT_SETTINGS = -1;

  public static List<MonitorInfo> Monitors() {
    var list = new List<MonitorInfo>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, dc, r, d) => {
      var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
      if (!GetMonitorInfoW(h, ref mi)) return true;
      int w = mi.rcMonitor.Right - mi.rcMonitor.Left, ht = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
      var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
      string name = EnumDisplayDevicesW(mi.szDevice, 0, ref dd, 0) && !string.IsNullOrEmpty(dd.DeviceString)
          ? $"{dd.DeviceString} ({w}x{ht})" : $"{w}x{ht}";
      var m = new MonitorInfo { Device = mi.szDevice, Name = name };
      if ((mi.dwFlags & 1) != 0) list.Insert(0, m); else list.Add(m);  // primary first, as in the game
      return true;
    }, IntPtr.Zero);
    return list;
  }

  // Refresh rates the monitor offers at its current resolution, highest first.
  public static List<int> RefreshRates(string device, out int current) {
    var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)) };
    current = EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 1 ? (int)dm.dmDisplayFrequency : 60;
    uint w = dm.dmPelsWidth, h = dm.dmPelsHeight;
    var rates = new SortedSet<int>();
    for (int i = 0; ; ++i) {
      var m = new DEVMODE { dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)) };
      if (!EnumDisplaySettingsW(device, i, ref m)) break;
      if (m.dmPelsWidth == w && m.dmPelsHeight == h && m.dmDisplayFrequency > 1) rates.Add((int)m.dmDisplayFrequency);
    }
    return rates.Reverse().ToList();
  }
#else
  // Linux / macOS: the window toolkit lists the screens (the Linux/macOS launcher sets Provider); refresh rates come from
  // xrandr on X11/XWayland (Linux) and are not listed on macOS (the game uses the screen's own rate).
  public static Func<List<MonitorInfo>> Provider;
  public static List<MonitorInfo> Monitors() { try { return Provider?.Invoke() ?? new List<MonitorInfo>(); } catch (Exception) { return new List<MonitorInfo>(); } }

  public static List<int> RefreshRates(string device, out int current) {
    current = 60;
    if (!OS.Linux) return new List<int>();
    var rates = new SortedSet<int>();
    try {
      var psi = new System.Diagnostics.ProcessStartInfo("xrandr", "--query") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
      string text;
      using (var p = System.Diagnostics.Process.Start(psi)) { text = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); }
      foreach (int r in ParseXrandr(text, device, out int cur)) { rates.Add(r); if (cur > 0) current = cur; }
    } catch (Exception) { }
    return rates.Reverse().ToList();
  }

  // The rates of the output's current resolution in `xrandr --query` ("   2560x1080  74.99*+  60.00"); the first
  // connected output when device is null or not listed. current: the one marked '*'.
  public static List<int> ParseXrandr(string text, string device, out int current) {
    current = 0;
    var outputs = new List<KeyValuePair<string, List<string>>>();
    foreach (string line in (text ?? "").Replace("\r", "").Split('\n')) {
      if (line.Length == 0) continue;
      if (!char.IsWhiteSpace(line[0])) {
        var parts = line.Split(' ');
        outputs.Add(parts.Length > 1 && parts[1] == "connected"
            ? new KeyValuePair<string, List<string>>(parts[0], new List<string>()) : new KeyValuePair<string, List<string>>(null, null));
        continue;
      }
      if (outputs.Count > 0 && outputs[outputs.Count - 1].Value != null) outputs[outputs.Count - 1].Value.Add(line);
    }
    var pick = outputs.FirstOrDefault(o => o.Key != null && o.Key == device);
    if (pick.Key == null) pick = outputs.FirstOrDefault(o => o.Key != null);
    var list = new List<int>();
    if (pick.Value == null) return list;
    foreach (string mode in pick.Value) {
      if (mode.IndexOf('*') < 0) continue;
      foreach (string tok in mode.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Skip(1)) {
        string num = new string(tok.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hz) || hz < 1) continue;
        int r = (int)Math.Round(hz);
        if (!list.Contains(r)) list.Add(r);
        if (tok.IndexOf('*') >= 0) current = r;
      }
      break;
    }
    return list;
  }
#endif
}

static class Gpus {
#if NETFRAMEWORK
  [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IDXGIFactory1 {
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
    [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    [PreserveSig] bool IsCurrent();
  }
  [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IDXGIAdapter1 {
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
    [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
  }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct DXGI_ADAPTER_DESC1 {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    public uint VendorId, DeviceId, SubSysId, Revision;
    public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public uint LuidLow; public int LuidHigh;
    public uint Flags;
  }
  [DllImport("dxgi")] static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

  // (DXGI index, name) of every hardware adapter.
  public static List<KeyValuePair<int, string>> List() {
    var list = new List<KeyValuePair<int, string>>();
    try {
      var iid = typeof(IDXGIFactory1).GUID;
      if (CreateDXGIFactory1(ref iid, out object o) < 0) return list;
      var f = (IDXGIFactory1)o;
      for (uint i = 0; f.EnumAdapters1(i, out var a) >= 0; ++i) {
        if (a.GetDesc1(out var d) >= 0 && (d.Flags & 2) == 0) list.Add(new KeyValuePair<int, string>((int)i, d.Description));
        Marshal.ReleaseComObject(a);
      }
      Marshal.ReleaseComObject(f);
    } catch (Exception) { }
    return list;
  }
#else
  public static List<KeyValuePair<int, string>> List() => new List<KeyValuePair<int, string>>();   // DXGI indexes only mean something on Windows
#endif
}

}
