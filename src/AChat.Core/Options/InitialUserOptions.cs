namespace AChat.Core.Options;

public class InitialUserOptions
{
    public const string Section = "InitialUser";

    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "changeme";
    public bool ForceChange { get; set; } = false;
}
