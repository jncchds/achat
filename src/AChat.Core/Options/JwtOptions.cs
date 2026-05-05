namespace AChat.Core.Options;

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "AChat";
    public string Audience { get; set; } = "AChat";
    public int ExpiryHours { get; set; } = 24;
}
