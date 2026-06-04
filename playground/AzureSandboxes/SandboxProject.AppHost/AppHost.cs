var builder = DistributedApplication.CreateBuilder(args);

var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");

builder.AddProject<Projects.SandboxProject_ApiService>("api")
    .WithHttpEndpoint(targetPort: 8080)
    .WithExternalHttpEndpoints()
    .WithEnvironment("SANDBOX_PROJECT_VALUE", "project-resource")
    .WithComputeEnvironment(sandboxGroup)
    .WithAzureSandboxResources(cpu: "1000m", memory: "2048Mi", disk: "20480Mi")
    .WithAzureSandboxEndpointAnonymousAccess("http");

builder.Build().Run();
