using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace FEMDesignDumper
{
    /// <summary>
    /// Serializes result objects to JSON (full fidelity) and CSV (flat table,
    /// one column per public property/field) without knowing their concrete types.
    /// </summary>
    internal static class ResultWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void WriteJson(string path, object data)
        {
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(path, json, Utf8NoBom);
        }

        /// <summary>
        /// Writes rows as CSV using the runtime type of the first row for the header.
        /// Returns the number of data rows written.
        /// </summary>
        public static int WriteCsv(string path, IReadOnlyList<object> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                File.WriteAllText(path, string.Empty, Utf8NoBom);
                return 0;
            }

            List<Member> members = GetMembers(rows[0].GetType());

            var sb = new StringBuilder();
            sb.Append(string.Join(",", members.Select(m => Escape(m.Name)))).Append("\r\n");
            foreach (object row in rows)
                sb.Append(string.Join(",", members.Select(m => Escape(FormatValue(m.Get(row)))))).Append("\r\n");

            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
            return rows.Count;
        }

        private sealed class Member
        {
            public string Name;
            public Func<object, object> Get;
        }

        private static List<Member> GetMembers(Type t)
        {
            var members = new List<Member>();

            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead) continue;
                if (p.GetIndexParameters().Length > 0) continue;
                PropertyInfo pi = p;
                members.Add(new Member { Name = pi.Name, Get = o => SafeGet(() => pi.GetValue(o)) });
            }

            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                FieldInfo fi = f;
                members.Add(new Member { Name = fi.Name, Get = o => SafeGet(() => fi.GetValue(o)) });
            }

            return members;
        }

        private static object SafeGet(Func<object> get)
        {
            try { return get(); }
            catch { return null; }
        }

        private static string FormatValue(object v)
        {
            if (v == null) return string.Empty;

            switch (v)
            {
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case float fl: return fl.ToString("R", CultureInfo.InvariantCulture);
                case decimal m: return m.ToString(CultureInfo.InvariantCulture);
            }

            Type t = v.GetType();
            if (t.IsPrimitive && v is IFormattable prim)
                return prim.ToString(null, CultureInfo.InvariantCulture);
            if (t.IsEnum)
                return v.ToString();

            // Nested FemDesign objects or collections: keep them as inline JSON so
            // the CSV stays a single cell instead of spilling across columns.
            if (v is IEnumerable && !(v is string))
                return JsonConvert.SerializeObject(v);
            if (t.Namespace != null && t.Namespace.StartsWith("FemDesign", StringComparison.Ordinal))
                return JsonConvert.SerializeObject(v);

            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            bool needsQuote = s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0
                || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;
            if (!needsQuote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
