# lazy-o-godot

Roslyn source generators for Godot 4 C#, so you can be lazy about the boilerplate.

lazy-o-godot reads your `project.godot`, `.tscn` and `.tres` files and your attributes at compile time and
writes the tedious parts for you: typed node fields, scene paths and instantiation, autoload access, input
action and group names, resource tables, singletons, node metadata helpers, loggers and multiplayer
synchronization. An analyzer that catches a common Rx.NET threading mistake comes with it.

```csharp
using GenUtilities;

public partial class MainMenu : Control {          // MainMenu.tscn has a %Title label
    public override void _Ready() {
        BindNodes();                                // _title = GetNode<Label>("%Title")
        _title.Text = "Hi";

        if (Input.IsActionPressed(Actions.jump)) {   // "jump" from project.godot [input]
            GetTree().Root.AddChild(Scenes.NewLobby<Node>());
        }
        Autoloads.UserService.Login();
    }
}
```

Everything is generated at build time. Nothing from this package ends up in your game's output except the C#
it writes.

| | |
|---|---|
| NuGet package | `Dev.Kani.Godot.Generator` |
| Namespace of generated code | `GenUtilities` |
| Targets | Godot 4 .NET projects; the generator is `netstandard2.1` on Roslyn 4.3 |

## Getting started

### 1. Create a GitHub token

