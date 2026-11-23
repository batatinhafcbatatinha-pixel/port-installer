// Recompiler mode: the launcher inside (or next to) a recompiler repository such as PS2Recomp, whose
// games/<game>/ folders each hold one game's port (setup.json + installer.json). The repository's own
// launcher.json gives what all its games share (PC settings, tools, executable and ini paths); each game's
// setup.json and installer.json give its name, disc, build steps and the hashes of the supported disc.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace PortLauncher {

sealed class RecompGame {
  public string Id, Dir, Name, Disc;
  public bool Supported = true;   // builds and runs on this system (its recompiler does and setup.json does not say "linux": false)
  public HashSet<string> Executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // disc file names of installer.json required_files
  public Dictionary<string, string> Required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // path -> SHA-256
  public Dictionary<string, long> Sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);         // expected_files
}

sealed class Recomp {
  public string Root, Name, Repo, Branch, Setup, ToolsDir, LauncherJson;
  public bool Supported = true;                  // runs on this system (launcher.json has its "linux"/"macos" block; see OS.Supports)
  public List<RecompGame> Games = new List<RecompGame>();
  public IDictionary<string, object> Generic;   // launcher.json "generic": setup.json template for discs with no profile
  public string CoverUrl;                        // launcher.json "cover_url": cover pictures by disc serial ({serial} = SLUS-12345)
  public string LabelRegex = @"^[A-Z]{4}_\d{3}\.\d{2}$";   // launcher.json "generic_label": the disc labels of this console (PS2 boot file)
  public string PortsDir => Path.Combine(Root, ".local", "ports");

