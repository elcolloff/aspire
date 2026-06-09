import type { DistributedApplicationBuilder, ExecutableResource } from '../.modules/aspire.js';
import {
    AspireExport,
    defineIntegration,
    type AspireTypeRef,
} from '../.modules/base.js';

const builderType: AspireTypeRef = {
    typeId: 'Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder',
    category: 'Handle',
    isInterface: true,
};

const executableType: AspireTypeRef = {
    typeId: 'Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource',
    category: 'Handle',
    isInterface: false,
};

const stringType: AspireTypeRef = {
    typeId: 'string',
    category: 'Primitive',
};

const stringArrayType: AspireTypeRef = {
    typeId: 'array',
    category: 'Array',
    elementType: stringType,
};

interface AddDenoAppArgs
{
    builder: DistributedApplicationBuilder;
    name: string;
    appDirectory: string;
    scriptPath: string;
    args?: string[];
}

interface WithDenoTaskArgs
{
    resource: ExecutableResource;
    taskName: string;
    args?: string[];
}

export const addDenoApp = AspireExport<AddDenoAppArgs, ExecutableResource>(
    {
        id: 'spike.deno/addDenoApp',
        method: 'addDenoApp',
        description: 'Adds a Deno application as an executable resource',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: builderType.typeId,
            targetType: builderType,
            targetParameterName: 'builder',
            returnsBuilder: true,
            returnType: executableType,
            parameters: [
                { name: 'name', type: stringType },
                { name: 'appDirectory', type: stringType },
                { name: 'scriptPath', type: stringType },
                { name: 'args', type: stringArrayType, isOptional: true },
            ],
        },
    },
    async ({ builder, name, appDirectory, scriptPath, args = [] }) => {
        console.log(`[@spike/aspire-deno] addDenoApp('${name}') starting`);

        const deno = await builder
            .addExecutable(name, 'deno', appDirectory, ['run', '--allow-net', '--allow-env', scriptPath, ...args])
            .withRequiredCommand('deno', { helpLink: 'https://docs.deno.com/runtime/getting_started/installation/' })
            .withOtlpExporter()
            .withIconName('CodeJsRectangle')
            .withEnvironment('DENO_ENV', 'development');

        console.log(`[@spike/aspire-deno] addDenoApp('${name}') complete`);
        return deno;
    }
);

export const withDenoTask = AspireExport<WithDenoTaskArgs, ExecutableResource>(
    {
        id: 'spike.deno/withDenoTask',
        method: 'withDenoTask',
        description: 'Runs a Deno application by invoking a task from deno.json',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: executableType.typeId,
            targetType: executableType,
            targetParameterName: 'resource',
            returnsBuilder: true,
            returnType: executableType,
            parameters: [
                { name: 'taskName', type: stringType },
                { name: 'args', type: stringArrayType, isOptional: true },
            ],
        },
    },
    async ({ resource, taskName, args = [] }) => {
        await resource.withArgsReplace(['task', taskName, ...args]);

        return resource;
    }
);

export default defineIntegration({
    name: 'DenoIntegration',
    capabilities: [addDenoApp, withDenoTask],
});
