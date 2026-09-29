using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Dynamic.Core;
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
internal static class JsonQuery
{
    internal const int MaxExpressionLength = 4000;
    private const long MaxSteps = 20_000_000;
    private const int MaxDepth = 16;
    private const int MaxMapKeys = 64;
    private const int MaxText = 65_536;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ParsingConfig Config = new()
    {
        AllowNewToEvaluateAnyType = false,
        AllowEqualsAndToStringMethodsOnObject = true,
        IsCaseSensitive = true,
        DisableMemberAccessToIndexAccessorFallback = true,
        NumberParseCulture = CultureInfo.InvariantCulture,
        PrioritizePropertyOrFieldOverTheType = true,
    };

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
        var shape = Infer(root);
        var instance = Materialize(root, shape);
        var parameter = Expression.Parameter(shape.Clr!, "it");
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var lambda = DynamicExpressionParser.ParseLambda(Config, [parameter], null, Projections(expression));
            var budget = new Budget(cancellationToken);
            var body = new Hoist(parameter).Visit(new Guard(budget).Visit(lambda.Body))!;
            var compiled = Expression.Lambda(Expression.Convert(body, typeof(object)), parameter).Compile();
            return Serialize(compiled.DynamicInvoke(instance), maxCharacters, budget);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error.InnerException);
            throw;
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

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
    }

    private static Node Infer(JsonNode root)
    {
        var shape = new Node();
        Observe(root, shape, 0);
        Resolve(shape, "it");
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

    private static void Resolve(Node node, string name)
    {
        var kinds = (node.Strings > 0 ? 1 : 0) + (node.Numbers > 0 ? 1 : 0) + (node.Bools > 0 ? 1 : 0) + (node.Objects > 0 ? 1 : 0) + (node.Arrays > 0 ? 1 : 0);
        if (kinds != 1 || node.Strings > 0) { node.Clr = typeof(string); return; }
        if (node.Numbers > 0) { node.Clr = typeof(double?); return; }
        if (node.Bools > 0) { node.Clr = typeof(bool?); return; }
        if (node.Arrays > 0)
        {
            if (node.Element is null || node.Element.Strings + node.Element.Numbers + node.Element.Bools + node.Element.Objects + node.Element.Arrays + node.Element.Nulls == 0)
                node.Element = new Node { Strings = 1 };
            Resolve(node.Element, name);
            node.Clr = typeof(List<>).MakeGenericType(node.Element.Clr!);
            return;
        }
        // Objects with open-ended keys (tags, or more keys than a real type has) are maps of text.
        if (name is "tags" || node.Properties.Count is 0 or > MaxMapKeys)
        {
            node.IsMap = true;
            node.Clr = typeof(Dictionary<string, string?>);
            return;
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var properties = new List<(string Json, string Identifier, Node Node)>();
        foreach (var (json, child) in node.Properties)
        {
            Resolve(child, json);
            var identifier = Identifier(json);
            while (!used.Add(identifier)) identifier += "_";
            properties.Add((json, identifier, child));
        }
        node.Clr = properties.Count == 0 ? typeof(DynamicClass)
            : DynamicClassFactory.CreateType([.. properties.Select(property => new DynamicProperty(property.Identifier, property.Node.Clr!))], createParameterCtor: false);
        node.Members = properties.ToDictionary(property => property.Json, property => (property.Identifier, node.Clr.GetProperty(property.Identifier)!), StringComparer.Ordinal);
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
            return item.ToDictionary(pair => pair.Key, pair => pair.Value is JsonValue text && text.GetValueKind() == JsonValueKind.String ? text.GetValue<string>() : pair.Value?.ToJsonString());
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
        : node.IsMap ? "Dictionary<string,string> (x.tags[\"key\"]; check ContainsKey first)"
        : node.Arrays > 0 && node.Element is not null ? $"List<{TypeName(node.Element)}>" : "object";

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
            var single = JsonSerializer.Serialize(Plain(result), Plain(result)?.GetType() ?? typeof(object));
            return single.Length <= maxCharacters ? (single, null)
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

        public IEnumerable<T> Watch<T>(IEnumerable<T> source)
        {
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
}
