using NativeDCB.Model;
using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Actors.Contracts;

public interface IEventBatchValidator
{
    EventBatch Validate(string database, EventBatch batch);
}