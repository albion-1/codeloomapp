namespace codeloomapp.Services;

public static class NameSafetyService
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum",
        "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
        "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint",
        "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",

        // These contextual keywords are legal in some positions, but rejecting them
        // here keeps beginner-created class and field names clear and predictable.
        "add", "alias", "and", "ascending", "async", "await", "by", "descending", "dynamic", "equals",
        "file", "from", "get", "global", "group", "init", "into", "join", "let", "managed", "nameof",
        "not", "notnull", "on", "or", "orderby", "partial", "record", "remove", "required", "scoped",
        "select", "set", "unmanaged", "value", "var", "when", "where", "with", "yield"
    };

    private static readonly HashSet<string> WindowsDeviceNames = BuildWindowsDeviceNames();

    public static bool IsValidCSharpIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (CSharpKeywords.Contains(text))
            return false;

        if (!IsIdentifierStart(text[0]))
            return false;

        return text.Skip(1).All(IsIdentifierPart);
    }

    public static string MakeSafeCSharpIdentifier(string? value, string fallback = "NewScript")
    {
        var source = value?.Trim() ?? string.Empty;
        var cleaned = new string(source
            .Where(character => char.IsLetterOrDigit(character) || character == '_')
            .ToArray());

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = fallback;

        if (!IsIdentifierStart(cleaned[0]))
            cleaned = "_" + cleaned;

        if (CSharpKeywords.Contains(cleaned))
            cleaned = "_" + cleaned;

        return cleaned;
    }

    public static bool IsValidWindowsFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var name = value.Trim();
        if (name is "." or ".."
            || name.EndsWith(' ')
            || name.EndsWith('.'))
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/')
            || name.Contains('\\'))
        {
            return false;
        }

        return !IsReservedWindowsDeviceName(name);
    }

    public static string MakeSafeWindowsPathPart(string? value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? string.Empty)
            .Select(character => invalid.Contains(character) || character is '/' or '\\' ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
            cleaned = fallback;

        if (IsReservedWindowsDeviceName(cleaned))
            cleaned = "_" + cleaned;

        return cleaned;
    }

    public static bool IsReservedWindowsDeviceName(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value.Trim().TrimEnd('.', ' '));
        return WindowsDeviceNames.Contains(stem);
    }

    private static bool IsIdentifierStart(char character)
    {
        return character == '_' || char.IsLetter(character);
    }

    private static bool IsIdentifierPart(char character)
    {
        return character == '_' || char.IsLetterOrDigit(character);
    }

    private static HashSet<string> BuildWindowsDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$"
        };

        for (var index = 1; index <= 9; index++)
        {
            names.Add("COM" + index);
            names.Add("LPT" + index);
        }

        return names;
    }
}