The package is published to [GitHub Packages](https://github.com/kanimaru/lazy-o-godot/packages), and GitHub's
NuGet registry needs a login even to download public packages.

1. Create a [personal access token (classic)](https://github.com/settings/tokens/new?scopes=read:packages&description=lazy-o-godot)
   with only the **`read:packages`** scope. Fine-grained tokens do not work with GitHub's NuGet registry.
2. Put it in the `GITHUB_PACKAGES_TOKEN` environment variable on your machine (and as a secret in your own
   CI).

### 2. Add the package

Add the feed once in a `nuget.config` next to your solution:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="lazy-o-godot" value="https://nuget.pkg.github.com/kanimaru/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <lazy-o-godot>
      <add key="Username" value="<your-github-username>" />
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
    </lazy-o-godot>
  </packageSourceCredentials>
</configuration>
```

NuGet reads `%GITHUB_PACKAGES_TOKEN%` from the environment, so the token never has to be committed.

Then reference the package in your game's `.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="Dev.Kani.Godot.Generator" Version="x.y.z" />
</ItemGroup>
```

The package is a development dependency: it is loaded by your compiler only and does not flow on to projects
that reference yours.

### 3. Show the generators your Godot files

Roslyn only sees files you list as `<AdditionalFiles>`. Several generators read Godot's files rather than C#,
and without these entries they quietly generate nothing:

```xml
<ItemGroup>
  <AdditionalFiles Include="project.godot" />          <!-- Actions, Groups, Autoloads -->
  <AdditionalFiles Include="src\**\*.tscn" />          <!-- Scenes, BindNodes, Singleton, Autoload types -->
  <AdditionalFiles Include="game_data\**\*.tres" />    <!-- ResourcePath -->
</ItemGroup>
```

| File | Read by |
|---|---|
| `project.godot` | `Actions` (`[input]`), `Groups` (`[global_group]`), `Autoloads` (`[autoload]`) |
| `*.tscn` | `Scenes`, `BindNodes()`, and through `Scenes` also `[Singleton]`; `Autoloads` uses them to type scene autoloads |
| `*.tres` | `[ResourcePath]`, for files in the directories it names |

Only listed scenes get `Scenes` members, so test or asset scenes you leave out stay out of the API.

Keep the `.tres` globs to the directories your `[ResourcePath]` classes name: that generator treats every
additional file in a matching directory as a resource, so a stray `.gdshader` there produces code that does
not compile.

### 4. Use it

Add `using GenUtilities;` and you're set. The attributes are generated too, so there is no separate attribute
package to reference.

### Building from source

To work on the generators, or to use an unreleased version, reference a checkout as an analyzer:

```xml
<ProjectReference Include="..\lazy-o-godot\dev.kani.godot.generator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />
```

`OutputItemType="Analyzer"` makes the compiler load the generators; `ReferenceOutputAssembly="false"` keeps
`Generators.dll` out of your game.

## What it generates

| Feature | Triggered by | You get |
|---|---|---|
| [Input actions](#input-actions--actions) | `project.godot` | `Actions.<action>` |
| [Global groups](#global-groups--groups) | `project.godot` | `Groups.<group>` |
| [Scenes](#scenes--scenes) | `*.tscn` | `Scenes.Path<Name>`, `Scenes.New<Name>()` |
| [Autoloads](#autoloads--autoloads) | `project.godot` (+ `*.tscn`) | `Autoloads.<Name>` |
| [Unique-name nodes](#unique-name-nodes--bindnodes) | a `*.tscn` named like your class | `BindNodes()` and typed fields |
| [Property wiring](#property-wiring--autowire) | `[Autowire]` | properties that write through to child nodes |
| [Singletons](#singletons--singleton) | `[Singleton]` | `Instance`, `GetOrCreate()` |
| [Node metadata](#node-metadata--registermeta) | `[RegisterMeta]` | `GetImpl()`, `SetImpl()` |
| [Logging](#logging--log) | `[Log]` | a static `log` field |
| [Multiplayer sync](#multiplayer-sync--sync) | `[Sync]`, `[SyncSettings]` | `StartSync()`, setters and change events |
| [Resource tables](#resource-tables--resourcepath) | `[ResourcePath]` + `*.tres` | a static field per resource, plus `All` |
| [Rx scheduler check](#rx-scheduler-check--lz0001) | Rx.NET time operators | warning `LZ0001` |

### Input actions — `Actions`

One `StringName` per action in the `[input]` section of `project.godot`. Non-alphanumeric characters are
dropped from the member name and the case is kept; the value is the real action name.

```ini
[input]
move_left={...}
```

```csharp
if (Input.IsActionPressed(Actions.moveleft)) { ... }
```

### Global groups — `Groups`

One `StringName` per entry in the `[global_group]` section of `project.godot`, documented with the group's
description.

```csharp
GetTree().GetNodesInGroup(Groups.enemies);
```

### Scenes — `Scenes`

For every `*.tscn` you list as an additional file:

- `Path<Name>`: the scene's `res://` path;
- `New<Name><T>()`: loads and instantiates the scene as `T`;
- `New<Name>()`: the same, already typed as the root's C# class, when the root runs one.

`<Name>` is the file name in PascalCase (`main_menu.tscn` → `PathMainMenu`, `NewMainMenu<T>()`).

```csharp
HighscoreRow row = Scenes.NewHighscoreRow();    // root runs the C# class HighscoreRow
Node level = Scenes.NewLobby<Node>();           // GDScript or script-less root
PackedScene packed = GD.Load<PackedScene>(Scenes.PathLobby);
```

The root's class is found through its `script` resource: a `.cs` file named like a class deriving from `Node`.

Each scene is loaded once and stays referenced, like a GDScript `preload`. That also avoids a Godot .NET issue
where a garbage-collected `PackedScene` wrapper later makes every instantiation of that scene log
`Handle is not initialized`.

`Scenes` is `internal`, so use it from the project that holds the scenes.

### Autoloads — `Autoloads`

Typed access to every entry in the `[autoload]` section of `project.godot`:

```csharp
Autoloads.UserService.LoadAsync(id);       // instead of GetNode<UserService>("/root/UserService")
if (Autoloads.NotificationsOrNull is { } notifications) { ... }
string path = Autoloads.PathUserService;   // "/root/UserService"
```

Use the `…OrNull` variant where the autoload may not be in the tree, such as `[Tool]` code in the editor.

The type is taken from what the autoload points at: the class of a `.cs` script; the class (or Godot type) of a
scene's root, if the scene is an additional file; otherwise `Node`. Autoloads whose names are not valid C#
identifiers are skipped.

### Unique-name nodes — `BindNodes()`

When a class has the same name as a `.tscn` file (case-insensitive, any folder), every node in that scene
marked **Access as Unique Name** becomes a typed private field, filled by a generated `BindNodes()`:

```csharp
public partial class MainMenu : Control {   // MainMenu.tscn, with a unique node "Title"
    public override void _Ready() {
        BindNodes();                        // _title = GetNode<Label>("%Title");
        _title.Text = "Hi";
    }
}
```

The field is named `_` plus the node name with a lowercase first letter (`Title` → `_title`). Its type is the
node's C# class when it has one: the script on the node, or for an instanced scene the class named like that
scene or the class on its root. Otherwise it is the node's Godot type, spelled as in the C# bindings
(`GPUParticles3D` → `GpuParticles3D`).

The class must be `partial`, and you call `BindNodes()` yourself. If nodes must be available before the scene
enters the tree, bind on instantiation instead:

```csharp
public override void _Notification(int what) {
    if (what == NotificationSceneInstantiated) {
        BindNodes();
    }
}
```

A missing node triggers Godot's usual "node not found" error, so keep `GetNodeOrNull` for nodes that are
optional.

### Property wiring — `[Autowire]`

Forward a property to a member of a child node, whether or not the node is ready yet:

```csharp
public partial class MainMenu : Control {
    [Autowire(nameof(_title), nameof(Label.Text))]
    public string Title { get => _titleVal; set => UpdateTitle(value); }

    public override void _Ready() {
        BindNodes();
        RefreshAllComponents();             // pushes values set before _Ready
    }
}
```

For each property you get a `_<property>Val` backing field and an `Update<Property>(value)` method that stores
the value and writes it to the node once `IsNodeReady()`. `RefreshAllComponents()` replays every stored value.
The class must be a `public partial` class deriving from `Node`.

### Singletons — `[Singleton]`

```csharp
[Singleton]
public partial class AudioManager : Node { }

AudioManager.Instance.Play(...);
```

`GetOrCreate()` instantiates `AudioManager.tscn` through [`Scenes`](#scenes--scenes) and adds it to the tree
root, so that scene must be an additional file. A non-`partial` class is reported as error `MG001`. For
autoloads, use [`Autoloads`](#autoloads--autoloads) instead.

### Node metadata — `[RegisterMeta]`

On a `partial` class, record or interface, generates helpers that store an implementation of that type in a
node's metadata:

```csharp
public static T? GetImpl(Node node);              // the node itself if it is a T, else its metadata entry
public static void SetImpl(Node node, Variant impl);
```

Handy for attaching capability interfaces such as `ITakeDamage` to nodes that cannot implement them. A
non-`partial` type is reported as error `MG001`.

### Logging — `[Log]`

On a `partial class`, generates `private static SimpleLogger log = new SimpleLogger("<ClassName>");`. Generic
classes are supported.

This is the only feature that needs code of your own: the generated field is a
`dev.kani.godot.util.logging.SimpleLogger`, which this package does not ship. Any class with that name and a
`string` constructor works, for example:

```csharp
namespace dev.kani.godot.util.logging;

public class SimpleLogger(string name) {
    public void Debug(string message) => Godot.GD.Print($"[{name}] {message}");
    public void Error(string message) => Godot.GD.PushError($"[{name}] {message}");
}
```

Without such a class, `[Log]` fails to compile; the other features are unaffected.

### Multiplayer sync — `[Sync]`

Builds a `MultiplayerSynchronizer` and its `SceneReplicationConfig` from annotated members:

```csharp
[SyncSettings(ReplicationInterval = 0.1f, PublicVisibility = true,
              VisibilityUpdateMode = MultiplayerSynchronizer.VisibilityUpdateModeEnum.Idle)]
public partial class Player : CharacterBody2D {
    [Sync(ReplicationMode = SceneReplicationConfig.ReplicationMode.OnChange, Spawn = true)]
    private int _health;

    [Export]
    public int Health { get => _health; set => SetHealth(value); }

    public override void _Ready() {
        StartSync();
    }
}
```

`StartSync()` configures and adds the synchronizer. For each member you get `Set<Name>(value)`, which assigns
and raises `<Name>Changed` only when the value changes. `<Name>` is the member name without leading
underscores, capitalized.

The replicated path is `".:<Name>"`, so declare the property that wraps the field yourself, as above.

Settings: `[SyncSettings]` takes `DeltaInterval`, `ReplicationInterval`, `PublicVisibility` and
`VisibilityUpdateMode`; `[Sync]` takes `ReplicationMode`, `Spawn` and `Logging`. See
[Known issues](#known-issues).

### Resource tables — `[ResourcePath]`

```csharp
[ResourcePath("game_data/items")]
public partial class Items { }
```

Every additional file in a directory ending with that path becomes a static field, named after the file and
typed by the resource's `script_class` (or `type`):

```csharp
public static readonly ItemData sword = GD.Load<ItemData>(/* res://game_data/items/sword.tres */);
public static ItemData[] All => [sword, ...];     // Resource[] if the types are mixed
```

Resources are loaded through `ProjectSettings.GlobalizePath` and members keep the file name as is, so this
suits editor and tool-side data better than exported games.

### Rx scheduler check — `LZ0001`

Rx.NET's time-based operators (`Timer`, `Interval`, `Delay`, `Throttle`, `Buffer`, `Timeout`, …) run on the
thread pool when you don't pass a scheduler. In Godot the callback then touches the scene tree from a worker
thread, which breaks things intermittently instead of failing loudly.

`LZ0001` warns about such calls when an overload that takes an `IScheduler` exists. Pass one that runs on the
main thread:

```csharp
Observable.Timer(TimeSpan.FromSeconds(1), GodotSchedulers.Process);   // fine
Observable.Timer(TimeSpan.FromSeconds(1));                            // LZ0001
```

The check only runs when `System.Reactive` is referenced.

## Troubleshooting

A source generator that cannot run fails quietly: the build succeeds and the generated code is simply
missing. Check, in this order:

1. Is it referenced as an analyzer? The package does this for you; a `ProjectReference` needs
   `OutputItemType="Analyzer"`.
2. Add `<WarningsAsErrors>CS8032;CS8034</WarningsAsErrors>` to your project. CS8032 is the compiler's warning
   for a generator it could not load.
3. Are your Godot files listed as `<AdditionalFiles>`, and do the globs match?
4. Is the class `partial` (and `public` for `[Autowire]`)?
5. For `BindNodes()`: is the class named exactly like the scene file, ignoring case?

To read what was generated, set `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` in your
project and look under `obj/…/generated/Generators/`. Old files stay there when a generator stops emitting
them, so delete the folder before trusting it.

## Known issues

- `[Sync]`: `Spawn` currently takes the value of `Logging`, and `Logging` never adds its debug output.
- `[Sync]` on a property registers it for replication and creates its event, but ignores `ReplicationMode` and
  `Spawn`; those only work on fields.
- `[Singleton]` and `[RegisterMeta]` share the diagnostic id `MG001`.

## Contributing

Issues and pull requests are welcome.

```bash
dotnet build dev.kani.godot.generator.csproj
```

- The code follows the [Google C# style guide](https://google.github.io/styleguide/csharp-style.html) with
  4-space indents, encoded in [.editorconfig](https://github.com/kanimaru/lazy-o-godot/blob/master/.editorconfig). Format with
  `dotnet format whitespace . --folder --exclude obj --exclude bin`.
- To debug a generator, add a `Properties/launchSettings.json` (it is gitignored) with a
  `DebugRoslynComponent` profile pointing at a Godot project:

  ```json
  {
    "profiles": {
      "DebugGenerators": {
        "commandName": "DebugRoslynComponent",
        "targetProject": "../MyGame/MyGame.csproj"
      }
    }
  }
  ```

- CI ([.github/workflows/ci.yml](https://github.com/kanimaru/lazy-o-godot/blob/master/.github/workflows/ci.yml)) packs the generator and builds a small Godot
  project against the package, failing if the generators do not load or produce nothing.
- Useful references: the [Rossynt](https://plugins.jetbrains.com/plugin/16902-rossynt/) syntax tree viewer,
  the [Source Generators Cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md)
  and [Let's Build an Incremental Source Generator With Roslyn](https://youtu.be/azJm_Y2nbAI) by Stefan Pölz.

### Releases

Pushing a `vX.Y.Z` tag builds, tests and publishes that version to GitHub Packages and creates a GitHub
Release for it.

## License

MIT, see [LICENSE](https://github.com/kanimaru/lazy-o-godot/blob/master/LICENSE).
