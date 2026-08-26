using System.Text;
using System.Text.RegularExpressions;
using codeloomapp.Models;

namespace codeloomapp.Services;

public static class CodeAssembler
{
    private static readonly HashSet<string> UnityCallbacks = new(StringComparer.Ordinal)
    {
        "Reset",
        "Awake",
        "OnEnable",
        "Start",
        "FixedUpdate",
        "Update",
        "LateUpdate",
        "OnGUI",
        "OnDisable",
        "OnDestroy",
        "OnValidate",
        "OnApplicationFocus",
        "OnApplicationPause",
        "OnApplicationQuit",
        "OnDrawGizmos",
        "OnDrawGizmosSelected",
        "OnCollisionEnter",
        "OnCollisionStay",
        "OnCollisionExit",
        "OnCollisionEnter2D",
        "OnCollisionStay2D",
        "OnCollisionExit2D",
        "OnTriggerEnter",
        "OnTriggerStay",
        "OnTriggerExit",
        "OnTriggerEnter2D",
        "OnTriggerStay2D",
        "OnTriggerExit2D",
        "OnMouseDown",
        "OnMouseUp",
        "OnMouseEnter",
        "OnMouseExit",
        "OnMouseOver",
        "OnMouseDrag",
        "OnAnimatorMove",
        "OnAnimatorIK"
    };

    private static readonly HashSet<string> ControlFlowNames = new(StringComparer.Ordinal)
    {
        "if",
        "for",
        "foreach",
        "while",
        "switch",
        "catch",
        "lock",
        "using"
    };

    private static readonly Regex NestedTypeRegex = new(
        @"(?m)^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|ref)\s+)*(?:class|struct|interface|enum|record|delegate)\b",
        RegexOptions.Compiled);

    private static readonly Regex PropertyAccessorRegex = new(
        @"\{[^{}]*\b(?:get|set|init)\b[^{}]*\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ExpressionPropertyRegex = new(
        @"(?m)^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|new|readonly)\s+)+[A-Za-z_][A-Za-z0-9_<>,?.\[\]]*\s+[A-Za-z_][A-Za-z0-9_]*\s*=>",
        RegexOptions.Compiled);

    private static readonly Regex MethodRegex = new(
        @"(?m)^\s*(?:\[[^\]\r\n]+\]\s*)*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|new|partial)\s+)*(?:(?:[A-Za-z_][A-Za-z0-9_<>,?.\[\]]*\s+)+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*(?:where\s+[^\r\n{=>]+\s*)?(?:\{|=>)",
        RegexOptions.Compiled);

    public static string Assemble(CodeFile file)
    {
        var builder = new StringBuilder();

        foreach (var usingStatement in file.UsingStatements
                     .Where(statement => !string.IsNullOrWhiteSpace(statement))
                     .Select(statement => statement.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            builder.AppendLine(usingStatement);
        }

        if (file.UsingStatements.Any(statement => !string.IsNullOrWhiteSpace(statement)))
            builder.AppendLine();

        var hasNamespace = !string.IsNullOrWhiteSpace(file.Namespace);
        var typeIndent = hasNamespace ? "    " : string.Empty;
        var memberIndent = typeIndent + "    ";

        if (hasNamespace)
        {
            // Block namespaces are intentionally used instead of C# 10 file-scoped
            // namespaces so exported scripts remain compatible with a wider range of
            // Unity editor/C# language-version combinations.
            builder.Append("namespace ")
                   .AppendLine(file.Namespace.Trim());
            builder.AppendLine("{");
        }

        if (!string.IsNullOrWhiteSpace(file.TypeAttributes))
        {
            foreach (var line in NormalizeLines(file.TypeAttributes))
                builder.Append(typeIndent).AppendLine(line);
        }

        var typeModifiers = string.IsNullOrWhiteSpace(file.TypeModifiers)
            ? string.Empty
            : file.TypeModifiers.Trim() + " ";
        var typeKind = string.IsNullOrWhiteSpace(file.TypeKind)
            ? "class"
            : file.TypeKind.Trim();

        builder.Append(typeIndent)
               .Append(typeModifiers)
               .Append(typeKind)
               .Append(' ')
               .Append(string.IsNullOrWhiteSpace(file.ClassName) ? "UnnamedClass" : file.ClassName.Trim());

        if (!string.IsNullOrWhiteSpace(file.BaseClass))
            builder.Append(" : ").Append(file.BaseClass.Trim());

        builder.AppendLine();
        builder.Append(typeIndent).AppendLine("{");

        var plan = BuildPlan(file)
            .Where(item => !string.IsNullOrWhiteSpace(item.Subfile.Code))
            .ToList();

        var grouped = plan
            .GroupBy(item => item.Section)
            .OrderBy(group => SectionOrder(group.Key))
            .ToList();

        for (var groupIndex = 0; groupIndex < grouped.Count; groupIndex++)
        {
            var group = grouped[groupIndex];

            builder.Append(memberIndent)
                   .Append("// --- ")
                   .Append(group.Key)
                   .AppendLine(" ---");
            builder.AppendLine();

            var items = group.ToList();
            for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                var item = items[itemIndex];
                builder.Append(memberIndent)
                       .Append("// ")
                       .AppendLine(item.Subfile.Name);

                AppendIndentedBlock(builder, item.Subfile.Code, memberIndent);

                if (itemIndex < items.Count - 1)
                    builder.AppendLine();
            }

            if (groupIndex < grouped.Count - 1)
                builder.AppendLine();
        }

        builder.Append(typeIndent).AppendLine("}");
        if (hasNamespace)
            builder.AppendLine("}");

        return builder.ToString();
    }

