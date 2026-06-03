import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addAzureSandboxGroup('sandboxes');

await builder
    .addViteApp('site', './site')
    .publishAsStaticWebsite({ outputPath: 'dist' })
    .withExternalHttpEndpoints();

await builder.build().run();
