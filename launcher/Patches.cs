// Patches and cheats from GitHub repositories: the ones the emulator communities keep (PCSX2's pnach files, the
// CWCheat database PPSSPP uses, Xenia's game patches, the PS4/PS5 lists) or any repository the player adds.
// Each source lists the entries for one game; the chosen ones are written next to the game and applied by its
// recompiler (PS2Recomp: tools/ps2port hotpatch, which retranslates only the patched functions in seconds).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PortLauncher {

enum PatchKind { Patch, Cheat }

// One repository with patches or cheats for a console.
sealed class PatchSource {
  public string Name, Repo, Branch = "main", Folder = "", Console, Format;   // Format: pnach, cwcheat, xenia, files
  public PatchKind Kind;
  public bool Builtin;

  public string Key => Repo + "|" + Folder + "|" + Kind;

  // The communities' own repositories, checked to exist (2026-11).
  public static readonly PatchSource[] Recommended = {
    new PatchSource { Name = "PCSX2 patches (widescreen, 60 fps, sem entrelaçamento)", Repo = "PCSX2/pcsx2_patches", Folder = "patches", Console = "ps2", Format = "pnach", Kind = PatchKind.Patch, Builtin = true },
    new PatchSource { Name = "CWCheat Database Plus (PPSSPP)", Repo = "Saramagrean/CWCheat-Database-Plus-", Branch = "master", Folder = "cheat.db", Console = "psp", Format = "cwcheat", Kind = PatchKind.Cheat, Builtin = true },
    new PatchSource { Name = "CWCheat Database Plus: códigos de FPS e câmera", Repo = "Saramagrean/CWCheat-Database-Plus-", Branch = "master", Folder = "cheat.db", Console = "psp", Format = "cwcheat", Kind = PatchKind.Patch, Builtin = true },
    new PatchSource { Name = "Xenia Canary game patches", Repo = "xenia-canary/game-patches", Folder = "patches", Console = "x360", Format = "xenia", Kind = PatchKind.Patch, Builtin = true },
    new PatchSource { Name = "PS4/PS5 Game Patch (illusion0001)", Repo = "illusion0001/PS4-PS5-Game-Patch", Folder = "patches/xml", Console = "ps5", Format = "files", Kind = PatchKind.Patch, Builtin = true },
    new PatchSource { Name = "GoldHEN Cheat Repository", Repo = "GoldHEN/GoldHEN_Cheat_Repository", Folder = "", Console = "ps5", Format = "files", Kind = PatchKind.Cheat, Builtin = true },
  };

  static string ListFile => Path.Combine(Prefs.Dir, "patch-sources.txt");

  // Recommended sources of the console and kind, then the ones the player added.
  public static List<PatchSource> For(string console, PatchKind kind) {
    var list = Recommended.Where(s => s.Console == console && s.Kind == kind).ToList();
    try {
      if (File.Exists(ListFile))
        foreach (string line in File.ReadAllLines(ListFile)) {
          var p = line.Split('\t');
          if (p.Length < 6 || p[0] != console || p[1] != kind.ToString()) continue;
          list.Add(new PatchSource { Console = p[0], Kind = kind, Repo = p[2], Branch = p[3], Folder = p[4], Format = p[5], Name = p[2] + (p[4].Length > 0 ? "/" + p[4] : "") });
        }
    } catch (Exception) { }
    return list;
  }

  // "owner/repo", "owner/repo/path" or a github.com URL (…/tree/<branch>/<path>).
  public static PatchSource Parse(string text, string console, PatchKind kind) {
    text = (text ?? "").Trim().TrimEnd('/');
    var m = Regex.Match(text, @"^(?:https?://github\.com/)?([\w.-]+)/([\w.-]+?)(?:\.git)?(?:/tree/([^/]+))?(?:/(.*))?$");
    if (!m.Success) return null;
    var s = new PatchSource { Console = console, Kind = kind, Repo = m.Groups[1].Value + "/" + m.Groups[2].Value, Folder = m.Groups[4].Value };
    if (m.Groups[3].Success) s.Branch = m.Groups[3].Value;
    else {
      try { s.Branch = Convert.ToString(Updater.Api("repos/" + s.Repo)["default_branch"]); } catch (Exception) { return null; }
    }
    s.Format = console == "ps2" ? "pnach" : console == "psp" ? "cwcheat" : console == "x360" ? "xenia" : "files";
    s.Name = s.Repo + (s.Folder.Length > 0 ? "/" + s.Folder : "");
    return s;
  }

