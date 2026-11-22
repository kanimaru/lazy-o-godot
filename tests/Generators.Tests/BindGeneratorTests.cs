using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using GenUtilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Generators.Tests;

public class BindGeneratorTests {
    private const string GodotStubs = """
        namespace Godot {
            public class GodotObject {}
            public class Node : GodotObject {
                public T GetNode<T>(string path) where T : class => null!;
            }
            public class Control : Node {}
            public class Button : Control {}
            public class Node3D : Node {}
        }
        """;

    private const string GameSource = """
        namespace Game.Ui {
            public partial class HeatJoystick : Godot.Control {}
            public partial class SliderJoystick : Godot.Control {}
        }

        namespace Game {
            public partial class ClawGame : Godot.Node3D {
                public void Ready() {
                    BindNodes();
                }
            }
        }
        """;

    private const string HeatJoystickScene = """
        [gd_scene format=3 uid="uid://joystick"]

        [ext_resource type="Script" uid="uid://script" path="res://src/HeatJoystick.cs" id="1_s"]

        [node name="HeatJoystick" type="Control"]
        script = ExtResource("1_s")

        [node name="Knob" type="Control" parent="."]
        """;

    private const string HorizontalVariantScene = """
        [gd_scene format=3 uid="uid://horizontal"]

        [ext_resource type="PackedScene" uid="uid://joystick" path="res://src/HeatJoystick.tscn" id="1_06ayk"]

        [node name="JoystickBase" instance=ExtResource("1_06ayk")]
        offset_left = -52.0

        [node name="Left" type="Control" parent="." index="1"]
        """;

    private const string NestedVariantScene = """
        [gd_scene format=3 uid="uid://nested"]

        [ext_resource type="PackedScene" uid="uid://horizontal" path="res://client/joystick_horizontal.tscn" id="1_h"]

        [node name="JoystickWide" instance=ExtResource("1_h")]
        offset_left = -80.0
        """;

    private const string ScriptedVariantScene = """
        [gd_scene format=3 uid="uid://scripted"]

        [ext_resource type="PackedScene" uid="uid://joystick" path="res://src/HeatJoystick.tscn" id="1_j"]
        [ext_resource type="Script" path="res://src/SliderJoystick.cs" id="2_s"]

        [node name="Slider" instance=ExtResource("1_j")]
        script = ExtResource("2_s")
        """;

    [Fact]
    public void InstanceOfVariantSceneIsTypedByTheScriptAtTheEndOfTheChain() {
        GeneratorResult result = Run(ClawScene("res://client/joystick_horizontal.tscn"));

        Assert.Contains("private Game.Ui.HeatJoystick _joystick;", result.Binder);
        Assert.Contains("private Button _drop;", result.Binder);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void InstanceOfVariantOfVariantSceneIsTypedByTheScriptAtTheEndOfTheChain() {
        GeneratorResult result = Run(ClawScene("res://client/joystick_wide.tscn"));

        Assert.Contains("private Game.Ui.HeatJoystick _joystick;", result.Binder);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ScriptOnTheVariantRootWinsOverTheInstancedScenesScript() {
        GeneratorResult result = Run(ClawScene("res://client/joystick_slider.tscn"));

        Assert.Contains("private Game.Ui.SliderJoystick _joystick;", result.Binder);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void UnresolvableInstanceIsReportedAndTheOtherNodesAreStillBound() {
        GeneratorResult result = Run(ClawScene("res://client/missing.tscn"));

        Assert.DoesNotContain("_joystick", result.Binder);
        Assert.Contains("private Button _drop;", result.Binder);
        Diagnostic warning = Assert.Single(result.GeneratorDiagnostics);
        Assert.Equal("LZ0002", warning.Id);
        Assert.Contains("Joystick", warning.GetMessage());
    }

    private static string ClawScene(string joystickPath) {
        return $$"""
            [gd_scene format=3 uid="uid://claw"]

            [ext_resource type="Script" path="res://src/ClawGame.cs" id="1_c"]
            [ext_resource type="PackedScene" uid="uid://any" path="{{joystickPath}}" id="7_6i3aa"]

            [node name="ClawGame" type="Node3D"]
            script = ExtResource("1_c")

            [node name="UI" type="Control" parent="."]

            [node name="Joystick" parent="UI" instance=ExtResource("7_6i3aa")]
            unique_name_in_owner = true

            [node name="Drop" type="Button" parent="UI"]
            unique_name_in_owner = true
            """;
    }

    private static GeneratorResult Run(string clawScene) {
        IEnumerable<MetadataReference> references = ((string)System.AppContext
                .GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Game",
            [CSharpSyntaxTree.ParseText(GodotStubs), CSharpSyntaxTree.ParseText(GameSource)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ImmutableArray<AdditionalText> scenes = [
            new SceneText("/project/src/HeatJoystick.tscn", HeatJoystickScene),
            new SceneText("/project/client/joystick_horizontal.tscn", HorizontalVariantScene),
            new SceneText("/project/client/joystick_wide.tscn", NestedVariantScene),
            new SceneText("/project/client/joystick_slider.tscn", ScriptedVariantScene),
            new SceneText("/project/src/ClawGame.tscn", clawScene),
        ];
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new BindGenerator().AsSourceGenerator()],
            additionalTexts: scenes);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);

        GeneratorRunResult run = driver.GetRunResult().Results.Single();
        string binder = run.GeneratedSources
            .Where(source => source.HintName == "ClawGame.Binder.g.cs")
            .Select(source => source.SourceText.ToString())
            .SingleOrDefault() ?? "";
        List<Diagnostic> errors = output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();
        return new GeneratorResult(binder, errors, diagnostics.ToList());
    }

    private sealed record GeneratorResult(
        string Binder, List<Diagnostic> Errors, List<Diagnostic> GeneratorDiagnostics);

    private sealed class SceneText : AdditionalText {
        private readonly string _content;

        public SceneText(string path, string content) {
            Path = path;
            _content = content;
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) {
            return SourceText.From(_content);
        }
    }
}
