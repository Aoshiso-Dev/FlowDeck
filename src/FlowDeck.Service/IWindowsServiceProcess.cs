namespace FlowDeck.Service;

internal interface IWindowsServiceProcess : IAsyncDisposable
{
    Task Completion { get; }
    Task PowerAsync(bool resume);
}
