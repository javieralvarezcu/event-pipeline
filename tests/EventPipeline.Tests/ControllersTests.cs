using EventPipeline.Core.Common;
using EventPipeline.Core.Contracts;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Events;
using EventPipeline.Core.Features.Muxo;
using EventPipeline.Core.Features.Posts;
using EventPipeline.Api.Features.Calendar;
using EventPipeline.Api.Features.Events;
using EventPipeline.Api.Features.Muxo;
using EventPipeline.Api.Features.Posts;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace EventPipeline.Tests;

public class ControllersTests
{
    private static DefaultHttpContext HttpContextWithKey(string? key = "test-key")
    {
        var context = new DefaultHttpContext();
        if (key != null)
            context.Request.Headers["X-DeepSeek-API-Key"] = key;
        return context;
    }

    private static PostsController PostsController(
        Func<List<InstagramPost>, string, CancellationToken, Task<RecognitionResponse>>? recognize = null)
    {
        var controller = new PostsController(new FakeRecognitionService(recognize));
        controller.ControllerContext = new ControllerContext { HttpContext = HttpContextWithKey() };
        return controller;
    }

    // --- /api/posts/recognize ---

    [Fact]
    public async Task Recognize_WithEmptyPosts_ReturnsBadRequest()
    {
        var result = await PostsController().Recognize(new List<InstagramPost>());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void RequireDeepSeekKey_WithoutHeader_Returns401()
    {
        var attribute = new RequireDeepSeekKeyAttribute();
        var context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new PostsController(new FakeRecognitionService()));

        attribute.OnActionExecuting(context);

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public void RequireDeepSeekKey_WithHeader_DoesNothing()
    {
        var attribute = new RequireDeepSeekKeyAttribute();
        var context = new ActionExecutingContext(
            new ActionContext(HttpContextWithKey(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new PostsController(new FakeRecognitionService()));

        attribute.OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void LlmEndpoints_AreDecoratedWithTheKeyRequirement()
    {
        AssertKeyRequired<EventPipeline.Api.Features.Posts.PostsController>(nameof(EventPipeline.Api.Features.Posts.PostsController.Recognize));
        AssertKeyRequired<EventPipeline.Api.Features.Events.EventsController>(nameof(EventPipeline.Api.Features.Events.EventsController.CleanupMonth));
        AssertKeyRequired<EventPipeline.Api.Features.Events.EventsController>(nameof(EventPipeline.Api.Features.Events.EventsController.UpdateEvent));
        AssertKeyRequired<EventPipeline.Api.Features.Events.EventsController>(nameof(EventPipeline.Api.Features.Events.EventsController.DeleteEvent));
        AssertKeyRequired<EventPipeline.Api.Features.Muxo.MuxoController>(nameof(EventPipeline.Api.Features.Muxo.MuxoController.CrossCheck));
    }

    [Fact]
    public void NonLlmEndpoints_DoNotRequireTheKey()
    {
        AssertKeyNotRequired<EventPipeline.Api.Features.Muxo.MuxoController>(nameof(EventPipeline.Api.Features.Muxo.MuxoController.Sync));
        AssertKeyNotRequired<EventPipeline.Api.Features.Muxo.MuxoController>(nameof(EventPipeline.Api.Features.Muxo.MuxoController.GetMuxoEvents));
        AssertKeyNotRequired<EventPipeline.Api.Features.Events.EventsController>(nameof(EventPipeline.Api.Features.Events.EventsController.GetEvents));
        AssertKeyNotRequired<EventPipeline.Api.Features.Events.EventsController>(nameof(EventPipeline.Api.Features.Events.EventsController.GetEvent));
    }

    private static void AssertKeyRequired<TController>(string actionName)
        => Assert.Contains(typeof(TController).GetMethod(actionName)!.GetCustomAttributes(true),
            a => a is RequireDeepSeekKeyAttribute);

    private static void AssertKeyNotRequired<TController>(string actionName)
        => Assert.DoesNotContain(typeof(TController).GetMethod(actionName)!.GetCustomAttributes(true),
            a => a is RequireDeepSeekKeyAttribute);

    [Fact]
    public async Task Recognize_WithInvalidRange_ReturnsBadRequest()
    {
        var result = await PostsController().Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") },
            dateFrom: new DateTime(2026, 9, 30), dateTo: new DateTime(2026, 9, 1));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Recognize_WithKeyAndPosts_ReturnsOkAndPassesThemThrough()
    {
        var posts = new List<InstagramPost> { TestData.CreatePost("p1", "cartel") };
        var controller = PostsController((received, key, _) =>
        {
            Assert.Equal(posts, received);
            Assert.Equal("test-key", key);
            return Task.FromResult(new RecognitionResponse { TotalPosts = 1, EventsFound = 0, Events = new() });
        });

        var result = await controller.Recognize(posts);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecognitionResponse>(ok.Value);
        Assert.Equal(1, response.TotalPosts);
    }

    // --- /api/events ---

    private static EventsController EventsController(
        FakeEventQueryService? query = null,
        FakeEventCrudService? crud = null,
        FakeCleanupService? cleanup = null)
    {
        var controller = new EventsController(
            query ?? new FakeEventQueryService(),
            crud ?? new FakeEventCrudService(),
            cleanup ?? new FakeCleanupService());
        controller.ControllerContext = new ControllerContext { HttpContext = HttpContextWithKey() };
        return controller;
    }

    [Fact]
    public async Task GetEvents_IsPublic_AndPassesTheRange()
    {
        var query = new FakeEventQueryService();
        var controller = EventsController(query: query);

        var result = await controller.GetEvents(
            dateFrom: new DateTime(2026, 9, 1), dateTo: new DateTime(2026, 9, 30));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new DateTime(2026, 9, 1), query.LastRange?.From);
        Assert.Equal(new DateTime(2026, 9, 30), query.LastRange?.To);
    }

    [Fact]
    public async Task GetEvents_WithInvalidRange_ReturnsBadRequest()
    {
        var result = await EventsController().GetEvents(
            dateFrom: new DateTime(2026, 9, 30), dateTo: new DateTime(2026, 9, 1));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetEvent_WithUnknownId_ReturnsNotFound()
    {
        var result = await EventsController(query: new FakeEventQueryService()).GetEvent("EVT-NOPE");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task CleanupMonth_WithInvalidMonth_ReturnsBadRequest()
    {
        var result = await EventsController().CleanupMonth(month: "septiembre");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CleanupMonth_WithValidMonth_PassesItToTheService()
    {
        var cleanup = new FakeCleanupService((year, month, key, _) =>
        {
            Assert.Equal(2026, year);
            Assert.Equal(9, month);
            Assert.Equal("test-key", key);
            return Task.FromResult(new CleanupResponse { Month = "2026-09" });
        });

        var result = await EventsController(cleanup: cleanup).CleanupMonth(month: "2026-09");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("2026-09", Assert.IsType<CleanupResponse>(ok.Value).Month);
    }

    // --- /api/muxo ---

    private static MuxoController MuxoController(
        FakeMuxoSyncService? sync = null,
        FakeCrossCheckService? crosscheck = null,
        FakeMuxoQueryService? query = null)
    {
        var controller = new MuxoController(
            sync ?? new FakeMuxoSyncService(),
            crosscheck ?? new FakeCrossCheckService(),
            query ?? new FakeMuxoQueryService());
        controller.ControllerContext = new ControllerContext { HttpContext = HttpContextWithKey() };
        return controller;
    }

    [Fact]
    public async Task Sync_IsPublic_AndClampsMonthsAhead()
    {
        var sync = new FakeMuxoSyncService((_, _) => Task.FromResult(new MuxoSyncResponse()));
        var controller = MuxoController(sync: sync);

        await controller.Sync(monthsAhead: 99);

        Assert.Equal(6, sync.LastMonthsAhead);
    }

    [Fact]
    public async Task CrossCheck_WithKey_ReturnsTheServiceResponse()
    {
        var crosscheck = new FakeCrossCheckService((key, _) =>
        {
            Assert.Equal("test-key", key);
            return Task.FromResult(new CrossCheckResponse { MatchesFound = 2 });
        });

        var result = await MuxoController(crosscheck: crosscheck).CrossCheck();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, Assert.IsType<CrossCheckResponse>(ok.Value).MatchesFound);
    }

    [Fact]
    public async Task GetMuxoEvents_IsPublic()
    {
        var result = await MuxoController().GetMuxoEvents();

        Assert.IsType<OkObjectResult>(result);
    }

    // --- Exception filter mapping ---

    [Fact]
    public void ApiExceptionFilter_MapsScrapeExceptionTo502()
    {
        var context = BuildExceptionContext(new ScrapeException("scrape roto"));

        new ApiExceptionFilter(NullLogger<ApiExceptionFilter>.Instance).OnException(context);

        Assert.True(context.ExceptionHandled);
        Assert.Equal(StatusCodes.Status502BadGateway, ((ObjectResult)context.Result!).StatusCode);
    }

    [Fact]
    public void ApiExceptionFilter_MapsHttpRequestExceptionTo502()
    {
        var context = BuildExceptionContext(new HttpRequestException("llm caído"));

        new ApiExceptionFilter(NullLogger<ApiExceptionFilter>.Instance).OnException(context);

        Assert.True(context.ExceptionHandled);
        Assert.Equal(StatusCodes.Status502BadGateway, ((ObjectResult)context.Result!).StatusCode);
    }

    [Fact]
    public void ApiExceptionFilter_MapsInvalidOperationExceptionTo500()
    {
        var context = BuildExceptionContext(new InvalidOperationException("respuesta rara"));

        new ApiExceptionFilter(NullLogger<ApiExceptionFilter>.Instance).OnException(context);

        Assert.True(context.ExceptionHandled);
        Assert.Equal(StatusCodes.Status500InternalServerError, ((ObjectResult)context.Result!).StatusCode);
    }

    private static ExceptionContext BuildExceptionContext(Exception exception)
        => new(new ActionContext(
            new DefaultHttpContext(),
            new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            new List<IFilterMetadata>())
        {
            Exception = exception
        };
}
