// The Recompilers window: every recompiler the launcher knows, whether it is on this PC, and buttons to download
// one (its source from GitHub, nothing from any game), point at a folder that already holds it, or open it.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortLauncher {

sealed partial class MainForm {
  void ShowRecomps() {
    if (!Lib || Busy) return;
    using (var d = new RecompsDialog(registry)) {
      d.ShowDialog(this);
      if (d.Download != null) DownloadRecomp(d.Download);
      else if (d.Changed) Rescan();
    }
  }

  string DefaultRecompParent() {
    // Next to the recompiler the launcher came with, else <largest drive>\Games.
    if (registry.Home != null && registry.Installed.TryGetValue(registry.Home, out var home)) return Path.GetDirectoryName(home.Root.TrimEnd('\\'));
    var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
    return Path.Combine(best?.RootDirectory.FullName ?? "C:\\", "Games");
  }

  async void DownloadRecomp(KnownRecomp k) {
    if (Busy || k == null) return;
    string parent = DefaultRecompParent();
    using (var d = new FolderBrowserDialog { Description = L.T($"Onde guardar o {k.Name} (uma pasta {k.Folder} é criada dentro)", $"Where to keep {k.Name} (a {k.Folder} folder is created inside)"),
                                             SelectedPath = Directory.Exists(parent) ? parent : "" }) {
      if (d.ShowDialog(this) != DialogResult.OK) return;
      parent = d.SelectedPath;
    }
    string target = Path.Combine(parent, k.Folder);
    SetLogVisible(true);
    log.Clear();
    stepLabel.Text = L.T("Baixando ", "Downloading ") + k.Name + "...";
    tbRecomps.Enabled = tbInstall.Enabled = tbOpen.Enabled = false;
    try {
      string root = File.Exists(Path.Combine(target, "launcher.json")) ? target
          : await Task.Run(() => Recomp.Download(k.Repo, k.Branch, target, AppendLog));
      var r = Recomp.Load(root);
      if (r == null) throw new IOException(L.T("A pasta baixada não tem launcher.json.", "The downloaded folder has no launcher.json."));
      r.EnsureInstallerFiles(AppendLog);
      registry.Remember(k.Id, r.Root);
      registry.Installed[k.Id] = r;
      stepLabel.Text = k.Name + L.T(" pronto. Procurando os jogos dele nas suas pastas...", " ready. Looking for its games in your folders...");
    } catch (Exception ex) {
      AppendLog(ex.Message);
      stepLabel.Text = L.T("O download parou. Veja o log.", "The download stopped. See the log.");
    }
    RefreshStatus();
    Rescan();
  }
}

sealed class RecompsDialog : Form {
  readonly RecompRegistry reg;
  readonly TableLayoutPanel list;
  public KnownRecomp Download;
  public bool Changed;