    public static IReadOnlyList<AssemblyPlanItem> BuildPlan(CodeFile file)
    {
        return file.Subfiles
            .Select((subfile, index) =>
            {
                var classification = Classify(file, subfile);
                return new AssemblyPlanItem(
                    subfile,
                    classification.Section,
                    NormalizeSection(subfile.AssemblySection),
                    classification.Reason,
                    index);
            })
            .OrderBy(item => SectionOrder(item.Section))
            .ThenBy(item => item.OriginalIndex)
            .ToList();
    }

    public static AssemblyClassification Classify(CodeFile file, CodeSubfile subfile)
    {
        var requestedSection = NormalizeSection(subfile.AssemblySection);
        if (!string.Equals(requestedSection, AssemblySections.Auto, StringComparison.Ordinal))
        {
            return new AssemblyClassification(
                requestedSection,
                "Manual placement override.");
        }

        var code = subfile.Code ?? string.Empty;
        var codeWithoutComments = StripComments(code).Trim();

        if (string.IsNullOrWhiteSpace(codeWithoutComments))
        {
            return new AssemblyClassification(
                AssemblySections.Other,
                "The fragment only contains comments or whitespace.");
        }

        if (NestedTypeRegex.IsMatch(codeWithoutComments))
        {
            return new AssemblyClassification(
                AssemblySections.NestedTypes,
                "Detected a nested class, struct, interface, enum, record, or delegate.");
        }

        if (PropertyAccessorRegex.IsMatch(codeWithoutComments)
            || ExpressionPropertyRegex.IsMatch(codeWithoutComments))
        {
            return new AssemblyClassification(
                AssemblySections.Properties,
                "Detected a C# property declaration.");
        }

        var methodMatch = MethodRegex.Match(codeWithoutComments);
        if (methodMatch.Success)
        {
            var methodName = methodMatch.Groups["name"].Value;

            if (!ControlFlowNames.Contains(methodName))
            {
                var plainClassName = file.ClassName.Split('<')[0].Trim();
                if (string.Equals(methodName, plainClassName, StringComparison.Ordinal))
                {
                    return new AssemblyClassification(
                        AssemblySections.Constructors,
                        $"Detected the {plainClassName} constructor.");
                }

                if (UnityCallbacks.Contains(methodName))
                {
                    return new AssemblyClassification(
                        AssemblySections.UnityLifecycle,
                        $"Recognized Unity callback {methodName}().");
                }

                return new AssemblyClassification(
                    AssemblySections.Methods,
                    $"Detected method {methodName}().");
            }
        }

        if (LooksLikeFieldDeclarations(codeWithoutComments))
        {
            return new AssemblyClassification(
                AssemblySections.Fields,
                "Detected field or setting declarations.");
        }

        var role = subfile.Role ?? string.Empty;
        if (role.Contains("property", StringComparison.OrdinalIgnoreCase))
        {
            return new AssemblyClassification(
                AssemblySections.Properties,
                "Inferred from the subfile role because the code shape was ambiguous.");
        }

        if (role.Contains("setting", StringComparison.OrdinalIgnoreCase)
            || role.Contains("field", StringComparison.OrdinalIgnoreCase)
            || role.Contains("state", StringComparison.OrdinalIgnoreCase))
        {
            return new AssemblyClassification(
                AssemblySections.Fields,
                "Inferred from the subfile role because the code shape was ambiguous.");
        }

        if (role.Contains("method", StringComparison.OrdinalIgnoreCase)
            || role.Contains("logic", StringComparison.OrdinalIgnoreCase)
            || role.Contains("action", StringComparison.OrdinalIgnoreCase)
            || role.Contains("input", StringComparison.OrdinalIgnoreCase))
        {
            return new AssemblyClassification(
                AssemblySections.Methods,
                "Inferred from the subfile role because the code shape was ambiguous.");
        }

        return new AssemblyClassification(
            AssemblySections.Other,
            "No stronger structural pattern was detected.");
    }

    public static string NormalizeSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
            return AssemblySections.Auto;

