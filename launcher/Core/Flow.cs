// What both launcher windows (Windows Forms on Windows, Avalonia on Linux and macOS) do the same way: the library's
// entries and their states, the texts that explain each state, how the setup and the game are started, the PC
// settings read from and written to the game's files, the dependency checks and the chosen patches.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PortLauncher {

enum EntryState { Ready, HasDisc, NoDisc, NoProfile, Kit, NeedsRecomp, Unsupported, NoRuntime }

// One game of the library. Each window adds its cover bitmap and state colour (partial class).
sealed partial class GameEntry {
  public string Key, Name, Serial, Disc, Dir, Platform, Console;
  public RecompGame Game;          // null: a disc without a profile, or the single game of a kit
  public Recomp Recomp;            // the installed recompiler of the game's console
  public KnownRecomp Known;        // the recompiler of the game's console (installed or not)
  public bool Installed;
  public bool NoProfile;           // the recompiler has no profile but can try its generic path
  public bool NeedsRecomp;         // the console's recompiler is not on this PC yet
  public bool Unsupported;         // the recompiler has neither a profile nor a generic path for it
  public bool NoRuntime;           // Linux / macOS: the recompiler or the game has no version for this system yet
  public bool CoverDone;

  public EntryState State => NoRuntime ? EntryState.NoRuntime : NeedsRecomp ? EntryState.NeedsRecomp : Unsupported ? EntryState.Unsupported : NoProfile ? EntryState.NoProfile : Installed ? EntryState.Ready : Disc != null ? EntryState.HasDisc : Dir == null ? EntryState.Kit : EntryState.NoDisc;
  public string StateText {
    get {
      switch (State) {
        case EntryState.Ready: return L.T("Pronto para jogar", "Ready to play");
        case EntryState.HasDisc: return L.T("Disco encontrado", "Disc found");
        case EntryState.NoProfile: return L.T("Experimental", "Experimental");
        case EntryState.NeedsRecomp: return L.T("Baixar recompilador", "Get recompiler");
        case EntryState.Unsupported: return L.T("Ainda sem suporte", "Not supported yet");
        case EntryState.NoRuntime: return L.T("Sem versão para ", "No version for ") + OS.Name;
        case EntryState.Kit: return L.T("Não instalado", "Not installed");
        default: return L.T("Sem disco", "No disc");
      }
    }
  }
}

static class Entries {
  // Library mode: the discs found in the player's folders (one entry per game), plus games already built from an
  // earlier disc; each with its console, its recompiler and what it takes to play it. Nothing else is listed.
  // current/currentCfg: the selected game and its configuration (its installed state is read from that config).
  public static List<GameEntry> Library(RecompRegistry registry, List<FoundDisc> found, RecompGame current, GameConfig currentCfg) {
    var entries = new List<GameEntry>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in registry.Installed) {
      var rc = kv.Value;
      string console = registry.Known.FirstOrDefault(k => k.Id == kv.Key)?.Console;
      foreach (var g in rc.Games) {
        var disc = found.FirstOrDefault(f => f.Game == g);
        bool installed = false;
        try { var c = g == current ? currentCfg : rc.ConfigFor(g); installed = c.Exe != null && File.Exists(Path.Combine(g.Dir, c.Exe)); } catch (Exception) { }
        if (disc == null && !installed) continue;
        string key = kv.Key + "/" + g.Id;
        seen.Add(key);
        string serial = Covers.Serial(disc?.Label) ?? g.Executables.Select(Covers.Serial).FirstOrDefault(x => x != null);
        entries.Add(new GameEntry { Key = key, Game = g, Recomp = rc, Known = registry.KnownFor(rc), Console = console, Name = g.Name,
                                    Serial = serial ?? Short(g.Disc), Disc = disc?.Path, Dir = g.Dir, Installed = installed && g.Supported,
                                    Platform = Consoles.Short(console), NoRuntime = !g.Supported });
      }
    }
    foreach (var f in found.Where(f => f.Game == null)) {
      string key = "disc:" + f.Console + ":" + (f.Label ?? f.Path);
      if (!seen.Add(key)) continue;
      string name = f.IsFolder ? new DirectoryInfo(f.Path).Name : Path.GetFileNameWithoutExtension(f.Path);
      var known = registry.ForConsole(f.Console);
      bool generic = f.Recomp != null && f.Recomp.Generic != null && f.Label != null && Regex.IsMatch(f.Label, f.Recomp.LabelRegex, RegexOptions.IgnoreCase);
      bool noRuntime = f.Recomp != null ? !f.Recomp.Supported : known != null && !known.Supported;
      entries.Add(new GameEntry { Key = key, Name = name, Serial = Covers.Serial(f.Label) ?? f.Label, Disc = f.Path, Console = f.Console,
                                  Recomp = f.Recomp, Known = known, NeedsRecomp = f.Recomp == null, NoProfile = generic,
                                  Unsupported = f.Recomp != null && !generic, Platform = Consoles.Short(f.Console), NoRuntime = noRuntime });
    }
    return entries;
  }

  // "PlayStation 2 (SLUS-21423)" -> "SLUS-21423".
  public static string Short(string disc) {
    if (string.IsNullOrEmpty(disc)) return null;
    var m = Regex.Match(disc, @"\(([^)]+)\)");
    return m.Success ? m.Groups[1].Value : null;
  }
}