  // A PlayStation 5 dump folder (eboot.bin + sce_sys/param.json): its title id (PPSA12345) and name, else null.
  public static string Ps5Title(string path, out string name) {
    name = null;
    if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "eboot.bin"))) return null;
    byte[] param = ReadDiscFile(path, "sce_sys/param.json");
    if (param == null) return null;
    try {
      var d = PortLauncher.Json.Object(Encoding.UTF8.GetString(param).TrimStart('﻿'));
      string id = Str(d, "titleId");
      if (d.TryGetValue("localizedParameters", out var lp) && lp is IDictionary<string, object> loc) {
        string def = Str(loc, "defaultLanguage", "en-US");
        var lang = (loc.TryGetValue(def, out var a) ? a : loc.TryGetValue("en-US", out var b) ? b : loc.Values.FirstOrDefault(v => v is IDictionary<string, object>)) as IDictionary<string, object>;
        name = Str(lang, "titleName");
      }
      return string.IsNullOrEmpty(id) ? null : id;
    } catch (Exception) { return null; }
  }

  // An Xbox One / Series dump folder (MicrosoftGame.config at its top): its title id (eight hex digits) and name,
  // else null. A config without a title id gets one from its own contents, so the same dump is always the same game.
  public static string XsTitle(string path, out string name) {
    name = null;
    if (!Directory.Exists(path)) return null;
    byte[] config = ReadDiscFile(path, "MicrosoftGame.config");
    if (config == null) return null;
    string text = Encoding.UTF8.GetString(config).TrimStart('\uFEFF');
    if (text.IndexOf("<Game", StringComparison.OrdinalIgnoreCase) < 0) return null;
    var m = Regex.Match(text, @"<ShellVisuals\b[^>]*\bDefaultDisplayName\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
    if (m.Success && !m.Groups[1].Value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
      name = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
    m = Regex.Match(text, @"<TitleId>\s*([0-9A-Fa-f]{8})\s*</TitleId>", RegexOptions.IgnoreCase);
    return m.Success ? m.Groups[1].Value.ToUpperInvariant() : Sha256(config).Substring(0, 8).ToUpperInvariant();
  }

  // A PSP disc: its id (UMD_DATA.BIN: ULUS-12345) and name (PARAM.SFO TITLE), else null.
  public static string PspTitle(string disc, out string name) {
    name = SfoTitle(ReadDiscFile(disc, "PSP_GAME/PARAM.SFO"));
    byte[] umd = ReadDiscFile(disc, "UMD_DATA.BIN");
    string id = umd == null ? null : Encoding.ASCII.GetString(umd).Split('|')[0].Trim('\0', ' ');
    return string.IsNullOrEmpty(id) ? null : id;
  }

  // The TITLE of a PARAM.SFO.
  public static string SfoTitle(byte[] d) {
    try {
      if (d == null || d.Length < 20 || d[0] != 0 || d[1] != (byte)'P' || d[2] != (byte)'S' || d[3] != (byte)'F') return null;
      int keys = BitConverter.ToInt32(d, 8), values = BitConverter.ToInt32(d, 12), count = BitConverter.ToInt32(d, 16);
      for (int i = 0; i < count; ++i) {
        int e = 20 + i * 16;
        int keyOff = BitConverter.ToUInt16(d, e), len = BitConverter.ToInt32(d, e + 4), dataOff = BitConverter.ToInt32(d, e + 12);
        int k = keys + keyOff, end = Array.IndexOf(d, (byte)0, k);
        if (end < 0 || Encoding.ASCII.GetString(d, k, end - k) != "TITLE") continue;
        string t = Encoding.UTF8.GetString(d, values + dataOff, len).Split('\0')[0].Trim();
        return t.Length > 0 ? t : null;
      }
    } catch (Exception) { }
    return null;
  }

  // An original Xbox disc: the title id of default.xbe's certificate (4D530000) and its title name, else null.
  public static string XboxTitle(string disc, out string name) {
    name = null;
    try {
      byte[] x = ReadDiscFile(disc, "default.xbe", false, 1 << 20);
      if (x == null || x.Length < 0x178 || Encoding.ASCII.GetString(x, 0, 4) != "XBEH") return null;
      uint baseAddr = BitConverter.ToUInt32(x, 0x104), cert = BitConverter.ToUInt32(x, 0x118);
      long c = (long)cert - baseAddr;
      if (c < 0 || c + 0x0C + 80 > x.Length) return null;
      uint titleId = BitConverter.ToUInt32(x, (int)c + 8);
      name = Encoding.Unicode.GetString(x, (int)c + 0x0C, 80).Split('\0')[0].Trim();
      if (name.Length == 0) name = null;
      return titleId.ToString("X8");
    } catch (Exception) { return null; }
  }

  // An Xbox 360 disc: the title id of default.xex (its execution info header), else null.
  public static string X360Title(string disc) {
    try {
      byte[] x = ReadDiscFile(disc, "default.xex", false, 1 << 16);
      if (x == null || x.Length < 0x18 || Encoding.ASCII.GetString(x, 0, 4) != "XEX2") return null;
      uint Be(int o) => (uint)(x[o] << 24 | x[o + 1] << 16 | x[o + 2] << 8 | x[o + 3]);
      uint count = Be(0x14);
      for (uint i = 0; i < count && 0x18 + i * 8 + 8 <= x.Length; ++i) {
        int e = (int)(0x18 + i * 8);
        if (Be(e) != 0x00040006u) continue;
        int info = (int)Be(e + 4);
        return info + 16 <= x.Length ? Be(info + 12).ToString("X8") : null;
      }
    } catch (Exception) { }
    return null;
  }

  // A port folder for a disc that has no profile in the repository: built by the generic pipeline (tools/ps2port;
  // PS5 dumps: tools/ps5port, with the game linked instead of copied). Returns the new game's id, or null when the disc cannot be read.
  public string CreateGenericPort(string disc, string boot) {
    if (Generic == null || boot == null) return null;
    string ps5 = Ps5Title(disc, out string ps5Name);
    if (ps5 != null) return CreatePs5Port(disc, ps5, ps5Name);
    string xs = XsTitle(disc, out string xsName);
    if (xs != null) return CreateLinkedPort(disc, xs, xsName, "Xbox One / Series (" + xs + ")", "MicrosoftGame.config");
    if (BootExecutable(disc) == null) return CreateDiscPort(disc);
    byte[] cnf = ReadDiscFile(disc, "SYSTEM.CNF"), elf = ReadDiscFile(disc, boot);
    if (cnf == null || elf == null) return null;
    string id = boot.Replace('.', '_').Replace('_', '-');   // SLUS_123.45 -> SLUS-123-45
    string dir = Path.Combine(PortsDir, id);
    Directory.CreateDirectory(dir);
    string title = Directory.Exists(disc) ? new DirectoryInfo(disc).Name : Path.GetFileNameWithoutExtension(disc);
    var setup = new Dictionary<string, object>(Generic);
    setup["game"] = title;
    setup["disc"] = "PlayStation 2 (" + boot + ")";
        File.WriteAllText(Path.Combine(dir, "setup.json"), PortLauncher.Json.Write(setup));
    var installer = new Dictionary<string, object> {
      ["schema"] = 1, ["name"] = title, ["revision"] = boot, ["filesystem"] = "iso9660", ["destination"] = "game_files",
      ["required_files"] = new Dictionary<string, object> { ["SYSTEM.CNF"] = Sha256(cnf).ToLowerInvariant(), [boot] = Sha256(elf).ToLowerInvariant() },
      ["required_paths"] = new object[0],
    };
    File.WriteAllText(Path.Combine(dir, "installer.json"), PortLauncher.Json.Write(installer));
    File.WriteAllText(Path.Combine(dir, "Play.cmd"),
        "@echo off\r\nrem Starts this game (built by the launcher with tools/ps2port).\r\n" +
        "python \"%~dp0..\\..\\..\\tools\\ps2port\\ps2port.py\" play --port \"%~dp0.\"\r\nexit /b %errorlevel%\r\n");
    if (!OS.Windows) {
      // Linux / macOS: the same start script for bash (setup.json keeps "play": "Play.cmd"; OS.ScriptName picks Play.sh).
      string sh = Path.Combine(dir, "Play.sh");
      File.WriteAllText(sh, "#!/usr/bin/env bash\n# Starts this game (built by the launcher with tools/ps2port).\n" +
          "here=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\n" +
          "exec \"${PYTHON:-python3}\" \"$here/../../../tools/ps2port/ps2port.py\" play --port \"$here\" \"$@\"\n");
      Shortcuts.MakeExecutable(sh);
    }
    return id;
  }

  // PSP, Xbox and Xbox 360 discs: the port folder is named after the disc's id (ULUS-12345, 4D530000) and recognises
  // the disc again by the SHA-256 of its identity files (PSP: UMD_DATA.BIN + PARAM.SFO; Xbox: default.xbe; Xbox 360:
  // default.xex). The build itself is the recompiler's "generic" steps; "play_cmd" is the start script.
  string CreateDiscPort(string disc) {
    string console = Consoles.Identify(disc, out string label), name = null, fs, what;
    string[] identity;
    switch (console) {
      case "psp": label = PspTitle(disc, out name); identity = new[] { "UMD_DATA.BIN", "PSP_GAME/PARAM.SFO" }; fs = "iso9660"; what = "PSP"; break;
      case "xbox": label = XboxTitle(disc, out name); identity = new[] { "default.xbe" }; fs = "xdvdfs"; what = "Xbox"; break;
      case "x360": {
        // A disc (XDVDFS), or a package (Xbox Live Arcade, games on demand) that names its game itself.
        label = X360Title(disc);
        identity = new[] { "default.xex" };
        string package = GameInstall.StfsPackage(disc);
        fs = package != null ? "stfs" : "xdvdfs";
        if (package != null) name = GameInstall.StfsTitle(package);
        what = package != null ? "Xbox 360 Arcade" : "Xbox 360";
        break;
      }
      default: return null;
    }
    if (label == null) return null;
    var required = new Dictionary<string, object>();
    var sizes = new Dictionary<string, object>();
    foreach (string rel in identity) {
      string sha = DiscFileSha256(disc, rel, out long size);
      if (sha == null) return null;
      required[rel] = sha;
      sizes[rel] = size;
    }
    string id = Regex.Replace(label, @"[^A-Za-z0-9_-]", "-");
    string dir = Path.Combine(PortsDir, id);
    Directory.CreateDirectory(dir);
    string title = name ?? (Directory.Exists(disc) ? new DirectoryInfo(disc).Name : Path.GetFileNameWithoutExtension(disc));
    var setup = new Dictionary<string, object>(Generic);
    setup.Remove("play_cmd");
    setup["game"] = title;
    setup["disc"] = what + " (" + label + ")";
    File.WriteAllText(Path.Combine(dir, "setup.json"), PortLauncher.Json.Write(setup));
    var installer = new Dictionary<string, object> {
      ["schema"] = 1, ["name"] = title, ["revision"] = label, ["filesystem"] = fs, ["destination"] = "game_files",
      ["required_files"] = required, ["expected_files"] = sizes, ["required_paths"] = new object[0],
    };
    File.WriteAllText(Path.Combine(dir, "installer.json"), PortLauncher.Json.Write(installer));
    string play = Str(Generic, "play_cmd");
    if (play != null) {
      string file = Path.Combine(dir, OS.ScriptName(Str(Generic, "play", "Play.cmd")));
      File.WriteAllText(file, play);
      Shortcuts.MakeExecutable(file);
    }
    return id;
  }

  // The SHA-256 (lower case) and size of one file of a disc image or extracted folder, whatever its size; null when missing.
  public static string DiscFileSha256(string disc, string rel, out long size) {
    size = 0;
    try {
      using (var sha = System.Security.Cryptography.SHA256.Create()) {
        string package = GameInstall.StfsPackage(disc);
        if (package != null) disc = package;
        if (Directory.Exists(disc)) {
          string f = Path.Combine(disc, rel.Replace('/', Path.DirectorySeparatorChar));
          if (!File.Exists(f)) return null;
          using (var h = File.OpenRead(f)) { size = h.Length; return BitConverter.ToString(sha.ComputeHash(h)).Replace("-", "").ToLowerInvariant(); }
        }
        using (var h = File.OpenRead(disc)) {
          List<GameInstall.Entry> entries = null;
          foreach (string fs in package != null ? new[] { "stfs" } : new[] { "xdvdfs", "iso9660" }) {
            try { entries = GameInstall.Inventory(h, h.Length, fs); break; } catch (InvalidDataException) { }
          }
          var e = entries?.FirstOrDefault(x => string.Equals(x.Path, rel, StringComparison.OrdinalIgnoreCase));
          if (e == null) return null;
          size = e.Size;
          var buf = new byte[1 << 20];
          foreach (var (offset, length) in e.Extents) {
            h.Seek(offset, SeekOrigin.Begin);
            for (long left = length; left > 0;) {
              int n = h.Read(buf, 0, (int)Math.Min(buf.Length, left));
              if (n <= 0) return null;
              sha.TransformBlock(buf, 0, n, null, 0);
              left -= n;
            }
          }
          sha.TransformFinalBlock(new byte[0], 0, 0);
          return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
      }
    } catch (Exception) { return null; }
  }

  // A PS5 dump: the game stays where it is (installer.json "mode": "link" makes game_files a junction to it);
  // the dump is recognised again by the SHA-256 of its sce_sys/param.json.
  string CreatePs5Port(string disc, string titleId, string titleName) =>
      CreateLinkedPort(disc, titleId, titleName, "PlayStation 5 (" + titleId + ")", "sce_sys/param.json");

  // A dump that is used where it is: keyFile (a small file of the dump) is how it is recognised again.
  string CreateLinkedPort(string disc, string titleId, string titleName, string discName, string keyFile) {
    byte[] param = ReadDiscFile(disc, keyFile);
    if (param == null) return null;
    string dir = Path.Combine(PortsDir, titleId);
    Directory.CreateDirectory(dir);
    string title = string.IsNullOrEmpty(titleName) ? new DirectoryInfo(disc).Name : titleName;
    var setup = new Dictionary<string, object>(Generic);
    setup.Remove("play_cmd");
    setup["game"] = title;
    setup["disc"] = discName;
        File.WriteAllText(Path.Combine(dir, "setup.json"), PortLauncher.Json.Write(setup));
    var installer = new Dictionary<string, object> {
      ["schema"] = 1, ["name"] = title, ["revision"] = titleId, ["filesystem"] = "folder", ["mode"] = "link", ["destination"] = "game_files",
      ["required_files"] = new Dictionary<string, object> { [keyFile] = Sha256(param).ToLowerInvariant() },
      ["expected_files"] = new Dictionary<string, object> { [keyFile] = param.LongLength },
      ["required_paths"] = new object[0],
    };
    File.WriteAllText(Path.Combine(dir, "installer.json"), PortLauncher.Json.Write(installer));
    string play = Str(Generic, "play_cmd");
    if (play != null) {
      string file = Path.Combine(dir, OS.ScriptName(Str(Generic, "play", "Play.cmd")));
      File.WriteAllText(file, play);
      Shortcuts.MakeExecutable(file);
    }
    return titleId;
  }

  // A JSON file with this system's block ("linux": {...}) merged over its top level.
  static IDictionary<string, object> ReadJson(string path) => OS.ForThisSystem(RawJson(path));
  static IDictionary<string, object> RawJson(string path) => PortLauncher.Json.Object(File.ReadAllText(path));
  static string Str(IDictionary<string, object> d, string k, string def = null) =>
      d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v) : def;

  // The repository holding this launcher: the given folder or one of its parents with a launcher.json that names a games folder.
  public static Recomp Find(string start) {
    for (var dir = start == null ? null : new DirectoryInfo(Path.GetFullPath(start)); dir != null; dir = dir.Parent) {
      string json = Path.Combine(dir.FullName, "launcher.json");
      if (File.Exists(json)) {
        try { var r = Load(dir.FullName); if (r != null) return r; } catch (Exception) { }
      }
    }
    return null;
  }

  public static Recomp Load(string root) {
    string json = Path.Combine(root, "launcher.json");
    var raw = RawJson(json);
    var d = OS.ForThisSystem(raw);
    string games = Str(d, "games_dir");
    if (games == null) return null;
    var r = new Recomp {
      Root = root, Name = Str(d, "recomp", Path.GetFileName(root)), Repo = Str(d, "repo"), Branch = Str(d, "branch", "main"),
      Setup = Path.Combine(root, OS.ScriptName(Str(d, "setup", "tools/port-installer/setup/setup.ps1"))),
      Supported = OS.Supports(raw),
      ToolsDir = Path.Combine(root, Str(d, "tools_dir", ".local/tools")), LauncherJson = File.ReadAllText(json),
    };
    r.Generic = d.TryGetValue("generic", out var gen) ? gen as IDictionary<string, object> : null;
    r.LabelRegex = Str(d, "generic_label", r.LabelRegex);
    r.CoverUrl = Str(d, "cover_url", r.Name.IndexOf("PS2", StringComparison.OrdinalIgnoreCase) >= 0 ? Covers.DefaultPs2Url : null);
    // Games of the repository (games/), then games the player added from a disc with no profile (.local/ports).
    var dirs = new List<string>();
    foreach (string folder in new[] { Path.Combine(root, games), r.PortsDir })
      if (Directory.Exists(folder)) dirs.AddRange(Directory.GetDirectories(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    foreach (var dir in dirs) {
        string setup = Path.Combine(dir, "setup.json"), installer = Path.Combine(dir, "installer.json");
        if (!File.Exists(setup) || !File.Exists(installer)) continue;
        try {
          var rawSetup = RawJson(setup);
          var s = OS.ForThisSystem(rawSetup);
          var g = new RecompGame { Id = Path.GetFileName(dir), Dir = dir, Name = Str(s, "game", Path.GetFileName(dir)), Disc = Str(s, "disc", ""),
                                   Supported = r.Supported && OS.Supports(rawSetup, false) };
          var inst = ReadJson(installer);
          if (inst.TryGetValue("required_files", out var req) && req is IDictionary<string, object> files)
            foreach (var kv in files) { g.Executables.Add(Path.GetFileName(kv.Key)); g.Required[kv.Key] = Convert.ToString(kv.Value); }
          if (inst.TryGetValue("expected_files", out var exp) && exp is IDictionary<string, object> sizes)
            foreach (var kv in sizes) g.Sizes[kv.Key] = Convert.ToInt64(kv.Value);
          r.Games.Add(g);
        } catch (Exception) { }
      }
    return r;
  }

  // One game's launcher configuration: the repository's shared part with the game's own values on top.
  public GameConfig ConfigFor(RecompGame game) {
    // A game with its own PC menu keys (games/<game>/launcher.json) uses that file instead of the shared one.
    string own = Path.Combine(game.Dir, "launcher.json");
    var c = GameConfig.Parse(File.Exists(own) ? File.ReadAllText(own) : LauncherJson);
    var s = ReadJson(Path.Combine(game.Dir, "setup.json"));
    c.Game = game.Name;
    c.Disc = game.Disc;
    c.Play = OS.ScriptName(Str(s, "play", c.Play));
    c.Minutes = Str(s, "minutes", c.Minutes);
    if (s.TryGetValue("disk_gb", out var gb) && gb != null) c.DiskGb = Convert.ToInt32(gb);
    if (s.TryGetValue("exe", out var exe) && exe != null) c.Exe = Convert.ToString(exe);
    if (s.TryGetValue("pc_menu", out var menu) && menu is bool hasMenu) c.PcMenu = hasMenu;
    // A game built with the generic path (.local/ports) has the generic path's settings, not those of the
    // repository's own games.
    bool generic = Path.GetFullPath(game.Dir).StartsWith(Path.GetFullPath(PortsDir) + Path.DirectorySeparatorChar,
                                                          StringComparison.OrdinalIgnoreCase);
    if (generic && Generic != null && Generic.TryGetValue("settings", out var gs) && gs is IDictionary<string, object> settings)
      c.ApplySettings(settings);
    c.Repo = Repo;
    c.ToolsRoot = Root;
    c.GameFiles.Clear();
    var inst = ReadJson(Path.Combine(game.Dir, "installer.json"));
    string dest = Str(inst, "destination", "game_files");
    if (inst.TryGetValue("required_files", out var req) && req is IDictionary<string, object> files)
      foreach (var kv in files) c.GameFiles[dest + "/" + kv.Key] = Convert.ToString(kv.Value);
    return c;
  }

  public RecompGame GameForDisc(string boot) => boot == null ? null : Games.FirstOrDefault(g => g.Executables.Contains(boot));

  // The scripts the launcher runs (setup, game-file installer) live in tools/port-installer, a submodule of the
  // repository. A ZIP download or a clone without submodules leaves it empty: the copy inside this exe fills it.
  public void EnsureInstallerFiles(Action<string> log = null) {
    if (File.Exists(Setup)) return;
    string target = Path.GetDirectoryName(Path.GetDirectoryName(Setup));   // .../tools/port-installer
    // Linux / macOS with a port-installer from before setup.sh existed: the launcher's own copy is used from its
    // cache instead (the recompiler's folder is left as it is).
    if (!OS.Windows && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) {
      string own = Path.Combine(Prefs.Cache, "port-installer");
      Extract(own);
      Setup = Path.Combine(own, "setup", "setup.sh");
      log?.Invoke("port-installer -> " + own);
      return;
    }
    Extract(target);
    log?.Invoke("port-installer -> " + target);
  }

  static void Extract(string target) {
    var asm = Assembly.GetExecutingAssembly();
    foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith("pi/"))) {
      string dest = Path.Combine(target, name.Substring(3).Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(dest));
      using (var src = asm.GetManifestResourceStream(name))
      using (var dst = File.Create(dest)) src.CopyTo(dst);
      if (dest.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)) Shortcuts.MakeExecutable(dest);
    }
  }

  // The newest source of a recompiler (its branch as a ZIP from GitHub: code only, nothing from any game).
  public static string Download(string repo, string branch, string target, Action<string> log) {
    target = Path.GetFullPath(target);
    if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !File.Exists(Path.Combine(target, "launcher.json")))
      throw new IOException(L.T("A pasta escolhida não está vazia: ", "The chosen folder is not empty: ") + target);
    string sha = null;
    try { sha = Updater.RemoteHead(repo, branch); } catch (Exception) { }
    string url = sha != null ? $"https://codeload.github.com/{repo}/zip/{sha}" : $"https://codeload.github.com/{repo}/zip/refs/heads/{branch}";
    string zip = Path.Combine(Path.GetTempPath(), repo.Replace('/', '-') + "-" + branch + ".zip");
    log(L.T("Baixando ", "Downloading ") + url);
    using (var web = new WebClient()) {
      web.Headers[HttpRequestHeader.UserAgent] = "port-launcher";
      web.DownloadFile(url, zip);
    }
    log(L.T("Extraindo para ", "Extracting to ") + target);
    KitDownload.Unzip(zip, target);
    File.Delete(zip);
    if (sha != null) Updater.SaveLocalHead(target, sha);
    log("OK");
    return target;
  }

  // Which game a disc image (or extracted folder) is. PlayStation 2: the executable SYSTEM.CNF boots. Other discs
  // (PSP: UMD_DATA.BIN, PARAM.SFO): every small required file of a game's installer.json must match its SHA-256.
  public RecompGame Detect(string path, out string label) {
    label = BootExecutable(path);
    if (label != null) return GameForDisc(label);
    label = Ps5Title(path, out _);   // PS5 dump: matched below by the SHA-256 of sce_sys/param.json
    label = label ?? XsTitle(path, out _);   // Xbox One / Series dump: by the SHA-256 of MicrosoftGame.config
    try {
      byte[] umd = ReadDiscFile(path, "UMD_DATA.BIN");
      if (umd != null) label = Encoding.ASCII.GetString(umd).Split('|')[0].Trim('\0', ' ');
      label = label ?? XboxTitle(path, out _) ?? X360Title(path);
      foreach (var g in Games) {
        var small = g.Required.Where(kv => g.Sizes.TryGetValue(kv.Key, out long n) && n <= (16 << 20)).ToList();
        if (small.Count == 0) continue;
        bool all = true;
        foreach (var kv in small) {
          byte[] data = ReadDiscFile(path, kv.Key);
          if (data == null || !string.Equals(Sha256(data), kv.Value, StringComparison.OrdinalIgnoreCase)) { all = false; break; }
        }
        if (all) return g;
      }
    } catch (Exception) { }
    return null;
  }

  static string Sha256(byte[] data) {
    using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
  }

  // The executable named by SYSTEM.CNF (BOOT2 = cdrom0:\SLUS_123.45;1) of a PlayStation 2 disc image or extracted folder.
  public static string BootExecutable(string path) {
    try {
      byte[] cnf = ReadDiscFile(path, "SYSTEM.CNF");
      if (cnf == null) return null;
      var m = Regex.Match(Encoding.ASCII.GetString(cnf), @"BOOT2?\s*=\s*cdrom0?:\\?([^;\s]+)", RegexOptions.IgnoreCase);
      return m.Success ? Path.GetFileName(m.Groups[1].Value.Replace('\\', '/')) : null;
    } catch (Exception) { return null; }
  }

  // A file (up to 16 MB) of an extracted folder or of an ISO 9660 image with 2048-byte sectors; null when missing.
  // A file of an Xbox / Xbox 360 disc image (XDVDFS): plain XISO or a full XGD1/2/3 image; null when not XDVDFS.
  static byte[] XdvdfsFile(string iso, string rel, bool probe = false, int head = 0) {
    using (var f = File.OpenRead(iso)) {
      long[] partitions = { 0, 0x18300000, 0x0FD90000, 0x02080000 };
      var vd = new byte[2048];
      foreach (long part in partitions) {
        if (part + 33 * 2048 > f.Length) continue;
        f.Seek(part + 32 * 2048, SeekOrigin.Begin);
        if (f.Read(vd, 0, 2048) != 2048 || Encoding.ASCII.GetString(vd, 0, 20) != "MICROSOFT*XBOX*MEDIA") continue;
        uint sector = BitConverter.ToUInt32(vd, 20), size = BitConverter.ToUInt32(vd, 24);
        bool isDir = true;
        foreach (string name in rel.Split('/')) {
          if (!isDir || size == 0 || size > (4u << 20)) return null;
          var dir = new byte[size];
          f.Seek(part + (long)sector * 2048, SeekOrigin.Begin);
          f.Read(dir, 0, dir.Length);
          // The entries form a binary tree (left/right offsets in 4-byte units); visit them all.
          var pending = new Stack<int>();
          pending.Push(0);
          bool found = false;
          var seen = new HashSet<int>();
          while (pending.Count > 0 && !found) {
            int off = pending.Pop();
            if (off + 14 > dir.Length || !seen.Add(off)) continue;
            int left = BitConverter.ToUInt16(dir, off) * 4, right = BitConverter.ToUInt16(dir, off + 2) * 4;
            if (BitConverter.ToUInt16(dir, off) == 0xFFFF) continue;
            int nameLen = dir[off + 13];
            if (off + 14 + nameLen > dir.Length) continue;
            if (string.Equals(Encoding.ASCII.GetString(dir, off + 14, nameLen), name, StringComparison.OrdinalIgnoreCase)) {
              sector = BitConverter.ToUInt32(dir, off + 4);
              size = BitConverter.ToUInt32(dir, off + 8);
              isDir = (dir[off + 12] & 0x10) != 0;
              found = true;
            }
            if (left != 0) pending.Push(left);
            if (right != 0) pending.Push(right);
          }
          if (!found) return null;
        }
        if (isDir) return null;
        if (probe) return new byte[0];
        if (head > 0) size = Math.Min(size, (uint)head);
        else if (size > (16u << 20)) return null;
        var data = new byte[size];
        f.Seek(part + (long)sector * 2048, SeekOrigin.Begin);
        int got = 0;
        while (got < data.Length) { int n = f.Read(data, got, data.Length - got); if (n <= 0) break; got += n; }
        return data;
      }
    }
    return null;
  }

  // Whether a disc image or extracted folder has the file, whatever its size.
  public static bool DiscHas(string disc, string rel) => ReadDiscFile(disc, rel, true) != null;

  // One file of an Xbox 360 package (STFS), as ReadDiscFile returns it.
  static byte[] PackageFile(string package, string rel, bool probe, int head) {
    try {
      using (var h = File.OpenRead(package)) {
        var e = GameInstall.Inventory(h, h.Length, "stfs").FirstOrDefault(x => string.Equals(x.Path, rel, StringComparison.OrdinalIgnoreCase));
        if (e == null) return null;
        if (probe) return new byte[0];
        long want = head > 0 ? Math.Min(e.Size, head) : e.Size;
        if (want > (16 << 20)) return null;
        var data = new byte[want];
        long got = 0;
        foreach (var (offset, length) in e.Extents) {
          if (got >= want) break;
          h.Seek(offset, SeekOrigin.Begin);
          int n = (int)Math.Min(length, want - got), r = 0;
          while (r < n) { int k = h.Read(data, (int)got + r, n - r); if (k <= 0) return null; r += k; }
          got += n;
        }
        return data;
      }
    } catch (Exception) { return null; }
  }

  // head > 0: only the first head bytes, whatever the file's size (an executable's header).
  public static byte[] ReadDiscFile(string disc, string rel, bool probe = false, int head = 0) {
    string package = GameInstall.StfsPackage(disc);
    if (package != null) return PackageFile(package, rel, probe, head);
    if (Directory.Exists(disc)) {
      string f = Path.Combine(disc, rel.Replace('/', Path.DirectorySeparatorChar));
      if (probe) return File.Exists(f) ? new byte[0] : null;
      if (!File.Exists(f)) return null;
      if (head > 0) {
        using (var h = File.OpenRead(f)) {
          var buf = new byte[Math.Min(h.Length, head)];
          int got = 0;
          while (got < buf.Length) { int n = h.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
          return buf;
        }
      }
      return new FileInfo(f).Length <= (16 << 20) ? File.ReadAllBytes(f) : null;
    }
    if (!File.Exists(disc)) return null;
    byte[] xbox = XdvdfsFile(disc, rel, probe, head);
    if (xbox != null) return xbox;
    using (var f = File.OpenRead(disc)) {
      var pvd = new byte[2048];
      f.Seek(16 * 2048, SeekOrigin.Begin);
      if (f.Read(pvd, 0, 2048) != 2048 || pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return null;
      uint lba = BitConverter.ToUInt32(pvd, 156 + 2), size = BitConverter.ToUInt32(pvd, 156 + 10);
      var parts = rel.Split('/');
      for (int level = 0; level < parts.Length; ++level) {
        var dir = new byte[Math.Min(size, 1u << 20)];
        f.Seek((long)lba * 2048, SeekOrigin.Begin);
        f.Read(dir, 0, dir.Length);
        bool found = false;
        for (int i = 0; i < dir.Length;) {
          int len = dir[i];
          if (len == 0) { i = (i / 2048 + 1) * 2048; continue; }
          if (i + 33 > dir.Length) break;
          int nameLen = dir[i + 32];
          string name = Encoding.ASCII.GetString(dir, i + 33, Math.Min(nameLen, dir.Length - i - 33));
          int semi = name.IndexOf(';');
          if (semi >= 0) name = name.Substring(0, semi);
          if (string.Equals(name, parts[level], StringComparison.OrdinalIgnoreCase)) {
            lba = BitConverter.ToUInt32(dir, i + 2); size = BitConverter.ToUInt32(dir, i + 10);
            found = true;
            break;
          }
          i += len;
        }
        if (!found) return null;
      }
      if (probe) return new byte[0];
      if (head > 0) size = Math.Min(size, (uint)head);
      else if (size > (16u << 20)) return null;
      var data = new byte[size];
      f.Seek((long)lba * 2048, SeekOrigin.Begin);
      int got = 0;
      while (got < data.Length) { int n = f.Read(data, got, data.Length - got); if (n <= 0) break; got += n; }
      return data;
    }
  }
}

// The recompilers this launcher can download when it runs on its own (recomps.json inside the exe).
sealed class KnownRecomp {
  public string Id, Console, Name, Repo, Branch, Folder, Pt, En;
  public string[] Platforms = { "windows" };   // recomps.json "platforms": the systems its games build and run on
  public bool Supported => Platforms.Contains(OS.Key);

  public static List<KnownRecomp> List() {
    var list = new List<KnownRecomp>();
    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("recomps.json")) {
      if (s == null) return list;
      using (var r = new StreamReader(s)) {
        var items = PortLauncher.Json.Parse(r.ReadToEnd()) as object[];
        foreach (var o in items ?? new object[0])
          if (o is IDictionary<string, object> d)
            list.Add(new KnownRecomp {
              Id = d.TryGetValue("id", out var id) ? Convert.ToString(id) : Convert.ToString(d["folder"]),
              Console = d.TryGetValue("console", out var con) ? Convert.ToString(con) : null,
              Name = Convert.ToString(d["name"]), Repo = Convert.ToString(d["repo"]),
              Branch = d.TryGetValue("branch", out var b) ? Convert.ToString(b) : "main",
              Folder = d.TryGetValue("folder", out var f) ? Convert.ToString(f) : Convert.ToString(d["name"]),
              Pt = d.TryGetValue("pt", out var pt) ? Convert.ToString(pt) : "", En = d.TryGetValue("en", out var en) ? Convert.ToString(en) : "",
              Platforms = d.TryGetValue("platforms", out var pl) && pl is object[] arr ? arr.Select(x => Convert.ToString(x)).ToArray() : new[] { "windows" },
            });
      }
    }
    return list;
  }
}

}
