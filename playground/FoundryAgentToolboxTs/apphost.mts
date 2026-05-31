// FoundryAgentToolboxTs sample
//
// Demonstrates wiring a Foundry Agent that calls a custom MCP server via the Foundry Toolbox,
// from a TypeScript AppHost, where the MCP server requires bearer-token authentication.
//
// Topology:
//
//   mcp-bearer-token (parameter, secret)
//        |
//        +--> mcp-server (Node, Express, @modelcontextprotocol/sdk)
//        |        validates Authorization: Bearer <token>
//        |
//        +--> foundry.project.field-tools toolbox
//                 .withMcpTool('directory', mcpServer http endpoint,
//                              { authorizationToken: refExpr`${bearerToken}`, headers: { ... } })
//
// At deploy time the toolbox is created on the Foundry data plane and the MCP tool is registered
// with the bearer token as its outbound Authorization header. When a Foundry agent invokes a tool,
// the Foundry data plane calls the MCP server with that header, the server validates it, and the
// tool runs.
//
// In `aspire start` (local) mode the Foundry data plane lives in Azure and cannot reach
// http://localhost:* MCP servers, so end-to-end tool invocations only fire after `aspire deploy`.
// The local run still validates that all resources start healthy, the MCP server enforces auth,
// and `aspire publish` produces correct artifacts (bicep + compose).

import { createBuilder, refExpr, FoundryModels } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const bearerToken = await builder.addParameter('mcp-bearer-token', { secret: true });

const mcpServer = await builder
    .addNodeApp('mcp-server', './mcp-server', 'src/server.ts')
    .withRunScript('start')
    .withHttpEndpoint({ env: 'PORT' })
    .withEnvironment('MCP_BEARER_TOKEN', bearerToken);

const foundry = await builder.addFoundry('foundry');
const project = await foundry.addProject('project');

// Chat model the Foundry-side agent will use. The toolbox tools are visible to any agent in the
// project that opts into the toolbox.
const _chat = await project.addModelDeployment('chat', FoundryModels.OpenAI.Gpt41Mini);

const toolbox = await project.addToolbox('field-tools', { version: 'v1' });

// A built-in Foundry tool, mainly to show the toolbox can mix Foundry-managed and custom tools.
await toolbox.withWebSearchTool();

// The custom MCP tool. The Foundry data plane calls mcpServer over HTTP and sends the bearer
// token in the Authorization header, so the same parameter we gave the MCP server above also
// flows out from Foundry as auth.
await toolbox.withMcpTool('directory', mcpServer.getEndpoint('http'), {
    authorizationToken: refExpr`${bearerToken}`,
    headers: {
        // Custom tracing/tenant header so operators can correlate Foundry-initiated calls in the
        // MCP server logs.
        'x-app-source': refExpr`foundry-toolbox-ts-sample`
    }
});

await builder.build().run();
