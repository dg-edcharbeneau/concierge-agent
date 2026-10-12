using Concierge.Contracts;

namespace Concierge.Core.Tests;

public sealed class RoomNumberFormatTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("10")]
    [InlineData("412")]
    [InlineData("9999")]
    public void AcceptsOneToFourDigitsWithoutALeadingZero(string roomNumber) =>
        Assert.True(RoomNumberFormat.IsValid(roomNumber));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0412")]
    [InlineData("012")]
    [InlineData("12345")]
    [InlineData(" 412")]
    [InlineData("41a")]
    [InlineData("４１２")]
    public void RejectsLeadingZerosAndNonDigits(string? roomNumber) =>
        Assert.False(RoomNumberFormat.IsValid(roomNumber));
}
