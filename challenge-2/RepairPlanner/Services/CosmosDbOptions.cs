namespace RepairPlanner.Services;

// Simple value object holding connection details; populated from environment variables in Program.cs.
public sealed record CosmosDbOptions(
    string Endpoint,
    string Key,
    string DatabaseName
);
