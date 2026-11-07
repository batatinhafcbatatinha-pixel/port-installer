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
using System.Web.Script.Serialization;

namespace PortLauncher {

sealed class RecompGame {
  public string Id, Dir, Name, Disc;
  public HashSet<string> Executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // disc file names of installer.json required_files
  public Dictionary<string, string> Required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // path -> SHA-256
  public Dictionary<string, long> Sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);         // expected_files
}

sealed class Recomp {
  public string Root, Name, Repo, Branch, Setup, ToolsDir, LauncherJson;
  public List<RecompGame> Games = new List<RecompGame>();
  public IDictionary<string, object> Generic;   // launcher.json "generic": setup.json template for discs with no profile
  public string PortsDir => Path.Combine(Root, ".local", "ports");

  // A port folder for a disc that has no profile in the repository: built by the generic pipeline (tools/ps2port).
  // Returns the new game's id, or null when the disc cannot be read.
  public string CreateGenericPort(string disc, string boot) {
    if (Generic == null || boot == null) return null;
    byte[] cnf = ReadDiscFile(disc, "SYSTEM.CNF"), elf = ReadDiscFile(disc, boot);
    if (cnf == null || elf == null) return null;
    string id = boot.Replace('.', '_').Replace('_', '-');   // SLUS_214.23 -> SLUS-214-23
    string dir = Path.Combine(PortsDir, id);
    Directory.CreateDirectory(dir);
    string title = Directory.Exists(disc) ? new DirectoryInfo(disc).Name : Path.GetFileNameWithoutExtension(disc);
    var setup = new Dictionary<string, object>(Generic);
    setup["game"] = title;
    setup["disc"] = "PlayStation 2 (" + boot + ")";
    var js = new JavaScriptSerializer();
    File.WriteAllText(Path.Combine(dir, "setup.json"), js.Serialize(setup));
    var installer = new Dictionary<string, object> {
      ["schema"] = 1, ["name"] = title, ["revision"] = boot, ["filesystem"] = "iso9660", ["destination"] = "game_files",
      ["required_files"] = new Dictionary<string, object> { ["SYSTEM.CNF"] = Sha256(cnf).ToLowerInvariant(), [boot] = Sha256(elf).ToLowerInvariant() },
      ["required_paths"] = new object[0],
    };
    File.WriteAllText(Path.Combine(dir, "installer.json"), js.Serialize(installer));
    File.WriteAllText(Path.Combine(dir, "Play.cmd"),
        "@echo off\r\nrem Starts this game (built by the launcher with tools/ps2port).\r\n" +
        "python \"%~dp0..\\..\\..\\tools\\ps2port\\ps2port.py\" play --port \"%~dp0.\"\r\nexit /b %errorlevel%\r\n");
    return id;
  }