  public RecompsDialog(RecompRegistry registry) {
    reg = registry;
    Text = L.T("Recompiladores", "Recompilers");
    Font = Theme.UI(10f);
    AutoScaleMode = AutoScaleMode.Dpi;
    ClientSize = new Size(860, 560);
    StartPosition = FormStartPosition.CenterParent;
    MinimizeBox = MaximizeBox = false;
    BackColor = Theme.Bg;
    ForeColor = Theme.Text;
    Theme.DarkTitle(this);
    var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Bg, Padding = new Padding(26, 20, 26, 18) };
    outer.Controls.Add(new Label {
      Text = L.T("Cada console tem o seu recompilador. Baixe os que quiser: só o código-fonte vem do GitHub, nada de nenhum jogo. Os jogos de cada um aparecem na aba do console.",
                 "Each console has its own recompiler. Get the ones you want: only source code comes from GitHub, nothing from any game. Each one's games show up in its console tab."),
      AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Theme.Text2, Margin = new Padding(0, 0, 0, 14) });
    list = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top, BackColor = Theme.Bg };
    list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    scroll.Controls.Add(list);
    outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    outer.Controls.Add(scroll);
    Controls.Add(outer);
    Fill();
  }

  void Fill() {
    list.SuspendLayout();
    list.Controls.Clear();
    foreach (var k in reg.Known) {
      reg.Installed.TryGetValue(k.Id, out var r);
      var card = new Card { BackColor = Theme.Surface, Height = 96, Dock = DockStyle.Top, Padding = new Padding(18, 12, 14, 12), Margin = new Padding(0, 0, 0, 10), Width = 790 };
      var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Surface };
      row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
      var text = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Surface };
      text.Controls.Add(new Label { Text = k.Name, Font = Theme.Semi(11.5f), AutoSize = true, ForeColor = Theme.Text });
      text.Controls.Add(new Label { Text = r != null ? L.T("Instalado", "Installed") + "  ·  " + r.Games.Count + L.T(" perfis de jogos", " game profiles") + "\n" + Compact(r.Root)
                                                     : (L.Pt ? k.Pt : k.En), AutoSize = true, MaximumSize = new Size(520, 0),
                                    ForeColor = r != null ? Theme.Accent : Theme.Text3, Margin = new Padding(0, 4, 0, 0) });
      var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Anchor = AnchorStyles.Right };
      if (r == null) {
        var get = new FlatButton(L.T("Baixar", "Download"), ButtonKind.Primary, 120, 38) { Margin = new Padding(0, 0, 8, 0) };
        get.Click += (s, e) => { Download = k; Close(); };
        var locate = new FlatButton(L.T("Já tenho...", "Locate..."), ButtonKind.Secondary, 120, 38) { Margin = Padding.Empty };
        locate.Click += (s, e) => Locate(k);
        buttons.Controls.Add(get);
        buttons.Controls.Add(locate);
      } else {
        var open = new FlatButton(L.T("Abrir pasta", "Open folder"), ButtonKind.Secondary, 130, 38) { Margin = new Padding(0, 0, 8, 0) };
        open.Click += (s, e) => System.Diagnostics.Process.Start("explorer.exe", "\"" + r.Root + "\"");
        buttons.Controls.Add(open);
        if (reg.Home != k.Id) {
          var forget = new FlatButton(L.T("Tirar da lista", "Remove"), ButtonKind.Secondary, 130, 38) { Margin = Padding.Empty };
          forget.Click += (s, e) => {
            if (!Dialog.Ask(this, Text, L.T($"Tirar o {k.Name} do launcher? A pasta fica no disco ({r.Root}).", $"Remove {k.Name} from the launcher? The folder stays on disk ({r.Root})."),
                            L.T("Tirar", "Remove"), L.T("Cancelar", "Cancel"))) return;
            reg.Forget(k.Id); Changed = true; Fill();
          };
          buttons.Controls.Add(forget);
        }
      }
      row.Controls.Add(text, 0, 0);
      row.Controls.Add(buttons, 1, 0);
      card.Controls.Add(row);
      list.Controls.Add(card);
    }
    list.ResumeLayout();
  }

  // A long folder path cut in the middle.
  static string Compact(string path) => path.Length <= 64 ? path : path.Substring(0, 18) + "..." + path.Substring(path.Length - 42);

  void Locate(KnownRecomp k) {
    using (var d = new FolderBrowserDialog { Description = L.T("A pasta do ", "The folder of ") + k.Name }) {
      if (d.ShowDialog(this) != DialogResult.OK) return;
      Recomp r = null;
      try { r = Recomp.Find(d.SelectedPath); } catch (Exception) { }
      if (r == null || reg.Match(r)?.Id != k.Id) {
        Dialog.Info(this, Text, L.T("Esta pasta não é do ", "This folder is not ") + k.Name + ".");
        return;
      }
      reg.Remember(k.Id, r.Root);
      reg.Installed[k.Id] = r;
      Changed = true;
      Fill();
    }
  }
}

}
