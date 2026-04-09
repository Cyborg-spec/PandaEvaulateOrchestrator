using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Register Shared Services
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<IUnitConverter, AviationUnitConverter>();

// Register Orchestrators
builder.Services.AddSingleton<StateOrchestrator<NavInputs>>();

// Register Mutators (Input Changing Logic)
builder.Services.AddSingleton<IFieldMutator<NavInputs>, DistanceMutator>();

// Register Solvers (Engineering Math Logic)
builder.Services.AddSingleton<IDomainSolver<NavInputs>, DistanceSolver>();

var app = builder.Build();

// =========================================================================
// 1. PRESENTATION LAYER
// =========================================================================

app.MapPost("/api/nav/evaluate", (EvaluateRequest request, StateOrchestrator<NavInputs> orchestrator, SessionStore store) =>
{
    var state = store.GetNavState(request.SessionId);
    var result = orchestrator.Execute(state, request.Trigger, request.NewValue, request.SolveTargets);
    //converting result to needed units here too via some service
    return Results.Ok(new PandaResponse<NavInputs>(result));
});

app.Run();

// =========================================================================
// 2. SHARED KERNEL & ORCHESTRATION LAYER
// =========================================================================

public class EvaluateRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public object NewValue { get; set; } = default!;
    public List<string> SolveTargets { get; set; } = new();
}

public class PandaResponse<T> where T : IPandaModule
{
    public T Inputs { get; }
    public Dictionary<string, FieldMetadata> Metadata { get; }
    public PandaResponse(T state) { Inputs = state; Metadata = state.Metadata; }
}

public class FieldMetadata
{
    public bool IsReadOnly { get; set; }
    public string? Warning { get; set; }
}

public interface IPandaModule
{
    Dictionary<string, FieldMetadata> Metadata { get; }
    void ResetMetadata();
}

public static class PandaModuleExtensions
{
    public static void InitMetadata(this IPandaModule module)
    {
        foreach (var prop in module.GetType().GetProperties())
        {
            if (prop.Name is nameof(IPandaModule.Metadata)) continue;
            module.Metadata[prop.Name] = new FieldMetadata();
        }
    }
}

// THE NEW DECOUPLED CONTRACTS

// 1. Handles ONLY data extraction, conversion, and applying the new value.
public interface IFieldMutator<T> where T : IPandaModule
{
    string TargetField { get; }
    bool TryMutate(T state, object rawValue);
}

// 2. Handles ONLY the mathematical side-effects.
public interface IDomainSolver<T> where T : IPandaModule
{
    string TriggerField { get; }
    void Solve(T state, List<string> solveTargets);
}

// THE COORDINATOR
public class StateOrchestrator<T> where T : class, IPandaModule
{
    private readonly IEnumerable<IFieldMutator<T>> _mutators;
    private readonly IEnumerable<IDomainSolver<T>> _solvers;

    public StateOrchestrator(IEnumerable<IFieldMutator<T>> mutators, IEnumerable<IDomainSolver<T>> solvers)
    {
        _mutators = mutators;
        _solvers = solvers;
    }

    public T Execute(T state, string trigger, object newValue, List<string> solveTargets)
    {
        state.ResetMetadata();

        // 1. Find the mutator for this specific field
        var mutator = _mutators.FirstOrDefault(m => m.TargetField == trigger);

        if (mutator != null)
        {
            // 2. Attempt to mutate. If it fails (e.g., bad data), stop the pipeline.
            bool success = mutator.TryMutate(state, newValue);

            // 3. If mutation succeeded, find and run all mathematical solvers tied to this trigger
            if (success)
            {
                var solvers = _solvers.Where(s => s.TriggerField == trigger);
                foreach (var solver in solvers)
                {
                    solver.Solve(state, solveTargets ?? new List<string>());
                }
            }
        }

        return state;
    }
}

// =========================================================================
// 3. INFRASTRUCTURE & CACHE
// =========================================================================

public class SessionStore
{
    private readonly ConcurrentDictionary<string, NavInputs> _navSessions = new();
    public NavInputs GetNavState(string sessionId) => _navSessions.GetOrAdd(sessionId, _ => new NavInputs());
}

public interface IUnitConverter { double ConvertFeetToMeters(double feet); }
public class AviationUnitConverter : IUnitConverter { public double ConvertFeetToMeters(double feet) => feet * 0.3048; }

// =========================================================================
// 4. NAV MODULE IMPLEMENTATION
// =========================================================================

public class NavInputs : IPandaModule
{
    public double Distance { get; set; } = 1000;
    public double Gradient { get; set; } = 0.05;
    public double Altitude { get; set; } = 50;

    [JsonIgnore]
    public Dictionary<string, FieldMetadata> Metadata { get; } = new();

    public NavInputs() => this.InitMetadata();

    public void ResetMetadata()
    {
        foreach (var meta in Metadata.Values) { meta.IsReadOnly = false; meta.Warning = null; }
    }
}

// --- RESPONSIBILITY 1: INPUT CHANGING (THE MUTATOR) ---
public class DistanceMutator : IFieldMutator<NavInputs>
{
    private readonly IUnitConverter _unitConverter;

    public DistanceMutator(IUnitConverter unitConverter) => _unitConverter = unitConverter;

    public string TargetField => nameof(NavInputs.Distance);

    public bool TryMutate(NavInputs state, object rawValue)
    {
        // 1. Extraction
        if (rawValue is not JsonElement jsonElement || !jsonElement.TryGetDouble(out double newDistance))
        {
            state.Metadata[TargetField].Warning = "Invalid numeric input.";
            return false; // Stop pipeline
        }

        // 2. Validation
        if (newDistance <= 0)
        {
            state.Metadata[TargetField].Warning = "Distance must be greater than zero.";
            return false; // Stop pipeline
        }

        // 3. Conversion & Mutation
        //state.Distance = _unitConverter.ConvertFeetToMeters(newDistance);
        state.Distance = newDistance;
        return true; // Proceed to solvers
    }
}

// --- RESPONSIBILITY 2: DOMAIN MATH (THE SOLVER) ---
public class DistanceSolver : IDomainSolver<NavInputs>
{
    // This solver listens for changes to Distance
    public string TriggerField => nameof(NavInputs.Distance);

    public void Solve(NavInputs state, List<string> solveTargets)
    {
        // Notice how clean this is. No JSON parsing, no validation. Just engineering math.
        bool solveGradient = solveTargets.Contains(nameof(state.Gradient));

        if (solveGradient)
        {
            state.Gradient = state.Altitude / state.Distance;
            state.Metadata[nameof(state.Gradient)].Warning = "Recalculated to maintain constraints.";
        }
        else
        {
            state.Altitude = state.Distance * state.Gradient;
            state.Metadata[nameof(state.Altitude)].Warning = "Recalculated to maintain constraints.";
        }
    }
}