using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;

namespace WinWipeHelper
{
    /// <summary>Office Deployment Tool choices, saved on the office item in apps.json.</summary>
    public sealed class OfficeOptions
    {
        public string Product = "O365ProPlusRetail";
        public List<string> Apps = new List<string> { "Word", "Excel" };
        public string Language = "MatchOS";
        public string Edition = "64";
        public string Channel = "Current";

        public static readonly (string Id, string Label)[] Products =
        {
            ("O365ProPlusRetail", "Microsoft 365 Apps for enterprise"),
            ("O365BusinessRetail", "Microsoft 365 Business"),
            ("O365HomePremRetail", "Microsoft 365 Personal / Family"),
            ("Home2024Retail", "Office 2024 Home"),
            ("HomeBusiness2024Retail", "Office 2024 Home & Business"),
        };

        /// <summary>The apps you can pick. Anything not ticked becomes an ExcludeApp.</summary>
        public static readonly string[] AllApps = { "Word", "Excel", "PowerPoint", "Outlook", "OneNote", "Access", "Publisher", "Teams", "OneDrive" };

        // ODT ExcludeApp IDs for each choice. Outlook covers classic and new Outlook; OneDrive covers the old Groove client too.
        static readonly Dictionary<string, string[]> ExcludeIds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Word"] = new[] { "Word" },
            ["Excel"] = new[] { "Excel" },
            ["PowerPoint"] = new[] { "PowerPoint" },
            ["Outlook"] = new[] { "Outlook", "OutlookForWindows" },
            ["OneNote"] = new[] { "OneNote" },
            ["Access"] = new[] { "Access" },
            ["Publisher"] = new[] { "Publisher" },
            ["Teams"] = new[] { "Teams" },
            ["OneDrive"] = new[] { "OneDrive", "Groove" },
        };
        // Never wanted: Skype for Business and the Bing search add-in.
        static readonly string[] AlwaysExclude = { "Lync", "Bing" };

        public static readonly HashSet<string> ValidExcludeIds = new HashSet<string>(
            ExcludeIds.Values.SelectMany(v => v).Concat(AlwaysExclude), StringComparer.OrdinalIgnoreCase);

        public void Normalize()
        {
            if (!Products.Any(p => p.Id.Equals(Product, StringComparison.OrdinalIgnoreCase))) Product = "O365ProPlusRetail";
            Product = Products.First(p => p.Id.Equals(Product, StringComparison.OrdinalIgnoreCase)).Id;
            Apps = (Apps ?? new List<string>()).Select(a => AllApps.FirstOrDefault(x => x.Equals(a, StringComparison.OrdinalIgnoreCase)))
                                               .Where(a => a != null).Distinct().ToList();
            if (string.IsNullOrWhiteSpace(Language)) Language = "MatchOS";
            Edition = Edition == "32" ? "32" : "64";
            if (string.IsNullOrWhiteSpace(Channel)) Channel = "Current";
        }

        public string ProductLabel => Products.FirstOrDefault(p => p.Id == Product).Label ?? Product;

        public string Summary() => $"{ProductLabel}: {(Apps.Count > 0 ? string.Join(", ", Apps) : "no apps")}  ({Edition}-bit, language {Language})";

        public IEnumerable<string> ExcludedAppIds()
        {
            foreach (var app in AllApps)
                if (!Apps.Contains(app, StringComparer.OrdinalIgnoreCase))
                    foreach (var id in ExcludeIds[app]) yield return id;
            foreach (var id in AlwaysExclude) yield return id;
        }

        /// <summary>Builds the ODT configuration.xml.</summary>
        public string ToConfigurationXml()
        {
            Normalize();
            var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
            var sb = new StringBuilder();
            using (var w = XmlWriter.Create(sb, settings))
            {
                w.WriteStartElement("Configuration");
                w.WriteStartElement("Add");
                w.WriteAttributeString("OfficeClientEdition", Edition);
                w.WriteAttributeString("Channel", Channel);
                w.WriteStartElement("Product");
                w.WriteAttributeString("ID", Product);
                w.WriteStartElement("Language");
                w.WriteAttributeString("ID", Language);
                if (Language.Equals("MatchOS", StringComparison.OrdinalIgnoreCase)) w.WriteAttributeString("Fallback", "en-us");
                w.WriteEndElement();
                foreach (var id in ExcludedAppIds())
                {
                    w.WriteStartElement("ExcludeApp");
                    w.WriteAttributeString("ID", id);
                    w.WriteEndElement();
                }
                w.WriteEndElement(); // Product
                w.WriteEndElement(); // Add
                w.WriteStartElement("Updates");
                w.WriteAttributeString("Enabled", "TRUE");
                w.WriteEndElement();
                w.WriteStartElement("Display");
                w.WriteAttributeString("Level", "None");
                w.WriteAttributeString("AcceptEULA", "TRUE");
                w.WriteEndElement();
                w.WriteEndElement(); // Configuration
            }
            return sb.ToString();
        }

        /// <summary>Checks a configuration.xml the way the dry run reports it. Returns problems (empty = valid).</summary>
        public static List<string> Validate(string xml)
        {
            var problems = new List<string>();
            var doc = new XmlDocument();
            try { doc.LoadXml(xml); }
            catch (Exception ex) { problems.Add("Not well-formed XML: " + ex.Message); return problems; }
            var root = doc.DocumentElement;
            if (root?.Name != "Configuration") { problems.Add("Root element must be <Configuration>."); return problems; }
            var add = root.SelectSingleNode("Add") as XmlElement;
            if (add == null) { problems.Add("Missing <Add>."); return problems; }
            var ed = add.GetAttribute("OfficeClientEdition");
            if (ed != "64" && ed != "32") problems.Add("OfficeClientEdition must be 64 or 32.");
            var products = add.SelectNodes("Product");
            if (products.Count == 0) problems.Add("Missing <Product>.");
            foreach (XmlElement p in products)
            {
                var id = p.GetAttribute("ID");
                if (!Products.Any(x => x.Id == id)) problems.Add("Unknown product ID: " + id);
                if (p.SelectNodes("Language").Count == 0) problems.Add("Product has no <Language>.");
                foreach (XmlElement ex in p.SelectNodes("ExcludeApp"))
                    if (!ValidExcludeIds.Contains(ex.GetAttribute("ID"))) problems.Add("Unknown ExcludeApp ID: " + ex.GetAttribute("ID"));
            }
            var display = root.SelectSingleNode("Display") as XmlElement;
            if (display == null || display.GetAttribute("AcceptEULA") != "TRUE") problems.Add("<Display AcceptEULA=\"TRUE\"> is needed for a silent install.");
            return problems;
        }
    }
}
