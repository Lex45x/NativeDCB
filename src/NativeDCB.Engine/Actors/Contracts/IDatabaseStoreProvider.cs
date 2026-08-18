namespace NativeDCB.Engine.Actors;

public interface IDatabaseStoreProvider
{
    Task<JsonEventStore> GetStoreAsync(string database, CancellationToken cancellationToken);

    string GetDirectory(string database);

    void ReportFault(string database, Exception exception);
}