// The launcher as one game library for every console: the recompilers installed on this PC (the one the launcher
// sits in, plus any downloaded or located from the Recompilers window), the console each disc belongs to, and a scan
// of the player's game folders that matches each disc with its recompiler's game.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PortLauncher {

static class Consoles {
  public static readonly string[] Order = { "ps2", "xbox", "x360", "psp", "ps5" };

  public static string Name(string id) {
    switch (id) {
      case "ps2": return "PlayStation 2";
      case "xbox": return "Xbox";
      case "x360": return "Xbox 360";
      case "psp": return "PSP";
      case "ps5": return "PlayStation 5";
      default: return id ?? "?";
    }
  }

  public static string Short(string id) {
    switch (id) {
      case "ps2": return "PS2";
      case "x360": return "Xbox 360";
      case "ps5": return "PS5";
      default: return Name(id);
    }
  }

  // Which console a disc image or extracted disc folder is for, and its label (PS2 boot file, PSP disc id, PS5 title id).
  public static string Identify(string path, out string label) {
    label = Recomp.BootExecutable(path);
    if (label != null) return "ps2";
    label = Recomp.Ps5Title(path, out _);
    if (label != null) return "ps5";
    try {
      byte[] umd = Recomp.ReadDiscFile(path, "UMD_DATA.BIN");
      if (umd != null) { label = Encoding.ASCII.GetString(umd).Split('|')[0].Trim('\0', ' '); return "psp"; }
      if (Recomp.DiscHas(path, "PSP_GAME/PARAM.SFO")) return "psp";
      if (Recomp.DiscHas(path, "default.xex")) return "x360";
      if (Recomp.DiscHas(path, "default.xbe")) return "xbox";
    } catch (Exception) { }
    return null;
  }
}

// The recompilers known to this launcher (recomps.json) and the ones installed on this PC
// (%APPDATA%\port-launcher\recomps.txt: "<id>\t<folder>" lines; the folder the launcher runs from is added by itself).
sealed class RecompRegistry {
  public readonly List<KnownRecomp> Known = KnownRecomp.List();
  public readonly Dictionary<string, Recomp> Installed = new Dictionary<string, Recomp>(StringComparer.OrdinalIgnoreCase);   // by known id
  public string Home;   // the recompiler the launcher runs from (its id), or null

  static string FileName => Path.Combine(Prefs.Dir, "recomps.txt");

  public KnownRecomp ForConsole(string console) => Known.FirstOrDefault(k => k.Console == console);
  public Recomp RecompForConsole(string console) { var k = ForConsole(console); return k != null && Installed.TryGetValue(k.Id, out var r) ? r : null; }
  public KnownRecomp KnownFor(Recomp r) => Known.FirstOrDefault(k => Installed.TryGetValue(k.Id, out var x) && x == r);

  // Which known recompiler a repository folder is (its launcher.json "repo", else the folder name).
  public KnownRecomp Match(Recomp r) {
    string repo = r.Repo ?? "";
    return Known.FirstOrDefault(k => string.Equals(k.Repo, repo, StringComparison.OrdinalIgnoreCase))
        ?? Known.FirstOrDefault(k => repo.EndsWith("/" + k.Folder, StringComparison.OrdinalIgnoreCase))
        ?? Known.FirstOrDefault(k => string.Equals(Path.GetFileName(r.Root.TrimEnd('\\')), k.Folder, StringComparison.OrdinalIgnoreCase));
  }

  public static RecompRegistry Load(string home) {
    var reg = new RecompRegistry();
    var here = home == null ? null : Recomp.Find(home);
    if (here != null) {
      var k = reg.Match(here);
      if (k != null) { reg.Remember(k.Id, here.Root); reg.Home = k.Id; }
    }
    foreach (var kv in reg.Saved()) {
      if (reg.Installed.ContainsKey(kv.Key) && reg.Home != kv.Key) continue;
      try { var r = Recomp.Load(kv.Value); if (r != null) reg.Installed[kv.Key] = r; } catch (Exception) { }
    }
    if (here != null && reg.Home != null) reg.Installed[reg.Home] = here;
    return reg;
  }

