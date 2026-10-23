namespace Shortener.Tests;

// Every test class marked [Collection("postgres")] shares one PostgresFixture, so one container starts for all of them.
[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
