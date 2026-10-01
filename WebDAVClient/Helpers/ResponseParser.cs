using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using WebDAVClient.Model;

namespace WebDAVClient.Helpers
{
    internal static class ResponseParser
    {
        internal static readonly XmlReaderSettings XmlReaderSettings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        public static Item ParseItem(Stream stream) => ParseItems(stream).FirstOrDefault();

        public static IEnumerable<Item> ParseItems(Stream stream)
        {
            using var reader = XmlReader.Create(stream, XmlReaderSettings);
            return ParseItems(reader);
        }

        public static IEnumerable<Item> ParseItems(string text)
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, XmlReaderSettings);
            return ParseItems(reader);
        }

        private static IEnumerable<Item> ParseItems(XmlReader reader)
        {
            XDocument document = XDocument.Load(reader);
            var items = new List<Item>();
            foreach (XElement response in document.Descendants().Where(element => IsNamed(element, "response")))
            {
                string href = Child(response, "href")?.Value;
                if (string.IsNullOrWhiteSpace(href) || !IsSuccessful(Child(response, "status")?.Value))
                    continue;

                var item = new Item { Href = href.Trim().Replace("#", "%23") };
                // A multistatus response can contain both successful and failed
                // property groups. Missing/failed properties must not replace
                // values from a successful group.
                foreach (XElement propstat in response.Elements().Where(element => IsNamed(element, "propstat")))
                {
                    if (!IsSuccessful(Child(propstat, "status")?.Value))
                        continue;
                    XElement properties = Child(propstat, "prop");
                    if (properties == null)
                        continue;
                    foreach (XElement property in properties.Elements())
                        ReadProperty(item, property);
                }

                string namePath = item.Href.TrimEnd('/');
                if (!item.IsCollection)
                    item.Href = namePath;
                if (string.IsNullOrEmpty(item.DisplayName))
                    item.DisplayName = Uri.UnescapeDataString(namePath.Substring(namePath.LastIndexOf('/') + 1));
                items.Add(item);
            }
            return items;
        }

        private static void ReadProperty(Item item, XElement property)
        {
            string value = property.Value;
            switch (property.Name.LocalName.ToLowerInvariant())
            {
                case "creationdate":
                    if (TryDate(value, out DateTime created))
                        item.CreationDate = created;
                    break;
                case "getlastmodified":
                    if (TryDate(value, out DateTime modified))
                        item.LastModified = modified;
                    break;
                case "displayname":
                    if (!string.IsNullOrEmpty(value))
                        item.DisplayName = value;
                    break;
                case "getcontentlength":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long length) && length >= 0)
                        item.ContentLength = length;
                    break;
                case "getcontenttype":
                    item.ContentType = value;
                    break;
                case "getetag":
                    item.Etag = value;
                    break;
                case "iscollection":
                    item.IsCollection = ReadBoolean(value);
                    break;
                case "resourcetype":
                    item.IsCollection = property.Elements().Any(element => IsNamed(element, "collection"));
                    break;
                case "hidden":
                case "ishidden":
                    item.IsHidden = property.IsEmpty || ReadBoolean(value);
                    break;
            }
        }

        private static bool ReadBoolean(string value) => value.Trim() == "1"
            || bool.TryParse(value, out bool result) && result;

        private static bool TryDate(string value, out DateTime date) => DateTime.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);

        private static bool IsSuccessful(string status)
        {
            // Some compatible servers omit propstat/status entirely.
            if (string.IsNullOrWhiteSpace(status))
                return true;
            string[] parts = status.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && int.TryParse(parts[1], out int code) && code is >= 200 and < 300;
        }

        private static XElement Child(XElement parent, string name) =>
            parent.Elements().FirstOrDefault(element => IsNamed(element, name));

        private static bool IsNamed(XElement element, string name) =>
            element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
    }
}
