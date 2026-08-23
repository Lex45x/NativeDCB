using System.Reflection;

using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NativeDCB.Server.Observability;

internal static class NativeDcbObservabilityExtensions
{
    public static void AddNativeDcbObservability(this WebApplicationBuilder builder)
    {
        bool disabled = string.Equals(
            builder.Configuration["OTEL_SDK_DISABLED"], "true", StringComparison.OrdinalIgnoreCase);
        if (disabled)
        {
            return;
        }

        bool export = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        OpenTelemetry.OpenTelemetryBuilder telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                builder.Configuration["OTEL_SERVICE_NAME"] ?? "NativeDCB.Server",
                serviceVersion: version));
        telemetry.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation()
                .AddSource("NativeDCB.Server", "NativeDCB.Actors", "NativeDCB.Engine");
            if (export)
            {
                tracing.AddOtlpExporter();
            }
        });
        telemetry.WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("NativeDCB.Server", "NativeDCB.Actors", "NativeDCB.Engine");
            if (export)
            {
                metrics.AddOtlpExporter();
            }
        });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            if (export)
            {
                logging.AddOtlpExporter();
            }
        });
    }
}