// What differs between Windows, Linux and macOS: where the launcher keeps its files, how it opens folders and
// web pages, starts the setup and the games (PowerShell/cmd on Windows, bash on Linux/macOS), stops them, makes
// shortcuts and picks the per-system part of a recompiler's configuration ("linux": {...}, "macos": {...}).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PortLauncher {

static class OS {
  public static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
  public static readonly bool Mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
  public static readonly bool Linux = !Windows && !Mac;

  // The key of this system in launcher.json / setup.json / recomps.json.
  public static string Key => Windows ? "windows" : Mac ? "macos" : "linux";
  public static string Name => Windows ? "Windows" : Mac ? "macOS" : "Linux";

  static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
  static string Xdg(string variable, string fallback) {
    string v = Environment.GetEnvironmentVariable(variable);
    return !string.IsNullOrEmpty(v) && Path.IsPathRooted(v) ? v : Path.Combine(Home, fallback);
  }

  // Preferences: %APPDATA%, ~/.config (XDG_CONFIG_HOME), ~/Library/Application Support.
  public static string ConfigRoot => Windows ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                                   : Mac ? Path.Combine(Home, "Library", "Application Support") : Xdg("XDG_CONFIG_HOME", ".config");
  // Caches: %LOCALAPPDATA%, ~/.cache (XDG_CACHE_HOME), ~/Library/Caches.
  public static string CacheRoot => Windows ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                                  : Mac ? Path.Combine(Home, "Library", "Caches") : Xdg("XDG_CACHE_HOME", ".cache");
  public static string DesktopDir {
    get {
      if (Windows || Mac) return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) is var d && d.Length > 0 ? d : Path.Combine(Home, "Desktop");
      // XDG user dirs: XDG_DESKTOP_DIR="$HOME/Desktop" in ~/.config/user-dirs.dirs (localised names, e.g. Área de Trabalho).
      try {
        string dirs = Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "user-dirs.dirs");
        if (File.Exists(dirs))
          foreach (string line in File.ReadAllLines(dirs)) {
            if (!line.StartsWith("XDG_DESKTOP_DIR=")) continue;
            string v = line.Substring(16).Trim().Trim('"').Replace("$HOME", Home);
            if (v.Length > 0) return v;
          }
      } catch (Exception) { }
      return Path.Combine(Home, "Desktop");
    }
  }

  // The launcher program: the exe, the ELF (or the AppImage it runs from) or the binary inside the .app bundle.
  public static string ExePath {
    get {
      if (Linux && Environment.GetEnvironmentVariable("APPIMAGE") is string img && img.Length > 0) return img;
#if NETFRAMEWORK
      return Process.GetCurrentProcess().MainModule.FileName;
#else
      return Environment.ProcessPath;
#endif
    }
  }

  // The .app bundle the launcher runs from (macOS), else null.
  public static string AppBundle {
    get {
      if (!Mac) return null;
      for (var d = new DirectoryInfo(Path.GetDirectoryName(ExePath)); d != null; d = d.Parent)
        if (d.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return d.FullName;
      return null;
    }
  }

  // The file manager's name in menus ("Show the disc in Explorer / Finder / the file manager").
  public static string FileManager(bool pt) => Windows ? "Explorer" : Mac ? "Finder" : pt ? "gerenciador de arquivos" : "file manager";

  public static void OpenFolder(string dir) {
    if (dir == null || !Directory.Exists(dir)) return;
    if (Windows) Start("explorer.exe", Quote(dir));
    else Start(Mac ? "open" : "xdg-open", Quote(dir));
  }

  // Shows a file selected in the file manager (Linux: through the FileManager1 D-Bus interface, else its folder).
  public static void Reveal(string file) {
    if (file == null) return;
    if (Windows) { Start("explorer.exe", "/select,\"" + file + "\""); return; }
    if (Mac) { Start("open", "-R " + Quote(file)); return; }
    string uri = new Uri(Path.GetFullPath(file)).AbsoluteUri;
    if (!Start("dbus-send", "--session --print-reply --dest=org.freedesktop.FileManager1 /org/freedesktop/FileManager1 " +
                            "org.freedesktop.FileManager1.ShowItems array:string:" + Quote(uri) + " string:\"\"", true))
      OpenFolder(Directory.Exists(file) ? file : Path.GetDirectoryName(file));
  }

  public static void OpenUrl(string url) {
    if (Windows) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    else Start(Mac ? "open" : "xdg-open", Quote(url));
  }

  static bool Start(string exe, string args, bool wait = false) {
    try {
      using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })) {
        if (wait && p != null) { if (!p.WaitForExit(5000)) return true; return p.ExitCode == 0; }
        return p != null;
      }
    } catch (Exception) { return false; }
  }

  // A double-quoted argument (Windows rules for the exe; the .NET argument parser uses the same on every system).
  public static string Quote(string s) => "\"" + (s ?? "").Replace("\\\"", "\\\\\"").Replace("\"", "\\\"") + "\"";

  // Ends a process and everything it started (the setup's Python and compilers, the game's runner).
  public static void KillTree(int pid) {
    if (Windows) {
      try { Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {pid}") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(); }
      catch (Exception) { }
      return;
    }
#if NETFRAMEWORK
    try { Process.GetProcessById(pid).Kill(); } catch (Exception) { }
#else
    try { Process.GetProcessById(pid).Kill(true); } catch (Exception) { }
#endif
  }

  // ------------------------------------------------------------------ scripts

  // A script of the port for this system: Windows runs .ps1/.cmd, Linux and macOS the .sh next to them
  // (Play.cmd -> Play.sh, setup.ps1 -> setup.sh). Other names stay.
  public static string ScriptName(string name) {
    if (Windows || string.IsNullOrEmpty(name)) return name;
    string ext = Path.GetExtension(name);
    if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
      return name.Substring(0, name.Length - ext.Length) + ".sh";
    return name;
  }

  // How to start a script with arguments, without a console window; output and errors are read by the launcher.
  public static ProcessStartInfo ScriptStart(string script, string args, string workDir) {
    ProcessStartInfo psi;
    string ext = Path.GetExtension(script).ToLowerInvariant();
    if (Windows && ext == ".ps1")
      psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                                 $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {args}");
    else if (Windows)
      psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c \"\"{script}\" {args} < nul\"");
    else if (ext == ".sh" || ext == "")
      psi = new ProcessStartInfo("/bin/bash", Quote(script) + (args.Length > 0 ? " " + args : ""));
    else
      psi = new ProcessStartInfo(script, args);
    psi.WorkingDirectory = workDir;
    psi.UseShellExecute = false;
    psi.CreateNoWindow = true;
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    if (!Windows) psi.RedirectStandardInput = true;   // like "< nul": a question in a script never waits for a keyboard
    return psi;
  }

  public static bool IsSetupProcess(ProcessStartInfo psi) =>
      psi != null && (psi.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
                      (!Windows && psi.Arguments.IndexOf("setup.sh", StringComparison.OrdinalIgnoreCase) >= 0));

  // ------------------------------------------------------------------ per-system configuration

  // launcher.json, setup.json and installer.json may hold a block per system ("windows", "linux", "macos") whose
  // keys replace the top-level ones there; "linux": false says the port has no Linux version.
  public static IDictionary<string, object> ForThisSystem(IDictionary<string, object> d) {
    if (d == null) return null;
    if (!d.TryGetValue(Key, out var block) || !(block is IDictionary<string, object> over)) return d;
    var merged = new Dictionary<string, object>(d);
    foreach (var kv in over) merged[kv.Key] = kv.Value;
    return merged;
  }

  // Whether a recompiler (its launcher.json) or a game (its setup.json) runs on this system. Windows: unless it says
  // "windows": false. Linux/macOS: only when it has its "linux"/"macos" block (or "<system>": true), since every port
  // started on Windows. A game (top = false) only needs not to refuse it: its recompiler's block covers it.
  public static bool Supports(IDictionary<string, object> d, bool top = true) {
    if (d == null) return Windows;
    if (d.TryGetValue(Key, out var v)) return !(v is bool b) || b;
    if (!Windows && !top && WindowsOnlySteps(d)) return false;
    return Windows || !top;
  }

  // A setup.json whose build steps run Windows scripts ("cmd", "/c", "build.cmd"; powershell; a .cmd/.bat/.ps1 file):
  // the game has not been ported to Linux / macOS yet unless its block for that system replaces the steps.
  public static bool WindowsOnlySteps(IDictionary<string, object> setup) {
    if (setup == null || !setup.TryGetValue("steps", out var st) || !(st is object[] steps)) return false;
    foreach (var item in steps.OfType<IDictionary<string, object>>()) {
      if (!item.TryGetValue("run", out var r) || !(r is object[] run) || run.Length == 0) continue;
      string first = Convert.ToString(run[0]).ToLowerInvariant();
      if (first == "cmd" || first == "cmd.exe" || first == "powershell" || first == "powershell.exe" || first == "pwsh" ||
          first.EndsWith(".cmd") || first.EndsWith(".bat") || first.EndsWith(".ps1")) return true;
    }
    return false;
  }
}

