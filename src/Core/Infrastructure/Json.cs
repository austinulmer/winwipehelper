using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace WinWipeHelper
{
    /// <summary>Overrides the JSON name of a field (default: the field name in camelCase).</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class JsonNameAttribute : Attribute
    {
        public string Name { get; }
        public JsonNameAttribute(string name) { Name = name; }
    }

    /// <summary>Always write this field, even when it is false / empty.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class JsonAlwaysAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class JsonIgnoreAttribute : Attribute { }

    /// <summary>
    /// Small JSON helper built only on the framework. Reads with JavaScriptSerializer and writes
    /// clean, indented, hand-editable JSON (no ' or \/ escapes). Maps public fields.
    /// </summary>
    public static class Json
    {
        static JavaScriptSerializer NewReader() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

        /// <summary>Parses into Dictionary&lt;string,object&gt; / object[] / primitives.</summary>
        public static object Parse(string text) => NewReader().DeserializeObject(text);

        public static T Deserialize<T>(string text) => (T)ConvertTo(Parse(text), typeof(T));

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            sb.Append("\r\n");
            return sb.ToString();
        }

        // ---- helpers for ad-hoc parsed JSON ------------------------------------------------
        public static object Get(object node, params string[] path)
        {
            foreach (var key in path)
            {
                if (node is Dictionary<string, object> d)
                {
                    object v = null;
                    foreach (var kv in d) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { v = kv.Value; break; }
                    node = v;
                }
                else return null;
            }
            return node;
        }
        public static string Str(object node, params string[] path) => Get(node, path) is object o ? Convert.ToString(o, CultureInfo.InvariantCulture) : null;
        public static IEnumerable<object> Arr(object node, params string[] path) => (Get(node, path) as object[]) ?? (Get(node, path) as ArrayList)?.Cast<object>() ?? Enumerable.Empty<object>();

        // ---- reading into typed objects ---------------------------------------------------
        static string NameOf(FieldInfo f)
        {
            var a = f.GetCustomAttribute<JsonNameAttribute>();
            if (a != null) return a.Name;
            return char.ToLowerInvariant(f.Name[0]) + f.Name.Substring(1);
        }

        static IEnumerable<FieldInfo> FieldsOf(Type t) =>
            t.GetFields(BindingFlags.Public | BindingFlags.Instance)
             .Where(f => !f.IsInitOnly && f.GetCustomAttribute<JsonIgnoreAttribute>() == null)
             .OrderBy(f => f.MetadataToken);

        public static object ConvertTo(object v, Type t)
        {
            if (t == typeof(object)) return v;
            var nullable = Nullable.GetUnderlyingType(t);
            if (v == null) return t.IsValueType && nullable == null ? Activator.CreateInstance(t) : null;
            if (nullable != null) t = nullable;

            if (t == typeof(string)) return v is string s ? s : Convert.ToString(v, CultureInfo.InvariantCulture);
            if (t == typeof(bool))
            {
                if (v is bool b) return b;
                if (v is string bs) return bs.Equals("true", StringComparison.OrdinalIgnoreCase) || bs == "1";
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0; } catch { return false; }
            }
            if (t.IsEnum)
            {
                try { return Enum.Parse(t, Convert.ToString(v, CultureInfo.InvariantCulture), true); }
                catch { return Activator.CreateInstance(t); }
            }
            if (t.IsPrimitive || t == typeof(decimal))
            {
                try { return Convert.ChangeType(v, t, CultureInfo.InvariantCulture); }
                catch { return Activator.CreateInstance(t); }
            }
            if (t == typeof(DateTime))
            {
                return DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : default;
            }

            if (t.IsArray)
            {
                var et = t.GetElementType();
                var src = AsList(v);
                var arr = Array.CreateInstance(et, src.Count);
                for (int i = 0; i < src.Count; i++) arr.SetValue(ConvertTo(src[i], et), i);
                return arr;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var et = t.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(t);
                foreach (var item in AsList(v)) list.Add(ConvertTo(item, et));
                return list;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var vt = t.GetGenericArguments()[1];
                var dict = (IDictionary)Activator.CreateInstance(t, StringComparer.OrdinalIgnoreCase);
                if (v is Dictionary<string, object> src)
                    foreach (var kv in src) dict[kv.Key] = ConvertTo(kv.Value, vt);
                return dict;
            }

            var obj = Activator.CreateInstance(t, true);
            if (v is Dictionary<string, object> map)
            {
                var lookup = new Dictionary<string, object>(map, StringComparer.OrdinalIgnoreCase);
                foreach (var f in FieldsOf(t))
                {
                    if (lookup.TryGetValue(NameOf(f), out var fv) || lookup.TryGetValue(f.Name, out fv))
                        f.SetValue(obj, ConvertTo(fv, f.FieldType));
                }
            }
            return obj;
        }

        static IList AsList(object v)
        {
            if (v is object[] a) return a;
            if (v is ArrayList al) return al;
            if (v is IList l) return l;
            return new object[] { v };
        }

        // ---- writing ----------------------------------------------------------------------
        static bool IsSimple(object v) => v == null || v is string || v is bool || v.GetType().IsPrimitive || v is decimal || v is Enum;

        static void Write(StringBuilder sb, object v, int indent)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case DateTime dt: WriteString(sb, dt.ToString("s", CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
                case float fl: sb.Append(fl.ToString("R", CultureInfo.InvariantCulture)); return;
            }
            var t = v.GetType();
            if (t.IsPrimitive || v is decimal) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }

            string pad = new string(' ', (indent + 1) * 2), endPad = new string(' ', indent * 2);

            if (v is IDictionary dict)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\r\n");
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(",\r\n");
                    first = false;
                    sb.Append(pad); WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture)); sb.Append(": ");
                    Write(sb, kv.Value, indent + 1);
                }
                sb.Append("\r\n").Append(endPad).Append('}');
                return;
            }
            if (v is IEnumerable seq)
            {
                var items = seq.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                if (items.All(IsSimple))
                {
                    // Short lists of simple values stay on one line, e.g. "apps": ["Word", "Excel"]
                    var inline = new StringBuilder("[");
                    for (int i = 0; i < items.Count; i++) { if (i > 0) inline.Append(", "); Write(inline, items[i], 0); }
                    inline.Append(']');
                    if (inline.Length <= 100) { sb.Append(inline); return; }
                }
                sb.Append("[\r\n");
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(",\r\n");
                    sb.Append(pad);
                    Write(sb, items[i], indent + 1);
                }
                sb.Append("\r\n").Append(endPad).Append(']');
                return;
            }

            // Object: public fields, declaration order. Skips null / "" / false / empty unless [JsonAlways].
            var parts = new List<KeyValuePair<string, object>>();
            foreach (var f in FieldsOf(t))
            {
                var fv = f.GetValue(v);
                bool always = f.GetCustomAttribute<JsonAlwaysAttribute>() != null;
                if (!always)
                {
                    if (fv == null) continue;
                    if (fv is string fs && fs.Length == 0) continue;
                    if (fv is bool fb && !fb) continue;
                    if (fv is ICollection c && c.Count == 0) continue;
                }
                parts.Add(new KeyValuePair<string, object>(NameOf(f), fv));
            }
            if (parts.Count == 0) { sb.Append("{}"); return; }

            // Small objects of simple values go on one line (e.g. manual links), others are expanded.
            if (parts.All(p => IsSimple(p.Value)))
            {
                var inline = new StringBuilder("{ ");
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0) inline.Append(", ");
                    WriteString(inline, parts[i].Key); inline.Append(": "); Write(inline, parts[i].Value, 0);
                }
                inline.Append(" }");
                if (inline.Length + indent * 2 <= 160) { sb.Append(inline); return; }
            }
            sb.Append("{\r\n");
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(",\r\n");
                sb.Append(pad); WriteString(sb, parts[i].Key); sb.Append(": ");
                Write(sb, parts[i].Value, indent + 1);
            }
            sb.Append("\r\n").Append(endPad).Append('}');
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
