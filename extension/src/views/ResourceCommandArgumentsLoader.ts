import * as vscode from 'vscode';
import { spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    resourceCommandDynamicInputsFailed,
    resourceCommandLoadingDynamicInputs,
} from '../loc/strings';
import { ResourceCommandArgumentInputJson } from './AppHostDataRepository';
import {
    buildResourceCommandCliArgs,
    ResourceCommandArgumentLoader,
    ResourceCommandArgumentValue,
} from './ResourceCommandArguments';

export interface ResourceCommandArgumentLoaderContext {
    terminalProvider: AspireTerminalProvider;
    resourceName: string;
    commandName: string;
    appHostPath?: string;
}

// Builds a loader that shells out to `aspire resource <name> <command> --load-arguments` so that
// callers (tree view, code lens, etc.) share a single implementation of dynamic argument loading.
// Returns undefined when the CLI invocation fails so collectResourceCommandArguments can abort
// the prompt flow.
export function createResourceCommandArgumentLoader(context: ResourceCommandArgumentLoaderContext): ResourceCommandArgumentLoader {
    return values => loadResourceCommandArgumentInputs(context, values);
}

async function loadResourceCommandArgumentInputs(
    context: ResourceCommandArgumentLoaderContext,
    values: readonly ResourceCommandArgumentValue[]): Promise<ResourceCommandArgumentInputJson[] | undefined> {
    // Refuse to invoke `aspire resource ... --load-arguments` without an explicit --apphost.
    // Without it the CLI auto-discovers some AppHost, which can return dynamic inputs for a
    // different process than the one the user clicked on when multiple AppHosts are running.
    if (!context.appHostPath) {
        extensionLogOutputChannel.warn(`Failed to load resource command arguments for '${context.resourceName}' (${context.commandName}): no AppHost path could be resolved.`);
        await vscode.window.showWarningMessage(resourceCommandDynamicInputsFailed, { modal: true });
        return undefined;
    }

    return await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: resourceCommandLoadingDynamicInputs },
        async () => {
            try {
                const cliPath = await context.terminalProvider.getAspireCliExecutablePath();
                const args = ['resource', context.resourceName, context.commandName, '--load-arguments', '--apphost', context.appHostPath!];
                args.push(...buildResourceCommandCliArgs(values));

                const loadedInputs = await new Promise<ResourceCommandArgumentInputJson[] | undefined>((resolve) => {
                    let settled = false;
                    let stdout = '';
                    let stderr = '';
                    const finish = (value: ResourceCommandArgumentInputJson[] | undefined) => {
                        if (!settled) {
                            settled = true;
                            resolve(value);
                        }
                    };

                    const child = spawnCliProcess(context.terminalProvider, cliPath, args, {
                        noExtensionVariables: true,
                        stdoutCallback: data => {
                            stdout += data;
                        },
                        stderrCallback: data => {
                            stderr += data;
                        },
                        errorCallback: error => {
                            extensionLogOutputChannel.warn(`Failed to load resource command arguments: ${error.message}`);
                            finish(undefined);
                        },
                        exitCallback: code => {
                            if (code !== 0) {
                                extensionLogOutputChannel.warn(`aspire resource --load-arguments exited with code ${code}. ${stderr.trim()}`);
                                finish(undefined);
                                return;
                            }

                            try {
                                const parsed = JSON.parse(stdout.trim());
                                if (isResourceCommandArgumentInputArray(parsed)) {
                                    finish(parsed);
                                    return;
                                }

                                extensionLogOutputChannel.warn('aspire resource --load-arguments returned JSON that was not resource command argument metadata.');
                            } catch (error) {
                                // This command is a hidden machine-readable contract. If anything besides
                                // the JSON metadata is written to stdout, fail the load so the CLI bug is
                                // visible instead of silently accepting a partial parse.
                                extensionLogOutputChannel.warn(`aspire resource --load-arguments returned invalid JSON stdout: ${error}`);
                            }

                            finish(undefined);
                        },
                    });

                    child.stdin.end();
                });

                if (!loadedInputs) {
                    await vscode.window.showWarningMessage(resourceCommandDynamicInputsFailed, { modal: true });
                }

                return loadedInputs;
            } catch (error) {
                extensionLogOutputChannel.warn(`Failed to load resource command arguments: ${error}`);
                await vscode.window.showWarningMessage(resourceCommandDynamicInputsFailed, { modal: true });
                return undefined;
            }
        });
}

function isResourceCommandArgumentInputArray(value: unknown): value is ResourceCommandArgumentInputJson[] {
    return Array.isArray(value) && value.every(item => {
        if (typeof item !== 'object' || item === null) {
            return false;
        }

        const candidate = item as Record<string, unknown>;
        return typeof candidate.name === 'string' && typeof candidate.inputType === 'string';
    });
}
