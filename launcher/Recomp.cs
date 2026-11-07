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
}

sealed class Recomp {
  public string Root, Name, Repo, Branch, Setup, ToolsDir, LauncherJson;
  public List<RecompGame> Games = new List<RecompGame>();

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
    string gamesDir = Path.Combine(root, games);
    if (Directory.Exists(gamesDir))
      foreach (var dir in Directory.GetDirectories(gamesDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) {
        string setup = Path.Combine(dir, "setup.json"), installer = Path.Combine(dir, "installer.json");
        if (!File.Exists(setup) || !File.Exists(installer)) continue;
        try {
          var s = Json(setup);
          var g = new RecompGame { Id = Path.GetFileName(dir), Dir = dir, Name = Str(s, "game", Path.GetFileName(dir)), Disc = Str(s, "disc", "") };
          if (Json(installer).TryGetValue("required_files", out var req) && req is IDictionary<string, object> files)
            foreach (var name in files.Keys) g.Executables.Add(Path.GetFileName(name));
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

  // The executable named by SYSTEM.CNF (BOOT2 = cdrom0:\SLUS_214.23;1) of a PlayStation 2 disc image or extracted folder.
  public static string BootExecutable(string path) {
    try {
      string cnf = null;
      if (Directory.Exists(path)) {
        string f = Path.Combine(path, "SYSTEM.CNF");
        if (File.Exists(f)) cnf = File.ReadAllText(f, Encoding.ASCII);
      } else if (File.Exists(path)) {
        cnf = IsoFile(path, "SYSTEM.CNF");
      }
      if (cnf == null) return null;
      var m = Regex.Match(cnf, @"BOOT2?\s*=\s*cdrom0?:\\?([^;\s]+)", RegexOptions.IgnoreCase);
      return m.Success ? Path.GetFileName(m.Groups[1].Value.Replace('\\', '/')) : null;
    } catch (Exception) { return null; }
  }

  // A file of the ISO 9660 root directory (2048-byte sectors), as text.
  static string IsoFile(string iso, string wanted) {
    using (var f = File.OpenRead(iso)) {
      var pvd = new byte[2048];
      f.Seek(16 * 2048, SeekOrigin.Begin);
      if (f.Read(pvd, 0, 2048) != 2048 || pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return null;
      uint rootLba = BitConverter.ToUInt32(pvd, 156 + 2), rootSize = BitConverter.ToUInt32(pvd, 156 + 10);
      var dir = new byte[Math.Min(rootSize, 1u << 20)];
      f.Seek((long)rootLba * 2048, SeekOrigin.Begin);
      f.Read(dir, 0, dir.Length);
      for (int i = 0; i < dir.Length;) {
        int len = dir[i];
        if (len == 0) { i = (i / 2048 + 1) * 2048; continue; }
        if (i + 33 > dir.Length) break;
        int nameLen = dir[i + 32];
        string name = Encoding.ASCII.GetString(dir, i + 33, Math.Min(nameLen, dir.Length - i - 33));
        int semi = name.IndexOf(';');
        if (semi >= 0) name = name.Substring(0, semi);
        if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) {
          uint lba = BitConverter.ToUInt32(dir, i + 2), size = BitConverter.ToUInt32(dir, i + 10);
          var data = new byte[Math.Min(size, 65536u)];
          f.Seek((long)lba * 2048, SeekOrigin.Begin);
          f.Read(data, 0, data.Length);
          return Encoding.ASCII.GetString(data);
        }
        i += len;
      }
    }
    return null;
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
