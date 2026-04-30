namespace AChat.Core.Entities;

public enum AccessSubjectType
{
    AchatUser = 0,
    TelegramUser = 1
}

public enum AccessStatus
{
    Allowed = 0,
    Denied = 1
}

public enum AccessRequestStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2
}
