// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Aspire.Dashboard.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[Config(typeof(Config))]
public class TelemetryRepositoryBenchmarks
{
    private const int TraceCount = 250;
    private const int SpansPerTrace = 40;
    private const int TelemetryFlowTraceCount = 100;
    private const int TelemetryFlowResourceCount = 8;
    private const int TelemetryFlowCallsPerTrace = 25;
    private const int TelemetryFlowServerSpansPerCall = 2;
    private const int EdgeKeySnapshotOperationsPerInvoke = 100_000;

    private readonly List<TelemetryFilter> _durationFilters =
    [
        new FieldTelemetryFilter
        {
            Field = KnownTraceFields.DurationField,
            Condition = FilterCondition.GreaterThanOrEqual,
            Value = "50"
        }
    ];

    private readonly List<TelemetryFilter> _noMatchDurationFilters =
    [
        new FieldTelemetryFilter
        {
            Field = KnownTraceFields.DurationField,
            Condition = FilterCondition.GreaterThanOrEqual,
            Value = "1000"
        }
    ];

    private readonly List<TelemetryFilter> _noMatchAttributeFilters =
    [
        new FieldTelemetryFilter
        {
            Field = "missing.attribute",
            Condition = FilterCondition.Equals,
            Value = "never"
        }
    ];

    private RepeatedField<ResourceSpans> _resourceSpans = [];
    private RepeatedField<ResourceSpans> _telemetryFlowResourceSpans = [];
    private TelemetryRepository _queryRepository = null!;
    private TelemetryRepository _telemetryGraphRepository = null!;

    [GlobalSetup]
    public void Setup()
    {
        _resourceSpans = CreateResourceSpans(TraceCount, SpansPerTrace);
        _telemetryFlowResourceSpans = CreateTelemetryFlowResourceSpans();
        _queryRepository = CreateRepository();
        _queryRepository.AddTraces(new AddContext(), _resourceSpans);
        _telemetryGraphRepository = CreateRepository(TelemetryFlowTraceCount + 1);
        _telemetryGraphRepository.AddTraces(new AddContext(), _telemetryFlowResourceSpans);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _queryRepository.Dispose();
        _telemetryGraphRepository.Dispose();
    }

    [Benchmark(Description = "TelemetryRepository: add 10k spans")]
    public int AddTracesLargeBatch()
    {
        using var repository = CreateRepository();
        var context = new AddContext();
        repository.AddTraces(context, _resourceSpans);

        return context.SuccessCount;
    }

    [Benchmark(Description = "TelemetryRepository: add telemetry flow traces")]
    public int AddTracesTelemetryFlow()
    {
        using var repository = CreateRepository(TelemetryFlowTraceCount + 1);
        var context = new AddContext();
        repository.AddTraces(context, _telemetryFlowResourceSpans);

        return context.SuccessCount + repository.GetTelemetryGraphEdgeKeys().Count;
    }

    [Benchmark(Description = "TelemetryRepository: telemetry graph edge keys", OperationsPerInvoke = EdgeKeySnapshotOperationsPerInvoke)]
    public int GetTelemetryGraphEdgeKeys()
    {
        var count = 0;
        for (var i = 0; i < EdgeKeySnapshotOperationsPerInvoke; i++)
        {
            count += _telemetryGraphRepository.GetTelemetryGraphEdgeKeys().Count;
        }

        return count;
    }