  public void Remember() {
    Directory.CreateDirectory(Prefs.Dir);
    var lines = File.Exists(ListFile) ? File.ReadAllLines(ListFile).ToList() : new List<string>();
    string row = string.Join("\t", Console, Kind.ToString(), Repo, Branch, Folder, Format);
    if (!lines.Contains(row)) lines.Add(row);
    File.WriteAllLines(ListFile, lines);
  }

  // ------------------------------------------------------------------ downloads (cached a day)

  static string CacheDir => Path.Combine(Prefs.Cache, "patches");
  static string Safe(string s) => Regex.Replace(s, @"[^\w.-]", "_");

  static string Cached(string name, Func<string> fetch, TimeSpan age) {
    string file = Path.Combine(CacheDir, Safe(name));
    if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < age) return File.ReadAllText(file, Encoding.UTF8);
    try {
      string text = fetch();
      Directory.CreateDirectory(CacheDir);
      File.WriteAllText(file, text, Encoding.UTF8);
      return text;
    } catch (Exception) {
      if (File.Exists(file)) return File.ReadAllText(file, Encoding.UTF8);   // offline: the last copy
      throw;
    }
  }

  public string Raw(string path) => Cached(Repo + "@" + Branch + "@" + path, () => {
    using (var web = new WebClient { Encoding = Encoding.UTF8 }) {
      web.Headers[HttpRequestHeader.UserAgent] = "port-launcher";
      return web.DownloadString($"https://raw.githubusercontent.com/{Repo}/{Branch}/{Uri.EscapeUriString(path)}");
    }
  }, TimeSpan.FromDays(1));

  // Every file path of the repository (one API call, cached).
  public List<string> Files() {
    string json = Cached(Repo + "@" + Branch + "@tree", () => {
      using (var web = new WebClient { Encoding = Encoding.UTF8 }) {
        web.Headers[HttpRequestHeader.UserAgent] = "port-launcher";
        web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
        return web.DownloadString($"https://api.github.com/repos/{Repo}/git/trees/{Branch}?recursive=1");
      }
    }, TimeSpan.FromDays(1));
    var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as IDictionary<string, object>;
    var tree = d != null && d.TryGetValue("tree", out var t) ? t as object[] : null;
    return (tree ?? new object[0]).OfType<IDictionary<string, object>>()
        .Where(e => Convert.ToString(e["type"]) == "blob").Select(e => Convert.ToString(e["path"]))
        .Where(p => Folder.Length == 0 || p == Folder || p.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase)).ToList();
  }
}

// One patch or cheat: a named group of code lines in its source's format.
sealed class PatchEntry {
  public string Title, Description, Author, File;
  public List<string> Lines = new List<string>();
  public bool Supported = true;   // the recompiler can apply it (PS2: EE writes)
  public override string ToString() => Title;
}

// What identifies a game in the patch repositories.
sealed class PatchGame {
  public string Console, Serial, Crc, Name, TitleId;
}

static class PatchLibrary {
  // The entries of one source for one game; `note` says what was matched or why nothing was.
  public static List<PatchEntry> Load(PatchSource source, PatchGame game, out string note) {
    note = "";
    switch (source.Format) {
      case "pnach": return Pnach(source, game, out note);
      case "cwcheat": return CwCheat(source, game, out note);
      case "xenia": return Xenia(source, game, out note);
      default: return FileList(source, game, out note);
    }
  }

