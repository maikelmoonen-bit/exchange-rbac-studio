using System.Text;

namespace ExchangeRbacStudio.Domain.Scoping;

public enum FilterRisk
{
    /// <summary>Filter selects recipients only by immutable identifiers.</summary>
    ExactIdentity,
    Low,
    Moderate,
    High,
    /// <summary>The filter could not be parsed; its effect is unknown.</summary>
    Unknown,
}

public sealed record FilterRiskFlag(string Code, FilterRisk Risk, string Description);

public sealed record FilterAnalysis(
    string? Filter,
    bool IsParsed,
    string? ParseError,
    FilterRisk OverallRisk,
    IReadOnlyList<FilterRiskFlag> Flags,
    RecipientFilterStrategy? ExactStrategy,
    IReadOnlyList<Guid> ExactIdentities,
    IReadOnlyList<string> Properties)
{
    public bool IsExactIdentityFilter => ExactStrategy is not null;
}

/// <summary>
/// Lightweight OPATH parser used to explain existing management scope filters and detect conditions that can
/// silently widen a scope (wildcards, negations, mutable attributes, group membership, proxy address matching).
/// The parser is a static analysis aid only; the authoritative membership check is always performed by Exchange.
/// </summary>
public static class RecipientFilterAnalyzer
{
    private static readonly HashSet<string> ComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "-eq", "-ne", "-like", "-notlike", "-gt", "-ge", "-lt", "-le", "-ceq", "-cne", "-clike", "-cnotlike",
        "-ieq", "-ine", "-ilike", "-inotlike",
    };

    private static readonly HashSet<string> MutableAttributeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Department", "Company", "Office", "City", "StateOrProvince", "CountryOrRegion", "UsageLocation", "Title",
        "Manager", "PostalCode", "StreetAddress", "Phone", "Notes", "Initials", "FirstName", "LastName", "Description",
        "CustomAttribute1", "CustomAttribute2", "CustomAttribute3", "CustomAttribute4", "CustomAttribute5",
        "CustomAttribute6", "CustomAttribute7", "CustomAttribute8", "CustomAttribute9", "CustomAttribute10",
        "CustomAttribute11", "CustomAttribute12", "CustomAttribute13", "CustomAttribute14", "CustomAttribute15",
        "ExtensionCustomAttribute1", "ExtensionCustomAttribute2", "ExtensionCustomAttribute3",
        "ExtensionCustomAttribute4", "ExtensionCustomAttribute5",
    };

    private static readonly HashSet<string> NameProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alias", "Name", "DisplayName", "UserPrincipalName", "WindowsLiveID", "SamAccountName",
    };

    private static readonly HashSet<string> AddressProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "PrimarySmtpAddress", "EmailAddresses", "WindowsEmailAddress", "ExternalEmailAddress",
    };

    private static readonly HashSet<string> TypeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "RecipientType", "RecipientTypeDetails", "ObjectClass", "ObjectCategory",
    };

    private static readonly Dictionary<string, RecipientFilterStrategy> IdentityProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ExternalDirectoryObjectId"] = RecipientFilterStrategy.ExternalDirectoryObjectId,
        ["Guid"] = RecipientFilterStrategy.ObjectGuid,
        ["ExchangeGuid"] = RecipientFilterStrategy.ExchangeGuid,
    };

    public static FilterAnalysis Analyze(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return new FilterAnalysis(filter, true, null, FilterRisk.High,
                [new FilterRiskFlag("EMPTY_FILTER", FilterRisk.High, "The scope has no recipient restriction filter, so it can include every recipient (subject to its recipient root).")],
                null, [], []);
        }

        FilterNode root;
        try
        {
            var tokens = Tokenize(filter);
            var parser = new Parser(tokens);
            root = parser.ParseExpression();
            parser.ExpectEnd();
        }
        catch (FilterParseException ex)
        {
            return new FilterAnalysis(filter, false, ex.Message, FilterRisk.Unknown,
                [new FilterRiskFlag("UNPARSEABLE", FilterRisk.Unknown, $"The filter could not be analyzed ({ex.Message}). Its effective membership must be verified in Exchange.")],
                null, [], []);
        }

        var comparisons = new List<ComparisonNode>();
        var negated = false;
        Collect(root, comparisons, ref negated);

        var flags = new List<FilterRiskFlag>();
        var properties = comparisons.Select(c => c.Property).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var (exactStrategy, exactIds) = TryExtractExactIdentity(root);
        if (exactStrategy is not null)
        {
            return new FilterAnalysis(filter, true, null, FilterRisk.ExactIdentity,
                [new FilterRiskFlag("EXACT_IDENTITY", FilterRisk.ExactIdentity, $"Matches {exactIds.Count} recipient(s) by immutable identifier ({RecipientFilterBuilder.PropertyName(exactStrategy.Value)}).")],
                exactStrategy, exactIds, properties);
        }

        if (negated || comparisons.Any(c => c.Operator.Contains("ne", StringComparison.OrdinalIgnoreCase) || c.Operator.Contains("not", StringComparison.OrdinalIgnoreCase)))
            flags.Add(new("NEGATION", FilterRisk.High, "Uses negation (-ne, -notlike or -not). Negative filters include every recipient that does not match, including recipients created in the future."));

        if (comparisons.Any(c => c.Value.Contains('*')))
            flags.Add(new("WILDCARD", FilterRisk.High, "Uses wildcard matching. New or renamed recipients that match the pattern join the scope automatically."));

        if (comparisons.Any(c => c.IsNullComparison))
            flags.Add(new("NULL_COMPARISON", FilterRisk.High, "Compares a property with $null, which typically matches a large, changing set of recipients."));

        if (comparisons.Any(c => c.Property.Equals("MemberOfGroup", StringComparison.OrdinalIgnoreCase)))
            flags.Add(new("GROUP_MEMBERSHIP", FilterRisk.Moderate, "Membership is controlled by a group. Anyone who can change the group's membership can change which mailboxes the application can access. Only direct members are in scope."));

        if (comparisons.Any(c => c.Property.Equals("AdministrativeUnits", StringComparison.OrdinalIgnoreCase)))
            flags.Add(new("ADMIN_UNIT", FilterRisk.Moderate, "Membership is controlled by an administrative unit. Anyone who can change the administrative unit can change the scope."));

        if (comparisons.Any(c => MutableAttributeProperties.Contains(c.Property)))
            flags.Add(new("MUTABLE_ATTRIBUTE", FilterRisk.Moderate, "Matches on directory attributes (for example Department or CustomAttribute) that recipient administrators or synchronization can change."));

        if (comparisons.Any(c => AddressProperties.Contains(c.Property)))
            flags.Add(new("ADDRESS_MATCH", FilterRisk.Moderate, "Matches on email addresses. Microsoft documents that PrimarySmtpAddress filters also match proxy addresses, and addresses can be reassigned to other recipients."));

        if (comparisons.Any(c => NameProperties.Contains(c.Property)))
            flags.Add(new("NAME_MATCH", FilterRisk.Low, "Matches on names or aliases, which can be changed or reused for new recipients."));

        if (comparisons.Count > 0 && comparisons.All(c => TypeProperties.Contains(c.Property)))
            flags.Add(new("TYPE_ONLY", FilterRisk.High, "Selects recipients only by recipient type, which typically includes every mailbox of that type in the organization."));

        if (comparisons.Any(c => IdentityProperties.ContainsKey(c.Property) && c.Operator.Equals("-eq", StringComparison.OrdinalIgnoreCase)) && flags.Count == 0)
            flags.Add(new("IDENTITY_MIXED", FilterRisk.Low, "Uses identity properties combined with other conditions; verify the effective membership."));

        var overall = flags.Count == 0 ? FilterRisk.Low : flags.Max(f => f.Risk);
        return new FilterAnalysis(filter, true, null, overall, flags, null, [], properties);
    }

    /// <summary>
    /// Recognizes filters of the form "P -eq 'guid'" or "(P -eq 'g1') -or (P -eq 'g2') ..." where P is one immutable
    /// identity property. Returns the strategy and the identity set, or (null, []) when the filter is anything else.
    /// </summary>
    public static (RecipientFilterStrategy? Strategy, IReadOnlyList<Guid> Identities) TryExtractExactIdentity(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return (null, []);
        try
        {
            var parser = new Parser(Tokenize(filter));
            var root = parser.ParseExpression();
            parser.ExpectEnd();
            return TryExtractExactIdentity(root);
        }
        catch (FilterParseException)
        {
            return (null, []);
        }
    }

    /// <summary>True when two filters select exactly the same identity set with the same strategy.</summary>
    public static bool AreEquivalentExactFilters(string? a, string? b)
    {
        var (sa, ia) = TryExtractExactIdentity(a);
        var (sb, ib) = TryExtractExactIdentity(b);
        return sa is not null && sa == sb && ia.ToHashSet().SetEquals(ib);
    }

    private static (RecipientFilterStrategy?, IReadOnlyList<Guid>) TryExtractExactIdentity(FilterNode root)
    {
        var leaves = new List<ComparisonNode>();
        if (!FlattenOr(root, leaves) || leaves.Count == 0) return (null, []);

        RecipientFilterStrategy? strategy = null;
        var ids = new List<Guid>();
        foreach (var leaf in leaves)
        {
            if (!leaf.Operator.Equals("-eq", StringComparison.OrdinalIgnoreCase)) return (null, []);
            if (!IdentityProperties.TryGetValue(leaf.Property, out var s)) return (null, []);
            if (strategy is not null && strategy != s) return (null, []);
            if (leaf.IsNullComparison || !Guid.TryParse(leaf.Value, out var id) || id == Guid.Empty) return (null, []);
            strategy = s;
            ids.Add(id);
        }

        return (strategy, ids.Distinct().OrderBy(g => g).ToList());
    }

    private static bool FlattenOr(FilterNode node, List<ComparisonNode> leaves)
    {
        switch (node)
        {
            case ComparisonNode comparison:
                leaves.Add(comparison);
                return true;
            case BinaryNode { Operator: "-or" } or:
                return FlattenOr(or.Left, leaves) && FlattenOr(or.Right, leaves);
            default:
                return false;
        }
    }

    private static void Collect(FilterNode node, List<ComparisonNode> comparisons, ref bool negated)
    {
        switch (node)
        {
            case ComparisonNode comparison:
                comparisons.Add(comparison);
                break;
            case BinaryNode binary:
                Collect(binary.Left, comparisons, ref negated);
                Collect(binary.Right, comparisons, ref negated);
                break;
            case NotNode not:
                negated = true;
                Collect(not.Operand, comparisons, ref negated);
                break;
        }
    }

    // ---------------------------------------------------------------- tokenizer

    private enum TokenKind { LParen, RParen, Operator, Identifier, String, Variable, Number, End }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            switch (c)
            {
                case '(':
                    tokens.Add(new Token(TokenKind.LParen, "(", i++));
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.RParen, ")", i++));
                    continue;
                case '\'' or '"':
                    tokens.Add(ReadString(text, ref i));
                    continue;
                case '-':
                {
                    var start = i++;
                    while (i < text.Length && char.IsLetter(text[i])) i++;
                    var op = text[start..i];
                    if (op.Length == 1) throw new FilterParseException($"Unexpected '-' at position {start}.");
                    tokens.Add(new Token(TokenKind.Operator, op.ToLowerInvariant(), start));
                    continue;
                }
                case '$':
                {
                    var start = i++;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(new Token(TokenKind.Variable, text[start..i].ToLowerInvariant(), start));
                    continue;
                }
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '-' or ':')) i++;
                tokens.Add(new Token(TokenKind.Number, text[start..i], start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.')) i++;
                tokens.Add(new Token(TokenKind.Identifier, text[start..i], start));
                continue;
            }

            throw new FilterParseException($"Unexpected character '{c}' at position {i}.");
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
        return tokens;
    }

    private static Token ReadString(string text, ref int i)
    {
        var quote = text[i];
        var start = i++;
        var sb = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i];
            if (c == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    sb.Append(quote);
                    i += 2;
                    continue;
                }

                i++;
                return new Token(TokenKind.String, sb.ToString(), start);
            }

            if (c == '`' && quote == '"' && i + 1 < text.Length)
            {
                sb.Append(text[i + 1]);
                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        throw new FilterParseException($"Unterminated string literal starting at position {start}.");
    }

    // ---------------------------------------------------------------- parser

    private abstract record FilterNode;

    private sealed record ComparisonNode(string Property, string Operator, string Value, bool IsNullComparison) : FilterNode;

    private sealed record BinaryNode(string Operator, FilterNode Left, FilterNode Right) : FilterNode;

    private sealed record NotNode(FilterNode Operand) : FilterNode;

    private sealed class FilterParseException(string message) : Exception(message);

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;

        private Token Current => tokens[_position];

        public FilterNode ParseExpression() => ParseOr();

        public void ExpectEnd()
        {
            if (Current.Kind != TokenKind.End)
                throw new FilterParseException($"Unexpected '{Current.Text}' at position {Current.Position}.");
        }

        private FilterNode ParseOr()
        {
            var left = ParseAnd();
            while (Current is { Kind: TokenKind.Operator, Text: "-or" })
            {
                _position++;
                left = new BinaryNode("-or", left, ParseAnd());
            }
            return left;
        }

        private FilterNode ParseAnd()
        {
            var left = ParseNot();
            while (Current is { Kind: TokenKind.Operator, Text: "-and" })
            {
                _position++;
                left = new BinaryNode("-and", left, ParseNot());
            }
            return left;
        }

        private FilterNode ParseNot()
        {
            if (Current is { Kind: TokenKind.Operator, Text: "-not" })
            {
                _position++;
                return new NotNode(ParseNot());
            }

            return ParsePrimary();
        }

        private FilterNode ParsePrimary()
        {
            if (Current.Kind == TokenKind.LParen)
            {
                _position++;
                var inner = ParseOr();
                if (Current.Kind != TokenKind.RParen)
                    throw new FilterParseException($"Expected ')' at position {Current.Position}.");
                _position++;
                return inner;
            }

            if (Current.Kind != TokenKind.Identifier)
                throw new FilterParseException($"Expected a property name at position {Current.Position}, found '{Current.Text}'.");

            var property = Current.Text;
            _position++;

            if (Current.Kind != TokenKind.Operator || !ComparisonOperators.Contains(Current.Text))
                throw new FilterParseException($"Expected a comparison operator after '{property}' at position {Current.Position}.");

            var op = Current.Text;
            _position++;

            var value = Current;
            if (value.Kind is not (TokenKind.String or TokenKind.Variable or TokenKind.Number or TokenKind.Identifier))
                throw new FilterParseException($"Expected a value after '{property} {op}' at position {value.Position}.");
            _position++;

            var isNull = value.Kind == TokenKind.Variable && value.Text == "$null";
            return new ComparisonNode(property, op, value.Text, isNull);
        }
    }
}