    [Benchmark(Description = "TelemetryRepository: query no filters")]
    public int GetTracesNoFilters()
    {
        var result = _queryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKeys = [],
            Filters = [],
            StartIndex = 0,
            Count = 100
        });

        return result.PagedResult.Items.Count;
    }

    [Benchmark(Description = "TelemetryRepository: duration filter 10k spans")]
    public int GetTracesDurationFilter()
    {
        var result = _queryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKeys = [],
            Filters = _durationFilters,
            StartIndex = 0,
            Count = 100
        });

        return result.PagedResult.Items.Count;
    }

    [Benchmark(Description = "TelemetryRepository: no-match duration 10k spans")]
    public int GetTracesNoMatchDurationFilter()
    {
        var result = _queryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKeys = [],
            Filters = _noMatchDurationFilters,
            StartIndex = 0,
            Count = 100
        });

        return result.PagedResult.Items.Count;
    }

    [Benchmark(Description = "TelemetryRepository: no-match filter 10k spans")]
    public int GetTracesNoMatchAttributeFilter()
    {
        var result = _queryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKeys = [],
            Filters = _noMatchAttributeFilters,
            StartIndex = 0,
            Count = 100
        });

        return result.PagedResult.Items.Count;
    }

    private static TelemetryRepository CreateRepository(int maxTraceCount = TraceCount + 1)
    {
        return new TelemetryRepository(
            NullLoggerFactory.Instance,
            Options.Create(new DashboardOptions
            {
                TelemetryLimits = new TelemetryLimitOptions
                {
                    MaxTraceCount = maxTraceCount
                }
            }),
            new PauseManager(),
            []);
    }

    private static RepeatedField<ResourceSpans> CreateResourceSpans(int traceCount, int spansPerTrace)
    {
        return
        [
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = new InstrumentationScope { Name = "BenchmarkScope" },
                        Spans = { CreateSpans(traceCount, spansPerTrace) }
                    }
                }
            }
        ];
    }

    private static IEnumerable<OtlpProtoSpan> CreateSpans(int traceCount, int spansPerTrace)
    {
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var traceIndex = 0; traceIndex < traceCount; traceIndex++)
        {
            for (var spanIndex = 0; spanIndex < spansPerTrace; spanIndex++)
            {
                var spanStartTime = startTime.AddSeconds(traceIndex).AddTicks(spanIndex);
                yield return new OtlpProtoSpan
                {
                    TraceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes($"trace-{traceIndex:0000}")),
                    SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes($"span-{spanIndex:0000}")),
                    ParentSpanId = spanIndex == 0
                        ? ByteString.Empty
                        : ByteString.CopyFrom(Encoding.UTF8.GetBytes($"span-{spanIndex - 1:0000}")),
                    Name = spanIndex == 0 ? "root-span" : $"span-{spanIndex}",
                    Kind = OtlpProtoSpan.Types.SpanKind.Internal,
                    StartTimeUnixNano = DateTimeToUnixNanoseconds(spanStartTime),
                    EndTimeUnixNano = DateTimeToUnixNanoseconds(spanStartTime.AddMilliseconds(spanIndex % 10 == 0 ? 100 : 5)),
                    Attributes =
                    {
                        new KeyValue
                        {
                            Key = "benchmark.index",
                            Value = new AnyValue { StringValue = spanIndex.ToString(CultureInfo.InvariantCulture) }
                        }
                    }
                };
            }
        }
    }

    private static RepeatedField<ResourceSpans> CreateTelemetryFlowResourceSpans()
    {
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var spansByResource = new List<OtlpProtoSpan>[TelemetryFlowResourceCount];
        for (var i = 0; i < spansByResource.Length; i++)
        {
            spansByResource[i] = [];
        }

        for (var traceIndex = 0; traceIndex < TelemetryFlowTraceCount; traceIndex++)
        {
            var traceId = $"flow-trace-{traceIndex:0000}";
            var traceStartTime = startTime.AddSeconds(traceIndex);
            spansByResource[0].Add(CreateSpan(
                traceId,
                spanId: $"flow-{traceIndex:0000}-root",
                parentSpanId: null,
                name: "GET /checkout",
                OtlpProtoSpan.Types.SpanKind.Server,
                traceStartTime,
                traceStartTime.AddMilliseconds(100)));

            for (var callIndex = 0; callIndex < TelemetryFlowCallsPerTrace; callIndex++)
            {
                var sourceResourceIndex = callIndex % TelemetryFlowResourceCount;
                var destinationResourceIndex = (sourceResourceIndex + 1) % TelemetryFlowResourceCount;
                var sourceSpanId = $"flow-{traceIndex:0000}-client-{callIndex:0000}";
                var spanStartTime = traceStartTime.AddMilliseconds(callIndex);

                spansByResource[sourceResourceIndex].Add(CreateSpan(
                    traceId,
                    sourceSpanId,
                    parentSpanId: sourceResourceIndex == 0 ? $"flow-{traceIndex:0000}-root" : null,
                    name: $"HTTP service-{destinationResourceIndex}",
                    OtlpProtoSpan.Types.SpanKind.Client,
                    spanStartTime,
                    spanStartTime.AddMilliseconds(10),
                    attributes:
                    [
                        new KeyValue { Key = "server.address", Value = new AnyValue { StringValue = $"service-{destinationResourceIndex}" } },
                        new KeyValue { Key = "server.port", Value = new AnyValue { StringValue = "8080" } }
                    ]));

                for (var serverSpanIndex = 0; serverSpanIndex < TelemetryFlowServerSpansPerCall; serverSpanIndex++)
                {
                    spansByResource[destinationResourceIndex].Add(CreateSpan(
                        traceId,
                        spanId: $"flow-{traceIndex:0000}-server-{callIndex:0000}-{serverSpanIndex:00}",
                        parentSpanId: sourceSpanId,
                        name: $"service-{destinationResourceIndex} handler",
                        OtlpProtoSpan.Types.SpanKind.Server,
                        spanStartTime.AddTicks(serverSpanIndex + 1),
                        spanStartTime.AddMilliseconds(8)));
                }
            }
        }

        var resourceSpans = new RepeatedField<ResourceSpans>();
        for (var i = 0; i < spansByResource.Length; i++)
        {
            resourceSpans.Add(new ResourceSpans
            {
                Resource = CreateResource($"service-{i}", $"service-{i}-instance"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = new InstrumentationScope { Name = "TelemetryFlowBenchmarkScope" },
                        Spans = { spansByResource[i] }
                    }
                }
            });
        }

        return resourceSpans;
    }

    private static OtlpProtoSpan CreateSpan(
        string traceId,
        string spanId,
        string? parentSpanId,
        string name,
        OtlpProtoSpan.Types.SpanKind kind,
        DateTime startTime,
        DateTime endTime,
        IEnumerable<KeyValue>? attributes = null)
    {
        var span = new OtlpProtoSpan
        {
            TraceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes(traceId)),
            SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes(spanId)),
            ParentSpanId = parentSpanId is null ? ByteString.Empty : ByteString.CopyFrom(Encoding.UTF8.GetBytes(parentSpanId)),
            Name = name,
            Kind = kind,
            StartTimeUnixNano = DateTimeToUnixNanoseconds(startTime),
            EndTimeUnixNano = DateTimeToUnixNanoseconds(endTime)
        };

        if (attributes is not null)
        {
            span.Attributes.Add(attributes);
        }

        return span;
    }

    private static Resource CreateResource(string name = "benchmark-app", string instanceId = "benchmark-instance")
    {
        return new Resource
        {
            Attributes =
            {
                new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = name } },
                new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = instanceId } }
            }
        };
    }

    private static ulong DateTimeToUnixNanoseconds(DateTime dateTime)
    {
        var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timeSinceEpoch = dateTime.ToUniversalTime() - unixEpoch;

        return (ulong)timeSinceEpoch.Ticks * 100;
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithInvocationCount(16)
                .WithUnrollFactor(1));

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}
