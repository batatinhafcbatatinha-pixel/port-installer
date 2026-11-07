// Updates, the way emulators do them: two channels.
//   Stable  - the newest release (a v* tag) of port-installer.
//   Nightly - the build of the newest commit (CI keeps it in the "nightly" pre-release, with "commit: <sha>" in its notes).
// The recompiler's own source follows the same choice: its newest release when it has one (stable), else / or
// the newest commit of its branch (nightly). A git clone is updated with git pull; a ZIP download is downloaded again.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PortLauncher {

enum UpdateChannel { Stable, Nightly }

sealed class LauncherRelease {
  public string Tag, Url, Commit, Date;
  public Version Version;
  public string Display => Tag == "nightly" ? "nightly " + (Commit ?? "").Substring(0, Math.Min(7, (Commit ?? "").Length)) : Tag;
}

static class Updater {
  public const string LauncherRepo = "batatinhafcbatatinha-pixel/port-installer";

  public static UpdateChannel Channel {
    get => Prefs.Text("update_channel", "stable") == "nightly" ? UpdateChannel.Nightly : UpdateChannel.Stable;
    set => Prefs.Set("update_channel", value == UpdateChannel.Nightly ? "nightly" : "stable");
  }

  // This build: its version (0.0.0 = a local build) and commit (CI sets SourceRevisionId).
  public static Version Version {
    get { var v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)); }
  }
  public static string Commit {
    get {
      var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
      int plus = info.IndexOf('+');
      return plus >= 0 ? info.Substring(plus + 1) : null;
    }
  }
  public static bool IsLocalBuild => Version.Major == 0 && Version.Minor == 0 && Version.Build == 0;
  public static bool IsNightlyBuild => Version.Revision > 0;
  public static string Describe() {
    if (IsLocalBuild) return L.T("compilação local", "local build") + (Commit != null ? " " + Short(Commit) : "");
    string v = Version.Major + "." + Version.Minor + "." + Version.Build;
    return IsNightlyBuild ? "nightly " + v + "-" + Version.Revision + (Commit != null ? " (" + Short(Commit) + ")" : "") : "v" + v;
  }
  static string Short(string sha) => sha.Length > 7 ? sha.Substring(0, 7) : sha;

  // A launcher with its game's launcher.json built in (a single-game kit) is not the generic release: it is not replaced.
  public static bool CanReplaceSelf => Assembly.GetExecutingAssembly().GetManifestResourceStream("launcher.json") == null;

  static WebClient Web() {
    var web = new WebClient();
    web.Headers[HttpRequestHeader.UserAgent] = "port-launcher";
    web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
    return web;
  }

  static IDictionary<string, object> Api(string path) {
    using (var web = Web())
      return new JavaScriptSerializer().DeserializeObject(web.DownloadString("https://api.github.com/" + path)) as IDictionary<string, object>;
  }

  static string S(IDictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v) : null;

  // The launcher the channel offers (null when it has none yet).
  public static LauncherRelease Latest(UpdateChannel channel) {
    IDictionary<string, object> rel;
    try { rel = Api("repos/" + LauncherRepo + (channel == UpdateChannel.Nightly ? "/releases/tags/nightly" : "/releases/latest")); }
    catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound) { return null; }
    var r = new LauncherRelease { Tag = S(rel, "tag_name"), Date = S(rel, "published_at") };
    if (rel.TryGetValue("assets", out var assets) && assets is object[] list)
      foreach (var a in list.OfType<IDictionary<string, object>>())
        if (string.Equals(S(a, "name"), "Launcher.exe", StringComparison.OrdinalIgnoreCase)) r.Url = S(a, "browser_download_url");
    var m = Regex.Match(S(rel, "body") ?? "", @"commit:\s*([0-9a-f]{7,40})", RegexOptions.IgnoreCase);
    if (m.Success) r.Commit = m.Groups[1].Value.ToLowerInvariant();
    m = Regex.Match(S(rel, "body") ?? "", @"version:\s*(\d+\.\d+\.\d+(\.\d+)?)", RegexOptions.IgnoreCase);
    if (channel == UpdateChannel.Stable) m = Regex.Match(r.Tag ?? "", @"(\d+\.\d+\.\d+)");
    if (m.Success) { var v = new Version(m.Groups[1].Value); r.Version = new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)); }
    return r.Url == null ? null : r;
  }

  // Whether the channel's launcher is another build than this one (and the one to switch to).
  public static bool IsNewer(LauncherRelease r, UpdateChannel channel) {
    if (r == null) return false;
    if (channel == UpdateChannel.Nightly) {
      if (r.Commit == null) return false;
      return Commit == null || !r.Commit.StartsWith(Commit, StringComparison.OrdinalIgnoreCase) && !Commit.StartsWith(r.Commit, StringComparison.OrdinalIgnoreCase);
    }
    return r.Version != null && r.Version > Version;
  }

  // Downloads the new Launcher.exe next to this one and swaps them (a running exe can be renamed, not overwritten).
  public static void ReplaceSelf(LauncherRelease r, Action<string> log) {
    string exe = Application.ExecutablePath, next = exe + ".new", old = exe + ".old";
    log?.Invoke(L.T("Baixando ", "Downloading ") + r.Url);
    using (var web = Web()) { web.Headers.Remove(HttpRequestHeader.Accept); web.DownloadFile(r.Url, next); }
    var head = new byte[2];
    using (var f = File.OpenRead(next)) {
      if (f.Length < 64 * 1024 || f.Read(head, 0, 2) != 2 || head[0] != 'M' || head[1] != 'Z') { f.Close(); File.Delete(next); throw new IOException("Launcher.exe: not a Windows program"); }
    }
    if (File.Exists(old)) File.Delete(old);
    File.Move(exe, old);
    try { File.Move(next, exe); } catch (Exception) { File.Move(old, exe); throw; }
    log?.Invoke("OK");
  }

  public static void Relaunch() {
    var args = Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a);
    Process.Start(new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", args)) { UseShellExecute = false });
  }

  public static void CleanUp() {
    try { string old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch (Exception) { }
  }

  // ------------------------------------------------------------------ the recompiler's source

  public static string RemoteHead(string repo, string branch) => S(Api($"repos/{repo}/commits/{branch}"), "sha");

  // The commit the channel points at: the newest release's tag (stable, when the repository has releases) or the branch.
  public static string RecompTarget(Recomp r, UpdateChannel channel, out string label) {
    label = r.Branch;
    if (channel == UpdateChannel.Stable) {
      try {
        string tag = S(Api($"repos/{r.Repo}/releases/latest"), "tag_name");
        if (tag != null) { label = tag; return S(Api($"repos/{r.Repo}/commits/{tag}"), "sha"); }
      } catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound) { }
    }
    return RemoteHead(r.Repo, r.Branch);
  }

  static string VersionFile(string root) => Path.Combine(root, ".local", "source-version.txt");
  public static void SaveLocalHead(string root, string sha) {
    try { Directory.CreateDirectory(Path.GetDirectoryName(VersionFile(root))); File.WriteAllText(VersionFile(root), sha + "\n"); } catch (Exception) { }
  }

  public static bool IsGitClone(Recomp r) => Directory.Exists(Path.Combine(r.Root, ".git")) || File.Exists(Path.Combine(r.Root, ".git"));

  // The commit the recompiler folder holds (null when unknown: a ZIP from before the launcher recorded it).
  public static string LocalHead(Recomp r) {
    if (IsGitClone(r)) return Git(r.Root, "rev-parse HEAD", null)?.Trim();
    try { return File.Exists(VersionFile(r.Root)) ? File.ReadAllText(VersionFile(r.Root)).Trim() : null; } catch (Exception) { return null; }
  }

  public static void UpdateRecomp(Recomp r, string sha, Action<string> log) {
    if (IsGitClone(r)) {
      if (Git(r.Root, "fetch --tags origin", log) == null) throw new IOException("git fetch");
      if (Git(r.Root, "merge --ff-only " + sha, log) == null)
        throw new IOException(L.T("git não conseguiu avançar (há mudanças locais na pasta?)", "git could not fast-forward (local changes in the folder?)"));
      Git(r.Root, "submodule update --init --recursive", log);
      return;
    }
    string zip = Path.Combine(Path.GetTempPath(), r.Repo.Replace('/', '-') + "-" + sha.Substring(0, 7) + ".zip");
    string url = $"https://codeload.github.com/{r.Repo}/zip/{sha}";
    log(L.T("Baixando ", "Downloading ") + url);
    using (var web = Web()) { web.Headers.Remove(HttpRequestHeader.Accept); web.DownloadFile(url, zip); }
    log(L.T("Extraindo para ", "Extracting to ") + r.Root);
    KitDownload.Unzip(zip, r.Root);
    File.Delete(zip);
    SaveLocalHead(r.Root, sha);
    log("OK");
  }

  // git in the folder; its output goes to the log. Returns the output, or null when git failed or is missing.
  static string Git(string dir, string args, Action<string> log) {
    try {
      var psi = new ProcessStartInfo("git", "-C \"" + dir + "\" " + args) {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
      };
      log?.Invoke("git " + args);
      using (var p = Process.Start(psi)) {
        string err = null;
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) { err += e.Data + "\n"; log?.Invoke(e.Data); } };
        p.BeginErrorReadLine();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (log != null) foreach (string line in output.Split('\n').Where(l => l.Trim().Length > 0)) log(line.TrimEnd());
        return p.ExitCode == 0 ? output : null;
      }
    } catch (Exception ex) { log?.Invoke("git: " + ex.Message); return null; }
  }
}

}
