using System.Text.RegularExpressions;
using codeloomapp.Models;

namespace codeloomapp.Services;

public static class VariableSyncService
{
    private static readonly Regex FieldRegex = new(
        @"^(?<indent>\s*)(?<attributes>(?:\[[^\]\r\n]+\]\s*)*)(?:(?<access>public|private\s+protected|private|protected\s+internal|protected|internal)\s+)?(?<modifiers>(?:(?:static|readonly|const|volatile|new|unsafe|required|fixed)\s+)*)?(?<type>[A-Za-z_][A-Za-z0-9_:.?<>,\[\]\s]*?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:=\s*(?<default>.*?))?;\s*(?<comment>//.*)?$",
        RegexOptions.Compiled);

    private static readonly string[] NonFieldStarters =
    {
        "using ",
        "namespace ",
        "return ",
        "throw ",
        "yield ",
        "break",
        "continue",
        "case ",
        "default:",
        "#"
    };

    public static void SyncFromCode(CodeFile file)
    {
        var previous = file.Variables.ToList();
        var detections = DetectFields(file);
        var consumed = new HashSet<VariableDefinition>();
        var rebuilt = new List<VariableDefinition>();

        foreach (var field in detections)
        {
            var existing = previous.FirstOrDefault(variable =>
                               !consumed.Contains(variable)
                               && string.Equals(variable.DeclaredIn, field.Subfile.Name, StringComparison.Ordinal)
                               && (string.Equals(variable.SourceName, field.Name, StringComparison.Ordinal)
                                   || string.Equals(variable.Name, field.Name, StringComparison.Ordinal)))
                           ?? previous.FirstOrDefault(variable =>
                               !consumed.Contains(variable)
                               && string.Equals(variable.DeclaredIn, field.Subfile.Name, StringComparison.Ordinal)
                               && variable.SourceLine == field.LineNumber)
                           ?? previous.FirstOrDefault(variable =>
                               !consumed.Contains(variable)
                               && string.Equals(variable.Name, field.Name, StringComparison.Ordinal));

            if (existing is not null)
                consumed.Add(existing);

            rebuilt.Add(new VariableDefinition
            {
                Name = field.Name,
                Type = field.Type,
                DefaultValue = field.DefaultValue,
                DeclaredIn = field.Subfile.Name,
                Meaning = existing?.Meaning ?? string.Empty,
                IsCodeBacked = true,
                Access = field.Access,
                Modifiers = field.Modifiers,
                SourceName = field.Name,
                SourceLine = field.LineNumber
            });
        }

        file.Variables.Clear();
        foreach (var variable in rebuilt)
            file.Variables.Add(variable);
    }

    public static IReadOnlyList<DetectedField> DetectFields(CodeFile file)
    {
        var result = new List<DetectedField>();

        foreach (var subfile in file.Subfiles)
            result.AddRange(DetectFields(subfile));

        return result;
    }