// The texts of the game panel and the questions before a setup.
static class Texts {
  public static string Primary(GameEntry e, bool lib) {
    if (e.NoRuntime) return L.T("SEM VERSÃO PARA ", "NO VERSION FOR ") + OS.Name.ToUpperInvariant();
    if (e.NeedsRecomp) return e.Known != null ? L.T("BAIXAR ", "GET ") + e.Known.Folder.ToUpperInvariant() : L.T("SEM RECOMPILADOR", "NO RECOMPILER");
    if (e.Unsupported) return L.T("AINDA SEM SUPORTE", "NOT SUPPORTED YET");
    if (e.NoProfile) return L.T("MONTAR (GENÉRICO)", "BUILD (GENERIC)");
    switch (e.State) {
      case EntryState.Ready: return L.T("JOGAR", "PLAY");
      case EntryState.HasDisc: return L.T("INSTALAR E MONTAR", "INSTALL AND BUILD");
      case EntryState.Kit: return L.T("INSTALAR", "INSTALL");
      default: return lib ? L.T("ESCOLHER O DISCO...", "CHOOSE THE DISC...") : L.T("INSTALAR", "INSTALL");
    }
  }

  public static string NoRuntimeNote(GameEntry e) {
    string who = e.Recomp?.Name ?? e.Known?.Name ?? Consoles.Name(e.Console);
    bool recompHasIt = e.Recomp != null && e.Recomp.Supported;
    return recompHasIt
        ? L.T($"Este jogo ainda não tem versão para {OS.Name} no {who} (só para Windows). Os outros jogos do {who} que têm aparecem prontos para montar.",
              $"This game has no {OS.Name} version in {who} yet (Windows only). {who}'s games that have one show up ready to build.")
        : L.T($"O {who} ainda não tem versão para {OS.Name}: os jogos dele por enquanto só montam e rodam no Windows. Quando tiver, uma atualização do recompilador traz.",
              $"{who} has no {OS.Name} version yet: its games only build and run on Windows for now. When it gets one, a recompiler update brings it.");
  }

