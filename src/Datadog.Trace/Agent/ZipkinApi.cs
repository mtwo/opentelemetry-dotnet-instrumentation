using System;
using System.Net;
using System.Threading.Tasks;
using Datadog.Trace.Configuration;
using Datadog.Trace.Logging;

namespace Datadog.Trace.Agent
{
    internal class ZipkinApi : IApi
    {
        private static readonly Vendors.Serilog.ILogger Log = DatadogLogging.For<ZipkinApi>();

        private readonly TracerSettings _settings;
        private Uri _tracesEndpoint;

        public ZipkinApi(TracerSettings settings)
        {
            Log.Debug("Creating new Zipkin Api");

            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _tracesEndpoint = _settings.AgentUri; // User needs to include the proper path.
        }

        public void SetBaseEndpoint(Uri baseEndpoint)
        {
            // TODO: Remove this method when it is also removed from the interface.
            _tracesEndpoint = new Uri(baseEndpoint, _tracesEndpoint.PathAndQuery);
        }

        public async Task<bool> SendTracesAsync(Span[][] traces)
        {
            if (traces == null || traces.Length == 0)
            {
                // Nothing to send, no ping for Zipkin.
                return true;
            }

            // retry up to 5 times with exponential back-off
            var retryLimit = 5;
            var retryCount = 1;
            var sleepDuration = 100; // in milliseconds

            while (true)
            {
                // TODO: Initially same code for Fx and Core.
                var request = WebRequest.CreateHttp(_tracesEndpoint);
                request.Method = "POST";
                request.ContentType = "application/json";

                using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                {
                    var serializer = new ZipkinSerializer();
                    serializer.Serialize(requestStream, traces, _settings);
                }

                Exception requestException = null;
                HttpStatusCode requestStatusCode = 0;
                try
                {
                    using var httpWebResponse = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);

                    // Zipkin specifies only "Accepted" as valid response, the code is more tolerant here.
                    // Following a criteria equivalent to HttpResponseMessage.EnsureSuccessStatusCode as
                    // done by the OpenTelemetry .NET SDK for their Zipkin exporter:
                    // See https://github.com/open-telemetry/opentelemetry-dotnet/blob/8cda9ef394a1b075fd156d73dace48e48f5b3c9b/src/OpenTelemetry.Exporter.Zipkin/ZipkinExporter.cs#L86
                    if (httpWebResponse.StatusCode >= HttpStatusCode.OK && httpWebResponse.StatusCode < HttpStatusCode.MultipleChoices)
                    {
                        return true;
                    }

                    requestStatusCode = httpWebResponse.StatusCode;
                    Log.Debug("HTTP error sending traces to {0}: {1}", _tracesEndpoint, httpWebResponse.StatusCode);
                }
                catch (Exception ex)
                {
                    requestException = ex;
                    Log.Debug("Exception sending traces to {0}: {1}", _tracesEndpoint, ex.Message);
                }

                if (retryCount >= retryLimit)
                {
                    if (requestException != null)
                    {
                        Log.Error("No more retries, dropping spans. Last exception sending traces to {0}: {1}", _tracesEndpoint, requestException.Message);
                    }
                    else
                    {
                        Log.Error("No more retries, dropping spans. Last HTTP error sending traces to {0}: {1}", _tracesEndpoint, requestStatusCode);
                    }

                    return false;
                }

                // retry
                await Task.Delay(sleepDuration).ConfigureAwait(false);
                retryCount++;
                sleepDuration *= 2;
            }
        }
    }
}
