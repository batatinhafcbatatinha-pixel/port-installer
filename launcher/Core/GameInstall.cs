// The game-file installer inside the launcher (the same work as engine.py / media.py / layout.py, which stay for
// Setup.cmd run on its own): reads the disc image (ISO 9660 or Xbox XDVDFS) or an extracted folder, checks the
// SHA-256 of the files installer.json pins, copies every file into game_files with a read-back check and, for
// ISO 9660, writes the .disc index (layout.tsv + metadata.bin) the runtimes read the disc through.
// No window and no Python: progress goes to the launcher's log and progress bar.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PortLauncher {

sealed class GameInstallResult {
  public string Source, ReportPath;
  public bool CanDeleteSource;
  public long SourceSize, SourceTicks;
}

static class GameInstall {
  const int Sector = 2048;
  const int Block = 4 << 20;

  public sealed class Entry {
    public string Path;
    public long Size;
    public List<(long Offset, long Length)> Extents = new List<(long, long)>();
  }

  // ------------------------------------------------------------------ paths

  static readonly HashSet<string> Reserved = new HashSet<string>(
      new[] { "CON", "PRN", "AUX", "NUL" }.Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { "COM" + i, "LPT" + i })),
      StringComparer.Ordinal);

  public static void SafePath(string name) {
    bool bad = string.IsNullOrEmpty(name) || name.StartsWith("/") || name.Contains('\\');
    if (!bad)
      foreach (string p in name.Split('/')) {
        if (p == "" || p == "." || p == ".." || p.EndsWith(" ") || p.EndsWith(".") ||
            p.IndexOfAny(":<>\"|?*".ToCharArray()) >= 0 || p.Any(c => c < 32) ||
            Reserved.Contains(p.Split('.')[0].ToUpperInvariant())) { bad = true; break; }
      }
    if (bad) throw new InvalidDataException(L.T("Caminho inseguro: ", "Unsafe path: ") + name);
  }

  static string Target(string root, string name) {
    SafePath(name);
    string path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
    string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!path.StartsWith(full, OS.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
      throw new InvalidDataException(L.T("Destino fora da pasta do jogo: ", "Destination outside the game folder: ") + name);
    for (var p = new FileInfo(path).Directory; p != null && p.FullName.Length >= full.Length - 1; p = p.Parent)
      if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException(L.T("Link/junção não permitido: ", "Link/junction not allowed: ") + p.FullName);
    if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
      throw new InvalidDataException(L.T("Link/junção não permitido: ", "Link/junction not allowed: ") + path);
    return path;
  }

  // ------------------------------------------------------------------ disc images

  static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
  static uint U32Be(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
  static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);

  public static List<Entry> Inventory(Stream stream, long size, string filesystem) {
    byte[] Read(long offset, long count) {
      if (offset < 0 || count < 0 || offset + count > size) throw new InvalidDataException(L.T("Imagem truncada ou extensão inválida", "Truncated image or invalid extent"));
      stream.Seek(offset, SeekOrigin.Begin);
      var data = new byte[count];
      int got = 0;
      while (got < count) { int n = stream.Read(data, got, (int)count - got); if (n <= 0) break; got += n; }
      if (got != count) throw new InvalidDataException(L.T("Leitura incompleta", "Incomplete read"));
      return data;
    }
    var entries = new List<Entry>();
    var paths = new HashSet<string>(StringComparer.Ordinal);
    var visited = new HashSet<(long, long)>();
    void Add(string name, long count, List<(long, long)> extents) {
      SafePath(name);
      string key = name.ToLowerInvariant();
      if (paths.Contains(key) || paths.Count >= 200000) throw new InvalidDataException(L.T("Caminhos duplicados ou arquivos demais", "Duplicate paths or too many files"));
      foreach (var (o, l) in extents) if (o < 0 || o + l > size) throw new InvalidDataException(L.T("Arquivo fora da imagem", "File outside the image"));
      paths.Add(key);
      entries.Add(new Entry { Path = name, Size = count, Extents = extents });
    }
    void Guard(long sector, long count, int depth) {
      if (depth > 64 || count > (64 << 20) || !visited.Add((sector, count))) throw new InvalidDataException(L.T("Estrutura de diretórios inválida", "Invalid directory structure"));
    }

    if (filesystem == "xdvdfs") {
      byte[] magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");
      long baseOff = -1;
      byte[] header = null;
      foreach (long b in new long[] { 0, 0xFD90000, 0x2080000, 0x18300000 }) {
        if (b + 33 * Sector > size) continue;
        var h = Read(b + 32 * Sector, Sector);
        if (h.Take(20).SequenceEqual(magic) && h.Skip(Sector - 20).SequenceEqual(magic)) { baseOff = b; header = h; break; }
      }
      if (header == null) throw new InvalidDataException(L.T("Volume Xbox/Xbox 360 XDVDFS não encontrado", "No Xbox/Xbox 360 XDVDFS volume found"));
      void Walk(long sector, long count, string prefix, int depth) {
        Guard(sector, count, depth);
        var table = Read(baseOff + sector * Sector, count);
        var pending = new Stack<int>();
        if (count > 0) pending.Push(0);
        var seen = new HashSet<int>();
        while (pending.Count > 0) {
          int off = pending.Pop();
          if (!seen.Add(off)) throw new InvalidDataException(L.T("Ciclo na árvore XDVDFS", "Cycle in the XDVDFS tree"));
          if (off + 14 > table.Length) throw new InvalidDataException(L.T("Registro XDVDFS truncado", "Truncated XDVDFS record"));
          ushort left = U16(table, off), right = U16(table, off + 2);
          uint start = U32(table, off + 4), length = U32(table, off + 8);
          byte attr = table[off + 12], n = table[off + 13];
          if (left == 0xFFFF && right == 0xFFFF) continue;
          if (off + 14 + n > table.Length) throw new InvalidDataException(L.T("Nome XDVDFS truncado", "Truncated XDVDFS name"));
          string name = Encoding.ASCII.GetString(table, off + 14, n);
          SafePath(name);
          string path = prefix + name;
          if ((attr & 0x10) != 0) Walk(start, length, path + "/", depth + 1);
          else Add(path, length, new List<(long, long)> { (baseOff + (long)start * Sector, length) });
          foreach (ushort child in new[] { left, right })
            if (child != 0 && child != 0xFFFF) pending.Push(child * 4);
        }
      }
      Walk(U32(header, 20), U32(header, 24), "", 0);
    } else if (filesystem == "iso9660") {
      var pvd = Read(16 * Sector, Sector);
      if (!(pvd[0] == 1 && Encoding.ASCII.GetString(pvd, 1, 5) == "CD001" && pvd[6] == 1) || U16(pvd, 128) != Sector)
        throw new InvalidDataException(L.T("Volume ISO9660 de 2048 bytes não encontrado", "No ISO9660 volume with 2048-byte sectors found"));
      uint volume = U32(pvd, 80);
      if (volume != U32Be(pvd, 84) || (long)volume * Sector > size)
        throw new InvalidDataException(L.T("Volume ISO9660 truncado ou tamanho inválido", "Truncated ISO9660 volume or invalid size"));
      (long Sector, long Count, byte Flags, byte[] Name) Record(byte[] d) {
        if (d.Length < 34 || d[0] != d.Length || 33 + d[32] > d.Length) throw new InvalidDataException(L.T("Registro ISO9660 inválido", "Invalid ISO9660 record"));
        uint sec = U32(d, 2), cnt = U32(d, 10);
        if (sec != U32Be(d, 6) || cnt != U32Be(d, 14) || d[26] != 0 || d[27] != 0)
          throw new InvalidDataException(L.T("Extensão ISO9660 inválida/intercalada", "Invalid/interleaved ISO9660 extent"));
        return (sec + d[1], cnt, d[25], d.Skip(33).Take(d[32]).ToArray());
      }
      void Walk(byte[] raw, string prefix, int depth) {
        var (sector, count, _, _) = Record(raw);
        Guard(sector, count, depth);
        var data = Read(sector * Sector, count);
        int offset = 0;
        Entry continuation = null;
        while (offset < data.Length) {
          int n = data[offset];
          if (n == 0) { offset = (offset / Sector + 1) * Sector; continue; }
          if (offset + n > data.Length) throw new InvalidDataException(L.T("Registro ISO9660 inválido", "Invalid ISO9660 record"));
          var rec = new byte[n];
          Array.Copy(data, offset, rec, 0, n);
          offset += n;
          var (start, length, flags, rawName) = Record(rec);
          if (rawName.Length == 1 && (rawName[0] == 0 || rawName[0] == 1)) continue;
          string name = Encoding.ASCII.GetString(rawName).Split(new[] { ';' }, 2)[0];
          SafePath(name);
          string path = prefix + name;
          if ((flags & 2) != 0) {
            if (continuation != null) throw new InvalidDataException(L.T("Multiextensão incompleta", "Incomplete multi-extent file"));
            Walk(rec, path + "/", depth + 1);
          } else {
            if (continuation != null && continuation.Path != path) throw new InvalidDataException(L.T("Multiextensão fora de ordem", "Multi-extent file out of order"));
            if (continuation != null) {
              continuation.Size += length;
              continuation.Extents.Add((start * Sector, length));
            } else {
              Add(path, length, new List<(long, long)> { (start * Sector, length) });
              continuation = entries[entries.Count - 1];
            }
            if ((flags & 0x80) == 0) continuation = null;
          }
        }
        if (continuation != null) throw new InvalidDataException(L.T("Multiextensão incompleta", "Incomplete multi-extent file"));
      }
      var root = new byte[pvd[156]];
      Array.Copy(pvd, 156, root, 0, root.Length);
      Walk(root, "", 0);
    } else {
      throw new InvalidDataException(L.T("Sistema de arquivos não suportado: ", "Unsupported file system: ") + filesystem);
    }
    foreach (var e in entries)
      if (e.Extents.Any(x => x.Offset + x.Length > size)) throw new InvalidDataException(L.T("Arquivo fora da imagem", "File outside the image"));
    if (entries.Count == 0) throw new InvalidDataException(L.T("Nenhum arquivo na imagem", "No files in the image"));
    return entries;
  }

  // ------------------------------------------------------------------ .disc index (layout.py)

  sealed class Node {
    public bool Directory;
    public string Parent;
    public List<string> Children = new List<string>();
    public long Size, Lsn;
    public List<(long Offset, long Length)> Extents;
  }

  static string Leaf(string path) { int i = path.LastIndexOf('/'); return i < 0 ? path : path.Substring(i + 1); }

  public static (byte[] Layout, byte[] Metadata) BuildLayout(List<Entry> entries) {
    var cmp = StringComparer.Ordinal;
    var dirs = new Dictionary<string, Node>(cmp) { [""] = new Node { Directory = true, Parent = "" } };
    var nodes = new Dictionary<string, Node>(dirs, cmp);
    foreach (var entry in entries) {
      string[] parts = entry.Path.Split('/');
      for (int depth = 1; depth < parts.Length; ++depth) {
        string path = string.Join("/", parts, 0, depth);
        if (!nodes.ContainsKey(path)) {
          string parent = string.Join("/", parts, 0, depth - 1);
          var node = new Node { Directory = true, Parent = parent };
          nodes[path] = dirs[path] = node;
          nodes[parent].Children.Add(path);
        }
      }
      string par = string.Join("/", parts, 0, parts.Length - 1);
      if (nodes.ContainsKey(entry.Path)) throw new InvalidDataException(L.T("Conflito entre arquivo e diretório", "File/directory conflict"));
      nodes[entry.Path] = new Node { Parent = par, Size = entry.Size, Extents = entry.Extents.ToList() };
      nodes[par].Children.Add(entry.Path);
    }
    int RecordSize(byte[] name) => 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
    byte[] Identifier(string path) => Encoding.ASCII.GetBytes(Leaf(path) + (nodes[path].Directory ? "" : ";1"));
    long DirectorySize(Node node) {
      long offset = 68;
      foreach (string child in node.Children.OrderBy(x => x, cmp)) {
        int n = RecordSize(Identifier(child));
        if (n > 255) throw new InvalidDataException(L.T("Nome longo demais para ISO9660: ", "Name too long for ISO9660: ") + child);
        if (offset % Sector + n > Sector) offset = (offset / Sector + 1) * Sector;
        offset += n;
      }
      return (offset + Sector - 1) / Sector * Sector;
    }
    long cursor = 24;
    foreach (var e in entries) foreach (var (o, l) in e.Extents) cursor = Math.Max(cursor, (o + l + Sector - 1) / Sector);
    foreach (var kv in dirs.OrderBy(k => k.Key, cmp)) {
      kv.Value.Size = DirectorySize(kv.Value);
      kv.Value.Lsn = cursor;
      cursor += kv.Value.Size / Sector;
    }
    var rows = new List<(long Offset, long Length, long Position, string Path)>();
    foreach (var kv in nodes.OrderBy(k => k.Key, cmp)) {
      var node = kv.Value;
      if (node.Directory) continue;
      var extents = node.Extents.Count > 0 ? node.Extents : new List<(long, long)> { (cursor * Sector, node.Size) };
      if (node.Extents.Count == 0) cursor += Math.Max(1, (node.Size + Sector - 1) / Sector);
      node.Lsn = extents[0].Item1 / Sector;
      long position = 0;
      foreach (var (o, l) in extents) {
        rows.Add((o, (l + Sector - 1) / Sector * Sector, position, kv.Key));
        position += l;
      }
    }
    void Both16(byte[] d, int at, int v) { d[at] = (byte)v; d[at + 1] = (byte)(v >> 8); d[at + 2] = (byte)(v >> 8); d[at + 3] = (byte)v; }
    void Le32(byte[] d, int at, long v) { for (int i = 0; i < 4; ++i) d[at + i] = (byte)(v >> (8 * i)); }
    void Be32(byte[] d, int at, long v) { for (int i = 0; i < 4; ++i) d[at + i] = (byte)(v >> (24 - 8 * i)); }
    void Both32(byte[] d, int at, long v) { Le32(d, at, v); Be32(d, at + 4, v); }
    byte[] Rec(Node node, byte[] name) {
      var d = new byte[RecordSize(name)];
      d[0] = (byte)d.Length;
      Both32(d, 2, node.Lsn);
      Both32(d, 10, node.Size);
      d[18] = 126; d[19] = 1; d[20] = 1;
      d[25] = (byte)(node.Directory ? 2 : 0);
      Both16(d, 28, 1);
      d[32] = (byte)name.Length;
      Array.Copy(name, 0, d, 33, name.Length);
      return d;
    }
    var metadata = new MemoryStream();
    void AddMetadata(long offset, byte[] data) {
      long position = metadata.Length;
      metadata.Write(data, 0, data.Length);
      rows.Add((offset, data.Length, position, ".disc/metadata.bin"));
    }
    var order = dirs.Keys.OrderBy(n => n.Count(c => c == '/') + (n.Length > 0 ? 1 : 0)).ThenBy(n => n, cmp).ToList();
    var ids = new Dictionary<string, int>(cmp);
    for (int i = 0; i < order.Count; ++i) ids[order[i]] = i + 1;
    var tables = new List<List<byte>>();
    foreach (bool little in new[] { true, false }) {
      var table = new List<byte>();
      foreach (string path in order) {
        byte[] name = path.Length > 0 ? Encoding.ASCII.GetBytes(Leaf(path)) : new byte[] { 0 };
        if (name.Length > 255) throw new InvalidDataException(L.T("Nome de diretório longo demais", "Directory name too long"));
        table.Add((byte)name.Length); table.Add(0);
        var tmp = new byte[6];
        if (little) { Le32(tmp, 0, nodes[path].Lsn); tmp[4] = (byte)ids[nodes[path].Parent]; tmp[5] = (byte)(ids[nodes[path].Parent] >> 8); }
        else { Be32(tmp, 0, nodes[path].Lsn); tmp[4] = (byte)(ids[nodes[path].Parent] >> 8); tmp[5] = (byte)ids[nodes[path].Parent]; }
        table.AddRange(tmp);
        table.AddRange(name);
        if (name.Length % 2 != 0) table.Add(0);
      }
      tables.Add(table);
    }
    int pathTableBytes = tables[0].Count;
    long tableSectors = (pathTableBytes + Sector - 1) / Sector;
    long littleLsn = cursor, bigLsn = cursor + tableSectors;
    cursor += tableSectors * 2;
    foreach (var (lsn, table) in new[] { (littleLsn, tables[0]), (bigLsn, tables[1]) }) {
      var padded = new byte[tableSectors * Sector];
      table.CopyTo(padded);
      AddMetadata(lsn * Sector, padded);
    }
    var pvd = new byte[Sector];
    pvd[0] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1); pvd[6] = 1;
    Both32(pvd, 80, cursor); Both16(pvd, 120, 1); Both16(pvd, 124, 1); Both16(pvd, 128, Sector);
    Both32(pvd, 132, pathTableBytes);
    Le32(pvd, 140, littleLsn);
    Be32(pvd, 148, bigLsn);
    Rec(nodes[""], new byte[] { 0 }).CopyTo(pvd, 156);
    pvd[881] = 1;
    AddMetadata(16 * Sector, pvd);
    var terminator = new byte[Sector];
    terminator[0] = 0xFF; Encoding.ASCII.GetBytes("CD001").CopyTo(terminator, 1); terminator[6] = 1;
    AddMetadata(17 * Sector, terminator);
    foreach (var kv in dirs.OrderBy(k => k.Key, cmp)) {
      var node = kv.Value;
      var data = new byte[node.Size];
      int offset = 0;
      var records = new List<byte[]> { Rec(node, new byte[] { 0 }), Rec(nodes[node.Parent], new byte[] { 1 }) };
      records.AddRange(node.Children.OrderBy(c => c, cmp).Select(c => Rec(nodes[c], Identifier(c))));
      foreach (var item in records) {
        if (offset % Sector + item.Length > Sector) offset = (offset / Sector + 1) * Sector;
        item.CopyTo(data, offset);
        offset += item.Length;
      }
      AddMetadata(node.Lsn * Sector, data);
    }
    rows = rows.OrderBy(r => r.Offset).ThenBy(r => r.Length).ThenBy(r => r.Position).ThenBy(r => r.Path, cmp).ToList();
    for (int i = 1; i < rows.Count; ++i)
      if (rows[i - 1].Offset + rows[i - 1].Length > rows[i].Offset) throw new InvalidDataException(L.T("Extensões sobrepostas no mapa do disco", "Overlapping extents in the disc map"));
    var text = new StringBuilder();
    text.Append("EXTRACTED_DISC_V1\t").Append(cursor * Sector).Append('\n');
    foreach (var r in rows) text.Append(r.Offset).Append('\t').Append(r.Length).Append('\t').Append(r.Position).Append('\t').Append(r.Path).Append('\n');
    return (new UTF8Encoding(false).GetBytes(text.ToString()), metadata.ToArray());
  }

  // ------------------------------------------------------------------ install (engine.py)

  static IDictionary<string, object> Dict(IDictionary<string, object> d, string k) =>
      d.TryGetValue(k, out var v) && v is IDictionary<string, object> x ? x : new Dictionary<string, object>();

  static string Hex(byte[] h) => BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();

  // Whether game_files already holds the pinned version (setup's Game-Ok: the SHA-256 of every required file).
  public static bool GameOk(string root, string installerJson) {
    try {
      var config = Json.Object(File.ReadAllText(installerJson).TrimStart('﻿'));
      var required = Dict(config, "required_files");
      if (required.Count == 0) return false;
      string dest = config.TryGetValue("destination", out var d) && d != null ? Convert.ToString(d) : "game_files";
      foreach (var kv in required) {
        string f = Path.Combine(root, dest, kv.Key.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(f)) return false;
        using (var s = File.OpenRead(f)) using (var sha = SHA256.Create())
          if (!string.Equals(Hex(sha.ComputeHash(s)), Convert.ToString(kv.Value), StringComparison.OrdinalIgnoreCase)) return false;
      }
      return true;
    } catch (Exception) { return false; }
  }

  // Copies and checks the game from source (disc image or folder) into root/<destination>. status: one line per
  // step; progress: bytes done, bytes total (copy + read-back). Throws on any mismatch, leaving the source untouched.
  public static GameInstallResult Install(string source, string root, string installerJson, Action<string> status,
                                          Action<long, long> progress, CancellationToken cancel) {
    source = Path.GetFullPath(source);
    root = Path.GetFullPath(root);
    var config = Json.Object(File.ReadAllText(installerJson).TrimStart('﻿'));
    Directory.CreateDirectory(root);
    string lockFile = Path.Combine(root, ".game-install.lock");
    using (new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
      return Run(source, root, config, status, progress, cancel);
  }

  static GameInstallResult Run(string source, string root, IDictionary<string, object> config, Action<string> status,
                               Action<long, long> progress, CancellationToken cancel) {
    void Check() { if (cancel.IsCancellationRequested) throw new OperationCanceledException(L.T("Instalação cancelada. A origem foi preservada.", "Install cancelled. The source was kept.")); }
    var cmpPath = OS.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    bool Inside(string a, string b) => (a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, cmpPath);

    status(L.T("Lendo arquivos da origem…", "Reading the source files…"));
    string filesystem = config.TryGetValue("filesystem", out var fs) ? Convert.ToString(fs) : "iso9660";
    string output = Target(root, config.TryGetValue("destination", out var dv) && dv != null ? Convert.ToString(dv) : "game_files");
    bool isIso = File.Exists(source);
    if (!isIso && !Directory.Exists(source)) throw new InvalidDataException(L.T("Selecione uma ISO ou pasta existente", "Choose an existing ISO or folder"));
    if (string.Equals(source, output, cmpPath) || (!isIso && (Inside(output, source) || Inside(source, output))))
      throw new InvalidDataException(L.T("A pasta de origem e o destino devem ser separados e não podem conter um ao outro", "The source and the destination must be separate and not contain each other"));
    var states = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
    (long, long) Fingerprint(string p) { var i = new FileInfo(p); return (i.Length, i.LastWriteTimeUtc.Ticks); }

    Stream stream = null;
    try {
      List<Entry> entries;
      if (isIso) {
        states[source] = Fingerprint(source);
        stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        entries = Inventory(stream, stream.Length, filesystem);
      } else {
        entries = new List<Entry>();
        var dirs = new Stack<string>();
        dirs.Push(source);
        while (dirs.Count > 0) {
          Check();
          string dir = dirs.Pop();
          foreach (string p in Directory.GetFileSystemEntries(dir)) {
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
              throw new InvalidDataException(L.T("A origem contém um link/junção: ", "The source holds a link/junction: ") + p);
            if (Directory.Exists(p)) { dirs.Push(p); continue; }
            string rel = p.Substring(source.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/');
            SafePath(rel);
            if (rel.StartsWith(".disc/")) continue;
            states[p] = Fingerprint(p);
            entries.Add(new Entry { Path = rel, Size = states[p].Item1 });
          }
        }
        if (entries.Count == 0) throw new InvalidDataException(L.T("A pasta está vazia", "The folder is empty"));
      }
      var byName = new Dictionary<string, Entry>(StringComparer.Ordinal);
      foreach (var e in entries) byName[e.Path.ToLowerInvariant()] = e;
      if (byName.Count != entries.Count) throw new InvalidDataException(L.T("Nomes duplicados sem distinção de maiúsculas", "Duplicate names ignoring case"));
      var required = Dict(config, "required_files");
      if (required.Count == 0) throw new InvalidDataException(L.T("Configuração sem identificação do jogo", "The configuration does not identify the game"));
      foreach (var kv in Dict(config, "expected_files"))
        if (!byName.TryGetValue(kv.Key.ToLowerInvariant(), out var e) || e.Size != Convert.ToInt64(kv.Value))
          throw new InvalidDataException(L.T("Conteúdo incompleto ou versão diferente: ", "Incomplete content or a different version: ") + kv.Key);

      IEnumerable<byte[]> Chunks(Entry entry) {
        if (stream != null) {
          foreach (var (offset, length) in entry.Extents) {
            stream.Seek(offset, SeekOrigin.Begin);
            long count = length;
            while (count > 0) {
              Check();
              var blob = new byte[Math.Min(Block, count)];
              int got = 0;
              while (got < blob.Length) { int n = stream.Read(blob, got, blob.Length - got); if (n <= 0) break; got += n; }
              if (got == 0) throw new InvalidDataException(L.T("Imagem truncada", "Truncated image"));
              if (got < blob.Length) Array.Resize(ref blob, got);
              count -= got;
              yield return blob;
            }
          }
        } else {
          using (var h = File.OpenRead(Path.Combine(source, entry.Path.Replace('/', Path.DirectorySeparatorChar)))) {
            var buf = new byte[Block];
            int n;
            while ((n = h.Read(buf, 0, buf.Length)) > 0) {
              Check();
              var blob = new byte[n];
              Array.Copy(buf, blob, n);
              yield return blob;
            }
          }
        }
      }

      foreach (var kv in required) {
        Check();
        if (!byName.TryGetValue(kv.Key.ToLowerInvariant(), out var entry)) throw new InvalidDataException(L.T("Arquivo obrigatório ausente: ", "Required file missing: ") + kv.Key);
        status(L.T("Identificando versão: ", "Identifying the version: ") + kv.Key);
        using (var sha = SHA256.Create()) {
          foreach (var blob in Chunks(entry)) sha.TransformBlock(blob, 0, blob.Length, null, 0);
          sha.TransformFinalBlock(new byte[0], 0, 0);
          if (Hex(sha.Hash) != Convert.ToString(kv.Value).ToLowerInvariant())
            throw new InvalidDataException(L.T("Jogo/região/revisão incompatível: ", "Different game/region/revision: ") + kv.Key);
        }
      }
      if (config.TryGetValue("required_paths", out var rp) && rp is object[] reqPaths)
        foreach (string name in reqPaths.Select(Convert.ToString)) {
          string n = name.ToLowerInvariant(), dir = n.TrimEnd('/') + "/";
          if (!entries.Any(e => e.Path.ToLowerInvariant() == n || e.Path.ToLowerInvariant().StartsWith(dir)))
            throw new InvalidDataException(L.T("Conteúdo obrigatório ausente: ", "Required content missing: ") + name);
        }
      var targets = entries.Select(e => (Entry: e, Path: Target(output, e.Path))).ToList();
      long total = entries.Sum(e => e.Size), needed = targets.Where(t => !File.Exists(t.Path)).Sum(t => t.Entry.Size);
      if (DiskFree(root) < needed) throw new InvalidDataException(L.T("Espaço insuficiente para os arquivos do jogo", "Not enough free space for the game files"));
      long workTotal = total * 2, done = 0;
      var report = new List<string>();
      string Rel(string p) => p.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1);

      string Materialize(string path, long count, IEnumerable<byte[]> blocks, string label) {
        Check();
        bool exists = File.Exists(path);
        if (Directory.Exists(path) || (exists && new FileInfo(path).Length != count))
          throw new InvalidDataException(L.T("Destino diferente; preservado: ", "A different destination file; kept: ") + path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".part");
        status((exists ? L.T("Comparando ", "Comparing ") : label + " ") + Rel(path));
        try {
          byte[] digest;
          long actual = 0;
          using (var sha = SHA256.Create())
          using (var h = exists ? new FileStream(path, FileMode.Open, FileAccess.Read) : new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) {
            foreach (var blob in blocks) {
              Check();
              sha.TransformBlock(blob, 0, blob.Length, null, 0);
              if (exists) {
                var have = new byte[blob.Length];
                int got = 0;
                while (got < have.Length) { int n = h.Read(have, got, have.Length - got); if (n <= 0) break; got += n; }
                if (got != blob.Length || !have.SequenceEqual(blob)) throw new InvalidDataException(L.T("Destino com conteúdo diferente; preservado: ", "Destination holds different content; kept: ") + path);
              } else h.Write(blob, 0, blob.Length);
              actual += blob.Length;
              done += blob.Length;
              progress(done, workTotal);
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            digest = sha.Hash;
          }
          if (actual != count) throw new InvalidDataException(L.T("Tamanho da origem mudou: ", "The source size changed: ") + path);
          status(L.T("Verificando ", "Checking ") + Rel(path));
          using (var sha = SHA256.Create())
          using (var h = File.OpenRead(exists ? path : temporary)) {
            var buf = new byte[Block];
            int n;
            while ((n = h.Read(buf, 0, buf.Length)) > 0) {
              Check();
              sha.TransformBlock(buf, 0, n, null, 0);
              done += n;
              progress(done, workTotal);
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            if (!sha.Hash.SequenceEqual(digest)) throw new InvalidDataException(L.T("Falha na verificação: ", "Check failed: ") + path);
          }
          if (!exists) {
            if (File.Exists(path)) throw new InvalidDataException(L.T("Destino criado por outro processo: ", "Destination created by another process: ") + path);
            File.Move(temporary, path);
          }
          return Hex(digest);
        } finally {
          if (File.Exists(temporary)) File.Delete(temporary);
        }
      }

      foreach (var (entry, path) in targets) {
        string digest = Materialize(path, entry.Size, Chunks(entry), isIso ? L.T("Extraindo", "Extracting") : L.T("Copiando", "Copying"));
        report.Add("    {\"path\": " + Json.Write(entry.Path) + ", \"bytes\": " + entry.Size + ", \"sha256\": \"" + digest + "\"}");
      }
      if (filesystem == "iso9660") {
        status(L.T("Gerando índice de acesso aos arquivos extraídos…", "Writing the index of the extracted files…"));
        var (layout, metadata) = BuildLayout(entries);
        foreach (var (name, data) in new[] { (".disc/metadata.bin", metadata), (".disc/layout.tsv", layout) }) {
          string path = Target(output, name);
          Directory.CreateDirectory(Path.GetDirectoryName(path));
          string temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
          File.WriteAllBytes(temp, data);
          Replace(temp, path);
        }
      }
      foreach (var kv in states)
        if (Fingerprint(kv.Key) != kv.Value) throw new InvalidDataException(L.T("A origem mudou durante a instalação: ", "The source changed during the install: ") + kv.Key);
      Check();
      string gameName = config.TryGetValue("name", out var gn) ? Convert.ToString(gn) : "";
      string reportText = "{\n  \"game\": " + Json.Write(gameName) + ",\n  \"verified\": true,\n  \"files\": [\n" + string.Join(",\n", report) +
                          "\n  ],\n  \"discRetained\": false,\n  \"sourceDeleted\": false\n}\n";
      string reportPath = Path.Combine(root, "game-install-report.json");
      string tempReport = Path.Combine(root, ".install-report." + Guid.NewGuid().ToString("N"));
      File.WriteAllText(tempReport, reportText, new UTF8Encoding(false));
      Replace(tempReport, reportPath);
      var st = isIso ? states[source] : (0L, 0L);
      return new GameInstallResult { Source = source, ReportPath = reportPath, CanDeleteSource = isIso && !Inside(source, output),
                                     SourceSize = st.Item1, SourceTicks = st.Item2 };
    } finally {
      stream?.Dispose();
    }
  }

  // Deletes the disc image after the player confirmed it (only when it did not change since the install).
  public static void DeleteSource(GameInstallResult r) {
    var i = new FileInfo(r.Source);
    if (!r.CanDeleteSource || !i.Exists || i.Length != r.SourceSize || i.LastWriteTimeUtc.Ticks != r.SourceTicks)
      throw new IOException(L.T("A origem mudou ou não pode ser apagada", "The source changed or cannot be deleted"));
    i.Delete();
    try { File.WriteAllText(r.ReportPath, File.ReadAllText(r.ReportPath).Replace("\"sourceDeleted\": false", "\"sourceDeleted\": true"), new UTF8Encoding(false)); } catch (Exception) { }
  }

  static void Replace(string temp, string path) {
    if (File.Exists(path)) File.Delete(path);
    File.Move(temp, path);
  }

  static long DiskFree(string path) {
    try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; } catch (Exception) { return long.MaxValue; }
  }
}

}