  public static string DetailNote(GameEntry e, GameConfig cfg) {
    if (e.NoRuntime) return NoRuntimeNote(e);
    if (e.NeedsRecomp)
      return e.Known != null
          ? L.T($"Jogos de {Consoles.Name(e.Console)} usam o {e.Known.Name}, que ainda não está neste PC. O launcher baixa o código dele do GitHub (nada de nenhum jogo) e depois monta o jogo a partir do seu disco.",
                $"{Consoles.Name(e.Console)} games use {e.Known.Name}, which is not on this PC yet. The launcher downloads its source from GitHub (nothing from any game), then builds the game from your disc.")
          : L.T("Ainda não há recompilador para este console.", "There is no recompiler for this console yet.");
    if (e.Unsupported)
      return L.T($"O {e.Recomp?.Name} ainda não tem este jogo. Atualizações do recompilador podem trazer suporte (menu Atualizações).",
                 $"{e.Recomp?.Name} does not have this game yet. Recompiler updates may add it (Updates menu).");
    if (e.NoProfile)
      return L.T("Este disco não tem perfil neste recompilador. O caminho genérico tenta montar qualquer jogo; pode não funcionar ou parar em algum ponto.",
                 "This disc has no profile in this recompiler. The generic path tries to build any game; it may not work or may stop somewhere.");
    switch (e.State) {
      case EntryState.Ready: return cfg.PcMenu
          ? L.T("No jogo, F1 (ou Back+Start no controle) abre as mesmas configurações do PC.", "In the game, F1 (or Back+Start on a controller) opens the same PC settings.")
          : L.T("As configurações de PC ficam em Configurar. No jogo: F10 desempenho, F11 tela cheia, F12 captura de tela.", "The PC settings are under Configure. In the game: F10 performance, F11 fullscreen, F12 screenshot.");
      case EntryState.HasDisc: return L.T("O disco foi encontrado nas suas pastas. A montagem instala o que faltar, copia e confere os arquivos e compila o jogo; as etapas prontas são puladas depois.",
                                          "The disc was found in your folders. The build installs what is missing, copies and checks the files and compiles the game; finished steps are skipped later.");
      case EntryState.Kit: return L.T($"O instalador baixa o kit do port (código-fonte), instala as ferramentas, pede o seu disco ({cfg.Disc}) e monta a versão para PC.",
                                      $"The installer downloads the port kit (source code), installs the tools, asks for your disc ({cfg.Disc}) and builds the PC version.");
      default: return L.T($"Nenhum disco deste jogo nas suas pastas{(GameFolders.Load().Count == 0 ? " (nenhuma pasta adicionada)" : "")}. Escolha a ISO ou adicione a pasta onde ela está.",
                          $"No disc of this game in your folders{(GameFolders.Load().Count == 0 ? " (no folder added)" : "")}. Choose the ISO or add the folder that holds it.");
    }
  }

  // What the setup is going to do (the question before it starts).
  public static string InstallPlan(GameConfig cfg, bool kitMissing, string source, string target) {
    string system = OS.Windows
        ? L.T("•  instalar o Python e o Microsoft C++ Build Tools se faltarem (o Build Tools tem cerca de 7 GB e pede permissão de administrador)\n",
              "•  install Python and the Microsoft C++ Build Tools if missing (the Build Tools are about 7 GB and ask for administrator permission)\n")
        : OS.Mac
        ? L.T("•  conferir o Python 3.11+ e as Command Line Tools da Apple (o compilador; o macOS pede para instalar se faltarem)\n",
              "•  check Python 3.11+ and Apple's Command Line Tools (the compiler; macOS offers to install them if missing)\n")
        : L.T("•  instalar pelo gerenciador de pacotes do sistema o que faltar (Python, compilador C/C++, bibliotecas; pede a sua senha)\n",
              "•  install what is missing with the system's package manager (Python, C/C++ compiler, libraries; asks for your password)\n");
    return L.T(
        "O instalador vai:\n\n" +
        (kitMissing ? "•  baixar o kit do port do GitHub (só código-fonte, cerca de 1 MB)\n" : "") +
        (cfg.BuildToolsGb > 0 || !OS.Windows ? system : "") +
        "•  baixar as ferramentas de compilação que ainda não estiverem na pasta\n" +
        (source != null ? $"•  copiar os arquivos do jogo de {source}\n" : $"•  pedir o seu disco ({cfg.Disc}) ou a imagem .iso\n") +
        $"•  montar a versão para PC (cerca de {cfg.Minutes} minutos)\n\n" +
        $"Pasta: {target}",
        "The installer will:\n\n" +
        (kitMissing ? "•  download the port kit from GitHub (source code only, about 1 MB)\n" : "") +
        (cfg.BuildToolsGb > 0 || !OS.Windows ? system : "") +
        "•  download the build tools not in the folder yet\n" +
        (source != null ? $"•  copy the game files from {source}\n" : $"•  ask for your disc ({cfg.Disc}) or its .iso image\n") +
        $"•  build the PC version (about {cfg.Minutes} minutes)\n\n" +
        $"Folder: {target}");
  }

  // Where a game is installed when the player does not say: <the drive with most free space>\Games\<repo> on
  // Windows, ~/Games/<repo> on Linux and macOS.
  public static string DefaultGamesFolder() {
    if (!OS.Windows) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
    var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
    return Path.Combine(best?.RootDirectory.FullName ?? "C:\\", "Games");
  }
}

