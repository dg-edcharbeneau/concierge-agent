namespace Concierge.Contracts;

/// <summary>
/// The single room-number rule shared by Web request validation, session lookup and seed validation:
/// one to four ASCII digits with no leading zero (for example <c>412</c>, not <c>0412</c>).
/// </summary>
public static class RoomNumberFormat
{
    public static bool IsValid(string? roomNumber)
    {
        if (roomNumber is not { Length: >= 1 and <= 4 } || roomNumber[0] == '0')
        {
            return false;
        }

        foreach (var character in roomNumber)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
