// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Console.Logs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Console.Logs;

[Collection("Lambda console output")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class LambdaConsoleExporterReplacementTests
{
    [Fact]
    public void KeepsTheSdkProcessorAndItsProviderLifecycle()
    {
        using var output = new ConsoleCapture();
        using var original = new ConsoleLogRecordExporter(new ConsoleExporterOptions());
        var processor = new SimpleLogRecordExportProcessor(original);
        var warnings = new List<string>();
        var resource = ResourceBuilder.CreateEmpty().AddService("replacement-test");
        LambdaConsoleExporterReplacement.Register(resource, warnings.Add);
        using var services = CreateServices(resource, options => options.AddProcessor(processor));
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("replacement-scope");

        logger.LogInformation("replacement message");

        var provider = services.GetRequiredService<LoggerProvider>();
        Assert.Same(provider, processor.ParentProvider);
        Assert.Null(original.ParentProvider);
        Assert.True(provider.ForceFlush(5000));
        Assert.Empty(warnings);
        using var record = ParseSingleRecord(output.Output);
        Assert.Equal("replacement message", record.RootElement.GetProperty("body").GetString());
        Assert.Equal("replacement-scope", record.RootElement.GetProperty("scope").GetProperty("name").GetString());
        Assert.Equal("replacement-test", record.RootElement.GetProperty("resource").GetProperty("attributes").GetProperty("service.name").GetString());

        services.Dispose();
        Assert.False(processor.Shutdown(5000));
    }

    [Fact]
    public void EarlyAndRepeatedResourceBuildsKeepTheActiveExporter()
    {
        using var output = new ConsoleCapture();
        var warnings = new List<string>();
        var resource = ResourceBuilder.CreateEmpty().AddService("repeated-build");
        LambdaConsoleExporterReplacement.Register(resource, warnings.Add);
        LambdaConsoleExporterReplacement.Register(resource, warnings.Add);
        resource.Build();
        resource.Build();
        using var services = CreateServices(resource, options => options.AddConsoleExporter());
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("repeat");

        logger.LogInformation("first message");
        resource.Build();
        resource.Build();
        logger.LogInformation("second message");

        var records = Lines(output.Output);
        Assert.Equal(2, records.Length);
        for (var index = 0; index < records.Length; index++)
        {
            using var record = JsonDocument.Parse(records[index]);
            Assert.Equal(index == 0 ? "first message" : "second message", record.RootElement.GetProperty("body").GetString());
            Assert.Equal("repeated-build", record.RootElement.GetProperty("resource").GetProperty("attributes").GetProperty("service.name").GetString());
        }

        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData((ConsoleExporterOutputTargets)0)]
    [InlineData(ConsoleExporterOutputTargets.Console)]
    [InlineData(ConsoleExporterOutputTargets.Debug)]
    [InlineData(ConsoleExporterOutputTargets.Console | ConsoleExporterOutputTargets.Debug)]
    public void PreservesConfiguredOutputTargets(ConsoleExporterOutputTargets targets)
    {
        using var output = new ConsoleCapture();
        using var debugOutput = new StringWriter();
        using var listener = new TextWriterTraceListener(debugOutput);
        Trace.Listeners.Add(listener);
        try
        {
            var resource = CreateResource();
            using var services = CreateServices(resource, options => options.AddConsoleExporter(console => console.Targets = targets));
            services.GetRequiredService<ILoggerFactory>().CreateLogger("targets").LogInformation("target message");

            if (targets.HasFlag(ConsoleExporterOutputTargets.Console))
            {
                using var record = ParseSingleRecord(output.Output);
                Assert.Equal("target message", record.RootElement.GetProperty("body").GetString());
            }
            else
            {
                Assert.Empty(output.Output);
            }

            if (targets.HasFlag(ConsoleExporterOutputTargets.Debug))
            {
                using var record = ParseSingleRecord(debugOutput.ToString());
                Assert.Equal("target message", record.RootElement.GetProperty("body").GetString());
            }
            else
            {
                Assert.Empty(debugOutput.ToString());
            }
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void UsesConsoleOutAtExportTime()
    {
        using var output = new ConsoleCapture();
        using var laterOutput = new StringWriter();
        using var services = CreateServices(CreateResource(), options => options.AddConsoleExporter());
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("redirect");

        System.Console.SetOut(laterOutput);
        logger.LogInformation("redirected message");

        Assert.Empty(output.Output);
        using var record = ParseSingleRecord(laterOutput.ToString());
        Assert.Equal("redirected message", record.RootElement.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PreservesNoneAndOtlpDelivery(bool includeConsole, bool includeOtlp)
    {
        using var output = new ConsoleCapture();
        using var transport = new RecordingHttpHandler();
        using var services = CreateServices(CreateResource(), options =>
        {
            if (includeConsole)
            {
                options.AddConsoleExporter();
            }

            if (includeOtlp)
            {
                options.AddOtlpExporter((exporter, processor) =>
                {
                    exporter.Endpoint = new Uri("http://localhost:4318/v1/logs");
                    exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                    exporter.HttpClientFactory = () => new HttpClient(transport, disposeHandler: false);
                    processor.ExportProcessorType = ExportProcessorType.Simple;
                });
            }
        });
        services.GetRequiredService<ILoggerFactory>().CreateLogger("selection").LogInformation("selected message");
        Assert.True(services.GetRequiredService<LoggerProvider>().ForceFlush(5000));

        Assert.Equal(includeOtlp ? 1 : 0, transport.RequestCount);
        if (includeConsole)
        {
            using var record = ParseSingleRecord(output.Output);
            Assert.Equal("selected message", record.RootElement.GetProperty("body").GetString());
        }
        else
        {
            Assert.Empty(output.Output);
        }
    }

    [Fact]
    public void LeavesBatchConsoleProcessorsUntouched()
    {
        using var output = new ConsoleCapture();
        using var original = new ConsoleLogRecordExporter(new ConsoleExporterOptions());
        var processor = new BatchLogRecordExportProcessor(original, maxQueueSize: 17, scheduledDelayMilliseconds: 60000, exporterTimeoutMilliseconds: 1234, maxExportBatchSize: 8);
        using var services = CreateServices(CreateResource(), options => options.AddProcessor(processor));
        services.GetRequiredService<ILoggerFactory>().CreateLogger("batch").LogInformation("batch message");
        var provider = services.GetRequiredService<LoggerProvider>();

        Assert.True(provider.ForceFlush(5000));
        Assert.Same(provider, processor.ParentProvider);
        Assert.Same(provider, original.ParentProvider);
        Assert.Contains("LogRecord.Body:", output.Output);
        Assert.Contains("batch message", output.Output);
        Assert.DoesNotContain("\"severityNumber\"", output.Output);
    }

    [Fact]
    public void LeavesCustomConsoleExportersUntouched()
    {
        using var output = new ConsoleCapture();
        var original = new CustomConsoleExporter();
        var processor = new SimpleLogRecordExportProcessor(original);
        using var services = CreateServices(CreateResource(), options => options.AddProcessor(processor));
        services.GetRequiredService<ILoggerFactory>().CreateLogger("custom").LogInformation("custom message");

        Assert.Same(services.GetRequiredService<LoggerProvider>(), original.ParentProvider);
        Assert.Contains("LogRecord.Body:", output.Output);
        Assert.DoesNotContain("\"severityNumber\"", output.Output);
        services.Dispose();
        Assert.Equal(1, original.DisposeCount);
    }

    [Fact]
    public void LeavesAnAlreadyAttachedConsoleExporterUntouched()
    {
        using var output = new ConsoleCapture();
        var original = new ConsoleLogRecordExporter(new ConsoleExporterOptions());
        using var firstServices = CreateServices(
            ResourceBuilder.CreateEmpty().AddService("existing-provider"),
            options => options.AddProcessor(new SimpleLogRecordExportProcessor(original)));
        var firstLogger = firstServices.GetRequiredService<ILoggerFactory>().CreateLogger("existing");
        firstLogger.LogInformation("existing message");
        Assert.NotNull(original.ParentProvider);
        using var secondServices = CreateServices(
            CreateResource(),
            options => options.AddProcessor(new SimpleLogRecordExportProcessor(original)));

        secondServices.GetRequiredService<ILoggerFactory>().CreateLogger("reuse").LogInformation("reused message");
        firstLogger.LogInformation("still existing");

        Assert.Contains("existing message", output.Output);
        Assert.Contains("reused message", output.Output);
        Assert.Contains("still existing", output.Output);
        Assert.DoesNotContain("\"severityNumber\"", output.Output);
    }

    [Fact]
    public void KeepsOtherExportersAndTheirLifecycle()
    {
        using var output = new ConsoleCapture();
        var other = new TrackingExporter();
        using var services = CreateServices(CreateResource(), options =>
        {
            options.AddConsoleExporter();
            options.AddProcessor(new SimpleLogRecordExportProcessor(other));
        });
        services.GetRequiredService<ILoggerFactory>().CreateLogger("mixed").LogInformation("mixed message");

        Assert.Equal(1, other.RecordCount);
        Assert.True(services.GetRequiredService<LoggerProvider>().ForceFlush(5000));
        Assert.Equal(1, other.FlushCount);
        using var record = ParseSingleRecord(output.Output);
        Assert.Equal("mixed message", record.RootElement.GetProperty("body").GetString());
        services.Dispose();
        Assert.Equal(1, other.ShutdownCount);
        Assert.Equal(1, other.DisposeCount);
    }

    [Fact]
    public void ConcurrentProvidersKeepTheirOwnResources()
    {
        using var output = new ConsoleCapture();
        Parallel.For(0, 16, index =>
        {
            var resource = ResourceBuilder.CreateEmpty().AddService($"provider-{index}");
            LambdaConsoleExporterReplacement.Register(resource);
            using var services = CreateServices(resource, options => options.AddConsoleExporter());
            services.GetRequiredService<ILoggerFactory>().CreateLogger("parallel").LogInformation("message {Index}", index);
        });

        var records = Lines(output.Output);
        Assert.Equal(16, records.Length);
        foreach (var line in records)
        {
            using var record = JsonDocument.Parse(line);
            var index = record.RootElement.GetProperty("attributes").GetProperty("Index").GetInt32();
            Assert.Equal($"provider-{index}", record.RootElement.GetProperty("resource").GetProperty("attributes").GetProperty("service.name").GetString());
        }
    }

    private static ResourceBuilder CreateResource()
    {
        var resource = ResourceBuilder.CreateEmpty().AddService("console-tests");
        LambdaConsoleExporterReplacement.Register(resource);
        return resource;
    }

    private static ServiceProvider CreateServices(ResourceBuilder resource, Action<OpenTelemetryLoggerOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resource);
            configure(options);
        }));
        return services.BuildServiceProvider();
    }

    private static JsonDocument ParseSingleRecord(string output) => JsonDocument.Parse(Assert.Single(Lines(output)));

    private static string[] Lines(string output) => output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter originalOutput = System.Console.Out;
        private readonly StringWriter output = new();

        public ConsoleCapture()
        {
            System.Console.SetOut(this.output);
        }

        public string Output => this.output.ToString();

        public void Dispose()
        {
            System.Console.SetOut(this.originalOutput);
            this.output.Dispose();
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return this.RecordRequest();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(this.RecordRequest());
        }

        private HttpResponseMessage RecordRequest()
        {
            this.RequestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        }
    }

    private sealed class CustomConsoleExporter : ConsoleLogRecordExporter
    {
        public CustomConsoleExporter()
            : base(new ConsoleExporterOptions())
        {
        }

        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingExporter : BaseExporter<LogRecord>
    {
        public long RecordCount { get; private set; }

        public int FlushCount { get; private set; }

        public int ShutdownCount { get; private set; }

        public int DisposeCount { get; private set; }

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            this.RecordCount += batch.Count;
            return ExportResult.Success;
        }

        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
            this.FlushCount++;
            return true;
        }

        protected override bool OnShutdown(int timeoutMilliseconds)
        {
            this.ShutdownCount++;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Serializes tests that capture process-wide stdout and trace listeners.
/// </summary>
[CollectionDefinition("Lambda console output", DisableParallelization = true)]
public class LambdaConsoleOutputCollection
{
}
