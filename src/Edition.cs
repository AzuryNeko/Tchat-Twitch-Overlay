namespace TchatOverlay
{
    /// <summary>
    /// Deux éditions compilées depuis le même code :
    ///  - Standard : lecture seule (dotnet publish)
    ///  - Pro      : + barre de réponse + options modérateur (dotnet publish -p:Edition=Pro)
    /// </summary>
    public static class Edition
    {
#if PRO
        public const bool IsPro = true;
        public const string Name = "Tchat Overlay Pro";
        public const string Scopes = "chat:read chat:edit moderator:manage:banned_users moderator:manage:chat_messages moderator:manage:chat_settings";
#else
        public const bool IsPro = false;
        public const string Name = "Tchat Overlay";
        public const string Scopes = "chat:read";
#endif
    }
}