// Starting the setup script and the game.
static class Runner {
  // The setup of one game (recompiler mode: the shared script with -GameDir; a kit: its own tools/setup.ps1|sh).
  public static ProcessStartInfo Setup(Recomp recomp, GameConfig cfg, string root, string only, string source, Action<string> log) {
    string script, args;
    bool sh = !OS.Windows;
    if (recomp != null) {
      recomp.EnsureInstallerFiles(log);
      script = recomp.Setup;
      args = sh ? $"--yes --no-shortcut --game-dir {OS.Quote(root)} --tools-dir {OS.Quote(recomp.ToolsDir)}"
                : $"-Yes -NoShortcut -GameDir \"{root}\" -ToolsDir \"{recomp.ToolsDir}\"";
    } else {
      script = Path.Combine(root, cfg.Setup);
      args = sh ? "--yes --no-shortcut" : "-Yes -NoShortcut";
    }
    if (only != null) args += (sh ? " --only " : " -Only ") + only;
    if (source != null && (only == null || only == "game")) args += sh ? " --source " + OS.Quote(source) : $" -Source \"{source}\"";
    var psi = OS.ScriptStart(script, args, root);
    psi.EnvironmentVariables["PORT_LAUNCHER"] = "1";
    if (!OS.Windows) {
      psi.StandardOutputEncoding = psi.StandardErrorEncoding = new UTF8Encoding(false);
      psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8:replace";
      psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
      psi.EnvironmentVariables["LANG"] = L.Pt ? "pt_BR.UTF-8" : "en_US.UTF-8";   // the script speaks the launcher's language
    }
    return psi;
  }

  // The game's start script (Play.cmd on Windows, Play.sh on Linux / macOS).
  public static ProcessStartInfo Play(GameConfig cfg, string root) => OS.ScriptStart(Path.Combine(root, cfg.Play), "", root);

  // The setup's step lines: "== 2/5 Python".
  public static readonly Regex StepLine = new Regex(@"^== (\d)/(\d) (.*)$");
}

// The PC settings: each value read from and written to wherever the game keeps it (see Setting.Store / Section /
// File / Div and Choice.Set).
static class SettingsStore {
  public static readonly HashSet<string> OffWords = new HashSet<string> { "Desligada", "Desligado", "Não", "Esconder", "Off", "No", "Hide" };

  public static bool IsToggle(Setting s) =>
      s.Source == null && s.Choices.Count == 2 && s.Choices.Any(c => c.Value == 0 && OffWords.Contains(c.Pt)) && s.Choices.Any(c => c.Value == 1);

  public static bool Visible(Setting s, GameConfig cfg, string root) =>
      s.Requires != "vulkan" || (root != null && cfg.VulkanDir != null && Directory.Exists(Path.Combine(root, cfg.VulkanDir)));