  static List<PatchEntry> Pnach(PatchSource source, PatchGame game, out string note) {
    var files = source.Files().Where(f => f.EndsWith(".pnach", StringComparison.OrdinalIgnoreCase)).ToList();
    string serial = game.Serial ?? "";
    var exact = files.Where(f => Path.GetFileNameWithoutExtension(f).Equals(serial + "_" + game.Crc, StringComparison.OrdinalIgnoreCase)).ToList();
    var bySerial = files.Where(f => Path.GetFileName(f).StartsWith(serial + "_", StringComparison.OrdinalIgnoreCase)).ToList();
    var byCrc = game.Crc == null ? new List<string>() : files.Where(f => Path.GetFileNameWithoutExtension(f).EndsWith(game.Crc, StringComparison.OrdinalIgnoreCase)).ToList();
    var chosen = exact.Count > 0 ? exact : byCrc.Count > 0 ? byCrc : bySerial;
    if (chosen.Count == 0) { note = L.T($"Nada para {serial} (CRC {game.Crc}) neste repositório.", $"Nothing for {serial} (CRC {game.Crc}) in this repository."); return new List<PatchEntry>(); }
    note = exact.Count > 0 || byCrc.Count > 0 ? L.T("Versão do disco confirmada pelo CRC: ", "Disc version confirmed by CRC: ") + string.Join(", ", chosen.Select(Path.GetFileName))
                                              : L.T("Atenção: arquivos de outra versão do disco (CRC diferente): ", "Careful: files of another disc version (different CRC): ") + string.Join(", ", chosen.Select(Path.GetFileName));
    var list = new List<PatchEntry>();
    foreach (string f in chosen) list.AddRange(ParsePnach(source.Raw(f), f));
    return list;
  }

  public static List<PatchEntry> ParsePnach(string text, string file) {
    var list = new List<PatchEntry>();
    PatchEntry cur = null, top = null;
    foreach (string raw in text.Replace("\r", "").Split('\n')) {
      string line = raw.Trim();
      var sec = Regex.Match(line, @"^\[(.+)\]$");
      if (sec.Success) { cur = new PatchEntry { Title = sec.Groups[1].Value.Replace("\\", " / "), File = file }; list.Add(cur); continue; }
      if (line.StartsWith("description=", StringComparison.OrdinalIgnoreCase)) { if (cur != null) cur.Description = line.Substring(12); continue; }
      if (line.StartsWith("author=", StringComparison.OrdinalIgnoreCase)) { if (cur != null) cur.Author = line.Substring(7); continue; }
      if (line.StartsWith("patch=", StringComparison.OrdinalIgnoreCase)) {
        if (cur == null) { top = top ?? new PatchEntry { Title = L.T("Patches sem seção", "Patches without a section"), File = file }; if (!list.Contains(top)) list.Add(top); top.Lines.Add(line); }
        else cur.Lines.Add(line);
      }
    }
    list.RemoveAll(e => e.Lines.Count == 0);
    return list;
  }

  // The CWCheat database: _S <disc id>, _G <title>, then _C<0|1> <name> followed by its _L lines.
  static List<PatchEntry> CwCheat(PatchSource source, PatchGame game, out string note) {
    string id = (game.Serial ?? "").ToUpperInvariant();
    var list = new List<PatchEntry>();
    bool inGame = false;
    PatchEntry cur = null;
    foreach (string raw in source.Raw(source.Folder).Replace("\r", "").Split('\n')) {
      string line = raw.Trim();
      if (line.StartsWith("_S ")) { inGame = line.Substring(3).Trim().ToUpperInvariant() == id; cur = null; continue; }
      if (!inGame) continue;
      if (line.StartsWith("_C")) {
        string title = line.Length > 4 ? line.Substring(4).Trim() : line;
        cur = new PatchEntry { Title = title, File = source.Folder };
        list.Add(cur);
      } else if (line.StartsWith("_L ") && cur != null) cur.Lines.Add(line);
    }
    // Section headers and notes of the database ("____[>>>General Cheats<<<]____", "↑[#Note: ...]") are not codes.
    list.RemoveAll(e => e.Lines.Count == 0 || e.Title.Contains(">>>") || e.Title.StartsWith("_") || e.Title.StartsWith("↑") || e.Title.StartsWith("#Note"));
    // The database mixes both kinds: FPS, resolution and camera codes go under patches, the rest under cheats.
    var fps = new Regex(@"\b(fps|frame|camera|cam|widescreen|resolution|aspect|bloom|blur)\b", RegexOptions.IgnoreCase);
    list = list.Where(e => fps.IsMatch(e.Title) == (source.Kind == PatchKind.Patch)).ToList();
    note = list.Count == 0 ? L.T($"Nada para {id} nesta base.", $"Nothing for {id} in this database.") : L.T($"{list.Count} códigos para {id}.", $"{list.Count} codes for {id}.");
    return list;
  }

