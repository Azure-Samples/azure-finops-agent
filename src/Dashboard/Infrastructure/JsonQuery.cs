using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.Exceptions;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzureFinOps.Dashboard.Infrastructure;

/// <summary>
/// Stateless cropping of one JSON response. Its CLR shape is inferred on the fly, so the model reads a compact
/// schema and sends a Dynamic LINQ expression (C# lambda syntax) that returns only the data it needs. Nothing is
/// stored. Only LINQ, string, math and date members are callable, every enumeration is metered, and the output is capped.
/// </summary>
internal static partial class JsonQuery
{
    internal const int MaxExpressionLength = 4000;
    private const long MaxSteps = 20_000_000;
    private const int MaxDepth = 16;
    private const int MaxMapKeys = 64;
    private const int MaxRepairs = 16;
    private const int MaxText = 65_536;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ParsingConfig Config = Settings();
    private static readonly ParsingConfig MathConfig = Settings(nullableMath: true);

    private static ParsingConfig Settings(bool nullableMath = false)
    {
        var config = new ParsingConfig
        {
            AllowNewToEvaluateAnyType = false,
            AllowEqualsAndToStringMethodsOnObject = true,
            IsCaseSensitive = true,
            DisableMemberAccessToIndexAccessorFallback = true,
            NumberParseCulture = CultureInfo.InvariantCulture,
            PrioritizePropertyOrFieldOverTheType = true,
        };
        if (nullableMath) config.ExpressionPromoter = new NullableArguments(config);
        return config;
    }

    // Lets a double? argument fill a double parameter. Used only after a Math call rejected one, because it would also turn
    // lifted double? arithmetic elsewhere in the query into plain double arithmetic.
    private sealed class NullableArguments(ParsingConfig config) : System.Linq.Dynamic.Core.Parser.ExpressionPromoter(config)
    {
        public override Expression? Promote(Expression source, Type type, bool exact, bool convertExpression) =>
            base.Promote(source, type, exact, convertExpression)
            ?? (exact || !type.IsValueType || Nullable.GetUnderlyingType(type) is not null || Nullable.GetUnderlyingType(source.Type) is not { } underlying ? null
                : base.Promote(Expression.Convert(source, underlying), type, exact, convertExpression));
    }

    /// <summary>A response body as a JSON tree: JSON as is, with column/row tables as objects; any other text as its lines.</summary>
    internal static JsonNode Parse(string body)
    {
        JsonNode? root = null;
        try { root = JsonNode.Parse(body); } catch (JsonException) { }
        return root is null ? new JsonObject { ["lines"] = new JsonArray([.. body.Split('\n').Select(line => (JsonNode?)line.TrimEnd('\r'))]) } : Tabulate(root);
    }

    /// <summary>The inferred shape as short indented lines, with a starter expression.</summary>
    internal static string Schema(JsonNode root, int maxLines = 120)
    {
        var shape = Infer(root);
        var lines = new List<string>();
        Render(shape, "it", 0, lines, maxLines);
        var example = Example(shape);
        return string.Join('\n', lines) + (example is null ? "" : $"\nExample: {example}");
    }

    /// <summary>The CLR type inferred for a response; responses of the same shape share one type.</summary>
    internal static Type Shape(JsonNode root) => Infer(root).Clr!;