  public static int Read(Setting s, GameConfig cfg, string root, string gameExe, string ini, Dictionary<string, string> plain) {
    string section = s.Section ?? cfg.Section;
    Func<string, string> text = key => plain != null ? (plain.TryGetValue(key, out var v) ? v : "") : Ini.GetText(ini, section, key);
    Func<string, int, int> number = (key, def) => int.TryParse(text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : def;
    switch (s.Store) {
      case "gpu_pref":
        return gameExe != null ? GpuPreference.Get(Path.GetFullPath(gameExe)) : s.Default;
      case "word": {
        string path = Path.Combine(root, s.File);
        string word = File.Exists(path) ? File.ReadAllText(path).Trim().Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
        var hit = s.Choices.FirstOrDefault(c => c.Word == word);
        return hit != null ? hit.Value : s.Default;
      }
    }
    if (s.Choices.Count > 0 && s.Choices[0].Set.Count > 0) {
      var hit = s.Choices.FirstOrDefault(c => c.Set.All(kv => number(kv.Key, int.MinValue) == kv.Value));
      return hit != null ? hit.Value : s.Default;
    }
    if (s.Div > 0)
      return double.TryParse(text(s.Key), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (int)Math.Round(d * s.Div) : s.Default;
    return number(s.Key, s.Default);
  }

  public static void Write(Setting s, int value, GameConfig cfg, string root, string gameExe, string ini, Dictionary<string, string> plain) {
    string section = s.Section ?? cfg.Section;
    Action<string, string> put = (key, v) => { if (plain != null) plain[key] = v; else Ini.PutText(ini, section, key, v); };
    switch (s.Store) {
      case "gpu_pref":
        if (gameExe != null) GpuPreference.Set(Path.GetFullPath(gameExe), value);
        return;
      case "word": {
        var hit = s.Choices.FirstOrDefault(c => c.Value == value);
        if (hit?.Word != null) File.WriteAllText(Path.Combine(root, s.File), hit.Word + "\n");
        return;
      }
    }
    var choice = s.Choices.FirstOrDefault(c => c.Value == value);
    if (choice != null && choice.Set.Count > 0) {
      foreach (var kv in choice.Set) put(kv.Key, kv.Value.ToString(CultureInfo.InvariantCulture));
      return;
    }
    put(s.Key, s.Div > 0 ? (value / (double)s.Div).ToString("0.00", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture));
  }
}

// The Dependencies window's rows and how each is checked on this system.
sealed class Dep {
  public string Name, Detail, Only;   // Only: the setup's --only part that installs it (null: full setup)
  public double Gb;
  public bool OnSystemDrive;          // installs on the system drive whatever the game folder is
  public bool? Ok;                    // null while checking
  public string Found;                // where it was found / why it is missing
}

static class DepsCheck {
  public static List<Dep> List(GameConfig cfg) {
    var list = new List<Dep> {
      new Dep { Name = "Python 3.11+", Detail = L.T("roda os scripts de montagem e o instalador dos arquivos do jogo", "runs the build scripts and the game file installer"),
                Only = "python", Gb = cfg.PythonGb, OnSystemDrive = true },
    };
    if (OS.Windows) {
      // A recompiler that compiles nothing (AnyPS5: deps "buildtools_gb": 0) does not need the C++ Build Tools.
      if (cfg.BuildToolsGb > 0)
        list.Add(new Dep { Name = "Microsoft C++ Build Tools", Detail = L.T("bibliotecas C++ e o Windows SDK (pede administrador)", "C++ libraries and the Windows SDK (asks for administrator)"),
                           Only = "buildtools", Gb = cfg.BuildToolsGb, OnSystemDrive = true });
    } else if (OS.Mac) {
      list.Add(new Dep { Name = "Command Line Tools (Xcode)", Detail = L.T("o compilador da Apple (clang) e o SDK do macOS", "Apple's compiler (clang) and the macOS SDK"),
                         Only = "buildtools", Gb = 1.5, OnSystemDrive = true });
    } else {
      list.Add(new Dep { Name = L.T("Compilador C/C++ e bibliotecas", "C/C++ compiler and libraries"),
                         Detail = L.T("do gerenciador de pacotes do sistema (pede a sua senha)", "from the system's package manager (asks for your password)"),
                         Only = "buildtools", Gb = 0.5, OnSystemDrive = true });
    }
    string sep = Path.DirectorySeparatorChar.ToString();
    foreach (var t in cfg.Tools)
      list.Add(new Dep { Name = t.Name, Only = "tools", Gb = t.Gb, Detail = !OS.Windows && (t.Id == "llvm" || t.Id == "llvm-mingw")
          ? L.T("o compilador do sistema (instalado com os pacotes acima)", "the system's compiler (installed with the packages above)")
          : cfg.ToolsRoot != null
          ? L.T($"fica dentro do recompilador (.local{sep}tools), usado por todos os jogos", $"kept inside the recompiler (.local{sep}tools), shared by every game")
          : L.T($"fica dentro da pasta do jogo (.local{sep}tools)", $"kept inside the game folder (.local{sep}tools)") });
    list.Add(new Dep { Name = L.T("Seus arquivos do jogo", "Your game files"), Detail = cfg.Disc + L.T(" (do seu disco ou .iso)", " (from your disc or .iso)"),
                       Only = "game", Gb = 0 });
    list.Add(new Dep { Name = L.T("Versão para PC (montada)", "PC version (built)"), Detail = L.T($"tradução e compilação, cerca de {cfg.Minutes} min", $"translation and compile, about {cfg.Minutes} min"),
                       Only = null, Gb = cfg.DiskGb });
    return list;
  }

  // Checks every row (slow: runs programs and hashes the game files; call it off the UI thread).
  public static List<KeyValuePair<bool, string>> Run(GameConfig cfg, string kit, bool installed, string exeName) {
    var r = new List<KeyValuePair<bool, string>>();
    r.Add(FindPython(cfg.ToolsRoot));
    if (OS.Windows) { if (cfg.BuildToolsGb > 0) r.Add(FindBuildTools()); }
    else r.Add(FindCompiler());
    string systemLlvm = SystemLlvm();
    foreach (var t in cfg.Tools) {
      if ((t.Id == "llvm" || (!OS.Windows && t.Id == "llvm-mingw")) && systemLlvm != null) { r.Add(new KeyValuePair<bool, string>(true, systemLlvm)); continue; }
      string toolsRoot = cfg.ToolsRoot ?? kit;
      bool ok = toolsRoot != null && t.Check != null && File.Exists(Path.Combine(toolsRoot, t.Check));
      r.Add(new KeyValuePair<bool, string>(ok, ok ? (cfg.ToolsRoot != null ? L.T("no recompilador", "in the recompiler folder") : L.T("na pasta do jogo", "in the game folder")) : ""));
    }
    r.Add(GameFilesOk(cfg, kit));
    r.Add(new KeyValuePair<bool, string>(installed, installed ? exeName : ""));
    return r;
  }

  public static string RunOut(string exe, string args) {
    try {
      var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      using (var p = Process.Start(psi)) {
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (Exception) { } return null; }
        return p.ExitCode == 0 ? output.Trim() : null;
      }
    } catch (Exception) { return null; }
  }

  public static KeyValuePair<bool, string> FindPython(string recompRoot) {
    const string probe = "-c \"import sys, tkinter; assert sys.version_info >= (3, 11); print('%d.%d' % sys.version_info[:2])\"";
    if (!OS.Windows) {
      // As setup.sh looks for it (python3 first; macOS also Homebrew and python.org).
      var names = new List<string> { "python3", "python3.13", "python3.12", "python3.11" };
      if (OS.Mac) names.AddRange(new[] { "/opt/homebrew/bin/python3", "/usr/local/bin/python3" });
      foreach (string c in names) {
        string v = RunOut(c, probe);
        if (!string.IsNullOrEmpty(v)) return new KeyValuePair<bool, string>(true, "Python " + v);
      }
      return new KeyValuePair<bool, string>(false, L.T("precisa do 3.11+ com tkinter", "needs 3.11+ with tkinter"));
    }
    // The Python that comes inside the launcher ZIP.
    if (recompRoot != null && File.Exists(Path.Combine(recompRoot, ".local", "python", "python.exe")))
      return new KeyValuePair<bool, string>(true, L.T("Python (incluído)", "Python (included)"));
    foreach (var c in new[] { new[] { "py", "-3 " }, new[] { "python", "-B " } }) {
      string v = RunOut(c[0], c[1] + probe);
      if (!string.IsNullOrEmpty(v)) return new KeyValuePair<bool, string>(true, "Python " + v);
    }
    string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe");
    if (File.Exists(local)) return new KeyValuePair<bool, string>(true, "Python 3.12");
    return new KeyValuePair<bool, string>(false, "");
  }

  public static KeyValuePair<bool, string> FindBuildTools() {
    string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    string vswhere = Path.Combine(x86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
    bool sdk = Directory.Exists(Path.Combine(x86, "Windows Kits", "10", "Include"));
    string vs = File.Exists(vswhere)
        ? RunOut(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property displayName") : null;
    if (!string.IsNullOrEmpty(vs) && sdk) return new KeyValuePair<bool, string>(true, vs.Split('\n')[0].Trim());
    if (!string.IsNullOrEmpty(vs)) return new KeyValuePair<bool, string>(false, L.T("falta o Windows SDK", "Windows SDK missing"));
    return new KeyValuePair<bool, string>(false, "");
  }

  // Linux / macOS: a C++ compiler (macOS: from the Command Line Tools, which xcode-select reports).
  public static KeyValuePair<bool, string> FindCompiler() {
    if (OS.Mac && RunOut("xcode-select", "-p") == null) return new KeyValuePair<bool, string>(false, L.T("rode a instalação pelo botão", "install it with the button"));
    foreach (string cxx in new[] { "clang++", "g++", "c++" }) {
      string v = RunOut(cxx, "--version");
      if (!string.IsNullOrEmpty(v)) return new KeyValuePair<bool, string>(true, v.Split('\n')[0].Trim());
    }
    return new KeyValuePair<bool, string>(false, "");
  }

  // A complete Clang 20+ already installed is used instead of the download (setup_tools.py); Linux / macOS always
  // use the system's clang.
  public static string SystemLlvm() {
    if (!OS.Windows) {
      string out_ = RunOut("clang++", "--version");
      var mm = out_ == null ? null : Regex.Match(out_, @"clang version (\d+)");
      return mm != null && mm.Success ? "Clang " + mm.Groups[1].Value + L.T(" do sistema", " (system)") : null;
    }
    string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin");
    string[] needed = { "clang.exe", "clang++.exe", "clang-cl.exe", "lld-link.exe", "llvm-ar.exe", "llvm-rc.exe" };
    if (!needed.All(n => File.Exists(Path.Combine(bin, n)))) return null;
    string v = RunOut(Path.Combine(bin, "clang++.exe"), "--version");
    var m = v == null ? null : Regex.Match(v, @"clang version (\d+)");
    return m != null && m.Success && int.Parse(m.Groups[1].Value) >= 20 ? "Clang " + m.Groups[1].Value + L.T(" do sistema", " (system)") : null;
  }

  public static KeyValuePair<bool, string> GameFilesOk(GameConfig cfg, string kit) {
    if (kit == null || cfg.GameFiles.Count == 0) return new KeyValuePair<bool, string>(false, "");
    foreach (var kv in cfg.GameFiles) {
      string path = Path.Combine(kit, kv.Key);
      if (!File.Exists(path)) return new KeyValuePair<bool, string>(false, "");
      using (var sha = SHA256.Create())
      using (var f = File.OpenRead(path)) {
        string hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
        if (!string.Equals(hash, kv.Value, StringComparison.OrdinalIgnoreCase))
          return new KeyValuePair<bool, string>(false, L.T("versão diferente", "different version"));
      }
    }
    return new KeyValuePair<bool, string>(true, L.T("versão conferida", "version verified"));
  }

  public static double FreeGb(string path) {
    try { return new DriveInfo(path).AvailableFreeSpace / (1024.0 * 1024 * 1024); } catch (Exception) { return 0; }
  }
}

// The patches and cheats the player chose for a game: remembered per game, and written where the recompiler reads them.
static class PatchChoices {
  public static string File(PatchGame game, PatchKind kind) =>
      Path.Combine(Prefs.Dir, "choices", Regex.Replace(game.Console + "-" + (game.Serial ?? game.Name ?? "game"), @"[^\w.-]", "_") + "-" + kind + ".txt");

  public static Dictionary<string, HashSet<string>> Load(PatchGame game, PatchKind kind) {
    var chosen = new Dictionary<string, HashSet<string>>();
    try {
      string f = File(game, kind);
      if (!System.IO.File.Exists(f)) return chosen;
      foreach (string line in System.IO.File.ReadAllLines(f)) {
        var p = line.Split('\t');
        if (p.Length != 2) continue;
        if (!chosen.TryGetValue(p[0], out var set)) chosen[p[0]] = set = new HashSet<string>();
        set.Add(p[1]);
      }
    } catch (Exception) { }
    return chosen;
  }

  // Saves the choice and writes the chosen entries next to the game (PS2: patches.pnach / cheats.pnach; PS5: the JSON
  // patches in <game>/patches). loaded: the entries already read for one source (others are read again).
  public static void Apply(PatchGame game, PatchKind kind, string gameDir, List<PatchSource> sources, Dictionary<string, HashSet<string>> chosen,
                           PatchSource loadedSource, List<PatchEntry> loaded, string what) {
    string choiceFile = File(game, kind);
    Directory.CreateDirectory(Path.GetDirectoryName(choiceFile));
    System.IO.File.WriteAllLines(choiceFile, chosen.SelectMany(kv => kv.Value.Select(t => kv.Key + "\t" + t)));
    var sb = new StringBuilder();
    sb.AppendLine("// " + what + L.T(" escolhidos no launcher para ", " chosen in the launcher for ") + game.Name + " (" + game.Serial + (game.Crc != null ? " " + game.Crc : "") + "). " +
                  L.T("O launcher reescreve este arquivo.", "The launcher rewrites this file."));
    int count = 0;
    var json = new List<KeyValuePair<string, string>>();   // PS5: file name -> the patch's JSON text
    foreach (var src in sources) {
      if (!chosen.TryGetValue(src.Key, out var on) || on.Count == 0) continue;
      List<PatchEntry> all;
      try { all = src == loadedSource ? loaded : PatchLibrary.Load(src, game, out _); } catch (Exception) { continue; }
      foreach (var e in all.Where(x => on.Contains(x.Title))) {
        sb.AppendLine();
        sb.AppendLine("[" + e.Title + "]");
        sb.AppendLine("// " + src.Repo + "/" + e.File);
        foreach (string l in e.Lines) sb.AppendLine(l);
        ++count;
        if (game.Console == "ps5" && e.Supported && e.File.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
          json.Add(new KeyValuePair<string, string>(Regex.Replace(src.Repo, @"[^\w.-]", "_") + "-" + Path.GetFileName(e.File), string.Join("\n", e.Lines)));
      }
    }
    string file = Path.Combine(gameDir, game.Console == "ps2" ? (kind == PatchKind.Patch ? "patches.pnach" : "cheats.pnach")
                                                             : (kind == PatchKind.Patch ? "patches-chosen.txt" : "cheats-chosen.txt"));
    if (count == 0) { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); }
    else System.IO.File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
    if (game.Console == "ps5" && kind == PatchKind.Patch) {
      // <game>/patches holds exactly the chosen patches: tools/ps5port applies them at play and undoes the rest.
      string dir = Path.Combine(gameDir, "patches");
      if (Directory.Exists(dir)) foreach (string old in Directory.GetFiles(dir, "*.json")) System.IO.File.Delete(old);
      if (json.Count > 0) Directory.CreateDirectory(dir);
      foreach (var kv in json) System.IO.File.WriteAllText(Path.Combine(dir, kv.Key), kv.Value, new UTF8Encoding(false));
    }
  }

  // PCSX2 names its patch files <serial>_<CRC of the boot executable>: the game as the patch repositories know it.
  public static PatchGame GameOf(GameEntry e) {
    var g = new PatchGame { Console = e.Console, Serial = e.Serial, Name = e.Name };
    if (e.Console == "ps5") g.TitleId = e.Serial;
    if (e.Console == "ps2" && e.Game != null) {
      string boot = e.Game.Executables.FirstOrDefault(x => Covers.Serial(x) != null);
      if (boot != null) {
        try {
          string installed = Path.Combine(e.Game.Dir, "game_files", boot);
          byte[] elf = System.IO.File.Exists(installed) ? System.IO.File.ReadAllBytes(installed) : e.Disc != null ? Recomp.ReadDiscFile(e.Disc, boot) : null;
          if (elf != null) g.Crc = PatchLibrary.Ps2Crc(elf);
          g.Serial = g.Serial ?? Covers.Serial(boot);
        } catch (Exception) { }
      }
    }
    return g;
  }

  // tools/ps2port hotpatch for a game (retranslates only the patched functions); null when the recompiler lacks it.
  public static ProcessStartInfo Hotpatch(GameEntry e) {
    var r = e.Recomp;
    string script = Path.Combine(r.Root, "tools", "ps2port", "ps2port.py");
    if (!System.IO.File.Exists(script)) return null;
    string python = OS.Windows ? Path.Combine(r.Root, ".local", "python", "python.exe") : "python3";
    if (OS.Windows && !System.IO.File.Exists(python)) python = "python";
    var psi = new ProcessStartInfo(python, $"\"{script}\" hotpatch --port \"{e.Game.Dir}\"") {
      WorkingDirectory = r.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    // llvm-mingw from the recompiler's tools (the ZIP's compiler); with none, ps2port finds Visual Studio itself.
    // Linux / macOS: the system's clang.
    string mingw = OS.Windows && Directory.Exists(r.ToolsDir) ? Directory.GetDirectories(r.ToolsDir, "llvm-mingw-*").FirstOrDefault() : null;
    if (mingw != null) {
      psi.EnvironmentVariables["PATH"] = Path.Combine(mingw, "bin") + ";" + psi.EnvironmentVariables["PATH"];
      psi.EnvironmentVariables["CC"] = "clang";
      psi.EnvironmentVariables["CXX"] = "clang++";
    } else if (!OS.Windows) {
      psi.EnvironmentVariables["CC"] = "clang";
      psi.EnvironmentVariables["CXX"] = "clang++";
    }
    return psi;
  }
}

}
