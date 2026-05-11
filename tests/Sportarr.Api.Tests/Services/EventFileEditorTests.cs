using Sportarr.Api.Endpoints;
using Sportarr.Api.Models;
using FluentAssertions;

namespace Sportarr.Api.Tests.Services;

/// <summary>
/// Tests for the EventFile editor merge logic. Exercises the pure-function
/// ApplyEdits helper that all three editor endpoints share, so we don't need
/// to spin up a WebApplicationFactory just to verify field-level behavior.
/// </summary>
public class EventFileEditorTests
{
    private static EventFile MakeFile(string? quality = "WEBDL-1080p", int? score = 530) => new()
    {
        Id = 1,
        EventId = 100,
        FilePath = "/tmp/test.mkv",
        Quality = quality,
        QualityScore = score ?? 0,
        Codec = "x264",
        Source = "WEBDL",
        ReleaseGroup = "OLDGROUP",
        OriginalTitle = "Original.Release.Title.1080p.WEB-DL.x264-OLDGROUP",
        Languages = new List<string> { "English" },
        IndexerFlags = "Internal",
        Exists = true
    };

    [Fact]
    public void ApplyEdits_ShouldUpdateOnlyProvidedFields()
    {
        var file = MakeFile();
        var req = new EventFileEditorEndpoints.EventFileEditRequest
        {
            Quality = "Bluray-2160p"
            // Everything else null — those fields stay unchanged.
        };

        EventFileEditorEndpoints.ApplyEdits(file, req);

        file.Quality.Should().Be("Bluray-2160p");
        file.Codec.Should().Be("x264");                  // unchanged
        file.ReleaseGroup.Should().Be("OLDGROUP");       // unchanged
        file.Languages.Should().Equal("English");        // unchanged
    }

    [Fact]
    public void ApplyEdits_ShouldRecomputeQualityScoreWhenQualityChanges()
    {
        var file = MakeFile(quality: "Unknown", score: 0);
        var req = new EventFileEditorEndpoints.EventFileEditRequest
        {
            Quality = "WEBDL-1080p"
        };

        EventFileEditorEndpoints.ApplyEdits(file, req);

        file.QualityScore.Should().BeGreaterThan(0,
            because: "the editor recomputes QualityScore whenever Quality is changed so the score stays in sync with the displayed string");
    }

    [Fact]
    public void ApplyEdits_ShouldClearLanguagesWhenEmptyListProvided()
    {
        var file = MakeFile();
        var req = new EventFileEditorEndpoints.EventFileEditRequest
        {
            Languages = new List<string>()
        };

        EventFileEditorEndpoints.ApplyEdits(file, req);

        file.Languages.Should().BeEmpty(
            because: "an explicit empty list is the user clearing the field, distinct from null which means 'leave unchanged'");
    }

    [Fact]
    public void ApplyEdits_ShouldUpdateLanguagesWithMultipleEntries()
    {
        var file = MakeFile();
        var req = new EventFileEditorEndpoints.EventFileEditRequest
        {
            Languages = new List<string> { "English", "Spanish", "French" }
        };

        EventFileEditorEndpoints.ApplyEdits(file, req);

        file.Languages.Should().Equal("English", "Spanish", "French");
    }

    [Fact]
    public void ApplyEdits_ShouldUpdateAllFieldsWhenAllProvided()
    {
        var file = MakeFile();
        var req = new EventFileEditorEndpoints.EventFileEditRequest
        {
            Quality = "Bluray-2160p Remux",
            Source = "BLURAY",
            Codec = "x265",
            ReleaseGroup = "NEWGROUP",
            OriginalTitle = "Better.Release.Title.2160p.BluRay.REMUX.HEVC-NEWGROUP",
            Languages = new List<string> { "English", "German" },
            IndexerFlags = "Freeleech, Scene",
            PartName = "Main Card",
            PartNumber = 3
        };

        EventFileEditorEndpoints.ApplyEdits(file, req);

        file.Quality.Should().Be("Bluray-2160p Remux");
        file.Source.Should().Be("BLURAY");
        file.Codec.Should().Be("x265");
        file.ReleaseGroup.Should().Be("NEWGROUP");
        file.OriginalTitle.Should().Be("Better.Release.Title.2160p.BluRay.REMUX.HEVC-NEWGROUP");
        file.Languages.Should().Equal("English", "German");
        file.IndexerFlags.Should().Be("Freeleech, Scene");
        file.PartName.Should().Be("Main Card");
        file.PartNumber.Should().Be(3);
    }

    [Fact]
    public void ApplyEdits_ShouldNotTouchPartNumberWhenNullProvided()
    {
        var file = MakeFile();
        file.PartNumber = 1;
        var req = new EventFileEditorEndpoints.EventFileEditRequest
        {
            PartName = "Prelims"
            // PartNumber not set — null
        };

        EventFileEditorEndpoints.ApplyEdits(file, req);

        file.PartName.Should().Be("Prelims");
        file.PartNumber.Should().Be(1, because: "null PartNumber means leave existing value alone");
    }

    [Fact]
    public void EventFileResponse_FromEventFile_ShouldIncludeAllFields()
    {
        var file = MakeFile();
        var response = EventFileResponse.FromEventFile(file);

        response.Id.Should().Be(1);
        response.EventId.Should().Be(100);
        response.Quality.Should().Be("WEBDL-1080p");
        response.Codec.Should().Be("x264");
        response.ReleaseGroup.Should().Be("OLDGROUP");
        response.Languages.Should().Equal("English");
        response.IndexerFlags.Should().Be("Internal");
        response.OriginalTitle.Should().Be("Original.Release.Title.1080p.WEB-DL.x264-OLDGROUP");
    }

    [Fact]
    public void EventFileResponse_FromEventFile_ShouldHandleNullLanguages()
    {
        // Defensive — old DB rows that predated the Languages column can come in
        // with the property left at the default. The DTO converter should give
        // the frontend an empty list rather than null to dodge UI null-checks.
        var file = MakeFile();
        file.Languages = null!;
        var response = EventFileResponse.FromEventFile(file);

        response.Languages.Should().NotBeNull();
        response.Languages.Should().BeEmpty();
    }
}
