// The Dependencies tab: everything the build needs, what is already on this PC, what is missing,
// how much space each part takes, and a button to install just that part (setup.ps1 -Only).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortLauncher {

sealed partial class MainForm {
  TableLayoutPanel depsList;
  Label depsSummary;
  FlatButton depsAllButton, depsRecheckButton;
  readonly List<FlatButton> depButtons = new List<FlatButton>();
  List<Dep> deps = new List<Dep>();
  bool depsChecking;

  void BuildDepsPage() {
    var stack = Stack(Theme.Bg);
    stack.Controls.Add(Words(L.T("O que a montagem do jogo precisa neste PC. O que faltar pode ser instalado item por item ou tudo de uma vez.",
                                "What building the game needs on this PC. Anything missing can be installed one by one or all at once."),
                           Theme.UI(9.5f), Theme.Text3, new Padding(4, 0, 4, 14)));
    depsList = AddCard(stack, out _);
    var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 84, ColumnCount = 3, BackColor = Theme.Bg, Padding = new Padding(0, 14, 0, 0) };
    bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    depsSummary = new Label { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Text2, Anchor = AnchorStyles.Left, Margin = new Padding(4, 0, 12, 0) };
    depsRecheckButton = new FlatButton(L.T("Verificar de novo", "Check again"), ButtonKind.Secondary, 170, 40) { Margin = new Padding(0, 0, 10, 0), Anchor = AnchorStyles.Right };
    depsRecheckButton.Click += (s, e) => CheckDeps();
    depsAllButton = new FlatButton(L.T("Instalar tudo o que falta", "Install everything missing"), ButtonKind.Primary, 240, 40) { Margin = Padding.Empty, Anchor = AnchorStyles.Right };
    depsAllButton.Click += (s, e) => FromDeps(() => Install(Current?.Disc));
    bottom.Controls.Add(depsSummary, 0, 0);
    bottom.Controls.Add(depsRecheckButton, 1, 0);
    bottom.Controls.Add(depsAllButton, 2, 0);
    pageDeps.Controls.Add(Scroller(stack));
    pageDeps.Controls.Add(bottom);
  }

  List<Dep> DepList() => DepsCheck.List(cfg);

  async void CheckDeps() {
    if (depsChecking) return;
    depsChecking = true;
    deps = DepList();
    RenderDeps();
    string kit = root;
    bool installed = Installed;
    string exeName = installed ? Path.GetFileName(GameExe) : "";
    var results = await Task.Run(() => DepsCheck.Run(cfg, kit, installed, exeName));
    for (int i = 0; i < deps.Count && i < results.Count; ++i) { deps[i].Ok = results[i].Key; deps[i].Found = results[i].Value; }
    depsChecking = false;
    RenderDeps();
  }


  void RenderDeps() {
    if (depsList == null || depsList.IsDisposed) return;
    depsList.SuspendLayout();
    depsList.Controls.Clear();
    depButtons.Clear();
    bool first = true;
    foreach (var d in deps) {
      if (!first) depsList.Controls.Add(new Panel { Height = 1, BackColor = Theme.Border, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty });
      first = false;
      var row = new TableLayoutPanel { ColumnCount = 5, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = new Padding(0, 12, 0, 12) };
      row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
      row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
      row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
      row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
      var dot = new StatusDot { Ok = d.Ok, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 0) };
      var name = Stack(Theme.Surface);
      name.Dock = DockStyle.None; name.Anchor = AnchorStyles.Left | AnchorStyles.Right; name.Margin = new Padding(0, 0, 12, 0);
      name.Controls.Add(Words(d.Name, Theme.Semi(10.5f), Theme.Text, new Padding(0, 0, 0, 2)));
      name.Controls.Add(Words(d.Detail, Theme.UI(9f), Theme.Text3, Padding.Empty));
      string stateText = d.Ok == null ? L.T("verificando...", "checking...")
                       : d.Ok == true ? L.T("Instalado", "Installed") : L.T("Falta", "Missing");
      var state = Stack(Theme.Surface);
      state.Dock = DockStyle.None; state.Anchor = AnchorStyles.Left | AnchorStyles.Right; state.Margin = new Padding(0, 0, 8, 0);
      state.Controls.Add(Words(stateText, Theme.Semi(10f), d.Ok == null ? Theme.Text3 : d.Ok == true ? Theme.Accent : Theme.Danger, new Padding(0, 0, 0, 2)));
      if (!string.IsNullOrEmpty(d.Found)) state.Controls.Add(Words(d.Found, Theme.UI(9f), Theme.Text3, Padding.Empty));
      string sizeText = d.Gb <= 0 ? "" : d.Gb < 1 ? $"~{Math.Max(1, (int)Math.Round(d.Gb * 1024))} MB" : $"~{d.Gb:0.#} GB";
      var size = Stack(Theme.Surface);
      size.Dock = DockStyle.None; size.Anchor = AnchorStyles.Left | AnchorStyles.Right;
      size.Controls.Add(Words(sizeText, Theme.Semi(10f), Theme.Text2, new Padding(0, 0, 0, 2)));
      if (d.OnSystemDrive && sizeText.Length > 0) size.Controls.Add(Words(L.T("no C:", "on C:"), Theme.UI(9f), Theme.Text3, Padding.Empty));
      var button = new FlatButton(d.Only == null ? L.T("Montar", "Build") : L.T("Instalar", "Install"), ButtonKind.Blue, 112, 36) { Anchor = AnchorStyles.Right, Margin = Padding.Empty, Tag = d };
      button.Visible = d.Ok == false && root != null;  // before the kit is downloaded only "install everything" works
      button.Click += (s, e) => { var dep = (Dep)((Control)s).Tag; FromDeps(() => InstallDep(dep)); };
      depButtons.Add(button);
      row.Controls.Add(dot, 0, 0);
      row.Controls.Add(name, 1, 0);
      row.Controls.Add(state, 2, 0);
      row.Controls.Add(size, 3, 0);
      row.Controls.Add(button, 4, 0);
      depsList.Controls.Add(row);
    }
    depsList.ResumeLayout();
    UpdateDepsSummary();
    UpdateDepButtons();
  }

  void UpdateDepsSummary() {
    var missing = deps.Where(d => d.Ok == false).ToList();
    double onC = missing.Where(d => d.OnSystemDrive).Sum(d => d.Gb);
    double here = missing.Where(d => !d.OnSystemDrive).Sum(d => d.Gb);
    string folder = root ?? DefaultInstallFolder();
    string driveName = Path.GetPathRoot(folder);
    double freeHere = FreeGb(driveName), freeC = FreeGb(Path.GetPathRoot(Environment.SystemDirectory));
    bool sameDrive = string.Equals(driveName, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase);
    bool shortOfSpace = false;
    string text;
    if (deps.Any(d => d.Ok == null)) text = L.T("Verificando...", "Checking...");
    else if (missing.Count == 0) text = L.T("Tudo instalado.", "Everything is installed.");
    else {
      text = L.T($"Faltam {missing.Count} itens: ~{here:0.#} GB em {driveName} (livre: {freeHere:0.#} GB)",
                 $"{missing.Count} items missing: ~{here:0.#} GB on {driveName} (free: {freeHere:0.#} GB)");
      if (onC > 0) text += sameDrive ? L.T($" + ~{onC:0.#} GB do sistema", $" + ~{onC:0.#} GB of system parts")
                                     : L.T($" e ~{onC:0.#} GB no C: (livre: {freeC:0.#} GB)", $" and ~{onC:0.#} GB on C: (free: {freeC:0.#} GB)");
      text += ".";
      shortOfSpace = sameDrive ? here + onC > freeHere : here > freeHere || onC > freeC;
      if (shortOfSpace) text += L.T(" Falta espaço em disco.", " Not enough disk space.");
    }
    depsSummary.Text = text;
    depsSummary.ForeColor = shortOfSpace ? Theme.Danger : missing.Count == 0 && !deps.Any(d => d.Ok == null) ? Theme.Accent : Theme.Text2;
  }

  static double FreeGb(string drive) => DepsCheck.FreeGb(drive);

  void UpdateDepButtons() {
    if (depsAllButton == null || depsAllButton.IsDisposed) return;
    bool busy = Busy;
    foreach (var b in depButtons) b.Enabled = !busy && root != null;
    depsAllButton.Enabled = !busy && deps.Any(d => d.Ok == false);
    depsRecheckButton.Enabled = !busy && !depsChecking;
  }

  void InstallDep(Dep d) {
    if (Busy || root == null) return;
    if (d.Only == null) { Install(Current?.Disc); return; }
    ShowLog();
    log.Clear();
    progress.Value = 0;
    RunSetup(d.Only);
  }
}

}