  List<KeyValuePair<string, string>> Saved() {
    var list = new List<KeyValuePair<string, string>>();
    try {
      if (File.Exists(FileName))
        foreach (string line in File.ReadAllLines(FileName)) {
          var p = line.Split('\t');
          if (p.Length == 2 && p[0].Length > 0 && Directory.Exists(p[1])) list.Add(new KeyValuePair<string, string>(p[0], p[1]));
        }
    } catch (Exception) { }
    return list;
  }

  // Remembers the folder of a recompiler (the newest one wins for its id).
  public void Remember(string id, string root) {
    try {
      var rows = Saved().Where(kv => kv.Key != id).ToList();
      rows.Add(new KeyValuePair<string, string>(id, Path.GetFullPath(root).TrimEnd('\\')));
      Directory.CreateDirectory(Prefs.Dir);
      File.WriteAllLines(FileName, rows.Select(kv => kv.Key + "\t" + kv.Value).ToArray());
    } catch (Exception) { }
  }

  public void Forget(string id) {
    try {
      Directory.CreateDirectory(Prefs.Dir);
      File.WriteAllLines(FileName, Saved().Where(kv => kv.Key != id).Select(kv => kv.Key + "\t" + kv.Value).ToArray());
    } catch (Exception) { }
    Installed.Remove(id);
    if (Home == id) Home = null;
  }

  public Recomp Reload(Recomp r) {
    var k = KnownFor(r);
    var fresh = Recomp.Load(r.Root) ?? r;
    if (k != null) Installed[k.Id] = fresh;
    return fresh;
  }

  // Signature of what the installed recompilers recognise: a change makes the scan read every disc again.
  public string Signature() =>
      "v2 " + ((uint)string.Join(";", Installed.OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value.Root + ":" +
          string.Join("|", kv.Value.Games.Select(g => g.Id + "=" + string.Join(",", g.Required.Values.OrderBy(v => v)))))).GetHashCode()).ToString("x8");
}

sealed class FoundDisc {
  public string Path, Label, Console;   // Label: the boot executable (PS2), disc id (PSP) or title id (PS5), when the disc has one
  public Recomp Recomp;                 // the installed recompiler of the disc's console, or null
  public RecompGame Game;               // null: no profile in that recompiler (or no recompiler)
  public bool IsFolder;
}

