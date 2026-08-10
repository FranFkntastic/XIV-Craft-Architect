using System.Diagnostics;

namespace FFXIV_Craft_Architect.Web.Services.Diagnostics;

public sealed class DiagnosticRequestHandler(
    ClientRequestLog requestLog,
    ILogger<DiagnosticRequestHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var method = request.Method.Method;
        var url = SanitizeUrl(request.RequestUri);

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            stopwatch.Stop();
            requestLog.Add(new ClientRequestLogEntry(
                startedAt,
                method,
                url,
                $"{(int)response.StatusCode} {response.StatusCode}",
                stopwatch.ElapsedMilliseconds));

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "HTTP request {Method} {Url} returned {StatusCode} in {DurationMilliseconds} ms",
                    method,
                    url,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }

            return response;
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            RecordFailure(startedAt, method, url, exception, stopwatch.ElapsedMilliseconds);
            throw new HttpRequestException(
                $"{method} {url} failed: {exception.Message}",
                exception,
                exception.StatusCode);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            RecordFailure(startedAt, method, url, exception, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private void RecordFailure(
        DateTimeOffset startedAt,
        string method,
        string url,
        Exception exception,
        long durationMilliseconds)
    {
        requestLog.Add(new ClientRequestLogEntry(
            startedAt,
            method,
            url,
            exception.GetType().Name,
            durationMilliseconds));
        logger.LogError(
            exception,
            "HTTP request {Method} {Url} failed with {ExceptionType} in {DurationMilliseconds} ms",
            method,
            url,
            exception.GetType().Name,
            durationMilliseconds);
    }

    private static string SanitizeUrl(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return "<unknown>";
        }

        if (!requestUri.IsAbsoluteUri)
        {
            return requestUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        }

        return requestUri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped);
    }
}
