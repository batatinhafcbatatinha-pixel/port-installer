// The PC settings (the game's ini, the same file the in-game F1 menu uses), shown in their own window.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PortLauncher {

sealed partial class MainForm {
  // ------------------------------------------------------------------ PC settings page

  static readonly HashSet<string> OffWords = new HashSet<string> { "Desligada", "Desligado", "Não", "Esconder", "Off", "No", "Hide" };

  void BuildSettingsPage() {
    var stack = Stack(Theme.Bg);
    settingsNote = Words("", Theme.UI(9.5f), Theme.Text3, new Padding(4, 0, 4, 14));
    stack.Controls.Add(settingsNote);

    var monitors = Displays.Monitors();
    var gpus = Gpus.List();
    foreach (var g in cfg.Groups) {
      var card = AddCard(stack, out _);
      card.Controls.Add(Words(L.T(g).ToUpperInvariant(), Theme.Semi(9.5f), Theme.Accent, new Padding(0, 0, 0, 4)));
      bool first = true;
      foreach (var s in g.Items) {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = new Padding(0, 12, 0, 12) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        var left = Stack(Theme.Surface);
        left.Dock = DockStyle.None; left.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        left.Margin = new Padding(0, 0, 24, 0);
        left.Controls.Add(Words(L.T(s), Theme.Semi(10.5f), Theme.Text, new Padding(0, 0, 0, 2)));
        string help = (L.Pt ? s.HelpPt : s.HelpEn) + (s.Restart ? L.T(" Vale na próxima vez que o jogo abrir.", " Applies the next time the game starts.") : "");
        if (help.Trim().Length > 0) left.Controls.Add(Words(help.Trim(), Theme.UI(9f), Theme.Text3, Padding.Empty));
        row.Controls.Add(left, 0, 0);

        Control editor;
        if (s.Max > s.Min) {
          var slider = new Slider { Width = 360, Anchor = AnchorStyles.Right };
          slider.Range(s.Min, s.Max, s.Step);
          slider.ValueChanged += (o, e) => Changed();
          editor = slider;
        } else if (s.Source == null && s.Choices.Count == 2 && s.Choices.Any(c => c.Value == 0 && OffWords.Contains(c.Pt)) && s.Choices.Any(c => c.Value == 1)) {
          var toggle = new ToggleSwitch { Width = 360, Anchor = AnchorStyles.Right };
          toggle.OnText = L.T(s.Choices.First(c => c.Value == 1));
          toggle.OffText = L.T(s.Choices.First(c => c.Value == 0));
          toggle.CheckedChanged += (o, e) => Changed();
          editor = toggle;
        } else {
          var box = new ChoiceBox { Width = 360, Anchor = AnchorStyles.Right };
          FillChoices(s, box, monitors, gpus);
          box.SelectedIndexChanged += (o, e) => { if (box == monitorCombo) RefillRefresh(); Changed(); };
          editor = box;
        }
        editor.Margin = Padding.Empty;
        row.Controls.Add(editor, 1, 0);
        if (!first) card.Controls.Add(new Panel { Height = 1, BackColor = Theme.Border, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty });
        first = false;
        card.Controls.Add(row);
        editors[s] = editor;
        rows[s] = row;
      }
    }

    var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 58, ColumnCount = 3, BackColor = Theme.Bg, Padding = new Padding(0, 14, 0, 0) };
    bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    saveState = new Label { AutoSize = true, ForeColor = Theme.Text3, Anchor = AnchorStyles.Left, Margin = new Padding(4, 10, 0, 0) };
    defaultsButton = new FlatButton(L.T("Restaurar padrões", "Restore defaults"), ButtonKind.Secondary, 190, 40) { Margin = new Padding(0, 0, 10, 0) };
    defaultsButton.Click += (s, e) => { foreach (var kv in editors) SetEditor(kv.Key, kv.Key.Default); Changed(); };
    saveButton = new FlatButton(L.T("Salvar", "Save"), ButtonKind.Primary, 150, 40) { Margin = Padding.Empty };
    saveButton.Click += (s, e) => SaveSettings();
    bottom.Controls.Add(saveState, 0, 0);
    bottom.Controls.Add(defaultsButton, 1, 0);
    bottom.Controls.Add(saveButton, 2, 0);
    pageSettings.Controls.Add(Scroller(stack));
    pageSettings.Controls.Add(bottom);
  }

  void Changed() {
    if (loading) return;
    dirty = true;
    saveState.Text = L.T("Alterações não salvas", "Unsaved changes");
    saveState.ForeColor = Theme.Text2;
  }

  void FillChoices(Setting s, ChoiceBox box, List<MonitorInfo> monitors, List<KeyValuePair<int, string>> gpus) {
    box.Items.Clear();
    switch (s.Source) {
      case "monitors":
        monitorCombo = box;
        for (int i = 0; i < Math.Max(1, monitors.Count); ++i)
          box.Items.Add(new ChoiceBox.Entry { Value = i, Text = (i + 1) + ": " + (i < monitors.Count ? monitors[i].Name : "?") });
        box.Tag = monitors;
        return;
      case "refresh":
        refreshCombo = box;
        box.Tag = s;
        RefillRefresh();
        return;
      case "gpus":
        box.Items.Add(new ChoiceBox.Entry { Value = -1, Text = L.T("Automática (padrão do Windows)", "Automatic (Windows default)") });
        foreach (var g in gpus) box.Items.Add(new ChoiceBox.Entry { Value = g.Key, Text = g.Value });
        return;
    }
    foreach (var c in s.Choices) box.Items.Add(new ChoiceBox.Entry { Value = c.Value, Text = L.T(c) });
  }

  void RefillRefresh() {
    if (refreshCombo == null) return;
    var monitors = monitorCombo?.Tag as List<MonitorInfo> ?? Displays.Monitors();  // no monitor row: the primary one
    int index = monitorCombo?.Selected?.Value ?? 0;
    string device = monitors != null && index < monitors.Count ? monitors[index].Device : null;
    int keep = refreshCombo.Selected?.Value ?? 0, current = 60;
    var rates = device != null ? Displays.RefreshRates(device, out current) : new List<int>();
    refreshCombo.Items.Clear();
    // Value 0 is "the monitor's rate" for the Xbox menus; a game whose 0 means something else names it in its choices.
    var zero = refreshCombo.Tag as Setting;
    var named = zero?.Choices.FirstOrDefault(c => c.Value == 0);
    refreshCombo.Items.Add(new ChoiceBox.Entry { Value = 0, Text = named != null ? L.T(named) : L.T($"A do monitor ({current} Hz)", $"Monitor refresh rate ({current} Hz)") });
    foreach (int hz in rates) refreshCombo.Items.Add(new ChoiceBox.Entry { Value = hz, Text = hz + " Hz" });
    refreshCombo.Select(keep, 0);
    refreshCombo.Invalidate();
  }

  void SetEditor(Setting s, int value) {
    switch (editors[s]) {
      case ChoiceBox box: box.Select(value, s.Default); break;
      case ToggleSwitch t: t.Checked = value != 0; break;
      case Slider sl: sl.Value = value; break;
    }
  }

  int GetEditor(Setting s) {
    switch (editors[s]) {
      case ChoiceBox box: return box.Selected?.Value ?? s.Default;
      case ToggleSwitch t: return t.Checked ? 1 : 0;
      case Slider sl: return sl.Value;
    }
    return s.Default;
  }

  bool SettingVisible(Setting s) =>
      s.Requires != "vulkan" || (root != null && cfg.VulkanDir != null && Directory.Exists(Path.Combine(root, cfg.VulkanDir)));

  void LoadSettings() {
    string ini = IniPath;
    bool ready = ini != null && Directory.Exists(Path.GetDirectoryName(ini));
    settingsNote.Text = ready
        ? (cfg.PcMenu ? L.T("As mesmas opções do menu F1 do jogo, salvas em ", "The same options as the game's F1 menu, saved in ")
                      : L.T("Opções do PC, salvas em ", "PC options, saved in ")) + Path.GetFileName(ini) +
          L.T(". O jogo usa estas opções quando abrir.", ". The game uses them when it starts.")
        : L.T("Dá para conferir as opções agora; para salvar, instale o jogo primeiro (as configurações ficam ao lado dele).",
              "You can look at the options now; to save them, install the game first (the settings are kept next to it).");
    saveButton.Enabled = ready; defaultsButton.Enabled = ready;
    loading = true;
    foreach (var kv in rows) kv.Value.Visible = SettingVisible(kv.Key);
    // Monitor first: the refresh list depends on it.
    var plain = ready && cfg.PlainIni ? PlainIni.Read(ini) : null;
    foreach (var s in editors.Keys.OrderBy(k => k.Source == "monitors" ? 0 : 1))
      SetEditor(s, ready ? ReadSetting(s, ini, plain) : s.Default);
    loading = false;
    dirty = false;
    saveState.Text = "";
  }

  // One value from wherever the game keeps it (see Setting.Store / Section / File / Div and Choice.Set).
  int ReadSetting(Setting s, string ini, Dictionary<string, string> plain) {
    string section = s.Section ?? cfg.Section;
    Func<string, string> text = key => plain != null ? (plain.TryGetValue(key, out var v) ? v : "") : Ini.GetText(ini, section, key);
    Func<string, int, int> number = (key, def) => int.TryParse(text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : def;
    switch (s.Store) {
      case "gpu_pref":
        return GameExe != null ? GpuPreference.Get(Path.GetFullPath(GameExe)) : s.Default;
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

  void WriteSetting(Setting s, int value, string ini, Dictionary<string, string> plain) {
    string section = s.Section ?? cfg.Section;
    Action<string, string> put = (key, v) => { if (plain != null) plain[key] = v; else Ini.PutText(ini, section, key, v); };
    switch (s.Store) {
      case "gpu_pref":
        if (GameExe != null) GpuPreference.Set(Path.GetFullPath(GameExe), value);
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

  void SaveSettings() {
    string ini = IniPath;
    if (ini == null || !Directory.Exists(Path.GetDirectoryName(ini))) return;
    var plain = cfg.PlainIni ? PlainIni.Read(ini) : null;
    foreach (var s in editors.Keys) if (SettingVisible(s)) WriteSetting(s, GetEditor(s), ini, plain);
    if (plain != null) PlainIni.Write(ini, plain);
    dirty = false;
    saveState.Text = L.T("Salvo. O jogo usa estas opções na próxima vez que abrir.", "Saved. The game uses these options the next time it starts.");
    saveState.ForeColor = Theme.Accent;
  }
}

}