    /// <summary>Evaluates the model's expression over the response root <c>it</c> and returns the result as JSON.</summary>
    internal static (string Json, string? Note) Evaluate(JsonNode root, string expression, int maxCharacters, CancellationToken cancellationToken)
    {
        if (expression.Length > MaxExpressionLength) throw new ArgumentException($"query is longer than {MaxExpressionLength} characters.");
        var text = Projections(expression);
        var numeric = NumericMembers().Matches(text).Select(match => match.Groups["m"].Value).ToHashSet(StringComparer.Ordinal);
        var shape = Infer(root, numeric);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var (lambda, parameter, absent) = ParseRepaired(shape, text, numeric);
            var instance = Materialize(root, shape);
            var budget = new Budget(cancellationToken);
            var body = new Hoist(parameter).Visit(new NullSafe().Visit(new Guard(budget).Visit(lambda.Body)))!;
            var compiled = Expression.Lambda(Expression.Convert(body, typeof(object)), parameter).Compile();
            var (json, note) = Serialize(compiled.DynamicInvoke(instance), maxCharacters, budget);
            return absent.Count == 0 ? (json, note) : (json, (note is null ? "" : note + "\n") + Absent(absent));
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            if (NullMath(error.InnerException)) throw NullMathError(error.InnerException);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error.InnerException);
            throw;
        }
        catch (InvalidOperationException error) when (NullMath(error))
        {
            throw NullMathError(error);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // A double? unwrapped for Math (MathConfig) was null in the data.
    private static bool NullMath(Exception error) =>
        error is InvalidOperationException && error.Message.StartsWith("Nullable object must have a value", StringComparison.Ordinal);

    private static InvalidOperationException NullMathError(Exception error) =>
        new("A null number was passed to Math; unwrap it with ?? (Math.Max(0, x.a ?? 0)).", error);

    // The shape is inferred from this one response, which omits null members and has no items in an empty list. When the
    // query reads a member this response lacks, or reads a keyed object as a collection, the shape widens to match (the
    // member reads as null) and the query parses again; the result names the absent members so a typo is not silent.
    private static (LambdaExpression Lambda, ParameterExpression Parameter, List<string> Absent) ParseRepaired(Node shape, string text, IReadOnlySet<string> numeric)
    {
        var absent = new List<string>();
        var config = Config;
        for (var attempt = 0; ; attempt++)
        {
            var parameter = Expression.Parameter(shape.Clr!, "it");
            try
            {
                return (DynamicExpressionParser.ParseLambda(config, [parameter], null, text), parameter, absent);
            }
            catch (ParseException error)
            {
                var message = error.Message;
                // A missing member followed by '.' is reported as an unknown type name; parsing only up to that member names its host type.
                if (NestedType().Match(message) is { Success: true } nested && Probe(config, text, nested.Groups["chain"].Value, error.Position, parameter) is { } probed)
                    message = probed;
                if (attempt < MaxRepairs && Widen(shape, message, absent)) { Resolve(shape, "it", numeric); continue; }
                // Math has no double? overloads; only then are double? arguments passed as double (a null one fails with a clear message).
                if (attempt < MaxRepairs && config == Config && MathOverload().IsMatch(message)) { config = MathConfig; continue; }
                if (MathOverload().IsMatch(message)) message += ". Numbers are double?; unwrap them for Math with ?? (Math.Max(0, (x.a ?? 0) - (x.b ?? 0)))";
                throw new ArgumentException(absent.Count == 0 ? message : $"{message}. {Absent(absent)}", error);
            }
        }
    }

    private static string? Probe(ParsingConfig config, string text, string chain, int position, ParameterExpression parameter)
    {
        var start = text[..Math.Clamp(position, 0, text.Length)].LastIndexOf(chain, StringComparison.Ordinal);
        if (start < 0) return null;
        try
        {
            DynamicExpressionParser.ParseLambda(config, [parameter], null, text[..(start + chain.Split('.')[0].Length)]);
            return null;
        }
        catch (ParseException error) when (MissingMember().IsMatch(error.Message))
        {
            return error.Message;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    private static string Absent(List<string> members) => $"Not in this response, so read as null: {string.Join(", ", members)}.";

    private static bool Widen(Node shape, string message, List<string> absent)
    {
        var changed = false;
        if (MissingMember().Match(message) is { Success: true } missing)
        {
            var member = missing.Groups["member"].Value;
            var type = missing.Groups["type"].Value;
            foreach (var (node, path) in Walk(shape, "it").ToList())
            {
                var observed = node.Members is not null && (node.Clr!.Name == type || node.Clr.FullName == type);
                if (!observed && !(type == "String" && node.Clr == typeof(string) && node.Unknown) || node.Properties.ContainsKey(member)
                    || node.Members?.Values.Any(known => known.Identifier.Equals(member, StringComparison.OrdinalIgnoreCase)) == true)
                    continue;
                node.Properties[member] = new Node();
                if (!node.Unknown) absent.Add($"{path}.{member}");
                changed = true;
            }
        }
        else if (NotCollection().Match(message) is { Success: true } method && CollectionMethods.Contains(method.Groups["method"].Value))
        {
            foreach (var (node, _) in Walk(shape, "it"))
                if (node.Members is not null && (node.Clr!.Name == method.Groups["type"].Value || node.Clr.FullName == method.Groups["type"].Value) && !node.ForceMap)
                {
                    node.ForceMap = true;
                    changed = true;
                }
        }
        return changed;
    }

    private static IEnumerable<(Node Node, string Path)> Walk(Node node, string path)
    {
        yield return (node, path);
        List<(Node Node, string Path)> children = node.IsMap ? node.Value is null ? [] : [(node.Value, path + "[key]")]
            : node.Arrays > 0 && node.Element is not null ? [(node.Element, path + "[]")]
            : node.Members?.Select(member => (node.Properties[member.Key], path + "." + member.Value.Identifier)).ToList() ?? [];
        foreach (var (child, childPath) in children)
            foreach (var item in Walk(child, childPath)) yield return item;
    }

    private static readonly HashSet<string> CollectionMethods = [.. typeof(Enumerable).GetMethods().Select(method => method.Name)];

    [System.Text.RegularExpressions.GeneratedRegex(@"No property or field '(?<member>\w+)' exists in type '(?<type>[^']+)'", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MissingMember();

    [System.Text.RegularExpressions.GeneratedRegex(@"No applicable (aggregate )?method '(?<method>\w+)' exists in type '(?<type>[^']+)'", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NotCollection();

    [System.Text.RegularExpressions.GeneratedRegex(@"No applicable method '\w+' exists in type 'Math'", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MathOverload();

    [System.Text.RegularExpressions.GeneratedRegex(@"^Type '(?<chain>\w+(\.\w+)*)' not found", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NestedType();

    // Member names the query uses as numbers: x.m ?? 0, x.m * 2, x.m > 5, 1 - x.m, Sum(r => r.m), Math.Round(x.m, ...).
    // A member never seen with a value (in an empty list, or null or absent throughout) takes double? when used this way.
    [System.Text.RegularExpressions.GeneratedRegex(@"\.(?<m>\w+)\s*(?:\?\?\s*[-+]?\d|[*/%-]|<=?|>=?|[!=]=\s*[-+]?\d)|(?:[*/%-]|<=?|(?<![=-])>=?)\s*\w+(?:\.\w+)*\.(?<m>\w+)\b(?!\s*\()|(?:Sum|Average|Min|Max)\(\s*\w+\s*=>\s*\w+(?:\.\w+)*\.(?<m>\w+)\s*\)|Math\.\w+\(\s*\(?\s*\w+(?:\.\w+)*\.(?<m>\w+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NumericMembers();

    /// <summary>Dynamic LINQ spells projections <c>new (expr as name)</c>; C#'s <c>new { name = expr }</c> is accepted too.</summary>
    internal static string Projections(string expression)
    {
        var output = new StringBuilder(expression.Length + 16);
        for (var index = 0; index < expression.Length;)
        {
            if (Literal(expression, index) is var end && end > index) { output.Append(expression, index, end - index); index = end; continue; }
            if (string.CompareOrdinal(expression, index, "new", 0, 3) == 0 && (index == 0 || !IsWord(expression[index - 1])))
            {
                var open = index + 3;
                while (open < expression.Length && char.IsWhiteSpace(expression[open])) open++;
                if (open < expression.Length && expression[open] is '{' or '(' && Close(expression, open) is var close && close > open)
                {
                    var members = Split(expression[(open + 1)..close]).Select(member =>
                    {
                        var text = Projections(member.Trim());
                        var equals = Assignment(text);
                        return equals > 0 ? $"({text[(equals + 1)..].Trim()}) as {text[..equals].Trim()}" : text;
                    });
                    output.Append("new (").AppendJoin(", ", members).Append(')');
                    index = close + 1;
                    continue;
                }
            }
            output.Append(expression[index++]);
        }
        return output.ToString();

        static bool IsWord(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

        // Index just after a string or char literal starting at index, or index when there is none.
        static int Literal(string text, int index)
        {
            if (text[index] is not ('"' or '\'')) return index;
            var quote = text[index];
            for (var at = index + 1; at < text.Length; at++)
            {
                if (text[at] == '\\') at++;
                else if (text[at] == quote) return at + 1;
            }
            return text.Length;
        }

        static int Close(string text, int open)
        {
            var depth = 0;
            for (var at = open; at < text.Length;)
            {
                if (Literal(text, at) is var end && end > at) { at = end; continue; }
                if (text[at] is '(' or '{' or '[') depth++;
                else if (text[at] is ')' or '}' or ']' && --depth == 0) return at;
                at++;
            }
            return -1;
        }

        static List<string> Split(string text)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var at = 0; at < text.Length;)
            {
                if (Literal(text, at) is var end && end > at) { at = end; continue; }
                if (text[at] is '(' or '{' or '[') depth++;
                else if (text[at] is ')' or '}' or ']') depth--;
                else if (text[at] == ',' && depth == 0) { parts.Add(text[start..at]); start = at + 1; }
                at++;
            }
            if (text[start..].Trim().Length > 0) parts.Add(text[start..]);
            return parts;
        }

        // "name = expr" (not ==, =>, <=, >= or !=) at the start of a member.
        static int Assignment(string member)
        {
            var at = 0;
            while (at < member.Length && IsWord(member[at])) at++;
            if (at == 0 || char.IsAsciiDigit(member[0])) return -1;
            while (at < member.Length && char.IsWhiteSpace(member[at])) at++;
            return at < member.Length - 1 && member[at] == '=' && member[at + 1] is not ('=' or '>') ? at : -1;
        }
    }

    // ---- shape inference -------------------------------------------------------------------------------------------

    private sealed class Node
    {
        public int Strings, Numbers, Bools, Objects, Arrays, Nulls;
        public long Items;
        public int MaxItems;
        public readonly Dictionary<string, Node> Properties = new(StringComparer.Ordinal);
        public Node? Element;
        public readonly HashSet<string> Distinct = new(StringComparer.Ordinal);
        public double Min = double.MaxValue, Max = double.MinValue;
        public Type? Clr;
        public Dictionary<string, (string Identifier, PropertyInfo Property)>? Members;
        public bool IsMap;
        public bool ForceMap;
        public Node? Value;

        // Nothing but null was seen here (an empty list's items, a null or absent member), so a query may read it as anything.
        public bool Unknown => Strings + Numbers + Bools + Objects + Arrays == 0;
    }

    private static Node Infer(JsonNode root, IReadOnlySet<string>? numeric = null)
    {
        var shape = new Node();
        Observe(root, shape, 0);
        Resolve(shape, "it", numeric ?? new HashSet<string>());
        return shape;
    }

    private static void Observe(JsonNode? value, Node node, int depth)
    {
        switch (value)
        {
            case null: node.Nulls++; break;
            case JsonObject item when depth < MaxDepth:
                node.Objects++;
                foreach (var (name, child) in item)
                {
                    if (!node.Properties.TryGetValue(name, out var property)) node.Properties[name] = property = new Node();
                    Observe(child, property, depth + 1);
                }
                break;
            case JsonArray list when depth < MaxDepth:
                node.Arrays++;
                node.Items += list.Count;
                node.MaxItems = Math.Max(node.MaxItems, list.Count);
                node.Element ??= new Node();
                foreach (var child in list) Observe(child, node.Element, depth + 1);
                break;
            case JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number:
                node.Numbers++;
                var number = scalar.GetValue<double>();
                node.Min = Math.Min(node.Min, number);
                node.Max = Math.Max(node.Max, number);
                break;
            case JsonValue scalar when scalar.GetValueKind() is JsonValueKind.True or JsonValueKind.False: node.Bools++; break;
            default:
                node.Strings++;
                var text = value is JsonValue { } s && s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>() : value.ToJsonString();
                if (node.Distinct.Count <= 12) node.Distinct.Add(text.Length > 60 ? text[..60] + "…" : text);
                break;
        }
    }

    private static void Resolve(Node node, string name, IReadOnlySet<string> numeric)
    {
        node.IsMap = false;
        node.Members = null;
        node.Value = null;
        var kinds = (node.Strings > 0 ? 1 : 0) + (node.Numbers > 0 ? 1 : 0) + (node.Bools > 0 ? 1 : 0) + (node.Objects > 0 ? 1 : 0) + (node.Arrays > 0 ? 1 : 0);
        if (node.Unknown && node.Properties.Count == 0) { node.Clr = numeric.Contains(name) ? typeof(double?) : typeof(string); return; }
        if (kinds > 1 || node.Strings > 0) { node.Clr = typeof(string); return; }
        if (node.Numbers > 0) { node.Clr = typeof(double?); return; }
        if (node.Bools > 0) { node.Clr = typeof(bool?); return; }
        if (node.Arrays > 0)
        {
            node.Element ??= new Node();
            Resolve(node.Element, name, numeric);
            node.Clr = typeof(List<>).MakeGenericType(node.Element.Clr!);
            return;
        }
        // Objects with open-ended keys (tags, more keys than a real type has, or read as a collection) are maps of their merged values.
        if (node.ForceMap || name is "tags" || node.Properties.Count is 0 or > MaxMapKeys)
        {
            var value = new Node();
            foreach (var child in node.Properties.Values) Merge(value, child);
            Resolve(value, name, numeric);
            node.IsMap = true;
            node.Value = value;
            node.Clr = typeof(Dictionary<,>).MakeGenericType(typeof(string), value.Clr!);
            return;
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var properties = new List<(string Json, string Identifier, Node Node)>();
        foreach (var (json, child) in node.Properties)
        {
            Resolve(child, json, numeric);
            var identifier = Identifier(json);
            while (!used.Add(identifier)) identifier += "_";
            properties.Add((json, identifier, child));
        }
        node.Clr = properties.Count == 0 ? typeof(DynamicClass)
            : DynamicClassFactory.CreateType([.. properties.Select(property => new DynamicProperty(property.Identifier, property.Node.Clr!))], createParameterCtor: false);
        node.Members = properties.ToDictionary(property => property.Json, property => (property.Identifier, node.Clr.GetProperty(property.Identifier)!), StringComparer.Ordinal);
    }

    private static void Merge(Node into, Node from)
    {
        into.Strings += from.Strings;
        into.Numbers += from.Numbers;
        into.Bools += from.Bools;
        into.Objects += from.Objects;
        into.Arrays += from.Arrays;
        into.Nulls += from.Nulls;
        into.Items += from.Items;
        into.MaxItems = Math.Max(into.MaxItems, from.MaxItems);
        into.Min = Math.Min(into.Min, from.Min);
        into.Max = Math.Max(into.Max, from.Max);
        foreach (var text in from.Distinct) { if (into.Distinct.Count <= 12) into.Distinct.Add(text); }
        foreach (var (name, child) in from.Properties)
        {
            if (!into.Properties.TryGetValue(name, out var property)) into.Properties[name] = property = new Node();
            Merge(property, child);
        }
        if (from.Element is not null) Merge(into.Element ??= new Node(), from.Element);
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "it", "parent", "root", "new", "iif", "np", "true", "false", "null", "and", "or", "not", "as", "is", "in", "mod", "eq", "ne", "lt", "le", "gt", "ge" };

    internal static string Identifier(string json)
    {
        var text = new StringBuilder(json.Length);
        foreach (var character in json) text.Append(char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_');
        if (text.Length == 0 || char.IsAsciiDigit(text[0]) || Keywords.Contains(text.ToString())) text.Insert(0, '_');
        return text.ToString();
    }

    private static object? Materialize(JsonNode? value, Node node)
    {
        if (value is null) return null;
        if (node.Clr == typeof(string))
            return value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : value.ToJsonString();
        if (node.Clr == typeof(double?)) return value is JsonValue number && number.GetValueKind() == JsonValueKind.Number ? number.GetValue<double>() : null;
        if (node.Clr == typeof(bool?)) return value is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? flag.GetValue<bool>() : null;
        if (value is JsonArray list && node.Element is not null)
        {
            var items = (IList)Activator.CreateInstance(node.Clr!)!;
            foreach (var child in list) items.Add(Materialize(child, node.Element));
            return items;
        }
        if (value is not JsonObject item) return null;
        if (node.IsMap)
        {
            var map = (IDictionary)Activator.CreateInstance(node.Clr!)!;
            foreach (var (name, child) in item) map[name] = Materialize(child, node.Value!);
            return map;
        }
        var instance = Activator.CreateInstance(node.Clr!)!;
        foreach (var (name, child) in item)
            if (node.Members!.TryGetValue(name, out var member)) member.Property.SetValue(instance, Materialize(child, node.Properties[name]));
        return instance;
    }

    // Column/row tables (Cost Management, Log Analytics, Resource Graph table format) become arrays of named objects.
    private static JsonNode Tabulate(JsonNode node)
    {
        if (node is JsonArray array)
            foreach (var child in array) { if (child is not null) Tabulate(child); }
        if (node is not JsonObject item) return node;
        if (item["columns"] is JsonArray columns && item["rows"] is JsonArray rows && columns.Count > 0
            && columns.All(column => column is JsonObject named && named["name"] is JsonValue) && rows.All(row => row is JsonArray))
        {
            var names = new List<string>();
            foreach (var column in columns)
            {
                var name = column!["name"]!.ToString();
                while (names.Contains(name, StringComparer.OrdinalIgnoreCase)) name += "_";
                names.Add(name);
            }
            item["rows"] = new JsonArray([.. rows.Select(row => (JsonNode?)new JsonObject(
                names.Select((name, index) => KeyValuePair.Create(name, ((JsonArray)row!).ElementAtOrDefault(index)?.DeepClone()))))]);
        }
        foreach (var (_, child) in item) { if (child is not null) Tabulate(child); }
        return node;
    }

    // ---- schema text -----------------------------------------------------------------------------------------------

    private static void Render(Node node, string name, int indent, List<string> lines, int maxLines)
    {
        if (lines.Count >= maxLines) return;
        var pad = new string(' ', indent * 2);
        var line = $"{pad}{name}: {TypeName(node)}{Detail(node)}";
        lines.Add(line);
        if (lines.Count >= maxLines) { lines.Add($"{pad}… schema truncated"); return; }
        var target = node.Element is not null && node.Arrays > 0 ? Unwrap(node) : node;
        if (target.IsMap && target.Value?.Members is not null) target = target.Value;
        if (target.Members is null) return;
        foreach (var (json, member) in target.Members)
        {
            var label = member.Identifier == json ? member.Identifier : $"{member.Identifier} (JSON \"{json}\")";
            Render(target.Properties[json], label, indent + 1, lines, maxLines);
        }
    }

    private static Node Unwrap(Node node)
    {
        while (node.Arrays > 0 && node.Element is not null) node = node.Element;
        return node;
    }

    private static string TypeName(Node node) =>
        node.Clr == typeof(string) ? "string" : node.Clr == typeof(double?) ? "double?" : node.Clr == typeof(bool?) ? "bool?"
        : node.IsMap ? $"Dictionary<string,{TypeName(node.Value!)}> (m[\"key\"] is null when absent; m.Select(p => p.Value))"
        : node.Arrays > 0 && node.Element is not null ? $"List<{(node.Element.Unknown ? "object" : TypeName(node.Element))}>" : "object";

    private static string Detail(Node node)
    {
        if (node.Arrays > 0) return node.Arrays == 1 ? $"  [{node.Items} items]" : $"  [up to {node.MaxItems} items]";
        if (node.Clr == typeof(double?) && node.Numbers > 0)
            return node.Min == node.Max ? $"  = {node.Min.ToString(CultureInfo.InvariantCulture)}" : $"  {node.Min.ToString(CultureInfo.InvariantCulture)}..{node.Max.ToString(CultureInfo.InvariantCulture)}";
        if (node.Clr != typeof(string) || node.Distinct.Count == 0) return node.Nulls > 0 && node.Strings == 0 ? "  (always null)" : "";
        var values = string.Join(", ", node.Distinct.Take(12).Select(value => JsonSerializer.Serialize(value)));
        return node.Distinct.Count <= 12 && node.Strings > node.Distinct.Count ? $"  one of {values}" : $"  e.g. {string.Join(", ", node.Distinct.Take(3).Select(value => JsonSerializer.Serialize(value)))}";
    }

    private static string? Example(Node root)
    {
        // The largest collection is where cropping pays; filter on its first text field and project two fields.
        (string Path, Node Node)? best = null;
        void Find(Node node, string path)
        {
            if (node.Arrays > 0 && node.Element?.Members is { Count: > 0 } && (best is null || node.Items > best.Value.Node.Items)) best = (path, node);
            if (node.Members is not null)
                foreach (var (json, member) in node.Members) Find(node.Properties[json], path + "." + member.Identifier);
        }
        Find(root, "it");
        if (best is not { } found) return null;
        var element = found.Node.Element!;
        var fields = element.Members!.Where(member => element.Properties[member.Key].Clr == typeof(string) || element.Properties[member.Key].Clr == typeof(double?))
            .Select(member => member.Value.Identifier).Take(2).ToArray();
        if (fields.Length == 0) return $"{found.Path}.Take(20)";
        return $"{found.Path}.Where(x => x.{fields[0]} != null).Select(x => new {{ {string.Join(", ", fields.Select(field => "x." + field))} }}).Take(20)";
    }

    // ---- evaluation -----------------------------------------------------------------------------------------------

    private static (string Json, string? Note) Serialize(object? result, int maxCharacters, Budget budget)
    {
        if (result is null or string || result is not IEnumerable sequence)
        {
            var node = JsonSerializer.SerializeToNode(Plain(result), Plain(result)?.GetType() ?? typeof(object));
            var single = node?.ToJsonString() ?? "null";
            if (single.Length <= maxCharacters) return (single, null);
            return Shrink(node!, maxCharacters) is { } cut
                ? (node!.ToJsonString(), $"Result truncated to the {maxCharacters / 1024} KB cap: {cut}. Narrow with Where, select fewer fields, or aggregate.")
                : throw new InvalidOperationException($"The result is {single.Length:N0} characters, over the {maxCharacters / 1024} KB cap; return a list, fewer fields or an aggregate.");
        }
        var text = new StringBuilder("[");
        var written = 0;
        var total = 0;
        foreach (var item in sequence)
        {
            budget.Step();
            total++;
            if (text.Length >= maxCharacters) continue;
            var json = JsonSerializer.Serialize(Plain(item), Plain(item)?.GetType() ?? typeof(object));
            if (text.Length + json.Length > maxCharacters) continue;
            if (written++ > 0) text.Append(',');
            text.Append(json);
        }
        text.Append(']');
        return (text.ToString(), written == total ? null
            : $"Result truncated: {written} of {total} items shown ({maxCharacters / 1024} KB cap). Narrow with Where, select fewer fields, or page with Skip/Take.");

        // A group serializes as its key and items rather than as a bare list.
        static object? Plain(object? value) =>
            value?.GetType().GetInterfaces().FirstOrDefault(face => face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IGrouping<,>)) is { } grouping
                ? new Dictionary<string, object?> { ["Key"] = grouping.GetProperty("Key")!.GetValue(value), ["Items"] = ((IEnumerable)value).Cast<object?>().ToList() }
                : value;
    }

    // An object result over the cap keeps its fields and trims its outermost lists from the end, largest first.
    private static string? Shrink(JsonNode node, int maxCharacters)
    {
        var lists = new List<(string Path, JsonArray List, int Count)>();
        Outermost(node, "result");
        var size = node.ToJsonString().Length;
        for (var round = 0; size > maxCharacters && round < 64; round++)
        {
            var sized = lists.Where(entry => entry.List.Count > 0).Select(entry => (entry.List, Size: entry.List.ToJsonString().Length)).ToList();
            if (sized.Count == 0) break;
            var (list, listSize) = sized.MaxBy(entry => entry.Size);
            var keep = (int)Math.Clamp((long)list.Count * (listSize - (size - maxCharacters)) / Math.Max(1, listSize) - 1, 0, list.Count - 1);
            while (list.Count > keep) list.RemoveAt(list.Count - 1);
            size = node.ToJsonString().Length;
        }
        return size > maxCharacters ? null
            : string.Join(", ", lists.Where(entry => entry.List.Count < entry.Count).Select(entry => $"{entry.Path} shows {entry.List.Count} of {entry.Count} items"));

        void Outermost(JsonNode? value, string path)
        {
            if (value is JsonArray list) lists.Add((path, list, list.Count));
            else if (value is JsonObject item)
                foreach (var (name, child) in item) Outermost(child, path + "." + name);
        }
    }

    /// <summary>The response's own coverage and provenance fields (completeness, paging, cache state, source), kept beside a cropped result.</summary>
    internal static JsonObject? Metadata(JsonNode root)
    {
        if (root is not JsonObject item) return null;
        var metadata = new JsonObject();
        foreach (var (name, value) in item)
            if (MetadataFields.Contains(name) && value is not null && (value is not JsonValue || value.ToJsonString().Length <= 600))
                metadata[name] = value.DeepClone();
        return metadata.Count == 0 ? null : metadata;
    }

    private static readonly HashSet<string> MetadataFields = new(StringComparer.Ordinal)
    {
        "complete", "partial", "pagesRead", "pages", "pageFailure", "count", "retrievedAtUtc", "source", "filter", "currencyCode",
        "nextLink", "@odata.nextLink", "NextMarker", "_finops", "_request", "_apiVersion", "sourceEvidence",
    };

    private sealed class Budget(CancellationToken cancellationToken)
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private long _steps;

        internal void Step()
        {
            if (++_steps > MaxSteps || (_steps & 1023) == 0 && (Stopwatch.GetElapsedTime(_start) > Deadline || cancellationToken.IsCancellationRequested))
                throw new TimeoutException("The query exceeded its evaluation budget. Avoid nested loops over the same list; filter first, then project.");
        }

        public string? Text(string? value)
        {
            if (value is { Length: > MaxText }) throw new InvalidOperationException($"A string in the query grew beyond {MaxText} characters.");
            _steps += (value?.Length ?? 0) / 64;
            Step();
            return value;
        }

        public IEnumerable<T> Watch<T>(IEnumerable<T>? source)
        {
            if (source is null) yield break;
            foreach (var item in source)
            {
                Step();
                yield return item;
            }
        }
    }

    // Only data access, LINQ operators and plain value helpers are callable; every LINQ source is metered.
    private sealed class Guard(Budget budget) : ExpressionVisitor
    {
        private static readonly HashSet<Type> ValueTypes =
        [
            typeof(string), typeof(Math), typeof(Convert), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid),
            typeof(bool), typeof(char), typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal),
            typeof(object), typeof(Enumerable), typeof(Queryable),
        ];

        private static readonly HashSet<string> Unbounded = ["PadLeft", "PadRight", "Format"];
        private static readonly System.Text.RegularExpressions.Regex LongDigits = new(@"\d{3}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static bool Allowed(Type? type) =>
            type is not null && (ValueTypes.Contains(type) || typeof(DynamicClass).IsAssignableFrom(type)
                || type.IsGenericType && type.GetGenericTypeDefinition() is var definition
                    && (definition == typeof(Nullable<>) || definition == typeof(List<>) || definition == typeof(Dictionary<,>)
                        || definition == typeof(KeyValuePair<,>) || definition == typeof(IGrouping<,>) || definition == typeof(IEnumerable<>)
                        || definition == typeof(ICollection<>) || definition == typeof(IList<>) || definition == typeof(IReadOnlyCollection<>))
                    && type.GetGenericArguments().All(Allowed));

        public override Expression? Visit(Expression? node)
        {
            if (node is not null && (typeof(MemberInfo).IsAssignableFrom(node.Type) || typeof(Delegate).IsAssignableFrom(node.Type) && node.NodeType != ExpressionType.Lambda
                || typeof(Assembly).IsAssignableFrom(node.Type)))
                throw new InvalidOperationException("Reflection is not available in a query.");
            return base.Visit(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var method = node.Method;
            if (!Allowed(method.DeclaringType) || Unbounded.Contains(method.Name) || method.Name == nameof(GetType)
                || method.DeclaringType == typeof(string) && method.IsStatic && method.GetParameters().Any(parameter => parameter.ParameterType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(parameter.ParameterType))
                || method.Name == nameof(string.Replace) && method.DeclaringType == typeof(string) && !(node.Arguments.Count == 2 && node.Arguments[1] is ConstantExpression { Value: string or char } replacement && $"{replacement.Value}".Length <= 64)
                || method.Name == nameof(ToString) && !method.IsStatic && node.Arguments.Any(argument => argument is not ConstantExpression { Value: string format } || LongDigits.IsMatch(format)))
                throw new InvalidOperationException($"{method.DeclaringType?.Name}.{method.Name} is not available in this form in a query.");
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            var parameters = method.GetParameters();
            var arguments = visited.Arguments.Select((argument, index) => Watch(argument, parameters[index].ParameterType)).ToArray();
            var call = arguments.SequenceEqual(visited.Arguments) ? visited : visited.Update(visited.Object, arguments);
            return call.Type == typeof(string) ? Expression.Call(Expression.Constant(budget), nameof(Budget.Text), null, call) : call;
        }

        // Every sequence handed to a method is enumerated through the step budget.
        private Expression Watch(Expression argument, Type parameterType)
        {
            if (argument.Type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(argument.Type)) return argument;
            var element = argument.Type.IsGenericType && argument.Type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? argument.Type.GetGenericArguments()[0]
                : argument.Type.GetInterfaces().FirstOrDefault(face => face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
            if (element is null) return argument;
            var watch = Expression.Call(Expression.Constant(budget), nameof(Budget.Watch), [element], argument);
            return parameterType.IsAssignableFrom(watch.Type) ? watch : argument;
        }

        protected override Expression VisitNewArray(NewArrayExpression node) =>
            node.NodeType == ExpressionType.NewArrayBounds ? throw new InvalidOperationException("Sized arrays are not available in a query.") : base.VisitNewArray(node);

        protected override Expression VisitMember(MemberExpression node)
        {
            if (!Allowed(node.Member.DeclaringType)) throw new InvalidOperationException($"{node.Member.DeclaringType?.Name}.{node.Member.Name} is not available in a query.");
            return base.VisitMember(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            if (!typeof(DynamicClass).IsAssignableFrom(node.Type) && !(node.Type.IsValueType && Allowed(node.Type)))
                throw new InvalidOperationException($"new {node.Type.Name} is not available in a query.");
            return base.VisitNew(node);
        }
    }

    // A call inside a lambda that reads only the response root (a share of root.value.Sum(...) per item) is evaluated
    // once, when first reached, instead of once per item, so the natural share-of-total query stays linear.
    private sealed class Hoist(ParameterExpression root) : ExpressionVisitor
    {
        private int _lambdas;

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            _lambdas++;
            try { return base.VisitLambda(node); }
            finally { _lambdas--; }
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (_lambdas == 0 || node.Type == typeof(void) || !Free.ReadsOnly(node, root)) return base.VisitMethodCall(node);
            var memo = Expression.Constant(new Memo());
            return Expression.Condition(Expression.Field(memo, nameof(Memo.Has)),
                Expression.Convert(Expression.Field(memo, nameof(Memo.Value)), node.Type),
                Expression.Call(memo, nameof(Memo.Set), [node.Type], node));
        }

        private sealed class Free(ParameterExpression root) : ExpressionVisitor
        {
            private readonly HashSet<ParameterExpression> _bound = [];
            private bool _root;
            private bool _other;

            internal static bool ReadsOnly(Expression node, ParameterExpression root)
            {
                var free = new Free(root);
                free.Visit(node);
                return free._root && !free._other;
            }

            protected override Expression VisitLambda<T>(Expression<T> node)
            {
                _bound.UnionWith(node.Parameters);
                return base.VisitLambda(node);
            }

            protected override Expression VisitBlock(BlockExpression node)
            {
                _bound.UnionWith(node.Variables);
                return base.VisitBlock(node);
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == root) _root = true;
                else if (!_bound.Contains(node)) _other = true;
                return node;
            }
        }
    }

    private sealed class Memo
    {
        public bool Has;
        public object? Value;

        public T Set<T>(T value)
        {
            Value = value;
            Has = true;
            return value;
        }
    }

    // Reads are as forgiving as JSON: a member, method or key read through a missing value is null (false or 0 for a
    // plain value), a missing key or index reads as null, and a missing list enumerates as empty (Budget.Watch).
    private sealed class NullSafe : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            var target = Visit(node.Expression);
            return Nullable(target) ? Guarded(target!, value => node.Update(value)) : node.Update(target);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var target = Visit(node.Object);
            var arguments = Visit(node.Arguments);
            if (target is not null && node.Method.Name == "get_Item" && arguments.Count == 1 && Lookup(target.Type, arguments[0].Type) is { } lookup)
                return Guarded(target, value => Expression.Call(lookup, value, arguments[0]));
            return Nullable(target) ? Guarded(target!, value => node.Update(value, arguments)) : node.Update(target, arguments);
        }

        protected override Expression VisitIndex(IndexExpression node)
        {
            var target = Visit(node.Object)!;
            var arguments = Visit(node.Arguments);
            if (arguments.Count == 1 && Lookup(target.Type, arguments[0].Type) is { } lookup)
                return Guarded(target, value => Expression.Call(lookup, value, arguments[0]));
            return Nullable(target) ? Guarded(target, value => node.Update(value, arguments)) : node.Update(target, arguments);
        }

        // Host constants (the budget, memos) are never null; value types cannot be.
        private static bool Nullable(Expression? target) => target is not null and not ConstantExpression && !target.Type.IsValueType;

        private static MethodInfo? Lookup(Type type, Type key)
        {
            if (!type.IsGenericType) return null;
            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();
            return definition == typeof(Dictionary<,>) && key == arguments[0] ? typeof(Reads).GetMethod(nameof(Reads.Key))!.MakeGenericMethod(arguments)
                : definition == typeof(List<>) && key == typeof(int) ? typeof(Reads).GetMethod(nameof(Reads.At))!.MakeGenericMethod(arguments)
                : null;
        }

        private static Expression Guarded(Expression target, Func<Expression, Expression> read)
        {
            var value = Expression.Variable(target.Type);
            var result = read(value);
            return Expression.Block(result.Type, [value], Expression.Assign(value, target),
                Expression.Condition(Expression.ReferenceEqual(value, Expression.Constant(null, target.Type)), Expression.Default(result.Type), result));
        }
    }

    private static class Reads
    {
        public static TValue? Key<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key) where TKey : notnull =>
            key is not null && map.TryGetValue(key, out var value) ? value : default;

        public static T? At<T>(List<T> list, int index) => index >= 0 && index < list.Count ? list[index] : default;
    }
}