// Desktop shortcuts that open the launcher on one game (Linux and macOS): a .desktop entry on Linux (in the
// applications menu and on the desktop), a small AppleScript app on the macOS desktop.
static class Shortcuts {
  public static string Create(string name, string launcher, string args, string workDir) {
    string safe = new string((name ?? "Game").Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c).ToArray());
    // Windows keeps its .lnk code in the Windows launcher (MainForm.CreateShortcut).
    if (OS.Windows) throw new PlatformNotSupportedException();
    if (OS.Mac) {
      string app = OS.AppBundle;
      string open = app != null ? "open -a " + ShQuote(app) + (string.IsNullOrEmpty(args) ? "" : " --args " + args)
                                : ShQuote(launcher) + (string.IsNullOrEmpty(args) ? "" : " " + args) + " >/dev/null 2>&1 &";
      string file = Path.Combine(OS.DesktopDir, safe + ".app");
      string script = "do shell script \"" + open.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
      using (var p = Process.Start(new ProcessStartInfo("osacompile", "-o " + OS.Quote(file) + " -e " + OS.Quote(script)) { UseShellExecute = false, CreateNoWindow = true }))
        p?.WaitForExit(15000);
      return file;
    }
    string text = "[Desktop Entry]\nType=Application\nName=" + name + "\nComment=Port launcher\n" +
                  "Exec=" + DesktopExec(launcher) + (string.IsNullOrEmpty(args) ? "" : " " + args) + "\n" +
                  "Path=" + workDir + "\nIcon=" + LinuxIcon() + "\nTerminal=false\nCategories=Game;\n";
    string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    string apps = Path.Combine(!string.IsNullOrEmpty(dataHome) ? dataHome : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "applications");
    string id = "port-launcher-" + new string(safe.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()) + ".desktop";
    Directory.CreateDirectory(apps);
    string menu = Path.Combine(apps, id);
    File.WriteAllText(menu, text, new UTF8Encoding(false));
    MakeExecutable(menu);
    string desk = OS.DesktopDir;
    if (Directory.Exists(desk)) {
      string onDesk = Path.Combine(desk, id);
      File.WriteAllText(onDesk, text, new UTF8Encoding(false));
      MakeExecutable(onDesk);
      // GNOME runs a desktop file only once it is marked trusted.
      try { Process.Start(new ProcessStartInfo("gio", "set " + OS.Quote(onDesk) + " metadata::trusted true") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000); } catch (Exception) { }
      return onDesk;
    }
    return menu;
  }

  // The launcher's own icon, installed by the Linux package next to the program (launcher.png), else a theme icon.
  static string LinuxIcon() {
    string dir = Path.GetDirectoryName(Environment.GetEnvironmentVariable("APPIMAGE") is string img && img.Length > 0 ? img : OS.ExePath);
    string png = Path.Combine(dir ?? "", "port-launcher.png");
    return File.Exists(png) ? png : "applications-games";
  }

  static string DesktopExec(string path) => path.IndexOfAny(new[] { ' ', '"', '\'', '\\', '$', '`' }) < 0 ? path : "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";
  public static string ShQuote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

  public static void MakeExecutable(string file) {
    if (OS.Windows) return;
    try { using (var p = Process.Start(new ProcessStartInfo("chmod", "+x " + OS.Quote(file)) { UseShellExecute = false, CreateNoWindow = true })) p?.WaitForExit(5000); } catch (Exception) { }
  }
}

}
