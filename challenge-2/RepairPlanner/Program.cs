using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlanner;
using RepairPlanner.Models;
using RepairPlanner.Services;

// =============================================================================
// Repair Planner Agent – entry point
// =============================================================================
// Flow:
//   1. Validate environment variables
//   2. Build services (logging, Cosmos DB, Foundry client)
//   3. Register the Foundry Prompt Agent version
//   4. Run sample faults through the planning workflow
//   5. Print a summary of every work order created
// =============================================================================

PrintBanner();

// ---------------------------------------------------------------------------
// 1. Read required environment variables
//    Fail fast at startup so the cause is obvious before any network calls.
// ---------------------------------------------------------------------------
static string RequiredEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException(
        $"Required environment variable '{name}' is not set. " +
        $"See challenge-2/README.md for setup instructions.");

var projectEndpoint    = RequiredEnv("AZURE_AI_PROJECT_ENDPOINT");
var modelDeployment    = RequiredEnv("MODEL_DEPLOYMENT_NAME");
var cosmosEndpoint     = RequiredEnv("COSMOS_ENDPOINT");
var cosmosKey          = RequiredEnv("COSMOS_KEY");
var cosmosDatabaseName = RequiredEnv("COSMOS_DATABASE_NAME");

// ---------------------------------------------------------------------------
// 2. Build services
// ---------------------------------------------------------------------------
// await using – like Python's "async with": disposes provider when the block ends.
await using var provider = new ServiceCollection()
    .AddLogging(b => b
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information))
    .BuildServiceProvider();

var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
var startupLogger = loggerFactory.CreateLogger("Startup");

startupLogger.LogInformation("Connecting to Azure AI Foundry project: {Endpoint}", projectEndpoint);

// DefaultAzureCredential tries: env vars → managed identity → VS Code login → Azure CLI
var projectClient = new AIProjectClient(
    new Uri(projectEndpoint),
    new DefaultAzureCredential());

startupLogger.LogInformation("Connecting to Cosmos DB: {Endpoint}", cosmosEndpoint);

var cosmosDb = new CosmosDbService(
    new CosmosDbOptions(cosmosEndpoint, cosmosKey, cosmosDatabaseName),
    loggerFactory.CreateLogger<CosmosDbService>());

// FaultMappingService uses hardcoded dictionaries – no external dependency.
IFaultMappingService faultMapping = new FaultMappingService();

var agent = new RepairPlannerAgent(
    projectClient,
    cosmosDb,
    faultMapping,
    modelDeployment,
    loggerFactory.CreateLogger<RepairPlannerAgent>());

// ---------------------------------------------------------------------------
// 3. Register the Foundry Prompt Agent version
//    Safe to call on every startup – overwrites the previous version if it
//    exists, so instruction changes are picked up automatically.
// ---------------------------------------------------------------------------
Console.WriteLine("\n[Step 1/3] Registering Foundry Prompt Agent...");
await agent.EnsureAgentVersionAsync();
Console.WriteLine("           Agent registered.");

// ---------------------------------------------------------------------------
// 4. Sample faults – representative scenarios from the workshop mappings
// ---------------------------------------------------------------------------
// In production this list would come from the upstream Fault Diagnosis agent.
var sampleFaults = new List<DiagnosedFault>
{
    new()
    {
        Id               = Guid.NewGuid().ToString(),
        MachineId        = "TBM-001",
        FaultType        = "building_drum_vibration",
        FaultDescription = "Excessive vibration detected on building drum during operation.",
        Severity         = "high",
        Confidence       = 0.92,
        TimestampUtc     = DateTime.UtcNow,
        Symptoms         = ["vibration_above_threshold", "bearing_noise"],
        DiagnosticNotes  = "Sensor TBM-001-VIB-01 reading 12.4 mm/s RMS (threshold: 7.1 mm/s).",
    },
    new()
    {
        Id               = Guid.NewGuid().ToString(),
        MachineId        = "TCP-003",
        FaultType        = "curing_temperature_excessive",
        FaultDescription = "Curing press zone 2 temperature exceeded safe operating limits.",
        Severity         = "critical",
        Confidence       = 0.97,
        TimestampUtc     = DateTime.UtcNow,
        Symptoms         = ["temperature_spike", "heater_fault_code"],
        DiagnosticNotes  = "Zone 2 reached 198°C; rated maximum is 185°C.",
    },
};

// ---------------------------------------------------------------------------
// 5. Run the planning workflow for each fault and collect results
// ---------------------------------------------------------------------------
Console.WriteLine($"\n[Step 2/3] Processing {sampleFaults.Count} diagnosed fault(s)...");

var results   = new List<WorkOrder>();
var failures  = new List<(DiagnosedFault Fault, Exception Error)>();

foreach (var fault in sampleFaults)
{
    Console.WriteLine($"\n  -> Fault: {fault.FaultType} | Machine: {fault.MachineId} | Severity: {fault.Severity}");
    try
    {
        var workOrder = await agent.PlanAndCreateWorkOrderAsync(fault);
        results.Add(workOrder);
        Console.WriteLine($"     Work order created: {workOrder.WorkOrderNumber}");
    }
    catch (Exception ex)
    {
        failures.Add((fault, ex));
        // Log and continue so one failure does not abort remaining faults.
        startupLogger.LogError(ex,
            "Failed to create work order for fault '{FaultType}' on machine '{MachineId}'.",
            fault.FaultType, fault.MachineId);
        Console.WriteLine($"     ERROR: {ex.Message}");
    }
}

// ---------------------------------------------------------------------------
// Print summary
// ---------------------------------------------------------------------------
Console.WriteLine("\n[Step 3/3] Summary");
Console.WriteLine(new string('=', 60));

if (results.Count == 0 && failures.Count == 0)
{
    Console.WriteLine("  No faults were processed.");
}

foreach (var wo in results)
{
    Console.WriteLine($"""
      Work Order : {wo.WorkOrderNumber}
      Machine    : {wo.MachineId}
      Fault      : {wo.FaultType}
      Type       : {wo.Type}
      Priority   : {wo.Priority}
      Status     : {wo.Status}
      Assigned   : {wo.AssignedTo ?? "(unassigned)"}
      Duration   : {wo.EstimatedDuration} min
      Tasks      : {wo.Tasks.Count}
      Parts used : {wo.PartsUsed.Count}
    """);
}

if (failures.Count > 0)
{
    Console.WriteLine($"\n  {failures.Count} fault(s) could not be planned:");
    foreach (var (fault, error) in failures)
        Console.WriteLine($"    - {fault.FaultType} on {fault.MachineId}: {error.Message}");
}

Console.WriteLine(new string('=', 60));
Console.WriteLine($"  Completed: {results.Count} succeeded, {failures.Count} failed.");

// Return a non-zero exit code if any fault failed, useful for CI pipelines.
return failures.Count > 0 ? 1 : 0;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static void PrintBanner()
{
    Console.WriteLine("""

    ╔══════════════════════════════════════════════╗
    ║       Repair Planner Agent  – Challenge 2    ║
    ║  Foundry Agents SDK + Cosmos DB  (.NET 10)   ║
    ╚══════════════════════════════════════════════╝
    """);
}

