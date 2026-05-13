// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Telemetry;
using StreamJsonRpc;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Adds profiling spans and trace-context propagation around StreamJsonRpc calls.
/// </summary>
/// <remarks>
/// StreamJsonRpc does not flow Activity.Current across the CLI/AppHost
/// process boundary for this backchannel, so these helpers wrap client calls and inject
/// W3C trace context into request-object RPC parameters.
/// </remarks>
internal static class ProfilingJsonRpcExtensions
{
    public static async Task InvokeWithProfilingAsync(
        this JsonRpc rpc,
        ProfilingTelemetry? profilingTelemetry,
        string connectionName,
        string methodName,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        using var activity = profilingTelemetry?.StartJsonRpcClientCall(connectionName, methodName, streaming: false) ?? default;
        arguments = WithProfilingContext(arguments, activity.CreateBackchannelProfilingContext());

        try
        {
            await rpc.InvokeWithCancellationAsync(methodName, arguments, cancellationToken).ConfigureAwait(false);
            activity.AddJsonRpcResponseReceivedEvent();
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    public static async Task<T> InvokeWithProfilingAsync<T>(
        this JsonRpc rpc,
        ProfilingTelemetry? profilingTelemetry,
        string connectionName,
        string methodName,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        using var activity = profilingTelemetry?.StartJsonRpcClientCall(connectionName, methodName, streaming: false) ?? default;
        arguments = WithProfilingContext(arguments, activity.CreateBackchannelProfilingContext());

        try
        {
            var response = await rpc.InvokeWithCancellationAsync<T>(methodName, arguments, cancellationToken).ConfigureAwait(false);
            activity.AddJsonRpcResponseReceivedEvent();
            return response;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    public static async Task<IAsyncEnumerable<T>?> InvokeStreamingWithProfilingAsync<T>(
        this JsonRpc rpc,
        ProfilingTelemetry? profilingTelemetry,
        string connectionName,
        string methodName,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        var activity = profilingTelemetry?.StartJsonRpcClientCall(connectionName, methodName, streaming: true) ?? default;
        arguments = WithProfilingContext(arguments, activity.CreateBackchannelProfilingContext());

        try
        {
            var response = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<T>>(methodName, arguments, cancellationToken).ConfigureAwait(false);
            activity.AddJsonRpcResponseReceivedEvent();
            if (response is null)
            {
                activity.Dispose();
                return null;
            }

            return EnumerateWithProfiling(response, activity, cancellationToken);
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            activity.Dispose();
            throw;
        }
    }

    private static async IAsyncEnumerable<T> EnumerateWithProfiling<T>(
        IAsyncEnumerable<T> response,
        ProfilingTelemetry.ActivityScope activity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // StreamJsonRpc returns the IAsyncEnumerable before any stream items are read.
        // Keep the client span alive through enumeration so the measured duration includes
        // the server producing items, transport time, and caller-side consumption.
        var itemCount = 0;
        var enumerator = response.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                T item;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    item = enumerator.Current;
                }
                catch (Exception ex)
                {
                    activity.SetError(ex);
                    throw;
                }

                if (itemCount == 0)
                {
                    activity.AddJsonRpcStreamFirstItemEvent();
                }

                itemCount++;
                yield return item;
            }

            activity.AddJsonRpcStreamCompletedEvent();
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            activity.SetJsonRpcStreamItemCount(itemCount);
            activity.Dispose();
        }
    }

    private static object?[] WithProfilingContext(object?[] arguments, BackchannelProfilingContext? profilingContext)
    {
        if (profilingContext is null || arguments.Length != 1)
        {
            return arguments;
        }

        // StreamJsonRpc accepts RPC parameters as an object array. The auxiliary backchannel
        // contract uses a single request object parameter, so replace that one argument with
        // a copy carrying the profiling context instead of mutating the caller's instance.
        return arguments[0] is BackchannelRequest request
            ? [request.WithProfilingContext(profilingContext)]
            : arguments;
    }

}
