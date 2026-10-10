using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace GenUtilities;

/// <summary>What a scene's root node is to C#: the class its script declares, or the Godot class it is.</summary>
internal static class SceneRoot {
    private static readonly Regex ScriptResource =
        new("""^\[ext_resource type="Script"(?: uid="[^"]+")? path="res:\/\/([^"]+\.cs)" id="([^"]+)"\]""", RegexOptions.Multiline);

    private static readonly Regex RootBlock = new(@"^\[node [^\n]*\]\r?\n((?:(?!\[)[^\n]*\n)*)", RegexOptions.Multiline);

    /// <summary>
    /// The fully qualified C# class the scene's root runs: the script on its first node, a class named like the script file
    /// and derived from <c>Godot.Node</c>. <c>null</c> for a GDScript root, no script, or a class that cannot be found.
    /// </summary>
    public static string? ScriptClass(Compilation compilation, string content, CancellationToken cancellationToken) {
        var scripts = ScriptResource.Matches(content).Cast<Match>()
            .ToDictionary(match => match.Groups[2].Value, match => match.Groups[1].Value);
        Match root = RootBlock.Match(content);
        if (!root.Success) {
            return null;
        }

        Match script = Regex.Match(root.Value, @"^script\s*=\s*ExtResource\(""([^""]+)""\)", RegexOptions.Multiline);
        if (!script.Success || !scripts.TryGetValue(script.Groups[1].Value, out string? scriptPath)) {
            return null;
        }

        return NodeClass(compilation, System.IO.Path.GetFileNameWithoutExtension(scriptPath), cancellationToken);
    }

    /// <summary>The C# class the root is: <see cref="ScriptClass"/>, else its Godot class, else <c>Godot.Node</c>.</summary>
    public static string NodeType(Compilation compilation, string content, CancellationToken cancellationToken) {
        if (ScriptClass(compilation, content, cancellationToken) is { } scriptClass) {
            return scriptClass;
        }

        Match root = RootBlock.Match(content);
        Match type = root.Success ? Regex.Match(root.Value, @"type\s*=\s*""([^""]+)""") : Match.Empty;
        return type.Success ? "global::Godot." + GodotTypeName(compilation, type.Groups[1].Value) : "global::Godot.Node";
    }

    /// <summary>The fully qualified class called <paramref name="className"/> that derives from <c>Godot.Node</c>, or <c>null</c>.</summary>
    public static string? NodeClass(Compilation compilation, string className, CancellationToken cancellationToken) {
        INamedTypeSymbol? type = compilation.GetSymbolsWithName(className, SymbolFilter.Type, cancellationToken)
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault(IsNode);
        return type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>
    /// The C# name of the Godot class a scene names: the bindings spell acronyms in Pascal case, so the scene's
    /// <c>GPUParticles3D</c> is <c>GpuParticles3D</c> in C# (<c>CSGBox3D</c>, <c>HTTPRequest</c>, ... likewise).
    /// </summary>
    public static string GodotTypeName(Compilation compilation, string sceneType) {
        if (compilation.GetTypeByMetadataName("Godot." + sceneType) is not null) {
            return sceneType;
        }

        INamespaceSymbol? godot = compilation.GlobalNamespace.GetNamespaceMembers().FirstOrDefault(n => n.Name == "Godot");
        INamedTypeSymbol? match = godot?.GetTypeMembers()
            .FirstOrDefault(t => string.Equals(t.Name, sceneType, StringComparison.OrdinalIgnoreCase));
        return match?.Name ?? sceneType;
    }

    private static bool IsNode(INamedTypeSymbol type) {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType) {
            if (current.ToDisplayString() == "Godot.Node") {
                return true;
            }
        }

        return false;
    }
}
