# Handling design-time connection strings when publishing migration bundles

When you generate a migration bundle during `aspire publish`
(`PublishAsMigrationBundle(...)`), Aspire runs the EF Core design-time tooling
(`dotnet ef migrations bundle`) against your startup project. EF Core has to
create an instance of your `DbContext` at design time to discover the model.

If your `DbContext` is configured with a provider that **parses the connection
string eagerly** while the context is being created — for example the Azure
Npgsql integration (`EnrichAzureNpgsqlDbContext`) — design-time creation fails
because the value injected for the connection string at publish time is a
placeholder/manifest expression rather than a real connection string. The error
looks like this:

```
✗ [ERR] Unable to create a 'DbContext' of type 'AppDbContext'. The exception
'Format of the initialization string does not conform to specification starting
at index 0.' was thrown while attempting to create an instance.
✗ Step 'api-migrations-generate-migration-bundle' failed.
```

A migration bundle does not need a real connection string at design time: the
bundle receives the connection string at **run time** via the `--connection`
argument that Aspire wires up automatically for the bundle container. So the fix
is to skip the connection-string parsing while EF Core is building the model at
design time.

## Steps to fix the project

EF Core exposes `EF.IsDesignTime`, which is `true` while the design-time tooling
(including the `migrations bundle` command) is running. Use it to call a
different `UseNpgsql` overload and to skip `EnrichAzureNpgsqlDbContext` at design
time.

Given a project that registers the `DbContext` like this:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static IHostApplicationBuilder AddAppData(this IHostApplicationBuilder builder)
{
    builder.Services.AddPooledDbContextFactory<AppDbContext>((sp, options) =>
    {
        options.UseNpgsql(builder.Configuration.GetConnectionString("postgresdb"));
    });

    builder.EnrichAzureNpgsqlDbContext<AppDbContext>();

    return builder;
}
```

1. Call the parameterless `UseNpgsql()` overload when `EF.IsDesignTime` is
   `true`. This lets EF Core build the model without parsing a connection string.
2. Only call `EnrichAzureNpgsqlDbContext` when **not** at design time, because the
   enrichment configures an Entra ID data source that parses the connection string.

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static IHostApplicationBuilder AddAppData(this IHostApplicationBuilder builder)
{
    builder.Services.AddPooledDbContextFactory<AppDbContext>((sp, options) =>
    {
        if (EF.IsDesignTime)
        {
            // At design time (e.g. when generating a migration bundle) no real
            // connection string is available. Use the parameterless overload so
            // EF Core can build the model without parsing a connection string.
            options.UseNpgsql();
        }
        else
        {
            options.UseNpgsql(builder.Configuration.GetConnectionString("postgresdb"));
        }
    });

    if (!EF.IsDesignTime)
    {
        // EnrichAzureNpgsqlDbContext configures an Entra ID data source that
        // parses the connection string, which is not available at design time.
        builder.EnrichAzureNpgsqlDbContext<AppDbContext>();
    }

    return builder;
}
```

With this change, `aspire publish` generates the migration bundle successfully,
and the deployed bundle still receives the real connection string at run time via
the `--connection` argument that Aspire injects into the bundle container.

> The same pattern applies to any provider that parses the connection string while
> the `DbContext` is being created (for example a custom `NpgsqlDataSource`). Guard
> the connection-string-dependent configuration with `EF.IsDesignTime`.
