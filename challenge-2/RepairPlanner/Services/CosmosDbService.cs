using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services;

public sealed class CosmosDbService
{
    private readonly Container _technicians;
    private readonly Container _parts;
    private readonly Container _workOrders;
    private readonly ILogger<CosmosDbService> _logger;

    // Primary constructor – parameters become fields automatically (like Python's __init__ with self.x = x)
    public CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
    {
        _logger = logger;

        var client = new CosmosClient(options.Endpoint, options.Key);
        var db = client.GetDatabase(options.DatabaseName);

        _technicians = db.GetContainer("Technicians");
        _parts       = db.GetContainer("PartsInventory");
        _workOrders  = db.GetContainer("WorkOrders");
    }

    // -------------------------------------------------------------------------
    // Technicians
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns all available technicians that have at least one of the required skills.
    /// Results are ordered so the technician with the highest skill-overlap comes first.
    /// </summary>
    public async Task<List<Technician>> GetAvailableTechniciansWithSkillsAsync(
        IReadOnlyList<string> requiredSkills,
        CancellationToken ct = default)
    {
        if (requiredSkills.Count == 0)
        {
            _logger.LogWarning("GetAvailableTechniciansWithSkillsAsync called with no required skills; returning all available technicians.");
        }

        // Cosmos SQL does not support cross-container joins, so we filter isAvailable
        // and do the skill-matching in memory (dataset is small).
        const string query = "SELECT * FROM c WHERE c.isAvailable = true";

        _logger.LogInformation("Querying available technicians from Cosmos DB.");

        var technicians = new List<Technician>();
        try
        {
            using var iterator = _technicians.GetItemQueryIterator<Technician>(
                new QueryDefinition(query));

            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct);
                technicians.AddRange(page);
            }
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error while querying technicians (status {Status}).", ex.StatusCode);
            throw;
        }

        if (requiredSkills.Count == 0)
            return technicians;

        // Sort descending by skill overlap count so best-match technician is first.
        var requiredSet = new HashSet<string>(requiredSkills, StringComparer.OrdinalIgnoreCase);
        return technicians
            .Where(t => t.Skills.Any(s => requiredSet.Contains(s)))
            .OrderByDescending(t => t.Skills.Count(s => requiredSet.Contains(s)))
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Parts inventory
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches inventory records for the given part numbers.
    /// Parts not found in the database are silently omitted.
    /// </summary>
    public async Task<List<Part>> GetPartsByPartNumbersAsync(
        IReadOnlyList<string> partNumbers,
        CancellationToken ct = default)
    {
        if (partNumbers.Count == 0)
        {
            _logger.LogInformation("No part numbers requested; skipping parts query.");
            return [];
        }

        // Build a parameterised IN-style query to avoid SQL injection risk.
        // Cosmos SDK parameterised queries sanitise the values before sending.
        var paramList = partNumbers
            .Select((pn, i) => (Alias: $"@pn{i}", Value: pn))
            .ToList();

        var inClause = string.Join(", ", paramList.Select(p => p.Alias));
        var queryDef = new QueryDefinition(
            $"SELECT * FROM c WHERE c.partNumber IN ({inClause})");

        foreach (var (alias, value) in paramList)
            queryDef = queryDef.WithParameter(alias, value);

        _logger.LogInformation(
            "Querying parts inventory for {Count} part number(s).", partNumbers.Count);

        var parts = new List<Part>();
        try
        {
            using var iterator = _parts.GetItemQueryIterator<Part>(queryDef);
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct);
                parts.AddRange(page);
            }
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error while querying parts (status {Status}).", ex.StatusCode);
            throw;
        }

        _logger.LogInformation("Retrieved {Count} part(s) from inventory.", parts.Count);
        return parts;
    }

    // -------------------------------------------------------------------------
    // Work orders
    // -------------------------------------------------------------------------

    /// <summary>
    /// Persists a new WorkOrder document.
    /// The <see cref="WorkOrder.Status"/> property is used as the partition key.
    /// </summary>
    public async Task<WorkOrder> CreateWorkOrderAsync(
        WorkOrder workOrder,
        CancellationToken ct = default)
    {
        // Treat null/empty strings as missing values before writing to Cosmos.
        if (string.IsNullOrWhiteSpace(workOrder.Id))
            workOrder.Id = Guid.NewGuid().ToString();

        if (string.IsNullOrWhiteSpace(workOrder.WorkOrderNumber))
            workOrder.WorkOrderNumber = $"WO-{DateTime.UtcNow:yyyyMMddHHmmss}";

        if (string.IsNullOrWhiteSpace(workOrder.Status))
            workOrder.Status = "open";

        if (string.IsNullOrWhiteSpace(workOrder.Priority))
            workOrder.Priority = "medium";

        if (string.IsNullOrWhiteSpace(workOrder.Type))
            workOrder.Type = "corrective";

        if (string.IsNullOrWhiteSpace(workOrder.MachineId))
            workOrder.MachineId = "unknown-machine";

        workOrder.CreatedAtUtc    = DateTime.UtcNow;
        workOrder.UpdatedAtUtc    = DateTime.UtcNow;

        _logger.LogInformation(
            "Creating work order {WorkOrderNumber} for machine {MachineId}.",
            workOrder.WorkOrderNumber, workOrder.MachineId);

        try
        {
            var response = await _workOrders.CreateItemAsync(
                workOrder,
                new PartitionKey(workOrder.Status),
                cancellationToken: ct);

            _logger.LogInformation(
                "Work order {WorkOrderNumber} created (id={Id}, RU={RU}).",
                workOrder.WorkOrderNumber, workOrder.Id, response.RequestCharge);

            return response.Resource;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex,
                "Cosmos DB error while creating work order {WorkOrderNumber} (status {Status}).",
                workOrder.WorkOrderNumber, ex.StatusCode);
            throw;
        }
    }
}
