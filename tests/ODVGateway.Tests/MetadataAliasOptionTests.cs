using ODVGateway.Options;

namespace ODVGateway.Tests;

public sealed class MetadataAliasOptionTests
{
    [Fact]
    public void GetFieldIds_SingleFieldId_ReturnsTrimmedValue()
    {
        var option = new MetadataAliasOption { FieldId = "  DocTitle  " };

        Assert.Equal(["DocTitle"], option.GetFieldIds());
    }

    [Fact]
    public void GetFieldIds_FieldIdAndFieldIds_CombinesInOrder()
    {
        var option = new MetadataAliasOption
        {
            FieldId = "Primary",
            FieldIds = ["Secondary", "Tertiary"]
        };

        Assert.Equal(["Primary", "Secondary", "Tertiary"], option.GetFieldIds());
    }

    [Fact]
    public void GetFieldIds_NullFieldId_UsesFieldIdsOnly()
    {
        var option = new MetadataAliasOption { FieldIds = ["a", "b"] };

        Assert.Equal(["a", "b"], option.GetFieldIds());
    }

    [Fact]
    public void GetFieldIds_DuplicatesAndWhitespace_AreRemovedCaseInsensitively()
    {
        var option = new MetadataAliasOption
        {
            FieldId = "Title",
            FieldIds = ["title", " TITLE ", "", "  ", "Other"]
        };

        Assert.Equal(["Title", "Other"], option.GetFieldIds());
    }

    [Fact]
    public void GetFieldIds_NoIds_ReturnsEmpty()
    {
        var option = new MetadataAliasOption();

        Assert.Empty(option.GetFieldIds());
    }
}
