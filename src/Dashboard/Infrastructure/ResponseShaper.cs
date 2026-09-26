using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AzureFinOps.Dashboard.Infrastructure;

/// <summary>
/// Converts successful non-JSON API bodies (CSV reports and exports, XML listings and feeds) into
/// JSON so every result can be retained, schema-discovered and queried the same way. Error bodies
/// and JSON pass through unchanged; nothing is summarized or reinterpreted.
/// </summary>
internal static partial class ResponseShaper
{
    internal const string TimestampPrefix = "Current UTC time: ";

    internal static string TimestampLine() =>
        TimestampPrefix + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\n";

    internal static (string Preamble, string Body) SplitPreamble(string response)
    {
        var body = response;
        var preamble = new StringBuilder();
        var first = true;
        while (first && body.StartsWith("HTTP ", StringComparison.Ordinal) || body.StartsWith(TimestampPrefix, StringComparison.Ordinal))
        {
            first = false;
            var end = body.IndexOf('\n');
            if (end < 0) return (preamble.Append(body).Append('\n').ToString(), "");
            preamble.Append(body, 0, end + 1);
            body = body[(end + 1)..];
        }
        return (preamble.ToString(), body);
    }

    /// <summary>Returns the response with a CSV or XML success body converted to JSON, otherwise unchanged.</summary>
    internal static string Normalize(string response, bool truncated = false)
    {
        var (preamble, body) = SplitPreamble(response);
        if (!preamble.StartsWith("HTTP 2", StringComparison.Ordinal)) return response;
        var text = body.TrimStart('\uFEFF');
        var lead = text.TrimStart();
        if (lead.Length == 0 || lead[0] is '{' or '[') return response;
        var converted = lead[0] == '<' ? XmlToJson(lead) : CsvToJson(text, truncated);
        return converted is null ? response : preamble + converted;
    }

    /// <summary>
    /// RFC 4180 CSV to a columnar JSON table: columns [{name,type}] and rows of typed values, the same
    /// shape as Cost Management, Log Analytics and Resource Graph tables, so rows are queryable by column name.
    /// </summary>
    internal static string? CsvToJson(string text, bool truncated = false)
    {
        var records = ParseCsv(text.TrimStart('\uFEFF'));
        if (truncated && records.Count > 1) records.RemoveAt(records.Count - 1);
        while (records.Count > 0 && records[^1] is [""]) records.RemoveAt(records.Count - 1);
        if (records.Count == 0) return null;
        var header = records[0];
        if (header.Count < 2) return null;
        var rows = records.Skip(1).ToList();
        if (rows.Count > 0 && rows.Count(row => row.Count == header.Count) < rows.Count * 0.9) return null;

        var names = new List<string>();
        foreach (var raw in header)
        {
            var name = string.IsNullOrWhiteSpace(raw) ? "column" + (names.Count + 1) : raw.Trim();
            var unique = name;
            for (var suffix = 2; names.Contains(unique, StringComparer.Ordinal); suffix++) unique = name + "_" + suffix;
            names.Add(unique);
        }
        var numeric = Enumerable.Range(0, names.Count).Select(column =>
        {
            var observed = false;
            foreach (var row in rows)
            {
                if (column >= row.Count || row[column].Length == 0) continue;
                if (!IsNumber(row[column])) return false;
                observed = true;
            }
            return observed;
        }).ToArray();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "csv");
            writer.WriteStartArray("columns");
            for (var column = 0; column < names.Count; column++)
            {
                writer.WriteStartObject();
                writer.WriteString("name", names[column]);
                writer.WriteString("type", numeric[column] ? "number" : "string");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("rowCount", rows.Count);
            writer.WriteBoolean("complete", !truncated);
            if (truncated) writer.WriteString("note", "Only the first part of the source was read; rows cover that part only, so totals are partial.");
            writer.WriteStartArray("rows");
            foreach (var row in rows)
            {
                writer.WriteStartArray();
                for (var column = 0; column < names.Count; column++)
                {
                    var value = column < row.Count ? row[column] : "";
                    if (numeric[column])
                    {
                        if (value.Length == 0) writer.WriteNullValue();
                        else writer.WriteRawValue(value.Trim(), skipInputValidation: false);
                    }
                    else writer.WriteStringValue(value);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsNumber(string value) => NumberPattern().IsMatch(value.Trim());

    // Plain decimals only: identifiers with leading zeros, GUIDs, dates and 16+ digit IDs stay strings.
    [GeneratedRegex(@"^-?(?:0|[1-9]\d{0,14})(?:\.\d+)?(?:[eE][+-]?\d{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character != '"') { field.Append(character); continue; }
                if (index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; continue; }
                quoted = false;
                continue;
            }
            switch (character)
            {
                case '"' when field.Length == 0: quoted = true; break;
                case ',': record.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    record.Add(field.ToString()); field.Clear();
                    records.Add(record); record = [];
                    break;
                default: field.Append(character); break;
            }
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }

    /// <summary>XML (blob listings, RSS/Atom feeds) to JSON; attributes become @name and text with attributes #text. DTDs are rejected.</summary>
    internal static string? XmlToJson(string text)
    {
        if (HtmlStart().IsMatch(text)) return null;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = 32_000_000,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var document = XDocument.Load(reader);
            if (document.Root is null) return null;
            var json = Newtonsoft.Json.JsonConvert.SerializeXNode(document.Root, Newtonsoft.Json.Formatting.None, omitRootObject: false);
            return json.StartsWith('{') && json.Length > 2 ? "{\"format\":\"xml\"," + json[1..] : null;
        }
        catch (Exception exception) when (exception is XmlException or Newtonsoft.Json.JsonException or InvalidOperationException) { return null; }
    }

    [GeneratedRegex(@"^\s*(?:<!doctype\s+html|<html[\s>])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlStart();
}
