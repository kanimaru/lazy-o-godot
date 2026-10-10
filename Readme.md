# lazy-o-godot — Roslyn source generators for Godot C#

A set of Roslyn **incremental source generators** and one analyzer that remove boilerplate from Godot 4 C#
projects: node binding, scene and autoload access, input actions, global groups, resource loading, singletons,
node metadata interfaces, logging and multiplayer synchronization.

Everything lives in one project, `dev.kani.godot.generator.csproj`:

| | |
|---|---|
| Assembly | `Generators.dll` |
| Root namespace | `GenUtilities` |
| NuGet package id | `Dev.Kani.Godot.Generator` |
| Target framework | `netstandard2.1` (Roslyn 4.3) |

The generators are consumed as an **analyzer**, never as a runtime reference. Nothing from this assembly ends
up in your game's output — only the C# it emits does.

## The generators

All generated types and attributes live in the `GenUtilities` namespace. Attributes are emitted by the
generators themselves (`RegisterPostInitializationOutput`), so you do not reference a separate attribute
package — just `using GenUtilities;`.

| Generator | Triggered by | Generates |
|---|---|---|
| [ActionGenerator](ActionGenerator.cs) | `project.godot` in `AdditionalFiles` | `GenUtilities.Actions` |
| [GroupGenerator](GroupGenerator.cs) | `project.godot` in `AdditionalFiles` | `GenUtilities.Groups` |
| [SceneGenerator](SceneGenerator.cs) | `*.tscn` in `AdditionalFiles` | `GenUtilities.Scenes` |
| [AutoloadGenerator](AutoloadGenerator.cs) | `project.godot` (and `*.tscn` to type them) in `AdditionalFiles` | `GenUtilities.Autoloads` |
| [BindGenerator](BindGenerator.cs) | a `*.tscn` whose file name matches a class name | `<Class>.Binder.g.cs` |
| [AutowireGenerator](AutowireGenerator.cs) | `[Autowire(...)]` on a property | `<Class>.Autowire.g.cs` |
| [SingletonGenerator](SingletonGenerator.cs) | `[Singleton]` on a class | `<Class>.g.cs` |
| [MetaGenerator](MetaGenerator.cs) | `[RegisterMeta]` on a class/record/interface | `<Type>.g.cs` |
| [LoggerGenerator](LoggerGenerator.cs) | `[Log]` on a class | `<Class>.Logger.g.cs` |
| [SyncGenerator](SyncGenerator.cs) | `[Sync]` on a field/property, `[SyncSettings]` on the class | `<Class>.Sync.g.cs` |
| [ResourceGenerator](ResourceGenerator.cs) | `[ResourcePath("dir")]` on a class + `*.tres` in `AdditionalFiles` | `<Class>.Res.g.cs` |
| [RxSchedulerAnalyzer](RxSchedulerAnalyzer.cs) | Rx.NET time-based operators | warning `LZ0001` |

### ActionGenerator — `Actions`

Reads the `[input]` section of `project.godot` and emits one `static StringName` per input action into
`partial class GenUtilities.Actions`. The field name is the action key with every non-alphanumeric character
stripped, **case kept** (`click` stays `click`); the value is the untouched action name.

```ini
# project.godot
[input]
move_left={...}
```

```csharp
if (Input.IsActionPressed(Actions.move_left)) { ... }
```

### GroupGenerator — `Groups`

Same input file, `[global_group]` section. Emits one `static StringName` per global group into
`partial class GenUtilities.Groups`, with the group's description from `project.godot` as the XML doc comment.
Use it instead of string literals or constants of your own:

```csharp
GetTree().GetNodesInGroup(Groups.enemies);
```

### SceneGenerator — `Scenes`

For every `*.tscn` passed as an `AdditionalFile`, emits into `class GenUtilities.Scenes`:

- `public const string Path<Name>` — the `res://` path, derived from the file path relative to `$(ProjectDir)`;
- `public static T New<Name><T>() where T : Node` — `GD.Load<PackedScene>(...).Instantiate<T>()`;
- `public static <RootClass> New<Name>()` — only when the scene's root node runs a C# class: the same
  instantiation, already typed as that class.

