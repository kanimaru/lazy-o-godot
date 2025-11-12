using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GenUtilities;

[Generator]
public class BindGenerator : IIncrementalGenerator {
	public void Initialize(IncrementalGeneratorInitializationContext context) {
		var classProvider = context.SyntaxProvider
			.CreateSyntaxProvider(
				(s, _) => s is ClassDeclarationSyntax,
				(ctx, _) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol)
			.Where(symbol => symbol is not null);

		var sceneFilesProvider = context.AdditionalTextsProvider
			.Where(file => file.Path.EndsWith(".tscn", System.StringComparison.OrdinalIgnoreCase));

		var classesAndScenesProvider = classProvider.Combine(sceneFilesProvider.Collect());

		var finalProvider = classesAndScenesProvider.Combine(context.CompilationProvider);

		context.RegisterSourceOutput(finalProvider, (spc, source) => {
			var classAndScenes = source.Left;
			var compilation = source.Right;
			var classSymbol = classAndScenes.Left;
			var sceneFiles = classAndScenes.Right;

			var sceneFile = sceneFiles.FirstOrDefault(f =>
				Path.GetFileNameWithoutExtension(f.Path) == classSymbol!.Name);

			if (sceneFile is not null) {
				GenerateCode(spc, compilation, classSymbol!, sceneFile);
			}
		});
	}