  // Xenia: patches/<title id> - <name>.patch.toml with [[patch]] tables (name, desc, author).
  static List<PatchEntry> Xenia(PatchSource source, PatchGame game, out string note) {
    var files = source.Files().Where(f => f.EndsWith(".patch.toml", StringComparison.OrdinalIgnoreCase)).ToList();
    var chosen = game.TitleId != null ? files.Where(f => Path.GetFileName(f).StartsWith(game.TitleId, StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();
    if (chosen.Count == 0) chosen = ByName(files, game.Name);
    var list = new List<PatchEntry>();
    foreach (string f in chosen) {
      PatchEntry cur = null;
      foreach (string raw in source.Raw(f).Replace("\r", "").Split('\n')) {
        string line = raw.Trim();
        if (line == "[[patch]]") { cur = new PatchEntry { File = f, Supported = false }; list.Add(cur); continue; }
        if (cur == null) continue;
        var kv = Regex.Match(line, "^(name|desc|author)\\s*=\\s*\"(.*)\"$");
        if (kv.Success) {
          if (kv.Groups[1].Value == "name") cur.Title = kv.Groups[2].Value;
          else if (kv.Groups[1].Value == "desc") cur.Description = kv.Groups[2].Value;
          else cur.Author = kv.Groups[2].Value;
        }
        cur.Lines.Add(raw);
      }
    }
    list.RemoveAll(e => string.IsNullOrEmpty(e.Title));
    note = chosen.Count == 0 ? L.T("Nenhum arquivo deste jogo no repositório.", "No file for this game in the repository.") : string.Join(", ", chosen.Select(Path.GetFileName));
    return list;
  }

  // Formats the launcher does not read yet: the game's files, to open on GitHub.
  static List<PatchEntry> FileList(PatchSource source, PatchGame game, out string note) {
    var files = source.Files();
    var chosen = new List<string>();
    if (game.TitleId != null) chosen = files.Where(f => f.IndexOf(game.TitleId, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    if (chosen.Count == 0) chosen = ByName(files, game.Name);
    note = chosen.Count == 0 ? L.T("Nenhum arquivo deste jogo no repositório.", "No file for this game in the repository.")
                             : L.T("Arquivos encontrados (este recompilador ainda não lê este formato).", "Files found (this recompiler does not read this format yet).");
    return chosen.Select(f => new PatchEntry { Title = Path.GetFileName(f), Description = f, File = f, Supported = false }).ToList();
  }

  // Files whose name holds every word of the game's name (3+ letters).
  static List<string> ByName(List<string> files, string name) {
    var words = Regex.Matches((name ?? "").ToLowerInvariant(), @"[a-z0-9]{3,}").Cast<Match>().Select(m => m.Value).Where(w => w != "the").ToList();
    if (words.Count == 0) return new List<string>();
    return files.Where(f => { string n = Path.GetFileName(f).ToLowerInvariant().Replace(" ", ""); return words.All(w => n.Contains(w)); }).Take(20).ToList();
  }

  // ------------------------------------------------------------------ the game's identity

  // PCSX2's ELF CRC: the XOR of every 32-bit word of the boot executable.
  public static string Ps2Crc(byte[] elf) {
    uint crc = 0;
    for (int i = 0; i + 4 <= elf.Length; i += 4) crc ^= BitConverter.ToUInt32(elf, i);
    return crc.ToString("X8");
  }
}

}
