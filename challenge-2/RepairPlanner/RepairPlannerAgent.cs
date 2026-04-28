using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;   // PromptAgentDefinition, AgentVersionCreationOptions, GetAIAgent extension
using Azure.Identity;
using Microsoft.Agents.AI;        // ChatClientAgent, AgentRunResponse
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner;

/// <summary>
/// Orchestrates the full repair-planning workflow:
///   1. Ensure the Foundry Prompt Agent is registered
///   2. Look up required skills and parts for the diagnosed fault
///   3. Query Cosmos DB for matching technicians and available parts
///   4. Ask the Foundry agent to produce a structured repair plan
///   5. Deserialise the JSON response into a WorkOrder
///   6. Apply mandatory defaults and persist to Cosmos DB
/// </summary>
public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,           // Foundry SDK client
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";

    // System prompt that instructs the LLM how to generate work orders.
    // Using a raw string literal (""") so no escaping is needed.
    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Generate a repair plan with tasks, timeline, and resource allocation.
        Return the response as valid JSON matching the WorkOrder schema.

        Output JSON with these fields:
        - workOrderNumber, machineId, title, description
        - type: "corrective" | "preventive" | "emergency"
        - priority: "critical" | "high" | "medium" | "low"
        - status, assignedTo (technician id or null), notes
        - estimatedDuration: integer (minutes, e.g. 60 not "60 minutes")
        - partsUsed: [{ partId, partNumber, quantity }]
        - tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]

        IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.

        Rules:
        - Assign the most qualified available technician
        - Include only relevant parts; empty array if none needed
        - Tasks must be ordered and actionable
        - Respond with raw JSON only – no markdown fences, no commentary
        """;

    // JsonSerializerOptions shared across all parse calls.
    // AllowReadingFromString handles LLMs that occasionally return numbers as "90" instead of 90.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // -------------------------------------------------------------------------
    // Agent registration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates (or updates) the Prompt Agent version in Azure AI Foundry.
    /// Safe to call on every startup – if the version already exists it is
    /// overwritten with the current instructions.
    /// </summary>
    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Registering Foundry Prompt Agent '{Name}'.", AgentName);

        var definition = new PromptAgentDefinition(model: modelDeploymentName)
        {
            Instructions = AgentInstructions,
        };

        await projectClient.Agents.CreateAgentVersionAsync(
            AgentName,
            new AgentVersionCreationOptions(definition),
            ct);

        logger.LogInformation("Agent '{Name}' registered successfully.", AgentName);
    }

    // -------------------------------------------------------------------------
    // Main workflow
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs the full planning pipeline for a diagnosed fault and returns the
    /// persisted <see cref="WorkOrder"/>.
    /// </summary>
    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(
        DiagnosedFault fault,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Starting repair planning for fault '{FaultType}' on machine '{MachineId}'.",
            fault.FaultType, fault.MachineId);

        // ------------------------------------------------------------------
        // Step 1 – Determine required skills and parts from static mapping
        // ------------------------------------------------------------------
        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredPartNumbers = faultMapping.GetRequiredParts(fault.FaultType);

        logger.LogInformation(
            "Fault mapping resolved: {SkillCount} skill(s), {PartCount} part number(s).",
            requiredSkills.Count, requiredPartNumbers.Count);

        // ------------------------------------------------------------------
        // Step 2 – Fetch live data from Cosmos DB
        // ------------------------------------------------------------------
        var techniciansTask = cosmosDb.GetAvailableTechniciansWithSkillsAsync(requiredSkills, ct);
        var partsTask       = cosmosDb.GetPartsByPartNumbersAsync(requiredPartNumbers, ct);

        // Run both queries concurrently – they target different containers.
        await Task.WhenAll(techniciansTask, partsTask);

        var technicians = techniciansTask.Result;
        var parts       = partsTask.Result;

        logger.LogInformation(
            "Cosmos DB returned {TechCount} technician(s) and {PartCount} part(s).",
            technicians.Count, parts.Count);

        // ------------------------------------------------------------------
        // Step 3 – Build the prompt and invoke the Foundry agent
        // ------------------------------------------------------------------
        var prompt = BuildPrompt(fault, requiredSkills, technicians, parts);

        logger.LogInformation("Invoking Foundry agent '{Name}'.", AgentName);

        var agent   = projectClient.GetAIAgent(name: AgentName);
        var response = await agent.RunAsync(prompt, thread: null, options: null);
        var rawJson  = response.Text ?? string.Empty;

        logger.LogDebug("Raw agent response: {Response}", rawJson);

        // ------------------------------------------------------------------
        // Step 4 – Parse response and apply defaults
        // ------------------------------------------------------------------
        var workOrder = ParseWorkOrder(rawJson, fault);

        // ------------------------------------------------------------------
        // Step 5 – Persist to Cosmos DB
        // ------------------------------------------------------------------
        var saved = await cosmosDb.CreateWorkOrderAsync(workOrder, ct);

        logger.LogInformation(
            "Work order '{WorkOrderNumber}' saved with id={Id}.",
            saved.WorkOrderNumber, saved.Id);

        return saved;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string BuildPrompt(
        DiagnosedFault fault,
        IReadOnlyList<string> requiredSkills,
        IReadOnlyList<Technician> technicians,
        IReadOnlyList<Part> parts)
    {
        // Serialise context objects into the prompt so the LLM has concrete data.
        var techJson  = JsonSerializer.Serialize(technicians, JsonOptions);
        var partsJson = JsonSerializer.Serialize(parts, JsonOptions);

        return $"""
            ## Diagnosed Fault
            Machine ID  : {fault.MachineId}
            Fault Type  : {fault.FaultType}
            Description : {fault.FaultDescription}
            Severity    : {fault.Severity}
            Confidence  : {fault.Confidence:P0}
            Symptoms    : {string.Join(", ", fault.Symptoms)}
            Notes       : {fault.DiagnosticNotes ?? "none"}

            ## Required Skills
            {string.Join(", ", requiredSkills)}

            ## Available Technicians (JSON)
            {techJson}

            ## Available Parts (JSON)
            {partsJson}

            Generate a complete work order as raw JSON.
            """;
    }

    private WorkOrder ParseWorkOrder(string rawJson, DiagnosedFault fault)
    {
        WorkOrder? workOrder = null;

        // Strip accidental markdown fences the model may still add.
        var json = rawJson.Trim();
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('\n') + 1;
            var end   = json.LastIndexOf("```");
            if (end > start)
                json = json[start..end].Trim();
        }

        try
        {
            workOrder = JsonSerializer.Deserialize<WorkOrder>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialise agent response. Raw JSON: {Json}", json);
        }

        // ??= means "assign only if currently null" (like Python's: x = x or default)
        workOrder ??= new WorkOrder();

        if (string.IsNullOrWhiteSpace(workOrder.Id))
            workOrder.Id = Guid.NewGuid().ToString();

        if (string.IsNullOrWhiteSpace(workOrder.WorkOrderNumber))
            workOrder.WorkOrderNumber = $"WO-{DateTime.UtcNow:yyyyMMddHHmmss}";

        workOrder.MachineId = string.IsNullOrWhiteSpace(workOrder.MachineId)
            ? fault.MachineId
            : workOrder.MachineId;
        workOrder.FaultType = fault.FaultType;

        if (string.IsNullOrWhiteSpace(workOrder.Status))
            workOrder.Status = "open";

        if (string.IsNullOrWhiteSpace(workOrder.Priority))
            workOrder.Priority = "medium";

        if (string.IsNullOrWhiteSpace(workOrder.Type))
            workOrder.Type = "corrective";

        workOrder.PartsUsed ??= [];
        workOrder.Tasks ??= [];

        return workOrder;
    }
}
