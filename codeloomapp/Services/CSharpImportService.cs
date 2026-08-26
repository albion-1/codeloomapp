using System.Text.RegularExpressions;
using codeloomapp.Models;

namespace codeloomapp.Services;

public static class CSharpImportService
{
    private static readonly Regex UsingRegex = new(
        @"(?m)^\s*(?<using>(?:global\s+)?using\s+[^;]+;)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex NamespaceRegex = new(
        @"(?m)^\s*namespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)\s*(?:;|\{)",
        RegexOptions.Compiled);

    private static readonly Regex TypeRegex = new(
        @"(?ms)(?<attrs>(?:(?:^|\n)\s*\[[^\]\r\n]+\]\s*)*)" +
        @"(?:^|\n)\s*(?<mods>(?:(?:public|internal|private|protected|static|abstract|sealed|partial|unsafe|new|readonly|ref)\s+)*)" +
        @"(?<kind>class|struct|interface|record(?:\s+class|\s+struct)?)\s+" +
        @"(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<generic>\s*<[^\r\n{>]+>)?\s*" +
        @"(?:\:\s*(?<bases>[^\r\n{]+))?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex MethodSignatureRegex = new(
        @"(?ms)^\s*(?:\[[^\]\r\n]+\]\s*)*" +
        @"(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|new|partial|unsafe)\s+)*" +
        @"(?:(?<return>[A-Za-z_][A-Za-z0-9_:.?<>,\[\]\s]*)\s+)?" +
        @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<parameters>[^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex PropertyNameRegex = new(
        @"(?ms)^\s*(?:\[[^\]\r\n]+\]\s*)*" +
        @"(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|new|readonly|required)\s+)*" +
        @"[A-Za-z_][A-Za-z0-9_:.?<>,\[\]\s]*\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\{|=>)",
        RegexOptions.Compiled);

    private static readonly Regex NestedTypeNameRegex = new(
        @"\b(?:class|struct|interface|enum|record)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    public static CSharpImportResult Import(string source, string sourceFileName)
    {
        var warnings = new List<string>();
        var normalized = NormalizeNewlines(source);

        var typeMatch = TypeRegex.Match(normalized);
        if (!typeMatch.Success)
        {
            throw new InvalidOperationException(
                "Code Loom could not find a class, struct, interface, or record declaration in this C# file.");
        }

        var plainName = typeMatch.Groups["name"].Value.Trim();
        var genericSuffix = typeMatch.Groups["generic"].Value.Trim();
        var className = plainName + genericSuffix;
        var typeKind = NormalizeTypeKind(typeMatch.Groups["kind"].Value);
        var modifiers = NormalizeSpacing(typeMatch.Groups["mods"].Value);
        if (string.IsNullOrWhiteSpace(modifiers))
            modifiers = "internal";

        var openingBrace = typeMatch.Index + typeMatch.Length - 1;
        var closingBrace = FindMatchingBrace(normalized, openingBrace);
        if (closingBrace < 0)
        {
            throw new InvalidOperationException(
                "The imported type appears to have an unmatched opening brace, so Code Loom cannot split it safely.");
        }

        var file = new CodeFile
        {
            Name = EnsureCsExtension(sourceFileName),
            ClassName = className,
            BaseClass = typeMatch.Groups["bases"].Value.Trim(),
            Namespace = NamespaceRegex.Match(normalized) is { Success: true } namespaceMatch
                ? namespaceMatch.Groups["name"].Value.Trim()
                : string.Empty,
            TypeKind = typeKind,
            TypeModifiers = modifiers,
            TypeAttributes = NormalizeAttributes(typeMatch.Groups["attrs"].Value)
        };

        foreach (Match usingMatch in UsingRegex.Matches(normalized))
        {
            var usingStatement = usingMatch.Groups["using"].Value.Trim();
            if (!file.UsingStatements.Contains(usingStatement, StringComparer.Ordinal))
                file.UsingStatements.Add(usingStatement);
        }

        var body = normalized[(openingBrace + 1)..closingBrace];
        var rawMembers = SplitTopLevelMembers(body);
        BuildSubfiles(file, rawMembers, plainName, warnings);

        if (file.Subfiles.Count == 0)
        {
            file.Subfiles.Add(new CodeSubfile
            {
                Name = $"{plainName}.Imported",
                Role = "Imported",
                Code = body.Trim(),
                AssemblySection = AssemblySections.Other,
                Purpose = "Imported class body that Code Loom could not split into smaller structural members."
            });
            warnings.Add("The class body was kept as one imported subfile because no safe member boundaries were detected.");
        }

        var trailingSource = normalized[(closingBrace + 1)..].Trim();
        if (!string.IsNullOrWhiteSpace(trailingSource))
        {
            warnings.Add(
                "This source file contains code after the first imported type. Code Loom imported only the first top-level type so it would not silently merge unrelated classes together.");
        }

        if (genericSuffix.Length > 0)
        {
            warnings.Add(
                "The imported type is generic. Code Loom preserves the generic type name, but some automatic constructor classification may be less precise than it is for normal Unity scripts.");
        }

        VariableSyncService.SyncFromCode(file);
        return new CSharpImportResult(file, warnings);
    }

    private static void BuildSubfiles(
        CodeFile file,
        IReadOnlyList<string> rawMembers,
        string plainClassName,
        List<string> warnings)
    {
        var pendingFields = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var otherIndex = 1;

        void FlushFields()
        {
            if (pendingFields.Count == 0)
                return;

            var name = MakeUniqueSubfileName($"{plainClassName}.Settings", usedNames);
            file.Subfiles.Add(new CodeSubfile
            {
                Name = name,
                Role = "Settings",
                Code = string.Join("\n\n", pendingFields.Select(member => member.Trim())),
                AssemblySection = AssemblySections.Auto,
                Receives = "Inspector/default values",
                Returns = "Class fields",
                UsedBy = "Detected automatically from code",
                Purpose = "Imported class-level fields and settings from the original C# file."
            });

            pendingFields.Clear();
        }

        foreach (var rawMember in rawMembers)
        {
            var member = rawMember.Trim();
            if (member.Length == 0)
                continue;

            var probe = new CodeSubfile { Code = member };
            var classification = CodeAssembler.Classify(file, probe);

            if (string.Equals(classification.Section, AssemblySections.Fields, StringComparison.Ordinal))
            {
                pendingFields.Add(member);
                continue;
            }

            FlushFields();

            var methodSignature = MethodSignatureRegex.Match(member);
            var methodName = methodSignature.Success
                ? methodSignature.Groups["name"].Value
                : string.Empty;

            if (string.Equals(classification.Section, AssemblySections.UnityLifecycle, StringComparison.Ordinal))
            {
                var name = MakeUniqueSubfileName(
                    $"{plainClassName}.{FallbackName(methodName, "UnityLifecycle")}",
                    usedNames);
                file.Subfiles.Add(CreateMethodSubfile(
                    name,
                    "Unity lifecycle",
                    member,
                    methodSignature,
                    $"Imported Unity callback {FallbackName(methodName, "lifecycle method")}()."));
                continue;
            }

            if (string.Equals(classification.Section, AssemblySections.Methods, StringComparison.Ordinal)
                || string.Equals(classification.Section, AssemblySections.Constructors, StringComparison.Ordinal))
            {
                var label = string.Equals(classification.Section, AssemblySections.Constructors, StringComparison.Ordinal)
                    ? plainClassName
                    : FallbackName(methodName, "Method");
                var name = MakeUniqueSubfileName($"{plainClassName}.{label}", usedNames);
                var role = InferMethodRole(methodName, classification.Section);
                file.Subfiles.Add(CreateMethodSubfile(
                    name,
                    role,
                    member,
                    methodSignature,
                    string.Equals(classification.Section, AssemblySections.Constructors, StringComparison.Ordinal)
                        ? $"Imported {plainClassName} constructor."
                        : $"Imported {FallbackName(methodName, "method")}() behavior from the original C# file."));
                continue;
            }

            if (string.Equals(classification.Section, AssemblySections.Properties, StringComparison.Ordinal))
            {
                var propertyMatch = PropertyNameRegex.Match(member);
                var propertyName = propertyMatch.Success
                    ? propertyMatch.Groups["name"].Value
                    : "Properties";
                var name = MakeUniqueSubfileName($"{plainClassName}.{propertyName}", usedNames);
                file.Subfiles.Add(new CodeSubfile
                {
                    Name = name,
                    Role = "Property",
                    Code = member,
                    AssemblySection = AssemblySections.Auto,
                    Purpose = $"Imported {propertyName} property from the original C# file."
                });
                continue;
            }

            if (string.Equals(classification.Section, AssemblySections.NestedTypes, StringComparison.Ordinal))
            {
                var nestedMatch = NestedTypeNameRegex.Match(member);
                var nestedName = nestedMatch.Success
                    ? nestedMatch.Groups["name"].Value
                    : "NestedType";
                var name = MakeUniqueSubfileName($"{plainClassName}.{nestedName}", usedNames);
                file.Subfiles.Add(new CodeSubfile
                {
                    Name = name,
                    Role = "Nested type",
                    Code = member,
                    AssemblySection = AssemblySections.Auto,
                    Purpose = $"Imported nested type {nestedName}."
                });
                continue;
            }

            var otherName = MakeUniqueSubfileName(
                $"{plainClassName}.Imported{otherIndex++}",
                usedNames);
            file.Subfiles.Add(new CodeSubfile
            {
                Name = otherName,
                Role = "Imported",
                Code = member,
                AssemblySection = AssemblySections.Other,
                Purpose = "Imported C# member that Code Loom could not confidently classify."
            });
            warnings.Add($"{otherName} was kept in the Other assembly section because its structure was ambiguous.");
        }

        FlushFields();
    }

    private static CodeSubfile CreateMethodSubfile(
        string name,
        string role,
        string member,
        Match signature,
        string purpose)
    {
        var parameters = signature.Success
            ? signature.Groups["parameters"].Value.Trim()
            : string.Empty;
        var returnType = signature.Success
            ? signature.Groups["return"].Value.Trim()
            : string.Empty;

        return new CodeSubfile
        {
            Name = name,
            Role = role,
            Code = member,
            AssemblySection = AssemblySections.Auto,
            Receives = string.IsNullOrWhiteSpace(parameters) ? "Nothing" : parameters,
            Returns = string.IsNullOrWhiteSpace(returnType) || string.Equals(returnType, "void", StringComparison.Ordinal)
                ? "Nothing"
                : returnType,
            UsedBy = "Detected automatically in Flow",
            Purpose = purpose
        };
    }

    private static IReadOnlyList<string> SplitTopLevelMembers(string body)
    {
        var members = new List<string>();
        var index = 0;

        while (index < body.Length)
        {
            while (index < body.Length && char.IsWhiteSpace(body[index]))
                index++;

            if (index >= body.Length)
                break;

            var start = index;
            var end = FindMemberEnd(body, start);
            if (end <= start)
                break;

            var member = body[start..end].Trim();
            if (member.Length > 0)
                members.Add(member);

            index = end;
        }

        return members;
    }

    private static int FindMemberEnd(string text, int start)
    {
        var depth = 0;
        var seenBlock = false;
        var state = ScanState.Normal;
        var escaped = false;

        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            switch (state)
            {
                case ScanState.LineComment:
                    if (current == '\n')
                        state = ScanState.Normal;
                    continue;

                case ScanState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        state = ScanState.Normal;
                        index++;
                    }
                    continue;

                case ScanState.String:
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (current == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (current == '"')
                        state = ScanState.Normal;
                    continue;

                case ScanState.VerbatimString:
                    if (current == '"' && next == '"')
                    {
                        index++;
                        continue;
                    }
                    if (current == '"')
                        state = ScanState.Normal;
                    continue;

                case ScanState.Character:
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (current == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (current == '\'')
                        state = ScanState.Normal;
                    continue;
            }

            if (current == '/' && next == '/')
            {
                state = ScanState.LineComment;
                index++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                state = ScanState.BlockComment;
                index++;
                continue;
            }

            if (current == '@' && next == '"')
            {
                state = ScanState.VerbatimString;
                index++;
                continue;
            }

            if (current == '"')
            {
                state = ScanState.String;
                continue;
            }

            if (current == '\'')
            {
                state = ScanState.Character;
                continue;
            }

            if (current == '{')
            {
                depth++;
                seenBlock = true;
                continue;
            }

            if (current == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && seenBlock)
                {
                    var end = index + 1;
                    while (end < text.Length && char.IsWhiteSpace(text[end]))
                        end++;
                    if (end < text.Length && text[end] == ';')
                        end++;
                    return end;
                }
                continue;
            }

            if (current == ';' && depth == 0)
                return index + 1;
        }

        return text.Length;
    }

    private static int FindMatchingBrace(string text, int openingBrace)
    {
        var depth = 0;
        var state = ScanState.Normal;
        var escaped = false;

        for (var index = openingBrace; index < text.Length; index++)
        {
            var current = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            switch (state)
            {
                case ScanState.LineComment:
                    if (current == '\n')
                        state = ScanState.Normal;
                    continue;
                case ScanState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        state = ScanState.Normal;
                        index++;
                    }
                    continue;
                case ScanState.String:
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (current == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (current == '"')
                        state = ScanState.Normal;
                    continue;
                case ScanState.VerbatimString:
                    if (current == '"' && next == '"')
                    {
                        index++;
                        continue;
                    }
                    if (current == '"')
                        state = ScanState.Normal;
                    continue;
                case ScanState.Character:
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (current == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (current == '\'')
                        state = ScanState.Normal;
                    continue;
            }

            if (current == '/' && next == '/')
            {
                state = ScanState.LineComment;
                index++;
                continue;
            }
            if (current == '/' && next == '*')
            {
                state = ScanState.BlockComment;
                index++;
                continue;
            }
            if (current == '@' && next == '"')
            {
                state = ScanState.VerbatimString;
                index++;
                continue;
            }
            if (current == '"')
            {
                state = ScanState.String;
                continue;
            }
            if (current == '\'')
            {
                state = ScanState.Character;
                continue;
            }

            if (current == '{')
                depth++;
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }

        return -1;
    }

    private static string InferMethodRole(string methodName, string section)
    {
        if (string.Equals(section, AssemblySections.Constructors, StringComparison.Ordinal))
            return "Constructor";

        if (methodName.Contains("Input", StringComparison.OrdinalIgnoreCase)
            || methodName.StartsWith("Read", StringComparison.OrdinalIgnoreCase))
        {
            return "Input";
        }

        if (methodName.StartsWith("Move", StringComparison.OrdinalIgnoreCase)
            || methodName.StartsWith("Turn", StringComparison.OrdinalIgnoreCase)
            || methodName.StartsWith("Apply", StringComparison.OrdinalIgnoreCase)
            || methodName.StartsWith("Set", StringComparison.OrdinalIgnoreCase))
        {
            return "Action";
        }

        return "Method";
    }

    private static string MakeUniqueSubfileName(string baseName, HashSet<string> usedNames)
    {
        if (usedNames.Add(baseName))
            return baseName;

        var suffix = 2;
        while (!usedNames.Add(baseName + suffix))
            suffix++;

        return baseName + suffix;
    }

    private static string FallbackName(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string NormalizeTypeKind(string kind)
    {
        return NormalizeSpacing(kind);
    }

    private static string NormalizeAttributes(string value)
    {
        return NormalizeNewlines(value)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Aggregate(string.Empty, (current, line) =>
                current.Length == 0 ? line : current + "\n" + line);
    }

    private static string NormalizeSpacing(string value)
    {
        return string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string EnsureCsExtension(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".cs";
    }

    private static string NormalizeNewlines(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
    }

    private enum ScanState
    {
        Normal,
        LineComment,
        BlockComment,
        String,
        VerbatimString,
        Character
    }
}

public sealed record CSharpImportResult(
    CodeFile File,
    IReadOnlyList<string> Warnings);
