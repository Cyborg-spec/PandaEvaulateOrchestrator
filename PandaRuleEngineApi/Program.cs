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

// ==========================================
// REGISTER PIPELINES
// ==========================================

// 1. NAV Module Registration
builder.Services.AddSingleton<StateOrchestrator<NavInputs>>();
builder.Services.AddSingleton<IFieldMutator<NavInputs>, DistanceMutator>();
builder.Services.AddSingleton<IDomainSolver<NavInputs>, DistanceSolver>();

// 2. OCS Module Registration (NEW)
builder.Services.AddSingleton<StateOrchestrator<OcsInputs>>();
builder.Services.AddSingleton<IFieldMutator<OcsInputs>, CategoryMutator>();
builder.Services.AddSingleton<IDomainSolver<OcsInputs>, OcsCategorySolver>();

var app = builder.Build();

// =========================================================================
// 1. PRESENTATION LAYER
// =========================================================================

app.MapPost("/api/nav/evaluate", (EvaluateRequest request, StateOrchestrator<NavInputs> orchestrator, SessionStore store) =>
{
    var state = store.GetNavState(request.SessionId);
    var result = orchestrator.Execute(state, request.Trigger, request.NewValue, request.SolveTargets);
    return Results.Ok(new SampleResponse<NavInputs>(result));
});

// NEW ENDPOINT for OCS
app.MapPost("/api/ocs/evaluate", (EvaluateRequest request, StateOrchestrator<OcsInputs> orchestrator, SessionStore store) =>
{
    var state = store.GetOcsState(request.SessionId);
    var result = orchestrator.Execute(state, request.Trigger, request.NewValue, request.SolveTargets);
    return Results.Ok(new SampleResponse<OcsInputs>(result));
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

public class SampleResponse<T> where T : IPandaModule
{
    public T Inputs { get; }
    public Dictionary<string, FieldMetadata> Metadata { get; }
    public SampleResponse(T state) { Inputs = state; Metadata = state.Metadata; }
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

public interface IFieldMutator<T> where T : IPandaModule
{
    string TargetField { get; }
    bool TryMutate(T state, object rawValue);
}

public interface IDomainSolver<T> where T : IPandaModule
{
    string TriggerField { get; }
    void Solve(T state, List<string> solveTargets);
}

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

        var mutator = _mutators.FirstOrDefault(m => m.TargetField == trigger);

        if (mutator != null)
        {
            bool success = mutator.TryMutate(state, newValue);

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
    private readonly ConcurrentDictionary<string, OcsInputs> _ocsSessions = new(); // NEW

    public NavInputs GetNavState(string sessionId) => _navSessions.GetOrAdd(sessionId, _ => new NavInputs());
    public OcsInputs GetOcsState(string sessionId) => _ocsSessions.GetOrAdd(sessionId, _ => new OcsInputs()); // NEW
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

public class DistanceMutator : IFieldMutator<NavInputs>
{
    private readonly IUnitConverter _unitConverter;
    public DistanceMutator(IUnitConverter unitConverter) => _unitConverter = unitConverter;
    public string TargetField => nameof(NavInputs.Distance);

    public bool TryMutate(NavInputs state, object rawValue)
    {
        if (rawValue is not JsonElement jsonElement || !jsonElement.TryGetDouble(out double newDistance))
        {
            state.Metadata[TargetField].Warning = "Invalid numeric input.";
            return false;
        }
        
        //here we can add our converters and etc. 

        if (newDistance <= 0)
        {
            state.Metadata[TargetField].Warning = "Distance must be greater than zero.";
            return false; 
        }
        
        state.Distance = newDistance;
        return true; 
    }
}

public class DistanceSolver : IDomainSolver<NavInputs>
{
    public string TriggerField => nameof(NavInputs.Distance);

    public void Solve(NavInputs state, List<string> solveTargets)
    {
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

// =========================================================================
// 5. OCS MODULE IMPLEMENTATION (NEW 1-TO-1 EXAMPLE)
// =========================================================================

public class OcsInputs : IPandaModule
{
    public string Category { get; set; } = "CAT_I";
    public double Slope { get; set; } = 0.02;

    [JsonIgnore]
    public Dictionary<string, FieldMetadata> Metadata { get; } = new();

    public OcsInputs() => this.InitMetadata();

    public void ResetMetadata()
    {
        foreach (var meta in Metadata.Values) { meta.IsReadOnly = false; meta.Warning = null; }
    }
}

// --- OCS MUTATOR ---
public class CategoryMutator : IFieldMutator<OcsInputs>
{
    public string TargetField => nameof(OcsInputs.Category);

    public bool TryMutate(OcsInputs state, object rawValue)
    {
        // 1. Extraction: Safely parse a string out of the JSON element
        if (rawValue is not JsonElement jsonElement || jsonElement.ValueKind != JsonValueKind.String)
        {
            state.Metadata[TargetField].Warning = "Category must be text.";
            return false;
        }

        string newCategory = jsonElement.GetString()!.ToUpper();

        // 2. Validation: Ensure it's a valid aviation category
        if (newCategory != "CAT_I" && newCategory != "CAT_II")
        {
            state.Metadata[TargetField].Warning = "Invalid aircraft category.";
            return false;
        }

        // 3. Mutation
        state.Category = newCategory;
        return true;
    }
}

// --- OCS SOLVER ---
public class OcsCategorySolver : IDomainSolver<OcsInputs>
{
    public string TriggerField => nameof(OcsInputs.Category);

    public void Solve(OcsInputs state, List<string> solveTargets)
    {
        if (state.Category == "CAT_II")
        {
            state.Slope = 0.025;
            state.Metadata[nameof(state.Slope)].IsReadOnly = true; // Lock the field in the UI
            state.Metadata[nameof(state.Slope)].Warning = "Slope locked by CAT II regulation.";
        }
        else 
        {
            state.Slope = 0.02;
        }
    }
}