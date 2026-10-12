namespace Concierge.AppHost;

internal static class PostgreSqlContainerImage
{
    public const string Repository = "pgvector/pgvector";
    public const string Tag = "0.8.1-pg18";
    public const string FullName = Repository + ":" + Tag;
    public const string PostgreSqlMajorVersion = "18";
    public const string VectorExtensionVersion = "0.8.1";
}
