using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class BroadcasTheNetClientTests
{
    private readonly Mock<HttpMessageHandler> _handler;
    private readonly Mock<IRateLimitService> _rateLimit;
    private readonly BroadcasTheNetClient _subject;
    private readonly Indexer _indexer;

    public BroadcasTheNetClientTests()
    {
        _handler = new Mock<HttpMessageHandler>();
        _rateLimit = new Mock<IRateLimitService>();

        _rateLimit
            .Setup(r => r.WaitAndPulseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(Task.CompletedTask);

        var httpClient = new HttpClient(_handler.Object);

        _subject = new BroadcasTheNetClient(
            httpClient,
            _rateLimit.Object,
            NullLogger<BroadcasTheNetClient>.Instance);

        _indexer = new Indexer
        {
            Id = 1,
            Name = "BroadcastheNet",
            Url = "https://api.broadcasthe.net",
            ApiKey = "abc"
        };
    }

    private void SetupHttpResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK, string mediaType = "application/json")
    {
        _handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, mediaType)
            });
    }

    private void SetupHttpResponse(HttpStatusCode statusCode)
    {
        _handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            });
    }

    private void VerifyRequest(string expectedUrl)
    {
        _handler
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Post &&
                    r.RequestUri!.ToString() == expectedUrl),
                ItExpr.IsAny<CancellationToken>());
    }

    private static string ReadFixture(string relativePath)
    {
        var dir = Path.GetDirectoryName(typeof(BroadcasTheNetClientTests).Assembly.Location)!;
        return File.ReadAllText(Path.Combine(dir, relativePath));
    }

    [Fact]
    public async Task should_parse_recent_feed_from_BroadcastheNet()
    {
        var recentFeed = ReadFixture("Services/Fixtures/BroadcastheNet/RecentFeed.json");
        SetupHttpResponse(recentFeed);

        var releases = await _subject.FetchRecentAsync(_indexer);

        releases.Should().HaveCount(2);

        var first = releases.First();
        first.Guid.Should().Be("BTN-123");
        first.Title.Should().Be("Jimmy.Kimmel.2014.09.15.Jane.Fonda.HDTV.x264-aAF");
        first.DownloadUrl.Should().Be("https://broadcasthe.net/torrents.php?action=download&id=123&authkey=123&torrent_pass=123");
        first.InfoUrl.Should().Be("https://broadcasthe.net/torrents.php?id=237457&torrentid=123");
        first.Indexer.Should().Be(_indexer.Name);
        first.PublishDate.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1410902133).UtcDateTime);
        first.Size.Should().Be(505099926);
        first.TorrentInfoHash.Should().Be("123");
        first.Seeders.Should().Be(40);
        first.Leechers.Should().Be(9);

        // Quality metadata from BTN fields
        first.Source.Should().Be("HDTV");
        first.Codec.Should().Be("x264");
        first.Quality.Should().Be("SD");

        VerifyRequest("https://api.broadcasthe.net/");
    }

    [Fact]
    public async Task should_throw_on_bad_request()
    {
        SetupHttpResponse(HttpStatusCode.BadRequest);

        var act = () => _subject.FetchRecentAsync(_indexer);

        await act.Should().ThrowAsync<IndexerRequestException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_throw_on_unauthorized()
    {
        SetupHttpResponse(HttpStatusCode.Unauthorized);

        var act = () => _subject.FetchRecentAsync(_indexer);

        await act.Should().ThrowAsync<IndexerRequestException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_throw_on_not_found()
    {
        SetupHttpResponse(HttpStatusCode.NotFound);

        var act = () => _subject.FetchRecentAsync(_indexer);

        await act.Should().ThrowAsync<IndexerRequestException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_throw_rate_limit_exception_on_service_unavailable_with_call_limit_body()
    {
        _handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Call Limit Exceeded")
            });

        var act = () => _subject.FetchRecentAsync(_indexer);

        await act.Should().ThrowAsync<IndexerRateLimitException>();
    }

    [Fact]
    public async Task should_throw_on_html_response()
    {
        SetupHttpResponse("<html><body>Cloudflare</body></html>", HttpStatusCode.OK, "text/html");

        var act = () => _subject.FetchRecentAsync(_indexer);

        await act.Should().ThrowAsync<IndexerRequestException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task should_throw_on_invalid_api_key_plain_text_response()
    {
        SetupHttpResponse("Error: Invalid API Key");

        var act = () => _subject.FetchRecentAsync(_indexer);

        await act.Should().ThrowAsync<IndexerRequestException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task should_use_configured_base_url_for_download_urls()
    {
        // Download URLs come from BTN's response, not from the configured base URL —
        // verify that http-configured indexers still get the response URLs as-is.
        var indexerWithHttp = new Indexer
        {
            Id = 2,
            Name = "BroadcastheNet",
            Url = "http://api.broadcasthe.net",
            ApiKey = "abc"
        };

        var recentFeed = ReadFixture("Services/Fixtures/BroadcastheNet/RecentFeed.json");
        SetupHttpResponse(recentFeed);

        var releases = await _subject.FetchRecentAsync(indexerWithHttp);

        releases.Should().HaveCount(2);
        // DownloadURL comes from the BTN JSON payload; the client doesn't rewrite it
        releases.First().DownloadUrl.Should().Be(
            "https://broadcasthe.net/torrents.php?action=download&id=123&authkey=123&torrent_pass=123");

        VerifyRequest("http://api.broadcasthe.net/");
    }

    [Fact]
    public async Task should_return_empty_list_when_result_has_no_torrents()
    {
        var emptyFeed = """{"id":"abc","result":{"Results":0}}""";
        SetupHttpResponse(emptyFeed);

        var releases = await _subject.FetchRecentAsync(_indexer);

        releases.Should().BeEmpty();
    }

    [Fact]
    public async Task should_throw_when_api_returns_error_in_json_rpc_response()
    {
        var errorResponse = """{"id":"abc","error":{"code":-32601,"message":"Method not found"}}""";
        SetupHttpResponse(errorResponse);

        var act = () => _subject.FetchRecentAsync(_indexer);

        await act.Should().ThrowAsync<IndexerRequestException>();
    }

    [Fact]
    public async Task should_set_guid_with_btn_prefix_and_torrent_id()
    {
        var recentFeed = ReadFixture("Services/Fixtures/BroadcastheNet/RecentFeed.json");
        SetupHttpResponse(recentFeed);

        var releases = await _subject.FetchRecentAsync(_indexer);

        releases.Should().OnlyContain(r => r.Guid.StartsWith("BTN-"));

        VerifyRequest("https://api.broadcasthe.net/");
    }

    [Fact]
    public async Task should_set_info_url_with_group_and_torrent_id()
    {
        var recentFeed = ReadFixture("Services/Fixtures/BroadcastheNet/RecentFeed.json");
        SetupHttpResponse(recentFeed);

        var releases = await _subject.FetchRecentAsync(_indexer);

        releases.First().InfoUrl.Should().Be("https://broadcasthe.net/torrents.php?id=237457&torrentid=123");

        VerifyRequest("https://api.broadcasthe.net/");
    }
}
