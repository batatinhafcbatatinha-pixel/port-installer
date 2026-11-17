// A small JSON reader and writer shared by the Windows launcher (.NET Framework) and the Linux/macOS launcher
// (.NET, compiled ahead of time). It gives the shapes the launcher always used: objects as
// IDictionary<string, object>, arrays as object[], numbers as int, long or decimal (double for exponents),
// true/false, strings and null.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PortLauncher {

static class Json {
  public static object Parse(string text) {
    if (text == null) throw new ArgumentNullException(nameof(text));
    var r = new Reader(text);
    r.Space();
    var v = r.Value();
    r.Space();
    if (!r.End) throw r.Error("text after the value");
    return v;
  }

  public static IDictionary<string, object> Object(string text) => Parse(text) as IDictionary<string, object>;

  sealed class Reader {
    readonly string s;
    int i;
    public Reader(string text) { s = text; if (s.Length > 0 && s[0] == '﻿') i = 1; }
    public bool End => i >= s.Length;
    public FormatException Error(string what) => new FormatException("JSON: " + what + " at " + i);

    public void Space() { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) ++i; }

    public object Value() {
      if (End) throw Error("unexpected end");
      char c = s[i];
      switch (c) {
        case '{': return Obj();
        case '[': return Arr();
        case '"': return Str();
        case 't': Word("true"); return true;
        case 'f': Word("false"); return false;
        case 'n': Word("null"); return null;
        default:
          if (c == '-' || (c >= '0' && c <= '9')) return Num();
          throw Error("unexpected '" + c + "'");
      }
    }

    void Word(string w) {
      if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) throw Error("expected " + w);
      i += w.Length;
    }

    IDictionary<string, object> Obj() {
      var d = new Dictionary<string, object>();
      ++i;
      Space();
      if (!End && s[i] == '}') { ++i; return d; }
      while (true) {
        Space();
        if (End || s[i] != '"') throw Error("expected a key");
        string k = Str();
        Space();
        if (End || s[i] != ':') throw Error("expected ':'");
        ++i;
        Space();
        d[k] = Value();
        Space();
        if (End) throw Error("unexpected end");
        if (s[i] == ',') { ++i; continue; }
        if (s[i] == '}') { ++i; return d; }
        throw Error("expected ',' or '}'");
      }
    }

    object[] Arr() {
      var list = new List<object>();
      ++i;
      Space();
      if (!End && s[i] == ']') { ++i; return list.ToArray(); }
      while (true) {
        Space();
        list.Add(Value());
        Space();
        if (End) throw Error("unexpected end");
        if (s[i] == ',') { ++i; continue; }
        if (s[i] == ']') { ++i; return list.ToArray(); }
        throw Error("expected ',' or ']'");
      }
    }

    string Str() {
      ++i;
      var b = new StringBuilder();
      while (true) {
        if (End) throw Error("unterminated string");
        char c = s[i++];
        if (c == '"') return b.ToString();
        if (c != '\\') { b.Append(c); continue; }
        if (End) throw Error("unterminated string");
        char e = s[i++];
        switch (e) {
          case '"': b.Append('"'); break;
          case '\\': b.Append('\\'); break;
          case '/': b.Append('/'); break;
          case 'b': b.Append('\b'); break;
          case 'f': b.Append('\f'); break;
          case 'n': b.Append('\n'); break;
          case 'r': b.Append('\r'); break;
          case 't': b.Append('\t'); break;
          case 'u':
            if (i + 4 > s.Length) throw Error("bad \\u escape");
            b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            i += 4;
            break;
          default: throw Error("bad escape");
        }
      }
    }

    object Num() {
      int start = i;
      if (s[i] == '-') ++i;
      while (i < s.Length && char.IsDigit(s[i])) ++i;
      bool fraction = false, exponent = false;
      if (i < s.Length && s[i] == '.') { fraction = true; ++i; while (i < s.Length && char.IsDigit(s[i])) ++i; }
      if (i < s.Length && (s[i] == 'e' || s[i] == 'E')) {
        exponent = true; ++i;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) ++i;
        while (i < s.Length && char.IsDigit(s[i])) ++i;
      }
      string t = s.Substring(start, i - start);
      if (exponent) return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
      if (!fraction) {
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return n;
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
      }
      return decimal.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
  }

  // ------------------------------------------------------------------ writing (compact, like JavaScriptSerializer)

  public static string Write(object value) {
    var b = new StringBuilder();
    Write(b, value);
    return b.ToString();
  }

  static void Write(StringBuilder b, object v) {
    switch (v) {
      case null: b.Append("null"); return;
      case string s: Quote(b, s); return;
      case bool x: b.Append(x ? "true" : "false"); return;
      case int n: b.Append(n.ToString(CultureInfo.InvariantCulture)); return;
      case long n: b.Append(n.ToString(CultureInfo.InvariantCulture)); return;
      case decimal n: b.Append(n.ToString(CultureInfo.InvariantCulture)); return;
      case double n: b.Append(n.ToString("R", CultureInfo.InvariantCulture)); return;
      case float n: b.Append(n.ToString("R", CultureInfo.InvariantCulture)); return;
      case IDictionary<string, object> d: {
        b.Append('{');
        bool first = true;
        foreach (var kv in d) {
          if (!first) b.Append(',');
          first = false;
          Quote(b, kv.Key);
          b.Append(':');
          Write(b, kv.Value);
        }
        b.Append('}');
        return;
      }
      case IEnumerable e: {
        b.Append('[');
        bool first = true;
        foreach (var x in e) {
          if (!first) b.Append(',');
          first = false;
          Write(b, x);
        }
        b.Append(']');
        return;
      }
      default:
        if (v is IConvertible c) { b.Append(Convert.ToString(c, CultureInfo.InvariantCulture)); return; }
        Quote(b, v.ToString());
        return;
    }
  }

  static void Quote(StringBuilder b, string s) {
    b.Append('"');
    foreach (char c in s) {
      switch (c) {
        case '"': b.Append("\\\""); break;
        case '\\': b.Append("\\\\"); break;
        case '\n': b.Append("\\n"); break;
        case '\r': b.Append("\\r"); break;
        case '\t': b.Append("\\t"); break;
        case '\b': b.Append("\\b"); break;
        case '\f': b.Append("\\f"); break;
        default:
          if (c < 0x20 || c == '<' || c == '>' || c == '&' || c == '\'') b.Append("\\u").Append(((int)c).ToString("x4"));
          else b.Append(c);
          break;
      }
    }
    b.Append('"');
  }
}

}
