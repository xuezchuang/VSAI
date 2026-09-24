using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderDiscoveryServiceTests
{
    [Fact]
    public async Task OpenAiCatalogParsesKnownMetadataWithoutInventingReasoningSupport()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, @"{
            'data': [{
                'id': 'model-a', 'display_name': 'Model A', 'context_window': 128000,
                'max_output_tokens': 4096, 'supports_images': false, 'supports_tools': true,
                'supported_reasoning_levels': ['low', 'high', 'invalid'], 'default_reasoning_level': 'high'
            }]
        }".Replace('\'', '"'))));
        var service = new CodexProviderDiscoveryService(client);

        var result = await service.FetchAsync(Provider("openai"), CancellationToken.None);

        Assert.Equal("success", result.Status);
        var model = Assert.Single(result.Models);
        Assert.Equal("Model A", model.DisplayName);
        Assert.Equal(128_000L, model.ContextWindow);
        Assert.Equal(false, model.SupportsImages);
        Assert.Equal(true, model.SupportsTools);
        Assert.Null(model.SupportsReasoning);
        Assert.Equal(new[] { "low", "high" }, model.ReasoningEfforts);
        Assert.Equal("high", model.DefaultReasoningEffort);
    }

    [Fact]
    public async Task AutoMidasUsesOnlyKnownSameOriginEndpointAndRichLevels()
    {
        var requests = new List<HttpRequestMessage>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request);
            return request.RequestUri!.AbsolutePath == "/v1/models"
                ? Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"old\",\"owned_by\":\"midas-gateway\"}]}")
                : Json(HttpStatusCode.OK, "{\"models\":[{\"id\":\"rich\",\"supports_image\":false,\"capabilities\":{\"tool_calling\":false,\"reasoning\":true},\"reasoning\":{\"default\":\"minimal\",\"levels\":[{\"request\":{\"reasoning_effort\":\"minimal\"}},{\"level\":\"enabled\"}]}}]}");
        }));
        var service = new CodexProviderDiscoveryService(client);

        var result = await service.FetchAsync(Provider("auto"), CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal(new[] { "/v1/models", "/api/models" }, requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.All(requests, request => Assert.Equal("Bearer", request.Headers.Authorization!.Scheme));
        var model = Assert.Single(result.Models);
        Assert.Equal("rich", model.Id);
        Assert.Equal(new[] { "minimal" }, model.ReasoningEfforts);
        Assert.Equal("minimal", model.DefaultReasoningEffort);
        Assert.Equal(true, model.SupportsReasoning);
        Assert.Equal(false, model.SupportsImages);
        Assert.Equal(false, model.SupportsTools);
    }

    [Fact]
    public async Task AuthenticationFailureIsSafeAndDoesNotExposeResponseBodyOrKey()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.Unauthorized, "secret response body")));
        var service = new CodexProviderDiscoveryService(client);
        var provider = Provider("openai");
        provider.ApiKey = "private-key";

        var result = await service.FetchAsync(provider, CancellationToken.None);

        Assert.Equal("auth-error", result.Status);
        Assert.Empty(result.Models);
        Assert.DoesNotContain("secret", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-key", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoMidasDoesNotExposeGenericModelsWhenRichCatalogIsRejected()
    {
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath == "/v1/models"
            ? Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"generic\",\"owned_by\":\"midas-gateway\"}]}")
            : Json(HttpStatusCode.Forbidden, "private gateway details")));

        var result = await new CodexProviderDiscoveryService(client).FetchAsync(Provider("auto"), CancellationToken.None);

        Assert.Equal("auth-error", result.Status);
        Assert.Empty(result.Models);
        Assert.DoesNotContain("private", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlainIdCatalogIsPartialBecauseCapabilitiesAreUnknown()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"plain\",\"owned_by\":\"compatible\"}]}")));

        var result = await new CodexProviderDiscoveryService(client).FetchAsync(Provider("openai"), CancellationToken.None);

        Assert.Equal("partial", result.Status);
        Assert.Equal("plain", Assert.Single(result.Models).Id);
    }

    [Fact]
    public async Task InvalidCatalogEntriesAreRejectedWithoutReplacingTheSnapshot()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"good\"},{\"id\":\"good\"},{\"id\":\"\"},\"unexpected\"]}")));
        var result = await new CodexProviderDiscoveryService(client).FetchAsync(Provider("openai"), CancellationToken.None);

        Assert.Equal("error", result.Status);
        Assert.Empty(result.Models);
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"first-page\"}],\"has_more\":true}")]
    [InlineData("{\"data\":[{\"id\":\"first-page\"}],\"next_cursor\":\"second-page\"}")]
    [InlineData("not json")]
    public async Task EmptyMalformedAndIncompleteSnapshotsNeverReplaceTheCache(string body)
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, body)));
        var result = await new CodexProviderDiscoveryService(client).FetchAsync(Provider("openai"), CancellationToken.None);
        Assert.Equal("error", result.Status);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task RedirectAndOversizedResponseAreNotTreatedAsModelCatalogs()
    {
        var calls = 0;
        using var redirectClient = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            var response = Json(HttpStatusCode.Redirect, "private response");
            response.Headers.Location = new Uri("https://other.example/models");
            return response;
        }));
        var redirected = await new CodexProviderDiscoveryService(redirectClient).FetchAsync(Provider("openai"), CancellationToken.None);
        Assert.Equal("error", redirected.Status);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("private", redirected.Error, StringComparison.Ordinal);
        using var oversizedClient = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, new string(' ', 2 * 1024 * 1024 + 1))));
        var oversized = await new CodexProviderDiscoveryService(oversizedClient).FetchAsync(Provider("openai"), CancellationToken.None);
        Assert.Equal("error", oversized.Status);
        Assert.Empty(oversized.Models);
    }

    [Fact]
    public async Task DisplayNameMustBeValidBeforeSnapshotIsAccepted()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"model\",\"display_name\":\"" + new string('a', 201) + "\"}]}")));
        var result = await new CodexProviderDiscoveryService(client).FetchAsync(Provider("openai"), CancellationToken.None);
        Assert.Equal("error", result.Status);
        Assert.Empty(result.Models);
    }

    private static CodexProviderConfiguration Provider(string source)
        => new()
        {
            BaseUrl = "https://gateway.example/v1",
            ApiKey = "test-key",
            Catalog = new CodexProviderCatalogConfiguration { Source = source }
        };

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> response;

        internal StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => this.response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var result = response(request);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
}
