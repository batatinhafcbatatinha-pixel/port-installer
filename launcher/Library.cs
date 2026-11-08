// The game library: the folders the player added, the discs found in them (with a cache so a rescan only
// reads new or changed images) and each game's cover picture (the player's own, the disc's icon, or one
// downloaded from a cover site such as PCSX2 uses; else the list draws a tile from the name).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PortLauncher {

// Launcher preferences (%APPDATA%\port-launcher\launcher.ini) and its caches (%LOCALAPPDATA%\port-launcher).
static class Prefs {
  // PORT_LAUNCHER_HOME (test aid) keeps preferences and caches in another folder.
  static string Home => Environment.GetEnvironmentVariable("PORT_LAUNCHER_HOME");
  public static string Dir => Home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "port-launcher");
  public static string Cache => Home != null ? Path.Combine(Home, "cache") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "port-launcher");
  static string File => Path.Combine(Dir, "launcher.ini");
  public static int Int(string key, int def) { try { return Ini.Get(File, "launcher", key, def); } catch (Exception) { return def; } }
  public static bool Bool(string key, bool def) => Int(key, def ? 1 : 0) != 0;
  public static string Text(string key, string def) { try { string v = Ini.GetText(File, "launcher", key); return v.Length > 0 ? v : def; } catch (Exception) { return def; } }
  public static void Set(string key, int value) { try { Directory.CreateDirectory(Dir); Ini.Put(File, "launcher", key, value); } catch (Exception) { } }
  public static void Set(string key, bool value) => Set(key, value ? 1 : 0);
  public static void Set(string key, string value) { try { Directory.CreateDirectory(Dir); Ini.PutText(File, "launcher", key, value); } catch (Exception) { } }
}

// The folders scanned for disc images, shared by every recompiler on this PC.
static class GameFolders {
  static string FileName => Path.Combine(Prefs.Dir, "game-folders.txt");