  static IDictionary<string, object> Json(string path) =>
      (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
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
    var d = Json(json);
    string games = Str(d, "games_dir");
    if (games == null) return null;
    var r = new Recomp {
      Root = root, Name = Str(d, "recomp", Path.GetFileName(root)), Repo = Str(d, "repo"), Branch = Str(d, "branch", "main"),
      Setup = Path.Combine(root, Str(d, "setup", "tools/port-installer/setup/setup.ps1")),
      ToolsDir = Path.Combine(root, Str(d, "tools_dir", ".local/tools")), LauncherJson = File.ReadAllText(json),
    };
    r.Generic = d.TryGetValue("generic", out var gen) ? gen as IDictionary<string, object> : null;
    // Games of the repository (games/), then games the player added from a disc with no profile (.local/ports).
    var dirs = new List<string>();
    foreach (string folder in new[] { Path.Combine(root, games), r.PortsDir })
      if (Directory.Exists(folder)) dirs.AddRange(Directory.GetDirectories(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    foreach (var dir in dirs) {
        string setup = Path.Combine(dir, "setup.json"), installer = Path.Combine(dir, "installer.json");
        if (!File.Exists(setup) || !File.Exists(installer)) continue;
        try {
          var s = Json(setup);
          var g = new RecompGame { Id = Path.GetFileName(dir), Dir = dir, Name = Str(s, "game", Path.GetFileName(dir)), Disc = Str(s, "disc", "") };
          var inst = Json(installer);
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
    var s = Json(Path.Combine(game.Dir, "setup.json"));
    c.Game = game.Name;
    c.Disc = game.Disc;
    c.Play = Str(s, "play", c.Play);
    c.Minutes = Str(s, "minutes", c.Minutes);
    if (s.TryGetValue("disk_gb", out var gb) && gb != null) c.DiskGb = Convert.ToInt32(gb);
    if (s.TryGetValue("exe", out var exe) && exe != null) c.Exe = Convert.ToString(exe);
    c.Repo = Repo;
    c.ToolsRoot = Root;
    c.GameFiles.Clear();
    var inst = Json(Path.Combine(game.Dir, "installer.json"));
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
    var asm = Assembly.GetExecutingAssembly();
    foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith("pi/"))) {
      string dest = Path.Combine(target, name.Substring(3).Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(dest));
      using (var src = asm.GetManifestResourceStream(name))
      using (var dst = File.Create(dest)) src.CopyTo(dst);
    }
    log?.Invoke("port-installer -> " + target);
  }

  // The newest source of a recompiler (its branch as a ZIP from GitHub: code only, nothing from any game).
  public static string Download(string repo, string branch, string target, Action<string> log) {
    target = Path.GetFullPath(target);
    if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !File.Exists(Path.Combine(target, "launcher.json")))
      throw new IOException(L.T("A pasta escolhida não está vazia: ", "The chosen folder is not empty: ") + target);
    string url = $"https://codeload.github.com/{repo}/zip/refs/heads/{branch}";
    string zip = Path.Combine(Path.GetTempPath(), repo.Replace('/', '-') + "-" + branch + ".zip");
    log(L.T("Baixando ", "Downloading ") + url);
    using (var web = new WebClient()) {
      web.Headers[HttpRequestHeader.UserAgent] = "port-launcher";
      web.DownloadFile(url, zip);
    }
    log(L.T("Extraindo para ", "Extracting to ") + target);
    KitDownload.Unzip(zip, target);
    File.Delete(zip);
    log("OK");
    return target;
  }

  // Which game a disc image (or extracted folder) is. PlayStation 2: the executable SYSTEM.CNF boots. Other discs
  // (PSP: UMD_DATA.BIN, PARAM.SFO): every small required file of a game's installer.json must match its SHA-256.
  public RecompGame Detect(string path, out string label) {
    label = BootExecutable(path);
    if (label != null) return GameForDisc(label);
    try {
      byte[] umd = ReadDiscFile(path, "UMD_DATA.BIN");
      if (umd != null) label = Encoding.ASCII.GetString(umd).Split('|')[0].Trim('\0', ' ');
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

  // The executable named by SYSTEM.CNF (BOOT2 = cdrom0:\SLUS_214.23;1) of a PlayStation 2 disc image or extracted folder.
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
  static byte[] XdvdfsFile(string iso, string rel) {
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
        if (isDir || size > (16u << 20)) return null;
        var data = new byte[size];
        f.Seek(part + (long)sector * 2048, SeekOrigin.Begin);
        int got = 0;
        while (got < data.Length) { int n = f.Read(data, got, data.Length - got); if (n <= 0) break; got += n; }
        return data;
      }
    }
    return null;
  }

  static byte[] ReadDiscFile(string disc, string rel) {
    if (Directory.Exists(disc)) {
      string f = Path.Combine(disc, rel.Replace('/', Path.DirectorySeparatorChar));
      return File.Exists(f) && new FileInfo(f).Length <= (16 << 20) ? File.ReadAllBytes(f) : null;
    }
    if (!File.Exists(disc)) return null;
    byte[] xbox = XdvdfsFile(disc, rel);
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
      if (size > (16u << 20)) return null;
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
  public string Name, Repo, Branch, Folder, Pt, En;

  public static List<KnownRecomp> List() {
    var list = new List<KnownRecomp>();
    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("recomps.json")) {
      if (s == null) return list;
      using (var r = new StreamReader(s)) {
        var items = new JavaScriptSerializer().DeserializeObject(r.ReadToEnd()) as object[];
        foreach (var o in items ?? new object[0])
          if (o is IDictionary<string, object> d)
            list.Add(new KnownRecomp {
              Name = Convert.ToString(d["name"]), Repo = Convert.ToString(d["repo"]),
              Branch = d.TryGetValue("branch", out var b) ? Convert.ToString(b) : "main",
              Folder = d.TryGetValue("folder", out var f) ? Convert.ToString(f) : Convert.ToString(d["name"]),
              Pt = d.TryGetValue("pt", out var pt) ? Convert.ToString(pt) : "", En = d.TryGetValue("en", out var en) ? Convert.ToString(en) : "",
            });
      }
    }
    return list;
  }
}

}