static class DiscScanner {
  static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "Windows", "$Recycle.Bin", "System Volume Information", "node_modules", ".git", ".local", "game_files", "AppData",
  };

  // Every disc image (.iso) or extracted disc folder under the folders, with its console and game.
  public static List<FoundDisc> Scan(RecompRegistry reg, IList<string> folders, Action<string> progress, CancellationToken cancel) {
    var cache = LoadCache(reg);
    var fresh = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    var found = new List<FoundDisc>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    foreach (string root in folders) {
      foreach (var candidate in Candidates(root, cancel)) {
        cancel.ThrowIfCancellationRequested();
        string path = candidate.Key;
        if (!seen.Add(path)) continue;
        if (clock.ElapsedMilliseconds > 150) { progress?.Invoke(path); clock.Restart(); }
        string stamp;
        try {
          stamp = candidate.Value
              ? Directory.GetLastWriteTimeUtc(path).Ticks.ToString()
              : new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks;
        } catch (Exception) { continue; }
        string console, label, id;
        if (cache.TryGetValue(path, out var row) && row[0] == stamp) { console = row[1]; label = row[2]; id = row[3]; }
        else {
          label = null; id = "";
          try { console = Consoles.Identify(path, out label); } catch (Exception) { console = null; }
          var rc = console == null ? null : reg.RecompForConsole(console);
          if (rc != null) {
            try { var g = rc.Detect(path, out string l2); id = g?.Id ?? ""; label = label ?? l2; } catch (Exception) { }
          }
          console = console ?? "";
          label = label ?? "";
        }
        fresh[path] = new[] { stamp, console, label, id };
        if (console.Length == 0) continue;   // not a disc of a known console
        var recomp = reg.RecompForConsole(console);
        var game = recomp != null && id.Length > 0 ? recomp.Games.FirstOrDefault(x => x.Id == id) : null;
        found.Add(new FoundDisc { Path = path, Console = console, Label = label.Length > 0 ? label : null, Recomp = recomp, Game = game, IsFolder = candidate.Value });
      }
    }
    SaveCache(reg, fresh);
    return found.OrderBy(f => f.IsFolder).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
  }

  // (path, is a folder): .iso files, and folders that hold an extracted disc (not looked into further).
  static IEnumerable<KeyValuePair<string, bool>> Candidates(string root, CancellationToken cancel) {
    if (Directory.Exists(root) && IsExtractedDisc(root)) { yield return new KeyValuePair<string, bool>(root, true); yield break; }
    if (File.Exists(root) && root.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) { yield return new KeyValuePair<string, bool>(root, false); yield break; }
    if (!Directory.Exists(root)) yield break;
    var pending = new Stack<KeyValuePair<string, int>>();
    pending.Push(new KeyValuePair<string, int>(root, 0));
    while (pending.Count > 0) {
      cancel.ThrowIfCancellationRequested();
      var item = pending.Pop();
      string dir = item.Key;
      if (item.Value > 0 && IsExtractedDisc(dir)) { yield return new KeyValuePair<string, bool>(dir, true); continue; }
      string[] files = null, dirs = null;
      try { files = Directory.GetFiles(dir, "*.iso"); dirs = Directory.GetDirectories(dir); } catch (Exception) { }
      if (files != null)
        foreach (string f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) yield return new KeyValuePair<string, bool>(f, false);
      if (dirs == null || item.Value >= 8) continue;
      foreach (string d in dirs.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)) {
        try {
          var info = new DirectoryInfo(d);
          if (SkipDirs.Contains(info.Name) || info.Name.StartsWith("$")) continue;
          if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue;
        } catch (Exception) { continue; }
        pending.Push(new KeyValuePair<string, int>(d, item.Value + 1));
      }
    }
  }

  public static bool IsExtractedDisc(string dir) =>
      File.Exists(Path.Combine(dir, "SYSTEM.CNF")) || Directory.Exists(Path.Combine(dir, "PSP_GAME")) ||
      File.Exists(Path.Combine(dir, "default.xbe")) || File.Exists(Path.Combine(dir, "default.xex")) ||
      (File.Exists(Path.Combine(dir, "eboot.bin")) && File.Exists(Path.Combine(dir, "sce_sys", "param.json")));   // PS5 dump

  static string CacheFile => Path.Combine(Prefs.Cache, "scan-all.tsv");

  static Dictionary<string, string[]> LoadCache(RecompRegistry reg) {
    var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    try {
      var lines = File.ReadAllLines(CacheFile, Encoding.UTF8);
      if (lines.Length == 0 || lines[0] != reg.Signature()) return d;
      foreach (string line in lines.Skip(1)) {
        var p = line.Split('\t');
        if (p.Length == 5) d[p[0]] = new[] { p[1], p[2], p[3], p[4] };
      }
    } catch (Exception) { }
    return d;
  }

  static void SaveCache(RecompRegistry reg, Dictionary<string, string[]> rows) {
    try {
      Directory.CreateDirectory(Prefs.Cache);
      var lines = new List<string> { reg.Signature() };
      lines.AddRange(rows.Select(kv => kv.Key + "\t" + string.Join("\t", kv.Value)));
      File.WriteAllLines(CacheFile, lines, Encoding.UTF8);
    } catch (Exception) { }
  }
}

}