  public static List<string> Load() {
    try {
      if (!File.Exists(FileName)) return new List<string>();
      return File.ReadAllLines(FileName).Select(l => l.Trim()).Where(l => l.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    } catch (Exception) { return new List<string>(); }
  }

  public static void Save(IEnumerable<string> folders) {
    Directory.CreateDirectory(Prefs.Dir);
    File.WriteAllLines(FileName, folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
  }

  // Adds the folders not in the list yet; returns how many were new.
  public static int Add(IEnumerable<string> folders) {
    var list = Load();
    int added = 0;
    foreach (string f in folders) {
      string full = Path.GetFullPath(f).TrimEnd('\\');
      if (full.Length == 2 && full[1] == ':') full += "\\";
      if (list.Any(x => string.Equals(x.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) continue;
      list.Add(full);
      ++added;
    }
    if (added > 0) Save(list);
    return added;
  }
}

sealed class FoundDisc {
  public string Path, Label;    // Label: the boot executable (PS2) or disc id (PSP), when the disc has one
  public RecompGame Game;       // null: no profile in this recompiler
  public bool IsFolder;
}

static class DiscScanner {
  static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "Windows", "$Recycle.Bin", "System Volume Information", "node_modules", ".git", ".local", "game_files", "AppData",
  };

  // Every disc image (.iso) or extracted disc folder under the folders, with the game it is.
  public static List<FoundDisc> Scan(Recomp recomp, IList<string> folders, Action<string> progress, CancellationToken cancel) {
    var cache = LoadCache(recomp);
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
        string label, id;
        if (cache.TryGetValue(path, out var row) && row[0] == stamp) { label = row[1]; id = row[2]; }
        else {
          RecompGame g = null;
          label = null;
          try { g = recomp.Detect(path, out label); } catch (Exception) { }
          id = g?.Id ?? "";
          label = label ?? "";
        }
        fresh[path] = new[] { stamp, label, id };
        var game = id.Length > 0 ? recomp.Games.FirstOrDefault(x => x.Id == id) : null;
        if (game == null && label.Length == 0) continue;   // not a disc of this console
        found.Add(new FoundDisc { Path = path, Label = label.Length > 0 ? label : null, Game = game, IsFolder = candidate.Value });
      }
    }
    SaveCache(recomp, fresh);
    // An .iso before an extracted folder of the same game, then by name.
    return found.OrderBy(f => f.IsFolder).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
  }

  // (path, is a folder): .iso files, and folders that hold an extracted disc (not looked into further).
  static IEnumerable<KeyValuePair<string, bool>> Candidates(string root, CancellationToken cancel) {
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

  static bool IsExtractedDisc(string dir) =>
      File.Exists(Path.Combine(dir, "SYSTEM.CNF")) || Directory.Exists(Path.Combine(dir, "PSP_GAME")) ||
      File.Exists(Path.Combine(dir, "default.xbe")) || File.Exists(Path.Combine(dir, "default.xex")) ||
      (File.Exists(Path.Combine(dir, "eboot.bin")) && File.Exists(Path.Combine(dir, "sce_sys", "param.json")));   // PS5 dump

  // One cache per recompiler; it starts over when the recompiler's games change (a new profile may match old discs).
  static string CacheFile(Recomp r) =>
      Path.Combine(Prefs.Cache, "scan-" + ((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(r.Root))).ToString("x8") + ".tsv");
  static string Signature(Recomp r) =>
      "v1 " + ((uint)string.Join("|", r.Games.Select(g => g.Id + "=" + string.Join(",", g.Required.Values.OrderBy(v => v)))).GetHashCode()).ToString("x8");

  static Dictionary<string, string[]> LoadCache(Recomp r) {
    var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    try {
      var lines = File.ReadAllLines(CacheFile(r), Encoding.UTF8);
      if (lines.Length == 0 || lines[0] != Signature(r)) return d;
      foreach (string line in lines.Skip(1)) {
        var p = line.Split('\t');
        if (p.Length == 4) d[p[0]] = new[] { p[1], p[2], p[3] };
      }
    } catch (Exception) { }
    return d;
  }

  static void SaveCache(Recomp r, Dictionary<string, string[]> rows) {
    try {
      Directory.CreateDirectory(Prefs.Cache);
      var lines = new List<string> { Signature(r) };
      lines.AddRange(rows.Select(kv => kv.Key + "\t" + string.Join("\t", kv.Value)));
      File.WriteAllLines(CacheFile(r), lines, Encoding.UTF8);
    } catch (Exception) { }
  }
}

// Cover pictures, as small bitmaps ready to draw.
static class Covers {
  public const string DefaultPs2Url = "https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/default/{serial}.jpg";
  static string CacheDir => Path.Combine(Prefs.Cache, "covers");
  static string CustomDir => Path.Combine(Prefs.Dir, "covers");
  static readonly string[] Exts = { ".png", ".jpg", ".jpeg" };

  // SLUS_214.23 -> SLUS-21423; ULUS-10041 stays.
  public static string Serial(string label) {
    if (string.IsNullOrEmpty(label)) return null;
    var m = Regex.Match(label, @"^([A-Z]{4})[_-]?(\d{3})\.?(\d{2})", RegexOptions.IgnoreCase);
    return m.Success ? (m.Groups[1].Value + "-" + m.Groups[2].Value + m.Groups[3].Value).ToUpperInvariant() : null;
  }

  static string SafeName(string key) => Regex.Replace(key, @"[^A-Za-z0-9._-]", "_");

  public static string CustomFile(string key) {
    foreach (string ext in Exts) { string f = Path.Combine(CustomDir, SafeName(key) + ext); if (File.Exists(f)) return f; }
    return null;
  }

  public static void SetCustom(string key, string image) {
    ClearCustom(key);
    Directory.CreateDirectory(CustomDir);
    string ext = Path.GetExtension(image).ToLowerInvariant();
    File.Copy(image, Path.Combine(CustomDir, SafeName(key) + (ext == ".png" ? ".png" : ".jpg")), true);
  }

  public static void ClearCustom(string key) {
    foreach (string ext in Exts) { try { File.Delete(Path.Combine(CustomDir, SafeName(key) + ext)); } catch (Exception) { } }
  }

  // The best picture for a game (slow: reads the disc or the network; call it off the UI thread).
  public static Bitmap Find(string key, string serial, string gameDir, string disc, string urlTemplate, bool online) {
    var files = new List<string> { CustomFile(key) };
    if (gameDir != null) foreach (string ext in Exts) files.Add(Path.Combine(gameDir, "cover" + ext));
    if (serial != null) foreach (string ext in Exts) files.Add(Path.Combine(CacheDir, SafeName(serial) + ext));
    files.Add(Path.Combine(CacheDir, SafeName(key) + ".png"));
    if (gameDir != null) files.Add(Path.Combine(gameDir, "game_files", "PSP_GAME", "ICON0.PNG"));
    if (gameDir != null) files.Add(Path.Combine(gameDir, "game_files", "sce_sys", "icon0.png"));
    foreach (string f in files) {
      if (f != null && File.Exists(f)) { var b = Thumb(f); if (b != null) return b; }
    }
    // The disc's own icon (PSP: PSP_GAME/ICON0.PNG; PS5 dump: sce_sys/icon0.png).
    if (disc != null) {
      try {
        byte[] icon = Recomp.ReadDiscFile(disc, "PSP_GAME/ICON0.PNG") ?? Recomp.ReadDiscFile(disc, "sce_sys/icon0.png");
        if (icon != null) {
          Directory.CreateDirectory(CacheDir);
          string f = Path.Combine(CacheDir, SafeName(serial ?? key) + ".png");
          File.WriteAllBytes(f, icon);
          var b = Thumb(f);
          if (b != null) return b;
        }
      } catch (Exception) { }
    }
    if (online && serial != null && !string.IsNullOrEmpty(urlTemplate)) {
      string miss = Path.Combine(CacheDir, SafeName(serial) + ".none");
      if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < TimeSpan.FromDays(7)) return null;
      try {
        Directory.CreateDirectory(CacheDir);
        string url = urlTemplate.Replace("{serial}", serial).Replace("{serial_}", serial.Replace('-', '_'));
        string ext = url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        string f = Path.Combine(CacheDir, SafeName(serial) + ext), part = f + ".part";
        using (var web = new WebClient()) {
          web.Headers[HttpRequestHeader.UserAgent] = "port-launcher";
          web.DownloadFile(url, part);
        }
        if (File.Exists(f)) File.Delete(f);
        File.Move(part, f);
        return Thumb(f);
      } catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound) {
        try { File.WriteAllText(miss, ""); } catch (Exception) { }
      } catch (Exception) { }
    }
    return null;
  }

  // A copy at most 360 px tall (the largest grid size), without keeping the file open.
  static Bitmap Thumb(string file) {
    try {
      using (var s = new MemoryStream(File.ReadAllBytes(file)))
      using (var img = Image.FromStream(s)) {
        float k = Math.Min(1f, Math.Min(360f / img.Height, 360f / img.Width));
        int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
        var b = new Bitmap(w, h);
        using (var g = Graphics.FromImage(b)) {
          g.InterpolationMode = InterpolationMode.HighQualityBicubic;
          g.PixelOffsetMode = PixelOffsetMode.HighQuality;
          g.DrawImage(img, 0, 0, w, h);
        }
        return b;
      }
    } catch (Exception) { return null; }
  }
}

}
