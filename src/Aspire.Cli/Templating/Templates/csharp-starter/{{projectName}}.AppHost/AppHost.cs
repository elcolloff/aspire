var builder = DistributedApplication.CreateBuilder(args);

{{#useRedisCache}}
var cache = builder.AddRedis("cache");

{{/useRedisCache}}
var apiService = builder.AddProject<Projects.{{generatedClassNamePrefix}}_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.{{generatedClassNamePrefix}}_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
{{#useRedisCache}}
    .WithReference(cache)
    .WaitFor(cache)
{{/useRedisCache}}
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
