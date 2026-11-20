using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using NexusMods.Sdk;
using Polly;

namespace NexusMods.Networking.HttpDownloader;

public static class Services
{
    /// <summary>
    /// Add the default HTTP downloader services
    /// </summary>
    public static IServiceCollection AddHttpDownloader(this IServiceCollection services)
    {
        return services
            .AddSingleton<HttpClient>(_ =>
            {
                var client = BuildClient();
                return client;
            })
            // Afterburn: a separate client for third-party links that only reaches the public internet.
            .AddSingleton<PublicWebHttpClient>(_ => new PublicWebHttpClient(BuildClient(publicWebOnly: true)));
    }

    private static HttpClient BuildClient(bool publicWebOnly = false)
    {
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(3),
                UseJitter = true,
            })
            .Build();

        HttpMessageHandler innerHandler = publicWebOnly
            ? new PublicRedirectHandler { InnerHandler = PublicWebHttpClient.CreateSocketsHandler() }
            : new SocketsHttpHandler();

        HttpMessageHandler handler = new ResilienceHandler(pipeline)
        {
            InnerHandler = innerHandler,
        };

        var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };

        client.DefaultRequestHeaders.UserAgent.Add(ApplicationConstants.UserAgent);

        return client;
    }
}
