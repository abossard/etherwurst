using HazMeBeenScammed.Core.Domain;

namespace HazMeBeenScammed.Tests.Unit;

public class DomainModelTests
{
    [Theory]
    [InlineData("0x742d35Cc6634C0532925a3b844Bc454e4438f44e", true)]
    [InlineData("0xABCDEF1234567890ABCDef1234567890abcdef12", true)]
    [InlineData("742d35Cc6634C0532925a3b844Bc454e4438f44e", false)]  // missing 0x
    [InlineData("0x742d35Cc6634C0532925a3b844Bc454e4438f4", false)]    // too short
    [InlineData("0x742d35Cc6634C0532925a3b844Bc454e4438f44eXX", false)] // too long
    [InlineData("", false)]
    [InlineData(null, false)]
    public void WalletAddress_Validation(string? value, bool expected)
    {
        Assert.Equal(expected, WalletAddress.IsValid(value!));
    }

    [Theory]
    [InlineData("0x5c504ed432cb51138bcf09aa5e8a410dd4a1e204b93aaa9b6d64c1b0726b9e4b", true)]
    [InlineData("0xABCDEFabcdef0123456789012345678901234567890123456789012345678901", true)]
    [InlineData("0x5c504ed432cb51138bcf09aa5e8a410dd4a1", false)]  // too short
    [InlineData("5c504ed432cb51138bcf09aa5e8a410dd4a1e204b93aaa9b6d64c1b0726b9e4b", false)] // no 0x
    [InlineData("", false)]
    public void TransactionHash_Validation(string? value, bool expected)
    {
        Assert.Equal(expected, TransactionHash.IsValid(value!));
    }

    [Fact]
    public void AnalysisRequest_DetectsWalletAddress()
    {
        var req = new AnalysisRequest("0x742d35Cc6634C0532925a3b844Bc454e4438f44e");
        Assert.Equal(AnalysisInputType.WalletAddress, req.InputType);
    }

    [Fact]
    public void AnalysisRequest_DetectsTransactionHash()
    {
        var req = new AnalysisRequest("0x5c504ed432cb51138bcf09aa5e8a410dd4a1e204b93aaa9b6d64c1b0726b9e4b");
        Assert.Equal(AnalysisInputType.TransactionHash, req.InputType);
    }

    [Fact]
    public void AnalysisRequest_DetectsUnknown()
    {
        var req = new AnalysisRequest("not-an-address");
        Assert.Equal(AnalysisInputType.Unknown, req.InputType);
    }
}
