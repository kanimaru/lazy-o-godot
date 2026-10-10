using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace GenUtilities;

/// <summary>
/// Flags time-based Rx.NET operators called without an explicit scheduler.
/// </summary>
/// <remarks>
/// <para>
/// In Godot, the overloads of <c>Observable.Timer</c>, <c>Interval</c>, <c>Delay</c>, <c>Throttle</c>,
/// <c>Sample</c>, <c>Buffer</c>, <c>Window</c>, <c>Timeout</c> and friends that take no <c>IScheduler</c>
/// run on the thread pool. The callback then touches the scene tree from a worker thread, which corrupts
/// engine state intermittently instead of failing loudly. Passing a main-thread scheduler (a frame-pumped
/// scheduler driven from <c>_Process</c>) is the fix, so the rule makes that the only quiet option.
/// </para>
/// <para>
/// A call is reported when it targets <c>System.Reactive.Linq.Observable</c>, takes a
/// <c>TimeSpan</c> or <c>DateTimeOffset</c> (or is <c>Timestamp</c>/<c>TimeInterval</c>), passes no
/// <c>IScheduler</c>, and an overload with an <c>IScheduler</c> parameter exists.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RxSchedulerAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "LZ0001";

    private const string ObservableTypeName = "System.Reactive.Linq.Observable";
    private const string SchedulerTypeName = "System.Reactive.Concurrency.IScheduler";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Time-based Rx operator without a scheduler",
        "'Observable.{0}' without a scheduler runs on the thread pool; pass a main-thread scheduler so the callback may touch the scene tree",
        "Threading",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Rx.NET time-based operators default to the thread pool. In Godot, pass a scheduler that runs on the main thread.");

    private static readonly ImmutableHashSet<string> TimeWithoutArguments =
        ImmutableHashSet.Create("Timestamp", "TimeInterval");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start => {
            INamedTypeSymbol? observable = start.Compilation.GetTypeByMetadataName(ObservableTypeName);
            INamedTypeSymbol? scheduler = start.Compilation.GetTypeByMetadataName(SchedulerTypeName);
            if (observable is null || scheduler is null) {
                return;
            }

            INamedTypeSymbol? timeSpan = start.Compilation.GetTypeByMetadataName("System.TimeSpan");
            INamedTypeSymbol? dateTimeOffset = start.Compilation.GetTypeByMetadataName("System.DateTimeOffset");

            start.RegisterOperationAction(operationContext => {
                var invocation = (IInvocationOperation)operationContext.Operation;
                IMethodSymbol method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
                if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, observable)) {
                    return;
                }

                if (method.Parameters.Any(p => IsType(p.Type, scheduler))) {
                    return;
                }

                bool isTimeBased = TimeWithoutArguments.Contains(method.Name)
                                   || method.Parameters.Any(p => IsType(p.Type, timeSpan) || IsType(p.Type, dateTimeOffset));
                if (!isTimeBased) {
                    return;
                }

                bool hasSchedulerOverload = observable.GetMembers(method.Name)
                    .OfType<IMethodSymbol>()
                    .Any(candidate => candidate.Parameters.Any(p => IsType(p.Type, scheduler)));
                if (!hasSchedulerOverload) {
                    return;
                }

                operationContext.ReportDiagnostic(Diagnostic.Create(Rule, invocation.Syntax.GetLocation(), method.Name));
            }, OperationKind.Invocation);
        });
    }

    private static bool IsType(ITypeSymbol type, INamedTypeSymbol? expected) {
        return expected is not null &&
               SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, expected);
    }
}
