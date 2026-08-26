using System.Text.RegularExpressions;
using codeloomapp.Models;

namespace codeloomapp.Services;

public static class FlowAnalysisService
{
    private static readonly HashSet<string> UnityCallbacks = new(StringComparer.Ordinal)
    {
        "Reset", "Awake", "OnEnable", "Start", "FixedUpdate", "Update", "LateUpdate",
        "OnGUI", "OnDisable", "OnDestroy", "OnValidate", "OnApplicationFocus",
        "OnApplicationPause", "OnApplicationQuit", "OnDrawGizmos", "OnDrawGizmosSelected",
        "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit", "OnCollisionEnter2D",
        "OnCollisionStay2D", "OnCollisionExit2D", "OnTriggerEnter", "OnTriggerStay",
        "OnTriggerExit", "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
        "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit", "OnMouseOver",
        "OnMouseDrag", "OnAnimatorMove", "OnAnimatorIK"
    };

    private static readonly HashSet<string> ControlFlowNames = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "lock", "using",
        "nameof", "typeof", "sizeof", "default", "checked", "unchecked"
    };

    private static readonly Regex MethodRegex = new(
        @"(?m)^\s*(?:\[[^\]\r\n]+\]\s*)*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|new|partial)\s+)*(?:(?:[A-Za-z_][A-Za-z0-9_<>,?.\[\]]*\s+)+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*(?:where\s+[^\r\n{=>]+\s*)?(?:\{|=>)",
        RegexOptions.Compiled);

    private static readonly Regex InvocationRegex = new(
        @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex NoiseRegex = new(
        "//.*?$|/\\*.*?\\*/|\\$?@\"(?:\"\"|[^\"])*\"|\\$?\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

    public static FlowAnalysisResult Analyze(CodeFile file)
    {
        var subfileOrder = file.Subfiles
            .Select((subfile, index) => new { subfile, index })
            .ToDictionary(item => item.subfile, item => item.index);

        var declarations = new List<FlowMethodDeclaration>();
        foreach (var subfile in file.Subfiles)
        {
            var searchableCode = MaskNoise(subfile.Code ?? string.Empty);
            foreach (Match match in MethodRegex.Matches(searchableCode))
            {
                declarations.Add(new FlowMethodDeclaration(
                    subfile,
                    match.Groups["name"].Value,
                    match.Groups["name"].Index));
            }
        }

        var methodsByName = declarations
            .GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var connections = new List<FlowConnection>();

        foreach (var source in file.Subfiles)
        {
            var searchableCode = MaskNoise(source.Code ?? string.Empty);
            var declarationOffsets = declarations
                .Where(declaration => ReferenceEquals(declaration.Subfile, source))
                .Select(declaration => declaration.NameOffset)
                .ToHashSet();

            foreach (Match invocation in InvocationRegex.Matches(searchableCode))
            {
                var methodName = invocation.Groups["name"].Value;
                var nameOffset = invocation.Groups["name"].Index;

                if (ControlFlowNames.Contains(methodName)
                    || declarationOffsets.Contains(nameOffset)
                    || !methodsByName.TryGetValue(methodName, out var possibleTargets))
                {
                    continue;
                }

                var targets = possibleTargets
                    .Where(target => !ReferenceEquals(target.Subfile, source))
                    .GroupBy(target => target.Subfile)
                    .Select(group => group.First())
                    .OrderBy(target => subfileOrder[target.Subfile])
                    .ToList();

                foreach (var target in targets)
                {
                    if (connections.Any(existing =>
                            ReferenceEquals(existing.Source, source)
                            && ReferenceEquals(existing.Target, target.Subfile)
                            && string.Equals(existing.MethodName, methodName, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    connections.Add(new FlowConnection(
                        source,
                        target.Subfile,
                        methodName,
                        invocation.Index));
                }
            }
        }

        var nodes = new List<FlowNode>();
        foreach (var subfile in file.Subfiles)
        {
            var declaredMethods = declarations
                .Where(declaration => ReferenceEquals(declaration.Subfile, subfile))
                .Select(declaration => declaration.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var outgoing = connections
                .Where(connection => ReferenceEquals(connection.Source, subfile))
                .OrderBy(connection => connection.Order)
                .ToList();

            var incoming = connections
                .Where(connection => ReferenceEquals(connection.Target, subfile))
                .OrderBy(connection => subfileOrder[connection.Source])
                .ToList();

            var hasUnityEntry = declaredMethods.Any(UnityCallbacks.Contains);
            var isEntryPoint = hasUnityEntry || (incoming.Count == 0 && outgoing.Count > 0);

            nodes.Add(new FlowNode
            {
                Subfile = subfile,
                Name = subfile.Name,
                Role = string.IsNullOrWhiteSpace(subfile.Role) ? "No role" : subfile.Role,
                Purpose = string.IsNullOrWhiteSpace(subfile.Purpose) ? "No plain-English purpose yet." : subfile.Purpose,
                Section = CodeAssembler.Classify(file, subfile).Section,
                MethodsText = declaredMethods.Count == 0
                    ? "No methods declared"
                    : string.Join(", ", declaredMethods.Select(name => name + "()")),
                CallsText = outgoing.Count == 0
                    ? "None detected"
                    : string.Join(", ", outgoing.Select(connection =>
                        $"{connection.MethodName}() → {connection.Target.Name}")),
                CalledByText = incoming.Count == 0
                    ? "None detected"
                    : string.Join(", ", incoming.Select(connection => connection.Source.Name).Distinct()),
                IsEntryPoint = isEntryPoint,
                EntryLabel = hasUnityEntry ? "UNITY ENTRY" : isEntryPoint ? "ENTRY" : string.Empty,
                OriginalIndex = subfileOrder[subfile]
            });
        }

        var entryNodes = nodes
            .Where(node => node.IsEntryPoint)
            .OrderBy(node => node.OriginalIndex)
            .ToList();

        if (entryNodes.Count == 0 && connections.Count > 0)
        {
            var firstConnected = nodes
                .Where(node => connections.Any(connection => ReferenceEquals(connection.Source, node.Subfile)))
                .OrderBy(node => node.OriginalIndex)
                .FirstOrDefault();

            if (firstConnected is not null)
                entryNodes.Add(firstConnected);
        }

        var paths = new List<FlowPath>();
        foreach (var entryNode in entryNodes)
        {
            var steps = BuildPath(entryNode, declarations, connections, file, subfileOrder);
            if (steps.Count == 0)
                continue;

            var entryMethod = steps[0].MethodName;
            var isUnity = declarations.Any(declaration =>
                ReferenceEquals(declaration.Subfile, entryNode.Subfile)
                && UnityCallbacks.Contains(declaration.Name));

            paths.Add(new FlowPath
            {
                Label = isUnity ? $"{entryMethod} execution" : $"{entryNode.Name} flow",
                Description = isUnity
                    ? $"Starts when Unity calls {entryMethod}; arrows follow detected calls in source order."
                    : "Starts from a code section with no detected callers; arrows follow detected calls in source order.",
                Steps = steps
            });
        }

        var connectedSubfiles = connections
            .SelectMany(connection => new[] { connection.Source, connection.Target })
            .Distinct()
            .Count();

        return new FlowAnalysisResult
        {
            Paths = paths,
            Nodes = nodes
                .OrderByDescending(node => node.IsEntryPoint)
                .ThenBy(node => node.OriginalIndex)
                .ToList(),
            ConnectionCount = connections.Count,
            Summary = connections.Count == 0
                ? $"{file.Subfiles.Count} subfiles · no cross-subfile method calls detected yet"
                : $"{file.Subfiles.Count} subfiles · {connections.Count} detected calls · {connectedSubfiles} connected subfiles"
        };
    }

    private static List<FlowStep> BuildPath(
        FlowNode entryNode,
        IReadOnlyList<FlowMethodDeclaration> declarations,
        IReadOnlyList<FlowConnection> connections,
        CodeFile file,
        IReadOnlyDictionary<CodeSubfile, int> subfileOrder)
    {
        var steps = new List<FlowStep>();
        var visited = new HashSet<CodeSubfile>();

        var entryDeclaration = declarations
            .Where(declaration => ReferenceEquals(declaration.Subfile, entryNode.Subfile))
            .OrderByDescending(declaration => UnityCallbacks.Contains(declaration.Name))
            .ThenBy(declaration => declaration.NameOffset)
            .FirstOrDefault();

        AddStep(
            entryNode.Subfile,
            entryDeclaration is null ? entryNode.Name : entryDeclaration.Name + "()",
            true);

        Visit(entryNode.Subfile);

        for (var index = 0; index < steps.Count; index++)
            steps[index].Connector = index == steps.Count - 1 ? string.Empty : "↓";

        return steps;

        void Visit(CodeSubfile source)
        {
            var outgoing = connections
                .Where(connection => ReferenceEquals(connection.Source, source))
                .OrderBy(connection => connection.Order)
                .ThenBy(connection => subfileOrder[connection.Target])
                .ToList();

            foreach (var connection in outgoing)
            {
                if (!visited.Add(connection.Target))
                    continue;

                AddStep(connection.Target, connection.MethodName + "()", false);
                Visit(connection.Target);
            }
        }

        void AddStep(CodeSubfile subfile, string methodName, bool isEntry)
        {
            visited.Add(subfile);
            steps.Add(new FlowStep
            {
                Subfile = subfile,
                SubfileName = subfile.Name,
                MethodName = methodName,
                Role = string.IsNullOrWhiteSpace(subfile.Role) ? "No role" : subfile.Role,
                Section = CodeAssembler.Classify(file, subfile).Section,
                KindLabel = isEntry ? "ENTRY" : "CALL"
            });
        }
    }

    private static string MaskNoise(string code)
    {
        return NoiseRegex.Replace(code, match =>
        {
            var characters = match.Value
                .Select(character => character is '\r' or '\n' ? character : ' ')
                .ToArray();
            return new string(characters);
        });
    }
}

internal sealed record FlowMethodDeclaration(CodeSubfile Subfile, string Name, int NameOffset);
internal sealed record FlowConnection(CodeSubfile Source, CodeSubfile Target, string MethodName, int Order);

public sealed class FlowAnalysisResult
{
    public IReadOnlyList<FlowPath> Paths { get; init; } = Array.Empty<FlowPath>();
    public IReadOnlyList<FlowNode> Nodes { get; init; } = Array.Empty<FlowNode>();
    public int ConnectionCount { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class FlowPath
{
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<FlowStep> Steps { get; init; } = Array.Empty<FlowStep>();
}

public sealed class FlowStep
{
    public required CodeSubfile Subfile { get; init; }
    public string SubfileName { get; init; } = string.Empty;
    public string MethodName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Section { get; init; } = string.Empty;
    public string KindLabel { get; init; } = string.Empty;
    public string Connector { get; set; } = string.Empty;
}

public sealed class FlowNode
{
    public required CodeSubfile Subfile { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string Section { get; init; } = string.Empty;
    public string MethodsText { get; init; } = string.Empty;
    public string CallsText { get; init; } = string.Empty;
    public string CalledByText { get; init; } = string.Empty;
    public bool IsEntryPoint { get; init; }
    public string EntryLabel { get; init; } = string.Empty;
    public int OriginalIndex { get; init; }
}
