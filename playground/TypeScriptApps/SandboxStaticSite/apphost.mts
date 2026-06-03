import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addAzureSandboxGroup('sandboxes');

const site = await builder.addDockerfile('site', './site');
await site.withHttpEndpoint({ name: 'http', targetPort: 80 });
await site.withExternalHttpEndpoints();

await builder.build().run();