        return AssemblySections.All.Contains(section, StringComparer.Ordinal)
            ? section
            : AssemblySections.Auto;
    }

    private static bool LooksLikeFieldDeclarations(string code)
    {
        var meaningfulLines = code
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Where(line => !line.StartsWith("[", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
            .ToList();

        if (meaningfulLines.Count == 0)
            return false;

        return meaningfulLines.All(line => line.EndsWith(';'));
    }

    private static string StripComments(string code)
    {
        var builder = new StringBuilder(code.Length);
        var state = CommentScanState.Normal;
        var escaped = false;
        var rawQuoteCount = 0;

        for (var index = 0; index < code.Length; index++)
        {
            var character = code[index];
            var next = index + 1 < code.Length ? code[index + 1] : '\0';

            switch (state)
            {
                case CommentScanState.LineComment:
                    if (character is '\r' or '\n')
                    {
                        builder.Append(character);
                        state = CommentScanState.Normal;
                    }
                    else
                    {
                        builder.Append(' ');
                    }
                    break;

                case CommentScanState.BlockComment:
                    if (character == '*' && next == '/')
                    {
                        builder.Append("  ");
                        index++;
                        state = CommentScanState.Normal;
                    }
                    else
                    {
                        builder.Append(character is '\r' or '\n' ? character : ' ');
                    }
                    break;

                case CommentScanState.String:
                    builder.Append(character);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        state = CommentScanState.Normal;
                    }
                    break;

                case CommentScanState.Char:
                    builder.Append(character);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '\'')
                    {
                        state = CommentScanState.Normal;
                    }
                    break;

                case CommentScanState.VerbatimString:
                    builder.Append(character);
                    if (character == '"')
                    {
                        if (next == '"')
                        {
                            builder.Append(next);
                            index++;
                        }
                        else
                        {
                            state = CommentScanState.Normal;
                        }
                    }
                    break;

                case CommentScanState.RawString:
                    if (character == '"')
                    {
                        var run = CountQuoteRun(code, index);
                        builder.Append('"', run);
                        index += run - 1;
                        if (run >= rawQuoteCount)
                        {
                            state = CommentScanState.Normal;
                            rawQuoteCount = 0;
                        }
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;

                default:
                    if (character == '/' && next == '/')
                    {
                        builder.Append("  ");
                        index++;
                        state = CommentScanState.LineComment;
                    }
                    else if (character == '/' && next == '*')
                    {
                        builder.Append("  ");
                        index++;
                        state = CommentScanState.BlockComment;
                    }
                    else if (character == '\'')
                    {
                        builder.Append(character);
                        state = CommentScanState.Char;
                        escaped = false;
                    }
                    else if (character == '"')
                    {
                        var quoteRun = CountQuoteRun(code, index);
                        if (quoteRun >= 3)
                        {
                            builder.Append('"', quoteRun);
                            index += quoteRun - 1;
                            rawQuoteCount = quoteRun;
                            state = CommentScanState.RawString;
                        }
                        else
                        {
                            builder.Append(character);
                            var verbatim = index > 0 && code[index - 1] == '@'
                                           || index > 1 && code[index - 2] == '@' && code[index - 1] == '$';
                            state = verbatim
                                ? CommentScanState.VerbatimString
                                : CommentScanState.String;
                            escaped = false;
                        }
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }

        return builder.ToString();
    }

    private static int CountQuoteRun(string code, int start)
    {
        var count = 0;
        while (start + count < code.Length && code[start + count] == '"')
            count++;
        return count;
    }

    private static int SectionOrder(string section)
    {
        return section switch
        {
            AssemblySections.Fields => 0,
            AssemblySections.Properties => 1,
            AssemblySections.Constructors => 2,
            AssemblySections.UnityLifecycle => 3,
            AssemblySections.Methods => 4,
            AssemblySections.NestedTypes => 5,
            AssemblySections.Other => 6,
            _ => 7
        };
    }

    private static void AppendIndentedBlock(StringBuilder builder, string code, string indent)
    {
        var normalized = code
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .TrimEnd('\n');

        foreach (var line in normalized.Split('\n'))
            builder.Append(indent).AppendLine(line);
    }

    private static IEnumerable<string> NormalizeLines(string value)
    {
        return value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd());
    }

    private enum CommentScanState
    {
        Normal,
        LineComment,
        BlockComment,
        String,
        Char,
        VerbatimString,
        RawString
    }
}

public sealed record AssemblyClassification(string Section, string Reason);

public sealed record AssemblyPlanItem(
    CodeSubfile Subfile,
    string Section,
    string Mode,
    string Reason,
    int OriginalIndex)
{
    public string SubfileName => Subfile.Name;
    public string PlacementLabel => string.Equals(Mode, AssemblySections.Auto, StringComparison.Ordinal)
        ? $"Auto → {Section}"
        : $"{Section} · manual";
}
