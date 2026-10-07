using System.Net;
using System.Text;
using System.Text.Json;
using EventPipeline.Core.Entities;
using EventPipeline.Web.Features.BrightData;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

/// <summary>
/// BrightDataService with the polling waits shrunk to nothing.
/// </summary>
public sealed class TestableBrightDataService : BrightDataService
{
    public TestableBrightDataService(IHttpClientFactory factory)
        : base(factory, NullLogger<BrightDataService>.Instance)
    {
    }

    protected override TimeSpan InitialDelay => TimeSpan.Zero;

    protected override TimeSpan PollInterval => TimeSpan.Zero;

    protected override TimeSpan PollDeadline => TimeSpan.FromSeconds(5);
}

public class BrightDataServiceTests
{
    private static BrightDataOptions Options => new("https://api.brightdata.com", "gd_test", PostsPerAccount: 4);

    private static IgAccount Account(string username) => new() { Username = username, ProfileUrl = $"https://www.instagram.com/{username}/" };

    [Fact]
    public async Task Fetch_TriggersPollsAndDownloadsWithBearerAuth()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.brightdata.com/datasets/v3/trigger?dataset_id=gd_test&notify=false&include_errors=true&custom_output_fields=posts%2Caccount", request.RequestUri!.ToString());
            Assert.Equal("Bearer token-123", request.Headers.Authorization!.ToString());
            Assert.Contains("limit_per_input", body);
            Assert.Contains("https://www.instagram.com/juevescong/", body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"snapshot_id\":\"snap-1\"}", Encoding.UTF8, "application/json")
            });
        });
        handler.Enqueue(HttpStatusCode.OK, "{\"status\":\"running\"}");
        handler.Enqueue(HttpStatusCode.OK, "{\"status\":\"ready\"}");
        handler.Enqueue(HttpStatusCode.OK, JsonSerializer.Serialize(new[]
        {
            new BrightDataAccountSnapshot
            {
                Account = "juevescong",
                Posts = new List<BrightDataPost>
                {
                    new() { Id = "p1", Caption = "Concierto", Url = "https://instagram.com/p/p1", ImageUrl = "https://cdn/p1.jpg", Datetime = new DateTime(2026, 9, 1) },
                    new() { Id = "p2", Caption = "Otro", Url = "https://instagram.com/p/p2" },
                    new() { Id = "p3", Caption = "Tercero", Url = "https://instagram.com/p/p3" },
                    new() { Id = "p4", Caption = "Cuarto", Url = "https://instagram.com/p/p4" },
                    new() { Id = "p5", Caption = "Quinto (fuera)", Url = "https://instagram.com/p/p5" },
                }
            }
        }));

        var service = new TestableBrightDataService(new StubHttpClientFactory(handler));
        var result = await service.FetchRecentPostsAsync(new[] { Account("juevescong") }, Options, "token-123");

        Assert.Empty(result.AccountErrors);
        Assert.Equal(4, result.Posts.Count); // first N posts only
        Assert.Equal("p1", result.Posts[0].PostId);
        Assert.Equal("juevescong", result.Posts[0].Account);
        Assert.Equal("https://instagram.com/p/p1", result.Posts[0].Url);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains(handler.Requests, r => r.RequestUri!.ToString().Contains("/progress/snap-1"));
        Assert.Contains(handler.Requests, r => r.RequestUri!.ToString().Contains("/snapshot/snap-1?format=json"));
    }

    [Fact]
    public async Task Fetch_StopsPollingWhenProgressIsNotRunning()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"snapshot_id\":\"snap-2\"}");
        handler.Enqueue(HttpStatusCode.OK, "{\"progress\":100}");
        handler.Enqueue(HttpStatusCode.OK, "[]");

        var service = new TestableBrightDataService(new StubHttpClientFactory(handler));
        var result = await service.FetchRecentPostsAsync(new[] { Account("juevescong") }, Options, "token");

        Assert.Empty(result.AccountErrors);
        Assert.Equal(3, handler.Requests.Count); // trigger, single progress poll, download
    }

    [Fact]
    public async Task Fetch_IsolatesErrorsPerAccount()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"snapshot_id\":\"snap-ok\"}");
        handler.Enqueue(HttpStatusCode.OK, "{\"status\":\"ready\"}");
        handler.Enqueue(HttpStatusCode.OK, JsonSerializer.Serialize(new[]
        {
            new BrightDataAccountSnapshot
            {
                Account = "buena",
                Posts = new List<BrightDataPost> { new() { Id = "p1", Caption = "Bien", Url = "https://instagram.com/p/p1" } }
            }
        }));
        handler.Enqueue(HttpStatusCode.BadRequest, "{\"error\":\"profile not found\"}");

        var service = new TestableBrightDataService(new StubHttpClientFactory(handler));
        var result = await service.FetchRecentPostsAsync(
            new[] { Account("buena"), Account("mala") }, Options, "token");

        Assert.Single(result.Posts);
        Assert.Equal("buena", result.Posts[0].Account);
        Assert.True(result.AccountErrors.ContainsKey("mala"));
    }

    [Fact]
    public async Task Fetch_MapsTheRealLowercaseBrightDataJson()
    {
        // Forma real de la respuesta de Bright Data (vista en el scrape de producción):
        // un array con un objeto cuyo campo es "posts" en minúsculas, sin "account".
        const string realBody = """
            [
              {
                "posts": [
                  {
                    "caption": "Cada jueves la misma fórmula",
                    "datetime": "2026-10-05T20:00:00",
                    "url": "https://www.instagram.com/p/abc123/",
                    "id": "abc123",
                    "image_url": "https://cdn.example.com/abc123.jpg"
                  },
                  {
                    "caption": "Otro post",
                    "datetime": "2026-10-06T21:00:00",
                    "url": "https://www.instagram.com/p/def456/",
                    "id": "def456"
                  }
                ]
              }
            ]
            """;

        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"snapshot_id\":\"snap-real\"}");
        handler.Enqueue(HttpStatusCode.OK, "{\"status\":\"ready\"}");
        handler.Enqueue(HttpStatusCode.OK, realBody);

        var service = new TestableBrightDataService(new StubHttpClientFactory(handler));
        var result = await service.FetchRecentPostsAsync(new[] { Account("juevescong") }, Options, "token");

        Assert.Empty(result.AccountErrors);
        Assert.Equal(2, result.Posts.Count);
        Assert.Equal("abc123", result.Posts[0].PostId);
        Assert.Equal("Cada jueves la misma fórmula", result.Posts[0].Caption);
        Assert.Equal("https://www.instagram.com/p/abc123/", result.Posts[0].Url);
        Assert.Equal("https://cdn.example.com/abc123.jpg", result.Posts[0].ImageUrl);
        Assert.Equal("juevescong", result.Posts[0].Account); // se asigna de la cuenta pedida
    }

    [Fact]
    public async Task Fetch_ToleratesSingleObjectSnapshotShape()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"snapshot_id\":\"snap-3\"}");
        handler.Enqueue(HttpStatusCode.OK, "{\"status\":\"ready\"}");
        handler.Enqueue(HttpStatusCode.OK, JsonSerializer.Serialize(new BrightDataAccountSnapshot
        {
            Account = "juevescong",
            Posts = new List<BrightDataPost> { new() { Id = "p1", Caption = "Solo", Url = "https://instagram.com/p/p1" } }
        }));

        var service = new TestableBrightDataService(new StubHttpClientFactory(handler));
        var result = await service.FetchRecentPostsAsync(new[] { Account("juevescong") }, Options, "token");

        Assert.Single(result.Posts);
        Assert.Equal("p1", result.Posts[0].PostId);
    }
}
