// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Container Apps sandbox resources to the application model.
/// </summary>
public static class AzureSandboxesExtensions
{
    /// <summary>
    /// Adds an Azure connector namespace resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A resource builder for the connector namespace.</returns>
    /// <remarks>
    /// The connector namespace hosts connector connections and managed MCP server configs.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorGatewayResource> AddAzureConnectorGateway(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddAzureProvisioning();

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var gatewayResource = (AzureConnectorGatewayResource)infrastructure.AspireResource;
            var gateway = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = ConnectorGateway.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                infrastructure => new ConnectorGateway(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    Location = BicepFunction.GetResourceGroup().Location,
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                });

            gateway.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned;

            foreach (var connectionResource in gatewayResource.Connections)
            {
                var connection = new ConnectorGatewayConnection(Infrastructure.NormalizeBicepIdentifier(connectionResource.Name))
                {
                    Parent = gateway,
                    Name = connectionResource.ConnectionName,
                    DisplayName = connectionResource.DisplayName ?? connectionResource.ConnectionName,
                    ConnectorName = connectionResource.ConnectorName
                };
                infrastructure.Add(connection);
            }

            foreach (var configResource in gatewayResource.McpServerConfigs)
            {
                var config = new ConnectorGatewayMcpServerConfig(Infrastructure.NormalizeBicepIdentifier(configResource.Name))
                {
                    Parent = gateway,
                    Name = configResource.ConfigName,
                    Kind = "ManagedMcpServer"
                };

                if (!string.IsNullOrWhiteSpace(configResource.Description))
                {
                    config.Description = configResource.Description;
                }

                foreach (var connectorDefinition in configResource.Connectors)
                {
                    var connector = new ConnectorGatewayMcpConnector
                    {
                        Name = connectorDefinition.Name,
                        ConnectionName = connectorDefinition.Connection.ConnectionName
                    };

                    foreach (var operationDefinition in connectorDefinition.Operations)
                    {
                        var operation = new ConnectorGatewayMcpOperation
                        {
                            Name = operationDefinition.Name
                        };

                        if (!string.IsNullOrWhiteSpace(operationDefinition.DisplayName))
                        {
                            operation.DisplayName = operationDefinition.DisplayName;
                        }

                        if (!string.IsNullOrWhiteSpace(operationDefinition.Description))
                        {
                            operation.Description = operationDefinition.Description;
                        }

                        connector.Operations.Add(operation);
                    }

                    config.Connectors.Add(connector);
                }

                infrastructure.Add(config);
            }

            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = gateway.Id.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = gateway.Name.ToBicepExpression() });
            var gatewayIdentity = new MemberExpression(new IdentifierExpression(gateway.BicepIdentifier), "identity");
            infrastructure.Add(new ProvisioningOutput("principalId", typeof(string)) { Value = (BicepValue<string>)new MemberExpression(gatewayIdentity, "principalId") });
            infrastructure.Add(new ProvisioningOutput("tenantId", typeof(string)) { Value = (BicepValue<string>)new MemberExpression(gatewayIdentity, "tenantId") });
        }

        var resource = new AzureConnectorGatewayResource(name, ConfigureInfrastructure);
        return builder.AddResource(resource);
    }

    /// <summary>
    /// Adds a connector connection to an Azure connector namespace.
    /// </summary>
    /// <param name="builder">The connector namespace resource builder.</param>
    /// <param name="name">The name of the Aspire resource.</param>
    /// <param name="connectorName">The connector catalog name.</param>
    /// <param name="displayName">The friendly display name shown for the connection.</param>
    /// <param name="connectionName">The Azure connector connection name. Defaults to <paramref name="name"/>.</param>
    /// <returns>A resource builder for the connector connection.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorGatewayConnectionResource> AddConnection(
        this IResourceBuilder<AzureConnectorGatewayResource> builder,
        [ResourceName] string name,
        string connectorName,
        string? displayName = null,
        string? connectionName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        connectionName ??= name;

        var connection = new AzureConnectorGatewayConnectionResource(name, connectionName, connectorName, displayName, builder.Resource);
        builder.Resource.Connections.Add(connection);
        return builder.ApplicationBuilder.AddResource(connection);
    }

    /// <summary>
    /// Adds a managed MCP server config to an Azure connector namespace.
    /// </summary>
    /// <param name="builder">The connector namespace resource builder.</param>
    /// <param name="name">The name of the Aspire resource.</param>
    /// <param name="description">The description shown to MCP clients.</param>
    /// <param name="configName">The Azure MCP server config name. Defaults to <paramref name="name"/>.</param>
    /// <returns>A resource builder for the MCP server config.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> AddMcpServerConfig(
        this IResourceBuilder<AzureConnectorGatewayResource> builder,
        [ResourceName] string name,
        string? description = null,
        string? configName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        configName ??= name;

        var config = new AzureConnectorGatewayMcpServerConfigResource(name, configName, description, builder.Resource);
        builder.Resource.McpServerConfigs.Add(config);
        return builder.ApplicationBuilder.AddResource(config);
    }

    /// <summary>
    /// Adds a connector operation route to a managed MCP server config.
    /// </summary>
    /// <param name="builder">The MCP server config resource builder.</param>
    /// <param name="name">The connector catalog name to expose through this MCP server config.</param>
    /// <param name="connection">The connector connection backing this MCP route.</param>
    /// <param name="operationName">The connector operation name.</param>
    /// <param name="displayName">The display name shown for the operation.</param>
    /// <param name="description">The description shown for the operation.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> WithConnector(
        this IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> builder,
        string name,
        IResourceBuilder<AzureConnectorGatewayConnectionResource> connection,
        string operationName,
        string? displayName = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        var connector = builder.Resource.Connectors.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        if (connector is null)
        {
            connector = new AzureConnectorGatewayMcpConnectorDefinition(name, connection.Resource);
            builder.Resource.Connectors.Add(connector);
        }
        else if (!ReferenceEquals(connector.Connection, connection.Resource))
        {
            throw new InvalidOperationException($"Connector '{name}' is already registered with a different connection resource.");
        }

        connector.Operations.Add(new AzureConnectorGatewayMcpOperationDefinition(operationName, displayName, description));
        return builder;
    }

    /// <summary>
    /// Adds an Azure Container Apps sandbox group resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A resource builder for the sandbox group.</returns>
    /// <remarks>
    /// Use <see cref="WithRoleAssignments{T}(IResourceBuilder{T}, IResourceBuilder{AzureSandboxGroupResource}, AzureSandboxGroupBuiltInRole[])"/>
    /// to grant an application resource access to the sandbox group.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureSandboxGroupResource> AddAzureSandboxGroup(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddAzureProvisioning();

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var sandboxResource = (AzureSandboxGroupResource)infrastructure.AspireResource;
            var sandboxGroup = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = SandboxGroup.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                infrastructure =>
                {
                    var resource = new SandboxGroup(infrastructure.AspireResource.GetBicepIdentifier())
                    {
                        Location = BicepFunction.GetResourceGroup().Location,
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    ApplyManagedServiceIdentity(resource.Identity, sandboxResource, infrastructure);
                    return resource;
                });

            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = sandboxGroup.Id.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = sandboxGroup.Name.ToBicepExpression() });
        }

        var resource = new AzureSandboxGroupResource(name, ConfigureInfrastructure)
        {
            DefaultContainerRegistry = CreateDefaultAzureContainerRegistry(builder, $"{name}-acr")
        };

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Configures the Azure sandbox group to use no managed identity.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureSandboxGroupResource> WithNoManagedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ManagedIdentityType = ManagedServiceIdentityType.None;
        builder.Resource.UserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Configures the Azure sandbox group to use a system-assigned managed identity.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureSandboxGroupResource> WithSystemAssignedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ManagedIdentityType = ManagedServiceIdentityType.SystemAssigned;
        builder.Resource.UserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Configures the Azure sandbox group to use a user-assigned managed identity.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <param name="identity">The user-assigned managed identity resource.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureSandboxGroupResource> WithUserAssignedIdentity(
        this IResourceBuilder<AzureSandboxGroupResource> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);

        builder.Resource.ManagedIdentityType = ManagedServiceIdentityType.UserAssigned;
        builder.Resource.UserAssignedIdentities.Add(identity.Resource);
        return builder;
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Container Apps sandbox group resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure sandbox group resource.</param>
    /// <param name="roles">The built-in sandbox group roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    [AspireExportIgnore(Reason = "AzureSandboxGroupBuiltInRole is not compatible with ATS. Use the AzureSandboxGroupRole-based overload instead.")]
    public static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> target,
        params AzureSandboxGroupBuiltInRole[] roles)
        where T : IResource
    {
        return builder.WithRoleAssignments(target, AzureSandboxGroupBuiltInRole.GetBuiltInRoleName, roles);
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Container Apps sandbox group resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure sandbox group resource.</param>
    /// <param name="roles">The Azure sandbox group roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <exception cref="ArgumentException">Thrown when a role value is not a valid <see cref="AzureSandboxGroupRole"/> value.</exception>
    [AspireExport("withSandboxGroupRoleAssignments")]
    internal static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> target,
        params AzureSandboxGroupRole[] roles)
        where T : IResource
    {
        if (roles is null || roles.Length == 0)
        {
            return builder.WithRoleAssignments(target, Array.Empty<AzureSandboxGroupBuiltInRole>());
        }

        var builtInRoles = new AzureSandboxGroupBuiltInRole[roles.Length];
        for (var i = 0; i < roles.Length; i++)
        {
            builtInRoles[i] = roles[i] switch
            {
                AzureSandboxGroupRole.SandboxGroupDataOwner => AzureSandboxGroupBuiltInRole.SandboxGroupDataOwner,
                _ => throw new ArgumentException($"'{roles[i]}' is not a valid {nameof(AzureSandboxGroupRole)} value.", nameof(roles))
            };
        }

        return builder.WithRoleAssignments(target, builtInRoles);
    }

    private static void ApplyManagedServiceIdentity(ManagedServiceIdentity identity, AzureSandboxGroupResource resource, AzureResourceInfrastructure infrastructure)
    {
        if (resource.ManagedIdentityType == ManagedServiceIdentityType.None && resource.UserAssignedIdentities.Count == 0)
        {
            return;
        }

        identity.ManagedServiceIdentityType = resource.ManagedIdentityType;

        foreach (var userAssignedIdentity in resource.UserAssignedIdentities)
        {
            var userAssignedIdentityIdParameter = userAssignedIdentity.Id.AsProvisioningParameter(infrastructure);
            var userAssignedIdentityId = BicepFunction.Interpolate($"{userAssignedIdentityIdParameter}").Compile().ToString();
            identity.UserAssignedIdentities[userAssignedIdentityId] = new UserAssignedIdentityDetails();
        }
    }

    private static AzureContainerRegistryResource CreateDefaultAzureContainerRegistry(IDistributedApplicationBuilder builder, string name)
    {
        var resource = new AzureContainerRegistryResource(name, ContainerRegistryInfrastructure.ConfigureContainerRegistry);
        if (builder.ExecutionContext.IsPublishMode)
        {
            builder.AddResource(resource)
                .WithAnnotation(new DefaultRoleAssignmentsAnnotation(new HashSet<RoleDefinition>()));
        }

        return resource;
    }
}
