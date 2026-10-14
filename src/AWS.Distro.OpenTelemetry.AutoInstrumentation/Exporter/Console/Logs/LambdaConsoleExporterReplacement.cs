// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Console.Logs;

/// <summary>
/// Replaces the selected console exporter while keeping the SDK's simple processor.
/// </summary>
internal sealed class LambdaConsoleExporterReplacement : IResourceDetector
{
    private static readonly Type? BuilderType = typeof(Sdk).Assembly.GetType("OpenTelemetry.Logs.LoggerProviderBuilderSdk");
    private static readonly PropertyInfo? ServiceProviderProperty = typeof(ResourceBuilder).GetProperty("ServiceProvider", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? ResourceBuilderProperty = BuilderType?.GetProperty("ResourceBuilder");
    private static readonly PropertyInfo? ProcessorsProperty = BuilderType?.GetProperty("Processors");
    private static readonly FieldInfo? ExporterField = typeof(BaseExportProcessor<LogRecord>).GetField("exporter", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ConsoleOptionsField = typeof(ConsoleExporter<LogRecord>).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic);
    private static int warningReported;

    private readonly ResourceBuilder builder;
    private readonly Action<string>? reportWarning;

    private LambdaConsoleExporterReplacement(ResourceBuilder builder, Action<string>? reportWarning)
    {
        this.builder = builder;
        this.reportWarning = reportWarning;
    }

    /// <inheritdoc/>
    public Resource Detect()
    {
        try
        {
            if (BuilderType == null || ServiceProviderProperty == null || ResourceBuilderProperty == null || ProcessorsProperty == null || ExporterField == null || ConsoleOptionsField == null)
            {
                this.Warn("The OpenTelemetry SDK layout is unsupported; retaining its console log exporter.");
                return Resource.Empty;
            }

            if (ServiceProviderProperty.GetValue(this.builder) is not IServiceProvider services
                || services.GetService(BuilderType) is not { } state
                || !ReferenceEquals(ResourceBuilderProperty.GetValue(state), this.builder)
                || ProcessorsProperty.GetValue(state) is not IEnumerable<BaseProcessor<LogRecord>> processors)
            {
                return Resource.Empty;
            }

            // SDK 1.16 builds resources after creating processors, before attaching them.
            foreach (var processor in processors)
            {
                if (processor.GetType() != typeof(SimpleLogRecordExportProcessor)
                    || processor.ParentProvider != null
                    || ExporterField.GetValue(processor) is not ConsoleLogRecordExporter exporter
                    || exporter.GetType() != typeof(ConsoleLogRecordExporter)
                    || exporter.ParentProvider != null
                    || ConsoleOptionsField.GetValue(exporter) is not ConsoleExporterOptions options)
                {
                    continue;
                }

                var replacement = new CompactConsoleLogRecordExporter(new TargetsWriter(options));
                try
                {
                    ExporterField.SetValue(processor, replacement);
                }
                catch
                {
                    replacement.Dispose();
                    throw;
                }

                // The unchanged processor will attach and own the replacement exporter.
                exporter.Dispose();
            }
        }
        catch (Exception exception)
        {
            this.Warn($"Lambda console exporter replacement failed ({exception.GetType().Name}).");
        }

        return Resource.Empty;
    }

    internal static void Register(ResourceBuilder builder, Action<string>? reportWarning = null)
    {
        // A plain detector also permits resource builds outside provider construction.
        builder.AddDetector(new LambdaConsoleExporterReplacement(builder, reportWarning));
    }

    private void Warn(string message)
    {
        if (this.reportWarning != null && Interlocked.Exchange(ref warningReported, 1) == 0)
        {
            try
            {
                this.reportWarning(message);
            }
            catch
            {
                // Reporting a replacement failure must not abort provider construction.
            }
        }
    }

    private sealed class TargetsWriter : TextWriter
    {
        private readonly ConsoleExporterOptions options;

        public TargetsWriter(ConsoleExporterOptions options)
        {
            this.options = options;
        }

        public override Encoding Encoding => System.Console.Out.Encoding;

        public override void WriteLine(string? value)
        {
            if ((this.options.Targets & ConsoleExporterOutputTargets.Console) != 0)
            {
                System.Console.Out.WriteLine(value);
            }

            if ((this.options.Targets & ConsoleExporterOutputTargets.Debug) != 0)
            {
                Trace.WriteLine(value);
            }
        }

        public override void Flush()
        {
            if ((this.options.Targets & ConsoleExporterOutputTargets.Console) != 0)
            {
                System.Console.Out.Flush();
            }
        }
    }
}
