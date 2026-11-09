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