	private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol root) {
		foreach (var namespaceOrType in root.GetMembers())
			if (namespaceOrType is INamespaceSymbol subNs) {
				foreach (var type in GetAllTypes(subNs)) yield return type;
			} else if (namespaceOrType is INamedTypeSymbol type) {
				yield return type;
			}
	}
	
	private class ClassDefinition {
		public string NamespaceName { get; set; } = "";
		public string ClassName { get; set; } = "";
		public HashSet<string> UsingNamespaceName { get; set; } = [];

		public List<MemberDefinition> MemberDefinitions { get; } = [];
	}

	private class MemberDefinition {
		public string Type { get; set; } = "";
		public string Name { get; set; } = "";
		public string NodeName { get; set; } = "";
	}

	private void GenerateCode(SourceProductionContext context, Compilation compilation, INamedTypeSymbol classSymbol,
		AdditionalText sceneFile) {
		var sceneContent = sceneFile.GetText(context.CancellationToken)?.ToString();
		if (sceneContent == null || string.IsNullOrEmpty(sceneContent)) return;

		var scriptResources = new Dictionary<string, string>();
		var sceneResources = new Dictionary<string, string>();
		var extResourceRegex =
			new Regex(
				"""^\[ext_resource type="([^"]+)"(?: uid="[^"]+")? path="res:\/\/([^"]+)" id="([^"]+)"\]""",
				RegexOptions.Multiline);

		foreach (Match match in extResourceRegex.Matches(sceneContent)) {
			var type = match.Groups[1].Value;
			var path = match.Groups[2].Value;
			var id = match.Groups[3].Value;

			if (type == "Script" && path.EndsWith(".cs"))
				scriptResources[id] = path;
			else if (type == "PackedScene" && path.EndsWith(".tscn"))
				sceneResources[id] = path;
		}

		var allTypes = GetAllTypes(compilation.GlobalNamespace).ToLookup(t => t.Name);

		var classDefinition = new ClassDefinition {
			ClassName = classSymbol.Name,
			NamespaceName = classSymbol.ContainingNamespace.ToDisplayString(),
		};
		classDefinition.UsingNamespaceName.Add("Godot");

		var contentLines =
			new List<string>(sceneContent.Split(['\r', '\n'], System.StringSplitOptions.RemoveEmptyEntries));
		var nodeBlocks = new List<string>();
		var currentNodeBlock = new StringBuilder();
		foreach (var line in contentLines) {
			if (line.StartsWith("[")) {
				if (currentNodeBlock.Length > 0) nodeBlocks.Add(currentNodeBlock.ToString());
				currentNodeBlock.Clear();
			}

			currentNodeBlock.AppendLine(line);
		}

		if (currentNodeBlock.Length > 0) nodeBlocks.Add(currentNodeBlock.ToString());

		foreach (var block in nodeBlocks) {
			if (!block.StartsWith("[node ") || !block.Contains("unique_name_in_owner = true")) continue;

			var nameMatch = Regex.Match(block, @"name\s*=\s*""([^""]+)""");
			if (!nameMatch.Success) continue;
			var nodeName = nameMatch.Groups[1].Value;

			string? nodeType = null;

			var instanceMatch = Regex.Match(block, @"instance\s*=\s*ExtResource\(""([^""]+)""\)");
			if (instanceMatch.Success) // It's an instanced scene
			{
				var instanceId = instanceMatch.Groups[1].Value;
				if (sceneResources.TryGetValue(instanceId, out var scenePath)) {
					var sceneClassName = Path.GetFileNameWithoutExtension(scenePath);
					var typeSymbol = allTypes[sceneClassName].FirstOrDefault();
					if (typeSymbol != null) {
						nodeType = typeSymbol.ToDisplayString();
						if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
							classDefinition.UsingNamespaceName.Add(
								typeSymbol.ContainingNamespace.ToDisplayString());
					}
				}
			} else // It's a regular node
			{
				var typeMatch = Regex.Match(block, @"type\s*=\s*""([^""]+)""");
				if (!typeMatch.Success) continue; // Regular nodes must have a type.

				nodeType = typeMatch.Groups[1].Value; // Default to the node's type

				var scriptMatch = Regex.Match(block, @"script\s*=\s*ExtResource\(""([^""]+)""\)");
				if (scriptMatch.Success) // Check for a script to get a more specific type
				{
					var scriptId = scriptMatch.Groups[1].Value;
					if (scriptResources.TryGetValue(scriptId, out var scriptPath)) {
						var scriptClassName = Path.GetFileNameWithoutExtension(scriptPath);
						var typeSymbol = allTypes[scriptClassName].FirstOrDefault();
						if (typeSymbol != null) {
							nodeType = typeSymbol.ToDisplayString(); // Override with a script class
							if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
								classDefinition.UsingNamespaceName.Add(
									typeSymbol.ContainingNamespace.ToDisplayString());
						}
					}
				}
			}

			if (string.IsNullOrEmpty(nodeName) || string.IsNullOrEmpty(nodeType)) continue;

			var memberName = $"_{char.ToLowerInvariant(nodeName[0])}{nodeName.Substring(1)}";
			classDefinition.MemberDefinitions.Add(new MemberDefinition {
				Name = memberName,
				Type = nodeType!,
				NodeName = nodeName,
			});
		}


		if (classDefinition.MemberDefinitions.Count > 0) GenerateClass(context, classDefinition);
	}

	private static void GenerateClass(SourceProductionContext context, ClassDefinition definition) {
		var fieldCode = new StringBuilder();
		foreach (var memberDefinition in definition.MemberDefinitions) {
			fieldCode.AppendLine($"    private {memberDefinition.Type} {memberDefinition.Name};");
		}

		var memberCode = new StringBuilder();
		foreach (var memberDefinition in definition.MemberDefinitions) {
			memberCode.AppendLine(
				$"""        {memberDefinition.Name} = GetNode<{memberDefinition.Type}>("%{memberDefinition.NodeName}");""");
		}

		var usingCode = new StringBuilder();
		foreach (var namespaceName in definition.UsingNamespaceName) usingCode.AppendLine($"using {namespaceName};");

		// Build up the source code
		var code = $$"""
		             // <auto-generated/>

		             using System;
		             {{usingCode}}

		             namespace {{definition.NamespaceName}};

		             partial class {{definition.ClassName}}
		             {
		             {{fieldCode}}
		                 private void BindNodes() {
		             {{memberCode}}
		                 }
		             }

		             """;
		context.AddSource($"{definition.ClassName}.Binder.g.cs", SourceText.From(code, Encoding.UTF8));
	}
}