`<Name>` is the file name with each `snake_case` part capitalized (`main_menu.tscn` → `PathMainMenu`,
`NewMainMenu<T>()`; `HeatJoystick.tscn` → `PathHeatJoystick`).

```csharp
HighscoreRow row = Scenes.NewHighscoreRow();           // root runs the C# class HighscoreRow
Node level = Scenes.NewLobby<Node>();                  // GDScript or script-less root
PackedScene packed = GD.Load<PackedScene>(Scenes.PathLobby);
```

The root's class is found through its `script` `ext_resource` (a `.cs` file named like the class, deriving
from `Godot.Node`). A GDScript root or a missing class gets only the generic `New<Name><T>()`.

### AutoloadGenerator — `Autoloads`

Reads the `[autoload]` section of `project.godot` and emits, for each entry, into
`static partial class GenUtilities.Autoloads`:

```csharp
public const string PathUserService = "/root/UserService";
public static UserService UserService => /* root.GetNode<UserService>(PathUserService) */;
public static UserService? UserServiceOrNull => /* null while the autoload is not in the tree */;
```

Use `…OrNull` where the autoload may not exist, such as `[Tool]` code running in the editor.

The member's type comes from what the autoload points at:

1. a `.cs` script: the class of that file;
2. a `.tscn`: the class its root runs (a `uid://…` entry is resolved through the scene headers; the scene must
   be an `AdditionalFile`), else the root's Godot class;
3. a GDScript, or anything the generator cannot resolve: `Godot.Node`.

```csharp
Autoloads.UserService.LoadAsync(id);          // instead of GetNode<UserService>("/root/UserService")
if (Autoloads.NotificationsOrNull is { } notifications) { ... }
```

An autoload whose name is not a valid C# identifier is skipped.

### BindGenerator — `%UniqueName` node fields

For each class whose name matches a `*.tscn` file name (case-insensitive, extension stripped; the folder does
not matter), scans that scene for nodes marked **Access as Unique Name** (`unique_name_in_owner = true`) and
emits a private field per node plus a `BindNodes()` method that fills them with `GetNode<T>("%NodeName")`.

The field name is the node name with a leading underscore and a lowercased first letter (`Title` → `_title`).
The field type is resolved in this order:

1. for an instanced scene whose instance carries its own `script`: that class (the override wins);
2. for an instanced scene: the class whose name matches the instanced `.tscn` file name;
3. failing that, the C# class on that instanced scene's **root** node, else its Godot type;
4. for a regular node: the C# class attached via its `script`, else the node's Godot type.

Godot types are spelled the way the C# bindings do (the scene's `GPUParticles3D` becomes `GpuParticles3D`).

Your class must be `partial`, and you call `BindNodes()` yourself, typically first thing in `_Ready()`:

```csharp
public partial class MainMenu : Control {   // MainMenu.tscn, next to MainMenu.cs
    public override void _Ready() {
        BindNodes();        // generated: _title = GetNode<Label>("%Title");
        _title.Text = "Hi";
    }
}
```

If callers fill the node before it enters the tree, bind when the scene is instantiated instead:

```csharp
public override void _Notification(int what) {
    if (what == NotificationSceneInstantiated) {
        BindNodes();
    }
}
```