    public static bool TryUpdateField(
        CodeFile file,
        VariableDefinition variable,
        string newName,
        string newType,
        string newDefaultValue,
        out CodeSubfile? changedSubfile,
        out string error)
    {
        changedSubfile = null;
        error = string.Empty;

        newName = newName.Trim();
        newType = newType.Trim();
        newDefaultValue = newDefaultValue.Trim();

        if (!NameSafetyService.IsValidCSharpIdentifier(newName))
        {
            error = "Variable names must be valid C# identifiers and cannot be C# keywords such as class, int, or namespace.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(newType)
            || newType.Contains('\n')
            || newType.Contains('\r')
            || newType.Contains(';')
            || newType.Contains('{')
            || newType.Contains('}'))
        {
            error = "Enter a single C# type such as float, int, Vector3, or List<string>.";
            return false;
        }

        if (newDefaultValue.Contains('\n')
            || newDefaultValue.Contains('\r')
            || newDefaultValue.Contains(';'))
        {
            error = "The default value must fit on one declaration line and cannot contain a semicolon.";
            return false;
        }

        var subfile = file.Subfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, variable.DeclaredIn, StringComparison.Ordinal));

        if (subfile is null)
        {
            error = "Code Loom can no longer find the subfile that declares this variable.";
            return false;
        }

        var fields = DetectFields(subfile);
        var field = fields.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, variable.SourceName, StringComparison.Ordinal))
                    ?? fields.FirstOrDefault(candidate =>
                        candidate.LineNumber == variable.SourceLine)
                    ?? fields.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, variable.Name, StringComparison.Ordinal));

        if (field is null)
        {
            error = "The declaration changed in the code editor. Refresh the Variables tab and try again.";
            return false;
        }

        var lines = NormalizeNewlines(subfile.Code).Split('\n').ToList();
        var replacement = BuildDeclaration(field, newName, newType, newDefaultValue);
        lines[field.LineNumber - 1] = replacement;
        subfile.Code = string.Join("\n", lines);

        variable.Name = newName;
        variable.Type = newType;
        variable.DefaultValue = newDefaultValue;
        variable.SourceName = newName;
        variable.SourceLine = field.LineNumber;
        changedSubfile = subfile;
        return true;
    }

    public static bool TryDeleteField(
        CodeFile file,
        VariableDefinition variable,
        out CodeSubfile? changedSubfile,
        out string error)
    {
        changedSubfile = null;
        error = string.Empty;

        var subfile = file.Subfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, variable.DeclaredIn, StringComparison.Ordinal));

        if (subfile is null)
        {
            error = "Code Loom can no longer find the subfile that declares this variable.";
            return false;
        }

        var fields = DetectFields(subfile);
        var field = fields.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, variable.SourceName, StringComparison.Ordinal))
                    ?? fields.FirstOrDefault(candidate =>
                        candidate.LineNumber == variable.SourceLine)
                    ?? fields.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, variable.Name, StringComparison.Ordinal));

        if (field is null)
        {
            error = "The declaration changed in the code editor. Refresh the Variables tab and try again.";
            return false;
        }

        var lines = NormalizeNewlines(subfile.Code).Split('\n').ToList();
        var declarationIndex = field.LineNumber - 1;
        lines.RemoveAt(declarationIndex);

        // Remove attributes that belonged directly to this field, such as [SerializeField].
        var attributeIndex = declarationIndex - 1;
        while (attributeIndex >= 0)
        {
            var trimmed = lines[attributeIndex].Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal)
                || !trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                break;
            }

            lines.RemoveAt(attributeIndex);
            declarationIndex--;
            attributeIndex--;
        }

        CollapseExcessBlankLines(lines);
        subfile.Code = string.Join("\n", lines).TrimEnd('\n');
        changedSubfile = subfile;
        return true;
    }

    public static AddedField AddField(CodeFile file, CodeSubfile? preferredSubfile)
    {
        var target = preferredSubfile is not null
                     && string.Equals(
                         CodeAssembler.Classify(file, preferredSubfile).Section,
                         AssemblySections.Fields,
                         StringComparison.Ordinal)
            ? preferredSubfile
            : file.Subfiles.FirstOrDefault(subfile =>
                string.Equals(
                    CodeAssembler.Classify(file, subfile).Section,
                    AssemblySections.Fields,
                    StringComparison.Ordinal));

        if (target is null)
        {
            var baseName = string.IsNullOrWhiteSpace(file.ClassName)
                ? "Class.Settings"
                : file.ClassName + ".Settings";

            target = new CodeSubfile
            {
                Name = MakeUniqueSubfileName(file, baseName),
                Role = "Settings",
                AssemblySection = AssemblySections.Fields,
                Purpose = "Stores class-level fields and settings."
            };

            file.Subfiles.Insert(0, target);
        }

        var existingNames = DetectFields(file)
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

        var name = "newVariable";
        var suffix = 2;
        while (existingNames.Contains(name))
        {
            name = "newVariable" + suffix;
            suffix++;
        }

        var declaration = $"private float {name} = 0f;";
        var normalized = NormalizeNewlines(target.Code).TrimEnd('\n');
        target.Code = string.IsNullOrWhiteSpace(normalized)
            ? declaration
            : normalized + "\n\n" + declaration;

        return new AddedField(target, name);
    }

    private static IReadOnlyList<DetectedField> DetectFields(CodeSubfile subfile)
    {
        var result = new List<DetectedField>();
        var lines = NormalizeNewlines(subfile.Code).Split('\n');
        var braceDepth = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];

            if (braceDepth == 0 && TryParseField(line, subfile, index + 1, out var field))
                result.Add(field);

            braceDepth = Math.Max(0, braceDepth + BraceDelta(line));
        }

        return result;
    }

    private static bool TryParseField(
        string line,
        CodeSubfile subfile,
        int lineNumber,
        out DetectedField field)
    {
        field = default!;
        var trimmed = line.TrimStart();

        if (trimmed.Length == 0
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.Contains("=>", StringComparison.Ordinal)
            || NonFieldStarters.Any(starter => trimmed.StartsWith(starter, StringComparison.Ordinal)))
        {
            return false;
        }

        var match = FieldRegex.Match(line);
        if (!match.Success)
            return false;

        var type = match.Groups["type"].Value.Trim();
        if (string.IsNullOrWhiteSpace(type)
            || string.Equals(type, "var", StringComparison.Ordinal)
            || type.Contains('(')
            || type.Contains(')')
            || HasTopLevelComma(type))
        {
            // A declaration such as `private int x, y;` is deliberately ignored.
            // Treating only the last name as a field would make the Variables view lie
            // about the actual declaration. Users can split it into one field per line.
            return false;
        }

        field = new DetectedField(
            subfile,
            lineNumber,
            match.Groups["indent"].Value,
            match.Groups["attributes"].Value,
            match.Groups["access"].Value.Trim(),
            NormalizeSpacing(match.Groups["modifiers"].Value),
            type,
            match.Groups["name"].Value,
            match.Groups["default"].Success ? match.Groups["default"].Value.Trim() : string.Empty,
            match.Groups["comment"].Value);

        return true;
    }

    private static string BuildDeclaration(
        DetectedField field,
        string name,
        string type,
        string defaultValue)
    {
        var pieces = new List<string>();
        if (!string.IsNullOrWhiteSpace(field.Access))
            pieces.Add(field.Access);
        if (!string.IsNullOrWhiteSpace(field.Modifiers))
            pieces.Add(field.Modifiers);
        pieces.Add(type);
        pieces.Add(name);

        var declaration = field.Indentation
                          + field.AttributePrefix
                          + string.Join(" ", pieces);

        if (!string.IsNullOrWhiteSpace(defaultValue))
            declaration += " = " + defaultValue;

        declaration += ";";

        if (!string.IsNullOrWhiteSpace(field.TrailingComment))
            declaration += " " + field.TrailingComment.TrimStart();

        return declaration;
    }

    private static string MakeUniqueSubfileName(CodeFile file, string baseName)
    {
        if (file.Subfiles.All(subfile => !string.Equals(subfile.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        var suffix = 2;
        while (file.Subfiles.Any(subfile =>
                   string.Equals(subfile.Name, baseName + suffix, StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
        }

        return baseName + suffix;
    }

    private static bool HasTopLevelComma(string value)
    {
        var angleDepth = 0;
        var bracketDepth = 0;

        foreach (var character in value)
        {
            switch (character)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth = Math.Max(0, angleDepth - 1);
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case ',' when angleDepth == 0 && bracketDepth == 0:
                    return true;
            }
        }

        return false;
    }

    private static string NormalizeSpacing(string value)
    {
        return string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeNewlines(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
    }

    private static int BraceDelta(string line)
    {
        var delta = 0;
        var inString = false;
        var inChar = false;
        var escaped = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];

            if (!inString && !inChar && character == '/'
                && index + 1 < line.Length && line[index + 1] == '/')
            {
                break;
            }

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if ((inString || inChar) && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (!inChar && character == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && character == '\'')
            {
                inChar = !inChar;
                continue;
            }

            if (inString || inChar)
                continue;

            if (character == '{')
                delta++;
            else if (character == '}')
                delta--;
        }

        return delta;
    }

    private static void CollapseExcessBlankLines(List<string> lines)
    {
        for (var index = lines.Count - 1; index >= 1; index--)
        {
            if (string.IsNullOrWhiteSpace(lines[index])
                && string.IsNullOrWhiteSpace(lines[index - 1]))
            {
                lines.RemoveAt(index);
            }
        }
    }
}

public sealed record DetectedField(
    CodeSubfile Subfile,
    int LineNumber,
    string Indentation,
    string AttributePrefix,
    string Access,
    string Modifiers,
    string Type,
    string Name,
    string DefaultValue,
    string TrailingComment);

public sealed record AddedField(CodeSubfile Subfile, string VariableName);
