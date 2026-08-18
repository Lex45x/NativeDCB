using NativeDCB.Model;

namespace NativeDCB.Engine.Actors;

public interface IEventBatchValidator
{
    EventBatch Validate(string database, EventBatch batch);
}