`BindNodes()` fails loudly (Godot's own "node not found" error) for each unique node that is missing, so a
class whose nodes are optional keeps `GetNodeOrNull` for those.

### AutowireGenerator — `[Autowire]`

```csharp
[AttributeUsage(AttributeTargets.Property)]
Autowire(string nodeFieldName, string nodeMemberName)
```

Put it on a property to push that property's value into a member of a child node, safely before and after the
node is ready:

```csharp
public partial class MainMenu : Control {
    [Autowire(nameof(_title), nameof(Label.Text))]
    public string Title { get => _titleVal; set => UpdateTitle(value); }
}
```

For every annotated property the generator emits a backing field `_<property>Val`, an
`Update<Property>(value)` method that stores the value and writes it through to the node only when
`IsNodeReady()`, and a single `RefreshAllComponents()` that replays every stored value — call it once the node
tree exists (e.g. in `_Ready()`, after `BindNodes()`).

The emitted partial is declared `public partial class`, and the generated code calls `IsNodeReady()`, so the
annotated type must be a `public partial` class deriving from `Node`.

### SingletonGenerator — `[Singleton]`

On a `partial class`, emits `Instance`, a `_instance` backing field and `GetOrCreate()`, which instantiates the
scene through `Scenes.New<ClassName><T>()` and adds it to the scene tree root:

```csharp
[Singleton]
public partial class AudioManager : Node { }

AudioManager.Instance.Play(...);
```

Because it goes through `Scenes`, this requires a `<ClassName>.tscn` to be visible to
[SceneGenerator](#scenegenerator--scenes). A non-`partial` type is reported as error **MG001**. For an
autoload, prefer [`Autoloads`](#autoloadgenerator--autoloads).

### MetaGenerator — `[RegisterMeta]`

On a `partial` class, record or interface, emits a pair of statics that store/retrieve an implementation of
that type in a node's Godot metadata, keyed by the type name:

```csharp
public static T? GetImpl(Node node);          // the node itself if it already is a T, else its meta entry
public static void SetImpl(Node node, Variant impl);
```

Useful for attaching capability interfaces (`ITakeDamage`, …) to nodes that cannot inherit them. A
non-`partial` type is reported as error **MG001**.

### LoggerGenerator — `[Log]`

On a `partial class`, emits `private static SimpleLogger log = new SimpleLogger("<ClassName>");`. Generic
classes are handled (the type parameter list is reproduced on the partial).

> This is the one generator whose output has a **runtime dependency**: the emitted file does
> `using dev.kani.godot.util.logging;`, so the consuming project must reference `dev.kani.godot.util`.

### SyncGenerator — `[Sync]` / `[SyncSettings]`

Builds a Godot `MultiplayerSynchronizer` and its `SceneReplicationConfig` from annotated members.

```csharp
[SyncSettings(ReplicationInterval = 0.1f, PublicVisibility = true,
              VisibilityUpdateMode = MultiplayerSynchronizer.VisibilityUpdateModeEnum.Idle)]
public partial class Player : CharacterBody2D {
    [Sync(ReplicationMode = SceneReplicationConfig.ReplicationMode.OnChange, Spawn = true)]
    private int _health;

    [Export] public int Health { get => _health; set => SetHealth(value); }

    public override void _Ready() => StartSync();
}
```

Emitted per class: `_syncConfig`, `_synchronizer`, and a `StartSync()` that configures the synchronizer from
`[SyncSettings]`, registers a replicated property per `[Sync]` member and adds the synchronizer as a child.
Emitted per member: `Set<Name>(value)` (a no-op if unchanged, otherwise assigns and raises the event) and an
`event Action<T>? <Name>Changed`. `<Name>` is the member name with leading underscores trimmed and the first
letter uppercased.

`[SyncSettings]` knobs: `DeltaInterval`, `ReplicationInterval`, `PublicVisibility`, `VisibilityUpdateMode`.
`[Sync]` knobs: `ReplicationMode`, `Spawn`, `Logging`.

Note that the generator registers `".:<Name>"` as the replicated path but does **not** emit the property
itself — declaring the (usually `[Export]`) property that wraps the field is up to you. See
[Known quirks](#known-quirks) for the current limits of `Spawn` / `Logging`.

### ResourceGenerator — `[ResourcePath]`

```csharp
[ResourcePath("game_data/items")]
public partial class Items { }
```

Every `AdditionalFile` whose containing directory path ends with that string becomes a `static readonly`
field on the class, named after the file (extension stripped) and typed by the `script_class="…"` — or,
failing that, the `type="…"` — recorded on the first line of the `.tres`:

```csharp
public static readonly ItemData sword =
    GD.Load<ItemData>(ProjectSettings.GlobalizePath("res://game_data/items/sword.tres"));

public static ItemData[] All => [ sword, ... ];   // Resource[] if the types are mixed
```

It loads through `GlobalizePath` and names members after the file name verbatim (no case change), so it
suits tool-side data more than exported games.

### RxSchedulerAnalyzer — `LZ0001`

Rx.NET's time-based operators (`Timer`, `Interval`, `Delay`, `Throttle`, `Sample`, `Buffer`, `Window`,
`Timeout`, `Timestamp`, `TimeInterval`, …) default to the thread pool when you give them no scheduler. In
Godot the callback then touches the scene tree from a worker thread, which corrupts engine state
intermittently instead of failing loudly.

`LZ0001` (warning, category Threading) reports a call to `System.Reactive.Linq.Observable` that takes a
`TimeSpan` or `DateTimeOffset` (or is `Timestamp`/`TimeInterval`), passes no `IScheduler`, and has an
overload that takes one. Pass a scheduler that runs on the main thread (one driven from `_Process`):

```csharp
Observable.Timer(TimeSpan.FromSeconds(1), GodotSchedulers.Process);   // fine
Observable.Timer(TimeSpan.FromSeconds(1));                            // LZ0001
```

The analyzer only runs when `System.Reactive` is referenced. Generated code is not analyzed.

## Consuming it

Two supported paths. Either way the reference must be wired up as an **analyzer**, not a normal reference.

### 1. Local checkout (development)

With dotquay the project links a checkout and undoes it again:

```bash
dotquay link Dev.Kani.Godot.Generator <path-to-this-folder> --analyzer
dotquay link Dev.Kani.Godot.Generator --remove
```

Without it, a `ProjectReference` does the same job:

```xml
<ProjectReference Include="..\dev.kani.godot.generator\dev.kani.godot.generator.csproj"
                  PrivateAssets="true" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

`OutputItemType="Analyzer"` is what makes the compiler load the generators;
`ReferenceOutputAssembly="false"` keeps `Generators.dll` out of the game's output.

IDEs that host Roslyn (Rider and others) lock the analyzer DLL while they run, which makes a rebuild of a
linked checkout fail with `MSB3021`. Compile against a copy instead: the consumer's build can copy the
analyzer into `obj/` under a content-hashed name.

### 2. PackageReference (GitLab package registry)

```bash
dotquay add Dev.Kani.Godot.Generator
dotquay update Dev.Kani.Godot.Generator     # bump to the latest release
```

or by hand:

```xml
<PackageReference Include="Dev.Kani.Godot.Generator" Version="x.y.z" PrivateAssets="all" />
```

The package ships the generator at `analyzers/dotnet/cs/` and has no `lib/` folder and no declared
dependencies, so no extra MSBuild metadata is needed to make it act as an analyzer.

The feed is the project-scoped GitLab NuGet registry, which requires authentication even for reads. Add a
`nuget.config` next to your solution (dotquay writes this for you):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="lazy-o-godot"
         value="https://<gitlab-host>/api/v4/projects/<project-id>/packages/nuget/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <lazy-o-godot>
      <add key="Username" value="<your-gitlab-username>" />
      <add key="ClearTextPassword" value="%GITLAB_NUGET_TOKEN%" />
    </lazy-o-godot>
  </packageSourceCredentials>
</configuration>
```

The token is a **personal access token with `read_api` scope** (a project access or deploy token with
`read_package_registry` works too). NuGet expands `%VAR%` from the environment, so keep the token in
`GITLAB_NUGET_TOKEN` rather than committing it.

### Releases

[.gitlab-ci.yml](.gitlab-ci.yml) releases from the default branch:

- A green pipeline on `master` auto-tags the next patch version `vX.Y.Z`, publishes it to the registry and
  creates a GitLab Release for it — **only when `*.cs` or the csproj changed**. Editing the Readme or the
  pipeline does not burn a version.
- The tag is pushed with `$CI_JOB_TOKEN`, so the project must allow Git push requests with the job token. No
  credential is put into a remote URL.
- To cut a specific major/minor, push a `vX.Y.Z` tag yourself — that tag runs its own pipeline through publish
  and becomes the baseline the next patch bump counts from.
- The `<Version>` in the csproj is a local-`dotnet pack` fallback only; CI always overrides it with
  `-p:Version=`.

### Required `AdditionalFiles`

Several generators read Godot's own files rather than C#, and Roslyn only sees files declared as
`<AdditionalFiles>`. Without these entries the corresponding generators silently produce nothing.

```xml
<ItemGroup>
  <AdditionalFiles Include="project.godot" />                <!-- Actions, Groups, Autoloads -->
  <AdditionalFiles Include="src\**\*.tscn" />                <!-- Scenes, Bind, Singleton, Autoload typing -->
  <AdditionalFiles Include="game_data\**\*.tres" />          <!-- ResourceGenerator -->
</ItemGroup>
```

| Glob | Read by |
|---|---|
| `project.godot` | `ActionGenerator` (`[input]`), `GroupGenerator` (`[global_group]`), `AutoloadGenerator` (`[autoload]`) |
| `*.tscn` | `SceneGenerator`, `BindGenerator` — and therefore `SingletonGenerator`, which calls into `Scenes`; `AutoloadGenerator` types scene autoloads through them |
| `*.tres` | `ResourceGenerator`, for files under a directory named by a `[ResourcePath]` |

Only scenes that are `AdditionalFiles` get `Scenes` members, so test scenes and asset scenes you leave out
stay out of the API.

Scope the `.tres` globs to the directories your `[ResourcePath]` classes actually name.
`ResourceGenerator` does **not** filter by extension: any `AdditionalFile` sitting in a matching directory is
treated as a `.tres`, so a `*.gdshader` (or anything else without a `script_class=`/`type=` first line) landing
in one of those directories will produce uncompilable output. No generator in this repo reads `*.gdshader`
directly — including that glob is only safe while those files live outside every `[ResourcePath]` directory.

## Known quirks

- **`[Sync]`'s `Spawn` and `Logging`.** In `SyncGenerator`, `MemberDefinition.Spawn` is assigned twice — first
  from the `Spawn` argument, then overwritten from `Logging`. So `Spawn` currently follows the value of
  `Logging`, and `Logging` itself is never true, meaning the `log.Debug(...)` line in the generated setter is
  never emitted.
- **`[Sync]` on properties** registers the property for replication and generates its event, but the
  `ReplicationMode` / `Spawn` arguments are only read for *fields*.
- `SingletonGenerator` and `MetaGenerator` share the diagnostic id **MG001** for "type must be partial".
- `Scenes` is generated without an access modifier (internal); reach it from the project that declares the
  scenes.

## Code style

The project follows the [Google C# style guide](https://google.github.io/styleguide/csharp-style.html), encoded
in [.editorconfig](.editorconfig). The one deviation is 4-wide indents instead of 2. Format with:

```bash
dotnet format whitespace . --folder --exclude obj --exclude bin
```

## How To?

### How to debug?

- Use the [launchSettings.json](Properties/launchSettings.json) profile — a `DebugRoslynComponent` profile
  pointing `targetProject` at a real consumer project, e.g.:

  ```json
  {
    "profiles": {
      "DebugRoslynSourceGenerator": {
        "commandName": "DebugRoslynComponent",
        "targetProject": "../BlackHaul.Game/BlackHaul.Game.csproj"
      }
    }
  }
  ```

  The file is gitignored, so create your own with the consumer projects you care about.
- To inspect the output instead of the generator, set
  `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` in the *consumer* and read
  `obj/<Config>/<Tfm>/generated/Generators/…`. Stale files from an older generator stay on disk; delete the
  folder before you trust it.

### Nothing is generated — what now?

A failing source generator fails *silently*: restore and build both succeed and no code appears. In order:

1. Is the reference wired as an analyzer (`OutputItemType="Analyzer"`, or the NuGet package resolving to
   `analyzers/dotnet/cs/Generators.dll`)?
2. Set `<WarningsAsErrors>CS8032;CS8034</WarningsAsErrors>` in the consumer — CS8032 is the compiler's
   *warning* for a generator that could not be loaded.
3. Are the `<AdditionalFiles>` entries present, and do the globs actually match?
4. Is the target type `partial` (and `public`, for `[Autowire]`)?
5. For `BindNodes()`: does the class name equal the scene's file name, ignoring case?

The `package:smoke` job in [.gitlab-ci.yml](.gitlab-ci.yml) asserts exactly these failure modes against a
throwaway consumer project on every pipeline.

### How can I determine which syntax nodes I should expect?

Consider installing the Roslyn syntax tree viewer plugin [Rossynt](https://plugins.jetbrains.com/plugin/16902-rossynt/).

### How to learn more about wiring source generators?

Watch the walkthrough
video: [Let's Build an Incremental Source Generator With Roslyn, by Stefan Pölz](https://youtu.be/azJm_Y2nbAI)
The complete set of information is available
in [Source Generators Cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md).

## License

MIT — see [LICENSE](LICENSE).
